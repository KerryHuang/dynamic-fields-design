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
    IApprovalInstanceRepository repo, IModuleRepository modules, IRecordRepository records, ApprovalPolicies policies, ApprovalReader reader)
{
    /// <summary>
    /// 呼叫者是這個實例的簽核管理員（設計 §7.8）；實例、模組或記錄不存在回 false（LoadAsync 的 authorize 回 403，實例不存在才 404）。
    /// 已關閉的模組改看 Read 軸（見 <see cref="ApprovalReader.ManagerGateAsync"/>），fail closed。
    /// </summary>
    public async Task<bool> ManagerGateAsync(long instanceId, CancellationToken ct)
    {
        if (await repo.GetAsync(instanceId, ct) is not { } inst) return false;
        var module = await modules.GetByIdAsync(inst.ModuleId, includeChildren: false, ct);
        var record = await records.GetAsync(inst.RecordId, ct);
        return module is not null && record is not null && await reader.ManagerGateAsync(module, record, ct);
    }

    /// <summary>意見檢查（字數上限讀政策）；通過回 null。</summary>
    public async Task<MessageRef?> CommentErrorAsync(string? comment, bool required, CancellationToken ct) =>
        ApprovalActionRules.CommentError(comment, required, await policies.CommentLengthAsync(ct));

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
            return (null, ServiceResult.Conflict(mine is { Status: ApprovalTaskStatus.Open } ? ApprovalMessages.StateChanged : ApprovalMessages.StageHandled,
                mine is { Status: ApprovalTaskStatus.Open } ? "stale" : null));
        inst.RowVersion = next;
        return (new Loaded(inst, module, def, await repo.ListRunsAsync(inst.Id, ct), await repo.ListTasksAsync(inst.Id, ct)), null);
    }

    public async Task<Dictionary<string, JsonElement?>> ValuesAsync(long recordId, CancellationToken ct) =>
        (await records.GetAsync(recordId, ct)) is { } r ? RecordValueProcessor.Parse(r.ValuesJson) : [];

    public static ServiceResult Bad(string key, MessageRef message) => ServiceResult.Invalid(key, message);

    /// <summary>
    /// 會簽一人一票（設計 §7.7）：同一人在同一關已經同意過，不能再處理（同意或退回）另一個任務。
    /// 「同意過」看負責人或代理任務的本人（計畫 J9）：不論以本人或代理身分，同一關只算一票。
    /// </summary>
    public static bool AlreadyApprovedInStage(IEnumerable<ApprovalTask> tasks, ApprovalTask me, string userId) =>
        tasks.Any(t => t.StageRunId == me.StageRunId && t.Id != me.Id && t.Status == ApprovalTaskStatus.Approved && ApprovalActionRules.IsActor(t, userId));

    /// <summary>排隊中的加簽關卡改 closed（退回、撤回；設計 §7.6）。</summary>
    public static void CloseQueued(IEnumerable<ApprovalStageRun> runs, DateTime now)
    {
        foreach (var r in runs.Where(r => r.Status == ApprovalStageStatus.Queued))
        {
            r.Status = ApprovalStageStatus.Closed;
            r.CompletedTime = now;
        }
    }

    /// <summary>open 任務改 closed（已由他人處理、撤回、退回）。</summary>
    public static void CloseOpen(IEnumerable<ApprovalTask> tasks)
    {
        foreach (var t in tasks.Where(t => t.Status == ApprovalTaskStatus.Open)) t.Status = ApprovalTaskStatus.Closed;
    }
}

/// <summary>加簽計畫：加簽對象（已去重）與方式（all 會簽、any 或簽）。</summary>
public sealed record AddSignPlan(IReadOnlyList<string> UserIds, string Mode);

