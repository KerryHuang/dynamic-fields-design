using DynamicModules.Domain.Data;
using DynamicModules.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Persistence;

/// <summary>SQLite in-memory 資料庫（連線保持開啟），已套用 migration。</summary>
public sealed class SqliteTestDb : IAsyncDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public SqliteConnection Connection => _connection;
    public DynamicDbContext Context { get; private set; } = null!;
    /// <summary>這個資料庫送出的 SQL 命令數，測試用前後差量計算查詢次數。</summary>
    public CountingCommandInterceptor Counter { get; } = new();

    /// <param name="migrate">false 時不套 migration，由測試自己依序套用（驗證既有資料升級）。</param>
    public static async Task<SqliteTestDb> CreateAsync(bool migrate = true)
    {
        var db = new SqliteTestDb();
        await db._connection.OpenAsync();
        // 從已 migrate 的範本整份複製，不必每個測試重跑 75 個 migration；後面的 MigrateAsync 只剩檢查歷程
        if (migrate) MigratedDatabaseTemplate.RestoreInto(db._connection);
        db.Context = new DynamicDbContext(new DbContextOptionsBuilder<DynamicDbContext>()
            .UseSqlite(db._connection, b => b.MigrationsAssembly("DynamicModules.Persistence"))
            .AddInterceptors(db.Counter)
            .Options);
        if (migrate) await db.Context.Database.MigrateAsync();
        return db;
    }

    public async ValueTask DisposeAsync()
    {
        await Context.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
