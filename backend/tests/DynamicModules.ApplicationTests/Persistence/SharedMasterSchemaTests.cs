using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace DynamicModules.ApplicationTests.Persistence;

/// <summary>migration SharedMasters（共用主檔設計 §8）：新表的鍵、篩選式唯一索引、檢查約束與 cascade。</summary>
public class SharedMasterSchemaTests
{
    [Fact]
    public async Task Migrate_CreatesSharedMasterTablesWithKeys()
    {
        await using var db = await SqliteTestDb.CreateAsync();
        var expected = new Dictionary<string, string[]>
        {
            ["Dynamic_AppMasterUse"] = ["AppId", "MasterAppId"],
            ["Dynamic_AppGrant"] = ["Id"],
            ["Dynamic_ModuleActionScope"] = ["ModuleId", "GroupId", "Action"],
            ["Dynamic_FieldGrant"] = ["Id"],
            ["Dynamic_AppOrphan"] = ["Id"],
        };
        foreach (var (table, keys) in expected)
        {
            var pk = await db.Context.Database
                .SqlQuery<string>($"SELECT name AS Value FROM pragma_table_info({table}) WHERE pk > 0 ORDER BY pk")
                .ToListAsync();
            pk.Should().Equal(keys, table);
        }
        (await Columns(db, "Dynamic_App")).Should().Contain("Kind");
        (await Columns(db, "Dynamic_Field")).Should().Contain("VisibilityRestricted");
    }

    [Fact]
    public async Task ExistingApps_GetKindApp_ExistingFieldsAreNotRestricted()
    {
        await using var db = await SqliteTestDb.CreateAsync(migrate: false);
        var migrator = db.Context.GetService<IMigrator>();
        await migrator.MigrateAsync("RemoveMockMold");
        await db.Context.Database.ExecuteSqlRawAsync(
            "INSERT INTO Dynamic_App (Key, NameI18n, PackageVersion, FormatVersion, Source, Status, Requires, RowVersion, CreatedUserId, CreatedTime) " +
            "VALUES ('old-app', '{{\"zh-TW\":\"舊\"}}', '1.0.0', 5, 'builtIn', 'Open', '{{}}', 'x', 'u', '2026-01-01')");
        await migrator.MigrateAsync();

        (await db.Context.Set<App>().AsNoTracking().SingleAsync(a => a.Key == "old-app")).Kind.Should().Be(AppKind.App);
        (await db.Context.Database
            .SqlQueryRaw<string>("SELECT dflt_value AS Value FROM pragma_table_info('Dynamic_Field') WHERE name = 'VisibilityRestricted'")
            .SingleAsync()).Should().Be("0");
    }

