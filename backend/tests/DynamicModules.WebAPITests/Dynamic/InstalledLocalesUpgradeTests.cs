using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Persistence.Postgres;
using DynamicModules.TestSupport;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 從前一版升級（migration InstalledLocales，語系包 i4a）：資料庫只套到前一版最後一個 migration、已有語系政策，再照 Program.cs 啟動
/// （套 InstalledLocales → 同步內建語系 → 語系快照 → 政策快照）。<c>MigrationConsistencyTests</c> 只證明模型與 snapshot 一致，這裡證明升級路徑。雙跑。
/// </summary>
[Trait("Category", "Postgres")]
public sealed class InstalledLocalesUpgradeTests
{
    private const string Previous = "SharedMailSettings";

    /// <summary>建一個空資料庫與連到它的 context（SQLite 記憶體共用快取，或 PostgreSQL 新資料庫）。</summary>
    private static async Task<(string ConnectionString, DynamicDbContext Context, SqliteConnection? KeepAlive, string? Postgres)> NewDatabaseAsync()
    {
        if (TestDatabase.IsPostgres)
        {
            var pg = await PostgresTestServer.CreateDatabaseAsync(migrated: false);
            return (pg, new DynamicDbContext((DbContextOptions<DynamicDbContext>)PostgresDatabase.Configure(new DbContextOptionsBuilder<DynamicDbContext>(),
                pg, new PostgresWriteSettings(PostgresWriteSettings.DefaultLockTimeoutSeconds)).Options), null, pg);
        }
        var cs = $"Data Source=file:loc{Guid.NewGuid():N}?mode=memory&cache=shared";
        var keepAlive = new SqliteConnection(cs);
        await keepAlive.OpenAsync();
        return (cs, new DynamicDbContext(new DbContextOptionsBuilder<DynamicDbContext>()
            .UseSqlite(cs, b => b.MigrationsAssembly("DynamicModules.Persistence")).Options), keepAlive, null);
    }

    [Fact]
    public async Task FromPreviousVersion_SyncsBuiltin()
    {
        var (connectionString, context, keepAlive, postgres) = await NewDatabaseAsync();
        try
        {
            await using (context)
            {
                await context.GetService<IMigrator>().MigrateAsync(Previous);
                context.Set<SystemSetting>().Add(new SystemSetting { Key = PolicyCatalog.LocaleDefault.SettingKey, Value = "en-US" });
                await context.SaveChangesAsync();
            }

            using var app = new TestApp(seedDemoPeople: false, sharedConnectionString: connectionString);
            _ = app.Services; // 啟動：照 Program.cs 的順序

            using (var scope = app.Services.CreateScope())
            {
                var rows = await scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<InstalledLocale>().AsNoTracking().ToListAsync();
                rows.Select(r => r.Code).Should().BeEquivalentTo("zh-TW", "en-US", "th", "vi");
                rows.Should().OnlyContain(r => r.Origin == LocaleOrigin.Builtin);
            }
            app.Services.GetRequiredService<LocaleRegistry>().Codes.Should().Equal("zh-TW", "en-US", "th", "vi");
            var options = await (await app.AsUser(null).GetAsync($"{Base}/auth/options")).Content.ReadFromJsonAsync<JsonElement>();
            options.GetProperty("defaultLocale").GetString().Should().Be("en-US");
            options.GetProperty("enabledLocales").EnumerateArray().Select(e => e.GetString()).Should().Equal("zh-TW", "en-US", "th", "vi");
            options.GetProperty("locales").EnumerateArray().Select(l => l.GetProperty("code").GetString()).Should().Equal("zh-TW", "en-US", "th", "vi");
        }
        finally
        {
            keepAlive?.Dispose();
            if (postgres is not null) PostgresTestServer.DropDatabase(postgres);
        }
    }

    /// <summary>從 i4a 的 InstalledLocales 升級到 LocalePacks（i4d1 計畫 Task 2 建議 1）：既有的匯入語系與政策值還在，兩張新表可寫可讀。</summary>
    [Fact]
    public async Task FromInstalledLocales_PackTablesUsable()
    {
        var at = new DateTime(2026, 11, 20, 1, 0, 0, DateTimeKind.Utc);
        var (connectionString, context, keepAlive, postgres) = await NewDatabaseAsync();
        try
        {
            await using (context)
            {
                await context.GetService<IMigrator>().MigrateAsync("InstalledLocales");
                context.Set<InstalledLocale>().Add(new InstalledLocale
                {
                    Code = "ja", NativeName = "日本語", FallbackCode = "en-US", Origin = LocaleOrigin.Installed, CreatedUtc = at, ModifiedUtc = at,
                });
                context.Set<SystemSetting>().Add(new SystemSetting { Key = PolicyCatalog.LocaleDefault.SettingKey, Value = "en-US" });
                await context.SaveChangesAsync();
            }

            using var app = new TestApp(seedDemoPeople: false, sharedConnectionString: connectionString);
            _ = app.Services;

            using var scope = app.Services.CreateScope();
            var sp = scope.ServiceProvider;
            var db = sp.GetRequiredService<DynamicDbContext>();
            (await db.Set<InstalledLocale>().AsNoTracking().SingleAsync(l => l.Code == "ja")).FallbackCode.Should().Be("en-US");
            (await db.Set<SystemSetting>().AsNoTracking().SingleAsync(s => s.Key == PolicyCatalog.LocaleDefault.SettingKey)).Value.Should().Be("en-US");

            var packs = sp.GetRequiredService<ILocalePackRepository>();
            packs.AddVersion(new LocalePack { Code = "ja", UiJson = """{"menu.home":"ホーム"}""", ImportedUtc = at, ModifiedUtc = at }, null, null, at);
            packs.AddPreview(new LocalePackPreview
            {
                Id = Guid.NewGuid().ToString("N"), UserId = "admin", Code = "ja", FileHash = new string('a', 64), FileName = "ja.langpack.json",
                CreatedUtc = at, ExpiresUtc = at.AddMinutes(10),
            });
            await sp.GetRequiredService<IUnitOfWork>().CommitAsync(CancellationToken.None);

            (await packs.ListCurrentAsync(CancellationToken.None)).Should().ContainSingle(p => p.Code == "ja");
            (await sp.GetRequiredService<ILocaleRepository>().StampAsync(CancellationToken.None)).PackCount.Should().Be(1);
        }
        finally
        {
            keepAlive?.Dispose();
            if (postgres is not null) PostgresTestServer.DropDatabase(postgres);
        }
    }
}
