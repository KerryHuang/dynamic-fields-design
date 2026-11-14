using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetRecord;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>後加簽（設計 §7.6、§17.3 劇本二第 1、2、5、6 步；計畫 a3c、U5、U8、J2 到 J5）。</summary>
public class ApprovalAddSignTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    private ApprovalTask Open(long instanceId, string user) =>
        TasksOf(instanceId).Single(t => t.AssigneeUserId == user && t.Status == ApprovalTaskStatus.Open);

    private List<ApprovalStageRun> RunsOf(long instanceId) =>
        Db.Context.Set<ApprovalStageRun>().AsNoTracking().Where(x => x.InstanceId == instanceId).OrderBy(x => x.Seq).ToList();

    private static ApprovalActionResponse Body(ServiceResult r) => (ApprovalActionResponse)r.Data!;

    private Task<ServiceResult> Approve(long id, string user, Dictionary<string, JsonElement?>? values = null)
    {
        As(user);
        return Send(new ApproveTaskRequest(Open(id, user).Id, Inst(id).RowVersion, null, values));
    }

    private async Task MustApprove(long id, string user)
    {
        var res = await Approve(id, user);
        res.IsSuccess.Should().BeTrue(Dump(res));
    }

    private Task<ServiceResult> AddSign(long id, string user, string[] to, string mode = ApprovalModes.Any, Dictionary<string, JsonElement?>? values = null,
        string? comment = null, string? rowVersion = null)
    {
        As(user);
        return Send(new AddSignTaskRequest(Open(id, user).Id, rowVersion ?? Inst(id).RowVersion, comment, values, [.. to], mode));
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

    private async Task SetOverride(PurchaseRig r, PolicyDefinition def, string value)
    {
        Db.Context.Add(new ModulePolicy { ModuleId = r.ModuleId, Code = def.Code, Value = value, ModifiedUserId = "admin" });
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
    }

    /// <summary>
    /// 流程：s1 mgrA（或簽）→ s2 財務群組會簽（fin1、fin2；可編輯欄位 折扣）→ s3 董事會（gm）；備援名單 lead1。buyer1 送出。
    /// </summary>
    private async Task<(PurchaseRig R, long Id)> Arrange()
    {
        var r = await BuildPurchase();
        var def = Def(null, Stage("s1", Users("mgrA")), Stage("s2", Group(FinanceGroup), mode: ApprovalModes.All, editable: [r.Discount]),
            Stage("s3", Group(BoardGroup))) with { FallbackApprovers = Users("lead1") };
        await SeedFlow(r, "po-g", r.Confirmed, 1, def);
        (await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed), Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received),
            Plain(r.Confirmed, r.Cancelled)])).IsSuccess.Should().BeTrue();
        await GrantEveryone(r.Module, r.Extra);
        As("buyer1");
        var po = await NewPo(r, price: 20000);
        return (r, (await MustSubmit(po.Id, r.Confirmed)).InstanceId!.Value);
    }

    [Fact]
    public async Task AddSign_AnyStage_InsertsStageBeforeNext()
    {
        var (_, id) = await Arrange();
        var res = await AddSign(id, "mgrA", ["emp2"], comment: "請確認數量");

        res.IsSuccess.Should().BeTrue(Dump(res));
        Body(res).Outcome.Should().Be(ApprovalOutcomes.Advanced);
        var runs = RunsOf(id);
        runs.Select(x => (x.StageCode, x.Status)).Should().Equal(("s1", "approved"), ((string?)null, "active"));
        var added = runs[1];
        added.AddedByUserId.Should().Be("mgrA");
        added.AddSignUserIds.Should().Equal("emp2");
        added.Mode.Should().Be(ApprovalModes.Any);
        added.DueTime.Should().BeNull();
        added.NameSnapshot!.Resolve("zh-TW").Should().Be(ApprovalMessages.AddSignStageName.Resolve("zh-TW"));
        Inst(id).CurrentStageRunId.Should().Be(added.Id);
        var task = Open(id, "emp2");
        task.Reason.Should().Be(ApprovalTaskReason.Added);
        task.StageRunId.Should().Be(added.Id);
        task.DueTime.Should().BeNull();
        TasksOf(id).Single(t => t.AssigneeUserId == "mgrA").Status.Should().Be(ApprovalTaskStatus.Approved);
        Notices("emp2", NotificationKinds.ApprovalAddSigned).Should().Be(1);
        var log = ActionsOf(id).Where(a => a.Action is ApprovalActions.AddSign or ApprovalActions.Approve).ToList();
        log.Should().ContainSingle().Which.Action.Should().Be(ApprovalActions.AddSign);
        log[0].TargetUserIds.Should().Equal("emp2");
        log[0].ActorUserId.Should().Be("mgrA");
        log[0].Comment.Should().Be("請確認數量");
        JsonDocument.Parse(log[0].DataJson!).RootElement.GetProperty("mode").GetString().Should().Be(ApprovalModes.Any);

        await MustApprove(id, "emp2");   // 加簽關卡同意 → 才進第 2 關
        TasksOf(id).Where(t => t.Status == ApprovalTaskStatus.Open).Select(t => t.AssigneeUserId).Should().BeEquivalentTo(["fin1", "fin2"]);
        RunsOf(id).Last().StageCode.Should().Be("s2");
    }

    [Fact]
    public async Task AddSign_AllStage_QueuedUntilStageComplete_InAddedOrder()
    {
        var (_, id) = await Arrange();
        await MustApprove(id, "mgrA");
        await MustAddSign(id, "fin1", "emp1");
        await MustAddSign(id, "fin2", "emp2");

        RunsOf(id).Select(x => (x.StageCode, x.Status)).Should().Equal(("s1", "approved"), ("s2", "approved"), ((string?)null, "active"), ((string?)null, "queued"));
        var first = RunsOf(id)[2];
        first.AddSignUserIds.Should().Equal("emp1");
        TasksOf(id).Where(t => t.Status == ApprovalTaskStatus.Open).Select(t => t.AssigneeUserId).Should().Equal("emp1");
        Notices("emp1", NotificationKinds.ApprovalAddSigned).Should().Be(1);
        Notices("emp2", NotificationKinds.ApprovalAddSigned).Should().Be(0);   // 排隊中不通知

        await MustApprove(id, "emp1");
        RunsOf(id).Select(x => x.Status).Should().Equal("approved", "approved", "approved", "active");
        TasksOf(id).Where(t => t.Status == ApprovalTaskStatus.Open).Select(t => t.AssigneeUserId).Should().Equal("emp2");
        Notices("emp2", NotificationKinds.ApprovalAddSigned).Should().Be(1);

        await MustApprove(id, "emp2");
        RunsOf(id).Last().StageCode.Should().Be("s3");
        TasksOf(id).Where(t => t.Status == ApprovalTaskStatus.Open).Select(t => t.AssigneeUserId).Should().Equal("gm");
    }

    /// <summary>§17.3 第 5 步前的狀態：mgrA 加簽 emp2、emp2 同意，第 2 關 fin1、fin2 進行中。</summary>
    private async Task<(PurchaseRig R, long Id)> AtStage2WithAddSign()
    {
        var (r, id) = await Arrange();
        await MustAddSign(id, "mgrA", "emp2");
        await MustApprove(id, "emp2");
        return (r, id);
    }

    [Fact]
    public async Task ReturnPrevious_FromStage2_ToAddSign_ThenResumesStage2()
    {
        var (_, id) = await AtStage2WithAddSign();
        var res = await Return(id, "fin1");

        Body(res).Outcome.Should().Be(ApprovalOutcomes.Returned);
        RunsOf(id).Select(x => (x.StageCode, x.Status)).Should()
            .Equal(("s1", "approved"), ((string?)null, "approved"), ("s2", "returned"), ((string?)null, "active"));
        var restarted = RunsOf(id)[3];
        restarted.AddedByUserId.Should().Be("mgrA");
        restarted.AddSignUserIds.Should().Equal("emp2");
        TasksOf(id).Where(t => t.Status == ApprovalTaskStatus.Open).Select(t => t.AssigneeUserId).Should().Equal("emp2");
        TasksOf(id).Where(t => t.AssigneeUserId is "fin1" or "fin2").Should().OnlyContain(t => t.Status != ApprovalTaskStatus.Open);
        Notices("emp2", NotificationKinds.ApprovalReturned).Should().Be(1);

        await MustApprove(id, "emp2");
        var runs = RunsOf(id);
        runs.Last().StageCode.Should().Be("s2");   // 回到第 2 關，不是第 3 關
        runs.Last().Status.Should().Be(ApprovalStageStatus.Active);
        TasksOf(id).Where(t => t.Status == ApprovalTaskStatus.Open).Select(t => t.AssigneeUserId).Should().BeEquivalentTo(["fin1", "fin2"]);
        runs.Count(x => x.StageCode == "s1").Should().Be(1);
    }

    [Fact]
    public async Task ReturnPrevious_FromRestartedAddSign_GoesToAnchorStage()
    {
        var (_, id) = await AtStage2WithAddSign();
        await Return(id, "fin1");
        var res = await Return(id, "emp2");   // 重來的加簽關卡再退回上一關 → 第 1 關，不是再跑一次加簽

        Body(res).Outcome.Should().Be(ApprovalOutcomes.Returned);
        var runs = RunsOf(id);
        runs.Last().StageCode.Should().Be("s1");
        runs.Last().Status.Should().Be(ApprovalStageStatus.Active);
        TasksOf(id).Where(t => t.Status == ApprovalTaskStatus.Open).Select(t => t.AssigneeUserId).Should().Equal("mgrA");

        // 第 1 關重來同意後，下一關是第 2 關（路徑函式截回），不是第 3 關也不是再加簽
        await MustApprove(id, "mgrA");
        RunsOf(id).Last().StageCode.Should().Be("s2");
        TasksOf(id).Where(t => t.Status == ApprovalTaskStatus.Open).Select(t => t.AssigneeUserId).Should().BeEquivalentTo(["fin1", "fin2"]);
    }

    [Fact]
    public async Task AddSign_AfterReturnRestart_DoesNotSkipReturnedStage()
    {
        // 加簽關卡重來後不跳關：s2 被退回到加簽關卡，加簽關卡同意後要回 s2，s2 同意後才是 s3
        var (_, id) = await AtStage2WithAddSign();
        await Return(id, "fin1");
        await MustApprove(id, "emp2");
        RunsOf(id).Last().StageCode.Should().Be("s2");
        await MustApprove(id, "fin1");
        await MustApprove(id, "fin2");
        RunsOf(id).Last().StageCode.Should().Be("s3");
        await MustApprove(id, "gm");
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Approved);
    }

    [Fact]
    public async Task AddSign_TooManyUsers_400_ModuleOverride()
    {
        var (r, id) = await Arrange();
        await SetOverride(r, PolicyCatalog.ApprovalMaxAddSignUsers, "2");
        var before = Inst(id).RowVersion;
        var actions = ActionsOf(id).Count;

        var res = await AddSign(id, "mgrA", ["emp1", "emp2", "emp3"]);

        res.IsSuccess.Should().BeFalse();
        res.Errors["userIds"].Should().Equal(ApprovalMessages.AddSignTooManyUsers(2).Resolve(null));
        Inst(id).RowVersion.Should().Be(before);
        ActionsOf(id).Should().HaveCount(actions);
        RunsOf(id).Should().ContainSingle();
        Open(id, "mgrA").Status.Should().Be(ApprovalTaskStatus.Open);
        (await AddSign(id, "mgrA", ["emp1", "emp2"])).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task AddSign_UsersRequiredAndDeduplicated_ModeValidated()
    {
        var (_, id) = await Arrange();
        var before = Inst(id).RowVersion;
        (await AddSign(id, "mgrA", [])).Errors["userIds"].Should().Equal(ApprovalMessages.AddSignUsersRequired.Resolve(null));
        (await AddSign(id, "mgrA", ["emp1"], mode: "first")).Errors["mode"].Should().Equal(ApprovalMessages.AddSignModeInvalid.Resolve(null));
        Inst(id).RowVersion.Should().Be(before);

        await MustAddSign(id, "mgrA", "emp1", "emp1");   // 去重後一位
        RunsOf(id)[1].AddSignUserIds.Should().Equal("emp1");
        TasksOf(id).Count(t => t.Reason == ApprovalTaskReason.Added).Should().Be(1);
    }

    [Fact]
    public async Task AddSign_InstanceLimit_400_RestartNotCounted()
    {
        var (r, id) = await Arrange();
        await SetOverride(r, PolicyCatalog.ApprovalMaxAddSignStages, "1");
        await MustAddSign(id, "mgrA", "emp2");   // 第一次 200
        await MustApprove(id, "emp2");
        await Return(id, "fin1");                // 退回到加簽關卡，重來不再計一次
        await MustApprove(id, "emp2");
        RunsOf(id).Last().StageCode.Should().Be("s2");

        var res = await AddSign(id, "fin1", ["emp1"]);   // 第二次 400
        res.IsSuccess.Should().BeFalse();
        res.Errors.Should().ContainKey("userIds");
        res.Errors["userIds"].Should().Equal(ApprovalMessages.AddSignLimit(1).Resolve(null));
        RunsOf(id).Count(x => x.StageCode is null).Should().Be(2);   // 原本與重來各一，沒有新的
        Open(id, "fin1").Status.Should().Be(ApprovalTaskStatus.Open);
    }

    [Fact]
    public async Task AddSign_TargetSelfInactiveInitiator_400()
    {
        var (r, id) = await Arrange();
        (await AddSign(id, "mgrA", ["mgrA"])).Errors["userIds"].Should().Equal(ApprovalMessages.TargetSelf.Resolve(null));
        (await AddSign(id, "mgrA", ["ghost"])).Errors["userIds"].Should().Equal(ApprovalMessages.TargetInactive.Resolve(null));
        await Db.Context.Set<AppUser>().Where(u => u.UserId == "emp3").ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        Db.Context.ChangeTracker.Clear();
        (await AddSign(id, "mgrA", ["emp3"])).Errors["userIds"].Should().Equal(ApprovalMessages.TargetInactive.Resolve(null));
        (await AddSign(id, "mgrA", ["buyer1"])).Errors["userIds"].Should().Equal(ApprovalMessages.TargetInitiator.Resolve(null));   // buyer1 是發起人
        RunsOf(id).Should().ContainSingle();

        await SetOverride(r, PolicyCatalog.ApprovalAllowSelfApproval, "true");   // U8：政策打開後發起人可以
        (await AddSign(id, "mgrA", ["buyer1"])).IsSuccess.Should().BeTrue();
        Open(id, "buyer1").Reason.Should().Be(ApprovalTaskReason.Added);
    }

    [Fact]
    public async Task AddSign_TargetIsHolderOfStage_StillAllowed()
    {
        // §6.4：同一人出現在兩關不自動略過；加簽是新的一關，「這關已有票」不適用
        var (_, id) = await Arrange();
        await MustApprove(id, "mgrA");
        (await AddSign(id, "fin1", ["fin2"])).IsSuccess.Should().BeTrue();
        RunsOf(id).Last().AddSignUserIds.Should().Equal("fin2");
    }

    [Fact]
    public async Task AddSign_TargetDeactivatedBeforeActivation_FallbackSeat()
    {
        var (_, id) = await Arrange();
        await MustApprove(id, "mgrA");
        await MustAddSign(id, "fin1", "emp1");
        await Db.Context.Set<AppUser>().Where(u => u.UserId == "emp1").ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        Db.Context.ChangeTracker.Clear();

        await MustApprove(id, "fin2");   // s2 完成，啟動排隊的加簽關卡
        var task = TasksOf(id).Single(t => t.Status == ApprovalTaskStatus.Open);
        task.AssigneeUserId.Should().Be("lead1");   // 流程的備援名單
        task.Reason.Should().Be(ApprovalTaskReason.Fallback);
        ActionsOf(id).Should().Contain(a => a.Action == ApprovalActions.Fallback);
    }

    [Fact]
    public async Task AddSign_InsideAddSignStage_Nested()
    {
        var (_, id) = await Arrange();
        await MustAddSign(id, "mgrA", "emp2");
        await MustAddSign(id, "emp2", "emp3");

        RunsOf(id).Select(x => (x.StageCode, x.Status, x.AddedByUserId)).Should()
            .Equal(("s1", "approved", null), (null, "approved", "mgrA"), (null, "active", "emp2"));
        Open(id, "emp3").Reason.Should().Be(ApprovalTaskReason.Added);
        await MustApprove(id, "emp3");
        RunsOf(id).Last().StageCode.Should().Be("s2");   // 加簽關卡都完成才回定義關卡
    }

    [Fact]
    public async Task AddSign_WithEditableValues_Written()
    {
        var (r, id) = await Arrange();
        await MustApprove(id, "mgrA");
        var po = Inst(id).RecordId;
        var res = await AddSign(id, "fin1", ["gm"], values: V((r.Discount, 18000)));

        res.IsSuccess.Should().BeTrue(Dump(res));
        Rec(po).Values[$"f_{r.Discount}"]!.Value.GetDecimal().Should().Be(18000);
        Db.Context.Set<RecordHistory>().AsNoTracking().Count(h => h.RecordId == po && h.FieldId == r.Discount).Should().Be(1);
        Rec(po).Row.ModifiedUserId.Should().Be("fin1");
        ActionsOf(id).Single(a => a.Action == ApprovalActions.AddSign).Targets().Should().Equal("gm");

        (await AddSign(id, "fin2", ["emp1"], values: V((r.Memo, "偷改")))).Errors[$"f_{r.Memo}"]
            .Should().Equal(ApprovalMessages.NotEditableField.Resolve(null));
    }

    [Fact]
    public async Task AddSign_WhenRerouted_NoQueuedInNewInstance()
    {
        var r = await Scenario3();
        As("buyer1");
        var po = await NewPo(r, price: 50000);
        var id = (await MustSubmit(po.Id, r.Confirmed)).InstanceId!.Value;
        var title = TitleId(r.Extra);
        (await Send(new DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord.CreateRecordRequest(
            r.Extra, V((title, "追加"), (r.ExtraRef, po.Id), (r.ExtraAmount, 120000m))))).IsCreated.Should().BeTrue();

        var res = await AddSign(id, "buyer2", ["emp1"]);

        Body(res).Outcome.Should().Be(ApprovalOutcomes.Rerouted);
        RunsOf(id).Should().NotContain(x => x.StageCode == null);   // 舊實例沒有加簽殘留
        ActionsOf(id).Should().NotContain(a => a.Action == ApprovalActions.AddSign);
        var fresh = Db.Context.Set<ApprovalInstance>().AsNoTracking().Single(i => i.RecordId == po.Id && i.Status == ApprovalInstanceStatus.Pending);
        RunsOf(fresh.Id).Should().OnlyContain(x => x.StageCode != null);
        TasksOf(fresh.Id).Should().NotContain(t => t.Reason == ApprovalTaskReason.Added);
        Notices("emp1", NotificationKinds.ApprovalAddSigned).Should().Be(0);
    }

    [Fact]
    public async Task Withdraw_AddSignStageUntouched_ClosesRemainingQueued()
    {
        var (_, id) = await Arrange();
        await MustApprove(id, "mgrA");
        await MustAddSign(id, "fin1", "emp1");
        await MustAddSign(id, "fin2", "emp2");   // emp1 的加簽關卡 active（沒人動過），emp2 的 queued
        RunsOf(id).Last().Status.Should().Be(ApprovalStageStatus.Queued);

        As("buyer1");
        var res = await Send(new WithdrawApprovalRequest(id, Inst(id).RowVersion, null));

        Body(res).Outcome.Should().Be(ApprovalOutcomes.Withdrawn);
        RunsOf(id).Where(x => x.StageCode == null).Should().OnlyContain(x => x.Status == ApprovalStageStatus.Closed);
        TasksOf(id).Should().NotContain(t => t.Status == ApprovalTaskStatus.Open);
    }

    [Fact]
    public async Task Reject_ToInitiator_ClosesQueued()
    {
        var (_, id) = await Arrange();
        await MustApprove(id, "mgrA");
        await MustAddSign(id, "fin1", "emp1");
        As("fin2");
        var res = await Send(new RejectTaskRequest(Open(id, "fin2").Id, Inst(id).RowVersion, "不同意", RejectTaskHandler.ToInitiator));

        Body(res).Outcome.Should().Be(ApprovalOutcomes.Rejected);
        RunsOf(id).Where(x => x.StageCode == null).Should().OnlyContain(x => x.Status == ApprovalStageStatus.Closed);
    }

    [Fact]
    public async Task ReturnPrevious_ClosesQueued_AndRestartsPreviousStage()
    {
        var (_, id) = await Arrange();
        await MustApprove(id, "mgrA");
        await MustAddSign(id, "fin1", "emp1");   // s2 還沒完成，加簽排隊中
        var res = await Return(id, "fin2");

        Body(res).Outcome.Should().Be(ApprovalOutcomes.Returned);
        RunsOf(id).Select(x => (x.StageCode, x.Status)).Should().Equal(("s1", "approved"), ("s2", "returned"), ((string?)null, "closed"), ("s1", "active"));
    }

    [Fact]
    public async Task AddSign_FallbackRoute_ActivatesThenCompletes()
    {
        var r = await Scenario3();
        As("buyer1");
        var po = await NewPo(r, price: 50000);
        var id = (await MustSubmit(po.Id, r.Confirmed)).InstanceId!.Value;
        var title = TitleId(r.Extra);
        (await Send(new DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord.CreateRecordRequest(
            r.Extra, V((title, "折讓"), (r.ExtraRef, po.Id), (r.ExtraAmount, -45000m))))).IsCreated.Should().BeTrue();
        await MustApprove(id, "buyer2");   // 重新比對 → 備援實例
        var fresh = Db.Context.Set<ApprovalInstance>().AsNoTracking().Single(i => i.RecordId == po.Id && i.Status == ApprovalInstanceStatus.Pending);
        fresh.RouteKind.Should().Be(ApprovalRouteKind.Fallback);

        await MustAddSign(fresh.Id, "admin", "emp2");
        RunsOf(fresh.Id).Select(x => (x.StageCode, x.Status)).Should().Equal((ApprovalEngine.FallbackStageCode, "approved"), (null, "active"));
        StateOf(r, po.Id).Should().Be(r.Draft);

        var done = await Approve(fresh.Id, "emp2");
        Body(done).Outcome.Should().Be(ApprovalOutcomes.Approved);
        Inst(fresh.Id).Status.Should().Be(ApprovalInstanceStatus.Approved);
        StateOf(r, po.Id).Should().Be(r.Confirmed);
    }

    [Fact]
    public async Task Approve_InAddSignStage_WithValues_400()
    {
        var (r, id) = await Arrange();
        await MustAddSign(id, "mgrA", "emp2");
        (await Approve(id, "emp2", V((r.Discount, 1)))).Errors[$"f_{r.Discount}"].Should().Equal(ApprovalMessages.NotEditableField.Resolve(null));
        Open(id, "emp2").Status.Should().Be(ApprovalTaskStatus.Open);
    }

    [Fact]
    public async Task AddSign_NotActor_403_StaleRowVersion_409()
    {
        var (_, id) = await Arrange();
        var task = Open(id, "mgrA");
        As("emp1");
        (await Send(new AddSignTaskRequest(task.Id, Inst(id).RowVersion, null, null, ["emp2"], ApprovalModes.Any))).IsForbidden.Should().BeTrue();
        var stale = await AddSign(id, "mgrA", ["emp2"], rowVersion: "old");
        stale.IsConflict.Should().BeTrue();
        RunsOf(id).Should().ContainSingle();
    }

    [Fact]
    public async Task RecordApproval_CanAddSign_FalseAtLimit()
    {
        var (r, id) = await Arrange();
        await SetOverride(r, PolicyCatalog.ApprovalMaxAddSignStages, "1");
        var po = Inst(id).RecordId;
        async Task<(bool CanAddSign, string? AddedBy)> View(string user)
        {
            As(user);
            var view = (RecordApprovalResponse)(await Send(new GetRecordApprovalRequest(po))).Data!;
            var stages = view.Current!.Stages;
            return (stages.SelectMany(s => s.Tasks).Single(t => t.AssigneeUserId == user && t.CanAct).CanAddSign, stages.Select(s => s.AddedByName).LastOrDefault(n => n is not null));
        }
        (await View("mgrA")).CanAddSign.Should().BeTrue();
        await MustAddSign(id, "mgrA", "emp2");
        var emp2 = await View("emp2");
        emp2.CanAddSign.Should().BeFalse();   // 已經 1 次，到上限
        emp2.AddedBy.Should().Be("mgrA");
    }
}

internal static class AddSignTestExt
{
    public static List<string> Targets(this ApprovalAction a) => a.TargetUserIds ?? [];
}
