using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using DynamicModules.Persistence.Seed;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Record = DynamicModules.Domain.Entities.Dynamic.Record;

namespace DynamicModules.ApplicationTests.Persistence;

public class SeedDataTests
{
    private static async Task<Module> AddModuleAsync(DynamicDbContext db, string key, string code, ModuleStatus status)
    {
        var now = new DateTime(2026, 11, 3, 9, 0, 0);
        db.Set<DynamicProgram>().Add(new DynamicProgram { ProgramCode = code, Active = true });
        var module = new Module
        {
            Key = key, ProgramCode = code, Status = status,
            NameI18n = new I18nText([KeyValuePair.Create("zh-TW", key)]), Blocks = [new Block()]
        };
        module.StampCreated("admin", now);
        db.Set<Module>().Add(module);
        await db.SaveChangesAsync();
        return module;
    }

    [Fact]
    public async Task Seed_CreatesGroupsAndMembers()
    {
        await using var db = await SqliteTestDb.CreateAsync();

        await SeedData.EnsureSeededAsync(db.Context);

        var groups = await db.Context.Set<PermissionGroup>().ToListAsync();
        groups.Select(g => g.Key).Should().BeEquivalentTo("mis", "sales", "sales-manager", "floor");
        groups.Single(g => g.Key == "mis").NameI18n.Values["zh-TW"].Should().Be("MIS");
        var members = await db.Context.Set<UserGroupMapping>().ToListAsync();
        string[] Of(string key) => members.Where(m => m.GroupId == groups.Single(g => g.Key == key).Id)
            .Select(m => m.UserId).Order().ToArray();
        Of("mis").Should().Equal("admin");
        Of("sales").Should().Equal("sales01", "sales02");
        Of("sales-manager").Should().Equal("manager01");
        Of("floor").Should().Equal("floor01");
        (await db.Context.Set<AppUser>().SingleAsync(u => u.UserId == "admin")).IsSystemAdmin.Should().BeTrue();
        (await db.Context.Set<AppUser>().CountAsync(u => u.IsSystemAdmin)).Should().Be(1);
    }

    [Fact]
    public async Task Seed_IsIdempotent()
    {
        await using var db = await SqliteTestDb.CreateAsync();
        await AddModuleAsync(db.Context, "order", "DYN00001", ModuleStatus.Active);

        await SeedData.EnsureSeededAsync(db.Context);
        await SeedData.EnsureSeededAsync(db.Context);

        (await db.Context.Set<PermissionGroup>().CountAsync()).Should().Be(4);
        (await db.Context.Set<UserGroupMapping>().CountAsync()).Should().Be(5);
        (await db.Context.Set<GroupProgramPrivilege>().CountAsync()).Should().Be(3);
    }

    [Fact]
    public async Task Seed_FirstUpgrade_GrantsExistingModules()
    {
        await using var db = await SqliteTestDb.CreateAsync();
        await AddModuleAsync(db.Context, "order", "DYN00001", ModuleStatus.Active);

        await SeedData.EnsureSeededAsync(db.Context);

        var keys = await db.Context.Set<PermissionGroup>().ToDictionaryAsync(g => g.Id, g => g.Key);
        var rows = await db.Context.Set<GroupProgramPrivilege>().ToListAsync();
        rows.Select(r => keys[r.GroupId]).Should().BeEquivalentTo("sales", "sales-manager", "floor");
        rows.Should().OnlyContain(r => r.ProgramCode == "DYN00001" && r.CanRead && r.CanAdd && r.CanEdit
            && r.CanDelete && r.CanPrint && r.CanAmt && !r.CanApprove);

        await AddModuleAsync(db.Context, "contract", "DYN00002", ModuleStatus.Active);
        await SeedData.EnsureSeededAsync(db.Context);

        (await db.Context.Set<GroupProgramPrivilege>().CountAsync(r => r.ProgramCode == "DYN00002")).Should().Be(0);
    }

