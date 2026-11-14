using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>流程圖設計 §13 第 7 點：引擎還不會跑 graph，執行期選到 graph 定義一律 409（fail closed）；fg2 引擎上線時移除。</summary>
public class ApprovalShapeGateTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    /// <summary>只有 shape 與空的 stages；現在 API 寫不出 graph，這裡直接在 Db 造出「回退後遇到」的資料。</summary>
    private static ApprovalDefinition Graph(JsonElement? when = null) => new("graph", when, null, null, []);

    private string MessageFor(string flow) => ApprovalMessages.GraphNotSupported(flow).Resolve(null)!;

    private async Task<PurchaseRig> WithGraphFlow(bool active = true, Func<PurchaseRig, JsonElement?>? when = null)
    {
        var r = await BuildPurchase();
        await SeedFlow(r, "po-g", r.Confirmed, 1, Graph(when?.Invoke(r)), active);
        await SeedFlow(r, "po-s", r.Confirmed, 2, Def(Cond(r.Total, "gte", 100000), Stage("lead", Group(PurchaseLeadGroup))));
        (await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed, ApprovalNoMatch.Direct), Plain(r.Confirmed, r.Partial),
            Plain(r.Partial, r.Received), Plain(r.Confirmed, r.Cancelled)])).IsSuccess.Should().BeTrue();
        await GrantEveryone(r.Module);
        return r;
    }

    [Fact]
    public async Task Submit_MatchedGraphFlow_409_NothingWritten()
    {
        var r = await WithGraphFlow();
        As("buyer1");
        var po = await NewPo(r, price: 5000);
        var rowVersion = Rec(po.Id).Row.RowVersion;

        var res = await Submit(po.Id, r.Confirmed);

        res.IsConflict.Should().BeTrue(Dump(res));
        res.Message.Should().Be(MessageFor("po-g"));
        Db.Context.Set<ApprovalInstance>().AsNoTracking().Count().Should().Be(0);
        Db.Context.Set<ApprovalTask>().AsNoTracking().Count().Should().Be(0);
        (StateOf(r, po.Id), Rec(po.Id).Row.RowVersion).Should().Be((r.Draft, rowVersion));
    }

    [Fact]
    public async Task Preview_MatchedGraphFlow_409()
    {
        var r = await WithGraphFlow();
        As("buyer1");
        var po = await NewPo(r, price: 5000);

        var res = await Send(new PreviewRecordApprovalRequest(po.Id, r.Confirmed));

        res.IsConflict.Should().BeTrue(Dump(res));
        res.Message.Should().Be(MessageFor("po-g"));
    }

    [Fact]
    public async Task Submit_GraphFlowInactive_NotBlocking()
    {
        var r = await WithGraphFlow(active: false);
        As("buyer1");
        var po = await NewPo(r, price: 150000);

        var res = await MustSubmit(po.Id, r.Confirmed);

        res.Outcome.Should().Be(ApprovalOutcomes.Submitted);
        Inst(res.InstanceId!.Value).FlowId.Should().Be(Flow("po-s").Id);
    }

    [Fact]
    public async Task Submit_GraphFlowConditionNotMet_NotBlocking()
    {
        // 只有比對到的 graph 流程才 409；條件不成立的 graph 流程不影響後面的 sequence 流程
        var r = await WithGraphFlow(when: x => Cond(x.Total, "gte", 99999999));
        As("buyer1");
        var po = await NewPo(r, price: 150000);

        var res = await MustSubmit(po.Id, r.Confirmed);

        res.Outcome.Should().Be(ApprovalOutcomes.Submitted);
        Inst(res.InstanceId!.Value).FlowId.Should().Be(Flow("po-s").Id);
    }

    [Fact]
    public async Task Approve_RecheckSelectsGraphFlow_409_Rollback()
    {
        var r = await Scenario3();
        As("buyer1");
        var po = await NewPo(r, price: 150000);
        var id = (await MustSubmit(po.Id, r.Confirmed)).InstanceId!.Value;
        // 把一條優先序更前、無條件的 graph 流程放進實例快照（模擬回退後快照裡出現 graph），重新比對就會選到它
        var inst = Db.Context.Set<ApprovalInstance>().Single(i => i.Id == id);
        inst.FlowSetSnapshot = [new FlowSnapshotEntry(9999, 9999, 0, true, Text("po-g"), Graph()), .. inst.FlowSetSnapshot];
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
        var task = TasksOf(id).Single(t => t.Status == ApprovalTaskStatus.Open);
        As(task.AssigneeUserId);

        var res = await Send(new ApproveTaskRequest(task.Id, Inst(id).RowVersion, null, null));

        res.IsConflict.Should().BeTrue(Dump(res));
        res.Message.Should().Be(MessageFor("po-g"));
        TasksOf(id).Single(t => t.Id == task.Id).Status.Should().Be(ApprovalTaskStatus.Open);
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Pending);
        ActionsOf(id).Should().NotContain(a => a.Action == ApprovalActions.Approve || a.Action == ApprovalActions.Reroute);
    }

    // ───────── 實例已綁定 graph（回退後遇到 fg2 留下的實例）：所有任務與實例動作都要擋 ─────────

    /// <summary>Scenario3 送出一張 150000 的單據（流程 po-a，有一個 open 任務），再把實例綁定版本的快照定義改成 graph。</summary>
    private async Task<(PurchaseRig R, long RecordId, long InstanceId, ApprovalTask Task)> BoundGraphInstance()
    {
        var r = await Scenario3();
        As("buyer1");
        var po = await NewPo(r, price: 150000);
        var id = (await MustSubmit(po.Id, r.Confirmed)).InstanceId!.Value;
        var inst = Db.Context.Set<ApprovalInstance>().Single(i => i.Id == id);
        // po-a 的條件不成立，同意後的重新比對會改選到 sequence 的 po-b（舊程式的 NewFlow 守門放行）
        inst.FlowSetSnapshot = inst.FlowSetSnapshot
            .Select(e => e.FlowVersionId == inst.FlowVersionId ? e with { Definition = Graph(Cond(r.Total, "gte", 99999999)) } : e).ToList();
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
        return (r, po.Id, id, TasksOf(id).Single(t => t.Status == ApprovalTaskStatus.Open));
    }

    private sealed record Snapshot(
        ApprovalInstance Instance, List<ApprovalTask> Tasks, List<ApprovalStageRun> Runs, List<ApprovalAction> Actions, string RecordRowVersion, long? RecordState);

    private Snapshot Capture(PurchaseRig r, long recordId, long instanceId) => new(
        Inst(instanceId), TasksOf(instanceId), Db.Context.Set<ApprovalStageRun>().AsNoTracking().Where(x => x.InstanceId == instanceId).OrderBy(x => x.Id).ToList(),
        ActionsOf(instanceId), Rec(recordId).Row.RowVersion, StateOf(r, recordId));

    private static string Json(object o) => JsonSerializer.Serialize(o);

    private void AssertRejectedUntouched(ServiceResult res, Snapshot before, PurchaseRig r, long recordId, long instanceId)
    {
        res.IsConflict.Should().BeTrue(Dump(res));
        res.Message.Should().Be(MessageFor("po-a"));
        var after = Capture(r, recordId, instanceId);
        // JsonElement 逐屬性比對會誤判，整份序列化成字串比（含實例的 RowVersion、Status）
        Json(after.Instance).Should().Be(Json(before.Instance));
        Json(after.Tasks).Should().Be(Json(before.Tasks));
        Json(after.Runs).Should().Be(Json(before.Runs));
        Json(after.Actions).Should().Be(Json(before.Actions));
        (after.RecordRowVersion, after.RecordState).Should().Be((before.RecordRowVersion, before.RecordState));
    }

    [Fact]
    public async Task BoundGraphInstance_RejectToInitiator_409_Untouched()
    {
        var (r, recordId, id, task) = await BoundGraphInstance();
        var before = Capture(r, recordId, id);
        As(task.AssigneeUserId);

        var res = await Send(new RejectTaskRequest(task.Id, before.Instance.RowVersion, "退回", RejectTaskHandler.ToInitiator));

        AssertRejectedUntouched(res, before, r, recordId, id);
    }

    [Fact]
    public async Task BoundGraphInstance_ApproveRecheckSelectsSequence_409_Untouched()
    {
        var (r, recordId, id, task) = await BoundGraphInstance();
        var before = Capture(r, recordId, id);
        As(task.AssigneeUserId);

        var res = await Send(new ApproveTaskRequest(task.Id, before.Instance.RowVersion, null, null));

        AssertRejectedUntouched(res, before, r, recordId, id);
    }

    [Fact]
    public async Task BoundGraphInstance_OtherActions_409_Untouched()
    {
        var (r, recordId, id, task) = await BoundGraphInstance();
        var before = Capture(r, recordId, id);
        var rv = before.Instance.RowVersion;

        As(task.AssigneeUserId);
        AssertRejectedUntouched(await Send(new TransferTaskRequest(task.Id, rv, "emp2", null)), before, r, recordId, id);
        AssertRejectedUntouched(await Send(new AddSignTaskRequest(task.Id, rv, null, null, ["emp2"], ApprovalModes.Any)), before, r, recordId, id);
        As("buyer1");
        AssertRejectedUntouched(await Send(new WithdrawApprovalRequest(id, rv, null)), before, r, recordId, id);
        As("admin");
        AssertRejectedUntouched(await Send(new CancelApprovalRequest(id, rv, "終止")), before, r, recordId, id);
        AssertRejectedUntouched(await Send(new ReassignTaskRequest(task.Id, rv, "emp2", null)), before, r, recordId, id);
    }
}
