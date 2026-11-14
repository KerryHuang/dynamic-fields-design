using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.PermissionService.Commands.SaveModulePermissions;
using DynamicModules.Application.Dynamic.PermissionService.Data;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetRecord;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>簽核管理員終止與改派（設計 §7.8；計畫 a3b、U4、U8）。</summary>
public class ApprovalManagerActionTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    private ApprovalTask Open(long instanceId, string user) =>
        TasksOf(instanceId).Single(t => t.AssigneeUserId == user && t.Status == ApprovalTaskStatus.Open);

    private Task<ServiceResult> Cancel(long instanceId, string user, string? comment = "供應商報價已過期，請重新詢價後再送", string? rowVersion = null)
    {
        As(user);
        return Send(new CancelApprovalRequest(instanceId, rowVersion ?? Inst(instanceId).RowVersion, comment!));
    }

    private Task<ServiceResult> Reassign(long instanceId, string user, string assignee, string to, string? comment = null, string? rowVersion = null)
    {
        var taskId = Open(instanceId, assignee).Id;
        As(user);
        return Send(new ReassignTaskRequest(taskId, rowVersion ?? Inst(instanceId).RowVersion, to, comment));
    }

    private async Task<long> SubmitAs(PurchaseRig r, string user, decimal total)
    {
        As(user);
        var po = await NewPo(r, price: total);
        return (await MustSubmit(po.Id, r.Confirmed)).InstanceId!.Value;
    }

    /// <summary>15 萬的單：第 1 關主管 gm。</summary>
    private async Task<(PurchaseRig R, long Id)> Stage1()
    {
        var r = await Scenario3();
        return (r, await SubmitAs(r, "buyer1", 150000));
    }

    /// <summary>給 user 這個模組的 Approve 軸（保留 everyone 群組的權限）。</summary>
    private async Task GrantApprove(string module, string user, DataScope scope)
    {
        var everyone = Db.Context.Set<PermissionGroup>().AsNoTracking().Single(g => g.Key == "everyone");
        var group = new PermissionGroup { Key = $"approvers-{user}", NameI18n = Text(user) };
        Db.Context.Add(group);
        await Db.Context.SaveChangesAsync();
        Db.Context.Add(new UserGroupMapping { GroupId = group.Id, UserId = user });
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
        As("admin");
        var res = await Send(new SaveModulePermissionsRequest(module, new ModulePermissionsDto(
            [new GroupPermissionDto(everyone.Id, new PrivilegesDto(true, true, true, true, false, true, false), DataScope.All),
             new GroupPermissionDto(group.Id, new PrivilegesDto(true, false, false, false, false, false, true), scope)], [], [], [])));
        res.IsSuccess.Should().BeTrue(Dump(res));
    }

    [Fact]
    public async Task Cancel_ByManager_Cancelled_StateUnchanged_TasksClosed_Notified()
    {
        var (r, id) = await Stage1();
        var recordId = Inst(id).RecordId;
        var before = StateOf(r, recordId);

        var res = await Cancel(id, "admin");

        res.IsSuccess.Should().BeTrue(Dump(res));
        var data = (ApprovalActionResponse)res.Data!;
        data.Outcome.Should().Be(ApprovalOutcomes.Cancelled);
        data.InstanceRowVersion.Should().Be(Inst(id).RowVersion);
        var inst = Inst(id);
        inst.Status.Should().Be(ApprovalInstanceStatus.Cancelled);
        inst.CompletedTime.Should().NotBeNull();
        inst.CurrentStageRunId.Should().BeNull();
        TasksOf(id).Should().OnlyContain(t => t.Status == ApprovalTaskStatus.Closed);
        Db.Context.Set<ApprovalStageRun>().AsNoTracking().Where(x => x.InstanceId == id).Should().OnlyContain(x => x.Status == ApprovalStageStatus.Closed);
        StateOf(r, recordId).Should().Be(before);
        As("buyer1");
        (await Put(recordId, V((r.Memo, "改一下")))).IsSuccess.Should().BeTrue();
        Notices("buyer1", NotificationKinds.ApprovalCancelled).Should().Be(1);
        Notices("gm", NotificationKinds.ApprovalCancelled).Should().Be(1);
        Notices("admin", NotificationKinds.ApprovalCancelled).Should().Be(0);
        var last = ActionsOf(id).Last();
        last.Action.Should().Be(ApprovalActions.Cancel);
        last.ActorUserId.Should().Be("admin");
        last.Comment.Should().Be("供應商報價已過期，請重新詢價後再送");
    }

    [Fact]
    public async Task Cancel_LockedFrom_StaysLockedState()
    {
        // §17.5 二審第 2 項（管理員終止變體）：已確認（簽核鎖定）的單送作廢，被管理員終止後記錄仍已確認、仍鎖定
        var r = await BuildPurchase();
        await SeedFlow(r, "po-a", r.Confirmed, 1, Def(Cond(r.NetTotal, "gte", 0), Stage("lead", Group(PurchaseLeadGroup))));
        await SeedFlow(r, "po-x", r.Cancelled, 1, Def(null, Stage("lead", Group(PurchaseLeadGroup))));
        (await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed), Plain(r.Confirmed, r.Draft), Plain(r.Confirmed, r.Partial),
            Plain(r.Partial, r.Received), Needs(r.Confirmed, r.Cancelled)])).IsSuccess.Should().BeTrue();
        await GrantEveryone(r.Module, r.Extra);
        As("buyer1");
        var po = await NewPo(r, extra: V((r.Discount, 0)));
        var first = (await MustSubmit(po.Id, r.Confirmed)).InstanceId!.Value;
        As("buyer2");
        (await Send(new ApproveTaskRequest(Open(first, "buyer2").Id, Inst(first).RowVersion, null, null))).IsSuccess.Should().BeTrue();
        As("buyer1");
        var second = (await MustSubmit(po.Id, r.Cancelled)).InstanceId!.Value;

        var res = await Cancel(second, "admin");

        res.IsSuccess.Should().BeTrue(Dump(res));
        StateOf(r, po.Id).Should().Be(r.Confirmed);
        As("buyer1");
        var put = await Put(po.Id, V((r.Discount, 50)));
        put.IsSuccess.Should().BeFalse();
        put.Errors.Should().ContainKey($"f_{r.Discount}");
    }

    [Fact]
    public async Task Cancel_CommentBlank_400_NothingWritten()
    {
        var (_, id) = await Stage1();
        string Snapshot() => string.Join(",", TasksOf(id).Select(t => t.Status)) + $"|{ActionsOf(id).Count}|{Inst(id).RowVersion}";
        var before = Snapshot();
        foreach (var blank in new[] { "", "   " })
        {
            var res = await Cancel(id, "admin", blank);
            res.IsInvalid.Should().BeTrue(Dump(res));
            res.Errors.Should().ContainKey("comment");
        }
        Snapshot().Should().Be(before);
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Pending);
    }

    [Fact]
    public async Task Cancel_NotManager_403()
    {
        var (r, id) = await Stage1();
        var asGm = await Cancel(id, "gm");
        asGm.IsForbidden.Should().BeTrue(Dump(asGm));   // 任務負責人沒有 Approve 軸
        // 有 Approve，但資料範圍只含自己的記錄（這筆是 buyer1 的）；不在 everyone 群組，否則 All 範圍會被其他群組帶進來
        Db.Context.Add(new AppUser { UserId = "scoped", DisplayName = "scoped" });
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
        await GrantApprove(r.Module, "scoped", DataScope.Own);
        var res = await Cancel(id, "scoped");
        res.IsForbidden.Should().BeTrue(Dump(res));
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Pending);
    }

    [Fact]
    public async Task Cancel_NonAdminManagerInScope_Succeeds()
    {
        var (r, id) = await Stage1();
        await GrantApprove(r.Module, "emp1", DataScope.All);
        var res = await Cancel(id, "emp1");
        res.IsSuccess.Should().BeTrue(Dump(res));
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Cancelled);
    }

    [Fact]
    public async Task Cancel_ClosedModule_409()
    {
        var (r, id) = await Stage1();
        await Db.Context.Set<Module>().Where(m => m.Key == r.Module).ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, ModuleStatus.Closed));
        Db.Context.ChangeTracker.Clear();
        var res = await Cancel(id, "admin");
        res.IsConflict.Should().BeTrue(Dump(res));
    }

    [Fact]
    public async Task Cancel_StaleRowVersion_409()
    {
        var (_, id) = await Stage1();
        var res = await Cancel(id, "admin", rowVersion: "stale");
        res.IsConflict.Should().BeTrue(Dump(res));
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Pending);
    }

    [Fact]
    public async Task Cancel_AlreadyEnded_409()
    {
        var (_, id) = await Stage1();
        As("buyer1");
        (await Send(new WithdrawApprovalRequest(id, Inst(id).RowVersion, null))).IsSuccess.Should().BeTrue();
        var res = await Cancel(id, "admin");
        res.IsConflict.Should().BeTrue(Dump(res));
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Withdrawn);
    }

    [Fact]
    public async Task Cancel_RevokesAllGrants()
    {
        var (r, id) = await Stage1();
        As("admin");
        await RevokeAll(r.Module);   // 沒有任何人有模組 Read，全靠臨時讀取
        var recordId = Inst(id).RecordId;
        As("gm");
        (await Send(new GetRecordRequest(recordId))).IsSuccess.Should().BeTrue();

        // 管理員要有 Approve 軸才能終止：admin 是系統管理員，不受 RevokeAll 影響
        (await Cancel(id, "admin")).IsSuccess.Should().BeTrue();

        As("gm");
        var gone = await Send(new GetRecordRequest(recordId));
        gone.IsForbidden.Should().BeTrue(Dump(gone));   // 計畫寫 404；沒 Read 也不是可動作者，guard 回 403（設計文件已更正）
    }

    [Fact]
    public async Task Cancel_ClosesQueuedRuns()
    {
        var (_, id) = await Stage1();
        Db.Context.Add(new ApprovalStageRun { InstanceId = id, Seq = 99, StageCode = null, Status = ApprovalStageStatus.Queued, AddedByUserId = "gm", AddSignUserIds = ["fin1"] });
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();

        (await Cancel(id, "admin")).IsSuccess.Should().BeTrue();

        Db.Context.Set<ApprovalStageRun>().AsNoTracking().Where(x => x.InstanceId == id)
            .Should().OnlyContain(x => x.Status == ApprovalStageStatus.Closed && x.CompletedTime != null);
    }

    [Fact]
    public async Task Reassign_ByManager_NewTask_ActionReassign()
    {
        var (_, id) = await Stage1();
        var old = Open(id, "gm");

        var res = await Reassign(id, "admin", "gm", "fin1", "gm 離職交接");

        res.IsSuccess.Should().BeTrue(Dump(res));
        ((ApprovalActionResponse)res.Data!).Outcome.Should().Be(ApprovalOutcomes.Reassigned);
        var tasks = TasksOf(id);
        tasks.Single(t => t.Id == old.Id).Status.Should().Be(ApprovalTaskStatus.Transferred);
        var added = tasks.Single(t => t.AssigneeUserId == "fin1");
        added.Status.Should().Be(ApprovalTaskStatus.Open);
        added.Reason.Should().Be(ApprovalTaskReason.Transferred);
        added.OriginalUserId.Should().Be("gm");
        added.SeatKey.Should().Be(old.SeatKey);
        added.DueTime.Should().Be(old.DueTime);
        var last = ActionsOf(id).Last();
        last.Action.Should().Be(ApprovalActions.Reassign);
        last.ActorUserId.Should().Be("admin");
        last.TargetUserIds.Should().Equal("fin1");
        last.Comment.Should().Be("gm 離職交接");
        Notices("fin1", NotificationKinds.ApprovalTransferred).Should().Be(1);
    }

    [Fact]
    public async Task Reassign_ToSelf_AllowedUnlessInitiator()
    {
        var (_, id) = await Stage1();
        (await Reassign(id, "admin", "gm", "admin")).IsSuccess.Should().BeTrue();
        Open(id, "admin").Reason.Should().Be(ApprovalTaskReason.Transferred);

        var r = await Scenario3("pp");
        var own = await SubmitAs(r, "admin", 50000);   // 管理員自己送的單：總額 5 萬走 B 流程，採購主管 buyer2
        var res = await Reassign(own, "admin", "buyer2", "admin");
        res.IsInvalid.Should().BeTrue(Dump(res));
        res.Errors["userId"].Should().Equal(ApprovalMessages.TargetInitiator.Resolve(null));
    }

    [Fact]
    public async Task Reassign_DeactivatedAssignee_StillWorks()
    {
        var (_, id) = await Stage1();
        await Db.Context.Set<AppUser>().Where(u => u.UserId == "gm").ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        Db.Context.ChangeTracker.Clear();
        var res = await Reassign(id, "admin", "gm", "fin1");
        res.IsSuccess.Should().BeTrue(Dump(res));
        Open(id, "fin1").Should().NotBeNull();
    }

    [Fact]
    public async Task Reassign_AssigneeWithoutApproveAxis_403()
    {
        var (_, id) = await Stage1();
        var res = await Reassign(id, "gm", "gm", "fin1");   // 任務負責人自己沒有 Approve 軸：要轉交走轉交
        res.IsForbidden.Should().BeTrue(Dump(res));
        TasksOf(id).Should().ContainSingle();
    }

    [Fact]
    public async Task Reassign_TaskNotOpen_409()
    {
        var (_, id) = await Stage1();
        var taskId = Open(id, "gm").Id;
        (await Reassign(id, "admin", "gm", "fin1")).IsSuccess.Should().BeTrue();
        As("admin");
        var res = await Send(new ReassignTaskRequest(taskId, Inst(id).RowVersion, "fin2", null));
        res.IsConflict.Should().BeTrue(Dump(res));
    }

    [Fact]
    public async Task Reassign_ToTargetInStage_400()
    {
        var (_, id) = await Stage1();
        var res = await Reassign(id, "admin", "gm", "gm");   // 接手的人就是現在的負責人
        res.IsInvalid.Should().BeTrue(Dump(res));
    }

    [Fact]
    public async Task Inbox_All_StatusCancelled_ListsIt()
    {
        var (_, id) = await Stage1();
        (await Cancel(id, "admin")).IsSuccess.Should().BeTrue();
        As("admin");
        var res = await Send(new GetApprovalTasksRequest("all", null, null, null, 1, 20, "cancelled"));
        res.IsSuccess.Should().BeTrue(Dump(res));
        ((PagedResponse<ApprovalInboxItem>)res.Data!).Items.Should().ContainSingle(i => i.InstanceId == id && i.Status == "cancelled");
    }

    [Fact]
    public async Task RecordApproval_ActionView_HasTargetNames()
    {
        var (_, id) = await Stage1();
        await Db.Context.Set<AppUser>().Where(u => u.UserId == "fin1").ExecuteUpdateAsync(s => s.SetProperty(u => u.DisplayName, "財務一"));
        Db.Context.ChangeTracker.Clear();
        (await Reassign(id, "admin", "gm", "fin1")).IsSuccess.Should().BeTrue();
        As("admin");
        var view = (RecordApprovalResponse)(await Send(new GetRecordApprovalRequest(Inst(id).RecordId))).Data!;
        var reassign = view.Current!.Actions.Single(a => a.Action == ApprovalActions.Reassign);
        reassign.TargetUserIds.Should().Equal("fin1");
        reassign.TargetNames.Should().Equal("財務一");   // 顯示名稱，不是帳號
    }
}
