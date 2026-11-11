using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.ApprovalService;

/// <summary>
/// 關卡推進與完成（設計 §6.3、§7.2、§8.2）。這一段同意時只照綁定版本推進；每次同意的重新比對在 a2c。
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
        foreach (var seat in seats.Where(x => x.Reason == ApprovalTaskReason.Fallback))
            Log(inst, ApprovalActions.Fallback, SystemActor, run.Id, comment: ApprovalMessages.FallbackNote(seat.FallbackFor ?? ""),
                targets: [.. seat.UserIds]);
        inst.CurrentStageRunId = run.Id;
        await uow.CommitAsync(ct);
        await notifier.NotifyAsync(kind, inst, created.Select(t => t.AssigneeUserId), user.UserId, ct);
        return run;
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
