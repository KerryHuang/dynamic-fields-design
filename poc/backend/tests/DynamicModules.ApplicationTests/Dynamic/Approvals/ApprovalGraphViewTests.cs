using System.Text.Json;
using System.Text.Json.Serialization;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetRecord;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.ApplicationTests.Dynamic.Approvals.ApprovalGraphTestKit;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>
/// 流程圖實例的檢視與簽核中心（計畫 fg2 Task 8；U5、U6、J24；設計 §8.5）。
/// 簽核人：a、a1＝fin1、a2＝fin2、b＝buyer2、c＝gm；發起人 buyer1。
/// </summary>
public class ApprovalGraphViewTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    // ───────── 圖與情境 ─────────

    /// <summary>start → fork f → 分支 a（fin1）、b（buyer2）→ join j → c（gm）→ end；aSource、aMode 是 a 的簽核人與會簽／或簽，aEditable 是 a 的可編輯欄位。</summary>
    private static ApprovalDefinition ForkJoin(string joinMode = ApprovalModes.All, ApproverSource? aSource = null, string aMode = ApprovalModes.Any,
        List<long>? aEditable = null) => G(
        [Start(), Fork("f"), Approval("a", aSource ?? Users("fin1"), mode: aMode, editable: aEditable), Approval("b", Users("buyer2")), Join("j", "f", joinMode),
         Approval("c", Users("gm")), End()],
        [E("e0", "start", "f"), E("fa", "f", "a"), E("fb", "f", "b"), E("aj", "a", "j"), E("bj", "b", "j"), E("jc", "j", "c"), E("ce", "c", "end")]);

    /// <summary>分支 fa 內連續兩個簽核 a1（fin1）→ a2（fin2），分支 fb 是 b（buyer2）；全部到齊後 c（gm）。</summary>
    private static ApprovalDefinition BranchSerial() => G(
        [Start(), Fork("f"), Approval("a1", Users("fin1")), Approval("a2", Users("fin2")), Approval("b", Users("buyer2")), Join("j", "f"),
         Approval("c", Users("gm")), End()],
        [E("e0", "start", "f"), E("fa", "f", "a1"), E("fb", "f", "b"), E("a12", "a1", "a2"), E("a2j", "a2", "j"), E("bj", "b", "j"),
         E("jc", "j", "c"), E("ce", "c", "end")]);

    /// <summary>start → s0（mgrA，根段）→ fork f → 分支 a（fin1）、b（buyer2）→ join j → c（gm）→ end：根段在匯合之前有已同意的簽核節點。</summary>
    private static ApprovalDefinition RootThenFork() => G(
        [Start(), Approval("s0", Users("mgrA")), Fork("f"), Approval("a", Users("fin1")), Approval("b", Users("buyer2")), Join("j", "f"),
         Approval("c", Users("gm")), End()],
        [E("e0", "start", "s0"), E("sf", "s0", "f"), E("fa", "f", "a"), E("fb", "f", "b"), E("aj", "a", "j"), E("bj", "b", "j"),
         E("jc", "j", "c"), E("ce", "c", "end")]);

    private async Task<PurchaseRig> Rig(Func<PurchaseRig, ApprovalDefinition> graph)
    {
        var r = await BuildPurchase();
        await SeedGraphFlow(r, "po-g", r.Confirmed, 1, graph(r));
        (await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed), Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received),
            Plain(r.Confirmed, r.Cancelled)])).IsSuccess.Should().BeTrue();
        await GrantEveryone(r.Module, r.Extra);
        return r;
    }

    private async Task<(PurchaseRig R, long Id)> Boot(Func<PurchaseRig, ApprovalDefinition> graph)
    {
        var r = await Rig(graph);
        As("buyer1");
        var po = await NewPo(r, price: 5000);
        return (r, (await MustSubmit(po.Id, r.Confirmed)).InstanceId!.Value);
    }

    private Task<(PurchaseRig R, long Id)> Boot(ApprovalDefinition graph) => Boot(_ => graph);

    private ApprovalTask Open(long id, string user) => TasksOf(id).Single(t => t.AssigneeUserId == user && t.Status == ApprovalTaskStatus.Open);

    private async Task MustApprove(long id, string user)
    {
        As(user);
        var res = await Send(new ApproveTaskRequest(Open(id, user).Id, Inst(id).RowVersion, null, null));
        res.IsSuccess.Should().BeTrue(Dump(res));
    }

    private async Task MustAddSign(long id, string user, string to)
    {
        As(user);
        var res = await Send(new AddSignTaskRequest(Open(id, user).Id, Inst(id).RowVersion, null, null, [to], ApprovalModes.Any));
        res.IsSuccess.Should().BeTrue(Dump(res));
    }

    private async Task<ApprovalInstanceView> ViewAs(long id, string user)
    {
        As(user);
        var res = await Send(new GetRecordApprovalRequest(Inst(id).RecordId));
        res.IsSuccess.Should().BeTrue(Dump(res));
        var all = (RecordApprovalResponse)res.Data!;
        return new[] { all.Current }.Concat(all.Previous).OfType<ApprovalInstanceView>().Single(v => v.Id == id);
    }

    private async Task<ApprovalTaskView> TaskViewOf(long id, string viewer, string assignee)
    {
        var taskId = Open(id, assignee).Id;
        return (await ViewAs(id, viewer)).Stages.SelectMany(s => s.Tasks).Single(t => t.Id == taskId);
    }

    private async Task<PagedResponse<ApprovalInboxItem>> Inbox(string user, string box)
    {
        As(user);
        var res = await Send(new GetApprovalTasksRequest(box, null, null, null, 1, 50));
        res.IsSuccess.Should().BeTrue(Dump(res));
        return (PagedResponse<ApprovalInboxItem>)res.Data!;
    }

    /// <summary>把實例綁定的定義換成缺預設線的壞圖（繞過驗證寫入）；節點實例與任務不動。</summary>
    private async Task BreakDefinition(PurchaseRig r, long id)
    {
        var inst = Db.Context.Set<ApprovalInstance>().Single(i => i.Id == id);
        var broken = G([Start(), Condition("c"), Approval("a", Users("fin1")), End()],
            [E("e0", "start", "c"), E("ca", "c", "a", Cond(r.Total, "gte", 1), order: 1), E("cb", "c", "end", Cond(r.Total, "gte", 2), order: 2), E("ae", "a", "end")]);
        inst.FlowSetSnapshot = [inst.FlowSetSnapshot.Single() with { Definition = broken }];
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
    }

    // ───────── 檢視：關卡列 ─────────

    [Fact]
    public async Task View_Graph_ListsOnlyApprovalAndAddSignRuns()
    {
        // U6：只放簽核節點與加簽；開始、分支、匯合、結束不放；每筆帶 nodeId、branchKey（J24）
        var (_, id) = await Boot(ForkJoin());
        await MustAddSign(id, "fin1", "emp1");
        await MustApprove(id, "buyer2");
        await MustApprove(id, "emp1");   // 兩條分支都到匯合，c 啟動；單據上有開始、分支、匯合的節點實例

        var view = await ViewAs(id, "buyer1");

        view.Stages.Select(s => (s.NodeId, s.BranchKey, s.StageCode)).Should().Equal(
            ("a", "fa", "a"), ("b", "fb", "b"), ("a", "fa", null), ("c", "", "c"));
        view.Stages.Select(s => s.Seq).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task View_Graph_CancelledRunHasCloseReason()
    {
        var (_, id) = await Boot(ForkJoin(ApprovalModes.Any));
        await MustApprove(id, "fin1");   // 任一到齊：b 被取消

        var view = await ViewAs(id, "buyer1");

        var b = view.Stages.Single(s => s.NodeId == "b");
        b.Status.Should().Be(ApprovalStageStatus.Closed);
        b.CloseReason.Should().Be(ApprovalCloseReasons.BranchCancelled);
        view.Stages.Single(s => s.NodeId == "a").CloseReason.Should().BeNull();
    }

    [Fact]
    public async Task View_CanReturnPrevious_MatchesHandler()
    {
        // 檢視的 canReturnPrevious 與 handler 同一個判斷：true 的退回上一關成功，false 的回 400
        var (_, id) = await Boot(BranchSerial());
        (await TaskViewOf(id, "buyer1", "fin1")).CanReturnPrevious.Should().BeFalse();   // 分支內第一個簽核：沒有上一關
        (await TaskViewOf(id, "buyer1", "buyer2")).CanReturnPrevious.Should().BeFalse();
        As("buyer2");
        var none = await Send(new RejectTaskRequest(Open(id, "buyer2").Id, Inst(id).RowVersion, "請重看", RejectTaskHandler.ToPrevious));
        none.Errors["to"].Should().Equal(ApprovalMessages.NoPreviousStage.Resolve(null));

        await MustApprove(id, "fin1");
        (await TaskViewOf(id, "buyer1", "fin2")).CanReturnPrevious.Should().BeTrue();   // a1 已同意，a2 的上一關是 a1
        As("fin2");
        var ok = await Send(new RejectTaskRequest(Open(id, "fin2").Id, Inst(id).RowVersion, "請重看", RejectTaskHandler.ToPrevious));
        ok.IsSuccess.Should().BeTrue(Dump(ok));
        Open(id, "fin1").Status.Should().Be(ApprovalTaskStatus.Open);
    }

    [Fact]
    public async Task View_CanReturnPrevious_FirstAfterJoin_False_HandlerRejects()
    {
        var (_, id) = await Boot(ForkJoin());
        await MustApprove(id, "fin1");
        await MustApprove(id, "buyer2");   // 匯合後的 c 沒有上一關（不跨過匯合）

        (await TaskViewOf(id, "buyer1", "gm")).CanReturnPrevious.Should().BeFalse();
        As("gm");
        var res = await Send(new RejectTaskRequest(Open(id, "gm").Id, Inst(id).RowVersion, "請重看", RejectTaskHandler.ToPrevious));
        res.Errors["to"].Should().Equal(ApprovalMessages.NoPreviousStage.Resolve(null));
    }

    [Fact]
    public async Task View_CanReturnPrevious_FirstAfterJoin_WithRootApproval_False_HandlerRejects()
    {
        // 審查 fg2d S1：根段有已同意的 s0 時，匯合後的 c 仍沒有上一關；檢視的上一關要用含匯合列的未過濾節點實例，
        // 過濾成只剩簽核列就會越過匯合找到 s0，檢視回 true、handler 卻拒絕
        var (_, id) = await Boot(RootThenFork());
        await MustApprove(id, "mgrA");
        await MustApprove(id, "fin1");
        await MustApprove(id, "buyer2");

        (await TaskViewOf(id, "buyer1", "gm")).CanReturnPrevious.Should().BeFalse();
        As("gm");
        var res = await Send(new RejectTaskRequest(Open(id, "gm").Id, Inst(id).RowVersion, "請重看", RejectTaskHandler.ToPrevious));
        res.IsSuccess.Should().BeFalse();
        res.Errors["to"].Should().Equal(ApprovalMessages.NoPreviousStage.Resolve(null));
    }

    [Fact]
    public async Task View_CanReturnPrevious_BrokenGraph_False_HandlerConflicts()
    {
        // 結構不合法：退回上一關要讀圖，檢視回 false，handler 回 409（U11）
        var (r, id) = await Boot(BranchSerial());
        await MustApprove(id, "fin1");
        await BreakDefinition(r, id);

        (await TaskViewOf(id, "buyer1", "fin2")).CanReturnPrevious.Should().BeFalse();
        As("fin2");
        var res = await Send(new RejectTaskRequest(Open(id, "fin2").Id, Inst(id).RowVersion, "請重看", RejectTaskHandler.ToPrevious));
        res.IsConflict.Should().BeTrue(Dump(res));
        res.Message.Should().Be(ApprovalMessages.GraphRuntimeInvalid.Resolve(null));
    }

    [Fact]
    public async Task View_CanWithdraw_MatchesHandler()
    {
        // 資格同 handler：每個 active 的簽核節點都還沒有同意或退回的任務；還沒有人動作時檢視與 handler 都放行
        var (_, fresh) = await Boot(ForkJoin());
        (await ViewAs(fresh, "buyer1")).CanWithdraw.Should().BeTrue();
        As("buyer1");
        (await Send(new WithdrawApprovalRequest(fresh, Inst(fresh).RowVersion, null))).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task View_CanWithdraw_PartialApprovalInAllNode_False_HandlerConflicts()
    {
        var (_, partial) = await Boot(ForkJoin(aSource: Users("fin1", "fin2"), aMode: ApprovalModes.All));
        await MustApprove(partial, "fin1");   // a 是會簽，fin1 同意了一席；a 仍是 active；current（Seq 最小的 active）正好是 a
        (await ViewAs(partial, "buyer1")).CanWithdraw.Should().BeFalse();
        As("buyer1");
        var denied = await Send(new WithdrawApprovalRequest(partial, Inst(partial).RowVersion, null));
        denied.IsConflict.Should().BeTrue();
        denied.Message.Should().Be(ApprovalMessages.CannotWithdraw.Resolve(null));
    }

    [Fact]
    public async Task View_CanWithdraw_ApprovalInLaterBranch_False()
    {
        // 已同意的簽核人在 Seq 較大的分支，current 是另一條分支的節點：檢視仍要回 false（第一版只看目前關卡會回 true）
        var def = G(
            [Start(), Fork("f"), Approval("a", Users("buyer2")), Approval("b", Users("fin1", "fin2"), mode: ApprovalModes.All), Join("j", "f"), End()],
            [E("e0", "start", "f"), E("fa", "f", "a"), E("fb", "f", "b"), E("aj", "a", "j"), E("bj", "b", "j"), E("je", "j", "end")]);
        var (_, id) = await Boot(def);
        await MustApprove(id, "fin1");
        Inst(id).CurrentStageRunId.Should().Be(Db.Context.Set<ApprovalStageRun>().AsNoTracking().Single(x => x.InstanceId == id && x.NodeId == "a").Id);

        (await ViewAs(id, "buyer1")).CanWithdraw.Should().BeFalse();
        As("buyer1");
        (await Send(new WithdrawApprovalRequest(id, Inst(id).RowVersion, null))).IsConflict.Should().BeTrue();
    }

    [Fact]
    public async Task View_EditableKeysFromApprovalNode()
    {
        // 可編輯欄位讀簽核節點（Steps），不是 def.Stages（graph 的 Stages 是空的）
        var (r, id) = await Boot(r0 => ForkJoin(aEditable: [r0.DueDate]));

        var view = await ViewAs(id, "fin1");

        view.Stages.Single(s => s.NodeId == "a").EditableFieldKeys.Should().Equal($"f_{r.DueDate}");
        view.Stages.Single(s => s.NodeId == "b").EditableFieldKeys.Should().BeEmpty();   // 別條分支的節點，fin1 在那裡不能動作
    }

    [Fact]
    public async Task View_BranchCancelledAction_DataHasNameI18n()
    {
        // 歷程的 data 帶 name（I18nText 原值）、forkId、joinId、arrived、cancelled，前端依語系組字
        var (_, id) = await Boot(ForkJoin(ApprovalModes.Any));
        await MustApprove(id, "fin1");

        var view = await ViewAs(id, "buyer1");

        var action = view.Actions.Single(a => a.Action == ApprovalActions.BranchCancelled);
        var data = action.Data!.Value;
        data.GetProperty("name").GetProperty("zh-TW").GetString().Should().Be("a");
        data.GetProperty("forkId").GetString().Should().Be("f");
        data.GetProperty("joinId").GetString().Should().Be("j");
        data.GetProperty("arrived").GetString().Should().Be("fa");
        data.GetProperty("cancelled").EnumerateArray().Select(e => e.GetString()).Should().Equal("fb");
    }

    [Fact]
    public async Task View_Sequence_JsonHasNoNodeFields()
    {
        // sequence 的回應 JSON 不變：序列化（同 WebAPI 的選項，null 不寫出）後不含 nodeId、branchKey、closeReason
        var r = await Scenario3();
        As("buyer1");
        var po = await NewPo(r, price: 150000);
        var id = (await MustSubmit(po.Id, r.Confirmed)).InstanceId!.Value;
        await MustApprove(id, "gm");
        var view = await ViewAs(id, "buyer1");
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

        var json = JsonSerializer.Serialize(view, web);

        json.Should().Contain("\"stageCode\"").And.NotContain("nodeId").And.NotContain("branchKey").And.NotContain("closeReason");
    }

    [Fact]
    public async Task View_Graph_JsonHasNodeFields_CloseReasonOnlyWhenSet()
    {
        var (_, id) = await Boot(ForkJoin(ApprovalModes.Any));
        await MustApprove(id, "fin1");
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

        var stages = JsonSerializer.SerializeToElement(await ViewAs(id, "buyer1"), web).GetProperty("stages").EnumerateArray().ToList();

        stages.All(s => s.TryGetProperty("nodeId", out _) && s.TryGetProperty("branchKey", out _)).Should().BeTrue();
        stages.Single(s => s.GetProperty("nodeId").GetString() == "a").TryGetProperty("closeReason", out _).Should().BeFalse();
        stages.Single(s => s.GetProperty("nodeId").GetString() == "b").GetProperty("closeReason").GetString().Should().Be("branchCancelled");
    }

    // ───────── 簽核中心 ─────────

    [Fact]
    public async Task Inbox_Todo_Graph_ShowsOwnNodeName()
    {
        // U5：待簽列取任務所在節點的名稱，並行時不會顯示另一條分支的節點（Seq 最小的 active 是 a）
        var (_, id) = await Boot(ForkJoin());

        (await Inbox("fin1", ApprovalInboxBoxes.Todo)).Items.Single().StageName!.Resolve("zh-TW").Should().Be("a");
        (await Inbox("buyer2", ApprovalInboxBoxes.Todo)).Items.Single().StageName!.Resolve("zh-TW").Should().Be("b");
        Inst(id).CurrentStageRunId.Should().NotBeNull();
    }

    [Fact]
    public async Task Inbox_Submitted_Graph_ShowsMinSeqActiveName()
    {
        // 「我送出的」照舊取實例的目前關卡（graph 是 Seq 最小的 active，J15）
        await Boot(ForkJoin());

        (await Inbox("buyer1", ApprovalInboxBoxes.Submitted)).Items.Single().StageName!.Resolve("zh-TW").Should().Be("a");
    }

    [Fact]
    public async Task Inbox_Done_Graph_ShowsCurrentNotOwnNode()
    {
        // 審查 fg2d S2：U5 只改「待簽」；「已處理」照舊取實例的目前關卡（Seq 最小的 active 是 b），不是自己處理過的 a
        var (_, id) = await Boot(ForkJoin());
        await MustApprove(id, "fin1");

        (await Inbox("fin1", ApprovalInboxBoxes.Done)).Items.Single().StageName!.Resolve("zh-TW").Should().Be("b");
    }

    [Fact]
    public async Task Inbox_Todo_CancelledTaskNotListed()
    {
        // 被任一到齊取消的任務是 closed，不出現在待簽
        var (_, id) = await Boot(ForkJoin(ApprovalModes.Any));
        (await Inbox("buyer2", ApprovalInboxBoxes.Todo)).Items.Should().ContainSingle();
        await MustApprove(id, "fin1");

        (await Inbox("buyer2", ApprovalInboxBoxes.Todo)).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Inbox_Sequence_Unchanged()
    {
        // sequence：待簽任務一定在目前關卡，名稱與「我送出的」相同
        var r = await Scenario3();
        As("buyer1");
        var po = await NewPo(r, price: 150000);
        var id = (await MustSubmit(po.Id, r.Confirmed)).InstanceId!.Value;
        var current = Db.Context.Set<ApprovalStageRun>().AsNoTracking().Single(x => x.Id == Inst(id).CurrentStageRunId).NameSnapshot!.Resolve("zh-TW");

        (await Inbox("gm", ApprovalInboxBoxes.Todo)).Items.Single().StageName!.Resolve("zh-TW").Should().Be(current).And.Be("mgr");
        (await Inbox("buyer1", ApprovalInboxBoxes.Submitted)).Items.Single().StageName!.Resolve("zh-TW").Should().Be("mgr");
    }

    [Fact]
    public async Task RecordSummary_Graph_StageSeqCountsApprovalNodesOnly()
    {
        // 標頭「第 N 關」：graph 的 Seq 含開始、分支、匯合等結構節點，序號改成簽核節點的順位，與時間軸一致；sequence 不變（GetRecord_HasApprovalSummary）
        var (r, id) = await Boot(ForkJoin());
        var recordId = Inst(id).RecordId;
        async Task<RecordApprovalSummary> Summary() => ((DynamicRecordResponse)(await Send(new GetRecordRequest(recordId))).Data!).Approval!;
        int SeqOf(long inst, string node) => Db.Context.Set<ApprovalStageRun>().AsNoTracking().Single(x => x.InstanceId == inst && x.NodeId == node).Seq;
        As("buyer1");

        var first = await Summary();
        first.StageName!.Resolve("zh-TW").Should().Be("a");
        first.StageSeq.Should().Be(1);                         // a：開始、分支不算；run.Seq 本身是 3
        SeqOf(id, "a").Should().BeGreaterThan(1);
        await MustApprove(id, "fin1");
        await MustApprove(id, "buyer2");

        var last = await Summary();
        last.StageName!.Resolve("zh-TW").Should().Be("c");
        last.StageSeq.Should().Be(3);                          // c：a、b、c 三個簽核節點的第三個；匯合節點不算
        (await ViewAs(id, "buyer1")).Stages.Select(s => s.NodeId).Should().Equal("a", "b", "c");
        SeqOf(id, "c").Should().BeGreaterThan(3);
    }
}
