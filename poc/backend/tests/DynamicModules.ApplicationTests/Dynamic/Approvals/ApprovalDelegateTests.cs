using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.AttachmentService;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetRecord;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>代理人（設計 §7.7、§10.1、§13.5、§17.5 一審第 7、8 項；計畫 a3d、U1、U8、J8、J9、J11）。</summary>
public class ApprovalDelegateTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    private sealed class Clock : IBusinessTime
    {
        private DateTime _now = new(2026, 11, 4, 9, 0, 0);
        /// <summary>排了 Ticks 時每讀一次 Now 就前進到下一個時間點（模擬一次請求內跨午夜）；排完回到最後設定的時間。</summary>
        public Queue<DateTime> Ticks { get; } = new();
        public DateTime Now { get => Ticks.TryDequeue(out var t) ? t : _now; set => _now = value; }
        public DateOnly Today => DateOnly.FromDateTime(Now);
    }

    private readonly Clock _clock = new();

    protected override void ConfigureServices(IServiceCollection services) => services.AddSingleton<IBusinessTime>(_clock);

    private static DateOnly D(int day) => new(2026, 11, day);

    private async Task SetDelegate(string user, string to, DateOnly start, DateOnly end)
    {
        As(user);
        var res = await Send(new SaveMyApprovalDelegateRequest(to, start, end));
        res.IsSuccess.Should().BeTrue(Dump(res));
    }

    private async Task<long> SubmitAs(PurchaseRig r, string user, decimal total)
    {
        As(user);
        var po = await NewPo(r, price: total);
        return (await MustSubmit(po.Id, r.Confirmed)).InstanceId!.Value;
    }

    private ApprovalTask OpenOf(long id, string assignee) =>
        TasksOf(id).Single(t => t.AssigneeUserId == assignee && t.Status == ApprovalTaskStatus.Open);

    private Task<ServiceResult> Approve(long id, ApprovalTask task, string user)
    {
        As(user);
        return Send(new ApproveTaskRequest(task.Id, Inst(id).RowVersion, null, null));
    }

    private async Task MustApprove(long id, ApprovalTask task, string user)
    {
        var res = await Approve(id, task, user);
        res.IsSuccess.Should().BeTrue(Dump(res));
    }

    private List<ApprovalAction> NotDelegated(long id) => ActionsOf(id).Where(a => a.Action == ApprovalActions.NotDelegated).ToList();

    private static string? Data(ApprovalAction a, string key) =>
        JsonDocument.Parse(a.DataJson!).RootElement.GetProperty(key).GetString();

    // Scenario3：15 萬 → 第 1 關主管 gm，第 2 關會簽 gm（board）、buyer2（purchase-lead）；5 萬 → 一關 buyer2。

    [Fact]
    public async Task Delegate_Active_NewTaskGoesToDelegate()
    {
        var r = await Scenario3();
        await SetDelegate("gm", "emp1", D(4), D(5));

        var id = await SubmitAs(r, "buyer1", 150000);

        var task = TasksOf(id).Single();
        task.AssigneeUserId.Should().Be("emp1");
        task.OriginalUserId.Should().Be("gm");
        task.Reason.Should().Be(ApprovalTaskReason.Delegated);
        Notices("emp1", NotificationKinds.ApprovalPending).Should().Be(1);
        NotDelegated(id).Should().BeEmpty();
    }

    [Fact]
    public async Task Delegate_BothCanRead_BothLoseAfterApprove()
    {
        var r = await Scenario3();
        await SetDelegate("buyer2", "emp1", D(4), D(5));
        var id = await SubmitAs(r, "buyer1", 50000);
        As("admin");
        await RevokeAll(r.Module);   // 沒有人有模組 Read，全靠臨時讀取
        var recordId = Inst(id).RecordId;
        foreach (var u in new[] { "buyer2", "emp1" })
        {
            As(u);
            (await Send(new GetRecordRequest(recordId))).IsSuccess.Should().BeTrue(u);
        }

        await MustApprove(id, OpenOf(id, "emp1"), "emp1");

        var approve = ActionsOf(id).Single(a => a.Action == ApprovalActions.Approve);
        approve.ActorUserId.Should().Be("emp1");
        approve.OnBehalfOfUserId.Should().Be("buyer2");
        foreach (var u in new[] { "buyer2", "emp1" })
        {
            As(u);
            (await Send(new GetRecordRequest(recordId))).IsForbidden.Should().BeTrue(u);   // 兩人都參與過（實際動作者、席位本人）：沒有 Read 維持 403；未參與者見 GetRecord_NoRead_NotParticipant_…
        }
    }

    [Fact]
    public async Task Delegate_OriginalCanStillAct_FirstWins()
    {
        var r = await Scenario3();
        await SetDelegate("buyer2", "emp1", D(4), D(5));
        var id = await SubmitAs(r, "buyer1", 50000);
        var task = OpenOf(id, "emp1");

        await MustApprove(id, task, "buyer2");

        var approve = ActionsOf(id).Single(a => a.Action == ApprovalActions.Approve);
        approve.ActorUserId.Should().Be("buyer2");
        approve.OnBehalfOfUserId.Should().BeNull();
        (await Approve(id, task, "emp1")).IsConflict.Should().BeTrue();
    }

    [Fact]
    public async Task Delegate_ExistingTasksNotMoved()
    {
        var r = await Scenario3();
        var id = await SubmitAs(r, "buyer1", 50000);

        await SetDelegate("buyer2", "emp1", D(4), D(5));

        var task = TasksOf(id).Single();
        task.AssigneeUserId.Should().Be("buyer2");
        task.Reason.Should().Be(ApprovalTaskReason.Resolved);
    }

    [Fact]
    public async Task Delegate_Period_InclusiveBusinessDates()
    {
        var r = await Scenario3();
        await SetDelegate("buyer2", "emp1", D(5), D(6));   // 明天起
        var notYet = await SubmitAs(r, "buyer1", 50000);
        TasksOf(notYet).Single().AssigneeUserId.Should().Be("buyer2");

        await SetDelegate("buyer2", "emp1", D(3), D(4));   // 迄日今天
        var lastDay = await SubmitAs(r, "buyer1", 50000);
        TasksOf(lastDay).Single().AssigneeUserId.Should().Be("emp1");

        _clock.Now = new DateTime(2026, 11, 4, 23, 30, 0);   // 營運日仍是 11-04
        var lateNight = await SubmitAs(r, "buyer1", 50000);
        TasksOf(lateNight).Single().AssigneeUserId.Should().Be("emp1");

        _clock.Now = new DateTime(2026, 11, 5, 0, 30, 0);   // 隔天：已過迄日
        var after = await SubmitAs(r, "buyer1", 50000);
        TasksOf(after).Single().AssigneeUserId.Should().Be("buyer2");
    }

    [Fact]
    public async Task Delegate_NoChain()
    {
        var r = await Scenario3();
        await SetDelegate("gm", "emp1", D(4), D(5));
        await SetDelegate("emp1", "emp2", D(4), D(5));

        var id = await SubmitAs(r, "buyer1", 150000);

        TasksOf(id).Single().AssigneeUserId.Should().Be("emp1");
    }

    [Fact]
    public async Task Delegate_DelegateIsInitiator_NotDelegated_LogsNote()
    {
        var r = await Scenario3();
        await SetDelegate("gm", "buyer1", D(4), D(5));

        var id = await SubmitAs(r, "buyer1", 150000);

        var task = TasksOf(id).Single();
        task.AssigneeUserId.Should().Be("gm");
        task.Reason.Should().Be(ApprovalTaskReason.Resolved);
        var note = NotDelegated(id).Single();
        note.ActorUserId.Should().Be(ApprovalEngine.SystemActor);
        note.StageRunId.Should().Be(task.StageRunId);
        Data(note, "userId").Should().Be("gm");
        Data(note, "delegateUserId").Should().Be("buyer1");
        Data(note, "reason").Should().Be("initiator");
        note.TargetUserIds.Should().Equal("gm", "buyer1");

        // 發起人自簽政策（A12）打開：同一個代理生效（U8）
        Db.Context.Add(new ModulePolicy
        {
            ModuleId = r.ModuleId, Code = PolicyCatalog.ApprovalAllowSelfApproval.Code, Value = "true", ModifiedUserId = "admin"
        });
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
        var allowed = await SubmitAs(r, "buyer1", 150000);
        var delegated = TasksOf(allowed).Single();
        delegated.AssigneeUserId.Should().Be("buyer1");
        delegated.Reason.Should().Be(ApprovalTaskReason.Delegated);
        NotDelegated(allowed).Should().BeEmpty();
    }

    [Fact]
    public async Task Delegate_DelegateAlreadyInStage_NotDelegated_LogsNote()
    {
        var r = await Scenario3();
        await SetDelegate("buyer2", "gm", D(4), D(5));
        var id = await SubmitAs(r, "buyer1", 150000);
        await MustApprove(id, OpenOf(id, "gm"), "gm");   // 第 1 關；第 2 關會簽 gm、buyer2

        var stage2 = TasksOf(id).Where(t => t.Status == ApprovalTaskStatus.Open).ToList();
        stage2.Select(t => (t.AssigneeUserId, t.Reason)).Should().BeEquivalentTo(new[]
        {
            ("gm", ApprovalTaskReason.Resolved), ("buyer2", ApprovalTaskReason.Resolved)
        });
        var note = NotDelegated(id).Single();
        Data(note, "userId").Should().Be("buyer2");
        Data(note, "delegateUserId").Should().Be("gm");
        Data(note, "reason").Should().Be("duplicate");
    }

    [Fact]
    public async Task Delegate_ApprovedAsOriginal_ThenActAsDelegateOnOtherSeat_409()
    {
        var r = await Scenario3();
        var id = await SubmitAs(r, "buyer1", 150000);
        await MustApprove(id, OpenOf(id, "gm"), "gm");
        // Db 直接造：第 2 關 buyer2 那一席代理給 gm（正常建任務時會因重複而不代理）
        var seat = OpenOf(id, "buyer2");
        await Db.Context.Set<ApprovalTask>().Where(t => t.Id == seat.Id).ExecuteUpdateAsync(s => s
            .SetProperty(t => t.AssigneeUserId, "gm").SetProperty(t => t.Reason, ApprovalTaskReason.Delegated));
        Db.Context.ChangeTracker.Clear();
        var own = TasksOf(id).Single(t => t.Status == ApprovalTaskStatus.Open && t.Reason == ApprovalTaskReason.Resolved);

        await MustApprove(id, own, "gm");

        (await Approve(id, TasksOf(id).Single(t => t.Id == seat.Id), "gm")).IsConflict.Should().BeTrue();
    }

    [Fact]
    public async Task Delegate_ApprovedAsOriginalOfDelegatedSeat_ThenOwnSeat_409()
    {
        // J9：本人處理了「代理給別人的那一席」之後，在同一關就沒有第二票（任務的負責人是代理人，舊的判斷看不到）
        var r = await Scenario3();
        var id = await SubmitAs(r, "buyer1", 150000);
        await MustApprove(id, OpenOf(id, "gm"), "gm");
        var gmSeat = OpenOf(id, "gm");
        var other = OpenOf(id, "buyer2");
        await Db.Context.Set<ApprovalTask>().Where(t => t.Id == gmSeat.Id).ExecuteUpdateAsync(s => s
            .SetProperty(t => t.AssigneeUserId, "emp1").SetProperty(t => t.Reason, ApprovalTaskReason.Delegated));
        await Db.Context.Set<ApprovalTask>().Where(t => t.Id == other.Id).ExecuteUpdateAsync(s => s
            .SetProperty(t => t.AssigneeUserId, "gm").SetProperty(t => t.Reason, ApprovalTaskReason.Delegated));
        Db.Context.ChangeTracker.Clear();

        await MustApprove(id, TasksOf(id).Single(t => t.Id == gmSeat.Id), "gm");   // 本人處理代理給 emp1 的那一席

        (await Approve(id, TasksOf(id).Single(t => t.Id == other.Id), "gm")).IsConflict.Should().BeTrue();
    }

    [Fact]
    public async Task Delegate_TwoApproversSameDelegate_SecondNotDelegated()
    {
        var r = await Scenario3();
        await SetDelegate("gm", "emp1", D(4), D(5));
        await SetDelegate("buyer2", "emp1", D(4), D(5));
        var id = await SubmitAs(r, "buyer1", 150000);
        await MustApprove(id, OpenOf(id, "emp1"), "emp1");   // 第 1 關代 gm

        var stage2 = TasksOf(id).Where(t => t.Status == ApprovalTaskStatus.Open).ToList();
        stage2.Should().HaveCount(2);
        stage2.Count(t => t.AssigneeUserId == "emp1" && t.Reason == ApprovalTaskReason.Delegated).Should().Be(1);
        stage2.Count(t => t.AssigneeUserId == t.OriginalUserId && t.Reason == ApprovalTaskReason.Resolved).Should().Be(1);
        Data(NotDelegated(id).Single(), "reason").Should().Be("duplicate");
    }

    [Fact]
    public async Task Delegate_InactiveDelegate_NotApplied()
    {
        var r = await Scenario3();
        await SetDelegate("buyer2", "emp1", D(4), D(5));
        await Db.Context.Set<AppUser>().Where(u => u.UserId == "emp1").ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        Db.Context.ChangeTracker.Clear();

        var id = await SubmitAs(r, "buyer1", 50000);

        TasksOf(id).Single().AssigneeUserId.Should().Be("buyer2");
    }

    [Fact]
    public async Task Delegate_AppliesToFallbackSeat_NotToTransferOrAddSign()
    {
        var r = await BuildPurchase();
        await SeedFlow(r, "po-f", r.Confirmed, 1, Def(null, Stage("s1", Users("emp3"))) with { FallbackApprovers = Users("lead1") });
        (await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed), Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received),
            Plain(r.Confirmed, r.Cancelled)])).IsSuccess.Should().BeTrue();
        await GrantEveryone(r.Module, r.Extra);
        await Db.Context.Set<AppUser>().Where(u => u.UserId == "emp3").ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        Db.Context.ChangeTracker.Clear();
        await SetDelegate("lead1", "emp2", D(4), D(5));
        await SetDelegate("fin1", "emp1", D(4), D(5));
        await SetDelegate("mgrA", "buyer2", D(4), D(5));

        var id = await SubmitAs(r, "buyer1", 1000);
        var fallback = TasksOf(id).Single();
        fallback.AssigneeUserId.Should().Be("emp2");   // 備援席位照樣代理
        fallback.OriginalUserId.Should().Be("lead1");
        fallback.Reason.Should().Be(ApprovalTaskReason.Delegated);

        As("emp2");
        var transfer = await Send(new TransferTaskRequest(fallback.Id, Inst(id).RowVersion, "fin1", null));
        transfer.IsSuccess.Should().BeTrue(Dump(transfer));
        var toFin = OpenOf(id, "fin1");   // 轉交點名的人不套代理（fin1 → emp1 不生效）
        toFin.Reason.Should().Be(ApprovalTaskReason.Transferred);
        ActionsOf(id).Single(a => a.Action == ApprovalActions.Transfer).OnBehalfOfUserId.Should().Be("lead1");   // J8：代理人轉交時寫本人

        As("fin1");
        var add = await Send(new AddSignTaskRequest(toFin.Id, Inst(id).RowVersion, null, null, ["mgrA"], ApprovalModes.Any));
        add.IsSuccess.Should().BeTrue(Dump(add));
        var added = OpenOf(id, "mgrA");   // 加簽點名的人不套代理（mgrA → buyer2 不生效）
        added.Reason.Should().Be(ApprovalTaskReason.Added);
    }

    [Fact]
    public async Task Delegate_RejectByDelegate_LogsOnBehalfOf()
    {
        var r = await Scenario3();
        await SetDelegate("buyer2", "emp1", D(4), D(5));
        var id = await SubmitAs(r, "buyer1", 50000);
        As("emp1");
        var res = await Send(new RejectTaskRequest(OpenOf(id, "emp1").Id, Inst(id).RowVersion, "數量不對", RejectTaskHandler.ToInitiator));
        res.IsSuccess.Should().BeTrue(Dump(res));

        var reject = ActionsOf(id).Single(a => a.Action == ApprovalActions.Reject);
        reject.ActorUserId.Should().Be("emp1");
        reject.OnBehalfOfUserId.Should().Be("buyer2");
    }

    [Fact]
    public async Task Inbox_Todo_ListsDelegatedTaskForBoth()
    {
        var r = await Scenario3();
        await SetDelegate("buyer2", "emp1", D(4), D(5));
        var id = await SubmitAs(r, "buyer1", 50000);

        foreach (var u in new[] { "buyer2", "emp1" })
        {
            As(u);
            var res = await Send(new GetApprovalTasksRequest(ApprovalInboxBoxes.Todo, null, null, null, 1, 20));
            res.IsSuccess.Should().BeTrue(Dump(res));
            ((PagedResponse<ApprovalInboxItem>)res.Data!).Items.Select(x => x.InstanceId).Should().Contain(id, u);
        }
    }

    [Fact]
    public async Task RecordApproval_ShowsDelegateRelationAndOnBehalfName()
    {
        var r = await Scenario3();
        await SetDelegate("buyer2", "emp1", D(4), D(5));
        var id = await SubmitAs(r, "buyer1", 50000);
        await MustApprove(id, OpenOf(id, "emp1"), "emp1");

        As("buyer1");
        var res = await Send(new GetRecordApprovalRequest(Inst(id).RecordId));
        res.IsSuccess.Should().BeTrue(Dump(res));
        var view = ((RecordApprovalResponse)res.Data!).Previous.Single(i => i.Id == id);
        var task = view.Stages.Single().Tasks.Single();
        (task.Reason, task.AssigneeName, task.OriginalName).Should().Be((ApprovalTaskReason.Delegated, "emp1", "buyer2"));
        var approve = view.Actions.Single(a => a.Action == ApprovalActions.Approve);
        (approve.OnBehalfOfUserId, approve.OnBehalfOfName).Should().Be(("buyer2", "buyer2"));
    }

    // ───────── 實際動作者（a3d 審查修正：我已處理、結束後的歷程、記錄存在性） ─────────

    private async Task<List<long>> Done(string user)
    {
        As(user);
        var res = await Send(new GetApprovalTasksRequest(ApprovalInboxBoxes.Done, null, null, null, 1, 100));
        res.IsSuccess.Should().BeTrue(Dump(res));
        return ((PagedResponse<ApprovalInboxItem>)res.Data!).Items.Select(x => x.InstanceId).ToList();
    }

    private Task<ServiceResult> History(long id, string user)
    {
        As(user);
        return Send(new GetRecordApprovalRequest(Inst(id).RecordId));
    }

    private Task<ServiceResult> Record(long recordId, string user)
    {
        As(user);
        return Send(new GetRecordRequest(recordId));
    }

    [Fact]
    public async Task Done_OriginalHandlesDelegatedTask_ListedForOriginalOnly()
    {
        var r = await Scenario3();
        await SetDelegate("buyer2", "emp1", D(4), D(5));
        var id = await SubmitAs(r, "buyer1", 50000);

        await MustApprove(id, OpenOf(id, "emp1"), "buyer2");   // 本人自己處理代理給 emp1 的任務

        (await Done("buyer2")).Should().Contain(id);
        (await Done("emp1")).Should().NotContain(id);
    }

    [Fact]
    public async Task Done_DelegateHandles_ListedForDelegateOnly()
    {
        var r = await Scenario3();
        await SetDelegate("buyer2", "emp1", D(4), D(5));
        var id = await SubmitAs(r, "buyer1", 50000);

        await MustApprove(id, OpenOf(id, "emp1"), "emp1");

        (await Done("emp1")).Should().Contain(id);
        (await Done("buyer2")).Should().NotContain(id);
    }

    [Fact]
    public async Task Done_ReassignAndCancel_NotHandled()
    {
        var r = await Scenario3();
        var reassigned = await SubmitAs(r, "buyer1", 50000);
        As("admin");
        var move = await Send(new ReassignTaskRequest(OpenOf(reassigned, "buyer2").Id, Inst(reassigned).RowVersion, "fin1", null));
        move.IsSuccess.Should().BeTrue(Dump(move));
        var cancelled = await SubmitAs(r, "buyer1", 50000);
        As("admin");
        var stop = await Send(new CancelApprovalRequest(cancelled, Inst(cancelled).RowVersion, "供應商報價已過期"));
        stop.IsSuccess.Should().BeTrue(Dump(stop));

        (await Done("buyer2")).Should().NotContain([reassigned, cancelled]);   // 被改派走、被終止的人沒有處理
        (await Done("admin")).Should().BeEmpty();   // 改派、終止是管理動作，不算處理任務
    }

    [Fact]
    public async Task History_AfterEnd_OriginalActed_DelegateLosesIt()
    {
        var r = await Scenario3();
        await SetDelegate("buyer2", "emp1", D(4), D(5));
        var id = await SubmitAs(r, "buyer1", 50000);
        As("admin");
        await RevokeAll(r.Module);
        foreach (var u in new[] { "buyer2", "emp1" })
            (await History(id, u)).IsSuccess.Should().BeTrue(u);   // open 期間：兩個可動作者都看得到

        await MustApprove(id, OpenOf(id, "emp1"), "buyer2");

        (await History(id, "buyer2")).IsSuccess.Should().BeTrue();
        (await History(id, "emp1")).IsNotFound.Should().BeTrue();   // 沒動作的代理人不再保有
    }

    [Fact]
    public async Task History_AfterEnd_DelegateActed_BothKeepIt()
    {
        var r = await Scenario3();
        await SetDelegate("buyer2", "emp1", D(4), D(5));
        var id = await SubmitAs(r, "buyer1", 50000);
        As("admin");
        await RevokeAll(r.Module);

        await MustApprove(id, OpenOf(id, "emp1"), "emp1");

        foreach (var u in new[] { "buyer2", "emp1" })
            (await History(id, u)).IsSuccess.Should().BeTrue(u);   // 實際動作者＋席位本人
    }

    [Fact]
    public async Task GetRecord_NoRead_NotParticipant_SameAsMissing_ParticipantGets403()
    {
        var r = await Scenario3();
        await SetDelegate("buyer2", "emp1", D(4), D(5));
        var id = await SubmitAs(r, "buyer1", 50000);
        As("admin");
        await RevokeAll(r.Module);
        await MustApprove(id, OpenOf(id, "emp1"), "buyer2");
        var recordId = Inst(id).RecordId;
        var missingId = recordId + 100000;

        foreach (var u in new[] { "emp1", "fin1" })   // 沒動作的代理人、和這張單無關的人
        {
            var exists = await Record(recordId, u);
            var missing = await Record(missingId, u);
            exists.IsNotFound.Should().BeTrue(u);
            (exists.IsNotFound, exists.Code, exists.Message).Should().Be((missing.IsNotFound, missing.Code, missing.Message), u);
            As(u);
            var file = await Send(new DownloadAttachmentRequest(recordId, 1, "x"));
            var noFile = await Send(new DownloadAttachmentRequest(missingId, 1, "x"));
            (file.IsNotFound, file.Code, file.Message).Should().Be((noFile.IsNotFound, noFile.Code, noFile.Message), u);
        }

        foreach (var u in new[] { "buyer2", "buyer1" })   // 處理過的本人、發起人：看得到歷程，維持 403
        {
            (await Record(recordId, u)).IsForbidden.Should().BeTrue(u);
            As(u);
            (await Send(new DownloadAttachmentRequest(recordId, 1, "x"))).IsForbidden.Should().BeTrue(u);
            (await History(id, u)).IsSuccess.Should().BeTrue(u);
        }
    }

    // ───────── 代辦歷程與讀取權收回（a3d 審查未覆蓋項） ─────────

    [Fact]
    public async Task Delegate_AddSignAndReturnPrevious_LogOnBehalfOf()
    {
        var r = await Scenario3();
        await SetDelegate("buyer2", "emp1", D(4), D(5));
        var id = await SubmitAs(r, "buyer1", 150000);
        await MustApprove(id, OpenOf(id, "gm"), "gm");   // 第 2 關會簽 gm、buyer2（代理給 emp1）
        var seat = OpenOf(id, "emp1");
        seat.Reason.Should().Be(ApprovalTaskReason.Delegated);

        As("emp1");
        var add = await Send(new AddSignTaskRequest(seat.Id, Inst(id).RowVersion, null, null, ["mgrA"], ApprovalModes.Any));
        add.IsSuccess.Should().BeTrue(Dump(add));
        ActionsOf(id).Single(a => a.Action == ApprovalActions.AddSign).OnBehalfOfUserId.Should().Be("buyer2");

        var other = await SubmitAs(r, "buyer1", 150000);
        await MustApprove(other, OpenOf(other, "gm"), "gm");
        As("emp1");
        var back = await Send(new RejectTaskRequest(OpenOf(other, "emp1").Id, Inst(other).RowVersion, "金額要重核", RejectTaskHandler.ToPrevious));
        back.IsSuccess.Should().BeTrue(Dump(back));
        var ret = ActionsOf(other).Single(a => a.Action == ApprovalActions.Return);
        (ret.ActorUserId, ret.OnBehalfOfUserId).Should().Be(("emp1", "buyer2"));
    }

    [Theory]
    [InlineData("transfer")]
    [InlineData("reassign")]
    [InlineData("return")]
    [InlineData("reject")]
    [InlineData("cancel")]
    public async Task Delegate_TaskEnds_BothLoseRead(string how)
    {
        var r = await Scenario3();
        await SetDelegate("buyer2", "emp1", D(4), D(5));
        var id = await SubmitAs(r, "buyer1", 150000);
        await MustApprove(id, OpenOf(id, "gm"), "gm");   // 第 2 關：buyer2 那一席代理給 emp1
        As("admin");
        await RevokeAll(r.Module);
        var recordId = Inst(id).RecordId;
        var seat = OpenOf(id, "emp1");
        foreach (var u in new[] { "buyer2", "emp1" })
            (await Record(recordId, u)).IsSuccess.Should().BeTrue(u);

        As(how is "reassign" or "cancel" ? "admin" : "emp1");
        var rv = Inst(id).RowVersion;
        var res = how switch
        {
            "transfer" => await Send(new TransferTaskRequest(seat.Id, rv, "fin1", null)),
            "reassign" => await Send(new ReassignTaskRequest(seat.Id, rv, "fin1", null)),
            "return" => await Send(new RejectTaskRequest(seat.Id, rv, "金額要重核", RejectTaskHandler.ToPrevious)),
            "reject" => await Send(new RejectTaskRequest(seat.Id, rv, "數量不對", RejectTaskHandler.ToInitiator)),
            _ => await Send(new CancelApprovalRequest(id, rv, "供應商報價已過期")),
        };
        res.IsSuccess.Should().BeTrue(Dump(res));

        foreach (var u in new[] { "buyer2", "emp1" })
            (await Record(recordId, u)).IsSuccess.Should().BeFalse($"{how}: {u}");
    }

    // ───────── 設定 ─────────

    [Fact]
    public async Task SaveDelegate_Validation_400s()
    {
        await Db.Context.Set<AppUser>().Where(u => u.UserId == "emp2").ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        Db.Context.ChangeTracker.Clear();
        As("gm");

        async Task Bad(SaveMyApprovalDelegateRequest req, string key, MessageRef message)
        {
            var res = await Send(req);
            res.IsInvalid.Should().BeTrue(Dump(res));
            res.Errors[key].Should().Equal(message.Resolve(null));
        }

        await Bad(new("gm", D(4), D(5)), "delegateUserId", ApprovalMessages.DelegateSelf);
        await Bad(new("emp2", D(4), D(5)), "delegateUserId", ApprovalMessages.DelegateInactive);
        await Bad(new("nobody", D(4), D(5)), "delegateUserId", ApprovalMessages.DelegateInactive);
        await Bad(new("emp1", D(6), D(5)), "startDate", ApprovalMessages.DelegatePeriod);
        await Bad(new("emp1", D(1), D(3)), "endDate", ApprovalMessages.DelegateEnded);
        (await Db.Context.Set<ApprovalDelegate>().CountAsync()).Should().Be(0);
    }

    private async Task<MyApprovalDelegateResponse> Mine(string user)
    {
        As(user);
        var res = await Send(new GetMyApprovalDelegateRequest());
        res.IsSuccess.Should().BeTrue(Dump(res));
        return (MyApprovalDelegateResponse)res.Data!;
    }

    [Fact]
    public async Task SaveGetClear_RoundTrip()
    {
        await SetDelegate("gm", "emp1", D(5), D(8));
        (await Mine("gm")).Delegate.Should().Be(new ApprovalDelegateView("emp1", "emp1", D(5), D(8), false, true));

        await SetDelegate("gm", "emp2", D(4), D(4));   // 覆寫
        (await Mine("gm")).Delegate.Should().Be(new ApprovalDelegateView("emp2", "emp2", D(4), D(4), true, false));
        (await Db.Context.Set<ApprovalDelegate>().AsNoTracking().CountAsync()).Should().Be(1);

        (await Mine("emp1")).Delegate.Should().BeNull();   // 只看得到自己的

        As("gm");
        (await Send(new ClearMyApprovalDelegateRequest())).IsSuccess.Should().BeTrue();
        (await Mine("gm")).Delegate.Should().BeNull();
        As("gm");
        (await Send(new ClearMyApprovalDelegateRequest())).IsSuccess.Should().BeTrue();   // 沒有設定時也成功
    }

    [Fact]
    public async Task Get_None_ReturnsNullDelegateWithBusinessToday()
    {
        var mine = await Mine("gm");
        mine.Delegate.Should().BeNull();
        mine.Today.Should().Be(D(4));   // 沒有設定也給營運日：前端的預設日期用它
        mine.SecondsToNextDay.Should().Be(15 * 3600);   // 09:00 → 隔天 00:00
    }

    [Fact]
    public async Task Get_StatusFollowsBusinessDay_AcrossMidnight()
    {
        await SetDelegate("gm", "emp1", D(5), D(5));

        _clock.Now = new DateTime(2026, 11, 4, 23, 30, 0);
        var before = await Mine("gm");
        (before.Today, before.SecondsToNextDay, before.Delegate!.Active, before.Delegate.Upcoming).Should().Be((D(4), 1800, false, true));

        _clock.Now = new DateTime(2026, 11, 5, 0, 0, 0);
        var during = await Mine("gm");
        (during.Today, during.SecondsToNextDay, during.Delegate!.Active, during.Delegate.Upcoming).Should().Be((D(5), 86400, true, false));

        _clock.Now = new DateTime(2026, 11, 6, 0, 0, 1);
        var after = await Mine("gm");
        (after.Today, after.Delegate!.Active, after.Delegate.Upcoming).Should().Be((D(6), false, false));   // 期間已過：兩者皆否
    }

    [Fact]
    public async Task Get_SamplesTimeOnce_WhenMidnightPassesDuringTheRequest()
    {
        await SetDelegate("gm", "emp1", D(5), D(5));
        _clock.Now = new DateTime(2026, 11, 5, 0, 0, 1);
        _clock.Ticks.Enqueue(new DateTime(2026, 11, 4, 23, 59, 59));   // 第一次讀時鐘還在 4 日，之後的讀取都已過午夜

        var mine = await Mine("gm");
        (mine.Today, mine.SecondsToNextDay, mine.Delegate!.Active, mine.Delegate.Upcoming).Should().Be((D(4), 1, false, true));   // 全部出自同一個時間點
    }
}
