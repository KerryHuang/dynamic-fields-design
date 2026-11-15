using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.ApplicationTests.Dynamic.Approvals.ApprovalGraphTestKit;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>
/// 流程圖分支內的後加簽、轉交、改派與代理（計畫 fg2 Task 7；流程圖設計 §8.6、J10、J11、U11；審查 fg2b S1 的探針收成正式測試）。簽核人：a、a1＝fin1、a2＝fin2、b＝buyer2、c＝gm；發起人 buyer1。
/// </summary>
public class ApprovalGraphAddSignTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    private sealed class Clock : IBusinessTime
    {
        public DateTime Now { get; set; } = new(2026, 11, 4, 9, 0, 0);
        public DateOnly Today => DateOnly.FromDateTime(Now);
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IBusinessTime>(new Clock());
    }

    // ───────── 圖與情境 ─────────

    /// <summary>start → a → c → end。</summary>
    private static ApprovalDefinition Linear() => G(
        [Start(), Approval("a", Users("fin1")), Approval("c", Users("gm")), End()],
        [E("e0", "start", "a"), E("ac", "a", "c"), E("ce", "c", "end")]);

    /// <summary>start → fork f → 分支 a（fin1）、b（buyer2）→ join j（全部到齊）→ c → end；aMode 是 a 的會簽或簽。</summary>
    private static ApprovalDefinition ForkJoin(string joinMode = ApprovalModes.All, string aMode = ApprovalModes.Any, ApproverSource? aSource = null) => G(
        [Start(), Fork("f"), Approval("a", aSource ?? Users("fin1"), mode: aMode), Approval("b", Users("buyer2")), Join("j", "f", joinMode),
         Approval("c", Users("gm")), End()],
        [E("e0", "start", "f"), E("fa", "f", "a"), E("fb", "f", "b"), E("aj", "a", "j"), E("bj", "b", "j"), E("jc", "j", "c"), E("ce", "c", "end")]);

    /// <summary>分支 fa 內連續兩個簽核 a1（fin1）→ a2（fin2），分支 fb 是 b（buyer2）；全部到齊後 c（gm）。</summary>
    private static ApprovalDefinition BranchSerial() => G(
        [Start(), Fork("f"), Approval("a1", Users("fin1")), Approval("a2", Users("fin2")), Approval("b", Users("buyer2")), Join("j", "f"),
         Approval("c", Users("gm")), End()],
        [E("e0", "start", "f"), E("fa", "f", "a1"), E("fb", "f", "b"), E("a12", "a1", "a2"), E("a2j", "a2", "j"), E("bj", "b", "j"),
         E("jc", "j", "c"), E("ce", "c", "end")]);

    /// <summary>
    /// 分支 fa：s1（mgrA）→ s2（fin1、fin2 會簽）→ s3（gm）；分支 fb 是 b（buyer2）；全部到齊。第一版巢狀加簽的順序案例搬到並行分支裡。
    /// </summary>
    private static ApprovalDefinition ChainInBranch() => G(
        [Start(), Fork("f"), Approval("s1", Users("mgrA")), Approval("s2", Users("fin1", "fin2"), mode: ApprovalModes.All), Approval("s3", Users("gm")),
         Approval("b", Users("buyer2")), Join("j", "f"), End()],
        [E("e0", "start", "f"), E("fa", "f", "s1"), E("fb", "f", "b"), E("s12", "s1", "s2"), E("s23", "s2", "s3"), E("s3j", "s3", "j"), E("bj", "b", "j"),
         E("je", "j", "end")]);

    private async Task<PurchaseRig> Rig(ApprovalDefinition graph)
    {
        var r = await BuildPurchase();
        await SeedGraphFlow(r, "po-g", r.Confirmed, 1, graph);
        (await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed), Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received),
            Plain(r.Confirmed, r.Cancelled)])).IsSuccess.Should().BeTrue();
        await GrantEveryone(r.Module, r.Extra);
        return r;
    }

    private async Task<(PurchaseRig R, long Id)> Boot(ApprovalDefinition graph)
    {
        var r = await Rig(graph);
        As("buyer1");
        var po = await NewPo(r, price: 5000);
        return (r, (await MustSubmit(po.Id, r.Confirmed)).InstanceId!.Value);
    }

    private List<ApprovalStageRun> RunsOf(long id) =>
        Db.Context.Set<ApprovalStageRun>().AsNoTracking().Where(x => x.InstanceId == id).OrderBy(x => x.Seq).ToList();

    private ApprovalTask Open(long id, string user) => TasksOf(id).Single(t => t.AssigneeUserId == user && t.Status == ApprovalTaskStatus.Open);

    private List<string> OpenUsers(long id) =>
        TasksOf(id).Where(t => t.Status == ApprovalTaskStatus.Open).Select(t => t.AssigneeUserId).Order().ToList();

    private Task<ServiceResult> Approve(long id, string user)
    {
        As(user);
        return Send(new ApproveTaskRequest(Open(id, user).Id, Inst(id).RowVersion, null, null));
    }

    private async Task MustApprove(long id, string user)
    {
        var res = await Approve(id, user);
        res.IsSuccess.Should().BeTrue(Dump(res));
    }

    private Task<ServiceResult> AddSign(long id, string user, string[] to, string mode = ApprovalModes.Any)
    {
        As(user);
        return Send(new AddSignTaskRequest(Open(id, user).Id, Inst(id).RowVersion, null, null, [.. to], mode));
    }

    private async Task MustAddSign(long id, string user, params string[] to)
    {
        var res = await AddSign(id, user, to);
        res.IsSuccess.Should().BeTrue(Dump(res));
    }

    private Task<ServiceResult> Return(long id, string user)
    {
        As(user);
        return Send(new RejectTaskRequest(Open(id, user).Id, Inst(id).RowVersion, "請重看", RejectTaskHandler.ToPrevious));
    }

    private Dictionary<long, int> SeqById(long id) => RunsOf(id).ToDictionary(x => x.Id, x => x.Seq);

    /// <summary>後一次的 Seq 不得比前一次小：只增不減（J11）。</summary>
    private static void AssertSeqNeverDecreases(Dictionary<long, int> before, Dictionary<long, int> after) =>
        before.Where(kv => after.TryGetValue(kv.Key, out var now) && now < kv.Value).Should().BeEmpty();

    private async Task SetOverride(PurchaseRig r, PolicyDefinition def, string value)
    {
        Db.Context.Add(new ModulePolicy { ModuleId = r.ModuleId, Code = def.Code, Value = value, ModifiedUserId = "admin" });
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
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

    // ───────── 後加簽：寫入與啟動 ─────────

    /// <summary>審查 fg2b S1 的探針：start → a → c → end；a 的簽核人加簽，加簽關卡要先啟動，不能少簽直接跳到 c。</summary>
    [Fact]
    public async Task AddSignOnGraphNode_AddSignActivatedBeforeNextNode()
    {
        var (_, id) = await Boot(Linear());

        var res = await AddSign(id, "fin1", ["emp1"]);

        res.IsSuccess.Should().BeTrue(Dump(res));
        OpenUsers(id).Should().Equal("emp1");
        RunsOf(id).Should().NotContain(x => x.NodeId == "c");
        await MustApprove(id, "emp1");
        OpenUsers(id).Should().Equal("gm");
    }

    [Fact]
    public async Task AddSign_RunCarriesGraphFields()
    {
        // J10：加簽列 StageCode = null、NodeId = 主節點、NodeKind = approval、BranchKey 同主節點、InEdgeId = null；Seq 在最大值之後
        var (_, id) = await Boot(ForkJoin(aMode: ApprovalModes.All, aSource: Users("fin1", "fin2")));
        var maxBefore = RunsOf(id).Max(x => x.Seq);

        await MustAddSign(id, "fin1", "emp1");

        var added = RunsOf(id).Single(x => x.StageCode is null);
        added.Should().Match<ApprovalStageRun>(x => x.NodeId == "a" && x.NodeKind == ApprovalNodeKinds.Approval && x.BranchKey == "fa" && x.InEdgeId == null
            && x.Status == ApprovalStageStatus.Queued && x.AddedByUserId == "fin1");
        added.AddSignUserIds.Should().Equal("emp1");
        added.Seq.Should().BeGreaterThan(maxBefore);
        OpenUsers(id).Should().Equal("buyer2", "fin2");   // 會簽沒完成：加簽排隊中，沒有任務
    }

    [Fact]
    public async Task AddSign_InBranch_QueuedSameBranchKey_ActivatesBeforeAdvancing()
    {
        var (_, id) = await Boot(ForkJoin());

        await MustAddSign(id, "fin1", "emp1");   // a 是單人簽：fin1 的同意讓 a 完成，加簽立刻啟動

        var runs = RunsOf(id);
        runs.Single(x => x.NodeId == "a" && x.StageCode == "a").Status.Should().Be(ApprovalStageStatus.Approved);
        var added = runs.Single(x => x.StageCode is null);
        added.Status.Should().Be(ApprovalStageStatus.Active);
        added.BranchKey.Should().Be("fa");
        OpenUsers(id).Should().Equal("buyer2", "emp1");
        Inst(id).CurrentStageRunId.Should().Be(runs.Where(x => x.Status == ApprovalStageStatus.Active).MinBy(x => x.Seq)!.Id);
        runs.Should().NotContain(x => x.NodeId == "j" && x.NodeKind == ApprovalNodeKinds.Join);   // 還沒有執行線到匯合
        runs.Select(x => x.Seq).Should().OnlyHaveUniqueItems();   // 審查 fg2d S3：Seq 不撞別條分支的列
        added.Seq.Should().BeGreaterThan(runs.Single(x => x.NodeId == "b").Seq);   // 加簽列排在別條分支的列之後（a 的 Seq 本來比 b 小）

        await MustApprove(id, "emp1");   // 加簽完成才沿 a 的出線到匯合
        RunsOf(id).Single(x => x.NodeKind == ApprovalNodeKinds.Join).Status.Should().Be(ApprovalStageStatus.Waiting);
        await MustApprove(id, "buyer2");
        OpenUsers(id).Should().Equal("gm");
    }

    [Fact]
    public async Task AddSign_OtherBranchUnaffected()
    {
        var (_, id) = await Boot(ForkJoin());
        await MustAddSign(id, "fin1", "emp1");
        var bBefore = Open(id, "buyer2").Id;

        await MustApprove(id, "buyer2");   // 另一條分支照常同意、到匯合等待，不受加簽影響

        Open(id, "emp1").Status.Should().Be(ApprovalTaskStatus.Open);
        RunsOf(id).Single(x => x.NodeId == "b").Status.Should().Be(ApprovalStageStatus.Approved);
        TasksOf(id).Single(t => t.Id == bBefore).Status.Should().Be(ApprovalTaskStatus.Approved);
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Pending);
        RunsOf(id).Single(x => x.StageCode is null).Status.Should().Be(ApprovalStageStatus.Active);
        RunsOf(id).Select(x => x.Seq).Should().OnlyHaveUniqueItems();   // 審查 fg2d S3
        RunsOf(id).Single(x => x.StageCode is null).Seq.Should().BeGreaterThan(RunsOf(id).Single(x => x.NodeId == "b").Seq);
        await MustApprove(id, "emp1");
        OpenUsers(id).Should().Equal("gm");
    }

    [Fact]
    public async Task AddSign_TwoBranchesEachQueued_EachActivatesOwn()
    {
        // 兩條分支的會簽節點各有一筆排隊中的加簽：一條完成只啟動它自己的，不會啟動別條分支的
        var def = G(
            [Start(), Fork("f"), Approval("a", Users("fin1", "fin2"), mode: ApprovalModes.All), Approval("b", Users("buyer2", "emp3"), mode: ApprovalModes.All),
             Join("j", "f"), End()],
            [E("e0", "start", "f"), E("fa", "f", "a"), E("fb", "f", "b"), E("aj", "a", "j"), E("bj", "b", "j"), E("je", "j", "end")]);
        var (_, id) = await Boot(def);
        await MustAddSign(id, "fin1", "emp1");
        await MustAddSign(id, "buyer2", "emp2");
        RunsOf(id).Where(x => x.StageCode is null).Select(x => (x.NodeId, x.BranchKey, x.Status)).Should().BeEquivalentTo(
            [("a", "fa", ApprovalStageStatus.Queued), ("b", "fb", ApprovalStageStatus.Queued)]);

        await MustApprove(id, "fin2");   // a 完成：只有 a 的加簽（emp1）啟動

        OpenUsers(id).Should().Equal("emp1", "emp3");
        RunsOf(id).Single(x => x.NodeId == "b" && x.StageCode is null).Status.Should().Be(ApprovalStageStatus.Queued);

        await MustApprove(id, "emp3");   // b 完成：輪到 b 的加簽（emp2）
        OpenUsers(id).Should().Equal("emp1", "emp2");
        await MustApprove(id, "emp1");
        await MustApprove(id, "emp2");
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Approved);
    }

    [Fact]
    public async Task AddSign_Nested_InBranch_OrderSameAsSequence_SeqMonotonic()
    {
        // 第一版 AddSign_NestedWhileOthersQueued：fin1、fin2 各加簽得 A、B；A 裡 emp1 再加簽得 C → 順序 A → C → B；graph 的 Seq 只增不減
        var (_, id) = await Boot(ChainInBranch());
        await MustApprove(id, "mgrA");
        var seq = SeqById(id);

        await MustAddSign(id, "fin1", "emp1");
        AssertSeqNeverDecreases(seq, seq = SeqById(id));
        await MustAddSign(id, "fin2", "emp2");   // s2 會簽完成，A 啟動
        AssertSeqNeverDecreases(seq, seq = SeqById(id));
        await MustAddSign(id, "emp1", "emp3");
        AssertSeqNeverDecreases(seq, seq = SeqById(id));

        var line = RunsOf(id).Where(x => x.BranchKey == "fa").ToList();
        line.Select(x => (x.StageCode, x.Status, x.AddedByUserId)).Should().Equal(
            ("s1", "approved", null), ("s2", "approved", null), (null, "approved", "fin1"), (null, "active", "emp1"), (null, "queued", "fin2"));
        RunsOf(id).Select(x => x.Seq).Should().OnlyHaveUniqueItems();
        OpenUsers(id).Should().Equal("buyer2", "emp3");

        await MustApprove(id, "emp3");
        OpenUsers(id).Should().Equal("buyer2", "emp2");
        await MustApprove(id, "emp2");
        OpenUsers(id).Should().Equal("buyer2", "gm");   // 加簽都簽完才進 s3
        RunsOf(id).Last(x => x.StageCode is not null).StageCode.Should().Be("s3");
    }

    [Fact]
    public async Task AddSign_RequeuedAfterReturn_ThenNestedAddSigns_OrderSameAsSequence()
    {
        // 審查 fg2d S4（偏離 4 的邊角）：fin1 在 a 加簽 A（emp1、emp2 會簽）→ emp1 在 A 加簽 C → emp2 同意，A 完成、C 啟動
        // → emp3 在 C 退回上一關，A 重啟成 A′、C 重排成 C2 → emp1 在 A′ 加簽 D、emp2 加簽 E；順序與第一版的數字算法相同：D → E → C2 → c
        var (_, id) = await Boot(Linear());
        (await AddSign(id, "fin1", ["emp1", "emp2"], ApprovalModes.All)).IsSuccess.Should().BeTrue();
        await MustAddSign(id, "emp1", "emp3");
        await MustApprove(id, "emp2");
        OpenUsers(id).Should().Equal("emp3");
        (await Return(id, "emp3")).IsSuccess.Should().BeTrue();
        OpenUsers(id).Should().Equal("emp1", "emp2");

        await MustAddSign(id, "emp1", "mgrA");   // D
        await MustAddSign(id, "emp2", "fin2");   // E；A′ 完成，D 啟動

        RunsOf(id).Select(x => x.Seq).Should().OnlyHaveUniqueItems();
        OpenUsers(id).Should().Equal("mgrA");
        await MustApprove(id, "mgrA");
        OpenUsers(id).Should().Equal("fin2");
        await MustApprove(id, "fin2");
        OpenUsers(id).Should().Equal("emp3");
        await MustApprove(id, "emp3");
        OpenUsers(id).Should().Equal("gm");
    }

    [Fact]
    public async Task AddSign_LimitCountsAcrossBranches()
    {
        // 加簽關卡數的政策以整個實例計：一條分支用掉的額度，另一條分支也看得到
        var (r, id) = await Boot(ForkJoin(aMode: ApprovalModes.All, aSource: Users("fin1", "fin2")));
        await SetOverride(r, PolicyCatalog.ApprovalMaxAddSignStages, "1");
        await MustAddSign(id, "fin1", "emp1");

        var res = await AddSign(id, "buyer2", ["emp2"]);

        res.IsSuccess.Should().BeFalse();
        res.Errors["userIds"].Should().Equal(ApprovalMessages.AddSignLimit(1).Resolve(null));
        Open(id, "buyer2").Status.Should().Be(ApprovalTaskStatus.Open);
        RunsOf(id).Count(x => x.StageCode is null).Should().Be(1);
    }

    [Fact]
    public async Task AddSign_QueuedInBranch_CancelledByAnyJoin_ClosedWithReason()
    {
        // 排隊中的加簽屬於被任一到齊取消的分支：一併關閉並記原因，任務不會啟動
        var (_, id) = await Boot(ForkJoin(ApprovalModes.Any, ApprovalModes.All, Users("fin1", "fin2")));
        await MustAddSign(id, "fin1", "emp1");

        await MustApprove(id, "buyer2");   // b 先到匯合：任一到齊通過，分支 a 被取消

        var added = RunsOf(id).Single(x => x.StageCode is null);
        added.Status.Should().Be(ApprovalStageStatus.Closed);
        added.CloseReason.Should().Be(ApprovalCloseReasons.BranchCancelled);
        TasksOf(id).Should().NotContain(t => t.AssigneeUserId == "emp1");
        Notices("emp1", NotificationKinds.ApprovalAddSigned).Should().Be(0);
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Pending);
        OpenUsers(id).Should().Equal("gm");
    }

    [Fact]
    public async Task ReturnPrevious_ToAddSignInBranch_RequeuedSameBranch()
    {
        // fin1 在 a1 加簽 emp1；emp1 退回上一關到 a1：被退回的加簽重新排隊，仍屬 a1、分支 fa；a1 重簽完才再輪到它；另一條分支的待簽不動
        var (_, id) = await Boot(BranchSerial());
        await MustAddSign(id, "fin1", "emp1");

        (await Return(id, "emp1")).IsSuccess.Should().BeTrue();

        var runs = RunsOf(id);
        var requeued = runs.Single(x => x.StageCode is null && x.Status == ApprovalStageStatus.Queued);
        requeued.Should().Match<ApprovalStageRun>(x => x.NodeId == "a1" && x.NodeKind == ApprovalNodeKinds.Approval && x.BranchKey == "fa");
        requeued.AddSignUserIds.Should().Equal("emp1");
        OpenUsers(id).Should().Equal("buyer2", "fin1");
        runs.Single(x => x.Status == ApprovalStageStatus.Active && x.NodeId == "a1").BranchKey.Should().Be("fa");

        await MustApprove(id, "fin1");   // a1 重簽完 → 先啟動排隊的加簽，不是 a2
        OpenUsers(id).Should().Equal("buyer2", "emp1");
        await MustApprove(id, "emp1");
        OpenUsers(id).Should().Equal("buyer2", "fin2");
    }

    // ───────── 轉交 ─────────

    [Fact]
    public async Task Transfer_InBranch_SameRunNewTask()
    {
        var (_, id) = await Boot(ForkJoin());
        var old = Open(id, "fin1");

        As("fin1");
        var res = await Send(new TransferTaskRequest(old.Id, Inst(id).RowVersion, "emp1", "出差中"));

        res.IsSuccess.Should().BeTrue(Dump(res));
        TasksOf(id).Single(t => t.Id == old.Id).Status.Should().Be(ApprovalTaskStatus.Transferred);
        var added = Open(id, "emp1");
        added.StageRunId.Should().Be(old.StageRunId);
        added.Reason.Should().Be(ApprovalTaskReason.Transferred);
        added.OriginalUserId.Should().Be("fin1");
        Open(id, "buyer2").Status.Should().Be(ApprovalTaskStatus.Open);   // 另一條分支不受影響
        await MustApprove(id, "emp1");
        RunsOf(id).Single(x => x.NodeId == "a").Status.Should().Be(ApprovalStageStatus.Approved);
    }

    [Fact]
    public async Task Transfer_TargetHasTaskInOtherBranch_Allowed()
    {
        // 轉交對象在同一關已有票才擋；他在另一條分支有待簽，不算同一關
        var (_, id) = await Boot(ForkJoin());

        As("fin1");
        var res = await Send(new TransferTaskRequest(Open(id, "fin1").Id, Inst(id).RowVersion, "buyer2", null));

        res.IsSuccess.Should().BeTrue(Dump(res));
        TasksOf(id).Count(t => t.AssigneeUserId == "buyer2" && t.Status == ApprovalTaskStatus.Open).Should().Be(2);
        foreach (var t in TasksOf(id).Where(t => t.AssigneeUserId == "buyer2" && t.Status == ApprovalTaskStatus.Open).ToList())   // 兩個待簽是不同分支的兩關，各自處理
        {
            As("buyer2");
            (await Send(new ApproveTaskRequest(t.Id, Inst(id).RowVersion, null, null))).IsSuccess.Should().BeTrue();
        }
        OpenUsers(id).Should().Equal("gm");
    }

    [Fact]
    public async Task Transfer_BrokenGraph_Ok()
    {
        // U11：結構不合法的實例，轉交不讀圖、照常做（轉過去的人同樣按不了同意）
        var (r, id) = await Boot(ForkJoin());
        await BreakDefinition(r, id);

        As("fin1");
        var res = await Send(new TransferTaskRequest(Open(id, "fin1").Id, Inst(id).RowVersion, "emp1", null));

        res.IsSuccess.Should().BeTrue(Dump(res));
        Open(id, "emp1").Reason.Should().Be(ApprovalTaskReason.Transferred);
    }

    // ───────── 改派 ─────────

    [Fact]
    public async Task Reassign_InBranch_OldAssigneeNotified()
    {
        var (_, id) = await Boot(ForkJoin());
        var old = Open(id, "fin1");

        As("admin");
        var res = await Send(new ReassignTaskRequest(old.Id, Inst(id).RowVersion, "emp1", "fin1 離職交接"));

        res.IsSuccess.Should().BeTrue(Dump(res));
        TasksOf(id).Single(t => t.Id == old.Id).Status.Should().Be(ApprovalTaskStatus.Reassigned);
        var added = Open(id, "emp1");
        added.StageRunId.Should().Be(old.StageRunId);
        added.Reason.Should().Be(ApprovalTaskReason.Reassigned);
        Notices("fin1", NotificationKinds.ApprovalReassignedAway).Should().Be(1);
        Notices("emp1", NotificationKinds.ApprovalTransferred).Should().Be(1);
        Open(id, "buyer2").Status.Should().Be(ApprovalTaskStatus.Open);
    }

    [Fact]
    public async Task Reassign_BrokenGraph_Ok()
    {
        var (r, id) = await Boot(ForkJoin());
        await BreakDefinition(r, id);

        As("admin");
        var res = await Send(new ReassignTaskRequest(Open(id, "fin1").Id, Inst(id).RowVersion, "emp1", null));

        res.IsSuccess.Should().BeTrue(Dump(res));
        Open(id, "emp1").Reason.Should().Be(ApprovalTaskReason.Reassigned);
    }

    // ───────── 代理 ─────────

    private async Task SetDelegate(string user, string to)
    {
        As(user);
        var res = await Send(new SaveMyApprovalDelegateRequest(to, new DateOnly(2026, 11, 4), new DateOnly(2026, 11, 5)));
        res.IsSuccess.Should().BeTrue(Dump(res));
    }

    [Fact]
    public async Task Delegate_InBranch_ApproveByDelegate_CountsAsHolder()
    {
        // a 是 fin1、fin2 會簽；fin1 代理給 emp1：emp1 同意算 fin1 那一席，fin2 同意後 a 完成
        var r = await Rig(ForkJoin(aMode: ApprovalModes.All, aSource: Users("fin1", "fin2")));
        await SetDelegate("fin1", "emp1");
        As("buyer1");
        var po = await NewPo(r, price: 5000);
        var id = (await MustSubmit(po.Id, r.Confirmed)).InstanceId!.Value;
        var seat = Open(id, "emp1");
        seat.Reason.Should().Be(ApprovalTaskReason.Delegated);
        seat.OriginalUserId.Should().Be("fin1");

        await MustApprove(id, "emp1");

        var approve = ActionsOf(id).Single(a => a.Action == ApprovalActions.Approve);
        approve.ActorUserId.Should().Be("emp1");
        approve.OnBehalfOfUserId.Should().Be("fin1");
        RunsOf(id).Single(x => x.NodeId == "a").Status.Should().Be(ApprovalStageStatus.Active);   // 還差 fin2
        await MustApprove(id, "fin2");
        RunsOf(id).Single(x => x.NodeId == "a").Status.Should().Be(ApprovalStageStatus.Approved);
        Open(id, "buyer2").Status.Should().Be(ApprovalTaskStatus.Open);
    }

    [Fact]
    public async Task Delegate_HolderAndDelegateInSameNode_OneVote()
    {
        // 一人一票看本人（J9）：fin1 處理了代理給 emp1 的那一席後，同一關 fin2 那一席被造成代理給 fin1，也不能再投
        var (_, id) = await Boot(ForkJoin(aMode: ApprovalModes.All, aSource: Users("fin1", "fin2")));
        var fin1Seat = Open(id, "fin1");
        var fin2Seat = Open(id, "fin2");
        await Db.Context.Set<ApprovalTask>().Where(t => t.Id == fin1Seat.Id).ExecuteUpdateAsync(s => s
            .SetProperty(t => t.AssigneeUserId, "emp1").SetProperty(t => t.Reason, ApprovalTaskReason.Delegated));
        await Db.Context.Set<ApprovalTask>().Where(t => t.Id == fin2Seat.Id).ExecuteUpdateAsync(s => s
            .SetProperty(t => t.AssigneeUserId, "fin1").SetProperty(t => t.Reason, ApprovalTaskReason.Delegated));
        Db.Context.ChangeTracker.Clear();

        As("fin1");
        (await Send(new ApproveTaskRequest(fin1Seat.Id, Inst(id).RowVersion, null, null))).IsSuccess.Should().BeTrue();   // 本人處理代理給 emp1 的那一席

        As("fin1");
        var second = await Send(new ApproveTaskRequest(fin2Seat.Id, Inst(id).RowVersion, null, null));
        second.IsConflict.Should().BeTrue(Dump(second));
        second.Message.Should().Be(ApprovalMessages.StageHandled.Resolve(null));
    }
}
