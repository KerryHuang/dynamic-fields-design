using DynamicModules.TestSupport;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.DemoService.Commands.GenerateDemoData;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic.Demo;

/// <summary>
/// 「Today = 2026-11-05、預設設定」的完整 Demo 產生結果，每個測試行程只在第一次用到時產生一次（lazy、執行緒安全），
/// 之後每個要在結果上寫入的測試各自複製一份：資料庫用 SQLite backup API 整份複製進自己的記憶體庫（不落地成檔，
/// 沒有 WAL 問題）；PostgreSQL 模式則把產生結果複製成一個不再連線的來源庫，每份以 <c>CREATE DATABASE … TEMPLATE</c> 複製。
/// 上傳目錄遞迴複製到自己的暫存目錄、隨 <see cref="DemoTestHost"/> 釋放時刪除。
/// 快照本身唯讀、行程內共用，不釋放（同 MigratedDatabaseTemplate）。
/// 只有「產生本身」的參數與預設相同時才能用；換今天、換附件設定、要驗產生過程的測試仍要真的產生。
/// </summary>
internal sealed class DemoSnapshot
{
    private static readonly Lazy<Task<DemoSnapshot>> Instance = new(() => Task.Run(BuildAsync));

    private readonly object _gate = new();
    private readonly SqliteConnection? _db;
    private readonly string _files;

    private DemoSnapshot(SqliteConnection? db, string? postgresSource, string files, ServiceResult result, List<(int Done, int Total)> progress)
        => (_db, PostgresSource, _files, Result, Progress) = (db, postgresSource, files, result, progress);

    /// <summary>PostgreSQL 模式：快照所在的資料庫（連線字串，只當複製來源、不連進去）；SQLite 模式是 null。</summary>
    public string? PostgresSource { get; }

    /// <summary>產生指令的回傳。</summary>
    public ServiceResult Result { get; }

    /// <summary>產生時每次回報的（完成數、總數）。</summary>
    public List<(int Done, int Total)> Progress { get; }

    public static Task<DemoSnapshot> GetAsync() => Instance.Value;

    /// <summary>複製一份可寫的 Demo 主機（今天、設定與產生時相同，除非 <paramref name="configure"/> 覆寫不影響產生結果的服務，如時鐘包裝或 AuthSettings）。</summary>
    public static async Task<DemoTestHost> CreateHostAsync(DateOnly? today = null, Action<IServiceCollection>? configure = null)
    {
        var snapshot = await GetAsync();
        snapshot.Result.IsSuccess.Should().BeTrue($"{snapshot.Result.Message} {string.Join("; ", snapshot.Result.Errors.Select(e => $"{e.Key}: {string.Join(" / ", e.Value)}"))}");
        return await DemoTestHost.CreateAsync(today, configure, snapshot);
    }

    internal void RestoreDatabase(SqliteConnection destination)
    {
        // 快照連線不是執行緒安全的，備份期間整段持鎖
        lock (_gate) _db!.BackupDatabase(destination);
    }

    internal void CopyFilesTo(string destination) => CopyDirectory(_files, destination);

    private static async Task<DemoSnapshot> BuildAsync()
    {
        var progress = new List<(int, int)>();
        await using var host = await DemoTestHost.CreateAsync();
        var result = await host.Send(new GenerateDemoDataRequest(DemoTestHost.Today, new SyncProgress(progress)));
        SqliteConnection? db = null;
        string? postgresSource = null;
        if (!TestDatabase.IsPostgres)
        {
            db = new SqliteConnection("DataSource=:memory:");
            db.Open();
        }
        var files = Path.Combine(Path.GetTempPath(), $"dm-demo-snap-{Guid.NewGuid():N}");
        if (result.IsSuccess)
        {
            // 來源庫活到測試行程結束（容器隨行程清掉；外部伺服器上與一般測試庫一樣要靠刪庫，這裡在行程結束時盡力刪）
            if (db is not null) ((SqliteConnection)host.Db.Connection).BackupDatabase(db);
            else
            {
                postgresSource = await PostgresTestServer.CloneDatabaseAsync(host.Db.PostgresConnectionString!);
                AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { PostgresTestServer.DropDatabase(postgresSource); } catch { /* 盡力而為 */ } };
            }
            CopyDirectory(host.AttachmentRoot, files);
            AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { Directory.Delete(files, true); } catch (IOException) { } };
        }
        return new DemoSnapshot(db, postgresSource, files, result, progress);
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        if (!Directory.Exists(from)) return;
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
    }

    /// <summary>Progress&lt;T&gt; 會丟到同步內容非同步回報，這裡用同步版。</summary>
    private sealed class SyncProgress(List<(int, int)> into) : IProgress<(int, int)>
    {
        public void Report((int, int) value) => into.Add(value);
    }
}