    [Fact]
    public async Task Orphan_FieldAndOptionWithSameName_BothFit_DuplicateKindAndNameRejected()
    {
        await using var db = await SqliteTestDb.CreateAsync();
        var module = await AddModuleAsync(db);
        var field = module.Blocks[0].Fields[0];
        var option = field.Options[0];
        var orphans = db.Context.Set<AppOrphan>();

        orphans.Add(new AppOrphan { FormerAppKey = "crm", Kind = OrphanKind.Field, Name = "partner/partner.vip", FieldId = field.Id });
        orphans.Add(new AppOrphan { FormerAppKey = "crm", Kind = OrphanKind.Option, Name = "partner/partner.vip", OptionId = option.Id });
        orphans.Add(new AppOrphan { FormerAppKey = "crm", Kind = OrphanKind.Block, Name = "partner/partner.vip", BlockId = module.Blocks[0].Id });
        await db.Context.SaveChangesAsync();
        (await orphans.CountAsync()).Should().Be(3);

        db.Context.ChangeTracker.Clear();
        orphans.Add(new AppOrphan { FormerAppKey = "crm", Kind = OrphanKind.Field, Name = "partner/partner.vip", FieldId = field.Id });
        await FluentActions.Awaiting(() => db.Context.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>();
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    public async Task Orphan_MustPointAtExactlyOneThing(bool withField, bool withOption, bool withBlock)
    {
        await using var db = await SqliteTestDb.CreateAsync();
        var module = await AddModuleAsync(db);
        var field = module.Blocks[0].Fields[0];
        db.Context.Set<AppOrphan>().Add(new AppOrphan
        {
            FormerAppKey = "crm", Kind = OrphanKind.Field, Name = "partner/partner.x",
            FieldId = withField ? field.Id : null, OptionId = withOption ? field.Options[0].Id : null,
            BlockId = withBlock ? module.Blocks[0].Id : null
        });
        await FluentActions.Awaiting(() => db.Context.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task AppGrant_ManualHasNoApp_AppSourcesNeedOne_EachSourceUniquePerAction()
    {
        await using var db = await SqliteTestDb.CreateAsync();
        var module = await AddModuleAsync(db);
        var app = await AddAppAsync(db, "crm");
        var group = await AddGroupAsync(db, "sales");
        var grants = db.Context.Set<AppGrant>();
        AppGrant Grant(GrantSource source, long? appId) => new()
        {
            SourceKind = source, AppId = appId, ModuleId = module.Id, GroupId = group.Id, Action = PermissionAction.Edit, Scope = DataScope.Own
        };

        grants.AddRange(Grant(GrantSource.App, app.Id), Grant(GrantSource.Manual, null));
        await db.Context.SaveChangesAsync();

        foreach (var bad in new[] { Grant(GrantSource.Manual, null), Grant(GrantSource.App, app.Id), Grant(GrantSource.Manual, app.Id), Grant(GrantSource.App, null) })
        {
            db.Context.ChangeTracker.Clear();
            grants.Add(bad);
            await FluentActions.Awaiting(() => db.Context.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>($"{bad.SourceKind}/{bad.AppId}");
        }
    }

    [Fact]
    public async Task FieldGrant_ManualHasNoApp_UniquePerSource()
    {
        await using var db = await SqliteTestDb.CreateAsync();
        var module = await AddModuleAsync(db);
        var field = module.Blocks[0].Fields[0];
        var app = await AddAppAsync(db, "crm");
        var group = await AddGroupAsync(db, "sales");
        var grants = db.Context.Set<FieldGrant>();
        FieldGrant Grant(GrantSource source, long? appId) => new() { SourceKind = source, AppId = appId, FieldId = field.Id, GroupId = group.Id };

        grants.AddRange(Grant(GrantSource.App, app.Id), Grant(GrantSource.Manual, null));
        await db.Context.SaveChangesAsync();

        foreach (var bad in new[] { Grant(GrantSource.Manual, null), Grant(GrantSource.App, app.Id), Grant(GrantSource.Manual, app.Id) })
        {
            db.Context.ChangeTracker.Clear();
            grants.Add(bad);
            await FluentActions.Awaiting(() => db.Context.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>($"{bad.SourceKind}/{bad.AppId}");
        }
    }

    [Fact]
    public async Task DeletingGroup_CascadesGrantsAndActionScopes()
    {
        await using var db = await SqliteTestDb.CreateAsync();
        var module = await AddModuleAsync(db);
        var app = await AddAppAsync(db, "crm");
        var group = await AddGroupAsync(db, "sales");
        db.Context.Set<AppGrant>().Add(new AppGrant
        {
            SourceKind = GrantSource.App, AppId = app.Id, ModuleId = module.Id, GroupId = group.Id, Action = PermissionAction.Read, Scope = DataScope.All
        });
        db.Context.Set<ModuleActionScope>().Add(new ModuleActionScope { ModuleId = module.Id, GroupId = group.Id, Action = PermissionAction.Read, Scope = DataScope.All });
        db.Context.Set<FieldGrant>().Add(new FieldGrant { SourceKind = GrantSource.App, AppId = app.Id, FieldId = module.Blocks[0].Fields[0].Id, GroupId = group.Id });
        await db.Context.SaveChangesAsync();

        await db.Context.Database.ExecuteSqlRawAsync("DELETE FROM Dynamic_PermissionGroup WHERE Id = {0}", group.Id);

        (await db.Context.Set<AppGrant>().CountAsync()).Should().Be(0);
        (await db.Context.Set<ModuleActionScope>().CountAsync()).Should().Be(0);
        (await db.Context.Set<FieldGrant>().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task MasterUse_DeletingUserCascades_DeletingUsedMasterIsRestricted()
    {
        await using var db = await SqliteTestDb.CreateAsync();
        var user = await AddAppAsync(db, "crm");
        var master = await AddAppAsync(db, "partner", AppKind.Master);
        db.Context.Set<AppMasterUse>().Add(new AppMasterUse { AppId = user.Id, MasterAppId = master.Id });
        await db.Context.SaveChangesAsync();

        await FluentActions.Awaiting(() => db.Context.Database.ExecuteSqlRawAsync("DELETE FROM Dynamic_App WHERE Id = {0}", master.Id))
            .Should().ThrowAsync<Exception>();
        await db.Context.Database.ExecuteSqlRawAsync("DELETE FROM Dynamic_App WHERE Id = {0}", user.Id);
        (await db.Context.Set<AppMasterUse>().CountAsync()).Should().Be(0);
        (await db.Context.Set<App>().AsNoTracking().SingleAsync(a => a.Key == "partner")).Kind.Should().Be(AppKind.Master);
    }

    private static async Task<List<string>> Columns(SqliteTestDb db, string table) =>
        await db.Context.Database.SqlQuery<string>($"SELECT name AS Value FROM pragma_table_info({table})").ToListAsync();

    private static I18nText Text(string zh) => new([KeyValuePair.Create("zh-TW", zh)]);

    private static async Task<Module> AddModuleAsync(SqliteTestDb db)
    {
        db.Context.Set<DynamicProgram>().Add(new DynamicProgram { ProgramCode = "DYN00001" });
        var module = new Module { Key = "partner", ProgramCode = "DYN00001", NameI18n = Text("往來對象"), Blocks = [new Block()] };
        db.Context.Set<Module>().Add(module);
        await db.Context.SaveChangesAsync();
        var field = new Field
        {
            ModuleId = module.Id, BlockId = module.Blocks[0].Id, Key = "f_1", Type = FieldType.Enum, LabelI18n = Text("等級"),
            Options = [new FieldOption { LabelI18n = Text("VIP"), Active = true }]
        };
        module.Blocks[0].Fields.Add(field);
        await db.Context.SaveChangesAsync();
        return module;
    }

    private static async Task<App> AddAppAsync(SqliteTestDb db, string key, AppKind kind = AppKind.App)
    {
        var app = new App { Key = key, Kind = kind, NameI18n = Text(key), PackageVersion = "1.0.0", FormatVersion = 6, Source = "builtIn", RowVersion = "x" };
        db.Context.Set<App>().Add(app);
        await db.Context.SaveChangesAsync();
        return app;
    }

    private static async Task<PermissionGroup> AddGroupAsync(SqliteTestDb db, string key)
    {
        var group = new PermissionGroup { Key = key, NameI18n = Text(key) };
        db.Context.Set<PermissionGroup>().Add(group);
        await db.Context.SaveChangesAsync();
        return group;
    }
}
