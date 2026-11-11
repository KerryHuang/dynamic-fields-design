using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Record = DynamicModules.Domain.Entities.Dynamic.Record;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>每次同意都重新比對完整路線（設計 §5.5；§17.5 第一輪 3、4，第二輪 5、6，第三輪 3、7）。</summary>
public class ApprovalRerouteTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    private ApprovalTask Open(long instanceId, string user) =>
        TasksOf(instanceId).Single(t => t.AssigneeUserId == user && t.Status == ApprovalTaskStatus.Open);

    private Task<ServiceResult> Approve(long instanceId, string user)
    {
        As(user);
        return Send(new ApproveTaskRequest(Open(instanceId, user).Id, Inst(instanceId).RowVersion, null, null));
    }

    private static ApprovalActionResponse Body(ServiceResult r) => (ApprovalActionResponse)r.Data!;

    private async Task<(long RecordId, long InstanceId)> SubmitAs(PurchaseRig r, string user, decimal total)
    {
        As(user);
        var po = await NewPo(r, price: total);
        return (po.Id, (await MustSubmit(po.Id, r.Confirmed)).InstanceId!.Value);
    }

    /// <summary>簽核中由其他模組（追加費用）讓彙總變動：允許的靜默更新（設計 §8.5）。</summary>
    private async Task AddExtra(PurchaseRig r, long poId, decimal amount)
    {
        As("buyer1");
        var title = TitleId(r.Extra);
        var res = await Send(new CreateRecordRequest(r.Extra, V((title, "追加"), (r.ExtraRef, poId), (r.ExtraAmount, amount))));
        res.IsCreated.Should().BeTrue(Dump(res));
    }

    private ApprovalInstance? PendingOf(long recordId) =>
        Db.Context.Set<ApprovalInstance>().AsNoTracking().SingleOrDefault(i => i.RecordId == recordId && i.Status == ApprovalInstanceStatus.Pending);

    private List<ApprovalStageRun> RunsOf(long instanceId) =>
        Db.Context.Set<ApprovalStageRun>().AsNoTracking().Where(x => x.InstanceId == instanceId).OrderBy(x => x.Seq).ToList();

    [Fact]
    public async Task AddOnRaisesTotal_Reroutes_ToFlowA()
    {
        var r = await Scenario3();
        var (po, id) = await SubmitAs(r, "buyer1", 50000);
        Inst(id).FlowId.Should().Be(Flow("po-b").Id);
        await AddExtra(r, po, 120000);

        var res = await Approve(id, "buyer2");
        res.IsSuccess.Should().BeTrue(Dump(res));
        Body(res).Outcome.Should().Be(ApprovalOutcomes.Rerouted);
        Body(res).NewFlowName!.Resolve("zh-TW").Should().Be("po-a");

        var old = Inst(id);
        old.Status.Should().Be(ApprovalInstanceStatus.Rerouted);
        TasksOf(id).Should().OnlyContain(t => t.Status != ApprovalTaskStatus.Open);
        TasksOf(id).Should().NotContain(t => t.Status == ApprovalTaskStatus.Approved);   // 這次同意不計入
        var reroute = ActionsOf(id).Single(a => a.Action == ApprovalActions.Reroute);
        reroute.Comment.Should().Be("單據內容已變，改用流程「po-a」重新簽核");

        var fresh = PendingOf(po)!;
        fresh.FlowId.Should().Be(Flow("po-a").Id);
        fresh.RestartedFromInstanceId.Should().Be(id);
        fresh.InitiatorUserId.Should().Be("buyer1");
        Body(res).InstanceId.Should().Be(fresh.Id);
        Body(res).InstanceRowVersion.Should().Be(fresh.RowVersion);
        RunsOf(fresh.Id).Should().ContainSingle().Which.StageCode.Should().Be("mgr");
        Open(fresh.Id, "gm").Should().NotBeNull();

        Notices("buyer1", NotificationKinds.ApprovalRerouted).Should().Be(1);
        Notices("buyer2", NotificationKinds.ApprovalRerouted).Should().Be(1);
        StateOf(r, po).Should().Be(r.Draft);
    }

    [Fact]
    public async Task Discount_NoFlowMatches_FallbackInstance()
    {
        var r = await Scenario3();
        var (po, id) = await SubmitAs(r, "buyer1", 50000);
        await AddExtra(r, po, -45000);

        var res = await Approve(id, "buyer2");
        Body(res).Outcome.Should().Be(ApprovalOutcomes.Rerouted);
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Rerouted);
        var fresh = PendingOf(po)!;
        fresh.RouteKind.Should().Be(ApprovalRouteKind.Fallback);
        RunsOf(fresh.Id).Should().ContainSingle().Which.Mode.Should().Be(ApprovalModes.Any);
        var task = Open(fresh.Id, "admin");   // 流程 B 沒設備援 → 系統管理員
        task.Reason.Should().Be(ApprovalTaskReason.Fallback);
        StateOf(r, po).Should().Be(r.Draft);   // 不會直接確認

        var done = await Approve(fresh.Id, "admin");
        Body(done).Outcome.Should().Be(ApprovalOutcomes.Approved);
        Inst(fresh.Id).Status.Should().Be(ApprovalInstanceStatus.Approved);
        StateOf(r, po).Should().Be(r.Confirmed);
    }

    [Fact]
    public async Task Fallback_KeepsSnapshot()
    {
        var r = await Scenario3();
        var (po, id) = await SubmitAs(r, "buyer1", 50000);
        // 送出後才新增的流程不在快照裡：重來時不能被選到
        await SeedFlow(r, "po-z", r.Confirmed, 0, Def(Cond(r.Total, "gte", 1000), Stage("lead", Group(PurchaseLeadGroup))));
        await AddExtra(r, po, -45000);

        Body(await Approve(id, "buyer2")).Outcome.Should().Be(ApprovalOutcomes.Rerouted);
        var fresh = PendingOf(po)!;
        fresh.RouteKind.Should().Be(ApprovalRouteKind.Fallback);
        JsonSerializer.Serialize(fresh.FlowSetSnapshot).Should().Be(JsonSerializer.Serialize(Inst(id).FlowSetSnapshot));
        Db.Context.Set<ApprovalInstanceFlow>().AsNoTracking().Where(x => x.InstanceId == fresh.Id).Select(x => x.FlowId).ToList()
            .Should().BeEquivalentTo(Db.Context.Set<ApprovalInstanceFlow>().AsNoTracking().Where(x => x.InstanceId == id).Select(x => x.FlowId).ToList());
    }

    [Fact]
    public async Task Fallback_ApproveWhenSnapshotFlowMatchesAgain_Reroutes()
    {
        // 備援簽核人同意時同樣在快照裡重新比對：又有流程符合就改走那個流程（設計 §5.5 第 5 點）
        var r = await Scenario3();
        var (po, id) = await SubmitAs(r, "buyer1", 50000);
        await AddExtra(r, po, -45000);
        await Approve(id, "buyer2");
        var fb = PendingOf(po)!;
        await AddExtra(r, po, 100000);   // 總額 105000 → 流程 A

        var res = await Approve(fb.Id, "admin");
        Body(res).Outcome.Should().Be(ApprovalOutcomes.Rerouted);
        var fresh = PendingOf(po)!;
        fresh.FlowId.Should().Be(Flow("po-a").Id);
        fresh.RouteKind.Should().Be(ApprovalRouteKind.Flow);
        fresh.RestartedFromInstanceId.Should().Be(fb.Id);
        StateOf(r, po).Should().Be(r.Draft);
    }

    [Fact]
    public async Task SkippedStageBecomesTrue_Reroutes()
    {
        var r = await BuildPurchase();
        await SeedFlow(r, "po-p", r.Confirmed, 1, Def(Cond(r.Total, "gte", 10000),
            Stage("board", Group(BoardGroup), when: Cond(r.Total, "gt", 50000)), Stage("lead", Group(PurchaseLeadGroup))));
        (await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed), Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received),
            Plain(r.Confirmed, r.Cancelled)])).IsSuccess.Should().BeTrue();
        await GrantEveryone(r.Module, r.Extra);
        var (po, id) = await SubmitAs(r, "buyer1", 40000);
        RunsOf(id).Select(x => x.Status).Should().Equal(ApprovalStageStatus.Skipped, ApprovalStageStatus.Active);
        await AddExtra(r, po, 20000);

        var res = await Approve(id, "buyer2");
        Body(res).Outcome.Should().Be(ApprovalOutcomes.Rerouted);
        var fresh = PendingOf(po)!;
        fresh.FlowId.Should().Be(Flow("po-p").Id);
        RunsOf(fresh.Id).Should().ContainSingle().Which.StageCode.Should().Be("board");
        Open(fresh.Id, "gm").Should().NotBeNull();
    }

    [Fact]
    public async Task ApprovedStageConditionBecomesFalse_NotRerouted()
    {
        // 前面已同意的關卡條件變成不成立，不算不同（多簽不算少簽）
        var r = await BuildPurchase();
        await SeedFlow(r, "po-p", r.Confirmed, 1, Def(null,
            Stage("board", Group(BoardGroup), when: Cond(r.Total, "gt", 50000)), Stage("lead", Group(PurchaseLeadGroup))));
        (await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed), Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received),
            Plain(r.Confirmed, r.Cancelled)])).IsSuccess.Should().BeTrue();
        await GrantEveryone(r.Module, r.Extra);
        var (po, id) = await SubmitAs(r, "buyer1", 60000);
        Body(await Approve(id, "gm")).Outcome.Should().Be(ApprovalOutcomes.Advanced);
        await AddExtra(r, po, -20000);
        Body(await Approve(id, "buyer2")).Outcome.Should().Be(ApprovalOutcomes.Approved);
    }

    [Fact]
    public async Task DisableOrNewVersion_FollowsSnapshot()
    {
        var r = await Scenario3();
        var (_, id) = await SubmitAs(r, "buyer1", 150000);
        var a = Flow("po-a");
        // 停用 A、A 改版（≥ 200000）、新增優先序 0 的 Z、B 上移到最前面：快照都不看
        await Db.Context.Set<ApprovalFlow>().Where(f => f.Id == a.Id).ExecuteUpdateAsync(s => s.SetProperty(f => f.Active, false));
        var v2 = new ApprovalFlowVersion
        {
            FlowId = a.Id, Version = 2, Definition = Def(Cond(r.Total, "gte", 200000), Stage("mgr", Manager(1))), CreatedBy = "admin", CreatedTime = SeedTime
        };
        Db.Context.Add(v2);
        await Db.Context.SaveChangesAsync();
        await Db.Context.Set<ApprovalFlow>().Where(f => f.Id == a.Id).ExecuteUpdateAsync(s => s.SetProperty(f => f.CurrentVersionId, v2.Id));
        await Db.Context.Set<ApprovalFlow>().Where(f => f.Key == "po-b").ExecuteUpdateAsync(s => s.SetProperty(f => f.Priority, -1));
        await SeedFlow(r, "po-z", r.Confirmed, 0, Def(Cond(r.Total, "gte", 1000), Stage("lead", Group(PurchaseLeadGroup))));
        Db.Context.ChangeTracker.Clear();

        var res = await Approve(id, "gm");
        Body(res).Outcome.Should().Be(ApprovalOutcomes.Advanced);
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Pending);
        TasksOf(id).Where(t => t.Status == ApprovalTaskStatus.Open).Select(t => t.AssigneeUserId).Should().BeEquivalentTo(["gm", "buyer2"]);
    }

    [Fact]
    public async Task SnapshotFlowY_EditedAfterSubmit_NewVersion_NotUsed()
    {
        var r = await Scenario3();
        await SeedFlow(r, "po-y", r.Confirmed, 0, Def(Cond(r.Total, "gte", 10000000),
            Stage("mgr", Manager(1)), Stage("board", Group(BoardGroup)), Stage("lead", Group(PurchaseLeadGroup))));
        var (_, id) = await SubmitAs(r, "buyer1", 150000);
        Inst(id).FlowId.Should().Be(Flow("po-a").Id);

        As("admin");
        var upd = await Send(new UpdateApprovalFlowRequest("po-y", new ApprovalFlowInput("po-y", Text("po-y"), r.Confirmed, 0, true,
            Def(Cond(r.Total, "gte", 1000), Stage("lead", Group(PurchaseLeadGroup))))));
        upd.IsSuccess.Should().BeTrue(Dump(upd));
        Db.Context.Set<ApprovalFlowVersion>().AsNoTracking().Count(v => v.FlowId == Flow("po-y").Id).Should().Be(2);

        Body(await Approve(id, "gm")).Outcome.Should().Be(ApprovalOutcomes.Advanced);
        Inst(id).FlowId.Should().Be(Flow("po-a").Id);
    }

    [Fact]
    public async Task NewFlowNeedsPick_NoNewInstance()
    {
        var r = await BuildPurchase();
        await SeedFlow(r, "po-k", r.Confirmed, 1, Def(Cond(r.Total, "gte", 100000),
            Stage("pick", new ApproverSource(ApprovalSources.InitiatorPick, CandidateGroupIds: [BoardGroup], Min: 1, Max: 1))));
        await SeedFlow(r, "po-b", r.Confirmed, 2, Def(Cond(r.Total, "gte", 10000), Stage("lead", Group(PurchaseLeadGroup))));
        (await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed), Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received),
            Plain(r.Confirmed, r.Cancelled)])).IsSuccess.Should().BeTrue();
        await GrantEveryone(r.Module, r.Extra);
        var (po, id) = await SubmitAs(r, "buyer1", 50000);
        await AddExtra(r, po, 120000);

        var res = await Approve(id, "buyer2");
        Body(res).Outcome.Should().Be(ApprovalOutcomes.Rerouted);
        Body(res).NewFlowName!.Resolve("zh-TW").Should().Be("po-k");
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Rerouted);
        PendingOf(po).Should().BeNull();
        Db.Context.Set<ApprovalInstance>().AsNoTracking().Count(i => i.RecordId == po).Should().Be(1);
        Notices("buyer1", NotificationKinds.ApprovalRerouted).Should().Be(1);
        StateOf(r, po).Should().Be(r.Draft);
        As("buyer1");
        (await Put(po, V((r.Memo, "重送前修改")))).IsSuccess.Should().BeTrue();   // 解除簽核中的唯讀
    }

    [Fact]
    public async Task UndecidableFlowCondition_OnRecheck_409_NothingChanged()
    {
        // 快照裡輪到的流程條件解析不了 → fail closed 409（主控裁決 26），不推進、不重來
        var r = await Scenario3();
        var (po, id) = await SubmitAs(r, "buyer1", 50000);
        var inst = Db.Context.Set<ApprovalInstance>().Single(i => i.Id == id);
        var a = inst.FlowSetSnapshot.Single(e => e.FlowId == Flow("po-a").Id);
        inst.FlowSetSnapshot = inst.FlowSetSnapshot.Select(e => e == a ? e with { Definition = e.Definition with { When = Cond(999999, "gte", 1) } } : e).ToList();
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();

        var res = await Approve(id, "buyer2");
        res.IsConflict.Should().BeTrue(Dump(res));
        res.Message.Should().Be(ApprovalMessages.FlowConditionUndecidable(Text("po-a")).Resolve(null));
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Pending);
        Open(id, "buyer2").Status.Should().Be(ApprovalTaskStatus.Open);
    }

    // ── 第三輪 7：用狀態欄位區分入口（送出部分） ──

    private async Task<PurchaseRig> EntryRig()
    {
        var r = await BuildPurchase();
        await SeedFlow(r, "po-a", r.Confirmed, 1, Def(Cond(r.Total, "gte", 100000),
            Stage("mgr", Manager(1)), Stage("board", Group(BoardGroup, PurchaseLeadGroup), mode: ApprovalModes.All)));
        var both = JsonSerializer.SerializeToElement(new
        {
            all = new object[] { new { field = r.Status, op = "eq", value = $"opt_{r.Changing}" }, new { field = r.Total, op = "lt", value = 100000 } }
        });
        await SeedFlow(r, "po-d", r.Confirmed, 2, Def(both, Stage("lead", Group(PurchaseLeadGroup))));
        await SeedFlow(r, "po-b", r.Confirmed, 3, Def(Cond(r.Total, "gte", 10000), Stage("lead", Group(PurchaseLeadGroup))));
        await SeedFlow(r, "po-c", r.Cancelled, 1, Def(null, Stage("board", Group(BoardGroup))));
        var wf = await SaveWorkflow(r, [
            Needs(r.Draft, r.Confirmed, ApprovalNoMatch.Direct), Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received),
            Needs(r.Confirmed, r.Cancelled), Plain(r.Confirmed, r.Changing), Needs(r.Changing, r.Confirmed, ApprovalNoMatch.Direct),
            Plain(r.Changing, r.Draft)
        ], AllStates(r, r.Changing));
        wf.IsSuccess.Should().BeTrue(Dump(wf));
        await GrantEveryone(r.Module, r.Extra);
        return r;
    }

    /// <summary>直接把記錄狀態寫成變更中（不經簽核，模擬已確認後走一般轉移「已確認 → 變更中」）。</summary>
    private async Task MoveTo(PurchaseRig r, long recordId, long state)
    {
        var row = Db.Context.Set<Record>().Single(x => x.Id == recordId);
        var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement?>>(row.ValuesJson)!;
        values[$"f_{r.Status}"] = JsonSerializer.SerializeToElement($"opt_{state}");
        row.ValuesJson = JsonSerializer.Serialize(values);
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
    }

    [Fact]
    public async Task EntryByStatus_FlowD_vs_FlowB_vs_FlowA()
    {
        var r = await EntryRig();
        As("buyer1");
        var changing = await NewPo(r, price: 50000);
        await MoveTo(r, changing.Id, r.Changing);
        var d = await MustSubmit(changing.Id, r.Confirmed);
        Inst(d.InstanceId!.Value).FlowId.Should().Be(Flow("po-d").Id);

        var draft = await NewPo(r, price: 50000);
        var b = await MustSubmit(draft.Id, r.Confirmed);
        Inst(b.InstanceId!.Value).FlowId.Should().Be(Flow("po-b").Id);

        var big = await NewPo(r, price: 120000);
        await MoveTo(r, big.Id, r.Changing);
        var a = (await MustSubmit(big.Id, r.Confirmed)).InstanceId!.Value;
        Inst(a).FlowId.Should().Be(Flow("po-a").Id);
        Body(await Approve(a, "gm")).Outcome.Should().Be(ApprovalOutcomes.Advanced);
        TasksOf(a).Where(t => t.Status == ApprovalTaskStatus.Open).Select(t => t.AssigneeUserId).Should().Contain("gm");
    }
}
