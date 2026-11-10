using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.CheckService;
using DynamicModules.Application.Dynamic.EventService;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.WorkflowService.Commands.SaveWorkflow;
using DynamicModules.Application.Dynamic.WorkflowService.Data;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Record = DynamicModules.Domain.Entities.Dynamic.Record;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>
/// 劇本一（設計 §17.2）的檢查與事件部分（裁決 23）：最後一關同意時以系統身分走轉移，事件、檢查在同一個交易裡跑；
/// 檢查失敗整筆回滾，連事件寫的值也不留。
/// </summary>
public class ApprovalLeaveTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    private Leave _l = null!;
    private long _balance;

    /// <summary>
    /// 餘額額度 10；檢查「同一餘額已核准天數 ≤ 額度」；事件「轉到已核准時把餘額的最後核准日設成今天」；
    /// 草稿 → 已核准 需簽核（block）；流程：第 1 關 manager 1，第 2 關 天數 &gt; 3 才進、manager 2。
    /// </summary>
    private async Task Arrange()
    {
        _l = await BuildLeave("lv");
        var check = new CheckDefinition([_l.ReqBalance], "sum", _l.ReqDays,
            J($$"""{"all":[{"field":{{_l.ReqStatus}},"op":"eq","value":"opt_{{_l.ReqApproved}}"}]}"""),
            new CheckAssert("lte", new CheckLimit(null, _l.BalLimit, _l.ReqBalance)), Text("超過額度"));
        (await Send(new CreateCheckRequest(new CheckInput("lv-cap", Text("cap"), _l.Req, true, check)))).IsCreated.Should().BeTrue();
        var ev = await Send(new CreateEventRequest(Event("lv-touch", _l.Req, OnTransition(_l.ReqApproved),
            UpdateVia("touch", _l.Balance, _l.ReqBalance, Const(_l.BalLast, "\"today\"")))));
        ev.IsCreated.Should().BeTrue(Dump(ev));

        var flow = await Send(new CreateApprovalFlowRequest(_l.Req, new ApprovalFlowInput("lv-flow", Text("請假"), _l.ReqApproved, 1, true,
            Def(null, Stage("mgr", Manager(1)), Stage("mgr2", Manager(2), when: Cond(_l.ReqDays, "gt", 3))))));
        flow.IsCreated.Should().BeTrue(Dump(flow));
        var wf = await Send(new SaveWorkflowRequest(_l.Req, new WorkflowInput(_l.ReqStatus, _l.ReqDraft,
            [new WorkflowState(_l.ReqDraft), new WorkflowState(_l.ReqApproved, true)], [Needs(_l.ReqDraft, _l.ReqApproved)])));
        wf.IsSuccess.Should().BeTrue(Dump(wf));
        await GrantEveryone(_l.Req, _l.Balance);
        _balance = ((DynamicRecordResponse)(await Send(new CreateRecordRequest(_l.Balance, V((_l.BalName, "b1"), (_l.BalLimit, 10)))))
            .Data!).Id;
    }

    private async Task<long> SubmitLeave(string user, decimal days)
    {
        As(user);
        var req = await Send(new CreateRecordRequest(_l.Req, V((TitleId(_l.Req), "請假"), (_l.ReqBalance, _balance), (_l.ReqDays, days))));
        req.IsCreated.Should().BeTrue(Dump(req));
        var id = ((DynamicRecordResponse)req.Data!).Id;
        return (await MustSubmit(id, _l.ReqApproved)).InstanceId!.Value;
    }

    private Task<ServiceResult> Approve(long instanceId, string user)
    {
        As(user);
        var task = TasksOf(instanceId).Single(t => t.AssigneeUserId == user && t.Status == ApprovalTaskStatus.Open);
        return Send(new ApproveTaskRequest(task.Id, Inst(instanceId).RowVersion, null, null));
    }

    private string? LastApproved() =>
        Rec(_balance).Values.TryGetValue($"f_{_l.BalLast}", out var v) && v is { ValueKind: JsonValueKind.String } s ? s.GetString() : null;

    [Fact]
    public async Task Approve_SkipsConditionStage_Completes_EventInSameTransaction()
    {
        // 劇本一第 3 步：2 天 → 第 2 關條件不成立被跳過；核准後事件把最後核准日設成今天；發起人收到已核准
        await Arrange();
        var id = await SubmitLeave("emp3", 2);
        TasksOf(id).Single().AssigneeUserId.Should().Be("lead1");
        LastApproved().Should().BeNull();

        var res = await Approve(id, "lead1");
        ((ApprovalActionResponse)res.Data!).Outcome.Should().Be(ApprovalOutcomes.Approved, Dump(res));
        ActionsOf(id).Should().Contain(a => a.Action == ApprovalActions.Skip);
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Approved);
        StateOf(Inst(id).RecordId).Should().Be(_l.ReqApproved);
        LastApproved().Should().Be("2026-11-04");
        Notices("emp3", NotificationKinds.ApprovalApproved).Should().Be(1);
        // 事件以同一個操作寫入：餘額的異動與簽核的「完成」同一個請求（RecordHistory 的修改者是按下同意的人）
        Db.Context.Set<RecordHistory>().AsNoTracking().Where(h => h.RecordId == _balance && h.FieldId == _l.BalLast)
            .Select(h => h.ChangedUserId).ToList().Should().Equal("lead1");
    }

    [Fact]
    public async Task Approve_FiveDays_SecondStageToLevel2()
    {
        // 劇本一第 4 步：5 天 → 第 1 關 lead1 同意後，第 2 關給 mgrA
        await Arrange();
        var id = await SubmitLeave("emp3", 5);
        ((ApprovalActionResponse)(await Approve(id, "lead1")).Data!).Outcome.Should().Be(ApprovalOutcomes.Advanced);
        TasksOf(id).Where(t => t.Status == ApprovalTaskStatus.Open).Select(t => t.AssigneeUserId).Should().Equal("mgrA");
        LastApproved().Should().BeNull();   // 還沒走到轉移，事件不跑
    }

    [Fact]
    public async Task Approve_CheckFails_400_StaysPending_EventRolledBack()
    {
        // 劇本一第 6 步：額度 10、已核准 8；再送 3 天，lead1 同意 → 檢查失敗 400，實例、任務、記錄、事件寫的值全部回滾
        await Arrange();
        var first = await SubmitLeave("emp3", 8);
        (await Approve(first, "lead1")).IsSuccess.Should().BeTrue();
        (await Approve(first, "mgrA")).IsSuccess.Should().BeTrue();
        Inst(first).Status.Should().Be(ApprovalInstanceStatus.Approved);
        await Db.Context.Set<Record>().Where(x => x.Id == _balance).ExecuteUpdateAsync(s => s.SetProperty(x => x.ValuesJson,
            x => x.ValuesJson.Replace("2026-11-04", "2026-01-01")));
        LastApproved().Should().Be("2026-01-01");

        var id = await SubmitLeave("emp3", 3);
        var rowVersion = Inst(id).RowVersion;
        var actions = ActionsOf(id).Count;
        var res = await Approve(id, "lead1");
        res.IsInvalid.Should().BeTrue(Dump(res));
        res.Errors[RecordValueProcessor.RecordErrorKey].Should().Equal("超過額度");
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Pending);
        Inst(id).RowVersion.Should().Be(rowVersion);
        ActionsOf(id).Should().HaveCount(actions);
        TasksOf(id).Single().Status.Should().Be(ApprovalTaskStatus.Open);
        StateOf(Inst(id).RecordId).Should().Be(_l.ReqDraft);
        LastApproved().Should().Be("2026-01-01");   // 事件已經跑過，但和檢查同一個交易，一起回滾
        Notices("emp3", NotificationKinds.ApprovalApproved).Should().Be(1);   // 只有第一張

        // 改按退回：意見必填；退回後實例 rejected、記錄維持草稿（流程沒設 rejectTo）。
        // 測試共用同一個 DbContext：資料庫已回滾，但追蹤中的實體仍是失敗請求改過的值，先清掉（正式環境每個請求各自一個 DbContext）
        Db.Context.ChangeTracker.Clear();
        As("lead1");
        var task = TasksOf(id).Single();
        (await Send(new RejectTaskRequest(task.Id, Inst(id).RowVersion, " ", RejectTaskHandler.ToInitiator))).Errors.Should().ContainKey("comment");
        var rejected = await Send(new RejectTaskRequest(task.Id, Inst(id).RowVersion, "額度不夠", RejectTaskHandler.ToInitiator));
        rejected.IsSuccess.Should().BeTrue(Dump(rejected));
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Rejected);
        StateOf(Inst(id).RecordId).Should().Be(_l.ReqDraft);
    }

    private long? StateOf(long recordId) =>
        Rec(recordId).Values.TryGetValue($"f_{_l.ReqStatus}", out var v) && v is { ValueKind: JsonValueKind.String } s
            && s.GetString() is { } text && text.StartsWith("opt_") ? long.Parse(text[4..]) : null;
}
