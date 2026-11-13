using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.WorkflowService.Data;
using DynamicModules.Application.Dynamic.WorkflowService.Queries.GetWorkflow;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>設計器要的後端補齊（a4a1）：流程回應的目前版本是否已用過、只改啟停不產生新版本、可編輯欄位候選。</summary>
public class ApprovalFlowDesignerTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    private static ApprovalFlowInput Input(string key, long to, int priority, ApprovalDefinition d, bool active = true, string? name = null) =>
        new(key, Text(name ?? key), to, priority, active, d);

    private static ApprovalDefinition Mgr(PurchaseRig r, long min = 10000) => Def(Cond(r.Total, "gte", min), Stage("mgr", Manager(1)));

    private async Task<PurchaseRig> WithAB()
    {
        var r = await BuildPurchase();
        await SeedFlow(r, "po-a", r.Confirmed, 1, Mgr(r, 100000));
        await SeedFlow(r, "po-b", r.Confirmed, 2, Mgr(r));
        (await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed, ApprovalNoMatch.Direct), Plain(r.Confirmed, r.Partial),
            Plain(r.Partial, r.Received), Plain(r.Confirmed, r.Cancelled)])).IsSuccess.Should().BeTrue();
        return r;
    }

    private int VersionCount(string key)
    {
        var id = Flow(key).Id;
        return Db.Context.Set<ApprovalFlowVersion>().AsNoTracking().Count(v => v.FlowId == id);
    }

    private async Task<ApprovalFlowResponse> Update(ApprovalFlowInput input)
    {
        var res = await Send(new UpdateApprovalFlowRequest(input.Key, input));
        res.IsSuccess.Should().BeTrue(Dump(res));
        return (ApprovalFlowResponse)res.Data!;
    }

    [Fact]
    public async Task Update_UsedVersion_OnlyActiveChanged_NoNewVersion()
    {
        var r = await WithAB();
        await SeedInstance(r, Flow("po-b"));
        var v1 = Flow("po-b").CurrentVersionId;

        var got = await Update(Input("po-b", r.Confirmed, 2, Mgr(r), active: false));

        got.Version.Should().Be(1);
        got.Active.Should().BeFalse();
        VersionCount("po-b").Should().Be(1);
        Flow("po-b").CurrentVersionId.Should().Be(v1);
    }

    [Fact]
    public async Task Update_UsedVersion_NameAndPriorityChanged_NoNewVersion()
    {
        var r = await WithAB();
        await SeedInstance(r, Flow("po-b"));

        var got = await Update(Input("po-b", r.Confirmed, 5, Mgr(r), name: "新名稱"));

        (got.Version, got.Priority).Should().Be((1, 5));
        got.Name.Resolve("zh-TW").Should().Be("新名稱");
        VersionCount("po-b").Should().Be(1);
    }

    [Fact]
    public async Task Update_UsedVersion_DefinitionChanged_NewVersion()
    {
        var r = await WithAB();
        await SeedInstance(r, Flow("po-b"));

        var got = await Update(Input("po-b", r.Confirmed, 2, Mgr(r, 20000)));

        got.Version.Should().Be(2);
        got.CurrentVersionUsed.Should().BeFalse();
        got.Used.Should().BeTrue();
        VersionCount("po-b").Should().Be(2);
    }

    /// <summary>流程引用的群組被刪掉（直接刪資料列，繞過 DeleteGroup 的引用守衛，模擬舊資料）。</summary>
    private async Task<(PurchaseRig R, ApprovalDefinition Def)> WithDeletedGroup()
    {
        var r = await WithAB();
        var def = Def(null, Stage("board", Group(BoardGroup)));
        await SeedFlow(r, "po-g", r.Confirmed, 3, def);
        await Db.Context.Set<UserGroupMapping>().Where(m => m.GroupId == BoardGroup).ExecuteDeleteAsync();
        await Db.Context.Set<PermissionGroup>().Where(g => g.Id == BoardGroup).ExecuteDeleteAsync();
        Db.Context.ChangeTracker.Clear();
        return (r, def);
    }

    [Fact]
    public async Task Update_DefinitionUnchanged_Deactivate_StillWorksAfterReferencedGroupDeleted()
    {
        var (r, def) = await WithDeletedGroup();

        var got = await Update(Input("po-g", r.Confirmed, 3, def, active: false));

        got.Active.Should().BeFalse();
        Flow("po-g").Active.Should().BeFalse();
        VersionCount("po-g").Should().Be(1);
    }

    [Fact]
    public async Task Update_DefinitionUnchanged_Activate_RejectedWhenReferencedGroupDeleted()
    {
        var (r, def) = await WithDeletedGroup();
        await Update(Input("po-g", r.Confirmed, 3, def, active: false));

        var res = await Send(new UpdateApprovalFlowRequest("po-g", Input("po-g", r.Confirmed, 3, def, active: true)));

        res.IsSuccess.Should().BeFalse();
        res.Errors.Keys.Should().Contain(k => k.StartsWith("definition.stages[0]"));
        Flow("po-g").Active.Should().BeFalse();
    }

    [Fact]
    public async Task Update_DefinitionChanged_Deactivate_StillValidated()
    {
        var (r, _) = await WithDeletedGroup();

        var res = await Send(new UpdateApprovalFlowRequest("po-g", Input("po-g", r.Confirmed, 3, Def(null, Stage("board", Group(BoardGroup, FinanceGroup))), active: false)));

        res.IsSuccess.Should().BeFalse();
        res.Errors.Keys.Should().Contain(k => k.StartsWith("definition.stages[0]"));
        Flow("po-g").Active.Should().BeTrue();
    }

    [Fact]
    public async Task Response_CurrentVersionUsed_TrueWhenBound()
    {
        var r = await WithAB();
        await SeedInstance(r, Flow("po-b"));
        var list = (IReadOnlyList<ApprovalFlowResponse>)(await Send(new GetApprovalFlowsRequest(r.Module))).Data!;
        list.Single(f => f.Key == "po-b").CurrentVersionUsed.Should().BeTrue();
        list.Single(f => f.Key == "po-a").CurrentVersionUsed.Should().BeFalse();
    }

    [Fact]
    public async Task Response_CurrentVersionUsed_FalseForNewFlow()
    {
        var r = await WithAB();
        var res = await Send(new CreateApprovalFlowRequest(r.Module, Input("po-new", r.Cancelled, 1, Def(null, Stage("s", Manager(1))))));
        res.IsCreated.Should().BeTrue(Dump(res));
        var got = (ApprovalFlowResponse)res.Data!;
        (got.CurrentVersionUsed, got.Used).Should().Be((false, false));
    }

    [Fact]
    public async Task Response_CurrentVersionUsed_TrueWhenOnlyInSnapshot()
    {
        var r = await WithAB();
        // 流程 B 只出現在別的實例的快照裡（不是實例綁定的流程）
        await SeedInstance(r, Flow("po-b"), snapshotOnly: true);
        var got = (ApprovalFlowResponse)(await Send(new GetApprovalFlowRequest("po-b"))).Data!;
        got.CurrentVersionUsed.Should().BeTrue();
        got.Used.Should().BeTrue();
    }
    // ───────── 可編輯欄位候選 ─────────

    private async Task<WorkflowResponse> GetWorkflow(PurchaseRig r)
    {
        var res = await Send(new GetWorkflowRequest(r.Module));
        res.IsSuccess.Should().BeTrue(Dump(res));
        return (WorkflowResponse)res.Data!;
    }

    [Fact]
    public async Task GetWorkflow_EditableFieldIds_ExcludeComputedStatusAndLockFill()
    {
        var r = await BuildPurchase();
        var other = await MustAdd(r.Module, FieldType.Text, label: "停用中");
        (await Deactivate(other.Id)).IsSuccess.Should().BeTrue();

        var got = (await GetWorkflow(r)).EditableFieldIds;

        got.Should().Contain([r.Memo, r.DueDate, r.Discount, r.Supplier, r.Owner, TitleId(r.Module)]);
        got.Should().NotContain([r.Total, r.LineSum, r.ExtraSum, r.NetTotal, r.No, r.Status, r.SupplierName, other.Id]);
        got.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task GetWorkflow_EditableFieldIds_ExcludeConversionTraceAndLedgerReversal()
    {
        var r = await BuildPurchase();
        Db.Context.Add(new Conversion
        {
            Key = "to-po", NameI18n = Text("轉"), SourceModuleId = (await Detail(r.Vendor)).Id, TargetModuleId = r.ModuleId,
            Definition = new ConversionDefinition([], new ConversionHeader(r.Supplier, []), null), CreatedUserId = "admin"
        });
        Db.Context.Add(new ModuleLedger { ModuleId = r.ModuleId, Definition = new LedgerDefinition(r.Owner, [], []) });
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();

        var got = (await GetWorkflow(r)).EditableFieldIds;

        got.Should().NotContain([r.Supplier, r.Owner]);
        got.Should().Contain([r.Memo, r.DueDate]);
    }

    [Fact]
    public async Task SaveWorkflow_ResponseHasEditableFieldIds()
    {
        var r = await BuildPurchase();
        var res = await SaveWorkflow(r, BaseTransitions(r));
        res.IsSuccess.Should().BeTrue(Dump(res));
        ((WorkflowResponse)res.Data!).EditableFieldIds.Should().Contain(r.Memo).And.NotContain(r.Total);
    }

    [Fact]
    public async Task Validator_StillRejectsFormulaAsEditable()
    {
        var r = await WithAB();
        var res = await Send(new UpdateApprovalFlowRequest("po-b",
            Input("po-b", r.Confirmed, 2, Def(null, Stage("mgr", Manager(1), editable: [r.Total])))));
        res.IsInvalid.Should().BeTrue(Dump(res));
        res.Errors.Should().ContainKey("definition.stages[0].editableFields");
    }
}
