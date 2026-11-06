using System.Diagnostics;
using System.Text.Json;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Persistence.Seed;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Record = DynamicModules.Domain.Entities.Dynamic.Record;

namespace LoadTest;

public sealed record SeedOptions(string Db, int Years, int PerYear, int Accounts, int Customers, int Seed = 20261106);

public sealed record SeedInfo(
    SchemaInfo Schema, int Accounts, int Customers, long Ledger, DateOnly FirstDate, DateOnly LastDate,
    long AccountFirstId, long CustomerFirstId, long LedgerFirstId)
{
    public static string PathFor(string db) => db + ".seed.json";

    public static SeedInfo Load(string db) =>
        JsonSerializer.Deserialize<SeedInfo>(File.ReadAllText(PathFor(db)))!;
}

/// <summary>
/// 大量灌資料：用與正式寫入相同的索引投影（RecordIndexer）與序列化，但繞過逐筆的驗證與交易，
/// 以預備好的 SQL 在單一交易內批次寫入。<c>selfcheck</c> 指令驗證這條路徑與 CreateRecordHandler 產生的列一致。
/// </summary>
public static class Seeder
{
    public static async Task RunAsync(SeedOptions o)
    {
        var dbPath = Path.GetFullPath(o.Db);
        foreach (var suffix in new[] { "", "-wal", "-shm", ".seed.json" })
            if (File.Exists(dbPath + suffix)) File.Delete(dbPath + suffix);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        await using var sp = Env.Build(dbPath);
        var total = (long)o.Years * o.PerYear;
        var sw = Stopwatch.StartNew();
        SchemaInfo schema;
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
            await db.Database.MigrateAsync();
            await SeedData.EnsureSeededAsync(db);
            schema = await Schema.CreateAsync(scope.ServiceProvider.GetRequiredService<MediatR.IMediator>());
        }
        Console.WriteLine($"schema 建立完成（{sw.Elapsed.TotalSeconds:F1}s）");

        using var scope2 = sp.CreateScope();
        var modules = scope2.ServiceProvider.GetRequiredService<IModuleRepository>();
        var registry = scope2.ServiceProvider.GetRequiredService<FieldTypeRegistry>();
        var account = (await modules.GetByKeyAsync("account", true, default))!;
        var customer = (await modules.GetByKeyAsync("customer", true, default))!;
        var ledger = (await modules.GetByKeyAsync("ledger", true, default))!;

        await using var conn = new SqliteConnection(Env.ConnectionString(dbPath));
        await conn.OpenAsync();
        var indexDdl = await DropSecondaryIndexes(conn);
        Exec(conn, "PRAGMA synchronous=OFF");
        Exec(conn, "PRAGMA cache_size=-1000000");

        using var tx = conn.BeginTransaction();
        var w = new BulkWriter(conn, tx, registry);
        var rnd = new Random(o.Seed);
        var now = Env.FixedNow;

        var accountFirst = w.NextId;
        for (var i = 0; i < o.Accounts; i++)
            w.Add(account, new()
            {
                [schema.AccountTitle] = Json($"A{1000 + i}"),
                [schema.AccountKind] = Json(schema.AccountKinds[i % schema.AccountKinds.Length]),
            }, now.AddDays(-3650));
        var customerFirst = w.NextId;
        for (var i = 0; i < o.Customers; i++)
            w.Add(customer, new()
            {
                [schema.CustomerTitle] = Json($"客戶{i:00000}"),
                [schema.CustomerRegion] = Json(schema.Regions[rnd.Next(schema.Regions.Length)]),
            }, now.AddDays(-3650));
        var ledgerFirst = w.NextId;

        var last = DateOnly.FromDateTime(now);
        var days = o.Years * 365;
        var first = last.AddDays(-(days - 1));
        var monthCounts = new SortedDictionary<string, long>();
        var noKeyId = ledger.Blocks.SelectMany(b => b.Fields).Single(f => f.Key == schema.LedgerNo).Id;
        var progress = Stopwatch.StartNew();
        for (long i = 0; i < total; i++)
        {
            var date = first.AddDays((int)(i * days / total));
            var period = date.ToString("yyyyMM");
            monthCounts[period] = monthCounts.GetValueOrDefault(period) + 1;
            var cents = (long)(Math.Pow(rnd.NextDouble(), 4) * 5_000_000) + 1; // 0.01 ~ 50,000.00，偏小額
            var amount = JsonSerializer.SerializeToElement(cents / 100m);
            var zero = JsonSerializer.SerializeToElement(0m);
            var debitSide = rnd.Next(2) == 0;
            var acct = accountFirst + (long)(o.Accounts * Math.Pow(rnd.NextDouble(), 3)); // 偏斜：少數科目佔多數分錄
            var values = new Dictionary<string, JsonElement?>
            {
                [schema.LedgerTitle] = Json($"傳票 {i:0000000} 摘要"),
                [schema.LedgerDate] = Json(date.ToString("yyyy-MM-dd")),
                [schema.LedgerAccount] = JsonSerializer.SerializeToElement(acct),
                [schema.LedgerDebit] = debitSide ? amount : zero,
                [schema.LedgerCredit] = debitSide ? zero : amount,
                [schema.LedgerStatus] = Json(rnd.Next(100) < 95 ? schema.Statuses[0] : schema.Statuses[1 + rnd.Next(2)]),
                [schema.LedgerNo] = Json($"JV-{period}-{monthCounts[period]:00000}"),
            };
            if (rnd.Next(100) < 70)
                values[schema.LedgerCustomer] = JsonSerializer.SerializeToElement(customerFirst + rnd.Next(o.Customers));
            w.Add(ledger, values, date.ToDateTime(new TimeOnly(9, 0)));
            if ((i + 1) % 250_000 == 0)
                Console.WriteLine($"  分錄 {i + 1:N0} / {total:N0}（{progress.Elapsed.TotalSeconds:F0}s）");
        }
        foreach (var (period, count) in monthCounts)
            Exec(conn, "INSERT INTO Dynamic_NumberSequence (FieldId, Period, ModuleId, NextValue) VALUES ($f, $p, $m, $n)", tx,
                ("$f", noKeyId), ("$p", period), ("$m", ledger.Id), ("$n", count + 1));
        tx.Commit();
        Console.WriteLine($"資料寫入完成（{sw.Elapsed.TotalSeconds:F0}s），重建索引…");

