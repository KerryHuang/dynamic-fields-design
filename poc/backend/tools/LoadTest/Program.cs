using System.Text.Json;
using DynamicModules.Domain.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using LoadTest;
using Microsoft.EntityFrameworkCore;

// 用法：
//   dotnet run --project poc/backend/tools/LoadTest -c Release -- seed --db <path> [--years 10] [--per-year 300000] [--accounts 300] [--customers 20000]
//   ... -- run --db <path> [--users 20] [--duration 60] [--phases solo,mixed,stress,writes] [--out report.json]
//   ... -- check --db <path>        印出 pragma、檔案大小、筆數
//   ... -- selfcheck                驗證批次灌入的列與 CreateRecordHandler 產生的一致
//   ... -- analyze --db <path>      對資料庫跑 ANALYZE（實驗用）
//   ... -- report-bench --db <path> [--runs 5]   各報表案例單人逐項量測，列出引擎、退回原因、狀態與延遲
//   ... -- report-check --db <path>              同一份資料用記憶體與 SQL 兩種引擎各跑一次逐項比對（資料庫要小於載入上限以外的筆數）

var cmd = args.FirstOrDefault() ?? "";
string Arg(string name, string fallback) =>
    args.SkipWhile(a => a != "--" + name).Skip(1).FirstOrDefault() ?? fallback;
int IntArg(string name, int fallback) => int.Parse(Arg(name, fallback.ToString()));

switch (cmd)
{
    case "seed":
        await Seeder.RunAsync(new SeedOptions(Arg("db", "load.db"), IntArg("years", 10), IntArg("per-year", 300_000),
            IntArg("accounts", 300), IntArg("customers", 20_000)));
        break;
    case "run":
        return await RunAsync();
    case "backfill":
        return await BackfillAsync();
    case "report-bench":
        return await ReportBenchAsync();
    case "report-check":
        return await ReportCheckAsync();
    case "explain":
        return await ExplainAsync();
    case "check":
        Check(Arg("db", "load.db"));
        break;
    case "selfcheck":
        return await SelfCheck.RunAsync();
    case "analyze":
        {
            using var c = new SqliteConnection(Env.ConnectionString(Arg("db", "load.db")));
            c.Open();
            Seeder.Exec(c, "ANALYZE");
            Console.WriteLine("ANALYZE 完成");
            break;
        }
    default:
        Console.WriteLine("指令：seed | run | check | selfcheck | analyze（見 Program.cs 開頭的用法）");
        return 1;
}
return 0;

