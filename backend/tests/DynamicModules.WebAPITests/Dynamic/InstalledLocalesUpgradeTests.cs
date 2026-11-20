using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
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

    [Fact]
    public async Task FromPreviousVersion_SyncsBuiltin()
    {
        SqliteConnection? keepAlive = null;
        string? postgres = null;
        string connectionString;
        DynamicDbContext context;
        if (TestDatabase.IsPostgres)
        {
            connectionString = postgres = await PostgresTestServer.CreateDatabaseAsync(migrated: false);
            context = new DynamicDbContext((DbContextOptions<DynamicDbContext>)PostgresDatabase.Configure(new DbContextOptionsBuilder<DynamicDbContext>(),
                connectionString, new PostgresWriteSettings(PostgresWriteSettings.DefaultLockTimeoutSeconds)).Options);
        }
        else
        {
            connectionString = $"Data Source=file:loc{Guid.NewGuid():N}?mode=memory&cache=shared";
            keepAlive = new SqliteConnection(connectionString);
            await keepAlive.OpenAsync();
            context = new DynamicDbContext(new DbContextOptionsBuilder<DynamicDbContext>()
                .UseSqlite(connectionString, b => b.MigrationsAssembly("DynamicModules.Persistence")).Options);
        }
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
}