        foreach (var ddl in indexDdl) Exec(conn, ddl);
        Exec(conn, "PRAGMA synchronous=FULL");
        Exec(conn, "PRAGMA journal_mode=WAL");
        Exec(conn, "PRAGMA wal_checkpoint(TRUNCATE)");
        Console.WriteLine($"索引重建完成（總計 {sw.Elapsed.TotalSeconds:F0}s）");

        var info = new SeedInfo(schema, o.Accounts, o.Customers, total, first, last, accountFirst, customerFirst, ledgerFirst);
        File.WriteAllText(SeedInfo.PathFor(dbPath), JsonSerializer.Serialize(info));
    }

    public static JsonElement Json(string s) => JsonSerializer.SerializeToElement(s);

    private static async Task<List<string>> DropSecondaryIndexes(SqliteConnection conn)
    {
        var ddl = new List<string>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name, sql FROM sqlite_master WHERE type='index' AND sql IS NOT NULL AND tbl_name IN ('Dynamic_Record','Dynamic_RecordIndex','Dynamic_RecordHistory')";
            await using var r = await cmd.ExecuteReaderAsync();
            var names = new List<string>();
            while (await r.ReadAsync()) { names.Add(r.GetString(0)); ddl.Add(r.GetString(1)); }
            r.Close();
            foreach (var n in names) Exec(conn, $"DROP INDEX \"{n}\"");
        }
        return ddl;
    }

    internal static void Exec(SqliteConnection conn, string sql, SqliteTransaction? tx = null, params (string, object)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v);
        cmd.ExecuteNonQuery();
    }
}

/// <summary>預備好的三個 INSERT；Id 自己配（資料庫是新的，從 1 起算，AUTOINCREMENT 的序列會跟著更新）。</summary>
public sealed class BulkWriter
{
    private readonly SqliteCommand _record, _index, _history;
    private readonly FieldTypeRegistry _registry;
    public long NextId { get; private set; } = 1;

    public BulkWriter(SqliteConnection conn, SqliteTransaction tx, FieldTypeRegistry registry, long nextId = 1)
    {
        _registry = registry;
        NextId = nextId;
        _record = Make(conn, tx, "INSERT INTO Dynamic_Record (Id, ModuleId, Title, OwnerUserId, ValuesJson, RowVersion, DeleteFlag, CreatedUserId, CreatedTime) VALUES ($id,$m,$t,'admin',$v,$rv,0,'admin',$ct)",
            "$id", "$m", "$t", "$v", "$rv", "$ct");
        _index = Make(conn, tx, "INSERT INTO Dynamic_RecordIndex (RecordId, FieldId, Seq, TextValue, NumberValue, DateValue, RefKey) VALUES ($r,$f,$s,$tv,$nv,$dv,$rk)",
            "$r", "$f", "$s", "$tv", "$nv", "$dv", "$rk");
        _history = Make(conn, tx, "INSERT INTO Dynamic_RecordHistory (RecordId, FieldId, Operation, OldValue, NewValue, ChangedUserId, ChangedTime) VALUES ($r,NULL,'Create',NULL,NULL,'admin',$ct)",
            "$r", "$ct");
    }

    private static SqliteCommand Make(SqliteConnection conn, SqliteTransaction tx, string sql, params string[] names)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var n in names) cmd.Parameters.Add(new SqliteParameter { ParameterName = n });
        cmd.Prepare();
        return cmd;
    }

    public long Add(Module module, Dictionary<string, JsonElement?> values, DateTime createdTime)
    {
        var id = NextId++;
        var title = RecordValueProcessor.TitleOf(module, values);
        Set(_record, "$id", id); Set(_record, "$m", module.Id); Set(_record, "$t", title);
        Set(_record, "$v", RecordValueProcessor.Serialize(values)); Set(_record, "$rv", Guid.NewGuid().ToString());
        Set(_record, "$ct", createdTime);
        _record.ExecuteNonQuery();
        foreach (var ix in RecordIndexer.Build(module, values, _registry))
        {
            Set(_index, "$r", id); Set(_index, "$f", ix.FieldId); Set(_index, "$s", ix.Seq);
            Set(_index, "$tv", ix.TextValue); Set(_index, "$nv", ix.NumberValue); Set(_index, "$dv", ix.DateValue); Set(_index, "$rk", ix.RefKey);
            _index.ExecuteNonQuery();
        }
        Set(_history, "$r", id); Set(_history, "$ct", createdTime);
        _history.ExecuteNonQuery();
        return id;
    }

    private static void Set(SqliteCommand c, string name, object? v) => c.Parameters[name].Value = v ?? DBNull.Value;
}
