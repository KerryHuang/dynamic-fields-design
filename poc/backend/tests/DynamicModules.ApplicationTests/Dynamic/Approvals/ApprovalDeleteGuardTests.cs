using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.AccountService.Commands.DeleteGroup;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.DeleteService;
using DynamicModules.Application.Dynamic.DeleteService.Data;
using DynamicModules.Application.Dynamic.DeleteService.Queries.CheckDelete;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>被流程版本引用的欄位與群組不能刪、不能停用；模組清除連帶刪掉簽核資料（設計 §8.6）。</summary>
public class ApprovalDeleteGuardTests : ApprovalTestBase
{
    private static string Dump(ServiceResult r) => r.Message + string.Join("；", r.Errors.Select(e => $"{e.Key}={string.Join("|", e.Value)}"));

    private async Task<DeleteCheckResponse> CheckField(long fieldId)
    {
        var r = await Send(new CheckDeleteRequest(DeleteTarget.Field, null, fieldId));
        r.IsSuccess.Should().BeTrue(Dump(r));
        return (DeleteCheckResponse)r.Data!;
    }

    private static IEnumerable<DeleteBlocker> Flows(DeleteCheckResponse c) => c.Blockers.Where(b => b.Kind == DeleteBlockerKind.ApprovalFlow);

    /// <summary>單據多一個沒被公式引用的整數欄位「等級」。</summary>
    private async Task<(PurchaseRig R, long Grade)> RigWithGrade()
    {
        var r = await BuildPurchase();
        return (r, (await MustAdd(r.Module, FieldType.Integer, label: "等級")).Id);
    }

    /// <summary>流程 Y（已確認）v1 的條件引用等級；之後改成 v2 不再引用。</summary>
    private async Task<(PurchaseRig R, long Grade)> OldVersionOnly(string instanceStatus)
    {
        var (r, grade) = await RigWithGrade();
        var y = await SeedFlow(r, "po-y", r.Confirmed, 1, Def(Cond(grade, "gte", 1), Stage("mgr", Manager(1))));
        await SeedInstance(r, y, instanceStatus, snapshotOnly: true);
        var upd = await Send(new UpdateApprovalFlowRequest("po-y",
            new ApprovalFlowInput("po-y", Text("po-y"), r.Confirmed, 1, true, Def(null, Stage("mgr", Manager(1))))));
        upd.IsSuccess.Should().BeTrue(Dump(upd));
        ((ApprovalFlowResponse)upd.Data!).Version.Should().Be(2);
        return (r, grade);
    }

