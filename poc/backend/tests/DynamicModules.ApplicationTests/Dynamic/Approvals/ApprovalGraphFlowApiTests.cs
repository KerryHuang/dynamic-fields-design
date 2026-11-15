using System.Text.Json;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using static DynamicModules.ApplicationTests.Dynamic.Approvals.ApprovalGraphTestKit;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>
/// 流程圖的存檔與試算（計畫 fg2g2 Task 11：U1–U4）。這個類別的測試主機沒有任何 graph 專用的替換，走正式的 DI 組態：
/// 流程從 API 存進去，再送出、同意，證明 graph 入口在正式組態下是開的。
/// </summary>
public class ApprovalGraphFlowApiTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    private static ApprovalFlowInput Input(string key, long to, int priority, ApprovalDefinition d, bool active = true) =>
        new(key, Text(key), to, priority, active, d);

    /// <summary>主管 → 法務、財務任一到齊 → 結束。</summary>
    private static ApprovalDefinition AnyOfTwo() => G(
        [Start(), Approval("mgr", Users("emp1")), Fork("f"), Approval("legal", Users("fin1")), Approval("fin", Users("buyer2")),
         Join("j", "f", ApprovalModes.Any), End()],
        [E("e0", "start", "mgr"), E("e1", "mgr", "f"), E("fl", "f", "legal"), E("ff", "f", "fin"), E("lj", "legal", "j"), E("fj", "fin", "j"), E("je", "j", "end")]);

    /// <summary>狀態機先不要求簽核（要求簽核的轉移需要已經有流程）；要送出的測試在建好流程後呼叫 <see cref="RequireApproval"/>。</summary>
    private async Task<PurchaseRig> Rig()
    {
        var r = await BuildPurchase();
        (await SaveWorkflow(r, [Plain(r.Draft, r.Confirmed), Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received),
            Plain(r.Confirmed, r.Cancelled)])).IsSuccess.Should().BeTrue();
        await GrantEveryone(r.Module);
        As("admin");
        return r;
    }

    private async Task RequireApproval(PurchaseRig r) =>
        (await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed), Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received),
            Plain(r.Confirmed, r.Cancelled)])).IsSuccess.Should().BeTrue();

    private async Task<ApprovalFlowResponse> Create(PurchaseRig r, ApprovalFlowInput input)
    {
        var res = await Send(new CreateApprovalFlowRequest(r.Module, input));
        res.IsCreated.Should().BeTrue(Dump(res));
        return (ApprovalFlowResponse)res.Data!;
    }

    private List<ApprovalFlowVersion> Versions(string key)
    {
        var id = Flow(key).Id;
        return Db.Context.Set<ApprovalFlowVersion>().AsNoTracking().Where(v => v.FlowId == id).OrderBy(v => v.Version).ToList();
    }

    // ───────── 存檔（U1） ─────────

    [Fact]
    public async Task Create_Graph_201_VersionOne()
    {
        var r = await Rig();

        var got = await Create(r, Input("po-g", r.Confirmed, 1, AnyOfTwo()));

        got.Version.Should().Be(1);
        got.Definition!.Shape.Should().Be(ApprovalShapes.Graph);
        Versions("po-g").Should().ContainSingle().Which.Definition.Nodes.Should().HaveCount(7);
    }

    [Fact]
    public async Task Create_GraphInvalid_400_NodeKeys()
    {
        var r = await Rig();
        // 條件節點缺預設線、x 沒有入線
        var broken = G([Start(), Condition("c"), Approval("a", Users("fin1")), Approval("x", Users("emp1")), End()],
            [E("e0", "start", "c"), E("ca", "c", "a", Cond(r.Total, "gte", 1), order: 1), E("cb", "c", "end", Cond(r.Total, "gte", 2), order: 2),
             E("ae", "a", "end"), E("xe", "x", "end")]);

        var res = await Send(new CreateApprovalFlowRequest(r.Module, Input("po-g", r.Confirmed, 1, broken)));

        res.IsInvalid.Should().BeTrue(Dump(res));
        res.Errors.Keys.Should().Contain("definition.nodes.x");
        res.Errors.Keys.Should().NotContain("definition.shape");
        Db.Context.Set<ApprovalFlow>().AsNoTracking().Count().Should().Be(0);
    }

    [Fact]
    public async Task Create_GraphWithNotifyNode_400()
    {
        // U2：通知節點在 fg5 之前存檔拒收
        var r = await Rig();
        var def = G([Start(), Approval("a", Users("fin1")), Notify("tell"), End()],
            [E("e0", "start", "a"), E("e1", "a", "tell"), E("e2", "tell", "end")]);

        var res = await Send(new CreateApprovalFlowRequest(r.Module, Input("po-g", r.Confirmed, 1, def)));

        res.IsInvalid.Should().BeTrue(Dump(res));
        res.Errors["definition.nodes.tell"].Should().Equal(ApprovalMessages.NotifyNodeNotYet);
        res.Errors.Keys.Should().Equal("definition.nodes.tell");
    }

    [Fact]
    public async Task Update_Graph_UnusedVersion_OverwritesVersionOne()
    {
        var r = await Rig();
        await Create(r, Input("po-g", r.Confirmed, 1, AnyOfTwo()));

        var res = await Send(new UpdateApprovalFlowRequest("po-g", Input("po-g", r.Confirmed, 1, AnyOfTwo() with { RejectTo = r.Draft })));

        res.IsSuccess.Should().BeTrue(Dump(res));
        ((ApprovalFlowResponse)res.Data!).Version.Should().Be(1);
        Versions("po-g").Should().ContainSingle().Which.Definition.RejectTo.Should().Be(r.Draft);
    }

    [Fact]
    public async Task Update_Graph_UsedVersion_NewVersion()
    {
        var r = await Rig();
        await Create(r, Input("po-g", r.Confirmed, 1, AnyOfTwo()));
        await RequireApproval(r);
        As("buyer1");
        var po = await NewPo(r);
        (await MustSubmit(po.Id, r.Confirmed)).Outcome.Should().Be(ApprovalOutcomes.Submitted);
        As("admin");

        var res = await Send(new UpdateApprovalFlowRequest("po-g", Input("po-g", r.Confirmed, 1, AnyOfTwo() with { RejectTo = r.Draft })));

        res.IsSuccess.Should().BeTrue(Dump(res));
        var got = (ApprovalFlowResponse)res.Data!;
        (got.Version, got.Used, got.PendingCount).Should().Be((2, true, 1));
        Versions("po-g").Select(v => v.Version).Should().Equal(1, 2);
    }

    [Fact]
    public async Task Toggle_GraphFlow_SameDefinition_Ok()
    {
        // 卡片上的啟停開關送回原定義：不被 U4 擋，也不重驗流程圖以外的東西
        var r = await Rig();
        await Create(r, Input("po-g", r.Confirmed, 1, AnyOfTwo()));

        var off = await Send(new UpdateApprovalFlowRequest("po-g", Input("po-g", r.Confirmed, 1, AnyOfTwo(), active: false)));
        var on = await Send(new UpdateApprovalFlowRequest("po-g", Input("po-g", r.Confirmed, 1, AnyOfTwo(), active: true)));

        off.IsSuccess.Should().BeTrue(Dump(off));
        on.IsSuccess.Should().BeTrue(Dump(on));
        Versions("po-g").Should().ContainSingle();
    }

    // ───────── U4：graph 不能存回 sequence ─────────

    [Fact]
    public async Task Update_GraphToSequence_400()
    {
        var r = await Rig();
        await Create(r, Input("po-g", r.Confirmed, 1, AnyOfTwo()));
        var before = Versions("po-g").Single().Definition;

        var res = await Send(new UpdateApprovalFlowRequest("po-g", Input("po-g", r.Confirmed, 1, Def(null, Stage("lead", Group(PurchaseLeadGroup))))));

        res.IsInvalid.Should().BeTrue(Dump(res));
        res.Errors["definition.shape"].Should().Equal(ApprovalMessages.GraphCannotRevert);
        Versions("po-g").Should().ContainSingle().Which.Definition.Shape.Should().Be(ApprovalShapes.Graph);
        Versions("po-g").Single().Definition.Nodes.Should().HaveCount(before.Nodes!.Count);
    }

    [Fact]
    public async Task Update_GraphToSequence_Deactivate_StillBlocked()
    {
        // 啟停開關若夾帶 sequence 定義，一樣擋（不能借停用繞過）
        var r = await Rig();
        await Create(r, Input("po-g", r.Confirmed, 1, AnyOfTwo()));

        var res = await Send(new UpdateApprovalFlowRequest("po-g", Input("po-g", r.Confirmed, 1, Def(null, Stage("lead", Group(PurchaseLeadGroup))), active: false)));

        res.IsInvalid.Should().BeTrue(Dump(res));
        res.Errors.Keys.Should().Contain("definition.shape");
    }

    [Fact]
    public async Task Update_SequenceToGraph_Allowed()
    {
        // 反方向不擋：sequence 流程可以換成流程圖（舊版本被用過就開新版本）
        var r = await Rig();
        await Create(r, Input("po-s", r.Confirmed, 1, Def(null, Stage("lead", Group(PurchaseLeadGroup)))));

        var res = await Send(new UpdateApprovalFlowRequest("po-s", Input("po-s", r.Confirmed, 1, AnyOfTwo())));

        res.IsSuccess.Should().BeTrue(Dump(res));
        ((ApprovalFlowResponse)res.Data!).Definition!.Shape.Should().Be(ApprovalShapes.Graph);
    }

    // ───────── 試算 ─────────

    [Fact]
    public async Task DraftPreview_Graph_PathReturned()
    {
        var r = await Rig();

        var res = await Send(new PreviewApprovalDraftRequest(r.Module, Input("po-g", r.Confirmed, 1, AnyOfTwo()), null, null, "buyer1"));

        res.IsSuccess.Should().BeTrue(Dump(res));
        var p = (ApprovalPreviewResponse)res.Data!;
        p.Outcome.Should().Be(ApprovalPreviewOutcomes.Flow);
        p.Path!.NodeIds.Should().Contain(["mgr", "legal", "fin", "j"]);
        p.Stages.Where(s => s.AnyGroup is not null).Select(s => s.Code).Should().Equal("legal", "fin");
    }

    [Fact]
    public async Task DraftPreview_GraphWithNotifyNode_400()
    {
        var r = await Rig();
        var def = G([Start(), Approval("a", Users("fin1")), Notify("tell"), End()],
            [E("e0", "start", "a"), E("e1", "a", "tell"), E("e2", "tell", "end")]);

        var res = await Send(new PreviewApprovalDraftRequest(r.Module, Input("po-g", r.Confirmed, 1, def), null, null, "buyer1"));

        res.IsInvalid.Should().BeTrue(Dump(res));
        res.Errors.Keys.Should().Contain("definition.nodes.tell");
    }

    // ───────── 存檔後直接送出、同意（正式組態，沒有開關） ─────────

    [Fact]
    public async Task Create_Graph_ThenSubmitApprove_EndToEnd()
    {
        var r = await Rig();
        await Create(r, Input("po-g", r.Confirmed, 1, AnyOfTwo()));
        await RequireApproval(r);
        As("buyer1");
        var po = await NewPo(r);

        var submit = await MustSubmit(po.Id, r.Confirmed);

        submit.Outcome.Should().Be(ApprovalOutcomes.Submitted);
        var id = submit.InstanceId!.Value;
        var mgr = TasksOf(id).Single(t => t.Status == ApprovalTaskStatus.Open);
        mgr.AssigneeUserId.Should().Be("emp1");
        As("emp1");
        (await Send(new ApproveTaskRequest(mgr.Id, Inst(id).RowVersion, null, null))).IsSuccess.Should().BeTrue();
        var fin = TasksOf(id).Single(t => t.AssigneeUserId == "buyer2" && t.Status == ApprovalTaskStatus.Open);
        As("buyer2");

        var done = await Send(new ApproveTaskRequest(fin.Id, Inst(id).RowVersion, null, null));

        done.IsSuccess.Should().BeTrue(Dump(done));
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Approved);
        TasksOf(id).Single(t => t.AssigneeUserId == "fin1").Status.Should().Be(ApprovalTaskStatus.Closed);
        StateOf(r, po.Id).Should().Be(r.Confirmed);
        ActionsOf(id).Should().Contain(a => a.Action == ApprovalActions.BranchCancelled);
    }

    // ───────── 搬家：原擋門測試中還有意義的案例（J7 對照表） ─────────

    [Fact]
    public async Task Submit_GraphFlowInactive_FallsThroughToNextFlow()
    {
        // 停用的 graph 流程不參與比對；後面的 sequence 流程照常命中（原擋門測試 Submit_GraphFlowInactive_NotBlocking）
        var r = await Rig();
        await Create(r, Input("po-g", r.Confirmed, 1, AnyOfTwo(), active: false));
        await Create(r, Input("po-s", r.Confirmed, 2, Def(null, Stage("lead", Group(PurchaseLeadGroup)))));
        await RequireApproval(r);
        As("buyer1");
        var po = await NewPo(r);

        var res = await MustSubmit(po.Id, r.Confirmed);

        Inst(res.InstanceId!.Value).FlowId.Should().Be(Flow("po-s").Id);
    }

    [Fact]
    public async Task Submit_GraphFlowConditionNotMet_FallsThroughToNextFlow()
    {
        // 條件不成立的 graph 流程不命中（原 Submit_GraphFlowConditionNotMet_NotBlocking）
        var r = await Rig();
        await Create(r, Input("po-g", r.Confirmed, 1, AnyOfTwo() with { When = Cond(r.Total, "gte", 99999999) }));
        await Create(r, Input("po-s", r.Confirmed, 2, Def(null, Stage("lead", Group(PurchaseLeadGroup)))));
        await RequireApproval(r);
        As("buyer1");
        var po = await NewPo(r, price: 150000);

        var res = await MustSubmit(po.Id, r.Confirmed);

        Inst(res.InstanceId!.Value).FlowId.Should().Be(Flow("po-s").Id);
    }
}
