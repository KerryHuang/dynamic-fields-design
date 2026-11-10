using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>流程的增改刪、版本與「被用過」（設計 §4.4、§5.2）。</summary>
public class ApprovalFlowDefinitionTests : ApprovalTestBase
{

    private static ApprovalFlowInput Input(string key, long to, int priority, ApprovalDefinition d, bool active = true) =>
        new(key, Text(key), to, priority, active, d);

    private ApprovalDefinition Mgr(PurchaseRig r, long min = 10000) => Def(Cond(r.Total, "gte", min), Stage("mgr", Manager(1)));

    private async Task<ApprovalFlowResponse> Create(PurchaseRig r, ApprovalFlowInput input)
    {
        var res = await Send(new CreateApprovalFlowRequest(r.Module, input));
        res.IsCreated.Should().BeTrue(Dump(res));
        return (ApprovalFlowResponse)res.Data!;
    }

    /// <summary>已確認上建流程 A（優先序 1）、B（優先序 2），再把 草稿 → 已確認 開需簽核、direct。</summary>
    private async Task<PurchaseRig> WithAB()
    {
        var r = await BuildPurchase();
        await Create(r, Input("po-a", r.Confirmed, 1, Mgr(r, 100000)));
        await Create(r, Input("po-b", r.Confirmed, 2, Mgr(r)));
        var wf = await SaveWorkflow(r, [
            Needs(r.Draft, r.Confirmed, ApprovalNoMatch.Direct), Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received),
            Plain(r.Confirmed, r.Cancelled)
        ]);
        wf.IsSuccess.Should().BeTrue(Dump(wf));
        return r;
    }

    private List<ApprovalFlowVersion> Versions(string key)
    {
        var id = Flow(key).Id;
        return Db.Context.Set<ApprovalFlowVersion>().AsNoTracking().Where(v => v.FlowId == id).OrderBy(v => v.Version).ToList();
    }

    [Fact]
    public async Task Save_Unused_OverwritesVersion1()
    {
        var r = await WithAB();
        var res = await Send(new UpdateApprovalFlowRequest("po-b", Input("po-b", r.Confirmed, 2, Mgr(r, 20000))));
        res.IsSuccess.Should().BeTrue(Dump(res));

        var got = (ApprovalFlowResponse)res.Data!;
        got.Version.Should().Be(1);
        got.Used.Should().BeFalse();
        Versions("po-b").Should().ContainSingle().Which.Definition.When!.Value.GetRawText().Should().Contain("20000");
    }

    [Fact]
    public async Task Save_Used_CreatesVersion2()
    {
        var r = await WithAB();
        await SeedInstance(r, Flow("po-b"));
        var v1 = Flow("po-b").CurrentVersionId;

        var res = await Send(new UpdateApprovalFlowRequest("po-b", Input("po-b", r.Confirmed, 2, Mgr(r, 20000))));
        res.IsSuccess.Should().BeTrue(Dump(res));

        var got = (ApprovalFlowResponse)res.Data!;
        (got.Version, got.Used, got.PendingCount).Should().Be((2, true, 1));
        var versions = Versions("po-b");
        versions.Select(v => v.Version).Should().Equal(1, 2);
        versions[0].Id.Should().Be(v1!.Value);
        versions[0].Definition.When!.Value.GetRawText().Should().Contain("10000");
        Flow("po-b").CurrentVersionId.Should().Be(versions[1].Id);
    }

    [Fact]
    public async Task SnapshotOnly_CountsAsUsed()
    {
        var r = await WithAB();
        await SeedInstance(r, Flow("po-a"), snapshotOnly: true);

        var res = await Send(new UpdateApprovalFlowRequest("po-a", Input("po-a", r.Confirmed, 1, Mgr(r, 1000))));
        res.IsSuccess.Should().BeTrue(Dump(res));
        ((ApprovalFlowResponse)res.Data!).Version.Should().Be(2);

        var del = await Send(new DeleteApprovalFlowRequest("po-a"));
        del.IsConflict.Should().BeTrue();
        del.Message.Should().Be("流程已被使用，只能停用");
    }

    [Fact]
    public async Task DeleteSecondFlow_LeavesDirectEntryEmpty_400()
    {
        var r = await WithAB();
        (await Send(new DeleteApprovalFlowRequest("po-a"))).IsSuccess.Should().BeTrue();

        var res = await Send(new DeleteApprovalFlowRequest("po-b"));
        res.IsInvalid.Should().BeTrue(Dump(res));
        res.Errors.Should().ContainKey($"states[{r.Confirmed}]");
        Db.Context.Set<ApprovalFlow>().AsNoTracking().Count(f => f.Key == "po-b").Should().Be(1);
    }

    [Fact]
    public async Task DisableLastActiveOfDirectEntry_400_OtherwiseOk()
    {
        var r = await WithAB();
        (await Send(new UpdateApprovalFlowRequest("po-a", Input("po-a", r.Confirmed, 1, Mgr(r, 100000), active: false)))).IsSuccess.Should().BeTrue();
        var res = await Send(new UpdateApprovalFlowRequest("po-b", Input("po-b", r.Confirmed, 2, Mgr(r), active: false)));
        res.Errors.Should().ContainKey($"states[{r.Confirmed}]").WhoseValue.Should().Contain(ApprovalMessages.DirectNeedsActive("已確認"));
    }

    [Fact]
    public async Task ChangeTarget_Used_400_Unused_RevalidatesConstraints()
    {
        var r = await WithAB();
        await SeedInstance(r, Flow("po-a"));

        var used = await Send(new UpdateApprovalFlowRequest("po-a", Input("po-a", r.Cancelled, 1, Mgr(r, 100000))));
        used.Errors.Should().ContainKey("toOptionId").WhoseValue.Should().Contain("被使用過的流程不能改目標狀態，請另建流程");

        // 沒被用過的 B 搬到已取消：已確認只剩被用過的 A（啟用中）→ 通過
        (await Send(new UpdateApprovalFlowRequest("po-b", Input("po-b", r.Cancelled, 1, Mgr(r))))).IsSuccess.Should().BeTrue();
        // 停用 A 後，已確認的 direct 入口沒有啟用中的流程 → 400
        var off = await Send(new UpdateApprovalFlowRequest("po-a", Input("po-a", r.Confirmed, 1, Mgr(r, 100000), active: false)));
        off.Errors.Should().ContainKey($"states[{r.Confirmed}]");
    }

    [Fact]
    public async Task ChangeTarget_Unused_BothAway_400()
    {
        var r = await WithAB();
        (await Send(new UpdateApprovalFlowRequest("po-a", Input("po-a", r.Cancelled, 1, Mgr(r, 100000))))).IsSuccess.Should().BeTrue();
        var res = await Send(new UpdateApprovalFlowRequest("po-b", Input("po-b", r.Cancelled, 2, Mgr(r))));
        res.Errors.Should().ContainKey($"states[{r.Confirmed}]").WhoseValue.Should().Contain(ApprovalMessages.NoFlow("已確認"));
    }

    [Fact]
    public async Task Reorder_SwapsPriorities()
    {
        var r = await WithAB();
        var res = await Send(new ReorderApprovalFlowsRequest(r.Module, r.Confirmed, ["po-b", "po-a"]));
        res.IsSuccess.Should().BeTrue(Dump(res));
        (Flow("po-b").Priority, Flow("po-a").Priority).Should().Be((1, 2));

        (await Send(new ReorderApprovalFlowsRequest(r.Module, r.Confirmed, ["po-b"]))).Errors.Should().ContainKey("keys");
    }

    [Fact]
    public async Task DuplicateKey_409()
    {
        var r = await WithAB();
        (await Send(new CreateApprovalFlowRequest(r.Module, Input("po-a", r.Confirmed, 3, Mgr(r))))).IsConflict.Should().BeTrue();
        (await Send(new UpdateApprovalFlowRequest("po-a", Input("po-x", r.Confirmed, 1, Mgr(r))))).Errors.Should().ContainKey("key");
    }

    [Fact]
    public async Task NonDesigner_403()
    {
        var r = await WithAB();
        As("sales01");
        (await Send(new CreateApprovalFlowRequest(r.Module, Input("po-z", r.Confirmed, 3, Mgr(r))))).IsForbidden.Should().BeTrue();
        (await Send(new UpdateApprovalFlowRequest("po-a", Input("po-a", r.Confirmed, 1, Mgr(r))))).IsForbidden.Should().BeTrue();
        (await Send(new DeleteApprovalFlowRequest("po-a"))).IsForbidden.Should().BeTrue();
        (await Send(new GetApprovalFlowsRequest(r.Module))).IsForbidden.Should().BeTrue();
    }

    [Fact]
    public async Task DetailModule_400()
    {
        var r = await BuildPurchase();
        var res = await Send(new CreateApprovalFlowRequest(r.Line, Input("line-a", r.Confirmed, 1, Mgr(r))));
        res.Errors.Should().ContainKey("moduleKey");
    }

    [Fact]
    public async Task Create_Rules_400()
    {
        var r = await BuildPurchase();
        (await Send(new CreateApprovalFlowRequest(r.Module, Input("po-a", r.Draft, 1, Mgr(r))))).Errors.Should().ContainKey("toOptionId");
        (await Send(new CreateApprovalFlowRequest(r.Module, Input("P", r.Confirmed, 1, Mgr(r))))).Errors.Should().ContainKey("key");
        (await Send(new CreateApprovalFlowRequest(r.Module, Input("po-a", r.Confirmed, 1, Def(null)))))
            .Errors.Should().ContainKey("definition.stages");
        await Create(r, Input("po-a", r.Confirmed, 1, Mgr(r)));
        (await Send(new CreateApprovalFlowRequest(r.Module, Input("po-b", r.Confirmed, 1, Mgr(r))))).Errors.Should().ContainKey("priority");
    }

    [Fact]
    public async Task Get_ListsFlowsWithVersion()
    {
        var r = await WithAB();
        var list = (IReadOnlyList<ApprovalFlowResponse>)(await Send(new GetApprovalFlowsRequest(r.Module))).Data!;
        list.Select(f => f.Key).Should().Equal("po-a", "po-b");
        list[0].ModuleKey.Should().Be(r.Module);
        list[0].Version.Should().Be(1);

        var one = (ApprovalFlowResponse)(await Send(new GetApprovalFlowRequest("po-b"))).Data!;
        one.Priority.Should().Be(2);
        (await Send(new GetApprovalFlowRequest("nope"))).IsNotFound.Should().BeTrue();
    }

    [Fact]
    public async Task EditableField_HiddenForSomeGroup_400()
    {
        // 裁決 22：可編輯欄位不得是對簽核人隱藏的欄位；存流程時不知道最後是誰簽，對任何群組隱藏就拒絕
        var r = await BuildPurchase();
        await HideFor(r.Module, r.DueDate, FinanceGroup);
        var bad = await Send(new CreateApprovalFlowRequest(r.Module,
            Input("po-h", r.Confirmed, 1, Def(null, Stage("lead", Group(PurchaseLeadGroup), editable: [r.DueDate])))));
        bad.Errors["definition.stages[0].editableFields"].Should().Equal(ApprovalMessages.EditableFieldHidden);
        Db.Context.Set<ApprovalFlow>().AsNoTracking().Count().Should().Be(0);

        await Create(r, Input("po-h", r.Confirmed, 1, Def(null, Stage("lead", Group(PurchaseLeadGroup), editable: [r.Memo]))));
    }
}