    [Fact]
    public async Task DeleteField_UsedByCurrentVersion_Blocked()
    {
        var (r, grade) = await RigWithGrade();
        await SeedFlow(r, "po-a", r.Confirmed, 1, Def(Cond(grade, "gte", 1), Stage("mgr", Manager(1), editable: [r.DueDate])));

        var off = await Deactivate(grade);
        off.IsConflict.Should().BeTrue(Dump(off));
        off.Message.Should().Be("欄位被簽核流程「po-a」使用");
        (await Deactivate(r.DueDate)).IsConflict.Should().BeTrue();
        Flows(await CheckField(grade)).Should().ContainSingle().Which.Name.Should().Be("po-a");
        Flows(await CheckField(r.DueDate)).Should().ContainSingle();
        Flows(await CheckField(r.Memo)).Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteField_UsedOnlyByOldVersionOfPendingInstance_Blocked()
    {
        var (_, grade) = await OldVersionOnly(ApprovalInstanceStatus.Pending);

        Flows(await CheckField(grade)).Should().ContainSingle().Which.Name.Should().Be("po-y");
        (await Deactivate(grade)).IsConflict.Should().BeTrue();
    }

    [Fact]
    public async Task DeleteField_UsedOnlyByFinishedInstances_Allowed()
    {
        var (_, grade) = await OldVersionOnly(ApprovalInstanceStatus.Approved);

        Flows(await CheckField(grade)).Should().BeEmpty();
        (await Deactivate(grade)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task DeleteGroup_ReferencedByFlow_409()
    {
        var r = await BuildPurchase();
        await SeedFlow(r, "po-a", r.Confirmed, 1, Def(null, Stage("board", Group(BoardGroup))));
        await SeedFlow(r, "po-b", r.Confirmed, 2, Def(null, Stage("pick",
            new ApproverSource(ApprovalSources.InitiatorPick, CandidateGroupIds: [BoardGroup], Min: 1, Max: 1))));
        await SeedFlow(r, "po-c", r.Cancelled, 1, Def(null, Stage("lead", Group(PurchaseLeadGroup))) with { FallbackApprovers = Group(FinanceGroup) });

        var res = await Send(new DeleteGroupRequest(BoardGroup));
        res.IsConflict.Should().BeTrue(Dump(res));
        res.Message.Should().Be("群組被簽核流程使用：po-a、po-b");
        (await Send(new DeleteGroupRequest(FinanceGroup))).Message.Should().Be("群組被簽核流程使用：po-c");
    }

    [Fact]
    public async Task DeleteGroup_OnlyOldVersionOfFinishedInstance_Allowed()
    {
        var r = await BuildPurchase();
        var y = await SeedFlow(r, "po-y", r.Confirmed, 1, Def(null, Stage("board", Group(BoardGroup))));
        await SeedInstance(r, y, ApprovalInstanceStatus.Approved);
        (await Send(new UpdateApprovalFlowRequest("po-y",
            new ApprovalFlowInput("po-y", Text("po-y"), r.Confirmed, 1, true, Def(null, Stage("mgr", Manager(1))))))).IsSuccess.Should().BeTrue();

        (await Send(new DeleteGroupRequest(BoardGroup))).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task RemoveModule_PurgesApprovalRows()
    {
        var r = await BuildPurchase();
        var flow = await SeedFlow(r, "po-a", r.Confirmed, 1, Def(null, Stage("mgr", Manager(1))));
        var inst = await SeedInstance(r, flow);
        var run = new ApprovalStageRun { InstanceId = inst.Id, Seq = 1, StageCode = "mgr", Mode = ApprovalModes.Any, Status = ApprovalStageStatus.Active };
        Db.Context.Add(run);
        await Db.Context.SaveChangesAsync();
        Db.Context.Add(new ApprovalTask
        {
            StageRunId = run.Id, InstanceId = inst.Id, RecordId = inst.RecordId, AssigneeUserId = "gm", OriginalUserId = "gm", SeatKey = "gm",
            CreatedTime = SeedTime
        });
        Db.Context.Add(new ApprovalAction { InstanceId = inst.Id, Action = ApprovalActions.Submit, ActorUserId = "buyer1", CreatedTime = SeedTime, OperationId = "op" });
        Db.Context.Add(new Notification
        {
            UserId = "gm", Kind = "approval-pending", TitleSnapshot = "x", Link = "/x", SourceType = "approval", SourceId = inst.Id, CreatedTime = SeedTime
        });
        Db.Context.Add(new Notification
        {
            UserId = "gm", Kind = "approval-pending", TitleSnapshot = "y", Link = "/y", SourceType = "approval", SourceId = inst.Id + 1000, CreatedTime = SeedTime
        });
        Db.Context.Add(new ApprovalDelegate { UserId = "gm", DelegateUserId = "emp1", StartDate = new(2026, 11, 4), EndDate = new(2026, 11, 5) });
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();

        await Svc<ModuleRemover>().RemoveAsync(r.ModuleId, default);

        (await Db.Context.Set<ApprovalFlow>().CountAsync()).Should().Be(0);
        (await Db.Context.Set<ApprovalFlowVersion>().CountAsync()).Should().Be(0);
        (await Db.Context.Set<ApprovalInstance>().CountAsync()).Should().Be(0);
        (await Db.Context.Set<ApprovalInstanceFlow>().CountAsync()).Should().Be(0);
        (await Db.Context.Set<ApprovalStageRun>().CountAsync()).Should().Be(0);
        (await Db.Context.Set<ApprovalTask>().CountAsync()).Should().Be(0);
        (await Db.Context.Set<ApprovalAction>().CountAsync()).Should().Be(0);
        (await Db.Context.Set<Notification>().Select(n => n.TitleSnapshot).ToListAsync()).Should().Equal("y");
        (await Db.Context.Set<ApprovalDelegate>().CountAsync()).Should().Be(1);
    }
}
