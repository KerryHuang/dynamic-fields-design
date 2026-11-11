using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.ApprovalService;

/// <summary>動作的共同前置與載入（設計 §7、§8.4）。</summary>
public sealed class ApprovalActionSupport(
    IApprovalInstanceRepository repo, IModuleRepository modules, IRecordRepository records)
{
    public sealed record Loaded(ApprovalInstance Inst, Module Module, ApprovalDefinition Definition, List<ApprovalStageRun> Runs, List<ApprovalTask> Tasks);

    /// <summary>
    /// 不存在 404；模組 Closed 409；實例不是 pending 409；RowVersion 條件更新失敗 409。成功時實例的 RowVersion 已換新。
    /// mine：呼叫者自己的任務；版本對不上而這個任務仍是 open，代表是拿舊畫面（例如會簽另一席剛同意），回「請重新整理」而不是「已由他人處理」。
    /// </summary>
    public async Task<(Loaded? Value, ServiceResult? Failure)> LoadAsync(long instanceId, string rowVersion, CancellationToken ct,
        Func<ApprovalInstance, ServiceResult?>? authorize = null, ApprovalTask? mine = null)
    {
        var inst = await repo.GetAsync(instanceId, ct);
        if (inst is null) return (null, ServiceResult.NotFound(ApprovalMessages.TaskNotFound));
        if (authorize?.Invoke(inst) is { } denied) return (null, denied);
        var module = await modules.GetByIdAsync(inst.ModuleId, includeChildren: true, ct);
        if (module is null) return (null, ServiceResult.NotFound(ApprovalMessages.TaskNotFound));
        if (!module.IsWritable())
            return (null, module.Status == ModuleStatus.Closed ? ServiceResult.Conflict(ModuleGate.Closed) : ServiceResult.NotFound(ApprovalMessages.TaskNotFound));
        if (inst.Status != ApprovalInstanceStatus.Pending) return (null, ServiceResult.Conflict(ApprovalMessages.StageHandled));
        if (ApprovalEngine.DefinitionOf(inst) is not { } def) return (null, ServiceResult.Conflict(ApprovalMessages.FlowVersionMissing));
        var next = Guid.NewGuid().ToString();
        if (!await repo.TryBumpRowVersionAsync(inst.Id, rowVersion, next, ct))
            return (null, ServiceResult.Conflict(mine is { Status: ApprovalTaskStatus.Open } ? ApprovalMessages.StateChanged : ApprovalMessages.StageHandled));
        inst.RowVersion = next;
        return (new Loaded(inst, module, def, await repo.ListRunsAsync(inst.Id, ct), await repo.ListTasksAsync(inst.Id, ct)), null);
    }

    public async Task<Dictionary<string, JsonElement?>> ValuesAsync(long recordId, CancellationToken ct) =>
        (await records.GetAsync(recordId, ct)) is { } r ? RecordValueProcessor.Parse(r.ValuesJson) : [];

    public static ServiceResult Bad(string key, MessageRef message) => ServiceResult.Invalid(key, message);

    /// <summary>會簽一人一票（設計 §7.7）：同一人在同一關已經同意過，不能再處理（同意或退回）另一個任務。</summary>
    public static bool AlreadyApprovedInStage(IEnumerable<ApprovalTask> tasks, ApprovalTask me, string userId) =>
        tasks.Any(t => t.StageRunId == me.StageRunId && t.Id != me.Id && t.Status == ApprovalTaskStatus.Approved && t.AssigneeUserId == userId);

    /// <summary>open 任務改 closed（已由他人處理、撤回、退回）。</summary>
    public static void CloseOpen(IEnumerable<ApprovalTask> tasks)
    {
        foreach (var t in tasks.Where(t => t.Status == ApprovalTaskStatus.Open)) t.Status = ApprovalTaskStatus.Closed;
    }
}