async Task<int> RunAsync()
{
    var db = Arg("db", "load.db");
    var seed = SeedInfo.Load(Path.GetFullPath(db));
    await using var sp = Env.Build(db);
    var w = new Workload(sp, seed);
    var all = w.All();
    var phases = Arg("phases", "solo,mixed,stress,writes").Split(',');
    var users = IntArg("users", 20);
    var duration = TimeSpan.FromSeconds(IntArg("duration", 60));
    var results = new List<PhaseResult>();

    void Done(PhaseResult r) { results.Add(r); Console.WriteLine(Recorder.Table(r)); }

    if (phases.Contains("solo")) Done(await w.Solo(all.Where(o => o.Weight > 0).ToList(), IntArg("solo-runs", 5)));
    // 實際使用：每人每 1–3 秒做一件事，約 80% 讀、20% 寫
    if (phases.Contains("mixed")) Done(await w.Crowd("mixed（有思考時間）", all, users, duration, (1000, 3000)));
    // 壓力：不停手，量系統容量上限
    if (phases.Contains("stress")) Done(await w.Crowd("stress（不停手）", all, users, duration, (0, 0)));
    // 只寫：量寫入鎖等待
    if (phases.Contains("writes")) Done(await w.Crowd("writes（只寫不停手）", Workload.Only(all, Arg("write-ops", "create_entry,update_entry").Split(',')), users, duration, (0, 0)));

    // 階段 2：分錄新增連動鏡射記錄＋跨記錄檢查；--hot 讓所有新增落在同一個科目（組內筆數持續成長）
    if (phases.Contains("events"))
    {
        await EventsSetup.EnsureAsync(sp, seed);
        w.HotAccount(args.Contains("--hot"));
        Done(await w.Crowd(args.Contains("--hot") ? "events（連動＋檢查，單一科目）" : "events（連動＋檢查）",
            Workload.Only(all, Arg("write-ops", "create_entry_events,update_entry").Split(','))
                .Select(o => o.Weight == 0 ? o with { Weight = 14 } : o).ToList(), users, duration, (0, 0)));
    }

    var outPath = Arg("out", "");
    if (outPath != "") File.WriteAllText(outPath, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
    return 0;
}

// 量「儲存跨記錄檢查時回填索引」的時間：先把分錄借方欄位的 ScaledValue 清成 NULL（模擬升級前建的索引），
// 再儲存一條以借方加總的檢查，量這個請求（含回填，持有寫入鎖）花多久。
async Task<int> BackfillAsync()
{
    var db = Arg("db", "load.db");
    var seed = SeedInfo.Load(Path.GetFullPath(db));
    using (var c = new SqliteConnection(Env.ConnectionString(db)))
    {
        c.Open();
        Seeder.Exec(c, $"UPDATE Dynamic_RecordIndex SET ScaledValue = NULL WHERE FieldId = {seed.Schema.LedgerDebitId}");
    }
    await using var sp = Env.Build(db);
    using var scope = sp.CreateScope();
    var m = scope.ServiceProvider.GetRequiredService<MediatR.IMediator>();
    var def = new DynamicModules.Domain.Entities.Dynamic.CheckDefinition([seed.Schema.LedgerStatusId], "sum", seed.Schema.LedgerDebitId, null,
        new DynamicModules.Domain.Entities.Dynamic.CheckAssert("gte", new DynamicModules.Domain.Entities.Dynamic.CheckLimit(0, null, null)),
        new DynamicModules.Domain.ValueObjects.I18nText([KeyValuePair.Create("zh-TW", "借方不可為負")]));
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var res = await m.Send(new DynamicModules.Application.Dynamic.CheckService.CreateCheckRequest(
        new DynamicModules.Application.Dynamic.CheckService.CheckInput("lt-backfill", def.Message, "ledger", true, def)));
    Console.WriteLine($"記錄 {seed.Ledger:N0} 筆：儲存檢查（含回填）{sw.Elapsed.TotalSeconds:N2} 秒，結果 {(res.IsCreated ? "成功" : "失敗：" + res.Message)}");
    return res.IsCreated ? 0 : 1;
}

async Task<int> ReportBenchAsync()
{
    var db = Arg("db", "load.db");
    var runs = IntArg("runs", 5);
    var seed = SeedInfo.Load(Path.GetFullPath(db));
    await using var sp = Env.Build(db);
    var cases = new Workload(sp, seed).ReportCases();
    Console.WriteLine($"{"案例",-30}{"引擎",-8}{"狀態",-8}{"p50ms",9}{"maxms",9}  退回原因");
    foreach (var (name, def) in cases)
    {
        var times = new List<double>();
        DynamicModules.Application.Dynamic.ReportService.Data.ReportResult? last = null;
        for (var i = 0; i < runs + 1; i++)   // 第一次暖機，不計入
        {
            using var scope = sp.CreateScope();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var res = await scope.ServiceProvider.GetRequiredService<MediatR.IMediator>()
                .Send(new DynamicModules.Application.Dynamic.ReportService.Queries.PreviewReport.PreviewReportRequest(def, "zh-TW"));
            if (i > 0) times.Add(sw.Elapsed.TotalMilliseconds);
            last = (DynamicModules.Application.Dynamic.ReportService.Data.ReportResult)res.Data!;
        }
        times.Sort();
        Console.WriteLine($"{name,-30}{last!.Engine,-8}{last.State,-8}{times[times.Count / 2],9:F0}{times[^1],9:F0}  {string.Join(",", last.FallbackReasons ?? [])}");
    }
    return 0;
}

// 印出報表 SQL 與 EXPLAIN QUERY PLAN（month｜selective）：直接用計畫建構器，不經授權流程
async Task<int> ExplainAsync()
{
    var db = Arg("db", "load.db");
    var seed = SeedInfo.Load(Path.GetFullPath(db));
    var s = seed.Schema;
    await using var sp = Env.Build(db);
    using var scope = sp.CreateScope();
    var ctx = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
    var moduleId = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(
        Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AsNoTracking(ctx.Set<DynamicModules.Domain.Entities.Dynamic.Module>()), m => m.Key == "ledger");
    var date = new DynamicModules.Domain.Reports.ReportSqlField(s.LedgerDateId, DynamicModules.Domain.Reports.ReportSqlKind.Date, 0, false, "month");
    var debit = new DynamicModules.Domain.Reports.ReportSqlField(s.LedgerDebitId, DynamicModules.Domain.Reports.ReportSqlKind.Number, 2, false, null);
    var status = new DynamicModules.Domain.Reports.ReportSqlField(s.LedgerStatusId, DynamicModules.Domain.Reports.ReportSqlKind.Text, 0, false, null);
    var range = new DynamicModules.Domain.Reports.ReportSqlCondition(date with { Bucket = null }, DynamicModules.Domain.Reports.ReportSqlOp.Range, [seed.LastDate.AddDays(-30).ToDateTime(TimeOnly.MinValue), seed.LastDate.AddDays(1).ToDateTime(TimeOnly.MinValue)]);
    var draft = new DynamicModules.Domain.Reports.ReportSqlCondition(status, DynamicModules.Domain.Reports.ReportSqlOp.Eq, [s.Statuses[1]]);
    var which = Arg("case", "month");
    var filters = which == "all"
        ? [new DynamicModules.Domain.Reports.ReportSqlCondition(date with { Bucket = null }, DynamicModules.Domain.Reports.ReportSqlOp.Range, [seed.FirstDate.ToDateTime(TimeOnly.MinValue), seed.LastDate.AddDays(1).ToDateTime(TimeOnly.MinValue)])]
        : which == "selective"
        ? new[] { new DynamicModules.Domain.Reports.ReportSqlCondition(date with { Bucket = null }, DynamicModules.Domain.Reports.ReportSqlOp.Range, [seed.FirstDate.ToDateTime(TimeOnly.MinValue), seed.LastDate.AddDays(1).ToDateTime(TimeOnly.MinValue)]), draft }
        : [range];
    var query = new DynamicModules.Domain.Reports.ReportSqlQuery(
        new DynamicModules.Domain.Reports.ReportSqlScope(moduleId.Id, null), [date],
        [new("n", "countMain", null, null), new("d", "sum", debit, null)], filters, [], []);
    var connection = ctx.Database.GetDbConnection();
    await connection.OpenAsync();
    var driver = await DynamicModules.Persistence.Repositories.Dynamic.ReportQueryRepository.ChooseDriverAsync(connection, null!, query.Filters, default);
    Console.WriteLine($"驅動條件：{(driver is null ? "（無）" : driver.Field.Kind + " " + driver.Op)}");
    var built = DynamicModules.Persistence.Dynamic.Query.ReportQueryBuilder.Aggregate(query, driver, total: false, scope.ServiceProvider.GetRequiredService<DynamicModules.Persistence.Dynamic.Dialects.ISqlDialect>());
    Console.WriteLine(built.Sql);
    foreach (var row in await Dapper.SqlMapper.QueryAsync<(int id, int parent, int notused, string detail)>(connection, "EXPLAIN QUERY PLAN " + built.Sql, built.Parameters))
        Console.WriteLine($"  {row.id,3} {row.parent,3}  {row.detail}");
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var n = (await Dapper.SqlMapper.QueryAsync(connection, built.Sql, built.Parameters)).Count();
    Console.WriteLine($"執行 {sw.Elapsed.TotalMilliseconds:F0} ms，{n} 列");
    return 0;
}

async Task<int> ReportCheckAsync()
{
    var db = Arg("db", "load.db");
    var seed = SeedInfo.Load(Path.GetFullPath(db));
    await using var sp = Env.Build(db);
    var options = sp.GetRequiredService<DynamicModules.Application.Dynamic.ReportService.ReportOptions>();
    options.MaxLoadRows = int.MaxValue - 1;
    options.MaxRows = int.MaxValue - 1;
    var failures = 0;
    foreach (var (name, def) in new Workload(sp, seed).ReportCases())
    {
        async Task<DynamicModules.Application.Dynamic.ReportService.Data.ReportResult> Run(DynamicModules.Application.Dynamic.ReportService.ReportEngineMode mode)
        {
            options.Engine = mode;
            using var scope = sp.CreateScope();
            var res = await scope.ServiceProvider.GetRequiredService<MediatR.IMediator>()
                .Send(new DynamicModules.Application.Dynamic.ReportService.Queries.PreviewReport.PreviewReportRequest(def, "zh-TW"));
            return (DynamicModules.Application.Dynamic.ReportService.Data.ReportResult)res.Data!;
        }
        var memory = await Run(DynamicModules.Application.Dynamic.ReportService.ReportEngineMode.MemoryOnly);
        var auto = await Run(DynamicModules.Application.Dynamic.ReportService.ReportEngineMode.Auto);
        bool Same()
        {
            if (memory.State != auto.State || (memory.Groups?.Count ?? -1) != (auto.Groups?.Count ?? -1)) return false;
            bool Eq(IReadOnlyDictionary<string, decimal?>? a, IReadOnlyDictionary<string, decimal?>? b) =>
                a is null ? b is null : b is not null && a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);
            if (!Eq(memory.Total, auto.Total)) return false;
            for (var i = 0; i < (memory.Groups?.Count ?? 0); i++)
                if (!memory.Groups![i].GroupKeys.SequenceEqual(auto.Groups![i].GroupKeys) || !Eq(memory.Groups[i].Values, auto.Groups[i].Values)) return false;
            return true;
        }
        var same = Same();
        if (!same) failures++;
        Console.WriteLine($"{(same ? "一致" : "不一致"),-6}{name,-30}記憶體={memory.State}/{memory.Engine} 之後={auto.State}/{auto.Engine} 組數={memory.Groups?.Count}/{auto.Groups?.Count} {string.Join(",", auto.FallbackReasons ?? [])}");
    }
    Console.WriteLine(failures == 0 ? "全部一致" : $"{failures} 個案例不一致");
    return failures == 0 ? 0 : 1;
}

void Check(string dbPath)
{
    using var c = new SqliteConnection(Env.ConnectionString(dbPath));
    c.Open();
    string Scalar(string sql) { using var q = c.CreateCommand(); q.CommandText = sql; return Convert.ToString(q.ExecuteScalar())!; }
    Console.WriteLine($"SQLite {Scalar("select sqlite_version()")}");
    foreach (var p in new[] { "journal_mode", "synchronous", "busy_timeout", "page_size", "cache_size", "wal_autocheckpoint" })
        Console.WriteLine($"PRAGMA {p} = {Scalar($"PRAGMA {p}")}");
    foreach (var f in new[] { dbPath, dbPath + "-wal" })
        if (File.Exists(f)) Console.WriteLine($"{Path.GetFileName(f)}：{new FileInfo(f).Length / 1024.0 / 1024:N0} MB");
    foreach (var t in new[] { "Dynamic_Record", "Dynamic_RecordIndex", "Dynamic_RecordHistory" })
        Console.WriteLine($"{t}：{long.Parse(Scalar($"select count(*) from {t}")):N0} 列");
    Console.WriteLine($"sqlite_stat1 存在：{Scalar("select count(*) from sqlite_master where name='sqlite_stat1'") == "1"}");
}
