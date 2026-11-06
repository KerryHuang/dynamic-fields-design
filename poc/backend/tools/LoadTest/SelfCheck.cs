using System.Text.Json;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LoadTest;

/// <summary>同一份值各走一次「正式的 CreateRecordHandler」與「批次灌入」，比對 Record、RecordIndex、RecordHistory 是否一致。</summary>
public static class SelfCheck
{
    public static async Task<int> RunAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "loadtest-selfcheck-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "check.db");
        try
        {
            await using var sp = Env.Build(dbPath);
            SchemaInfo s;
            using (var scope = sp.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Database.MigrateAsync();
                await DynamicModules.Persistence.Seed.SeedData.EnsureSeededAsync(scope.ServiceProvider.GetRequiredService<DynamicDbContext>());
                s = await Schema.CreateAsync(scope.ServiceProvider.GetRequiredService<IMediator>());
            }

            using var scope2 = sp.CreateScope();
            var modules = scope2.ServiceProvider.GetRequiredService<IModuleRepository>();
            var registry = scope2.ServiceProvider.GetRequiredService<FieldTypeRegistry>();
            var ledger = (await modules.GetByKeyAsync("ledger", true, default))!;
            var account = (await modules.GetByKeyAsync("account", true, default))!;

            await using var conn = new SqliteConnection(Env.ConnectionString(dbPath));
            await conn.OpenAsync();
            long maxId;
            using (var tx = conn.BeginTransaction())
            {
                var w = new BulkWriter(conn, tx, registry);
                w.Add(account, new() { [s.AccountTitle] = Seeder.Json("A1"), [s.AccountKind] = Seeder.Json(s.AccountKinds[0]) }, Env.FixedNow);
                tx.Commit();
                maxId = w.NextId;
            }

            Dictionary<string, JsonElement?> Values(string no) => new()
            {
                [s.LedgerTitle] = Seeder.Json("摘要"),
                [s.LedgerDate] = Seeder.Json("2026-03-01"),
                [s.LedgerAccount] = JsonSerializer.SerializeToElement(1L),
                [s.LedgerDebit] = JsonSerializer.SerializeToElement(123.45m),
                [s.LedgerCredit] = JsonSerializer.SerializeToElement(0m),
                [s.LedgerStatus] = Seeder.Json(s.Statuses[0]),
            };

            // 正式路徑
            using (var scope3 = sp.CreateScope())
            {
                var res = await scope3.ServiceProvider.GetRequiredService<IMediator>().Send(new CreateRecordRequest("ledger", Values("")));
                if (!res.IsCreated) { Console.WriteLine("handler 建立失敗：" + res.Message); return 1; }
            }
            var handlerId = maxId;
            // 批次路徑（自動編號欄位另外給值，比對時略過）
            long bulkId;
            using (var tx = conn.BeginTransaction())
            {
                var w = new BulkWriter(conn, tx, registry, maxId + 1);
                var v = Values("");
                v[s.LedgerNo] = Seeder.Json("JV-202611-00002");
                bulkId = w.Add(ledger, v, Env.FixedNow);
                tx.Commit();
            }

            var a = Dump(conn, handlerId, s);
            var b = Dump(conn, bulkId, s);
            var same = a == b;
            Console.WriteLine(same ? "selfcheck 通過：批次灌入與 CreateRecordHandler 的列一致" : $"selfcheck 失敗\n--- handler\n{a}\n--- bulk\n{b}");
            return same ? 0 : 1;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    /// <summary>取出一筆記錄的所有列，去掉 Id、RowVersion、時間與自動編號本身的值。</summary>
    private static string Dump(SqliteConnection conn, long id, SchemaInfo s)
    {
        var lines = new List<string>();
        string Q(string sql, params string[] cols)
        {
            using var c = conn.CreateCommand();
            c.CommandText = sql;
            c.Parameters.AddWithValue("$id", id);
            using var r = c.ExecuteReader();
            var rows = new List<string>();
            while (r.Read()) rows.Add(string.Join("|", cols.Select((_, i) => r.IsDBNull(i) ? "∅" : r.GetValue(i).ToString())));
            return string.Join("\n", rows);
        }
        var values = JsonDocument.Parse(Q("select ValuesJson from Dynamic_Record where Id=$id", "v")).RootElement
            .EnumerateObject().Where(p => p.Name != s.LedgerNo).Select(p => $"{p.Name}={p.Value.GetRawText()}");
        lines.Add(string.Join(",", values));
        lines.Add(Q("select ModuleId, Title, OwnerUserId, DeleteFlag, CreatedUserId, ModifiedUserId, HostKey from Dynamic_Record where Id=$id",
            "m", "t", "o", "d", "cu", "mu", "h"));
        lines.Add(Q("select FieldId, Seq, case when TextValue like 'JV-%' then 'no' else TextValue end, NumberValue, DateValue, RefKey from Dynamic_RecordIndex where RecordId=$id order by FieldId, Seq",
            "f", "s", "t", "n", "d", "r"));
        lines.Add(Q("select FieldId, Operation, OldValue, NewValue, ChangedUserId from Dynamic_RecordHistory where RecordId=$id order by Id",
            "f", "o", "ov", "nv", "u"));
        return string.Join("\n", lines);
    }
}