/// <summary>同意（設計 §7.2、§8.2）：或簽任一人、會簽全部席位；關卡完成就推進，沒有下一關就核准並執行轉移。</summary>
public class ApproveTaskHandler(
    IApprovalInstanceRepository repo, ApprovalActionSupport support, ApprovalEngine engine, ApprovalTransitionRunner runner,
    IPermissionRepository permissions, ICurrentUser user, IBusinessTime time, IUnitOfWork uow, IModuleRepository modules, FieldTypeRegistry registry)
    : IRequestHandler<ApproveTaskRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(ApproveTaskRequest request, CancellationToken ct)
    {
        var task = await repo.GetTaskAsync(request.TaskId, ct);
        if (task is null) return ServiceResult.NotFound(ApprovalMessages.TaskNotFound);
        if (ApprovalActionRules.CommentError(request.Comment, required: false) is { } ce) return ApprovalActionSupport.Bad("comment", ce);
        var (loaded, failure) = await support.LoadAsync(task.InstanceId, request.RowVersion, ct,
            _ => ApprovalActionRules.IsActor(task, user.UserId) ? null : ServiceResult.Forbidden(), task);
        if (failure is not null) return failure;
        var (inst, module, def, runs, tasks) = loaded!;
        var me = tasks.Single(t => t.Id == task.Id);
        if (me.Status != ApprovalTaskStatus.Open) return ServiceResult.Conflict(ApprovalMessages.StageHandled);
        var run = runs.Single(r => r.Id == me.StageRunId);
        var stageTasks = tasks.Where(t => t.StageRunId == run.Id).ToList();
        if (ApprovalActionSupport.AlreadyApprovedInStage(tasks, me, user.UserId)) return ServiceResult.Conflict(ApprovalMessages.StageHandled);

        var values = request.Values ?? [];
        var stageDef = def.Stages.FirstOrDefault(s => s.Code == run.StageCode);
        var editable = (stageDef?.EditableFields ?? []).Select(id => $"f_{id}").ToHashSet();
        var outside = values.Keys.Where(k => !editable.Contains(k)).ToList();
        if (outside.Count > 0) return ServiceResult.Invalid(outside.ToDictionary(k => k, _ => new[] { ApprovalMessages.NotEditableField }));
        if (values.Count > 0 && await HiddenKeysAsync(module, values.Keys, ct) is { Count: > 0 } hidden)
            return ServiceResult.Invalid(hidden.ToDictionary(k => k, _ => new[] { ApprovalMessages.FieldHiddenForApprover }));

        // 可編輯欄位的豁免：送出時的狀態是簽核鎖定狀態就不放行（設計 §8.5）
        if (values.Count > 0 && (module.Workflow is { } wf ? ApprovalRuntimeCheck.VerifyEditable(wf, inst.FromOptionId) : "模組沒有狀態機") is { } locked)
            return engine.RuntimeInvalid(inst, locked);
        // 先寫可編輯欄位，再用當下的記錄值重新比對路線（設計 §5.5 第 1 點、§8.2 第 1、2 步）
        if (values.Count > 0 && await runner.RunAsync(inst, null, values, ct) is { IsSuccess: false } written) return written;
        var current = await support.ValuesAsync(inst.RecordId, ct);
        var check = ApprovalRouter.Recheck(inst, runs, module, current, registry, time.Today, await modules.ListUserIdsAsync(ct));
        if (check.Undecidable is { } undecidable) return ServiceResult.Conflict(ApprovalMessages.FlowConditionUndecidable(undecidable.Name));
        if (check.Changed)
        {
            // 這次同意不計入；回 200 讓交易提交，重來的實例才建得起來
            var (fresh, rerouted, rerouteFailure) = await engine.RerouteAsync(inst, check, module, current, ct);
            if (rerouteFailure is not null) return rerouteFailure;
            await uow.CommitAsync(ct);
            return ServiceResult.Success(new ApprovalActionResponse(rerouted, fresh?.Id ?? inst.Id, fresh?.RowVersion ?? inst.RowVersion, check.NewFlow?.Name));
        }

        var now = time.Now;
        me.Status = ApprovalTaskStatus.Approved;
        me.ActedTime = now;
        engine.Log(inst, ApprovalActions.Approve, user.UserId, run.Id, me.Id, Clean(request.Comment),
            data: values.Count > 0 ? JsonSerializer.Serialize(values.Keys) : null);

        bool complete;
        if (run.Mode == ApprovalModes.All)
        {
            foreach (var t in stageTasks.Where(t => t.SeatKey == me.SeatKey && t.Status == ApprovalTaskStatus.Open)) t.Status = ApprovalTaskStatus.Closed;
            complete = stageTasks.GroupBy(t => t.SeatKey).All(g => g.Any(t => t.Status == ApprovalTaskStatus.Approved));
        }
        else
        {
            ApprovalActionSupport.CloseOpen(stageTasks);
            complete = true;
        }

        var outcome = ApprovalOutcomes.Advanced;
        if (complete)
        {
            run.Status = ApprovalStageStatus.Approved;
            run.CompletedTime = now;
            await uow.CommitAsync(ct);
            // 備援實例只有一關：重新比對沒有流程符合，就核准並轉移（設計 §5.5 第 5 點）
            var (next, nextFailure) = inst.RouteKind == ApprovalRouteKind.Fallback
                ? (null, null)
                : await engine.ActivateNextAsync(inst, module, current, ct);
            if (nextFailure is not null) return nextFailure;
            if (next is null)
            {
                if (await engine.CompleteAsync(inst, ct) is { } completeFailure) return completeFailure;
                outcome = ApprovalOutcomes.Approved;
            }
        }
        await uow.CommitAsync(ct);
        return ServiceResult.Success(new ApprovalActionResponse(outcome, inst.Id, inst.RowVersion));
    }

    /// <summary>
    /// 對這位簽核人隱藏的鍵（設計 §9.2「欄位對該簽核人隱藏時，不能編輯」；裁決 22 執行期）：依簽核人本人的欄位權限；
    /// 讀不到權限輸入時全部當成隱藏（fail closed）。
    /// </summary>
    private async Task<List<string>> HiddenKeysAsync(Module module, IEnumerable<string> keys, CancellationToken ct)
    {
        var inputs = await permissions.LoadInputsAsync(user.UserId, module, ct);
        var hidden = inputs is null ? null : PermissionResolver.Resolve(module, inputs).HiddenFieldIds;
        return keys.Where(k => hidden is null || (long.TryParse(k.AsSpan(2), out var id) && hidden.Contains(id))).ToList();
    }

    internal static string? Clean(string? comment) => string.IsNullOrWhiteSpace(comment) ? null : comment;
}