/// <summary>
/// 同意的核心（設計 §7.2、§8.2；計畫 J5）：或簽任一人、會簽全部席位；關卡完成就推進，沒有下一關就核准並執行轉移。
/// 加簽（設計 §7.6）是同意的一種：同樣的可編輯欄位、重新比對、一人一票，只是歷程寫 add-sign、並多建一筆排隊的加簽關卡。
/// </summary>
public sealed class ApprovalApprover(
    IApprovalInstanceRepository repo, ApprovalActionSupport support, ApprovalEngine engine, ApprovalTransitionRunner runner,
    ICurrentUser user, IBusinessTime time, IUnitOfWork uow, IModuleRepository modules, FieldTypeRegistry registry,
    IDynamicAccessGuard guard, IRecordRepository records, ApprovalReadGrant grant)
{
    public async Task<ServiceResult> ApproveAsync(long taskId, string rowVersion, string? comment, Dictionary<string, JsonElement?>? requestValues,
        AddSignPlan? addSign, CancellationToken ct)
    {
        var task = await repo.GetTaskAsync(taskId, ct);
        if (task is null) return ServiceResult.NotFound(ApprovalMessages.TaskNotFound);
        if (await support.CommentErrorAsync(comment, required: false, ct) is { } ce) return ApprovalActionSupport.Bad("comment", ce);
        var (loaded, failure) = await support.LoadAsync(task.InstanceId, rowVersion, ct,
            _ => ApprovalActionRules.IsActor(task, user.UserId) ? null : ServiceResult.Forbidden(), task);
        if (failure is not null) return failure;
        var (inst, module, def, runs, tasks) = loaded!;
        var me = tasks.Single(t => t.Id == task.Id);
        if (me.Status != ApprovalTaskStatus.Open) return ServiceResult.Conflict(ApprovalMessages.StageHandled);
        var run = runs.Single(r => r.Id == me.StageRunId);
        var stageTasks = tasks.Where(t => t.StageRunId == run.Id).ToList();
        if (ApprovalActionSupport.AlreadyApprovedInStage(tasks, me, user.UserId)) return ServiceResult.Conflict(ApprovalMessages.StageHandled);

        var values = requestValues ?? [];
        var stageDef = def.Stages.FirstOrDefault(s => s.Code == run.StageCode);
        var editable = (stageDef?.EditableFields ?? []).Select(id => $"f_{id}").ToHashSet();
        var outside = values.Keys.Where(k => !editable.Contains(k)).ToList();
        if (outside.Count > 0) return ServiceResult.Invalid(outside.ToDictionary(k => k, _ => new[] { ApprovalMessages.NotEditableField }));
        if (values.Count > 0 && await BlockedKeysAsync(module, inst.RecordId, values.Keys, ct) is { Count: > 0 } blocked)
            return ServiceResult.Invalid(blocked.ToDictionary(b => b.Key, b => new[] { b.Value }));

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
            var (fresh, rerouted, rerouteFailure) = await engine.RerouteAsync(inst, check, module, current, ct, values.Keys);
            if (rerouteFailure is not null) return rerouteFailure;
            await uow.CommitAsync(ct);
            return ServiceResult.Success(new ApprovalActionResponse(rerouted, fresh?.Id ?? inst.Id, fresh?.RowVersion ?? inst.RowVersion, check.NewFlow?.Name, NeedsResubmit: fresh is null));
        }

        var now = time.Now;
        var onBehalfOf = ApprovalActionRules.OnBehalfOf(me, user.UserId);
        me.Status = ApprovalTaskStatus.Approved;
        me.ActedTime = now;
        if (addSign is null)
            engine.Log(inst, ApprovalActions.Approve, user.UserId, run.Id, me.Id, ApproveTaskHandler.Clean(comment),
                data: values.Count > 0 ? JsonSerializer.Serialize(values.Keys) : null, onBehalfOf: onBehalfOf);
        else
        {
            // 位置在寫這次的 add-sign 歷程之前算：同一關先前加簽出的關卡數
            var priorAddSigns = (await repo.ListActionsAsync([inst.Id], ct)).Count(a => a.Action == ApprovalActions.AddSign && a.StageRunId == run.Id);
            engine.Log(inst, ApprovalActions.AddSign, user.UserId, run.Id, me.Id, ApproveTaskHandler.Clean(comment), targets: [.. addSign.UserIds],
                data: values.Count > 0 ? JsonSerializer.Serialize(new { mode = addSign.Mode, editedKeys = values.Keys }) : JsonSerializer.Serialize(new { mode = addSign.Mode }),
                onBehalfOf: onBehalfOf);
            // 排隊中的加簽關卡：這一關完成後、下一個定義關卡之前依 Seq 啟動（設計 §7.6「插在目前關卡之後」；a3c 審查 S1）。
            // 位置緊接目前關卡與它先前加簽出的關卡（同一關先加簽的先簽）；排在後面的其他排隊關卡（例如巢狀加簽時，上一層其他人的加簽）順延一號
            var seq = run.Seq + 1 + priorAddSigns;
            foreach (var later in runs.Where(r => r.Status == ApprovalStageStatus.Queued && r.Seq >= seq)) later.Seq++;
            repo.AddRun(new ApprovalStageRun
            {
                InstanceId = inst.Id, Seq = seq, StageCode = null, NameSnapshot = ApprovalMessages.AddSignStageName, Mode = addSign.Mode,
                Status = ApprovalStageStatus.Queued, AddedByUserId = user.UserId, AddSignUserIds = [.. addSign.UserIds]
            });
        }

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
            // 先啟動排隊的加簽關卡；備援實例沒有排隊的就核准並轉移（設計 §5.5 第 5 點）——兩者都在 ActivateNextAsync 裡處理
            var (next, nextFailure) = await engine.ActivateNextAsync(inst, module, current, ct);
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
    /// 這位簽核人不能寫的鍵（設計 §9.2「欄位對該簽核人隱藏時，不能編輯」；裁決 22 執行期；a4b4 審查 U3 加上唯讀）：隱藏回隱藏的訊息，
    /// 欄位例外唯讀或應用程式關閉而凍結回唯讀的訊息。與簽核檢視共用同一個來源（有 Read 用 Read、沒有用臨時讀取權、再沒有用自己的欄位例外；
    /// 見 ApprovalReadGrant.HiddenFieldIdsAsync／UnwritableFieldIdsAsync），所以畫面上列為可編輯的欄位，同意時一定能改；
    /// 讀不到權限輸入時全部當成隱藏（fail closed）。
    /// </summary>
    private async Task<Dictionary<string, MessageRef>> BlockedKeysAsync(Module module, long recordId, IEnumerable<string> keys, CancellationToken ct)
    {
        var record = await records.GetAsync(recordId, ct);
        var read = module.IsReadable() ? (await guard.AuthorizeAsync(module, PermissionAction.Read, ct)).Permissions : null;
        var granted = record is not null && read is null ? await grant.ForRecordAsync(module, record, ct) : null;
        var hidden = record is null ? null : await grant.HiddenFieldIdsAsync(module, read, granted, ct);
        var unwritable = record is null ? null : await grant.UnwritableFieldIdsAsync(module, read, granted, ct);
        var blocked = new Dictionary<string, MessageRef>();
        foreach (var k in keys)
        {
            var id = long.TryParse(k.AsSpan(2), out var parsed) ? parsed : (long?)null;
            if (hidden is null || id is null || hidden.Contains(id.Value)) blocked[k] = ApprovalMessages.FieldHiddenForApprover;
            else if (unwritable is null || unwritable.Contains(id.Value)) blocked[k] = ApprovalMessages.FieldReadOnlyForApprover;
        }
        return blocked;
    }
}

