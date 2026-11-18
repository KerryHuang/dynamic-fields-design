using System.Reflection;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Persistence.Repositories.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static DynamicModules.ApplicationTests.Dynamic.Approvals.ApprovalGraphTestKit;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>
/// 流程圖實例的併發：處理權、處理權之內的狀態檢查、重試與晚到訊息（計畫 fg2 Task 10；J16、J17、J18；U13）。
/// SQLite 由 WriteGate 讓寫入請求整筆序列化，自動重試不會自然發生，所以用 <see cref="FlakyClaimRepository"/> 讓處理權的條件更新失敗，
/// 並在失敗當下以同一個 DbContext 的 ExecuteUpdate 直接改資料列（共用單例、同一個交易，繞過變更追蹤，已追蹤的物件因此真的變成舊值）。
/// 簽核人：a、a1＝fin1、a2＝fin2、b＝buyer2、c＝gm；發起人 buyer1。
/// </summary>
public class ApprovalGraphConcurrencyTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    private readonly ClaimControl _claim = new();

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Scoped<IApprovalInstanceRepository>(_ =>
            FlakyClaimRepository.Wrap(new ApprovalInstanceRepository(Db.Context), _claim)));
    }

    /// <summary>
    /// 讓處理權的條件更新（<see cref="IApprovalInstanceRepository.TryBumpRowVersionAsync"/>）前幾次回 false 的替身。
    /// 用 DispatchProxy 轉送其他所有成員，之後儲存庫介面加方法也不必同步修改；onFirstFail 在第一次失敗、回 false 之前執行，用來模擬「別人剛提交」。
    /// </summary>
    internal sealed class ClaimControl
    {
        public int FailuresLeft, Calls, GuardCalls;
        public Func<Task>? OnFirstFail;

        public void Arm(int failures, Func<Task>? onFirstFail = null)
        {
            FailuresLeft = failures;
            Calls = 0;
            OnFirstFail = onFirstFail;
        }
    }

    internal class FlakyClaimRepository : DispatchProxy
    {
        private IApprovalInstanceRepository _inner = null!;
        private ClaimControl _control = null!;

        public static IApprovalInstanceRepository Wrap(IApprovalInstanceRepository inner, ClaimControl control)
        {
            var proxy = Create<IApprovalInstanceRepository, FlakyClaimRepository>();
            var self = (FlakyClaimRepository)(object)proxy;
            self._inner = inner;
            self._control = control;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IApprovalInstanceRepository.TryBumpRowVersionAsync)) return TryBumpAsync(args!);
            if (method.Name is nameof(IApprovalInstanceRepository.GetRowVersionAsync) or nameof(IApprovalInstanceRepository.ReloadTrackedAsync))
                _control.GuardCalls++;
            try { return method.Invoke(_inner, args); }
            catch (TargetInvocationException e) { throw e.InnerException!; }
        }

        private async Task<bool> TryBumpAsync(object?[] a)
        {
            _control.Calls++;
            if (_control.FailuresLeft > 0)
            {
                _control.FailuresLeft--;
                if (_control.OnFirstFail is { } inject)
                {
                    _control.OnFirstFail = null;
                    await inject();
                }
                return false;
            }
            return await _inner.TryBumpRowVersionAsync((long)a[0]!, (string)a[1]!, (string)a[2]!, (CancellationToken)a[3]!);
        }
    }

    // ───────── 圖與情境 ─────────

    /// <summary>start → fork f → 分支 a（fin1）、b（buyer2）→ join j → c（gm）→ end；joinMode 是全部到齊或任一到齊，aMode、aSource 是 a 的簽核方式與簽核人。</summary>
    private static ApprovalDefinition ForkJoin(string joinMode = ApprovalModes.All, string aMode = ApprovalModes.Any, ApproverSource? aSource = null) => G(
        [Start(), Fork("f"), Approval("a", aSource ?? Users("fin1"), mode: aMode), Approval("b", Users("buyer2")), Join("j", "f", joinMode),
         Approval("c", Users("gm")), End()],
        [E("e0", "start", "f"), E("fa", "f", "a"), E("fb", "f", "b"), E("aj", "a", "j"), E("bj", "b", "j"), E("jc", "j", "c"), E("ce", "c", "end")]);

    /// <summary>任一到齊、匯合後直接結束：任一分支同意，單據就核准。</summary>
    private static ApprovalDefinition AnyJoinThenEnd() => G(
        [Start(), Fork("f"), Approval("a", Users("fin1")), Approval("b", Users("buyer2")), Join("j", "f", ApprovalModes.Any), End()],
        [E("e0", "start", "f"), E("fa", "f", "a"), E("fb", "f", "b"), E("aj", "a", "j"), E("bj", "b", "j"), E("je", "j", "end")]);

    /// <summary>start → a（fin1、fin2 或簽）→ end。</summary>
    private static ApprovalDefinition OrSign() => G(
        [Start(), Approval("a", Users("fin1", "fin2")), End()],
        [E("e0", "start", "a"), E("ae", "a", "end")]);

    private async Task<PurchaseRig> Rig(ApprovalDefinition def)
    {
        var r = await BuildPurchase();
        if (def.Shape == ApprovalShapes.Graph) await SeedGraphFlow(r, "po-g", r.Confirmed, 1, def);
        else await SeedFlow(r, "po-s", r.Confirmed, 1, def);
        (await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed), Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received),
            Plain(r.Confirmed, r.Cancelled)])).IsSuccess.Should().BeTrue();
        await GrantEveryone(r.Module, r.Extra);
        return r;
    }

    private async Task<(PurchaseRig R, long Id)> Boot(ApprovalDefinition def)
    {
        var r = await Rig(def);
        As("buyer1");
        var po = await NewPo(r, price: 5000);
        return (r, (await MustSubmit(po.Id, r.Confirmed)).InstanceId!.Value);
    }

    private List<ApprovalStageRun> RunsOf(long id) =>
        Db.Context.Set<ApprovalStageRun>().AsNoTracking().Where(x => x.InstanceId == id).OrderBy(x => x.Seq).ToList();

    private ApprovalTask Open(long id, string user) => TasksOf(id).Single(t => t.AssigneeUserId == user && t.Status == ApprovalTaskStatus.Open);

    private ApprovalTask TaskOf(long id, string user) => TasksOf(id).Single(t => t.AssigneeUserId == user);

    private List<string> OpenUsers(long id) =>
        TasksOf(id).Where(t => t.Status == ApprovalTaskStatus.Open).Select(t => t.AssigneeUserId).Order().ToList();

    private Task<ServiceResult> Approve(long taskId, string user, string rowVersion)
    {
        As(user);
        return Send(new ApproveTaskRequest(taskId, rowVersion, null, null));
    }

    private async Task MustApprove(long id, string user)
    {
        var res = await Approve(Open(id, user).Id, user, Inst(id).RowVersion);
        res.IsSuccess.Should().BeTrue(Dump(res));
    }

    private Task<ServiceResult> RejectToInitiator(long taskId, string user, string rowVersion)
    {
        As(user);
        return Send(new RejectTaskRequest(taskId, rowVersion, "退回", RejectTaskHandler.ToInitiator));
    }

    private Task<ServiceResult> Withdraw(long id, string rowVersion)
    {
        As("buyer1");
        return Send(new WithdrawApprovalRequest(id, rowVersion, null));
    }

    private static void ShouldConflict(ServiceResult res, MessageRef message)
    {
        res.IsConflict.Should().BeTrue(Dump(res));
        res.Message.Should().Be(message.Resolve(null));
    }

    private async Task SetOverride(PurchaseRig r, PolicyDefinition def, string value)
    {
        Db.Context.Add(new ModulePolicy { ModuleId = r.ModuleId, Code = def.Code, Value = value, ModifiedUserId = "admin" });
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
    }

    /// <summary>失敗當下的注入：直接改資料列（不進變更追蹤）。</summary>
    private async Task SetTask(long taskId, string status, string? assignee = null)
    {
        var task = Db.Context.Set<ApprovalTask>().Where(t => t.Id == taskId);
        await task.ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, status));
        if (assignee is not null) await task.ExecuteUpdateAsync(s => s.SetProperty(t => t.AssigneeUserId, assignee));
    }

    /// <summary>注入一筆別人剛寫的 add-sign 歷程（加簽總數算歷程筆數，ExecuteUpdate 加不了列；用原生 SQL 而不是 Add，免得新列進了追蹤）。</summary>
    private Task InjectAddSignAction(long instanceId) =>
        Db.Context.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"Dynamic_ApprovalAction\" (\"InstanceId\", \"Action\", \"ActorUserId\", \"CreatedTime\", \"OperationId\") VALUES ({0}, 'add-sign', 'emp9', '2026-11-04 09:00:00', 'other')",
            instanceId);

    // ───────── 處理權：任務層不比對請求版本 ─────────

    [Fact]
    public async Task BranchApprove_StaleRequestRowVersion_StillSucceeds()
    {
        // 法務同意後，財務拿同意前的畫面版本同意：任務層動作不比對請求版本（J16），照常成功
        var (_, id) = await Boot(ForkJoin());
        var stale = Inst(id).RowVersion;
        await MustApprove(id, "fin1");

        var res = await Approve(Open(id, "buyer2").Id, "buyer2", stale);

        res.IsSuccess.Should().BeTrue(Dump(res));
        OpenUsers(id).Should().Equal("gm");
    }

    [Fact]
    public async Task Claim_FailsTwice_RetriesAndSucceeds()
    {
        var (_, id) = await Boot(ForkJoin());
        var before = Inst(id).RowVersion;
        _claim.Arm(2);

        var res = await Approve(Open(id, "fin1").Id, "fin1", before);

        res.IsSuccess.Should().BeTrue(Dump(res));
        _claim.Calls.Should().Be(3);
        Inst(id).RowVersion.Should().NotBe(before);
        TaskOf(id, "fin1").Status.Should().Be(ApprovalTaskStatus.Approved);
    }

    [Fact]
    public async Task Claim_FailsFiveTimes_409Busy_NothingWritten()
    {
        var (_, id) = await Boot(ForkJoin());
        var before = Inst(id).RowVersion;
        var tasks = TasksOf(id).Select(t => (t.Id, t.Status)).ToList();
        var runs = RunsOf(id).Select(r => (r.Id, r.Status)).ToList();
        var actions = ActionsOf(id).Count;
        _claim.Arm(ApprovalGraphLimits.MaxClaimAttempts);

        var res = await Approve(Open(id, "fin1").Id, "fin1", before);

        ShouldConflict(res, ApprovalMessages.Busy);
        _claim.Calls.Should().Be(ApprovalGraphLimits.MaxClaimAttempts);
        Inst(id).RowVersion.Should().Be(before);
        TasksOf(id).Select(t => (t.Id, t.Status)).Should().Equal(tasks);
        RunsOf(id).Select(r => (r.Id, r.Status)).Should().Equal(runs);
        ActionsOf(id).Count.Should().Be(actions);
    }

    [Fact]
    public async Task Claim_RetryAfterConcurrentCommit_SeesFreshTaskStatus()
    {
        // Review Focus 5：或簽兩席，fin1 的請求在處理權之前已讀過自己的任務；第一次條件更新失敗的當下，另一席同意、自己的任務被關閉。
        // 重試成功後若還拿著追蹤中的舊任務（Open），就會誤判成功；reload 之後是 closed → 這一關已由他人處理
        var (_, id) = await Boot(ForkJoin(aSource: Users("fin1", "fin2")));
        var mine = Open(id, "fin1").Id;
        var other = Open(id, "fin2").Id;
        _claim.Arm(1, async () =>
        {
            await SetTask(other, ApprovalTaskStatus.Approved);
            await SetTask(mine, ApprovalTaskStatus.Closed);
        });

        var res = await Approve(mine, "fin1", Inst(id).RowVersion);

        ShouldConflict(res, ApprovalMessages.StageHandled);
        _claim.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Claim_RetryAfterInstanceRejected_409InstanceReturned()
    {
        // 第一次條件更新失敗的當下，單據被另一條分支退回；重試取得處理權後 reload，看到已退回
        var (_, id) = await Boot(ForkJoin());
        _claim.Arm(1, () => Db.Context.Set<ApprovalInstance>().Where(i => i.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, ApprovalInstanceStatus.Rejected)));

        var res = await Approve(Open(id, "fin1").Id, "fin1", Inst(id).RowVersion);

        ShouldConflict(res, ApprovalMessages.InstanceReturned);
    }

    [Fact]
    public async Task Claim_RetryReloadsAllTrackedRuns()
    {
        // 審查 plan-rev2 S4：先前的請求已把另一條分支的節點實例與任務載入共用的 DbContext（追蹤中是 active／open）；
        // 第一次條件更新失敗的當下，那條分支同意了。重試後 handler 看到的必須是新值：退回發起人只關還在走的執行線，不能把已同意的那條關掉
        var (_, id) = await Boot(ForkJoin());
        _ = Db.Context.Set<ApprovalStageRun>().Where(r => r.InstanceId == id).ToList();
        _ = Db.Context.Set<ApprovalTask>().Where(t => t.InstanceId == id).ToList();
        var bTask = Open(id, "buyer2").Id;
        var bRun = RunsOf(id).Single(r => r.NodeId == "b").Id;
        _claim.Arm(1, async () =>
        {
            await SetTask(bTask, ApprovalTaskStatus.Approved);
            await Db.Context.Set<ApprovalStageRun>().Where(r => r.Id == bRun)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, ApprovalStageStatus.Approved));
        });

        var res = await RejectToInitiator(Open(id, "fin1").Id, "fin1", Inst(id).RowVersion);

        res.IsSuccess.Should().BeTrue(Dump(res));
        RunsOf(id).Single(r => r.Id == bRun).Status.Should().Be(ApprovalStageStatus.Approved);
        TasksOf(id).Single(t => t.Id == bTask).Status.Should().Be(ApprovalTaskStatus.Approved);
    }

    [Fact]
    public async Task AddSign_TwoBranchesAtLimitMinusOne_SecondGets400()
    {
        // 上限 2、已有 1 筆：第二個請求在載入前看到 1（通過快速失敗），處理權失敗的當下另一條分支剛加簽，處理權後重算為 2 → 400
        var (r, id) = await Boot(ForkJoin());
        await SetOverride(r, PolicyCatalog.ApprovalMaxAddSignStages, "2");
        As("fin1");
        (await Send(new AddSignTaskRequest(Open(id, "fin1").Id, Inst(id).RowVersion, null, null, ["emp1"], ApprovalModes.Any))).IsSuccess.Should().BeTrue();
        _claim.Arm(1, () => InjectAddSignAction(id));
        var actions = ActionsOf(id).Count;

        As("buyer2");
        var res = await Send(new AddSignTaskRequest(Open(id, "buyer2").Id, Inst(id).RowVersion, null, null, ["emp2"], ApprovalModes.Any));

        res.IsSuccess.Should().BeFalse();
        res.IsConflict.Should().BeFalse();
        res.Errors["userIds"].Should().Equal(ApprovalMessages.AddSignLimit(2).Resolve(null));
        TaskOf(id, "buyer2").Status.Should().Be(ApprovalTaskStatus.Open);
        RunsOf(id).Count(x => x.StageCode is null).Should().Be(1);
        ActionsOf(id).Count.Should().Be(actions);
    }

    [Fact]
    public async Task Approve_OneVoteChecked_AfterClaim()
    {
        // 一人一票的檢查在處理權之後：會簽兩席，fin1 同意；第一次失敗的當下，另一席被改成 fin1 名下且已同意 → 重新讀取後判斷為已投票
        var (_, id) = await Boot(ForkJoin(ApprovalModes.All, ApprovalModes.All, Users("fin1", "fin2")));
        var mine = Open(id, "fin1").Id;
        var seat2 = Open(id, "fin2").Id;
        _claim.Arm(1, () => SetTask(seat2, ApprovalTaskStatus.Approved, assignee: "fin1"));

        var res = await Approve(mine, "fin1", Inst(id).RowVersion);

        ShouldConflict(res, ApprovalMessages.StageHandled);
    }

    [Fact]
    public async Task Transfer_StateCheckAfterClaim()
    {
        // 轉交對象「在這一關已有任務」的判斷讀處理權後的任務：失敗的當下，另一席被改派給轉交對象 → 400
        var (_, id) = await Boot(ForkJoin(aSource: Users("fin1", "fin2")));
        var mine = Open(id, "fin1").Id;
        var seat2 = Open(id, "fin2").Id;
        _claim.Arm(1, () => SetTask(seat2, ApprovalTaskStatus.Open, assignee: "emp1"));

        As("fin1");
        var res = await Send(new TransferTaskRequest(mine, Inst(id).RowVersion, "emp1", null));

        res.IsSuccess.Should().BeFalse();
        res.Errors["userId"].Should().Equal(ApprovalMessages.TargetInStage.Resolve(null));
        TaskOf(id, "fin1").Status.Should().Be(ApprovalTaskStatus.Open);
    }

    [Fact]
    public async Task Reassign_StateCheckAfterClaim()
    {
        var (_, id) = await Boot(ForkJoin(aSource: Users("fin1", "fin2")));
        var mine = Open(id, "fin1").Id;
        var seat2 = Open(id, "fin2").Id;
        _claim.Arm(1, () => SetTask(seat2, ApprovalTaskStatus.Open, assignee: "emp1"));

        As("admin");
        var res = await Send(new ReassignTaskRequest(mine, Inst(id).RowVersion, "emp1", null));

        res.IsSuccess.Should().BeFalse();
        res.Errors["userId"].Should().Equal(ApprovalMessages.TargetInStage.Resolve(null));
        TaskOf(id, "fin1").Status.Should().Be(ApprovalTaskStatus.Open);
    }

    // ───────── 晚到動作的訊息（U13、J17） ─────────

    [Fact]
    public async Task LateApprove_BranchCancelled_409BranchCompleted()
    {
        // 任一到齊：法務先同意，財務的任務因此被取消；財務再按同意（單據仍在簽核中，從 handler 的任務狀態檢查出口回應）
        var (_, id) = await Boot(ForkJoin(ApprovalModes.Any));
        var late = Open(id, "buyer2").Id;
        await MustApprove(id, "fin1");

        var res = await Approve(late, "buyer2", Inst(id).RowVersion);

        ShouldConflict(res, ApprovalMessages.BranchCompleted);
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Pending);
    }

    [Fact]
    public async Task LateApprove_AnyJoinReachedEnd_409BranchCompleted()
    {
        // 審查 plan-rev2 S3：任一到齊、匯合後直接結束，財務同意讓單據核准後，法務再按同意：從 LoadAsync 處理權後的出口回應，仍是「其他分支已完成」
        var (_, id) = await Boot(AnyJoinThenEnd());
        var late = Open(id, "fin1").Id;
        await MustApprove(id, "buyer2");
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Approved);

        var res = await Approve(late, "fin1", Inst(id).RowVersion);

        ShouldConflict(res, ApprovalMessages.BranchCompleted);
    }

    [Fact]
    public async Task LateApprove_InstanceRejectedByOtherBranch_409InstanceReturned()
    {
        // 全部到齊：法務退回發起人後，財務在沒重新整理的畫面按同意（LoadAsync 在處理權前不再先回 StageHandled）
        var (_, id) = await Boot(ForkJoin());
        var late = Open(id, "buyer2").Id;
        var rv = Inst(id).RowVersion;
        (await RejectToInitiator(Open(id, "fin1").Id, "fin1", rv)).IsSuccess.Should().BeTrue();

        var res = await Approve(late, "buyer2", rv);

        ShouldConflict(res, ApprovalMessages.InstanceReturned);
    }

    [Fact]
    public async Task LateTransfer_BranchCancelled_409BranchCompleted()
    {
        var (_, id) = await Boot(ForkJoin(ApprovalModes.Any));
        var late = Open(id, "buyer2").Id;
        await MustApprove(id, "fin1");

        As("buyer2");
        var res = await Send(new TransferTaskRequest(late, Inst(id).RowVersion, "emp1", null));

        ShouldConflict(res, ApprovalMessages.BranchCompleted);
    }

    [Fact]
    public async Task LateReject_InstanceRejectedByOtherBranch_409InstanceReturned()
    {
        var (_, id) = await Boot(ForkJoin());
        var late = Open(id, "buyer2").Id;
        var rv = Inst(id).RowVersion;
        (await RejectToInitiator(Open(id, "fin1").Id, "fin1", rv)).IsSuccess.Should().BeTrue();

        var res = await RejectToInitiator(late, "buyer2", rv);

        ShouldConflict(res, ApprovalMessages.InstanceReturned);
    }

    [Fact]
    public async Task LateWithdraw_InstanceRejected_StaleRowVersion_409InstanceReturned()
    {
        // 實例層：版本不符，但單據已被退回 → 「單據已退回」，不是「請重新整理」（單據被退回時版本一定已經換過）
        var (_, id) = await Boot(ForkJoin());
        var stale = Inst(id).RowVersion;
        (await RejectToInitiator(Open(id, "fin1").Id, "fin1", stale)).IsSuccess.Should().BeTrue();

        var res = await Withdraw(id, stale);

        ShouldConflict(res, ApprovalMessages.InstanceReturned);
    }

    [Fact]
    public async Task LateApprove_InstanceApproved_409StageHandled()
    {
        // 其他非 pending 狀態照舊：或簽另一人同意、流程走到結束，自己的節點實例不是 branchCancelled → 「這一關已由他人處理」
        var (_, id) = await Boot(OrSign());
        var late = Open(id, "fin2").Id;
        await MustApprove(id, "fin1");
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Approved);

        var res = await Approve(late, "fin2", Inst(id).RowVersion);

        ShouldConflict(res, ApprovalMessages.StageHandled);
    }

    // ───────── 實例層：撤回、終止比對請求版本，不重試 ─────────

    [Fact]
    public async Task Withdraw_AfterBranchApproved_StaleRowVersion_409InstanceChanged()
    {
        var (_, id) = await Boot(ForkJoin());
        var stale = Inst(id).RowVersion;
        await MustApprove(id, "fin1");

        var res = await Withdraw(id, stale);

        ShouldConflict(res, ApprovalMessages.InstanceChanged);
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Pending);
    }

    [Fact]
    public async Task Withdraw_FreshRowVersion_Ok()
    {
        var (_, id) = await Boot(ForkJoin());
        await MustApprove(id, "fin1");

        var res = await Withdraw(id, Inst(id).RowVersion);

        res.IsSuccess.Should().BeTrue(Dump(res));
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Withdrawn);
    }

    [Fact]
    public async Task Withdraw_ClaimFails_NoRetry_409InstanceChanged()
    {
        var (_, id) = await Boot(ForkJoin());
        _claim.Arm(1);

        var res = await Withdraw(id, Inst(id).RowVersion);

        ShouldConflict(res, ApprovalMessages.InstanceChanged);
        _claim.Calls.Should().Be(1);
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Pending);
    }

    [Fact]
    public async Task Cancel_StaleRowVersion_409InstanceChanged()
    {
        var (_, id) = await Boot(ForkJoin());
        var stale = Inst(id).RowVersion;
        await MustApprove(id, "fin1");

        As("admin");
        var res = await Send(new CancelApprovalRequest(id, stale, "終止"));

        ShouldConflict(res, ApprovalMessages.InstanceChanged);
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Pending);
    }

    // ───────── 審查 fg2f：晚到訊息優先序、實例層 reload、pending 防線 ─────────

    [Fact]
    public async Task LateApprove_BranchCancelledThenInstanceRejected_InstanceReturnedBeatsBranchCompleted()
    {
        // U13 ① 優先於 ②：任一到齊，fin1 同意（buyer2 那條被取消）→ gm 退回發起人 → buyer2 在舊畫面按同意。
        // 自己那列是 branchCancelled（滿足 ②）、單據又已退回（滿足 ①），必須回「單據已退回」
        var (_, id) = await Boot(ForkJoin(ApprovalModes.Any));
        var late = Open(id, "buyer2").Id;
        var rv = Inst(id).RowVersion;
        await MustApprove(id, "fin1");
        (await RejectToInitiator(Open(id, "gm").Id, "gm", Inst(id).RowVersion)).IsSuccess.Should().BeTrue();

        var res = await Approve(late, "buyer2", rv);

        ShouldConflict(res, ApprovalMessages.InstanceReturned);
    }

    [Fact]
    public async Task Withdraw_ClaimFails_InstanceRejectedMeanwhile_409InstanceReturned()
    {
        // 實例層處理權失敗後要先 reload 再判斷（J17 第 2 點）。共用單例 DbContext 下，前一個請求若已把實例追蹤成 rejected，不 reload 也看得到，
        // 所以這裡不先做退回請求：撤回自己的 GetAsync 追蹤到 pending，失敗當下才用 ExecuteUpdate 改成 rejected，沒有 reload 就會回「請重新整理」
        var (_, id) = await Boot(ForkJoin(ApprovalModes.All));
        _claim.Arm(1, () => Db.Context.Set<ApprovalInstance>().Where(i => i.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, ApprovalInstanceStatus.Rejected)));

        var res = await Withdraw(id, Inst(id).RowVersion);

        ShouldConflict(res, ApprovalMessages.InstanceReturned);
    }

    [Fact]
    public async Task Cancel_ClaimFails_InstanceRejectedMeanwhile_409InstanceReturned()
    {
        var (_, id) = await Boot(ForkJoin(ApprovalModes.All));
        _claim.Arm(1, () => Db.Context.Set<ApprovalInstance>().Where(i => i.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, ApprovalInstanceStatus.Rejected)));

        As("admin");
        var res = await Send(new CancelApprovalRequest(id, Inst(id).RowVersion, "終止"));

        ShouldConflict(res, ApprovalMessages.InstanceReturned);
    }

    [Fact]
    public async Task Withdraw_FreshRowVersion_InstanceRejected_409InstanceReturned_Untouched()
    {
        // 處理權之後的 pending 判斷是撤回的唯一防線（撤回沒有 me.Status 檢查，沒有 active 列時 CanWithdraw 為 true）：帶最新版本也不能撤回已退回的單
        var (_, id) = await Boot(ForkJoin(ApprovalModes.All));
        (await RejectToInitiator(Open(id, "fin1").Id, "fin1", Inst(id).RowVersion)).IsSuccess.Should().BeTrue();

        var res = await Withdraw(id, Inst(id).RowVersion);

        ShouldConflict(res, ApprovalMessages.InstanceReturned);
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Rejected);
    }

    [Fact]
    public async Task Cancel_FreshRowVersion_InstanceApproved_409StageHandled_Untouched()
    {
        // 同上，終止也只靠這道判斷：帶最新版本也不能終止已核准的單
        var (_, id) = await Boot(OrSign());
        await MustApprove(id, "fin1");
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Approved);

        As("admin");
        var res = await Send(new CancelApprovalRequest(id, Inst(id).RowVersion, "終止"));

        ShouldConflict(res, ApprovalMessages.StageHandled);
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Approved);
    }

    [Fact]
    public async Task LateReassign_BranchCancelled_409BranchCompleted()
    {
        // 改派要把本人的節點實例（runs）一併傳給 TaskNotOpen，晚到訊息才認得出「分支被取消」
        var (_, id) = await Boot(ForkJoin(ApprovalModes.Any));
        var late = Open(id, "buyer2").Id;
        await MustApprove(id, "fin1");

        As("admin");
        var res = await Send(new ReassignTaskRequest(late, Inst(id).RowVersion, "emp1", null));

        ShouldConflict(res, ApprovalMessages.BranchCompleted);
    }

    // ───────── sequence 完全照第一版 ─────────

    private static ApprovalDefinition SequenceTwoApprovers() =>
        Def(null, Stage("s1", Users("fin1", "fin2"), mode: ApprovalModes.All), Stage("s2", Users("gm")));

    [Fact]
    public async Task Sequence_LateApprove_AfterReject_StillStageHandled()
    {
        var (_, id) = await Boot(SequenceTwoApprovers());
        var late = Open(id, "fin2").Id;
        var rv = Inst(id).RowVersion;
        (await RejectToInitiator(Open(id, "fin1").Id, "fin1", rv)).IsSuccess.Should().BeTrue();

        var res = await Approve(late, "fin2", rv);

        ShouldConflict(res, ApprovalMessages.StageHandled);
    }

    [Fact]
    public async Task Sequence_LateWithdraw_AfterReject_StillStageHandled()
    {
        var (_, id) = await Boot(SequenceTwoApprovers());
        var stale = Inst(id).RowVersion;
        (await RejectToInitiator(Open(id, "fin1").Id, "fin1", stale)).IsSuccess.Should().BeTrue();

        var res = await Withdraw(id, stale);

        ShouldConflict(res, ApprovalMessages.StageHandled);
    }

    [Fact]
    public async Task Sequence_LateApprove_EndedAndFlowVersionMissing_StillStageHandled()
    {
        // 審查 plan-rev2 S1：判斷 graph 要先取定義，但 sequence 維持「先判 pending、再取定義」：已結束而且快照找不到綁定版本，仍回 StageHandled，不是 FlowVersionMissing
        var (_, id) = await Boot(SequenceTwoApprovers());
        var late = Open(id, "fin2").Id;
        var rv = Inst(id).RowVersion;
        (await RejectToInitiator(Open(id, "fin1").Id, "fin1", rv)).IsSuccess.Should().BeTrue();
        var inst = Db.Context.Set<ApprovalInstance>().Single(i => i.Id == id);
        inst.FlowSetSnapshot = [];
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();

        var res = await Approve(late, "fin2", rv);

        ShouldConflict(res, ApprovalMessages.StageHandled);
    }

    [Fact]
    public async Task Sequence_StaleRowVersion_StillStateChanged()
    {
        // 第一版：版本對不上而自己的任務仍是 open → 請重新整理（帶 stale 代碼）；任務已處理 → 已由他人處理
        var (_, id) = await Boot(Def(null, Stage("s1", Users("fin1", "fin2"), mode: ApprovalModes.All), Stage("s2", Users("gm"))));
        var stale = Inst(id).RowVersion;
        var seat2 = Open(id, "fin2").Id;
        await MustApprove(id, "fin1");

        var res = await Approve(seat2, "fin2", stale);

        ShouldConflict(res, ApprovalMessages.StateChanged);
        res.Code.Should().Be("stale");
        TaskOf(id, "fin2").Status.Should().Be(ApprovalTaskStatus.Open);
    }

    [Fact]
    public async Task Sequence_AddSignAtLimit_Still400BeforeLoad()
    {
        // 第一版的加簽總數檢查在載入實例之前：上限已用完時，就算帶的是過期版本也先回 400，不是 409
        var (r, id) = await Boot(SequenceTwoApprovers());
        await SetOverride(r, PolicyCatalog.ApprovalMaxAddSignStages, "1");
        As("fin1");
        (await Send(new AddSignTaskRequest(Open(id, "fin1").Id, Inst(id).RowVersion, null, null, ["emp1"], ApprovalModes.Any))).IsSuccess.Should().BeTrue();

        As("fin2");
        var res = await Send(new AddSignTaskRequest(Open(id, "fin2").Id, "stale-version", null, null, ["emp2"], ApprovalModes.Any));

        res.IsConflict.Should().BeFalse();
        res.Errors["userIds"].Should().Equal(ApprovalMessages.AddSignLimit(1).Resolve(null));
    }
}
