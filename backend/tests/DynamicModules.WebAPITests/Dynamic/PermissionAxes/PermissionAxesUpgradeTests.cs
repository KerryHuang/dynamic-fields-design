using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DynamicModules.Application.Common.Acting;
using DynamicModules.Application.Common.Security;
using DynamicModules.Domain.Authorization;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Persistence.Postgres;
using DynamicModules.Persistence.Seed;
using DynamicModules.TestSupport;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic.PermissionAxes;

/// <summary>
/// 權限軸的資料與升級帶入（權限軸設計 §6、§18）：全新庫（PAX-01）、升級冒煙（PAX-01b）、回滾與重新升級（PAX-02b）、能力對照（PAX-03）。
/// 升級類以「已到最新 schema、沒跑過 SeedData」的資料庫置入 <see cref="PermissionAxesUpgradeData"/>，再照 Program.cs 啟動測試主機
/// （MigrateAsync → SeedData.EnsureSeededAsync → AdminBootstrap）。本段沒有 schema 變更，最新的 migration 就是上一版的最後一個。雙跑。
/// </summary>
[Trait("Category", "Postgres")]
public sealed class PermissionAxesUpgradeTests
{
    /// <summary>上一版的資料庫＋升級後的測試主機；Dispose 時連同資料庫一起清掉。</summary>
    private sealed class Upgraded : IDisposable
    {
        private SqliteConnection? _keepAlive;
        private string? _postgres;
        public TestApp App { get; private set; } = null!;
        public Dictionary<string, long> Groups { get; private set; } = null!;
        public List<string> GuardBefore { get; private set; } = null!;

        public static async Task<Upgraded> CreateAsync()
        {
            var u = new Upgraded();
            string connectionString;
            DynamicDbContext context;
            if (TestDatabase.IsPostgres)
            {
                connectionString = u._postgres = await PostgresTestServer.CreateDatabaseAsync();
                context = new DynamicDbContext((DbContextOptions<DynamicDbContext>)PostgresDatabase.Configure(new DbContextOptionsBuilder<DynamicDbContext>(),
                    connectionString, new PostgresWriteSettings(PostgresWriteSettings.DefaultLockTimeoutSeconds)).Options);
            }
            else
            {
                connectionString = $"Data Source=file:pax{Guid.NewGuid():N}?mode=memory&cache=shared";
                u._keepAlive = new SqliteConnection(connectionString);
                await u._keepAlive.OpenAsync();
                MigratedDatabaseTemplate.RestoreInto(u._keepAlive);
                context = new DynamicDbContext(new DbContextOptionsBuilder<DynamicDbContext>().UseSqlite(connectionString).Options);
            }
            await using (context)
            {
                u.Groups = await PermissionAxesUpgradeData.InsertAsync(context);
                u.GuardBefore = PermissionAxesUpgradeData.GuardRows(context, withoutPermissionAdmin: true);
            }
            u.App = new TestApp(seedDemoPeople: false, sharedConnectionString: connectionString);
            _ = u.App.Services; // 啟動：照 Program.cs 的順序 MigrateAsync、EnsureSeededAsync
            return u;
        }

        public T Db<T>(Func<DynamicDbContext, T> read)
        {
            using var scope = App.Services.CreateScope();
            return read(scope.ServiceProvider.GetRequiredService<DynamicDbContext>());
        }

        public async Task SeedAgainAsync()
        {
            using var scope = App.Services.CreateScope();
            await SeedData.EnsureSeededAsync(scope.ServiceProvider.GetRequiredService<DynamicDbContext>());
        }

        public async Task<(List<string> Groups, List<string> Users)> HoldersAsync()
        {
            using var scope = App.Services.CreateScope();
            return await PermissionAxesUpgradeData.PermissionAdminHoldersAsync(scope.ServiceProvider.GetRequiredService<DynamicDbContext>());
        }

        public void Dispose()
        {
            App.Dispose();
            _keepAlive?.Dispose();
            if (_postgres is not null) PostgresTestServer.DropDatabase(_postgres);
        }
    }

    [Fact]
    public void PAX01_FreshDatabase_HasActivePermissionAdminProgram_MarkerSet_NoGrantRows()
    {
        using var app = new TestApp();
        void Check(TestApp a)
        {
            using var scope = a.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
            db.Set<DynamicProgram>().AsNoTracking().Single(p => p.ProgramCode == SystemProgramCodes.PermissionAdmin).Active.Should().BeTrue();
            db.Set<SystemSetting>().Any(s => s.Key == SystemSetting.PermissionAxesImportedOn).Should().BeTrue();
            (db.Set<GroupProgramPrivilege>().Count(p => p.ProgramCode == SystemProgramCodes.PermissionAdmin)
             + db.Set<UserProgramPrivilege>().Count(p => p.ProgramCode == SystemProgramCodes.PermissionAdmin)).Should().Be(0);
        }
        Check(app);

        using var restarted = new TestApp(sharedConnectionString: app.ConnectionString);
        Check(restarted);
    }

