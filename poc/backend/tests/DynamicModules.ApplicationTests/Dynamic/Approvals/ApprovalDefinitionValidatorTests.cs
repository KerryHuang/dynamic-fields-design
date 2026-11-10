using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>流程定義的結構驗證：每條規則一個案例（設計 §4.3、§6.1、§9.2）。</summary>
public class ApprovalDefinitionValidatorTests : ApprovalTestBase
{
    private PurchaseRig _r = null!;

    private async Task<Dictionary<string, string[]>> Validate(ApprovalDefinition d)
    {
        _r ??= await BuildPurchase();
        var module = (await Svc<IModuleRepository>().GetByKeyAsync(_r.Module, includeChildren: true, CancellationToken.None))!;
        return await Svc<ApprovalDefinitionValidator>().ValidateAsync(module, module.Workflow!, d, CancellationToken.None);
    }

    private static string E(Dictionary<string, string[]> errors, string key)
    {
        errors.Should().ContainKey(key, string.Join("；", errors.Select(e => $"{e.Key}={e.Value[0]}")));
        return errors[key][0];
    }

    private ApprovalDefinition Valid(params ApprovalStageDef[] stages) =>
        Def(null, stages.Length > 0 ? stages : [Stage("mgr", Manager(1))]);

    [Fact]
    public async Task Valid_NoErrors()
    {
        await BuildRig();
        var d = Def(Cond(_r.Total, "gte", 100000),
            Stage("mgr", Manager(1), dueHours: 24),
            Stage("fin", Group(FinanceGroup), mode: ApprovalModes.All, editable: [_r.Memo, _r.DueDate]),
            Stage("ceo", Users("gm"), when: Cond(_r.Status, "eq", Opt(_r.Changing))),
            Stage("pick", new ApproverSource(ApprovalSources.InitiatorPick, CandidateGroupIds: [BoardGroup], Min: 1, Max: 2)),
            Stage("owner", new ApproverSource(ApprovalSources.UserField, FieldId: _r.Owner, CandidateGroupIds: [PurchaseLeadGroup]),
                onEmpty: ApprovalOnEmpty.Skip)) with { RejectTo = _r.Draft, FallbackApprovers = Group(BoardGroup) };

        (await Validate(d)).Should().BeEmpty();
    }

    private async Task BuildRig() => _r ??= await BuildPurchase();

    [Fact]
    public async Task Shape_Graph_400() =>
        E(await Validate(Valid() with { Shape = "graph" }), "shape").Should().Be(ApprovalMessages.ShapeInvalid);

    [Fact]
    public async Task When_UnknownField_400()
    {
        await BuildRig();
        E(await Validate(Def(Cond(999999, "gte", 1), Stage("mgr", Manager(1)))), "when").Should().StartWith("條件不正確");
    }

    [Fact]
    public async Task RejectTo_NotAState_400() =>
        E(await Validate(Valid() with { RejectTo = 999999 }), "rejectTo").Should().Be(ApprovalMessages.RejectToInvalidState);

    [Fact]
    public async Task Fallback_Manager_400() =>
        E(await Validate(Valid() with { FallbackApprovers = Manager(1) }), "fallbackApprovers").Should().Be(ApprovalMessages.FallbackSourceInvalid);

    [Fact]
    public async Task Stages_None_400() =>
        E(await Validate(Def(null)), "stages").Should().Be(ApprovalMessages.StagesCount);

    [Fact]
    public async Task Stages_11_400() =>
        E(await Validate(Def(null, Enumerable.Range(0, 11).Select(i => Stage($"s{i}", Manager(1))).ToArray())), "stages")
            .Should().Be(ApprovalMessages.StagesCount);

    [Fact]
    public async Task StageCode_Pattern_400() =>
        E(await Validate(Valid(Stage("Mgr", Manager(1)))), "stages[0].code").Should().Be(ApprovalMessages.StageCodeInvalid);

    [Fact]
    public async Task StageCode_Duplicate_400() =>
        E(await Validate(Valid(Stage("a", Manager(1)), Stage("a", Manager(2)))), "stages").Should().Be(ApprovalMessages.StageCodeDuplicate);

    [Fact]
    public async Task StageName_MissingZhTw_400() =>
        E(await Validate(Valid(Stage("a", Manager(1)) with { Name = new() })), "stages[0].name").Should().Be(ApprovalMessages.StageNameRequired);

    [Fact]
    public async Task StageWhen_Invalid_400()
    {
        await BuildRig();
        E(await Validate(Valid(Stage("a", Manager(1), when: Cond(_r.Total, "bogus", 1)))), "stages[0].when").Should().StartWith("條件不正確");
    }

    [Fact]
    public async Task Mode_Invalid_400() =>
        E(await Validate(Valid(Stage("a", Manager(1), mode: "most"))), "stages[0].mode").Should().Be(ApprovalMessages.ModeInvalid);

    [Fact]
    public async Task Source_Unknown_400() =>
        E(await Validate(Valid(Stage("a", new ApproverSource("role")))), "stages[0].approvers").Should().Be(ApprovalMessages.SourceInvalid);

