using DynamicModules.ApplicationTests.Persistence;
using DynamicModules.Domain.Authorization;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Persistence.Postgres;
using DynamicModules.Persistence.Seed;
using DynamicModules.TestSupport;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic.PermissionAxes;

/// <summary>
/// 升級帶入 <see cref="PermissionAxesImport"/>（權限軸設計 §6.3，PAX-02、PAX-02c）：用不跑 SeedData 的資料庫（已到最新 schema；
/// 本段沒有 schema 變更，等同上一版）置入 <see cref="PermissionAxesUpgradeData"/> 後執行。雙跑：資料匯入與多實例在兩個 provider 行為要相同。
/// </summary>
[Trait("Category", "Postgres")]
public class PermissionAxesImportTests
{
    [Fact]
    public async Task PAX02_Import_GivesPermissionAdminToEveryDesignerEditRow_AndTouchesNothingElse()
    {
        await using var db = await TestDb.CreateAsync();
        await PermissionAxesUpgradeData.InsertAsync(db.Context);
        var before = PermissionAxesUpgradeData.GuardRows(db.Context, withoutPermissionAdmin: true);

        await SeedData.EnsureSeededAsync(db.Context);

        var (groups, users) = await PermissionAxesUpgradeData.PermissionAdminHoldersAsync(db.Context);
        groups.Should().Equal(PermissionAxesUpgradeData.ImportedGroups, "G1、G3、G6 帶入；G2、G5（殘列被清掉）、G7 沒有");
        users.Should().Equal(PermissionAxesUpgradeData.ImportedUsers, "u1、u5（停用照樣帶入）；u2、u4 沒有");
        PermissionAxesUpgradeData.GuardRows(db.Context, withoutPermissionAdmin: true).Should().Equal(before, "DYN00000 與業務模組的列逐列不變");
        (await db.Context.Set<SystemSetting>().AsNoTracking().SingleAsync(s => s.Key == SystemSetting.PermissionAxesImportedOn)).Value
            .Should().NotBeNullOrEmpty();
        (await db.Context.Set<DynamicProgram>().AsNoTracking().SingleAsync(p => p.ProgramCode == SystemProgramCodes.PermissionAdmin)).Active
            .Should().BeTrue("舊版退回時設成 false 的 program 列改回 true");
    }

    /// <summary>同一個庫的第二條連線與 DbContext（SQLite 用暫存檔，PostgreSQL 用同一個測試資料庫）。</summary>
    private sealed class SecondContext : IAsyncDisposable
    {
        private string? _file;
        public DynamicDbContext First { get; private set; } = null!;
        public DynamicDbContext Second { get; private set; } = null!;
        private TestDb? _db;

        public static async Task<SecondContext> CreateAsync()
        {
            var c = new SecondContext();
            if (TestDatabase.IsPostgres)
            {
                c._db = await TestDb.CreateAsync();
                c.First = c._db.Context;
                c.Second = new DynamicDbContext((DbContextOptions<DynamicDbContext>)PostgresDatabase.Configure(
                    new DbContextOptionsBuilder<DynamicDbContext>(), c._db.PostgresConnectionString!,
                    new PostgresWriteSettings(PostgresWriteSettings.DefaultLockTimeoutSeconds)).Options);
                return c;
            }
            // 兩條連線同時寫同一個 SQLite 庫要用檔案（共用快取的 in-memory 庫會直接 table is locked，不是正式站的行為）
            c._file = Path.Combine(Path.GetTempPath(), $"dm-pax02c-{Guid.NewGuid():N}.db");
            await using (var seed = new SqliteConnection($"Data Source={c._file}"))
            {
                await seed.OpenAsync();
                MigratedDatabaseTemplate.RestoreInto(seed);
            }
            DynamicDbContext Open() => new(new DbContextOptionsBuilder<DynamicDbContext>()
                .UseSqlite($"Data Source={c._file}", b => b.MigrationsAssembly("DynamicModules.Persistence")).Options);
            c.First = Open();
            c.Second = Open();
            return c;
        }

        public async ValueTask DisposeAsync()
        {
            await Second.DisposeAsync();
            if (_db is not null) { await _db.DisposeAsync(); return; }
            await First.DisposeAsync();
            SqliteConnection.ClearAllPools();
            foreach (var f in Directory.GetFiles(Path.GetDirectoryName(_file!)!, Path.GetFileName(_file!) + "*")) File.Delete(f);
        }
    }

    [Fact]
    public async Task PAX02c_TwoInstancesImportAtOnce_BothSucceed_RowsWrittenOnce()
    {
        await using var c = await SecondContext.CreateAsync();
        await PermissionAxesUpgradeData.InsertAsync(c.First);

        // 兩個實例同時啟動：各自在自己的 DbContext 執行帶入（Seed 許可，測試以 Test 許可代替，能力相同）
        await Task.WhenAll(
            Task.Run(() => TestPermits.RunForTestsAsync(() => PermissionAxesImport.RunAsync(c.First))),
            Task.Run(() => TestPermits.RunForTestsAsync(() => PermissionAxesImport.RunAsync(c.Second))));

        c.First.ChangeTracker.Clear();
        var (groups, users) = await PermissionAxesUpgradeData.PermissionAdminHoldersAsync(c.First);
        groups.Should().Equal(PermissionAxesUpgradeData.ImportedGroups);
        users.Should().Equal(PermissionAxesUpgradeData.ImportedUsers);
        (await c.First.Set<SystemSetting>().CountAsync(s => s.Key == SystemSetting.PermissionAxesImportedOn)).Should().Be(1);
    }
}