/// <summary>同意（設計 §7.2、§8.2）；實際流程在 <see cref="ApprovalApprover"/>。</summary>
public class ApproveTaskHandler(ApprovalApprover approver) : IRequestHandler<ApproveTaskRequest, ServiceResult>
{
    public Task<ServiceResult> Handle(ApproveTaskRequest request, CancellationToken ct) =>
        approver.ApproveAsync(request.TaskId, request.RowVersion, request.Comment, request.Values, null, ct);

    internal static string? Clean(string? comment) => string.IsNullOrWhiteSpace(comment) ? null : comment;
}

/// <summary>
/// 同意並加簽（設計 §7.6）：輸入檢查（人數、方式、對象、關卡數上限）在載入實例之前做完，400 不換 rowVersion；
/// 對象規則用 <see cref="ApprovalActionRules.TargetError"/> 的前三條（存在且啟用、不是自己、發起人依 A12），「這關已有票」不適用——加簽是新的一關。
/// </summary>
public class AddSignTaskHandler(
    IApprovalInstanceRepository repo, ApprovalApprover approver, ApprovalPolicies policies, IPermissionRepository permissions, ICurrentUser user)
    : IRequestHandler<AddSignTaskRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(AddSignTaskRequest request, CancellationToken ct)
    {
        var task = await repo.GetTaskAsync(request.TaskId, ct);
        if (task is null) return ServiceResult.NotFound(ApprovalMessages.TaskNotFound);
        if (!ApprovalActionRules.IsActor(task, user.UserId)) return ServiceResult.Forbidden();
        if (request.Mode is not (ApprovalModes.All or ApprovalModes.Any)) return ApprovalActionSupport.Bad("mode", ApprovalMessages.AddSignModeInvalid);
        var userIds = request.UserIds.Where(u => !string.IsNullOrWhiteSpace(u)).Distinct(StringComparer.Ordinal).ToList();
        if (userIds.Count == 0) return ApprovalActionSupport.Bad("userIds", ApprovalMessages.AddSignUsersRequired);
        var inst = await repo.GetAsync(task.InstanceId, ct);
        if (inst is null) return ServiceResult.NotFound(ApprovalMessages.TaskNotFound);
        var maxUsers = await policies.MaxAddSignUsersAsync(inst.ModuleId, ct);
        if (userIds.Count > maxUsers) return ApprovalActionSupport.Bad("userIds", ApprovalMessages.AddSignTooManyUsers(maxUsers));

        var activeUsers = (await permissions.ListUsersAsync(ct)).Where(u => u.IsActive).Select(u => u.UserId).ToHashSet(StringComparer.Ordinal);
        var allowSelf = await policies.AllowSelfApprovalAsync(inst.ModuleId, ct);
        foreach (var target in userIds)
            if (ApprovalActionRules.TargetError(target, user.UserId, inst.InitiatorUserId, allowSelf, activeUsers, []) is { } bad)
                return ApprovalActionSupport.Bad("userIds", bad);

        // 關卡數以歷程的 add-sign 筆數計（J4）：退回後重來的加簽關卡不再算一次
        var maxStages = await policies.MaxAddSignStagesAsync(inst.ModuleId, ct);
        if ((await repo.ListActionsAsync([inst.Id], ct)).Count(a => a.Action == ApprovalActions.AddSign) >= maxStages)
            return ApprovalActionSupport.Bad("userIds", ApprovalMessages.AddSignLimit(maxStages));

        return await approver.ApproveAsync(request.TaskId, request.RowVersion, request.Comment, request.Values, new AddSignPlan(userIds, request.Mode), ct);
    }
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
        if (await support.CommentErrorAsync(request.Comment, required: true, ct) is { } ce) return ApprovalActionSupport.Bad("comment", ce);
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
        var onBehalfOf = ApprovalActionRules.OnBehalfOf(me, user.UserId);

        if (request.To == ToPrevious)
        {
            var previous = ApprovalPath.Previous(runs, run);
            if (previous is null) return ApprovalActionSupport.Bad("to", ApprovalMessages.NoPreviousStage);
            Reject(me, run, tasks, runs, now, ApprovalStageStatus.Returned);
            engine.Log(inst, ApprovalActions.Return, user.UserId, run.Id, me.Id, request.Comment, onBehalfOf: onBehalfOf);
            await uow.CommitAsync(ct);
            var restartSeq = runs.Max(r => r.Seq) + 1;
            var restart = await engine.RestartStageAsync(inst, module, await support.ValuesAsync(inst.RecordId, ct), previous, restartSeq, ct);
            if (restart is not null) return restart;
            // 被退回的是加簽關卡：排回隊伍，退回目標重簽後再簽一次（a3c 審查 M1）。只是重排、不是新的加簽，所以不寫 add-sign 歷程、不計入關卡數上限；
            // 再往前退時 CloseQueued 會把它關掉
            if (run.StageCode is null)
                repo.AddRun(new ApprovalStageRun
                {
                    InstanceId = inst.Id, Seq = restartSeq + 1, StageCode = null, NameSnapshot = ApprovalMessages.AddSignStageName, Mode = run.Mode,
                    Status = ApprovalStageStatus.Queued, AddedByUserId = run.AddedByUserId, AddSignUserIds = [.. run.AddSignUserIds ?? []]
                });
            // 設計 §12「被退回」：發起人也要知道（上一關的新負責人由 RestartStageAsync 通知）
            await notifier.NotifyAsync(NotificationKinds.ApprovalReturned, inst, [inst.InitiatorUserId], user.UserId, ct);
            await uow.CommitAsync(ct);
            return ServiceResult.Success(new ApprovalActionResponse(ApprovalOutcomes.Returned, inst.Id, inst.RowVersion));
        }

        Reject(me, run, tasks, runs, now, ApprovalStageStatus.Closed);
        inst.Status = ApprovalInstanceStatus.Rejected;
        inst.CompletedTime = now;
        inst.CurrentStageRunId = null;
        context.Touch(inst.RecordId);
        engine.Log(inst, ApprovalActions.Reject, user.UserId, run.Id, me.Id, request.Comment, onBehalfOf: onBehalfOf);
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

    private static void Reject(ApprovalTask me, ApprovalStageRun run, List<ApprovalTask> tasks, List<ApprovalStageRun> runs, DateTime now, string runStatus)
    {
        me.Status = ApprovalTaskStatus.Rejected;
        me.ActedTime = now;
        ApprovalActionSupport.CloseOpen(tasks);
        run.Status = runStatus;
        run.CompletedTime = now;
        ApprovalActionSupport.CloseQueued(runs, now);
    }
}

