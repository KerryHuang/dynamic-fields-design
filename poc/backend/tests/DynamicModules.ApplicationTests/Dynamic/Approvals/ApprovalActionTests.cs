using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>同意、退回、撤回與最後一關的轉移（設計 §7.2 到 §7.4、§8.2、§8.3；劇本一、三的對應步驟）。</summary>
public class ApprovalActionTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    private ApprovalTask Open(long instanceId, string user) =>
        TasksOf(instanceId).Single(t => t.AssigneeUserId == user && t.Status == ApprovalTaskStatus.Open);

    private Task<ServiceResult> Approve(long instanceId, string user, Dictionary<string, System.Text.Json.JsonElement?>? values = null)
    {
        As(user);
        return Send(new ApproveTaskRequest(Open(instanceId, user).Id, Inst(instanceId).RowVersion, null, values));
    }

    private Task<ServiceResult> Reject(long instanceId, string user, string comment, string to = RejectTaskHandler.ToInitiator)
    {
        As(user);
        return Send(new RejectTaskRequest(Open(instanceId, user).Id, Inst(instanceId).RowVersion, comment, to));
    }

    private static string Outcome(ServiceResult r) => ((ApprovalActionResponse)r.Data!).Outcome;

    private async Task<long> SubmitAs(PurchaseRig r, string user, decimal total)
    {
        As(user);
        var po = await NewPo(r, price: total);
        return (await MustSubmit(po.Id, r.Confirmed)).InstanceId!.Value;
    }

    [Fact]
    public async Task Countersign_TwoTasks_SameUserBothStages_NotSkipped_Withdraw409_ThenApproved()
    {
        var r = await Scenario3();
        var id = await SubmitAs(r, "buyer1", 150000);
        Outcome(await Approve(id, "gm")).Should().Be(ApprovalOutcomes.Advanced);
        TasksOf(id).Where(t => t.Status == ApprovalTaskStatus.Open).Select(t => t.AssigneeUserId).Should().BeEquivalentTo(["gm", "buyer2"]);

        Outcome(await Approve(id, "gm")).Should().Be(ApprovalOutcomes.Advanced);   // 會簽：還差 buyer2
        As("buyer1");
        var withdraw = await Send(new WithdrawApprovalRequest(id, Inst(id).RowVersion, null));
        withdraw.Message.Should().Be(ApprovalMessages.CannotWithdraw.Resolve(null));

        var done = await Approve(id, "buyer2");
        Outcome(done).Should().Be(ApprovalOutcomes.Approved);
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Approved);
        StateOf(r, Inst(id).RecordId).Should().Be(r.Confirmed);
        ActionsOf(id).Select(a => a.Action).Should().EndWith([ApprovalActions.Approve, ApprovalActions.Complete]);
        Notices("buyer1", NotificationKinds.ApprovalApproved).Should().Be(1);
        Rec(Inst(id).RecordId).Row.ModifiedUserId.Should().Be("buyer2");   // 轉移的修改者是按下最後一個同意的人
    }

    [Fact]
    public async Task Approve_SkipsConditionStage_Completes()
    {
        var r = await BuildPurchase();
        await SeedFlow(r, "po-s", r.Confirmed, 1,
            Def(null, Stage("lead", Group(PurchaseLeadGroup)), Stage("board", Group(BoardGroup), when: Cond(r.Total, "gt", 1000000))));
        (await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed), Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received),
            Plain(r.Confirmed, r.Cancelled)])).IsSuccess.Should().BeTrue();
        await GrantEveryone(r.Module);
        var id = await SubmitAs(r, "buyer1", 5000);
        Outcome(await Approve(id, "buyer2")).Should().Be(ApprovalOutcomes.Approved);
        Db.Context.Set<ApprovalStageRun>().AsNoTracking().Where(x => x.InstanceId == id).OrderBy(x => x.Seq)
            .Select(x => new { x.StageCode, x.Status, x.SkipReason }).ToList()
            .Should().Equal(new { StageCode = (string?)"lead", Status = "approved", SkipReason = (string?)null },
                new { StageCode = (string?)"board", Status = "skipped", SkipReason = (string?)"condition" });
        ActionsOf(id).Should().Contain(a => a.Action == ApprovalActions.Skip);
        StateOf(r, Inst(id).RecordId).Should().Be(r.Confirmed);
        Notices("buyer1", NotificationKinds.ApprovalApproved).Should().Be(1);   // 審查測試缺口 7
    }

    [Fact]
    public async Task NextAction_UsesInstanceRowVersionFromPreviousResponse()
    {
        // 每個動作都用上一次回應的 InstanceRowVersion，不重新讀資料庫（RowVersion 修法的回歸；審查測試缺口 5）
        var r = await Scenario3();
        var id = await SubmitAs(r, "buyer1", 150000);
        As("gm");
        var first = (ApprovalActionResponse)(await Send(new ApproveTaskRequest(Open(id, "gm").Id, Inst(id).RowVersion, null, null))).Data!;
        first.InstanceRowVersion.Should().Be(Inst(id).RowVersion);
        var second = await Send(new ApproveTaskRequest(Open(id, "gm").Id, first.InstanceRowVersion, null, null));
        second.IsSuccess.Should().BeTrue(Dump(second));
        var rv2 = ((ApprovalActionResponse)second.Data!).InstanceRowVersion;
        rv2.Should().Be(Inst(id).RowVersion).And.NotBe(first.InstanceRowVersion);
        As("buyer2");
        var last = await Send(new ApproveTaskRequest(Open(id, "buyer2").Id, rv2, null, null));
        Outcome(last).Should().Be(ApprovalOutcomes.Approved);
        ((ApprovalActionResponse)last.Data!).InstanceRowVersion.Should().Be(Inst(id).RowVersion);
    }

    [Fact]
    public async Task AnyStage_OtherTasksClosed_SecondApprove409()
    {
        var r = await BuildPurchase();
        await SeedFlow(r, "po-f", r.Confirmed, 1, Def(null, Stage("fin", Group(FinanceGroup)), Stage("lead", Group(PurchaseLeadGroup))));
        (await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed), Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received),
            Plain(r.Confirmed, r.Cancelled)])).IsSuccess.Should().BeTrue();
        await GrantEveryone(r.Module);
        var id = await SubmitAs(r, "buyer1", 5000);
        var fin2Task = Open(id, "fin2");
        var stale = Inst(id).RowVersion;
        Outcome(await Approve(id, "fin1")).Should().Be(ApprovalOutcomes.Advanced);
        TasksOf(id).Single(t => t.Id == fin2Task.Id).Status.Should().Be(ApprovalTaskStatus.Closed);
        As("fin2");
        var late = await Send(new ApproveTaskRequest(fin2Task.Id, stale, null, null));
        late.IsConflict.Should().BeTrue();
        late.Message.Should().Be("這一關已由他人處理");
    }

    [Fact]
    public async Task Countersign_LateArrival_OwnSeatOpen_RefreshMessage()
    {
        // 會簽兩席：gm 先同意，buyer2 拿舊版本同意 → 409，但他的席位還沒處理，文案不能說「已由他人處理」（審查建議 5）
        var r = await Scenario3();
        var id = await SubmitAs(r, "buyer1", 150000);
        await Approve(id, "gm");
        var buyer2Task = Open(id, "buyer2");
        var stale = Inst(id).RowVersion;
        Outcome(await Approve(id, "gm")).Should().Be(ApprovalOutcomes.Advanced);
        As("buyer2");
        var late = await Send(new ApproveTaskRequest(buyer2Task.Id, stale, null, null));
        late.IsConflict.Should().BeTrue();
        late.Message.Should().Be(ApprovalMessages.StateChanged.Resolve(null));
        var lateReject = await Send(new RejectTaskRequest(buyer2Task.Id, stale, "舊畫面", RejectTaskHandler.ToInitiator));
        lateReject.Message.Should().Be(ApprovalMessages.StateChanged.Resolve(null));
        TasksOf(id).Single(t => t.Id == buyer2Task.Id).Status.Should().Be(ApprovalTaskStatus.Open);
        Outcome(await Approve(id, "buyer2")).Should().Be(ApprovalOutcomes.Approved);   // 重新整理後可以同意
    }

    [Fact]
    public async Task NotActor_403_UnknownTask_404()
    {
        var r = await Scenario3();
        var id = await SubmitAs(r, "buyer1", 50000);
        As("fin1");
        (await Send(new ApproveTaskRequest(Open(id, "buyer2").Id, Inst(id).RowVersion, null, null))).IsForbidden.Should().BeTrue();
        (await Send(new ApproveTaskRequest(999999, "x", null, null))).IsNotFound.Should().BeTrue();
        As("buyer2");
        (await Send(new WithdrawApprovalRequest(id, Inst(id).RowVersion, null))).IsForbidden.Should().BeTrue();
    }

    [Fact]
    public async Task Reject_ToInitiator_RejectToState()
    {
        var r = await BuildPurchase();
        await SeedFlow(r, "po-r", r.Confirmed, 1, Def(null, Stage("lead", Group(PurchaseLeadGroup))) with { RejectTo = r.Returned });
        (await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed), Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received),
            Plain(r.Confirmed, r.Cancelled), Plain(r.Draft, r.Returned)])).IsSuccess.Should().BeTrue();
        await GrantEveryone(r.Module);
        var id = await SubmitAs(r, "buyer1", 5000);
        Outcome(await Reject(id, "buyer2", "請補報價")).Should().Be(ApprovalOutcomes.Rejected);
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Rejected);
        StateOf(r, Inst(id).RecordId).Should().Be(r.Returned);
        Notices("buyer1", NotificationKinds.ApprovalRejected).Should().Be(1);
        ActionsOf(id).Last(a => a.Action == ApprovalActions.Reject).Comment.Should().Be("請補報價");
    }

    [Fact]
    public async Task Reject_BlankOrTooLongComment_400_NothingWritten()
    {
        var r = await Scenario3();
        var id = await SubmitAs(r, "buyer1", 50000);
        var before = Inst(id).RowVersion;
        var actions = ActionsOf(id).Count;
        (await Reject(id, "buyer2", "   ")).Errors["comment"].Should().Equal("退回必須填寫意見");
        (await Reject(id, "buyer2", new string('字', 1001))).Errors["comment"].Should().Equal("意見不能超過 1000 字");
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Pending);
        Inst(id).RowVersion.Should().Be(before);
        ActionsOf(id).Should().HaveCount(actions);
        Open(id, "buyer2").Status.Should().Be(ApprovalTaskStatus.Open);
        StateOf(r, Inst(id).RecordId).Should().Be(r.Draft);
    }

    [Fact]
    public async Task Reject_ToPrevious_ReturnsToLatestApprovedStage_FirstStage400()
    {
        var r = await Scenario3();
        var id = await SubmitAs(r, "buyer1", 150000);
        (await Reject(id, "gm", "第一關沒有上一關", RejectTaskHandler.ToPrevious)).Errors["to"].Should().Equal(ApprovalMessages.NoPreviousStage.Resolve(null));

        await Approve(id, "gm");
        Outcome(await Reject(id, "buyer2", "請重看", RejectTaskHandler.ToPrevious)).Should().Be(ApprovalOutcomes.Returned);
        var runs = Db.Context.Set<ApprovalStageRun>().AsNoTracking().Where(x => x.InstanceId == id).OrderBy(x => x.Seq).ToList();
        runs.Select(x => (x.StageCode, x.Status)).Should().Equal(("mgr", "approved"), ("board", "returned"), ("mgr", "active"));
        TasksOf(id).Where(t => t.Status == ApprovalTaskStatus.Open).Select(t => (t.AssigneeUserId, t.StageRunId)).Should().Equal(("gm", runs[2].Id));
        Notices("gm", NotificationKinds.ApprovalReturned).Should().Be(1);
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Pending);
    }

    [Fact]
    public async Task Withdraw_BeforeAnyAction_Withdrawn_TaskClosed_Notified()
    {
        var r = await Scenario3();
        var id = await SubmitAs(r, "buyer1", 50000);
        As("buyer1");
        var res = await Send(new WithdrawApprovalRequest(id, Inst(id).RowVersion, null));
        Outcome(res).Should().Be(ApprovalOutcomes.Withdrawn);
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Withdrawn);
        TasksOf(id).Single().Status.Should().Be(ApprovalTaskStatus.Closed);
        Notices("buyer2", NotificationKinds.ApprovalWithdrawn).Should().Be(1);
        var po = Inst(id).RecordId;
        StateOf(r, po).Should().Be(r.Draft);
        (await Put(po, V((r.Memo, "可以改了")))).IsSuccess.Should().BeTrue();   // 解除簽核中
    }

    [Fact]
    public async Task Approve_ValuesOutsideEditable_400_EditableWritten()
    {
        var r = await BuildPurchase();
        await SeedFlow(r, "po-e", r.Confirmed, 1, Def(null, Stage("lead", Group(PurchaseLeadGroup), editable: [r.DueDate]), Stage("board", Group(BoardGroup))));
        (await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed), Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received),
            Plain(r.Confirmed, r.Cancelled)])).IsSuccess.Should().BeTrue();
        await GrantEveryone(r.Module);
        var id = await SubmitAs(r, "buyer1", 5000);
        var po = Inst(id).RecordId;
        (await Approve(id, "buyer2", V((r.Memo, "偷改")))).Errors[$"f_{r.Memo}"].Should().Equal(ApprovalMessages.NotEditableField.Resolve(null));
        Open(id, "buyer2").Status.Should().Be(ApprovalTaskStatus.Open);
        Rec(po).Values.ContainsKey($"f_{r.Memo}").Should().BeFalse();

        Outcome(await Approve(id, "buyer2", V((r.DueDate, "2026-12-01")))).Should().Be(ApprovalOutcomes.Advanced);
        Rec(po).Values[$"f_{r.DueDate}"]!.Value.GetString().Should().Be("2026-12-01");
        Rec(po).Row.ModifiedUserId.Should().Be("buyer2");
    }

    [Fact]
    public async Task Approve_EditableFieldHiddenForApprover_400_NothingWritten()
    {
        // 裁決 22（執行期）：流程存好之後欄位才被設成對簽核人隱藏 → 同意帶這個欄位的值回 400，什麼都不寫
        var r = await BuildPurchase();
        await SeedFlow(r, "po-e", r.Confirmed, 1, Def(null, Stage("lead", Group(PurchaseLeadGroup), editable: [r.DueDate])));
        (await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed), Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received),
            Plain(r.Confirmed, r.Cancelled)])).IsSuccess.Should().BeTrue();
        await GrantEveryone(r.Module);
        var id = await SubmitAs(r, "buyer1", 5000);
        As("admin");
        await HideFor(r.Module, r.DueDate, PurchaseLeadGroup);
        var po = Inst(id).RecordId;
        var before = Inst(id).RowVersion;

        var res = await Approve(id, "buyer2", V((r.DueDate, "2026-12-01")));
        res.Errors[$"f_{r.DueDate}"].Should().Equal(ApprovalMessages.FieldHiddenForApprover.Resolve(null));
        Inst(id).RowVersion.Should().Be(before);
        Open(id, "buyer2").Status.Should().Be(ApprovalTaskStatus.Open);
        Rec(po).Values.ContainsKey($"f_{r.DueDate}").Should().BeFalse();
        Outcome(await Approve(id, "buyer2")).Should().Be(ApprovalOutcomes.Approved);   // 不帶值照常同意
    }

    [Fact]
    public async Task Approve_ClosedModule_409()
    {
        var r = await Scenario3();
        var id = await SubmitAs(r, "buyer1", 50000);
        await Db.Context.Set<Module>().Where(m => m.Key == r.Module).ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, ModuleStatus.Closed));
        Db.Context.ChangeTracker.Clear();
        var res = await Approve(id, "buyer2");
        res.IsConflict.Should().BeTrue(Dump(res));
        res.Message.Should().Be(ModuleGate.ClosedMessage);
    }

    [Fact]
    public async Task Version2_DoesNotAffectPendingInstance()
    {
        var r = await Scenario3();
        var id = await SubmitAs(r, "buyer1", 150000);
        As("admin");
        var flow = Flow("po-a");
        var upd = await Send(new UpdateApprovalFlowRequest("po-a", new ApprovalFlowInput("po-a", Text("po-a"), r.Confirmed, 1, true,
            Def(Cond(r.Total, "gte", 100000), Stage("mgr", Manager(1))))));
        upd.IsSuccess.Should().BeTrue(Dump(upd));
        Db.Context.Set<ApprovalFlowVersion>().AsNoTracking().Count(v => v.FlowId == flow.Id).Should().Be(2);

        await Approve(id, "gm");
        // 版本 1 的第 2 關（會簽）仍在
        TasksOf(id).Where(t => t.Status == ApprovalTaskStatus.Open).Select(t => t.AssigneeUserId).Should().BeEquivalentTo(["gm", "buyer2"]);
    }

    [Fact]
    public async Task EmptySystem_GroupSource_SubmitAndApprove_ManagerFallsBack()
    {
        // 沒有部門的使用者：manager 來源找不到人 → 備援（系統管理員）
        await Db.Context.Set<UserDepartment>().ExecuteDeleteAsync();
        var r = await BuildPurchase();
        await SeedFlow(r, "po-x", r.Confirmed, 1, Def(null, Stage("mgr", Manager(1)), Stage("lead", Group(PurchaseLeadGroup))));
        (await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed), Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received),
            Plain(r.Confirmed, r.Cancelled)])).IsSuccess.Should().BeTrue();
        await GrantEveryone(r.Module);
        var id = await SubmitAs(r, "buyer1", 5000);
        Open(id, "admin").Reason.Should().Be(ApprovalTaskReason.Fallback);
        Outcome(await Approve(id, "admin")).Should().Be(ApprovalOutcomes.Advanced);
        Outcome(await Approve(id, "buyer2")).Should().Be(ApprovalOutcomes.Approved);
        StateOf(r, Inst(id).RecordId).Should().Be(r.Confirmed);
    }
    private Task Deactivate(params string[] users) =>
        Db.Context.Set<AppUser>().Where(u => users.Contains(u.UserId)).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));

    private async Task<PurchaseRig> OneFlow(ApprovalDefinition d)
    {
        var r = await BuildPurchase();
        await SeedFlow(r, "po-n", r.Confirmed, 1, d);
        (await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed), Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received),
            Plain(r.Confirmed, r.Cancelled)])).IsSuccess.Should().BeTrue();
        await GrantEveryone(r.Module);
        return r;
    }

    [Fact]
    public async Task Submit_AllStage_TwoEmptyUnits_SingleFallback_409_NoInstance()
    {
        // 會簽兩個空單位、備援只有 admin 一人：送出就 409，不留實例（設計 §6.5；審查必修 1）
        var r = await OneFlow(Def(null, Stage("both", Users("emp1", "emp2"), mode: ApprovalModes.All)));
        await Deactivate("emp1", "emp2");
        As("buyer1");
        var po = await NewPo(r, price: 5000);
        var res = await Submit(po.Id, r.Confirmed);
        res.IsConflict.Should().BeTrue(Dump(res));
        res.Message.Should().Be(ApprovalMessages.NoApprover("both", "emp2").Resolve(null));
        Db.Context.Set<ApprovalInstance>().AsNoTracking().Count(i => i.RecordId == po.Id).Should().Be(0);
        StateOf(r, po.Id).Should().Be(r.Draft);
    }

    [Fact]
    public async Task Approve_NextStageNoApprover_409_NothingChanged()
    {
        // 推進時下一關找不到人、備援也沒有 → 同意回 409、什麼都不留（設計 §6.5 末段）
        var r = await OneFlow(Def(null, Stage("lead", Group(PurchaseLeadGroup)), Stage("next", Users("emp1"))) with { FallbackApprovers = Users("emp2") });
        var id = await SubmitAs(r, "buyer1", 5000);
        await Deactivate("emp1", "emp2");
        var before = Inst(id).RowVersion;
        var actions = ActionsOf(id).Count;
        var res = await Approve(id, "buyer2");
        res.IsConflict.Should().BeTrue(Dump(res));
        res.Message.Should().Be(ApprovalMessages.NoApprover("next", "emp1").Resolve(null));
        Inst(id).RowVersion.Should().Be(before);
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Pending);
        ActionsOf(id).Should().HaveCount(actions);
        Open(id, "buyer2").Status.Should().Be(ApprovalTaskStatus.Open);
        Db.Context.Set<ApprovalStageRun>().AsNoTracking().Count(x => x.InstanceId == id).Should().Be(1);
        StateOf(r, Inst(id).RecordId).Should().Be(r.Draft);
    }
    [Fact]
    public async Task Reject_ToPrevious_NotifiesInitiator()
    {
        // 設計 §12「被退回」：發起人；退回上一關時另通知上一關的新任務負責人（審查建議 3）
        var r = await Scenario3();
        var id = await SubmitAs(r, "buyer1", 150000);
        await Approve(id, "gm");
        Outcome(await Reject(id, "buyer2", "請重看", RejectTaskHandler.ToPrevious)).Should().Be(ApprovalOutcomes.Returned);
        Notices("buyer1", NotificationKinds.ApprovalReturned).Should().Be(1);
        Notices("gm", NotificationKinds.ApprovalReturned).Should().Be(1);
        Notices("buyer2", NotificationKinds.ApprovalReturned).Should().Be(0);
    }

    [Fact]
    public async Task Reject_SameUserApprovedOtherSeatInStage_409()
    {
        // 會簽兩個空單位、備援 gm 與 mgrA 共用：gm 同意 fb:0 後，不能再用 fb:1 的任務退回（設計 §7.7；審查建議 6）
        var r = await OneFlow(Def(null, Stage("both", Users("emp1", "emp2"), mode: ApprovalModes.All)) with { FallbackApprovers = Users("gm", "mgrA") });
        await Deactivate("emp1", "emp2");
        var id = await SubmitAs(r, "buyer1", 5000);
        var gmTasks = TasksOf(id).Where(t => t.AssigneeUserId == "gm").OrderBy(t => t.SeatKey).ToList();
        gmTasks.Select(t => t.SeatKey).Should().Equal("fb:0", "fb:1");
        As("gm");
        Outcome(await Send(new ApproveTaskRequest(gmTasks[0].Id, Inst(id).RowVersion, null, null))).Should().Be(ApprovalOutcomes.Advanced);

        var actions = ActionsOf(id).Count;
        var res = await Send(new RejectTaskRequest(gmTasks[1].Id, Inst(id).RowVersion, "改退回", RejectTaskHandler.ToInitiator));
        res.IsConflict.Should().BeTrue(Dump(res));
        res.Message.Should().Be(ApprovalMessages.StageHandled.Resolve(null));
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Pending);
        ActionsOf(id).Should().HaveCount(actions);
        TasksOf(id).Single(t => t.Id == gmTasks[1].Id).Status.Should().Be(ApprovalTaskStatus.Open);
    }

    /// <summary>只有一關（採購主管群組）的流程，RejectTo 直接寫進定義（不經驗證，模擬設定後來被改掉）。</summary>
    private async Task<(PurchaseRig R, long Id)> RejectRig(Func<PurchaseRig, long?> rejectTo)
    {
        var r = await BuildPurchase();
        var flow = await SeedFlow(r, "po-rt", r.Confirmed, 1, Def(null, Stage("lead", Group(PurchaseLeadGroup))));
        (await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed), Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received),
            Plain(r.Confirmed, r.Cancelled)])).IsSuccess.Should().BeTrue();
        var version = Db.Context.Set<ApprovalFlowVersion>().Single(v => v.FlowId == flow.Id);
        version.Definition = version.Definition with { RejectTo = rejectTo(r) };
        await Db.Context.SaveChangesAsync();
        await GrantEveryone(r.Module);
        return (r, await SubmitAs(r, "buyer1", 5000));
    }

    [Fact]
    public async Task Reject_RejectToTransitionGone_409_NothingChanged()
    {
        // 執行期再驗：RejectTo 指向的轉移（草稿 → 部分到貨）沒有定義 → 409，實例與記錄都不動（設計 §8.3）
        var (r, id) = await RejectRig(x => x.Partial);
        var res = await Reject(id, "buyer2", "不行");
        res.IsConflict.Should().BeTrue(Dump(res));
        res.Message.Should().Be(ApprovalMessages.TransitionGone.Resolve(null));
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Pending);
        Open(id, "buyer2").Status.Should().Be(ApprovalTaskStatus.Open);
        StateOf(r, Inst(id).RecordId).Should().Be(r.Draft);
    }

    [Theory]
    [InlineData(false)]   // 沒設 RejectTo
    [InlineData(true)]    // RejectTo 等於送出時的狀態（草稿）
    public async Task Reject_RejectToNullOrFrom_StateUnchanged(bool toFrom)
    {
        var (r, id) = await RejectRig(x => toFrom ? x.Draft : null);
        Outcome(await Reject(id, "buyer2", "不行")).Should().Be(ApprovalOutcomes.Rejected);
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Rejected);
        StateOf(r, Inst(id).RecordId).Should().Be(r.Draft);
    }
}
