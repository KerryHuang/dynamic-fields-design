using DynamicModules.Application.Common.Security;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Common;

public class PermissionResolverTests
{
    private const string Code = "DYN00007";

    private static Module MakeModule(params Field[] fields) => new()
    {
        Id = 1, ProgramCode = Code, Blocks = [new Block { Fields = [.. fields] }]
    };

    private static GroupProgramPrivilege G(long group, Action<GroupProgramPrivilege>? set = null)
    {
        var p = new GroupProgramPrivilege { GroupId = group, ProgramCode = Code };
        set?.Invoke(p);
        return p;
    }

    private static PermissionInputs Inputs(
        long[]? groups = null, GroupProgramPrivilege[]? gp = null, UserProgramPrivilege? up = null,
        ModuleDataScope[]? scopes = null, FieldPermission[]? fp = null, FieldOptionScope[]? os = null,
        string[]? mates = null, bool admin = false, ModuleActionScope[]? acts = null, FieldGrant[]? fg = null) =>
        new(new AppUser { UserId = "me", IsSystemAdmin = admin }, groups ?? [], gp ?? [], up,
            scopes ?? [], fp ?? [], os ?? [], new HashSet<string>(mates ?? ["me"]), acts ?? [], fg ?? []);

    private static ModuleActionScope A(long group, PermissionAction action, DataScope scope) =>
        new() { ModuleId = 1, GroupId = group, Action = action, Scope = scope };

    // ---- 共用主檔：逐動作範圍（設計 §6）----

    [Fact]
    public void Resolve_WithActionScopes_EachActionTakesTheWidestScopeOfTheGroupsGrantingIt()
    {
        var module = MakeModule();
        var inputs = Inputs([1, 2],
            [G(1, p => p.CanRead = true), G(2, p => { p.CanRead = true; p.CanEdit = true; })],
            scopes: [S(1, DataScope.All), S(2, DataScope.Own)],
            mates: ["me", "mate"],
            acts: [A(1, PermissionAction.Read, DataScope.All), A(2, PermissionAction.Read, DataScope.Own), A(2, PermissionAction.Edit, DataScope.Own)]);

        var read = PermissionResolver.Resolve(module, inputs, forAction: PermissionAction.Read);
        var edit = PermissionResolver.Resolve(module, inputs, forAction: PermissionAction.Edit);

        read.Scope.Should().Be(DataScope.All);
        read.OwnerUserIds.Should().BeNull();
        edit.Scope.Should().Be(DataScope.Own, "沒有任何來源授予過「編輯 all」");
        edit.OwnerUserIds.Should().BeEquivalentTo(["me"]);
        edit.Actions.Should().BeEquivalentTo(read.Actions);
        PermissionResolver.Resolve(module, inputs).Scope.Should().Be(DataScope.All, "沒指定動作時用讀取的範圍");
    }

    [Fact]
    public void Resolve_WithoutActionScopes_EveryActionUsesTheReadScopeAsBefore()
    {
        var module = MakeModule();
        var inputs = Inputs([1, 2],
            [G(1, p => p.CanRead = true), G(2, p => { p.CanRead = true; p.CanEdit = true; })],
            scopes: [S(1, DataScope.All), S(2, DataScope.Own)]);

        PermissionResolver.Resolve(module, inputs, forAction: PermissionAction.Edit).Scope.Should().Be(DataScope.All);
        PermissionResolver.Resolve(module, inputs, forAction: PermissionAction.Edit).Hash
            .Should().Be(PermissionResolver.Resolve(module, inputs).Hash);
    }

    // ---- 共用主檔：貢獻欄位限定可見（設計 §6）----

    [Fact]
    public void Resolve_RestrictedField_HiddenUnlessAGroupOfTheUserIsGranted_AndHiddenStillWins()
    {
        var plain = new Field { Id = 1, Type = FieldType.Text, Status = FieldStatus.Active };
        var restricted = new Field { Id = 2, Type = FieldType.Text, Status = FieldStatus.Active, VisibilityRestricted = true };
        var formula = Formula(3, "{2} + 1");
        var module = MakeModule(plain, restricted, formula);
        var read = new[] { G(1, p => p.CanRead = true), G(2, p => p.CanRead = true) };

        PermissionResolver.Resolve(module, Inputs([1], read)).HiddenFieldIds.Should().BeEquivalentTo([2L, 3L], "沒有可見列；引用它的公式也隱藏");
        PermissionResolver.Resolve(module, Inputs([1], read, fg: [new FieldGrant { FieldId = 2, GroupId = 2 }]))
            .HiddenFieldIds.Should().BeEquivalentTo([2L, 3L], "授予的是別的群組");
        PermissionResolver.Resolve(module, Inputs([1], read, fg: [new FieldGrant { FieldId = 2, GroupId = 1 }]))
            .HiddenFieldIds.Should().BeEmpty();
        PermissionResolver.Resolve(module, Inputs([1], read, fp: [new FieldPermission { GroupId = 1, FieldId = 2, Access = FieldAccess.Hidden }],
                fg: [new FieldGrant { FieldId = 2, GroupId = 1 }]))
            .HiddenFieldIds.Should().Contain(2L, "收緊優先：可見授予不解除 Hidden");
        PermissionResolver.Resolve(module, Inputs([1], read, admin: true)).HiddenFieldIds.Should().BeEmpty("系統管理員看得到");
    }