/// <summary>撤回（設計 §7.4）：只有發起人；目前關卡還沒有任何同意或退回。</summary>
public class WithdrawApprovalHandler(
    ApprovalActionSupport support, ApprovalEngine engine, ApprovalNotifier notifier, ApprovalWriteContext context,
    ICurrentUser user, IBusinessTime time, IUnitOfWork uow) : IRequestHandler<WithdrawApprovalRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(WithdrawApprovalRequest request, CancellationToken ct)
    {
        if (await support.CommentErrorAsync(request.Comment, required: false, ct) is { } ce) return ApprovalActionSupport.Bad("comment", ce);
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
        ApprovalActionSupport.CloseQueued(runs, now);
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

/// <summary>轉交（設計 §7.5）：把自己這個任務交給另一位使用者；對象規則見 <see cref="ApprovalActionRules.TargetError"/>。</summary>
public class TransferTaskHandler(
    IApprovalInstanceRepository repo, ApprovalActionSupport support, ApprovalHandover handover, ApprovalPolicies policies,
    IPermissionRepository permissions, ICurrentUser user, IUnitOfWork uow) : IRequestHandler<TransferTaskRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(TransferTaskRequest request, CancellationToken ct)
    {
        var task = await repo.GetTaskAsync(request.TaskId, ct);
        if (task is null) return ServiceResult.NotFound(ApprovalMessages.TaskNotFound);
        if (await support.CommentErrorAsync(request.Comment, required: false, ct) is { } ce) return ApprovalActionSupport.Bad("comment", ce);
        var (loaded, failure) = await support.LoadAsync(task.InstanceId, request.RowVersion, ct,
            _ => ApprovalActionRules.IsActor(task, user.UserId) ? null : ServiceResult.Forbidden(), task);
        if (failure is not null) return failure;
        var (inst, _, _, runs, tasks) = loaded!;
        var me = tasks.Single(t => t.Id == task.Id);
        if (me.Status != ApprovalTaskStatus.Open) return ServiceResult.Conflict(ApprovalMessages.StageHandled);
        if (ApprovalActionSupport.AlreadyApprovedInStage(tasks, me, user.UserId)) return ServiceResult.Conflict(ApprovalMessages.StageHandled);

        var activeUsers = (await permissions.ListUsersAsync(ct)).Where(u => u.IsActive).Select(u => u.UserId).ToHashSet(StringComparer.Ordinal);
        var allowSelf = await policies.AllowSelfApprovalAsync(inst.ModuleId, ct);
        if (ApprovalActionRules.TargetError(request.UserId, user.UserId, inst.InitiatorUserId, allowSelf, activeUsers,
                tasks.Where(t => t.StageRunId == me.StageRunId)) is { } bad)
            return ApprovalActionSupport.Bad("userId", bad);

        await handover.HandOverAsync(inst, me, request.UserId, ApprovalActions.Transfer, user.UserId, ApprovalActionRules.OnBehalfOf(me, user.UserId),
            ApproveTaskHandler.Clean(request.Comment), ct);
        await uow.CommitAsync(ct);
        return ServiceResult.Success(new ApprovalActionResponse(ApprovalOutcomes.Transferred, inst.Id, inst.RowVersion));
    }
}

