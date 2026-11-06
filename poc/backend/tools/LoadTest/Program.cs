using System.Text.Json;
using DynamicModules.Domain.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using LoadTest;

// 用法：
//   dotnet run --project poc/backend/tools/LoadTest -c Release -- seed --db <path> [--years 10] [--per-year 300000] [--accounts 300] [--customers 20000]
//   ... -- run --db <path> [--users 20] [--duration 60] [--phases solo,mixed,stress,writes] [--out report.json]
//   ... -- check --db <path>        印出 pragma、檔案大小、筆數
//   ... -- selfcheck                驗證批次灌入的列與 CreateRecordHandler 產生的一致
//   ... -- analyze --db <path>      對資料庫跑 ANALYZE（實驗用）

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

    if (phases.Contains("solo")) Done(await w.Solo(all, IntArg("solo-runs", 5)));
    // 實際使用：每人每 1–3 秒做一件事，約 80% 讀、20% 寫
    if (phases.Contains("mixed")) Done(await w.Crowd("mixed（有思考時間）", all, users, duration, (1000, 3000)));
    // 壓力：不停手，量系統容量上限
    if (phases.Contains("stress")) Done(await w.Crowd("stress（不停手）", all, users, duration, (0, 0)));
    // 只寫：量寫入鎖等待
    if (phases.Contains("writes")) Done(await w.Crowd("writes（只寫不停手）", Workload.Only(all, Arg("write-ops", "create_entry,update_entry").Split(',')), users, duration, (0, 0)));

    var outPath = Arg("out", "");
    if (outPath != "") File.WriteAllText(outPath, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
    return 0;
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
