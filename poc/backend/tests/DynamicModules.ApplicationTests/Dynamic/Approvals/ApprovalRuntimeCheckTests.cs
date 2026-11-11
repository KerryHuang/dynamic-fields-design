using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>內容變更歷程與執行期再驗（設計 §5.2 末段、§8.3、§8.5；§17.5 第三輪 4）。</summary>
public class ApprovalRuntimeCheckTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    private ApprovalTask Open(long instanceId, string user) =>
        TasksOf(instanceId).Single(t => t.AssigneeUserId == user && t.Status == ApprovalTaskStatus.Open);

    private Task<ServiceResult> Approve(long instanceId, string user, Dictionary<string, JsonElement?>? values = null)
    {
        As(user);
        return Send(new ApproveTaskRequest(Open(instanceId, user).Id, Inst(instanceId).RowVersion, null, values));
    }

    private async Task<(long RecordId, long InstanceId)> SubmitAs(PurchaseRig r, string user, decimal total, long? to = null)
    {
        As(user);
        var po = await NewPo(r, price: total);
        return (po.Id, (await MustSubmit(po.Id, to ?? r.Confirmed)).InstanceId!.Value);
    }

    private async Task AddExtra(PurchaseRig r, long poId, decimal amount)
    {
        As("buyer1");
        var res = await Send(new CreateRecordRequest(r.Extra, V((TitleId(r.Extra), "追加"), (r.ExtraRef, poId), (r.ExtraAmount, amount))));
        res.IsCreated.Should().BeTrue(Dump(res));
    }

    /// <summary>直接改狀態機的轉移（略過儲存驗證，模擬驗證漏洞或資料不一致）。</summary>
    private async Task ReplaceTransitions(PurchaseRig r, Func<List<WorkflowTransition>, List<WorkflowTransition>> change)
    {
        var wf = Db.Context.Set<Workflow>().Single(w => w.ModuleId == r.ModuleId);
        wf.Transitions = change([.. wf.Transitions]);
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
    }

    private int AllNotices() => Db.Context.Set<Notification>().AsNoTracking().Count();

    [Fact]
    public async Task ContentChanged_RecordedWithBeforeAfter_NoNotification()
    {
        var r = await Scenario3();
        var (po, id) = await SubmitAs(r, "buyer1", 50000);
        var notices = AllNotices();
        await AddExtra(r, po, 120000);

        var changed = ActionsOf(id).Single(a => a.Action == ApprovalActions.ContentChanged);
        changed.ActorUserId.Should().Be(ApprovalEngine.SystemActor);
        var data = JsonDocument.Parse(changed.DataJson!).RootElement.EnumerateArray()
            .ToDictionary(e => e.GetProperty("fieldId").GetInt64(), e => (Before: e.GetProperty("before").GetDecimal(), After: e.GetProperty("after").GetDecimal()));
        data[r.Total].Should().Be((50000m, 170000m));
        data[r.ExtraSum].Should().Be((0m, 120000m));
        data.Keys.Should().NotContain(r.Memo);
        AllNotices().Should().Be(notices);   // 不發通知（主控裁決 2）

        // 讀歷程時依讀者的欄位權限遮蔽：總額對 board（gm）隱藏
        As("admin");
        await HideFor(r.Module, r.Total, BoardGroup);
        As("gm");
        var view = (RecordApprovalResponse)(await Send(new GetRecordApprovalRequest(po))).Data!;
        var shown = view.Current!.Actions.Single(a => a.Action == ApprovalActions.ContentChanged).Data!.Value.EnumerateArray()
            .Select(e => e.GetProperty("fieldId").GetInt64()).ToList();
        shown.Should().NotContain(r.Total).And.Contain(r.ExtraSum);
    }

    [Fact]
    public async Task ContentChanged_FallbackMask_IncludesDetailSourceHidden()
    {
        // 讀者沒有 Read、也沒有臨時讀取權（失去權限的發起人）：遮蔽集合要含明細來源隱藏 —
        // 明細小計隱藏 → 明細合計、總額一起隱藏
        var r = await Scenario3();
        var (po, _) = await SubmitAs(r, "buyer1", 50000);
        await AddExtra(r, po, 120000);
        As("admin");
        var everyone = Db.Context.Set<PermissionGroup>().AsNoTracking().Single(g => g.Key == "everyone").Id;
        await HideDetailFor(r.Line, r.LineSubtotal, everyone);
        await RevokeAll(r.Module);
        As("buyer1");
        var res = await Send(new GetRecordApprovalRequest(po));
        res.IsSuccess.Should().BeTrue(Dump(res));
        var shown = ((RecordApprovalResponse)res.Data!).Current!.Actions.Single(a => a.Action == ApprovalActions.ContentChanged).Data!.Value
            .EnumerateArray().Select(e => e.GetProperty("fieldId").GetInt64()).ToList();
        shown.Should().NotContain(r.Total).And.Contain(r.ExtraSum);
    }

    [Fact]
    public async Task ContentChanged_NotWrittenForOwnEditableWrite_OrNonPendingRecord()
    {
        var r = await Scenario3();
        As("buyer1");
        var draft = await NewPo(r, price: 50000);
        await AddExtra(r, draft.Id, 100);   // 不在簽核中：不寫
        Db.Context.Set<ApprovalAction>().AsNoTracking().Count(a => a.Action == ApprovalActions.ContentChanged).Should().Be(0);
    }

    [Fact]
    public async Task RejectToLockedState_Runtime_409_AdminNotifiedInSeparateTransaction()
    {
        var r = await BuildPurchase();
        var flow = await SeedFlow(r, "po-rt", r.Confirmed, 1, Def(null, Stage("lead", Group(PurchaseLeadGroup))));
        (await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed), Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received),
            Plain(r.Confirmed, r.Cancelled)])).IsSuccess.Should().BeTrue();
        await GrantEveryone(r.Module);
        // 略過儲存驗證：退回到部分進貨（鎖定狀態），並補上「草稿 → 部分進貨」讓轉移本身存在
        var version = Db.Context.Set<ApprovalFlowVersion>().Single(v => v.FlowId == flow.Id);
        version.Definition = version.Definition with { RejectTo = r.Partial };
        await Db.Context.SaveChangesAsync();
        await ReplaceTransitions(r, t => [.. t, Plain(r.Draft, r.Partial)]);
        var (po, id) = await SubmitAs(r, "buyer1", 5000);
        var actions = ActionsOf(id).Count;

        As("buyer2");
        var res = await Send(new RejectTaskRequest(Open(id, "buyer2").Id, Inst(id).RowVersion, "不行", RejectTaskHandler.ToInitiator));
        res.IsConflict.Should().BeTrue(Dump(res));
        res.Message.Should().Be(ApprovalMessages.RuntimeInvalid.Resolve(null));
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Pending);
        ActionsOf(id).Should().HaveCount(actions);
        Open(id, "buyer2").Status.Should().Be(ApprovalTaskStatus.Open);
        StateOf(r, po).Should().Be(r.Draft);
        Notices("buyer1", NotificationKinds.ApprovalRejected).Should().Be(0);
        // 主交易回滾後另開交易通知系統管理員
        var alert = Db.Context.Set<Notification>().AsNoTracking().Single(n => n.Kind == NotificationKinds.ApprovalAdminAlert);
        alert.UserId.Should().Be("admin");
        alert.SourceId.Should().Be(id);
        alert.TitleSnapshot.Should().Contain("鎖定");
    }

    [Fact]
    public async Task ApproveTransitionRemoved_Runtime_409()
    {
        var r = await Scenario3();
        var (po, id) = await SubmitAs(r, "buyer1", 50000);
        await ReplaceTransitions(r, t => t.Where(x => !(x.From == r.Draft && x.To == r.Confirmed)).ToList());

        var res = await Approve(id, "buyer2");
        res.IsConflict.Should().BeTrue(Dump(res));
        res.Message.Should().Be(ApprovalMessages.RuntimeInvalid.Resolve(null));
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Pending);
        Open(id, "buyer2").Status.Should().Be(ApprovalTaskStatus.Open);
        StateOf(r, po).Should().Be(r.Draft);
        Notices("admin", NotificationKinds.ApprovalAdminAlert).Should().Be(1);
        Notices("buyer1", NotificationKinds.ApprovalApproved).Should().Be(0);
    }

    [Fact]
    public async Task EditableFieldExempt_FromLockedState_409()
    {
        // 從鎖定狀態出發的簽核（作廢）不能借豁免改核准過的值；定義時的限制漏網時在這裡擋（設計 §8.5、§17.5 第三輪 4）
        var r = await BuildPurchase();
        await SeedFlow(r, "po-b", r.Confirmed, 1, Def(Cond(r.Total, "gte", 10000), Stage("lead", Group(PurchaseLeadGroup))));
        var c = await SeedFlow(r, "po-c", r.Cancelled, 1, Def(null, Stage("board", Group(BoardGroup))));
        var wf = await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed, ApprovalNoMatch.Direct), Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received),
            Needs(r.Confirmed, r.Cancelled)]);
        wf.IsSuccess.Should().BeTrue(Dump(wf));
        // 略過儲存驗證：入口 from（已確認）是鎖定狀態，存流程時本來會拒絕可編輯欄位
        var version = Db.Context.Set<ApprovalFlowVersion>().Single(v => v.FlowId == c.Id);
        version.Definition = Def(null, Stage("board", Group(BoardGroup), editable: [r.DueDate]));
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
        await GrantEveryone(r.Module);
        As("buyer1");
        var po = await NewPo(r, price: 5000);
        (await MustSubmit(po.Id, r.Confirmed, status: 200)).Outcome.Should().Be(ApprovalOutcomes.Direct);
        var id = (await MustSubmit(po.Id, r.Cancelled)).InstanceId!.Value;

        var res = await Approve(id, "gm", V((r.DueDate, "2026-12-01")));
        res.IsConflict.Should().BeTrue(Dump(res));
        res.Message.Should().Be(ApprovalMessages.RuntimeInvalid.Resolve(null));
        Rec(po.Id).Values.Should().NotContainKey($"f_{r.DueDate}");
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Pending);
        Notices("admin", NotificationKinds.ApprovalAdminAlert).Should().Be(1);

        // 不帶可編輯欄位照常核准
        var ok = await Approve(id, "gm");
        ok.IsSuccess.Should().BeTrue(Dump(ok));
        StateOf(r, po.Id).Should().Be(r.Cancelled);
    }

    [Theory]
    [InlineData(false)]   // 已定義的核准轉移
    [InlineData(true)]    // 已定義、不需簽核的退回轉移
    public void Verify_Passes(bool isReject)
    {
        var wf = new Workflow
        {
            StatusFieldId = 1, InitialOptionId = 10,
            Transitions = [new(10, 20, [], new TransitionApproval(true, ApprovalNoMatch.Block)), new(10, 30, [])]
        };
        ApprovalRuntimeCheck.Verify(wf, 10, isReject ? 30 : 20, isReject).Should().BeNull();
    }

    [Fact]
    public void Verify_Fails_UndefinedRequiresApprovalOrIntoLocked()
    {
        var wf = new Workflow
        {
            StatusFieldId = 1, InitialOptionId = 10,
            Transitions =
            [
                new(10, 20, [], new TransitionApproval(true, ApprovalNoMatch.Block)), new(20, 40, []), new(10, 40, []),
                new(10, 50, [], new TransitionApproval(true, ApprovalNoMatch.Block))
            ]
        };
        ApprovalRuntimeCheck.Verify(wf, 10, 30, isReject: false).Should().NotBeNull();   // 未定義
        ApprovalRuntimeCheck.Verify(wf, 10, 30, isReject: true).Should().NotBeNull();
        ApprovalRuntimeCheck.Verify(wf, 10, 50, isReject: true).Should().NotBeNull();    // 退回轉移需簽核
        ApprovalRuntimeCheck.Verify(wf, 10, 40, isReject: true).Should().NotBeNull();    // 40 是鎖定狀態（20 的下游），10 不是
        ApprovalRuntimeCheck.Verify(wf, 20, 40, isReject: true).Should().BeNull();       // 鎖定 → 鎖定可以
    }
}
