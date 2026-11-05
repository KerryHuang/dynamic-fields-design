using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using DynamicModules.Persistence.Repositories.Dynamic;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Persistence;

public class PermissionRepositoryTests
{
    private static readonly DateTime Now = new(2026, 11, 3, 9, 0, 0);

    private static Module NewModule(string key, string code)
    {
        var block = new Block();
        block.StampCreated("admin", Now);
        var module = new Module { Key = key, ProgramCode = code, NameI18n = new I18nText([KeyValuePair.Create("zh-TW", key)]), Blocks = [block] };
        module.StampCreated("admin", Now);
        return module;
    }

    private static Field NewField(Module m)
    {
        var field = new Field { ModuleId = m.Id, BlockId = m.Blocks[0].Id, Key = "f_" + m.Key, Options = [new FieldOption { Active = true }, new FieldOption { Active = true }] };
        field.StampCreated("admin", Now);
        foreach (var o in field.Options) o.StampCreated("admin", Now);
        return field;
    }

    private static async Task<(Module A, Module B, PermissionGroup G1, PermissionGroup G2, PermissionGroup G3)> SeedAsync(DynamicDbContext db)
    {
        var a = NewModule("a", "DYN00001");
        var b = NewModule("b", "DYN00002");
        PermissionGroup Group(string k) => new() { Key = k, NameI18n = new I18nText([KeyValuePair.Create("zh-TW", k)]) };
        var g1 = Group("g1"); var g2 = Group("g2"); var g3 = Group("g3");
        db.Set<DynamicProgram>().AddRange(new DynamicProgram { ProgramCode = "DYN00001", Active = true },
            new DynamicProgram { ProgramCode = "DYN00002", Active = true });
        db.Set<AppUser>().AddRange(new AppUser { UserId = "me" }, new AppUser { UserId = "mate" }, new AppUser { UserId = "stranger" });
        db.Set<Module>().AddRange(a, b);
        db.Set<PermissionGroup>().AddRange(g1, g2, g3);
        await db.SaveChangesAsync();
        var fa = NewField(a); var fb = NewField(b);
        db.Set<Field>().AddRange(fa, fb);
        await db.SaveChangesAsync();

        db.Set<UserGroupMapping>().AddRange(
            new UserGroupMapping { GroupId = g1.Id, UserId = "me" }, new UserGroupMapping { GroupId = g1.Id, UserId = "mate" },
            new UserGroupMapping { GroupId = g3.Id, UserId = "stranger" });
        db.Set<GroupProgramPrivilege>().AddRange(
            new GroupProgramPrivilege { GroupId = g1.Id, ProgramCode = "DYN00001", CanRead = true },
            new GroupProgramPrivilege { GroupId = g1.Id, ProgramCode = "DYN00002", CanRead = true },
            new GroupProgramPrivilege { GroupId = g3.Id, ProgramCode = "DYN00001", CanRead = true });
        db.Set<UserProgramPrivilege>().AddRange(
            new UserProgramPrivilege { UserId = "me", ProgramCode = "DYN00001", CanEdit = true },
            new UserProgramPrivilege { UserId = "me", ProgramCode = "DYN00002", CanDelete = true });
        db.Set<ModuleDataScope>().AddRange(
            new ModuleDataScope { ModuleId = a.Id, GroupId = g1.Id, Scope = DataScope.Group },
            new ModuleDataScope { ModuleId = a.Id, GroupId = g3.Id, Scope = DataScope.Own },
            new ModuleDataScope { ModuleId = b.Id, GroupId = g1.Id, Scope = DataScope.Own });
        db.Set<FieldPermission>().AddRange(
            new FieldPermission { FieldId = fa.Id, GroupId = g1.Id, Access = FieldAccess.Hidden },
            new FieldPermission { FieldId = fa.Id, GroupId = g3.Id, Access = FieldAccess.ReadOnly },
            new FieldPermission { FieldId = fb.Id, GroupId = g1.Id, Access = FieldAccess.Hidden });
        db.Set<FieldOptionScope>().AddRange(
            new FieldOptionScope { OptionId = fa.Options[0].Id, GroupId = g3.Id },
            new FieldOptionScope { OptionId = fb.Options[0].Id, GroupId = g1.Id });
        await db.SaveChangesAsync();
        return (a, b, g1, g2, g3);
    }

    [Fact]
    public async Task LoadInputs_FiltersToModuleAndUserGroups()
    {
        await using var t = await SqliteTestDb.CreateAsync();
        var (a, _, g1, _, g3) = await SeedAsync(t.Context);

        var inputs = await new PermissionRepository(t.Context).LoadInputsAsync("me", a, CancellationToken.None);

        inputs.Should().NotBeNull();
        inputs!.GroupIds.Should().BeEquivalentTo([g1.Id]);
        inputs.GroupPrivileges.Should().ContainSingle(p => p.GroupId == g1.Id && p.ProgramCode == "DYN00001");
        inputs.UserPrivilege!.ProgramCode.Should().Be("DYN00001");
        inputs.Scopes.Should().ContainSingle().Which.Scope.Should().Be(DataScope.Group);
        inputs.FieldPermissions.Should().ContainSingle().Which.Access.Should().Be(FieldAccess.Hidden);
        inputs.OptionScopes.Should().ContainSingle().Which.GroupId.Should().Be(g3.Id);
        inputs.GroupMateUserIds.Should().BeEquivalentTo(["me", "mate"]);
    }

    [Fact]
    public async Task LoadInputs_UnknownUser_ReturnsNull()
    {
        await using var t = await SqliteTestDb.CreateAsync();
        var (a, _, _, _, _) = await SeedAsync(t.Context);

        (await new PermissionRepository(t.Context).LoadInputsAsync("nobody", a, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task LoadProgramInputs_LoadsOnlyThatProgram()
    {
        await using var t = await SqliteTestDb.CreateAsync();
        await SeedAsync(t.Context);
        var repo = new PermissionRepository(t.Context);

        var inputs = await repo.LoadProgramInputsAsync("me", "DYN00002", CancellationToken.None);

        inputs.Should().NotBeNull();
        inputs!.GroupPrivileges.Should().ContainSingle().Which.ProgramCode.Should().Be("DYN00002");
        inputs.UserPrivilege!.CanDelete.Should().BeTrue();
        inputs.Scopes.Should().BeEmpty();
        inputs.FieldPermissions.Should().BeEmpty();
        inputs.OptionScopes.Should().BeEmpty();
        inputs.GroupMateUserIds.Should().BeEquivalentTo(["me"]);
        (await repo.LoadProgramInputsAsync("nobody", "DYN00002", CancellationToken.None)).Should().BeNull();
    }
}