    [Fact]
    public async Task Users_21_400()
    {
        await BuildRig();
        var ids = Enumerable.Range(0, 21).Select(i => $"u{i}").ToArray();
        foreach (var u in ids) Db.Context.Add(new AppUser { UserId = u, DisplayName = u });
        await Db.Context.SaveChangesAsync();
        E(await Validate(Valid(Stage("a", Users(ids)))), "stages[0].approvers").Should().Be(ApprovalMessages.UsersCount);
    }

    [Fact]
    public async Task Users_InactiveOrMissing_400()
    {
        await BuildRig();
        await Db.Context.Set<AppUser>().Where(u => u.UserId == "gm").ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        E(await Validate(Valid(Stage("a", Users("gm")))), "stages[0].approvers").Should().Be(ApprovalMessages.UsersInvalid);
        E(await Validate(Valid(Stage("a", Users("nobody")))), "stages[0].approvers").Should().Be(ApprovalMessages.UsersInvalid);
    }

    [Fact]
    public async Task Group_EmptyOrMissing_400()
    {
        E(await Validate(Valid(Stage("a", Group()))), "stages[0].approvers").Should().Be(ApprovalMessages.GroupsInvalid);
        E(await Validate(Valid(Stage("a", Group(999999)))), "stages[0].approvers").Should().Be(ApprovalMessages.GroupsInvalid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public async Task Level6_400(int level) =>
        E(await Validate(Valid(Stage("a", Manager(level)))), "stages[0].approvers").Should().Be(ApprovalMessages.ManagerLevel);

    [Fact]
    public async Task UserField_WithoutCandidates_400()
    {
        await BuildRig();
        var r = await Validate(Valid(Stage("a", new ApproverSource(ApprovalSources.UserField, FieldId: _r.Owner))));
        E(r, "stages[0].approvers").Should().Be("使用者欄位來源必須設定候選群組");
    }

    [Fact]
    public async Task UserField_NotUserField_400()
    {
        await BuildRig();
        var r = await Validate(Valid(Stage("a", new ApproverSource(ApprovalSources.UserField, FieldId: _r.Memo, CandidateGroupIds: [BoardGroup]))));
        E(r, "stages[0].approvers").Should().Be(ApprovalMessages.UserFieldInvalid);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(3, 2)]
    [InlineData(1, 21)]
    public async Task InitiatorPick_Range_400(int min, int max) =>
        E(await Validate(Valid(Stage("a", new ApproverSource(ApprovalSources.InitiatorPick, Min: min, Max: max)))), "stages[0].approvers")
            .Should().Be(ApprovalMessages.PickRange);

    [Theory]
    [InlineData(0)]
    [InlineData(8761)]
    public async Task DueHours_OutOfRange_400(int hours) =>
        E(await Validate(Valid(Stage("a", Manager(1), dueHours: hours))), "stages[0].dueHours").Should().Be(ApprovalMessages.DueHours);

    [Fact]
    public async Task OnEmpty_Invalid_400() =>
        E(await Validate(Valid(Stage("a", Manager(1), onEmpty: "drop"))), "stages[0].onEmpty").Should().Be(ApprovalMessages.OnEmptyInvalid);

    [Theory]
    [InlineData("formula")]
    [InlineData("rollup")]
    [InlineData("autonumber")]
    [InlineData("status")]
    [InlineData("lockFill")]
    [InlineData("otherModule")]
    [InlineData("missing")]
    public async Task EditableField_Formula_400(string kind)
    {
        await BuildRig();
        var id = kind switch
        {
            "formula" => _r.Total, "rollup" => _r.LineSum, "autonumber" => _r.No, "status" => _r.Status,
            "lockFill" => _r.SupplierName, "otherModule" => _r.LineQty, _ => 999999L
        };
        E(await Validate(Valid(Stage("a", Manager(1), editable: [id]))), "stages[0].editableFields")
            .Should().Be(ApprovalMessages.EditableFieldInvalid);
    }

    [Fact]
    public async Task EditableField_Inactive_400()
    {
        await BuildRig();
        (await Deactivate(_r.DueDate)).IsSuccess.Should().BeTrue();
        E(await Validate(Valid(Stage("a", Manager(1), editable: [_r.DueDate]))), "stages[0].editableFields")
            .Should().Be(ApprovalMessages.EditableFieldInvalid);
    }

    [Fact]
    public async Task EditableField_ConversionTrace_400()
    {
        await BuildRig();
        Db.Context.Add(new Conversion
        {
            Key = "to-po", NameI18n = Text("轉"), SourceModuleId = (await Detail(_r.Vendor)).Id, TargetModuleId = _r.ModuleId,
            Definition = new ConversionDefinition([], new ConversionHeader(_r.Supplier, []), null), CreatedUserId = "admin"
        });
        await Db.Context.SaveChangesAsync();
        E(await Validate(Valid(Stage("a", Manager(1), editable: [_r.Supplier]))), "stages[0].editableFields")
            .Should().Be(ApprovalMessages.EditableFieldInvalid);
    }
}
