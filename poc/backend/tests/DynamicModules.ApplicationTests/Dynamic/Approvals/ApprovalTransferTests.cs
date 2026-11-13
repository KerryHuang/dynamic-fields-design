using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetRecord;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>轉交（設計 §7.5、§17.3 第 7 步前半、二審建議 1；計畫 a3a、U1、U3、U8）。</summary>
public class ApprovalTransferTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    private ApprovalTask Open(long instanceId, string user) =>
        TasksOf(instanceId).Single(t => t.AssigneeUserId == user && t.Status == ApprovalTaskStatus.Open);

    private Task<ServiceResult> Transfer(long instanceId, string user, string to, string? comment = null, string? rowVersion = null)
    {
        As(user);
        return Send(new TransferTaskRequest(Open(instanceId, user).Id, rowVersion ?? Inst(instanceId).RowVersion, to, comment));
    }

    private async Task MustTransfer(long instanceId, string user, string to)
    {
        var res = await Transfer(instanceId, user, to);
        res.IsSuccess.Should().BeTrue(Dump(res));
    }

    private async Task Approve(long instanceId, string user)
    {
        As(user);
        var res = await Send(new ApproveTaskRequest(Open(instanceId, user).Id, Inst(instanceId).RowVersion, null, null));
        res.IsSuccess.Should().BeTrue(Dump(res));
    }

    private async Task<long> SubmitAs(PurchaseRig r, string user, decimal total)
    {
        As(user);
        var po = await NewPo(r, price: total);
        return (await MustSubmit(po.Id, r.Confirmed)).InstanceId!.Value;
    }

    /// <summary>15 萬的單：第 1 關主管 gm；gm 同意後第 2 關會簽 gm、buyer2。</summary>
    private async Task<(PurchaseRig R, long Id)> Stage1()
    {
        var r = await Scenario3();
        return (r, await SubmitAs(r, "buyer1", 150000));
    }

    private async Task<(PurchaseRig R, long Id)> Stage2()
    {
        var (r, id) = await Stage1();
        await Approve(id, "gm");
        return (r, id);
    }

    [Fact]
    public async Task Transfer_NewTaskForTarget_OldTransferred_OriginalKept()
    {
        var (_, id) = await Stage1();
        var old = Open(id, "gm");
        var res = await Transfer(id, "gm", "fin1", "出差中");

        res.IsSuccess.Should().BeTrue(Dump(res));
        var data = (ApprovalActionResponse)res.Data!;
        data.Outcome.Should().Be(ApprovalOutcomes.Transferred);
        data.InstanceId.Should().Be(id);
        data.InstanceRowVersion.Should().Be(Inst(id).RowVersion);
        var tasks = TasksOf(id);
        var from = tasks.Single(t => t.Id == old.Id);
        from.Status.Should().Be(ApprovalTaskStatus.Transferred);
        from.ActedTime.Should().NotBeNull();
        var added = tasks.Single(t => t.AssigneeUserId == "fin1");
        added.Status.Should().Be(ApprovalTaskStatus.Open);
        added.Reason.Should().Be(ApprovalTaskReason.Transferred);
        added.OriginalUserId.Should().Be("gm");
        added.SeatKey.Should().Be(old.SeatKey);
        added.DueTime.Should().Be(old.DueTime);
        added.StageRunId.Should().Be(old.StageRunId);
        var last = ActionsOf(id).Last();
        last.Action.Should().Be(ApprovalActions.Transfer);
        last.ActorUserId.Should().Be("gm");
        last.TargetUserIds.Should().Equal("fin1");
        last.Comment.Should().Be("出差中");
        Notices("fin1", NotificationKinds.ApprovalTransferred).Should().Be(1);
        Notices("gm", NotificationKinds.ApprovalTransferred).Should().Be(0);
    }

    [Fact]
    public async Task Transfer_KeepsDueTime_NotRestarted()
    {
        var (_, id) = await Stage1();
        var due = SeedTime.AddHours(24);   // 啟動時就定好的期限，遠早於「現在 + 24 小時」
        var old = Open(id, "gm");
        await Db.Context.Set<ApprovalTask>().Where(t => t.Id == old.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.DueTime, due));
        Db.Context.ChangeTracker.Clear();

        await MustTransfer(id, "gm", "fin1");

        var added = Open(id, "fin1");
        added.DueTime.Should().NotBeNull();
        added.DueTime.Should().Be(due);
        added.DueTime.Should().NotBe(added.CreatedTime.AddHours(24));
    }

    [Fact]
    public async Task Transfer_OldAssigneeLosesReadAndAct()
    {
        var (r, id) = await Stage1();
        As("admin");
        await RevokeAll(r.Module);   // 沒有任何人有模組 Read，全靠臨時讀取
        var recordId = Inst(id).RecordId;
        As("gm");
        (await Send(new GetRecordRequest(recordId))).IsSuccess.Should().BeTrue();
        var gmTask = Open(id, "gm");

        await MustTransfer(id, "gm", "fin1");

        As("gm");
        var gone = await Send(new GetRecordRequest(recordId));
        gone.IsForbidden.Should().BeTrue(Dump(gone));   // 沒有 Read 也不是可動作者：guard 回 403（計畫寫 404，以實作為準）
        var finTask = Open(id, "fin1");
        var approve = await Send(new ApproveTaskRequest(finTask.Id, Inst(id).RowVersion, null, null));
        approve.IsForbidden.Should().BeTrue(Dump(approve));
        var stale = await Send(new ApproveTaskRequest(gmTask.Id, Inst(id).RowVersion, null, null));
        stale.IsConflict.Should().BeTrue(Dump(stale));   // 已轉交的任務：409 StageHandled
        As("fin1");
        (await Send(new GetRecordRequest(recordId))).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Transfer_ToInitiator_400_UnlessSelfApprovalPolicyOn()
    {
        var (r, id) = await Stage1();
        var res = await Transfer(id, "gm", "buyer1");
        res.IsInvalid.Should().BeTrue(Dump(res));
        res.Errors["userId"].Should().Equal(ApprovalMessages.TargetInitiator.Resolve(null));
        TasksOf(id).Should().ContainSingle();

        Db.Context.ChangeTracker.Clear();
        Db.Context.Add(new ModulePolicy
        {
            ModuleId = r.ModuleId, Code = PolicyCatalog.ApprovalAllowSelfApproval.Code, Value = "true", ModifiedUserId = "admin"
        });
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();

        (await Transfer(id, "gm", "buyer1")).IsSuccess.Should().BeTrue();
        TasksOf(id).Single(t => t.Status == ApprovalTaskStatus.Open).AssigneeUserId.Should().Be("buyer1");
    }

    [Fact]
    public async Task Transfer_ToSelf_400()
    {
        var (_, id) = await Stage1();
        var res = await Transfer(id, "gm", "gm");
        res.IsInvalid.Should().BeTrue(Dump(res));
        res.Errors["userId"].Should().Equal(ApprovalMessages.TargetSelf.Resolve(null));
    }

    [Fact]
    public async Task Transfer_ToInactiveOrUnknown_400()
    {
        var (_, id) = await Stage1();
        Db.Context.Add(new AppUser { UserId = "gone", DisplayName = "gone", IsActive = false });
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
        foreach (var target in new[] { "gone", "nobody" })
        {
            var res = await Transfer(id, "gm", target);
            res.IsInvalid.Should().BeTrue(Dump(res));
            res.Errors["userId"].Should().Equal(ApprovalMessages.TargetInactive.Resolve(null));
        }
        TasksOf(id).Should().ContainSingle();
    }

    [Fact]
    public async Task Transfer_ToHolderOfAnotherSeat_400_EvenIfOnlyDelegatedOriginal()
    {
        var (_, id) = await Stage2();   // 第 2 關會簽：gm、buyer2
        var res = await Transfer(id, "gm", "buyer2");
        res.IsInvalid.Should().BeTrue(Dump(res));
        res.Errors["userId"].Should().Equal(ApprovalMessages.TargetInStage.Resolve(null));

        // buyer2 的任務改成由 emp1 代理（本人 buyer2）：轉給本人、轉給代理人都不行
        var seat = Open(id, "buyer2");
        await Db.Context.Set<ApprovalTask>().Where(t => t.Id == seat.Id).ExecuteUpdateAsync(s => s
            .SetProperty(t => t.Reason, ApprovalTaskReason.Delegated).SetProperty(t => t.AssigneeUserId, "emp1").SetProperty(t => t.OriginalUserId, "buyer2"));
        Db.Context.ChangeTracker.Clear();
        foreach (var target in new[] { "buyer2", "emp1" })
        {
            var again = await Transfer(id, "gm", target);
            again.IsInvalid.Should().BeTrue($"{target}: {Dump(again)}");
            again.Errors["userId"].Should().Equal(ApprovalMessages.TargetInStage.Resolve(null));
        }
        TasksOf(id).Count(t => t.Status == ApprovalTaskStatus.Transferred).Should().Be(0);
    }

    [Fact]
    public async Task Transfer_BackToPreviousHolder_Allowed()
    {
        var (_, id) = await Stage1();
        await MustTransfer(id, "gm", "fin1");
        await MustTransfer(id, "fin1", "gm");
        var tasks = TasksOf(id);
        tasks.Select(t => (t.AssigneeUserId, t.Status)).Should().Equal(
            ("gm", ApprovalTaskStatus.Transferred), ("fin1", ApprovalTaskStatus.Transferred), ("gm", ApprovalTaskStatus.Open));
        tasks.Last().OriginalUserId.Should().Be("gm");
    }

    [Fact]
    public async Task Transfer_ThenNewAssigneeApproves_SatisfiesSeat()
    {
        var (_, id) = await Stage2();
        await MustTransfer(id, "gm", "fin1");
        await Approve(id, "buyer2");
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Pending);   // gm 的席位還沒有票
        await Approve(id, "fin1");
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Approved);
        TasksOf(id).Count(t => t.Status == ApprovalTaskStatus.Approved).Should().Be(3);
    }

    [Fact]
    public async Task Transfer_StaleRowVersion_409_NothingWritten()
    {
        var (_, id) = await Stage1();
        var before = (TasksOf(id).Count, ActionsOf(id).Count, Inst(id).RowVersion);
        var res = await Transfer(id, "gm", "fin1", rowVersion: "stale");
        res.IsConflict.Should().BeTrue(Dump(res));
        (TasksOf(id).Count, ActionsOf(id).Count, Inst(id).RowVersion).Should().Be(before);
        Open(id, "gm").Reason.Should().Be(ApprovalTaskReason.Resolved);
    }

    [Fact]
    public async Task Transfer_NotActor_403()
    {
        var (_, id) = await Stage1();
        As("mgrA");
        var res = await Send(new TransferTaskRequest(Open(id, "gm").Id, Inst(id).RowVersion, "fin1", null));
        res.IsForbidden.Should().BeTrue(Dump(res));
        TasksOf(id).Should().ContainSingle();
    }

    [Fact]
    public async Task Transfer_ClosedModule_409()
    {
        var (r, id) = await Stage1();
        await Db.Context.Set<Module>().Where(m => m.Key == r.Module).ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, ModuleStatus.Closed));
        Db.Context.ChangeTracker.Clear();
        var res = await Transfer(id, "gm", "fin1");
        res.IsConflict.Should().BeTrue(Dump(res));
    }

    [Fact]
    public async Task Transfer_CommentTooLong_400()
    {
        var (_, id) = await Stage1();
        var res = await Transfer(id, "gm", "fin1", new string('字', 1001));
        res.IsInvalid.Should().BeTrue(Dump(res));
        res.Errors.Should().ContainKey("comment");
        TasksOf(id).Should().ContainSingle();
    }

    [Fact]
    public async Task Transfer_DoesNotBlockWithdraw()
    {
        var (_, id) = await Stage1();
        await MustTransfer(id, "gm", "fin1");
        As("buyer1");
        var res = await Send(new WithdrawApprovalRequest(id, Inst(id).RowVersion, null));
        res.IsSuccess.Should().BeTrue(Dump(res));
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Withdrawn);
    }

    [Fact]
    public async Task Transfer_InboxDoneListsOld_TodoListsNew()
    {
        var (_, id) = await Stage1();
        await MustTransfer(id, "gm", "fin1");
        async Task<List<long>> Ids(string user, string box)
        {
            As(user);
            var res = await Send(new GetApprovalTasksRequest(box, null, null, null, 1, 20));
            res.IsSuccess.Should().BeTrue(Dump(res));
            return ((PagedResponse<ApprovalInboxItem>)res.Data!).Items.Select(i => i.InstanceId).ToList();
        }
        (await Ids("gm", "done")).Should().Contain(id);
        (await Ids("gm", "todo")).Should().NotContain(id);
        (await Ids("fin1", "todo")).Should().Contain(id);
    }

    [Fact]
    public async Task RecordApproval_TaskView_HasOriginalName()
    {
        var (_, id) = await Stage1();
        await Db.Context.Set<AppUser>().Where(u => u.UserId == "gm").ExecuteUpdateAsync(s => s.SetProperty(u => u.DisplayName, "總經理"));
        Db.Context.ChangeTracker.Clear();
        await MustTransfer(id, "gm", "fin1");
        As("fin1");
        var view = (RecordApprovalResponse)(await Send(new GetRecordApprovalRequest(Inst(id).RecordId))).Data!;
        var task = view.Current!.Stages.SelectMany(s => s.Tasks).Single(t => t.AssigneeUserId == "fin1");
        task.Reason.Should().Be(ApprovalTaskReason.Transferred);
        task.OriginalName.Should().Be("總經理");   // 顯示名稱，不是帳號
        task.CanAct.Should().BeTrue();
    }
}