    private static ModuleDataScope S(long group, DataScope scope) =>
        new() { ModuleId = 1, GroupId = group, Scope = scope };

    private static Field Formula(long id, string expr, string resultType = "number") =>
        new() { Id = id, Type = FieldType.Formula, Status = FieldStatus.Active, Config = new FieldConfig { Formula = expr, ResultType = resultType } };

    [Fact]
    public void Resolve_FormulaWithHiddenInput_Hidden()
    {
        var module = MakeModule(
            new Field { Id = 1, Type = FieldType.Integer, Status = FieldStatus.Active },
            Formula(2, "{1}*2"), Formula(3, "{2}+1"), Formula(4, "1+1"));

        var r = PermissionResolver.Resolve(module, Inputs([1],
            [G(1, p => { p.CanRead = true; p.CanAmt = true; })],
            fp: [new FieldPermission { GroupId = 1, FieldId = 1, Access = FieldAccess.Hidden }]));

        r.HiddenFieldIds.Should().BeEquivalentTo([1L, 2L, 3L]);
    }

    [Fact]
    public void Resolve_FormulaWithCurrencyInput_NoPricing_Hidden()
    {
        var module = MakeModule(
            new Field { Id = 1, Type = FieldType.Currency, Status = FieldStatus.Active },
            Formula(2, "{1}*2"));

        var r = PermissionResolver.Resolve(module, Inputs([1], [G(1, p => p.CanRead = true)]));

        r.HiddenFieldIds.Should().BeEquivalentTo([1L, 2L]);
    }

