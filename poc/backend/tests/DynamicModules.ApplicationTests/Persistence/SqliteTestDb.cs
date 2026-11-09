using DynamicModules.Domain.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Persistence;

/// <summary>SQLite in-memory 資料庫（連線保持開啟），已套用 migration。</summary>
public sealed class SqliteTestDb : IAsyncDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public SqliteConnection Connection => _connection;
    public DynamicDbContext Context { get; private set; } = null!;

    /// <param name="migrate">false 時不套 migration，由測試自己依序套用（驗證既有資料升級）。</param>
    public static async Task<SqliteTestDb> CreateAsync(bool migrate = true)
    {
        var db = new SqliteTestDb();
        await db._connection.OpenAsync();
        db.Context = new DynamicDbContext(new DbContextOptionsBuilder<DynamicDbContext>()
            .UseSqlite(db._connection, b => b.MigrationsAssembly("DynamicModules.Persistence"))
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