/// <summary>退回發起人或上一關（設計 §7.3、§8.3）；意見必填。</summary>
public class RejectTaskHandler(
    IApprovalInstanceRepository repo, ApprovalActionSupport support, ApprovalEngine engine, ApprovalTransitionRunner runner,
    ApprovalNotifier notifier, ApprovalWriteContext context, ICurrentUser user, IBusinessTime time, IUnitOfWork uow)
    : IRequestHandler<RejectTaskRequest, ServiceResult>
{
    public const string ToInitiator = "initiator", ToPrevious = "previous";

    public async Task<ServiceResult> Handle(RejectTaskRequest request, CancellationToken ct)
    {
        var task = await repo.GetTaskAsync(request.TaskId, ct);
        if (task is null) return ServiceResult.NotFound(ApprovalMessages.TaskNotFound);
        if (ApprovalActionRules.CommentError(request.Comment, required: true) is { } ce) return ApprovalActionSupport.Bad("comment", ce);
        if (request.To is not (ToInitiator or ToPrevious)) return ApprovalActionSupport.Bad("to", ApprovalMessages.RejectTargetInvalid);
        var (loaded, failure) = await support.LoadAsync(task.InstanceId, request.RowVersion, ct,
            _ => ApprovalActionRules.IsActor(task, user.UserId) ? null : ServiceResult.Forbidden(), task);
        if (failure is not null) return failure;
        var (inst, module, def, runs, tasks) = loaded!;
        var me = tasks.Single(t => t.Id == task.Id);
        if (me.Status != ApprovalTaskStatus.Open) return ServiceResult.Conflict(ApprovalMessages.StageHandled);
        if (ApprovalActionSupport.AlreadyApprovedInStage(tasks, me, user.UserId)) return ServiceResult.Conflict(ApprovalMessages.StageHandled);
        var run = runs.Single(r => r.Id == me.StageRunId);
        var now = time.Now;

        if (request.To == ToPrevious)
        {
            var previous = runs.Where(r => r.Seq < run.Seq && r.Status == ApprovalStageStatus.Approved).MaxBy(r => r.Seq);
            if (previous is null) return ApprovalActionSupport.Bad("to", ApprovalMessages.NoPreviousStage);
            Reject(me, run, tasks, now, ApprovalStageStatus.Returned);
            engine.Log(inst, ApprovalActions.Return, user.UserId, run.Id, me.Id, request.Comment);
            await uow.CommitAsync(ct);
            var restart = await engine.RestartStageAsync(inst, module, await support.ValuesAsync(inst.RecordId, ct), previous, runs.Max(r => r.Seq) + 1, ct);
            if (restart is not null) return restart;
            // 設計 §12「被退回」：發起人也要知道（上一關的新負責人由 RestartStageAsync 通知）
            await notifier.NotifyAsync(NotificationKinds.ApprovalReturned, inst, [inst.InitiatorUserId], user.UserId, ct);
            await uow.CommitAsync(ct);
            return ServiceResult.Success(new ApprovalActionResponse(ApprovalOutcomes.Returned, inst.Id, inst.RowVersion));
        }

        Reject(me, run, tasks, now, ApprovalStageStatus.Closed);
        inst.Status = ApprovalInstanceStatus.Rejected;
        inst.CompletedTime = now;
        inst.CurrentStageRunId = null;
        context.Touch(inst.RecordId);
        engine.Log(inst, ApprovalActions.Reject, user.UserId, run.Id, me.Id, request.Comment);
        if (def.RejectTo is { } rt && rt != inst.FromOptionId)
        {
            // 執行前再驗（設計 §5.2、§8.3）：退回轉移已定義、不需簽核、不會讓單據不簽就進入鎖定狀態
            var reason = module.Workflow is { } wf ? ApprovalRuntimeCheck.Verify(wf, inst.FromOptionId, rt, isReject: true) : "模組沒有狀態機";
            if (reason is not null) return engine.RuntimeInvalid(inst, reason);
            if (await runner.RunAsync(inst, rt, null, ct) is { IsSuccess: false } moved) return moved;
        }
        await notifier.NotifyAsync(NotificationKinds.ApprovalRejected, inst, [inst.InitiatorUserId], user.UserId, ct);
        await uow.CommitAsync(ct);
        return ServiceResult.Success(new ApprovalActionResponse(ApprovalOutcomes.Rejected, inst.Id, inst.RowVersion));
    }

    private static void Reject(ApprovalTask me, ApprovalStageRun run, List<ApprovalTask> tasks, DateTime now, string runStatus)
    {
        me.Status = ApprovalTaskStatus.Rejected;
        me.ActedTime = now;
        ApprovalActionSupport.CloseOpen(tasks);
        run.Status = runStatus;
        run.CompletedTime = now;
    }
}