/// <summary>簽核管理員終止（設計 §7.8）：全部 open 任務與 active、queued 關卡實例關閉；記錄狀態不動；意見必填（U4）。</summary>
public class CancelApprovalHandler(
    ApprovalActionSupport support, ApprovalEngine engine, ApprovalNotifier notifier, ApprovalWriteContext context,
    ICurrentUser user, IBusinessTime time, IUnitOfWork uow) : IRequestHandler<CancelApprovalRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(CancelApprovalRequest request, CancellationToken ct)
    {
        if (await support.CommentErrorAsync(request.Comment, required: true, ct) is { } ce) return ApprovalActionSupport.Bad("comment", ce);
        var manager = await support.ManagerGateAsync(request.InstanceId, ct);
        var (loaded, failure) = await support.LoadAsync(request.InstanceId, request.RowVersion, ct, _ => manager ? null : ServiceResult.Forbidden());
        if (failure is not null) return failure;
        var (inst, _, _, runs, tasks) = loaded!;

        var now = time.Now;
        var currentRun = inst.CurrentStageRunId;
        var notify = tasks.Where(t => t.Status == ApprovalTaskStatus.Open).Select(t => t.AssigneeUserId).Append(inst.InitiatorUserId).ToList();
        ApprovalActionSupport.CloseOpen(tasks);
        foreach (var run in runs.Where(r => r.Status is ApprovalStageStatus.Active or ApprovalStageStatus.Queued))
        {
            run.Status = ApprovalStageStatus.Closed;
            run.CompletedTime = now;
        }
        inst.Status = ApprovalInstanceStatus.Cancelled;
        inst.CompletedTime = now;
        inst.CurrentStageRunId = null;
        context.Touch(inst.RecordId);
        engine.Log(inst, ApprovalActions.Cancel, user.UserId, currentRun, comment: ApproveTaskHandler.Clean(request.Comment));
        await notifier.NotifyAsync(NotificationKinds.ApprovalCancelled, inst, notify, user.UserId, ct);
        await uow.CommitAsync(ct);
        return ServiceResult.Success(new ApprovalActionResponse(ApprovalOutcomes.Cancelled, inst.Id, inst.RowVersion));
    }
}

