using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.WorkflowService;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>送出簽核（設計 §5.2、§6；劇本三的流程 A、B、C）。</summary>
public class ApprovalSubmitTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    [Fact]
    public async Task Submit_CreatesInstance_FirstStageTask_RecordStaysDraft()
    {
        var r = await Scenario3();
        As("buyer1");
        var po = await NewPo(r, price: 50000);
        var res = await MustSubmit(po.Id, r.Confirmed);

        res.Outcome.Should().Be(ApprovalOutcomes.Submitted);
        var inst = Inst(res.InstanceId!.Value);
        inst.Status.Should().Be(ApprovalInstanceStatus.Pending);
        inst.FlowSetSnapshot.Select(e => e.Priority).Should().Equal(1, 2);
        TasksOf(inst.Id).Select(t => t.AssigneeUserId).Should().Equal("buyer2");
        StateOf(r, po.Id).Should().Be(r.Draft);
        Rec(po.Id).Row.RowVersion.Should().Be(res.RecordRowVersion).And.NotBe(po.RowVersion);
        Notices("buyer2", NotificationKinds.ApprovalPending).Should().Be(1);
        var n = Db.Context.Set<Notification>().AsNoTracking().Single(x => x.UserId == "buyer2");
        (n.Link, n.SourceType, n.SourceId).Should().Be(($"/dynamic/{r.Module}/{po.Id}", "approval", inst.Id));
    }

    [Fact]
    public async Task Submit_NoMatch_Direct_TransitionsNoInstance()
    {
        var r = await Scenario3();
        As("buyer1");
        var po = await NewPo(r, price: 5000);
        var res = await MustSubmit(po.Id, r.Confirmed, status: 200);
        res.Outcome.Should().Be(ApprovalOutcomes.Direct);
        StateOf(r, po.Id).Should().Be(r.Confirmed);
        Db.Context.Set<ApprovalInstance>().AsNoTracking().Count().Should().Be(0);
    }

    [Theory]
    [InlineData(5000)]     // 原本會落到 direct
    [InlineData(150000)]   // 原本會選到後面的流程
    public async Task Submit_FlowConditionUndecidable_409_NoDirect(int total)
    {
        // 流程條件解析不了（引用的欄位已不在）不算成立也不算不成立：fail closed 回 409（主控裁決；審查建議 2）
        var r = await Scenario3();
        await SeedFlow(r, "po-z", r.Confirmed, 0, Def(Cond(999999, "gt", 0), Stage("mgr", Manager(1))));
        As("buyer1");
        var po = await NewPo(r, price: total);
        var res = await Submit(po.Id, r.Confirmed);
        res.IsConflict.Should().BeTrue(Dump(res));
        res.Message.Should().Be(ApprovalMessages.FlowConditionUndecidable("po-z"));
        StateOf(r, po.Id).Should().Be(r.Draft);
        Db.Context.Set<ApprovalInstance>().AsNoTracking().Count().Should().Be(0);
    }

    [Fact]
    public async Task Submit_FlowConditionEmptyGroup_201_FlowMatches()
    {
        // 空群組 {"all":[]} 是「沒有條件」：解析得出來（Node 為 null、無 Error），流程成立，不能當解析失敗回 409
        var r = await Scenario3();
        await SeedFlow(r, "po-e", r.Confirmed, 0, Def(JsonSerializer.SerializeToElement(new { all = Array.Empty<object>() }), Stage("mgr", Manager(1))));
        As("buyer1");
        var po = await NewPo(r, price: 5000);
        var res = await MustSubmit(po.Id, r.Confirmed);
        res.Outcome.Should().Be(ApprovalOutcomes.Submitted);
        Inst(res.InstanceId!.Value).Status.Should().Be(ApprovalInstanceStatus.Pending);
    }

    [Fact]
    public async Task Submit_AllStagesSkipped_201_Approved_Transitioned()
    {
        // 送出時每一關的條件都不成立：建實例、全部略過、直接轉移（實作者技術決定 3）
        var r = await BuildPurchase();
        await SeedFlow(r, "po-s", r.Confirmed, 1, Def(null, Stage("board", Group(BoardGroup), when: Cond(r.Total, "gt", 1000000))));
        (await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed), Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received),
            Plain(r.Confirmed, r.Cancelled)])).IsSuccess.Should().BeTrue();
        await GrantEveryone(r.Module);
        As("buyer1");
        var po = await NewPo(r, price: 5000);
        var res = await MustSubmit(po.Id, r.Confirmed);
        res.Outcome.Should().Be(ApprovalOutcomes.Approved);
        Inst(res.InstanceId!.Value).Status.Should().Be(ApprovalInstanceStatus.Approved);
        TasksOf(res.InstanceId!.Value).Should().BeEmpty();
        StateOf(r, po.Id).Should().Be(r.Confirmed);
    }

    [Fact]
    public async Task Submit_NoMatch_Block_400()
    {
        var r = await Scenario3();
        As("buyer1");
        var po = await NewPo(r, price: 5000);
        await MustSubmit(po.Id, r.Confirmed, status: 200);
        await Db.Context.Set<ApprovalFlow>().Where(f => f.Key == "po-c").ExecuteUpdateAsync(s => s.SetProperty(f => f.Active, false));

        var res = await Submit(po.Id, r.Cancelled);
        res.Errors[$"f_{r.Status}"].Should().Equal(ApprovalMessages.NoMatchingFlow);
        Db.Context.Set<ApprovalInstance>().AsNoTracking().Count().Should().Be(0);
    }

    [Fact]
    public async Task Submit_PriorityPicksFirstMatch()
    {
        var r = await Scenario3();
        As("buyer1");
        var big = await NewPo(r, price: 150000);
        var a = await MustSubmit(big.Id, r.Confirmed);
        Inst(a.InstanceId!.Value).FlowId.Should().Be(Flow("po-a").Id);
        TasksOf(a.InstanceId!.Value).Select(t => t.AssigneeUserId).Should().Equal("gm");   // 採購部主管空缺 → 往上到總部

        await Db.Context.Set<ApprovalFlow>().Where(f => f.Key == "po-a").ExecuteUpdateAsync(s => s.SetProperty(f => f.Active, false));
        var big2 = await NewPo(r, price: 150000);
        Inst((await MustSubmit(big2.Id, r.Confirmed)).InstanceId!.Value).FlowId.Should().Be(Flow("po-b").Id);
    }

    [Fact]
    public async Task Submit_InitiatorIsManager_GoesUp()
    {
        var r = await Scenario3();
        As("lead1");
        var po = await NewPo(r, price: 150000);
        TasksOf((await MustSubmit(po.Id, r.Confirmed)).InstanceId!.Value).Select(t => t.AssigneeUserId).Should().Equal("mgrA");
    }

    [Fact]
    public async Task Submit_GroupOnlyInitiator_Fallback()
    {
        var r = await Scenario3();
        As("buyer2");
        var po = await NewPo(r, price: 50000);
        var id = (await MustSubmit(po.Id, r.Confirmed)).InstanceId!.Value;
        var task = TasksOf(id).Should().ContainSingle().Subject;
        (task.AssigneeUserId, task.Reason).Should().Be(("admin", ApprovalTaskReason.Fallback));
        ActionsOf(id).Should().Contain(x => x.Action == ApprovalActions.Fallback && x.Comment == ApprovalMessages.FallbackNote("purchase-lead"));
        Notices("admin", NotificationKinds.ApprovalPending).Should().Be(1);
    }

    [Fact]
    public async Task Submit_NoEditPermission_403()
    {
        var r = await Scenario3();
        var po = await NewPo(r, price: 50000);
        As("sales01");   // 示範帳號，對這個模組沒有任何權限
        (await Submit(po.Id, r.Confirmed)).IsForbidden.Should().BeTrue();
    }

    [Fact]
    public async Task Submit_NotInTransitionGroup_400()
    {
        var r = await Scenario3(submitGroups: []);
        var wf = await SaveWorkflow(r, [
            new WorkflowTransition(r.Draft, r.Confirmed, [FinanceGroup], new TransitionApproval(true, ApprovalNoMatch.Direct)),
            Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received), Needs(r.Confirmed, r.Cancelled)
        ]);
        wf.IsSuccess.Should().BeTrue(Dump(wf));
        As("buyer1");
        var po = await NewPo(r, price: 50000);
        (await Submit(po.Id, r.Confirmed)).Errors[$"f_{r.Status}"].Should().Equal(WorkflowEnforcer.NotPermittedMessage);
        As("fin1");
        (await Submit(po.Id, r.Confirmed)).IsCreated.Should().BeTrue();
    }

    [Fact]
    public async Task Submit_StaleRowVersion_409_And_NotApprovalTransition_400()
    {
        var r = await Scenario3();
        As("buyer1");
        var po = await NewPo(r, price: 50000);
        var stale = await Submit(po.Id, r.Confirmed, rowVersion: "old");
        stale.IsConflict.Should().BeTrue();
        stale.Code.Should().Be("stale");
        (await Submit(po.Id, r.Partial)).Errors[$"f_{r.Status}"].Should().Equal(WorkflowEnforcer.NotAllowedMessage);
    }

    [Fact]
    public async Task Submit_Twice_Second409()
    {
        var r = await Scenario3();
        As("buyer1");
        var po = await NewPo(r, price: 50000);
        await MustSubmit(po.Id, r.Confirmed);
        var again = await Submit(po.Id, r.Confirmed);
        again.IsConflict.Should().BeTrue();
        again.Message.Should().Be("這張單已在簽核中");
    }

    [Fact]
    public async Task Submit_Twice_SameStaleRowVersion_SecondAlreadyPending()
    {
        // 兩個分頁拿同一個記錄版本先後送出：第二個要回「已在簽核中」，不是「資料已被修改」（設計 §8.4；審查必修 2）
        var r = await Scenario3();
        As("buyer1");
        var po = await NewPo(r, price: 50000);
        var stale = Rec(po.Id).Row.RowVersion;
        (await Submit(po.Id, r.Confirmed, rowVersion: stale)).IsCreated.Should().BeTrue();
        var again = await Submit(po.Id, r.Confirmed, rowVersion: stale);
        again.IsConflict.Should().BeTrue();
        again.Message.Should().Be(ApprovalMessages.AlreadyPending);
    }

    [Fact]
    public async Task Submit_InitiatorPick_InvalidPicks_400()
    {
        var r = await BuildPurchase();
        await SeedFlow(r, "po-pick", r.Confirmed, 1, Def(null, Stage("it",
            new ApproverSource(ApprovalSources.InitiatorPick, CandidateGroupIds: [FinanceGroup, PurchaseLeadGroup, BoardGroup], Min: 1, Max: 1))));
        (await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed), Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received),
            Plain(r.Confirmed, r.Cancelled)])).IsSuccess.Should().BeTrue();
        await GrantEveryone(r.Module);
        await Db.Context.Set<AppUser>().Where(u => u.UserId == "fin2").ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        As("buyer2");
        var po = await NewPo(r, price: 50000);

        Dictionary<string, List<string>>[] bad =
        [
            new() { ["it"] = ["fin1", "fin1"] },     // 重複
            new() { ["it"] = ["buyer2"] },           // 發起人自己
            new() { ["it"] = ["fin2"] },             // 停用
            new() { ["it"] = ["emp1"] },             // 不在候選群組
            new() { ["it"] = [] },                   // 少於 min（缺必選）
        ];
        foreach (var picks in bad)
            (await Submit(po.Id, r.Confirmed, picks)).Errors.Should().ContainKey("picks.it", string.Join(",", picks["it"]));
        (await Submit(po.Id, r.Confirmed, new() { ["it"] = ["fin1", "gm"] })).Errors["picks.it"].Should().Equal(ApprovalMessages.PickCount(1, 1));
        (await Submit(po.Id, r.Confirmed, new() { ["it"] = ["fin1"], ["zz"] = ["gm"] })).Errors.Should().ContainKey("picks.zz");
        Db.Context.Set<ApprovalInstance>().AsNoTracking().Count().Should().Be(0);

        var ok = await Submit(po.Id, r.Confirmed, new() { ["it"] = ["gm"] });
        ok.IsCreated.Should().BeTrue(Dump(ok));
        var inst = Inst(((SubmitApprovalResponse)ok.Data!).InstanceId!.Value);
        inst.Picks["it"].Should().Equal("gm");
        TasksOf(inst.Id).Select(t => t.AssigneeUserId).Should().Equal("gm");
    }

    [Fact]
    public async Task Submit_InitiatorPick_CandidatesUnmatched_NoPickRequired_PickRejected()
    {
        // 候選群組在開啟套件時被略過：解析一律當空單位交給備援，送出驗證要一致——不要求自選，帶了自選回 400 說明（審查建議 10）
        var r = await BuildPurchase();
        await SeedFlow(r, "po-pick", r.Confirmed, 1, Def(null, Stage("it",
            new ApproverSource(ApprovalSources.InitiatorPick, CandidateGroupIds: [], Min: 1, Max: 1, CandidatesUnmatched: true))));
        (await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed), Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received),
            Plain(r.Confirmed, r.Cancelled)])).IsSuccess.Should().BeTrue();
        await GrantEveryone(r.Module);
        As("buyer2");
        var po = await NewPo(r, price: 50000);

        (await Submit(po.Id, r.Confirmed, new() { ["it"] = ["gm"] })).Errors["picks.it"].Should().Equal(ApprovalMessages.PickCandidatesUnmatched);
        Db.Context.Set<ApprovalInstance>().AsNoTracking().Count().Should().Be(0);

        var ok = await Submit(po.Id, r.Confirmed);
        ok.IsCreated.Should().BeTrue(Dump(ok));
        TasksOf(((SubmitApprovalResponse)ok.Data!).InstanceId!.Value).Select(t => (t.AssigneeUserId, t.Reason))
            .Should().Equal(("admin", ApprovalTaskReason.Fallback));
    }

    [Fact]
    public async Task Submit_CountersignPick_CandidatesUnmatched_OneFallback_409()
    {
        // 審查 M1 重現：min = 2 會簽、候選群組對不上、只有一位備援 → 不能建出一票就過的關卡，照「找不到足夠簽核人」擋下
        var r = await BuildPurchase();
        await SeedFlow(r, "po-pick", r.Confirmed, 1, Def(null, Stage("it",
            new ApproverSource(ApprovalSources.InitiatorPick, CandidateGroupIds: [], Min: 2, Max: 2, CandidatesUnmatched: true),
            mode: ApprovalModes.All)) with { FallbackApprovers = Users("gm") });
        (await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed), Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received),
            Plain(r.Confirmed, r.Cancelled)])).IsSuccess.Should().BeTrue();
        await GrantEveryone(r.Module);
        As("buyer2");
        var po = await NewPo(r, price: 50000);

        var res = await Submit(po.Id, r.Confirmed);
        res.IsConflict.Should().BeTrue(Dump(res));
        res.Message.Should().Be(ApprovalMessages.NoApprover("it", ApprovalMessages.PickLabel));
        Db.Context.Set<ApprovalInstance>().AsNoTracking().Count().Should().Be(0);
    }

    [Fact]
    public async Task Submit_UserField_NotCandidateOrSelf_400()
    {
        var r = await BuildPurchase();
        await SeedFlow(r, "po-owner", r.Confirmed, 1, Def(null, Stage("owner",
            new ApproverSource(ApprovalSources.UserField, FieldId: r.Owner, CandidateGroupIds: [FinanceGroup]))));
        (await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed), Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received),
            Plain(r.Confirmed, r.Cancelled)])).IsSuccess.Should().BeTrue();
        await GrantEveryone(r.Module);
        As("buyer1");
        var outside = await NewPo(r, price: 50000, extra: V((r.Owner, "emp1")));
        (await Submit(outside.Id, r.Confirmed)).Errors[$"f_{r.Owner}"].Should().Equal(ApprovalMessages.UserFieldNotCandidate);
        var self = await NewPo(r, price: 50000, extra: V((r.Owner, "buyer1")));
        (await Submit(self.Id, r.Confirmed)).Errors[$"f_{r.Owner}"].Should().Equal(ApprovalMessages.UserFieldIsInitiator);
        var good = await NewPo(r, price: 50000, extra: V((r.Owner, "fin1")));
        var res = await Submit(good.Id, r.Confirmed);
        TasksOf(((SubmitApprovalResponse)res.Data!).InstanceId!.Value).Select(t => t.AssigneeUserId).Should().Equal("fin1");
    }

}