/// <summary>撤回（設計 §7.4）：只有發起人；目前關卡還沒有任何同意或退回。</summary>
public class WithdrawApprovalHandler(
    ApprovalActionSupport support, ApprovalEngine engine, ApprovalNotifier notifier, ApprovalWriteContext context,
    ICurrentUser user, IBusinessTime time, IUnitOfWork uow) : IRequestHandler<WithdrawApprovalRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(WithdrawApprovalRequest request, CancellationToken ct)
    {
        if (ApprovalActionRules.CommentError(request.Comment, required: false) is { } ce) return ApprovalActionSupport.Bad("comment", ce);
        var (loaded, failure) = await support.LoadAsync(request.InstanceId, request.RowVersion, ct,
            i => i.InitiatorUserId == user.UserId ? null : ServiceResult.Forbidden());
        if (failure is not null) return failure;
        var (inst, _, _, runs, tasks) = loaded!;
        var current = runs.FirstOrDefault(r => r.Id == inst.CurrentStageRunId) ?? runs.LastOrDefault(r => r.Status == ApprovalStageStatus.Active);
        var currentTasks = tasks.Where(t => t.StageRunId == current?.Id).ToList();
        if (currentTasks.Any(t => t.Status is ApprovalTaskStatus.Approved or ApprovalTaskStatus.Rejected))
            return ServiceResult.Conflict(ApprovalMessages.CannotWithdraw);

        var now = time.Now;
        var notify = tasks.Where(t => t.Status == ApprovalTaskStatus.Open).Select(t => t.AssigneeUserId).ToList();
        ApprovalActionSupport.CloseOpen(tasks);
        if (current is not null)
        {
            current.Status = ApprovalStageStatus.Closed;
            current.CompletedTime = now;
        }
        inst.Status = ApprovalInstanceStatus.Withdrawn;
        inst.CompletedTime = now;
        inst.CurrentStageRunId = null;
        context.Touch(inst.RecordId);
        engine.Log(inst, ApprovalActions.Withdraw, user.UserId, current?.Id, comment: ApproveTaskHandler.Clean(request.Comment));
        await notifier.NotifyAsync(NotificationKinds.ApprovalWithdrawn, inst, notify, user.UserId, ct);
        await uow.CommitAsync(ct);
        return ServiceResult.Success(new ApprovalActionResponse(ApprovalOutcomes.Withdrawn, inst.Id, inst.RowVersion));
    }
}
