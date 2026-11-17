using DynamicModules.Domain.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.TestSupport;

/// <summary>
/// 已套完全部 migration 的 SQLite 範本（記憶體庫）。每個測試行程第一次用到時建一次，之後每個測試把它整份複製到自己的資料庫，
/// 省下每測試一次完整 <c>MigrateAsync</c>（約 0.35 秒、75 個 migration）。
/// 範本只活在行程內、由當下組件的 migration 建出來，不落地成檔，所以 migration 一變就必然是新的，沒有過期問題。
/// </summary>
public static class MigratedDatabaseTemplate
{
    private static readonly object Gate = new();
    private static SqliteConnection? _template;

    /// <summary>把範本整份複製進已開啟的目的連線（目的庫必須是空的、沒有進行中的交易）。可多執行緒同時呼叫。</summary>
    public static void RestoreInto(SqliteConnection destination)
    {
        lock (Gate)
        {
            _template ??= Build();
            // 範本連線不是執行緒安全的，備份期間整段持鎖；備份 2 MB 以下只要 1 ms 上下
            _template.BackupDatabase(destination);
        }
    }

    private static SqliteConnection Build()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var context = new DynamicDbContext(new DbContextOptionsBuilder<DynamicDbContext>()
            .UseSqlite(connection, b => b.MigrationsAssembly("DynamicModules.Persistence"))
            .Options);
        context.Database.Migrate();
        return connection;
    }
}