    [Fact]
    public async Task PAX01b_UpgradeSmoke_StartsOnBothProviders_GuardRowsUnchangedExceptImport()
    {
        using var u = await Upgraded.CreateAsync();

        u.Db(db => PermissionAxesUpgradeData.GuardRows(db, withoutPermissionAdmin: true)).Should().Equal(u.GuardBefore);
        var (groups, users) = await u.HoldersAsync();
        groups.Should().Equal(PermissionAxesUpgradeData.ImportedGroups);
        users.Should().Equal(PermissionAxesUpgradeData.ImportedUsers);
        (await u.App.AsUser().GetAsync("/api/health")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static object Body(bool read, bool edit, bool permissionAdmin) =>
        new { designer = new { read, add = false, edit, delete = false }, permissionAdmin };

    [Fact]
    public async Task PAX02b_RollbackAndReupgrade_KeepGrantsAndRevokes_DeletingMarkerReimports()
    {
        using var u = await Upgraded.CreateAsync();
        var admin = u.App.AsUser();
        foreach (var (group, body) in new[] { ("pax-g1", Body(true, true, false)), ("pax-g2", Body(true, false, true)) })
        {
            var res = await admin.PutAsJsonAsync($"{Base}/admin/groups/{u.Groups[group]}/system-privileges", body);
            res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        }

        // 回滾：舊版程式不認 SYS00001，舊版 SeedData 把 program 列設成 Active = false（不經新程式，直接改）
        using (var scope = u.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
            (await db.Set<DynamicProgram>().SingleAsync(p => p.ProgramCode == SystemProgramCodes.PermissionAdmin)).Active = false;
            await db.SaveChangesAsync();
        }
        await u.SeedAgainAsync();
        await u.SeedAgainAsync();

        var (groups, _) = await u.HoldersAsync();
        groups.Should().Equal(["pax-g2", "pax-g3", "pax-g6"], "G1 的收回不復活、G2 的授予不丟失");
        u.Db(db => db.Set<DynamicProgram>().AsNoTracking().Single(p => p.ProgramCode == SystemProgramCodes.PermissionAdmin).Active).Should().BeTrue();

        // 強制重帶：刪掉標記，照當時的設計器修改權重算
        using (var scope = u.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
            db.Set<SystemSetting>().Remove(await db.Set<SystemSetting>().SingleAsync(s => s.Key == SystemSetting.PermissionAxesImportedOn));
            await db.SaveChangesAsync();
        }
        await u.SeedAgainAsync();
        (groups, _) = await u.HoldersAsync();
        groups.Should().Equal(PermissionAxesUpgradeData.ImportedGroups, "G1 恢復、G2 消失");
    }

    [Fact]
    public async Task PAX03_CapabilityContract_SameUsersCanSavePermissions_AsTheOldRule()
    {
        using var u = await Upgraded.CreateAsync();
        var admin = u.App.AsUser();
        var module = await CreateModule(admin, "pax-up-a");
        var body = JsonNode.Parse(await (await admin.GetAsync($"{Base}/modules/{module}/permissions")).Content.ReadAsStringAsync())!;

        // 舊規則：啟用中、有效 DYN00000 修改（任一群組列或個人列 CanEdit）的人能存模組權限
        var oldRule = u.Db(db =>
        {
            var members = db.Set<UserGroupMapping>().AsNoTracking().ToList();
            var groupEdit = db.Set<GroupProgramPrivilege>().AsNoTracking()
                .Where(p => p.ProgramCode == SystemProgramCodes.Designer && p.CanEdit).Select(p => p.GroupId).ToHashSet();
            var userEdit = db.Set<UserProgramPrivilege>().AsNoTracking()
                .Where(p => p.ProgramCode == SystemProgramCodes.Designer && p.CanEdit).Select(p => p.UserId).ToHashSet();
            return db.Set<AppUser>().AsNoTracking().Where(x => x.IsActive && !x.IsSystemAdmin).ToList()
                .Where(x => userEdit.Contains(x.UserId) || members.Any(m => m.UserId == x.UserId && groupEdit.Contains(m.GroupId)))
                .Select(x => x.UserId).Order(StringComparer.Ordinal).ToList();
        });
        oldRule.Should().Equal("g3m", "g6m", "u1", "u8");

        var allowed = new List<string>();
        foreach (var user in PermissionAxesUpgradeData.Users.Where(x => x != "u5"))
        {
            var res = await u.App.AsUser(user).PutAsync($"{Base}/modules/{module}/permissions",
                new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));
            if (res.StatusCode == HttpStatusCode.OK) allowed.Add(user);
            else await PaxAssert.Forbidden(res, "permissionAdminRequired");
        }
        allowed.Order(StringComparer.Ordinal).Should().Equal(oldRule, "K1、K2：升級前後能存模組權限的人逐人相同");

        // N1：G3（只有修改、沒有讀取）的成員因權限設定隱含讀取，進得了設計器（新增的能力）
        (await u.App.AsUser("g3m").GetAsync($"{Base}/modules")).StatusCode.Should().Be(HttpStatusCode.OK);

        // K5：停用帳號照列帶入，停用期間沒有權限設定，重新啟用後拿回
        async Task<bool> IsPermissionAdmin()
        {
            using var scope = u.App.Services.CreateScope();
            using var _ = ActingScope.Begin("u5");
            return await scope.ServiceProvider.GetRequiredService<IDynamicAccessGuard>().IsPermissionAdminAsync(CancellationToken.None);
        }
        (await IsPermissionAdmin()).Should().BeFalse();
        (await admin.PostAsync($"{Base}/admin/users/u5/activate", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await IsPermissionAdmin()).Should().BeTrue();
    }
}