/// <summary>
/// 簽核管理員改派（設計 §7.8）：把某個 open 任務換成另一位使用者，離職或不在的人的待簽靠它搬走。對象規則同轉交，
/// 管理員不是任務的負責人，所以沒有「不能交給自己」這條（可以改派給自己，除非他是發起人或這關已有票）。
/// 原負責人收到通知（使用者裁決 2026-11-14）：他已失去讀取權，所以只帶單號與對象姓名、不帶連結。
/// </summary>
public class ReassignTaskHandler(
    IApprovalInstanceRepository repo, ApprovalActionSupport support, ApprovalHandover handover, ApprovalPolicies policies, ApprovalNotifier notifier,
    IPermissionRepository permissions, ICurrentUser user, IUnitOfWork uow) : IRequestHandler<ReassignTaskRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(ReassignTaskRequest request, CancellationToken ct)
    {
        var task = await repo.GetTaskAsync(request.TaskId, ct);
        if (task is null) return ServiceResult.NotFound(ApprovalMessages.TaskNotFound);
        if (await support.CommentErrorAsync(request.Comment, required: false, ct) is { } ce) return ApprovalActionSupport.Bad("comment", ce);
        var manager = await support.ManagerGateAsync(task.InstanceId, ct);
        var (loaded, failure) = await support.LoadAsync(task.InstanceId, request.RowVersion, ct, _ => manager ? null : ServiceResult.Forbidden(), task);
        if (failure is not null) return failure;
        var (inst, _, _, _, tasks) = loaded!;
        var me = tasks.Single(t => t.Id == task.Id);
        if (me.Status != ApprovalTaskStatus.Open) return ServiceResult.Conflict(ApprovalMessages.StageHandled);

        var users = await permissions.ListUsersAsync(ct);
        var activeUsers = users.Where(u => u.IsActive).Select(u => u.UserId).ToHashSet(StringComparer.Ordinal);
        var allowSelf = await policies.AllowSelfApprovalAsync(inst.ModuleId, ct);
        if (ApprovalActionRules.TargetError(request.UserId, null, inst.InitiatorUserId, allowSelf, activeUsers,
                tasks.Where(t => t.StageRunId == me.StageRunId)) is { } bad)
            return ApprovalActionSupport.Bad("userId", bad);

        var previous = me.AssigneeUserId;
        await handover.HandOverAsync(inst, me, request.UserId, ApprovalActions.Reassign, user.UserId, null,
            ApproveTaskHandler.Clean(request.Comment), ct);
        var targetName = users.FirstOrDefault(u => u.UserId == request.UserId)?.DisplayName ?? request.UserId;
        await notifier.NotifyAsync(NotificationKinds.ApprovalReassignedAway, inst, [previous], user.UserId, ct,
            title: $"{inst.TitleSnapshot} → {targetName}", withLink: false);
        await uow.CommitAsync(ct);
        return ServiceResult.Success(new ApprovalActionResponse(ApprovalOutcomes.Reassigned, inst.Id, inst.RowVersion));
    }
}
