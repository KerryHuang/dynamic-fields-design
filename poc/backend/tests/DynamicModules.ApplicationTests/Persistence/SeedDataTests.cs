using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Entities.Mock;
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
    public async Task FreshDatabase_HasOnlyAdmin()
    {
        await using var db = await SqliteTestDb.CreateAsync();

        await SeedData.EnsureSeededAsync(db.Context);

        var users = await db.Context.Set<AppUser>().AsNoTracking().ToListAsync();
        users.Should().ContainSingle().Which.UserId.Should().Be("admin");
        users[0].IsSystemAdmin.Should().BeTrue();
        (await db.Context.Set<PermissionGroup>().CountAsync()).Should().Be(0);
        (await db.Context.Set<MockMold>().CountAsync()).Should().Be(0);
        (await db.Context.Set<MockCustomer>().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Reseed_DoesNotRestoreRemovedMembers()
    {
        await using var db = await SqliteTestDb.CreateAsync();
        await SeedData.EnsureSeededAsync(db.Context);
        await DemoPeople.EnsureAsync(db.Context);
        await db.Context.Set<UserGroupMapping>().Where(m => m.UserId == "sales01").ExecuteDeleteAsync();

        await SeedData.EnsureSeededAsync(db.Context);

        (await db.Context.Set<UserGroupMapping>().AnyAsync(m => m.UserId == "sales01")).Should().BeFalse();
    }

    [Fact]
    public async Task Reseed_DoesNotForceAdminFlag()
    {
        await using var db = await SqliteTestDb.CreateAsync();
        await SeedData.EnsureSeededAsync(db.Context);
        db.Context.Set<AppUser>().Add(new AppUser { UserId = "boss", DisplayName = "boss", IsSystemAdmin = true });
        (await db.Context.Set<AppUser>().SingleAsync(u => u.UserId == "admin")).IsSystemAdmin = false;
        await db.Context.SaveChangesAsync();

        await SeedData.EnsureSeededAsync(db.Context);

        (await db.Context.Set<AppUser>().AsNoTracking().SingleAsync(u => u.UserId == "admin")).IsSystemAdmin.Should().BeFalse();
    }

    [Fact]
    public async Task DemoPeople_CreatesGroupsMembersMoldsCustomers()
    {
        await using var db = await SqliteTestDb.CreateAsync();
        await SeedData.EnsureSeededAsync(db.Context);

        await DemoPeople.EnsureAsync(db.Context);
        await DemoPeople.EnsureAsync(db.Context);

        var groups = await db.Context.Set<PermissionGroup>().AsNoTracking().ToListAsync();
        groups.Select(g => g.Key).Should().BeEquivalentTo("mis", "sales", "sales-manager", "floor", "maint", "qa", "planner", "purchasing", "warehouse", "ops-manager");
        var members = await db.Context.Set<UserGroupMapping>().AsNoTracking().ToListAsync();
        string[] Of(string key) => members.Where(m => m.GroupId == groups.Single(g => g.Key == key).Id)
            .Select(m => m.UserId).Order().ToArray();
        Of("mis").Should().Equal("admin");
        Of("sales").Should().Equal("sales01", "sales02");
        Of("floor").Should().Equal("floor01", "floor02");
        Of("planner").Should().Equal("planner01");
        Of("purchasing").Should().Equal("buyer01");
        Of("warehouse").Should().Equal("keeper01");
        Of("ops-manager").Should().Equal("ops01");
        (await db.Context.Set<AppUser>().CountAsync()).Should().Be(12);
        (await db.Context.Set<AppUser>().Where(u => u.UserId != "admin").AllAsync(u => u.IsDemoAccount && u.PasswordHash == null))
            .Should().BeTrue();
        (await db.Context.Set<MockMold>().Select(m => m.MoldNo).ToListAsync())
            .Should().BeEquivalentTo(Enumerable.Range(1, 12).Select(i => $"M-{i:D4}"));
        (await db.Context.Set<MockCustomer>().Select(c => c.CustomerNo).ToListAsync())
            .Should().BeEquivalentTo(Enumerable.Range(1, 8).Select(i => $"C-{i:D3}"));
    }

    [Fact]
    public async Task DemoPeople_DoesNotOverwriteExisting()
    {
        await using var db = await SqliteTestDb.CreateAsync();
        db.Context.Set<MockMold>().Add(new MockMold { MoldNo = "M-0006", Name = "自訂名稱" });
        db.Context.Set<AppUser>().Add(new AppUser { UserId = "qa01", DisplayName = "自訂" });
        await db.Context.SaveChangesAsync();

        await DemoPeople.EnsureAsync(db.Context);

        (await db.Context.Set<MockMold>().AsNoTracking().SingleAsync(m => m.MoldNo == "M-0006")).Name.Should().Be("自訂名稱");
        (await db.Context.Set<AppUser>().AsNoTracking().SingleAsync(u => u.UserId == "qa01")).DisplayName.Should().Be("自訂");
    }

    [Fact]
    public async Task Seed_IsIdempotent()
    {
        await using var db = await SqliteTestDb.CreateAsync();
        await AddModuleAsync(db.Context, "order", "DYN00001", ModuleStatus.Active);

        await SeedData.EnsureSeededAsync(db.Context);
        await SeedData.EnsureSeededAsync(db.Context);

        (await db.Context.Set<AppUser>().CountAsync()).Should().Be(1);
        (await db.Context.Set<GroupProgramPrivilege>().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Seed_ReassignsUnknownOwnersToAdmin()
    {
        await using var db = await SqliteTestDb.CreateAsync();
        var module = await AddModuleAsync(db.Context, "order", "DYN00001", ModuleStatus.Active);
        await DemoPeople.EnsureAsync(db.Context);
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
