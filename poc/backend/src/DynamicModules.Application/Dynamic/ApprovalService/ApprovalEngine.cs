using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.ApprovalService;

/// <summary>
/// 關卡推進、重新比對後的重來與完成（設計 §5.5、§6.3、§7.2、§8.2）。
/// 系統動作（跳過、完成、備援說明）的歷程以 <see cref="SystemActor"/> 記錄。
/// </summary>
public sealed class ApprovalEngine(
    IApprovalInstanceRepository repo, ApproverResolver resolver, ApprovalTaskFactory tasks, ApprovalNotifier notifier,
    ApprovalTransitionRunner runner, FieldTypeRegistry registry, IModuleRepository modules, IBusinessTime time, ICurrentUser user,
    IOperationContext op, IUnitOfWork uow, ApprovalWriteContext context)
{
    public const string SystemActor = "system";

    /// <summary>實例綁定版本的定義（從快照取，不讀版本表的現行內容）。</summary>
    public static ApprovalDefinition? DefinitionOf(ApprovalInstance inst) =>
        inst.FlowSetSnapshot.FirstOrDefault(e => e.FlowVersionId == inst.FlowVersionId)?.Definition;

    public ApprovalAction Log(ApprovalInstance inst, string action, string actor, long? runId = null, long? taskId = null, string? comment = null,
        List<string>? targets = null, string? data = null)
    {
        var a = new ApprovalAction
        {
            InstanceId = inst.Id, StageRunId = runId, TaskId = taskId, Action = action, ActorUserId = actor, Comment = comment,
            TargetUserIds = targets, DataJson = data, CreatedTime = time.Now, OperationId = op.OperationId
        };
        repo.AddAction(a);
        return a;
    }

    /// <summary>從目前位置之後找下一關：when 不成立寫 skip(condition)；AllUnitsEmpty 寫 skip(empty)；成立就建 StageRun 與任務。沒有下一關回 null。</summary>
    public async Task<(ApprovalStageRun? Run, ServiceResult? Failure)> ActivateNextAsync(
        ApprovalInstance inst, Module module, IReadOnlyDictionary<string, JsonElement?> values, CancellationToken ct)
    {
        if (DefinitionOf(inst) is not { } def) return (null, ServiceResult.Conflict(ApprovalMessages.FlowVersionMissing));
        var runs = await repo.ListRunsAsync(inst.Id, ct);
        var last = runs.LastOrDefault(r => r.StageCode is not null);
        var start = last is null ? 0 : def.Stages.FindIndex(s => s.Code == last.StageCode) + 1;
        var seq = runs.Count == 0 ? 0 : runs.Max(r => r.Seq);
        var userIds = await modules.ListUserIdsAsync(ct);
        ResolveContext? ctx = null;

        for (var i = start; i < def.Stages.Count; i++)
        {
            var s = def.Stages[i];
            var run = new ApprovalStageRun { InstanceId = inst.Id, Seq = ++seq, StageCode = s.Code, NameSnapshot = s.Name, Mode = s.Mode };
            if (!ApprovalRouter.Holds(s.When, module, values, registry, time.Today, userIds))
            {
                await SkipAsync(inst, run, ApprovalSkipReasons.Condition, ct);
                continue;
            }
            ctx ??= await resolver.ContextAsync(module, inst.InitiatorUserId, values, null, ct);
            var resolution = ApproverResolver.Resolve(ctx with { Picks = inst.Picks.GetValueOrDefault(s.Code) }, s.Name, s.Mode, s.Approvers,
                def.FallbackApprovers, s.OnEmpty);
            if (resolution.Error is { } error) return (null, ServiceResult.Conflict(error));
            if (resolution.AllUnitsEmpty)
            {
                await SkipAsync(inst, run, ApprovalSkipReasons.Empty, ct);
                continue;
            }
            return (await StartAsync(inst, run, resolution.Seats, s.DueHours, ct), null);
        }
        inst.CurrentStageRunId = null;
        return (null, null);
    }

    /// <summary>啟動一個關卡：建 StageRun、任務、備援說明，通知新任務的負責人（不通知這次動作的人自己）。</summary>
    public async Task<ApprovalStageRun> StartAsync(ApprovalInstance inst, ApprovalStageRun run, IReadOnlyList<SeatPlan> seats, int? dueHours,
        CancellationToken ct, string kind = NotificationKinds.ApprovalPending)
    {
        var now = time.Now;
        run.Status = ApprovalStageStatus.Active;
        run.ActivatedTime = now;
        run.DueTime = dueHours is { } h ? now.AddHours(h) : null;
        repo.AddRun(run);
        await uow.CommitAsync(ct);
        var created = await tasks.CreateAsync(inst, run, seats, ct);
        foreach (var seat in seats.Where(x => x.Reason == ApprovalTaskReason.Fallback && x.FallbackFor is not null))
            Log(inst, ApprovalActions.Fallback, SystemActor, run.Id, comment: ApprovalMessages.FallbackNote(seat.FallbackFor!),
                targets: [.. seat.UserIds]);
        inst.CurrentStageRunId = run.Id;
        await uow.CommitAsync(ct);
        await notifier.NotifyAsync(kind, inst, created.Select(t => t.AssigneeUserId), user.UserId, ct);
        return run;
    }

    /// <summary>備援實例唯一一關的關卡代號；不符合定義的代號格式，不會和流程的關卡撞名。</summary>
    public const string FallbackStageCode = "_fallback";

    /// <summary>
    /// 路線不同時重來（設計 §5.5 第 4 到 6 點）：這次同意不計入；舊實例 rerouted、open 任務關閉、寫 reroute 歷程；
    /// 新流程含發起人自選 → 不建新實例；快照沒有流程符合 → 備援實例（一關或簽）；否則依新流程從第 1 關開始。
    /// 新實例沿用舊快照、發起人不變。通知發起人、舊實例中同意過的人與這次按同意的人 approvalRerouted。回傳新實例（沒建就是 null）或失敗。
    /// </summary>
    public async Task<(ApprovalInstance? Fresh, string Outcome, ServiceResult? Failure)> RerouteAsync(ApprovalInstance old, RouteCheck check, Module module,
        IReadOnlyDictionary<string, JsonElement?> values, CancellationToken ct)
    {
        var now = time.Now;
        var runs = await repo.ListRunsAsync(old.Id, ct);
        var tasks = await repo.ListTasksAsync(old.Id, ct);
        var notify = tasks.Where(t => t.Status == ApprovalTaskStatus.Approved).Select(t => t.AssigneeUserId)
            .Prepend(user.UserId).Prepend(old.InitiatorUserId).ToList();
        foreach (var t in tasks.Where(t => t.Status == ApprovalTaskStatus.Open)) t.Status = ApprovalTaskStatus.Closed;
        foreach (var r in runs.Where(r => r.Status is ApprovalStageStatus.Active or ApprovalStageStatus.Queued))
        {
            r.Status = ApprovalStageStatus.Closed;
            r.CompletedTime = now;
        }
        old.Status = ApprovalInstanceStatus.Rerouted;
        old.CompletedTime = now;
        old.CurrentStageRunId = null;
        context.Touch(old.RecordId);
        var name = check.NewFlow?.Name.Resolve(I18nText.FallbackLocale);
        Log(old, ApprovalActions.Reroute, SystemActor, comment: check.NewFlow is null ? ApprovalMessages.RerouteFallbackNote
            : check.NeedsPick ? ApprovalMessages.ReroutePickNote(name!) : ApprovalMessages.RerouteNote(name!),
            data: JsonSerializer.Serialize(new { flowId = check.NewFlow?.FlowId, flowVersionId = check.NewFlow?.FlowVersionId }));
        await uow.CommitAsync(ct);   // 舊實例先離開 pending，部分唯一索引才放得下新實例

        if (check.NeedsPick)
        {
            await notifier.NotifyAsync(NotificationKinds.ApprovalRerouted, old, notify, null, ct);
            return (null, ApprovalOutcomes.Rerouted, null);
        }

        var fresh = new ApprovalInstance
        {
            RecordId = old.RecordId, ModuleId = old.ModuleId,
            // 備援實例保留舊綁定版本：備援名單與 rejectTo 都取自它（設計 §5.5 第 5 點）
            FlowId = check.NewFlow?.FlowId ?? old.FlowId, FlowVersionId = check.NewFlow?.FlowVersionId ?? old.FlowVersionId,
            FlowSetSnapshot = old.FlowSetSnapshot, RouteKind = check.NewFlow is null ? ApprovalRouteKind.Fallback : ApprovalRouteKind.Flow,
            FromOptionId = old.FromOptionId, ToOptionId = old.ToOptionId, InitiatorUserId = old.InitiatorUserId,
            Status = ApprovalInstanceStatus.Pending, RestartedFromInstanceId = old.Id, TitleSnapshot = old.TitleSnapshot,
            SubmittedTime = now, RowVersion = Guid.NewGuid().ToString(), OperationId = op.OperationId
        };
        repo.Add(fresh);
        context.Touch(fresh.RecordId);
        await uow.CommitAsync(ct);
        repo.AddSnapshotRows(old.FlowSetSnapshot.Select(e => new ApprovalInstanceFlow { InstanceId = fresh.Id, FlowId = e.FlowId, FlowVersionId = e.FlowVersionId }));

        var outcome = ApprovalOutcomes.Rerouted;
        if (check.NewFlow is null)
        {
            var ctx = await resolver.ContextAsync(module, fresh.InitiatorUserId, values, null, ct);
            var resolution = ApproverResolver.ResolveFallback(ctx, DefinitionOf(old)?.FallbackApprovers);
            if (resolution.Error is { } error) return (null, outcome, ServiceResult.Conflict(error));
            var run = new ApprovalStageRun
            {
                InstanceId = fresh.Id, Seq = 1, StageCode = FallbackStageCode, NameSnapshot = ApprovalMessages.FallbackStageName, Mode = ApprovalModes.Any
            };
            await StartAsync(fresh, run, resolution.Seats, null, ct);
        }
        else
        {
            var (run, failure) = await ActivateNextAsync(fresh, module, values, ct);
            if (failure is not null) return (null, outcome, failure);
            if (run is null)
            {
                // 新流程的關卡全部跳過：照送出時的規則直接核准
                if (await CompleteAsync(fresh, ct) is { } completeFailure) return (null, outcome, completeFailure);
                outcome = ApprovalOutcomes.Approved;
            }
        }
        await notifier.NotifyAsync(NotificationKinds.ApprovalRerouted, old, notify, null, ct);
        return (fresh, outcome, null);
    }

    /// <summary>退回上一關：用同一個關卡代號建新的關卡實例並重新解析簽核人（設計 §7.3）；新負責人收到 approvalReturned。</summary>
    public async Task<ServiceResult?> RestartStageAsync(ApprovalInstance inst, Module module, IReadOnlyDictionary<string, JsonElement?> values,
        ApprovalStageRun previous, int seq, CancellationToken ct)
    {
        if (DefinitionOf(inst) is not { } def || def.Stages.FirstOrDefault(s => s.Code == previous.StageCode) is not { } s)
            return ServiceResult.Conflict(ApprovalMessages.StageDefMissing);
        var ctx = await resolver.ContextAsync(module, inst.InitiatorUserId, values, inst.Picks.GetValueOrDefault(s.Code), ct);
        var resolution = ApproverResolver.Resolve(ctx, s.Name, s.Mode, s.Approvers, def.FallbackApprovers, s.OnEmpty);
        if (resolution.Error is { } error) return ServiceResult.Conflict(error);
        if (resolution.AllUnitsEmpty) return ServiceResult.Conflict(ApprovalMessages.NoApprover(s.Name, s.Code));
        var run = new ApprovalStageRun { InstanceId = inst.Id, Seq = seq, StageCode = s.Code, NameSnapshot = s.Name, Mode = s.Mode };
        await StartAsync(inst, run, resolution.Seats, s.DueHours, ct, NotificationKinds.ApprovalReturned);
        return null;
    }

    private async Task SkipAsync(ApprovalInstance inst, ApprovalStageRun run, string reason, CancellationToken ct)
    {
        run.Status = ApprovalStageStatus.Skipped;
        run.SkipReason = reason;
        run.ActivatedTime = run.CompletedTime = time.Now;
        repo.AddRun(run);
        await uow.CommitAsync(ct);
        Log(inst, ApprovalActions.Skip, SystemActor, run.Id, data: JsonSerializer.Serialize(new { reason }));
    }

    /// <summary>實例 approved、寫 complete、以 runner 執行 From → To；失敗回結果（呼叫端回給使用者，整筆回滾）。</summary>
    public async Task<ServiceResult?> CompleteAsync(ApprovalInstance inst, CancellationToken ct)
    {
        inst.Status = ApprovalInstanceStatus.Approved;
        inst.CompletedTime = time.Now;
        inst.CurrentStageRunId = null;
        context.Touch(inst.RecordId);
        Log(inst, ApprovalActions.Complete, SystemActor);
        // 執行前再驗（設計 §8.3）：要走的轉移仍是狀態機上已定義的轉移
        var module = await modules.GetByIdAsync(inst.ModuleId, includeChildren: true, ct);
        if (module?.Workflow?.Find(inst.FromOptionId, inst.ToOptionId) is null) return ServiceResult.Conflict(ApprovalMessages.TransitionGone);
        var result = await runner.RunAsync(inst, inst.ToOptionId, null, ct);
        if (!result.IsSuccess) return result;
        await notifier.NotifyAsync(NotificationKinds.ApprovalApproved, inst, [inst.InitiatorUserId], user.UserId, ct);
        return null;
    }
}

public static class ApprovalSkipReasons
{
    public const string Condition = "condition", Empty = "empty";
}