    [Fact]
    public async Task Seed_FreshDbThenModulesCreated_RestartDoesNotGrant()
    {
        await using var db = await SqliteTestDb.CreateAsync();
        await SeedData.EnsureSeededAsync(db.Context);
        await AddModuleAsync(db.Context, "order", "DYN00001", ModuleStatus.Active);

        await SeedData.EnsureSeededAsync(db.Context);

        (await db.Context.Set<GroupProgramPrivilege>().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Seed_GrantsClearedLater_NotRegranted()
    {
        await using var db = await SqliteTestDb.CreateAsync();
        await AddModuleAsync(db.Context, "order", "DYN00001", ModuleStatus.Active);
        await SeedData.EnsureSeededAsync(db.Context);
        (await db.Context.Set<GroupProgramPrivilege>().CountAsync()).Should().Be(3);
        db.Context.Set<GroupProgramPrivilege>().RemoveRange(db.Context.Set<GroupProgramPrivilege>());
        await db.Context.SaveChangesAsync();

        await SeedData.EnsureSeededAsync(db.Context);

        (await db.Context.Set<GroupProgramPrivilege>().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Seed_ReassignsUnknownOwnersToAdmin()
    {
        await using var db = await SqliteTestDb.CreateAsync();
        var module = await AddModuleAsync(db.Context, "order", "DYN00001", ModuleStatus.Active);
        db.Context.Set<Record>().AddRange(
            NewRecord(module.Id, "anonymous"), NewRecord(module.Id, "sales01"));
        await db.Context.SaveChangesAsync();

        await SeedData.EnsureSeededAsync(db.Context);

        var owners = await db.Context.Set<Record>().AsNoTracking().Select(r => r.OwnerUserId).ToListAsync();
        owners.Should().BeEquivalentTo("admin", "sales01");
    }

    [Fact]
    public async Task Seed_ReassignsAnonymousPersonalViewsToAdmin_SharedUntouched()
    {
        await using var db = await SqliteTestDb.CreateAsync();
        var module = await AddModuleAsync(db.Context, "order", "DYN00001", ModuleStatus.Active);
        db.Context.Set<ListView>().AddRange(
            NewView(module.Id, "anonymous"), NewView(module.Id, "sales01"), NewView(module.Id, null));
        await db.Context.SaveChangesAsync();

        await SeedData.EnsureSeededAsync(db.Context);

        var owners = await db.Context.Set<ListView>().AsNoTracking().Select(v => v.OwnerUserId).ToListAsync();
        owners.Should().BeEquivalentTo(new string?[] { "admin", "sales01", null });
    }

    private static ListView NewView(long moduleId, string? owner)
    {
        var v = new ListView { ModuleId = moduleId, OwnerUserId = owner };
        v.StampCreated("admin", new DateTime(2026, 11, 3, 9, 0, 0));
        return v;
    }

    private static Record NewRecord(long moduleId, string owner)
    {
        var r = new Record { ModuleId = moduleId, OwnerUserId = owner };
        r.StampCreated(owner, new DateTime(2026, 11, 3, 9, 0, 0));
        return r;
    }

    [Fact]
    public async Task Seed_SyncsProgramActiveWithModuleStatus()
    {
        await using var db = await SqliteTestDb.CreateAsync();
        await AddModuleAsync(db.Context, "a", "DYN00001", ModuleStatus.Active);
        await AddModuleAsync(db.Context, "b", "DYN00002", ModuleStatus.Draft);
        await AddModuleAsync(db.Context, "c", "DYN00003", ModuleStatus.Inactive);

        await SeedData.EnsureSeededAsync(db.Context);

        var active = await db.Context.Set<DynamicProgram>().AsNoTracking().ToDictionaryAsync(p => p.ProgramCode, p => p.Active);
        active["DYN00001"].Should().BeTrue();
        active["DYN00002"].Should().BeFalse();
        active["DYN00003"].Should().BeFalse();
        active["DYN00000"].Should().BeTrue();
    }
}