    [Fact]
    public void Resolve_CurrencyResultFormula_NoPricing_Hidden()
    {
        var module = MakeModule(
            new Field { Id = 1, Type = FieldType.Integer, Status = FieldStatus.Active },
            Formula(2, "{1}*2", "currency"), Formula(3, "{1}*2"));

        var noPricing = PermissionResolver.Resolve(module, Inputs([1], [G(1, p => p.CanRead = true)]));
        var pricing = PermissionResolver.Resolve(module, Inputs([1], [G(1, p => { p.CanRead = true; p.CanAmt = true; })]));

        noPricing.HiddenFieldIds.Should().BeEquivalentTo([2L]);
        pricing.HiddenFieldIds.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_ActionsAreOrOfGroupsAndPersonal()
    {
        var r = PermissionResolver.Resolve(MakeModule(), Inputs([1, 2],
            [G(1, p => p.CanRead = true), G(2, p => p.CanAdd = true)],
            new UserProgramPrivilege { UserId = "me", ProgramCode = Code, CanDelete = true }));

        r.Actions.Should().BeEquivalentTo([PermissionAction.Read, PermissionAction.Add, PermissionAction.Delete]);
        r.Can(PermissionAction.Edit).Should().BeFalse();
    }

    [Fact]
    public void Resolve_ScopeIsWidestAmongReadGroups()
    {
        var r = PermissionResolver.Resolve(MakeModule(), Inputs([1, 2],
            [G(1, p => p.CanRead = true), G(2, p => p.CanRead = true)],
            scopes: [S(1, DataScope.Own), S(2, DataScope.All)]));

        r.Scope.Should().Be(DataScope.All);
        r.OwnerUserIds.Should().BeNull();
    }

    [Fact]
    public void Resolve_ScopeIgnoresGroupsWithoutRead()
    {
        var r = PermissionResolver.Resolve(MakeModule(), Inputs([1, 2],
            [G(1, p => p.CanRead = true), G(2, p => p.CanAdd = true)],
            scopes: [S(1, DataScope.Own), S(2, DataScope.All)]));

        r.Scope.Should().Be(DataScope.Own);
        r.OwnerUserIds.Should().BeEquivalentTo(["me"]);
    }

    [Fact]
    public void Resolve_MissingScopeRowMeansAll()
    {
        var r = PermissionResolver.Resolve(MakeModule(), Inputs([1], [G(1, p => p.CanRead = true)]));

        r.Scope.Should().Be(DataScope.All);
        r.OwnerUserIds.Should().BeNull();
    }

    [Fact]
    public void Resolve_PersonalReadOnly_ScopeIsOwn()
    {
        var r = PermissionResolver.Resolve(MakeModule(), Inputs([1], [G(1)],
            new UserProgramPrivilege { UserId = "me", ProgramCode = Code, CanRead = true }));

        r.Scope.Should().Be(DataScope.Own);
        r.OwnerUserIds.Should().BeEquivalentTo(["me"]);
    }

    [Fact]
    public void Resolve_GroupScope_OwnersAreGroupMates()
    {
        var r = PermissionResolver.Resolve(MakeModule(), Inputs([1], [G(1, p => p.CanRead = true)],
            scopes: [S(1, DataScope.Group)], mates: ["me", "u2"]));

        r.Scope.Should().Be(DataScope.Group);
        r.OwnerUserIds.Should().BeEquivalentTo(["me", "u2"]);
    }

    [Fact]
    public void Resolve_FieldHiddenWinsOverNothing()
    {
        var r = PermissionResolver.Resolve(MakeModule(), Inputs([1, 2], [G(1), G(2)],
            fp: [new FieldPermission { FieldId = 5, GroupId = 1, Access = FieldAccess.Hidden }]));

        r.HiddenFieldIds.Should().BeEquivalentTo([5L]);
    }

    [Fact]
    public void Resolve_HiddenWinsOverReadOnly()
    {
        var r = PermissionResolver.Resolve(MakeModule(), Inputs([1, 2], [G(1), G(2)],
            fp:
            [
                new FieldPermission { FieldId = 5, GroupId = 1, Access = FieldAccess.ReadOnly },
                new FieldPermission { FieldId = 5, GroupId = 2, Access = FieldAccess.Hidden }
            ]));

        r.HiddenFieldIds.Should().Contain(5);
        r.ReadOnlyFieldIds.Should().NotContain(5);
        r.CanWrite(new Field { Id = 5 }).Should().BeFalse();
    }

    [Fact]
    public void Resolve_NoPricing_HidesCurrencyFields()
    {
        var module = MakeModule(new Field { Id = 7, Type = FieldType.Currency }, new Field { Id = 8, Type = FieldType.Text });

        var without = PermissionResolver.Resolve(module, Inputs([1], [G(1, p => p.CanRead = true)]));
        var with = PermissionResolver.Resolve(module, Inputs([1], [G(1, p => { p.CanRead = true; p.CanAmt = true; })]));

        without.HiddenFieldIds.Should().BeEquivalentTo([7L]);
        with.HiddenFieldIds.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_OptionScopedToOtherGroup_IsUnselectable()
    {
        var r = PermissionResolver.Resolve(MakeModule(), Inputs([1], [G(1)],
            os: [new FieldOptionScope { OptionId = 9, GroupId = 3 }]));

        r.UnselectableOptionIds.Should().BeEquivalentTo([9L]);
        r.UnselectableOptionIds.Should().NotContain(10);
    }

    [Fact]
    public void Resolve_OptionScopedToMyGroup_IsSelectable()
    {
        var r = PermissionResolver.Resolve(MakeModule(), Inputs([1, 3], [G(1)],
            os: [new FieldOptionScope { OptionId = 9, GroupId = 3 }]));

        r.UnselectableOptionIds.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_SystemAdmin_BypassesEverything()
    {
        var module = MakeModule(new Field { Id = 7, Type = FieldType.Currency });
        var r = PermissionResolver.Resolve(module, Inputs(admin: true,
            fp: [new FieldPermission { FieldId = 7, GroupId = 1, Access = FieldAccess.Hidden }]));

        r.Actions.Should().HaveCount(7);
        r.Scope.Should().Be(DataScope.All);
        r.OwnerUserIds.Should().BeNull();
        r.HiddenFieldIds.Should().BeEmpty();
        r.ReadOnlyFieldIds.Should().BeEmpty();
        r.UnselectableOptionIds.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_HashChangesWhenPermissionsChange()
    {
        PermissionInputs With(bool add) => Inputs([1], [G(1, p => { p.CanRead = true; p.CanAdd = add; })]);

        var a = PermissionResolver.Resolve(MakeModule(), With(true));
        var same = PermissionResolver.Resolve(MakeModule(), With(true));
        var fewer = PermissionResolver.Resolve(MakeModule(), With(false));

        a.Hash.Should().MatchRegex("^[0-9a-f]{8}$");
        same.Hash.Should().Be(a.Hash);
        fewer.Hash.Should().NotBe(a.Hash);
    }

    [Fact]
    public void ResolveProgram_SystemAdmin_GetsAllSeven()
    {
        PermissionResolver.ResolveProgram("DYN00000", Inputs(admin: true)).Should().HaveCount(7);
    }

    [Fact]
    public void ResolveProgram_IsOrOfGroupsAndPersonalForThatProgram()
    {
        var gp = new GroupProgramPrivilege { GroupId = 1, ProgramCode = "DYN00000", CanRead = true };
        var other = new GroupProgramPrivilege { GroupId = 1, ProgramCode = Code, CanDelete = true };
        var up = new UserProgramPrivilege { UserId = "me", ProgramCode = "DYN00000", CanEdit = true };

        var actions = PermissionResolver.ResolveProgram("DYN00000", Inputs([1], [gp, other], up));

        actions.Should().BeEquivalentTo([PermissionAction.Read, PermissionAction.Edit]);
    }
}
