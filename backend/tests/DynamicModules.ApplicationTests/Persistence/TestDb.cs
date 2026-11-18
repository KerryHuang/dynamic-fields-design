using System.Data.Common;
using DynamicModules.Application.Common;
using DynamicModules.Domain.Data;
using DynamicModules.Persistence;
using DynamicModules.Persistence.Dynamic.Dialects;
using DynamicModules.Persistence.Postgres;
using DynamicModules.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Persistence;

/// <summary>
/// 測試用資料庫，已套用 migration。預設是 SQLite in-memory（連線保持開啟）；<c>FORMWRIGHT_TEST_DB=postgres</c> 時改成
/// PostgreSQL 測試伺服器上從樣板複製的一個資料庫（PostgreSQL provider 設計 §6.2、§6.3），Dispose 時刪掉。
/// PostgreSQL 模式的 DbContext 選項呼叫正式的 <see cref="PostgresDatabase.Configure"/>（模型調整、AutoTransactionBehavior、閘門與全庫鎖），
/// 測試專屬的命令計數與寫入守衛另外疊加。
/// </summary>
public sealed class TestDb : IAsyncDisposable
{
    private SqliteConnection? _sqlite;
    private string? _postgres;

    /// <summary>DbContext 用的連線（SQLite 是保持開啟的那一條；PostgreSQL 是 context 自己的連線，用前要先開）。</summary>
    public DbConnection Connection => _sqlite ?? Context.Database.GetDbConnection();
    public DynamicDbContext Context { get; private set; } = null!;
    /// <summary>PostgreSQL 模式下這個資料庫的連線字串；SQLite 模式是 null。</summary>
    public string? PostgresConnectionString => _postgres;
    /// <summary>這個資料庫送出的 SQL 命令數，測試用前後差量計算查詢次數。</summary>
    public CountingCommandInterceptor Counter { get; } = new();

    /// <param name="migrate">false 時不套 migration，由測試自己依序套用（驗證既有資料升級；只在 SQLite 有意義，§5.3）。</param>
    /// <param name="restore">給了就用它把資料整份複製進這個庫，取代 migration 範本（例如已產生 Demo 資料的快照；只支援 SQLite）。</param>
    /// <param name="postgresSource">PostgreSQL 模式的對應：從這個資料庫（連線字串）整份複製，取代 migration 樣板。</param>
    public static async Task<TestDb> CreateAsync(bool migrate = true, Action<SqliteConnection>? restore = null, string? postgresSource = null)
    {
        var db = new TestDb();
        if (TestDatabase.IsPostgres)
        {
            if (restore is not null) throw new NotSupportedException("TestDb 的 restore（SQLite 快照）在 PostgreSQL 模式不支援，改給 postgresSource");
            db._postgres = postgresSource is not null
                ? await PostgresTestServer.CloneDatabaseAsync(postgresSource)
                : await PostgresTestServer.CreateDatabaseAsync(migrated: migrate);
            db.Context = new DynamicDbContext((DbContextOptions<DynamicDbContext>)PostgresDatabase.Configure(
                    new DbContextOptionsBuilder<DynamicDbContext>(), db._postgres, PostgresSettings)
                .AddInterceptors(db.Counter, WriteGuardInterceptor.Instance)
                .Options);
            return db;
        }

        if (postgresSource is not null) throw new NotSupportedException("postgresSource 只在 PostgreSQL 模式使用");
        db._sqlite = new SqliteConnection("DataSource=:memory:");
        await db._sqlite.OpenAsync();
        // 從已 migrate 的範本整份複製，不必每個測試重跑 75 個 migration；後面的 MigrateAsync 只剩檢查歷程
        if (restore is not null) restore(db._sqlite);
        else if (migrate) MigratedDatabaseTemplate.RestoreInto(db._sqlite);
        db.Context = new DynamicDbContext(new DbContextOptionsBuilder<DynamicDbContext>()
            .UseSqlite(db._sqlite, b => b.MigrationsAssembly("DynamicModules.Persistence"))
            .AddInterceptors(db.Counter, WriteGuardInterceptor.Instance)
            .Options);
        if (migrate) await db.Context.Database.MigrateAsync();
        return db;
    }

    /// <summary>PostgreSQL 模式的寫入設定（等鎖上限用正式預設值）；TestDb 的 DbContext 與 <see cref="AddProviderServices"/> 共用。</summary>
    private static readonly PostgresWriteSettings PostgresSettings = new(PostgresWriteSettings.DefaultLockTimeoutSeconds);

    /// <summary>
    /// 自己組 DI 的測試主機用：依目前的測試資料庫註冊 provider 專屬的方言與交易管理（與 AddSqliteDatabase／AddPostgresDatabase 相同的實作），
    /// 其餘（repository、DbContext）照各測試自己的註冊。
    /// </summary>
    public static void AddProviderServices(IServiceCollection services)
    {
        if (TestDatabase.IsPostgres)
        {
            services.AddSingleton<ISqlDialect, PostgresDialect>();
            services.AddSingleton(PostgresSettings);
            services.AddScoped<ITransactionManager, PostgresTransactionManager>();
        }
        else
        {
            services.AddSingleton<ISqlDialect, SqliteDialect>();
            services.AddScoped<ITransactionManager, EfTransactionManager>();
        }
    }

    /// <summary>直接建交易管理（不經 DI 的測試）：SQLite 是 <see cref="EfTransactionManager"/>，PostgreSQL 是 <see cref="PostgresTransactionManager"/>。</summary>
    public static ITransactionManager NewTransactionManager(DynamicDbContext context) =>
        TestDatabase.IsPostgres ? new PostgresTransactionManager(context, PostgresSettings) : new EfTransactionManager(context);

    public async ValueTask DisposeAsync()
    {
        await Context.DisposeAsync();
        if (_sqlite is not null) await _sqlite.DisposeAsync();
        if (_postgres is not null) await PostgresTestServer.DropDatabaseAsync(_postgres);
    }
}
