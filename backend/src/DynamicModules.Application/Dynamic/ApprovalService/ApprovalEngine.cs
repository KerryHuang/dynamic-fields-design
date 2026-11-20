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
/// <see cref="AdvanceAsync"/> 是送出、同意、重來三條路徑上唯一呼叫 <see cref="CompleteAsync"/> 的地方（計畫 fg2 J22）；
/// graph 實例交給 <see cref="ApprovalGraphEngine"/>，它不注入本類別，單關步驟經 <see cref="IApprovalStageSteps"/> 以參數傳入。
/// </summary>
public sealed class ApprovalEngine(
    IApprovalInstanceRepository repo, ApproverResolver resolver, ApprovalTaskFactory tasks, ApprovalNotifier notifier,
    ApprovalTransitionRunner runner, FieldTypeRegistry registry, IModuleRepository modules, IBusinessTime time, ICurrentUser user,
    IOperationContext op, IUnitOfWork uow, ApprovalWriteContext context, ApprovalAlert alert, ApprovalGraphEngine graph, DynamicModules.Application.Common.RequestLocale requestLocale) : IApprovalStageSteps
{
    public const string SystemActor = "system";

    /// <summary>實例綁定版本的定義（從快照取，不讀版本表的現行內容）。</summary>
    public static ApprovalDefinition? DefinitionOf(ApprovalInstance inst) =>
        inst.FlowSetSnapshot.FirstOrDefault(e => e.FlowVersionId == inst.FlowVersionId)?.Definition;

    public ApprovalAction Log(ApprovalInstance inst, string action, string actor, long? runId = null, long? taskId = null, string? comment = null,
        List<string>? targets = null, string? data = null, string? onBehalfOf = null)
    {
        var a = new ApprovalAction
        {
            InstanceId = inst.Id, StageRunId = runId, TaskId = taskId, Action = action, ActorUserId = actor, OnBehalfOfUserId = onBehalfOf, Comment = comment,
            TargetUserIds = targets, DataJson = data, CreatedTime = time.Now, OperationId = op.OperationId
        };
        repo.AddAction(a);
        return a;
    }

    /// <summary>
    /// 推進到停住為止；走到底就由這裡呼叫唯一一次 <see cref="CompleteAsync"/>（J22）。done：剛完成的關卡（送出、重來傳 null）。
    /// sequence（含備援）：ActivateNextAsync，沒有下一關就完成；graph：交給圖引擎，圖引擎回報走到結束才完成。
    /// Completed 為 true 代表已核准並執行轉移；Failure 非 null 時呼叫端回給使用者、整筆回滾。
    /// </summary>
    public async Task<(bool Completed, ServiceResult? Failure)> AdvanceAsync(ApprovalInstance inst, Module module,
        IReadOnlyDictionary<string, JsonElement?> values, ApprovalStageRun? done, CancellationToken ct)
    {
        bool reachedEnd;
        if (DefinitionOf(inst) is { } def && ApprovalGraphMode.Applies(inst, def))
        {
            var (result, graphFailure) = done is null
                ? await graph.StartAsync(inst, module, values, this, ct)
                : await graph.AfterApprovedAsync(inst, module, done, values, this, ct);
            if (graphFailure is not null) return (false, graphFailure);
            reachedEnd = result == GraphAdvance.ReachedEnd;
        }
        else
        {
            // fail-closed 防線（擋門刪除後）：走流程的實例，形狀不是 sequence 也不是 graph 就停，不讀空的 Stages 直接核准
            if (inst.RouteKind == ApprovalRouteKind.Flow && DefinitionOf(inst) is { Shape: not ApprovalShapes.Sequence } bad)
                return (false, RuntimeInvalid(inst, $"流程形狀不支援：{bad.Shape}"));
            var (run, failure) = await ActivateNextAsync(inst, module, values, ct);
            if (failure is not null) return (false, failure);
            reachedEnd = run is null;
        }
        if (!reachedEnd) return (false, null);
        return await CompleteAsync(inst, ct) is { } completeFailure ? (false, completeFailure) : (true, null);
    }

    /// <summary>從目前位置之後找下一關：when 不成立寫 skip(condition)；AllUnitsEmpty 寫 skip(empty)；成立就建 StageRun 與任務。沒有下一關回 null。</summary>
    private async Task<(ApprovalStageRun? Run, ServiceResult? Failure)> ActivateNextAsync(
        ApprovalInstance inst, Module module, IReadOnlyDictionary<string, JsonElement?> values, CancellationToken ct)
    {
        if (DefinitionOf(inst) is not { } def) return (null, ServiceResult.Conflict(ApprovalMessages.FlowVersionMissing));
        var runs = await repo.ListRunsAsync(inst.Id, ct);
        // 這一關完成後，先啟動排隊中的加簽關卡（Seq 小的先，設計 §7.6）；備援實例沒有加簽就是最後一關
        if (runs.Where(r => r.Status == ApprovalStageStatus.Queued).MinBy(r => r.Seq) is { } queued)
            return await ActivateQueuedAsync(inst, module, def, queued, values, ct);
        if (inst.RouteKind == ApprovalRouteKind.Fallback) return (null, null);
        var start = ApprovalPath.NextStageIndex(def, runs);
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

    /// <summary>
    /// 啟動排隊中的加簽關卡（計畫 J3）：以 users 來源解析加簽對象，所以停用帳號改備援、發起人依 A12 移除、人數技術上限都照既有規則；
    /// 席位 Reason 改成 added（備援席位維持 fallback）；沒有逾期時數；通知 approvalAddSigned。
    /// </summary>
    private async Task<(ApprovalStageRun? Run, ServiceResult? Failure)> ActivateQueuedAsync(
        ApprovalInstance inst, Module module, ApprovalDefinition def, ApprovalStageRun queued, IReadOnlyDictionary<string, JsonElement?> values, CancellationToken ct)
    {
        var (seats, failure) = await ResolveAddSignAsync(inst, module, def, queued, values, ct);
        if (failure is not null) return (null, failure);
        return (await StartAsync(inst, queued, seats, null, ct, NotificationKinds.ApprovalAddSigned), null);
    }

    public async Task<(IReadOnlyList<SeatPlan> Seats, ServiceResult? Failure)> ResolveAddSignAsync(
        ApprovalInstance inst, Module module, ApprovalDefinition def, ApprovalStageRun addSign, IReadOnlyDictionary<string, JsonElement?> values, CancellationToken ct)
    {
        var ctx = await resolver.ContextAsync(module, inst.InitiatorUserId, values, null, ct);
        var source = new ApproverSource(ApprovalSources.Users, UserIds: [.. addSign.AddSignUserIds ?? []]);
        var resolution = ApproverResolver.Resolve(ctx, ApprovalMessages.AddSignStageName, addSign.Mode, source, def.FallbackApprovers, ApprovalOnEmpty.Fallback);
        if (resolution.Error is { } error) return ([], ServiceResult.Conflict(error));
        return ([.. resolution.Seats.Select(s => s.Reason == ApprovalTaskReason.Resolved ? s with { Reason = ApprovalTaskReason.Added } : s)], null);
    }

    /// <summary>
    /// 啟動一個關卡：建 StageRun、任務、備援說明，通知新任務的負責人（不通知這次動作的人自己）。
    /// defer 不為 null（graph 的一次推進，J6）時不立刻寫通知，只把任務記進清單，由圖引擎在推進結束時只寫仍是 open 的；sequence 不傳，照舊立即寫。
    /// </summary>
    public async Task<ApprovalStageRun> StartAsync(ApprovalInstance inst, ApprovalStageRun run, IReadOnlyList<SeatPlan> seats, int? dueHours,
        CancellationToken ct, string kind = NotificationKinds.ApprovalPending, List<PendingNotice>? defer = null)
    {
        var now = time.Now;
        run.Status = ApprovalStageStatus.Active;
        run.ActivatedTime = now;
        run.DueTime = dueHours is { } h ? now.AddHours(h) : null;
        if (run.Id == 0) repo.AddRun(run);   // 排隊中的加簽關卡已經存在，只改狀態
        await uow.CommitAsync(ct);
        var (created, notes) = await tasks.CreateAsync(inst, run, seats, ct);
        // 代理沒有套用：Targets 是本人與代理人（畫面顯示兩人姓名），DataJson 帶原因（設計 §7.7、計畫 J11）
        foreach (var note in notes)
            Log(inst, ApprovalActions.NotDelegated, SystemActor, run.Id, targets: [note.UserId, note.DelegateUserId],
                data: JsonSerializer.Serialize(new { userId = note.UserId, delegateUserId = note.DelegateUserId, reason = note.Reason }));
        foreach (var seat in seats.Where(x => x.Reason == ApprovalTaskReason.Fallback && x.FallbackFor is not null))
            // 畫面依 kind 用使用者語系組文字；comment 是固定繁中，只留給稽核
            Log(inst, ApprovalActions.Fallback, SystemActor, run.Id, comment: ApprovalMessages.FallbackNote(seat.FallbackFor!),
                targets: [.. seat.UserIds], data: JsonSerializer.Serialize(new { kind = "fallback" }));
        inst.CurrentStageRunId = run.Id;
        await uow.CommitAsync(ct);
        if (defer is not null) defer.Add(new PendingNotice(kind, [.. created.Select(t => t.Id)], user.UserId));
        else await notifier.NotifyAsync(kind, inst, created.Select(t => t.AssigneeUserId), user.UserId, ct);
        return run;
    }

    /// <summary>備援實例唯一一關的關卡代號；不符合定義的代號格式，不會和流程的關卡撞名。</summary>
    public const string FallbackStageCode = "_fallback";

    /// <summary>
    /// 路線不同時重來（設計 §5.5 第 4 到 6 點）：這次同意不計入；舊實例 rerouted、open 任務關閉、寫 reroute 歷程；
    /// 新流程含發起人自選 → 不建新實例；快照沒有流程符合 → 備援實例（一關或簽）；否則依新流程從第 1 關開始。
    /// 新實例沿用舊快照、發起人不變。通知發起人、舊實例中同意過的人與這次按同意的人 approvalRerouted。回傳新實例（沒建就是 null）或失敗。
    /// editedKeys：這次同意先寫入的可編輯欄位；值留著，reroute 歷程記下 editedBy 與 editedKeys（讀歷程時鍵依讀者欄位權限遮蔽）。
    /// </summary>
    public async Task<(ApprovalInstance? Fresh, string Outcome, ServiceResult? Failure)> RerouteAsync(ApprovalInstance old, RouteCheck check, Module module,
        IReadOnlyDictionary<string, JsonElement?> values, CancellationToken ct, IReadOnlyCollection<string>? editedKeys = null)
    {
        var now = time.Now;
        var runs = await repo.ListRunsAsync(old.Id, ct);
        var tasks = await repo.ListTasksAsync(old.Id, ct);
        var notify = tasks.Where(t => t.Status == ApprovalTaskStatus.Approved).Select(t => t.AssigneeUserId)
            .Prepend(user.UserId).Prepend(old.InitiatorUserId).ToList();
        foreach (var t in tasks.Where(t => t.Status == ApprovalTaskStatus.Open)) t.Status = ApprovalTaskStatus.Closed;
        if (DefinitionOf(old) is { } oldDef && ApprovalGraphMode.Applies(old, oldDef)) ApprovalActionSupport.CloseLive(runs, now);   // 連匯合等待中的列一起關（J14）
        else
            foreach (var r in runs.Where(r => r.Status is ApprovalStageStatus.Active or ApprovalStageStatus.Queued))
            {
                r.Status = ApprovalStageStatus.Closed;
                r.CompletedTime = now;
            }
        old.Status = ApprovalInstanceStatus.Rerouted;
        old.CompletedTime = now;
        old.CurrentStageRunId = null;
        context.Touch(old.RecordId);
        var name = check.NewFlow?.Name.Resolve(requestLocale.SystemDefault);
        // 畫面依 kind 用使用者語系組文字；comment 是固定繁中，只留給資料庫與稽核
        var rerouteKind = check.NewFlow is null ? "fallback" : check.NeedsPick ? "pick" : "flow";
        Log(old, ApprovalActions.Reroute, SystemActor, comment: check.NewFlow is null ? ApprovalMessages.RerouteFallbackNote
            : check.NeedsPick ? ApprovalMessages.ReroutePickNote(name!) : ApprovalMessages.RerouteNote(name!),
            data: editedKeys is { Count: > 0 }
                ? JsonSerializer.Serialize(new { kind = rerouteKind, flowId = check.NewFlow?.FlowId, flowVersionId = check.NewFlow?.FlowVersionId, editedBy = user.UserId, editedKeys })
                : JsonSerializer.Serialize(new { kind = rerouteKind, flowId = check.NewFlow?.FlowId, flowVersionId = check.NewFlow?.FlowVersionId }));
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
            // 新流程的關卡全部跳過（或圖直接走到結束）：照送出時的規則直接核准
            var (completed, failure) = await AdvanceAsync(fresh, module, values, null, ct);
            if (failure is not null) return (null, outcome, failure);
            if (completed) outcome = ApprovalOutcomes.Approved;
        }
        await notifier.NotifyAsync(NotificationKinds.ApprovalRerouted, old, notify, null, ct);
        return (fresh, outcome, null);
    }

    /// <summary>
    /// 退回上一關：用同一個關卡代號建新的關卡實例並重新解析簽核人（設計 §7.3）；新負責人收到 approvalReturned。
    /// 目標是加簽關卡時以同一組加簽人、對象、方式重來；是備援關卡時照建備援實例的寫法重新解析備援名單（a3c 審查 M2）。
    /// </summary>
    public async Task<ServiceResult?> RestartStageAsync(ApprovalInstance inst, Module module, IReadOnlyDictionary<string, JsonElement?> values,
        ApprovalStageRun previous, int seq, CancellationToken ct)
    {
        if (DefinitionOf(inst) is not { } def) return ServiceResult.Conflict(ApprovalMessages.StageDefMissing);
        if (previous.StageCode == FallbackStageCode)
        {
            var fallbackCtx = await resolver.ContextAsync(module, inst.InitiatorUserId, values, null, ct);
            var fallback = ApproverResolver.ResolveFallback(fallbackCtx, def.FallbackApprovers);
            if (fallback.Error is { } fallbackError) return ServiceResult.Conflict(fallbackError);
            var restarted = new ApprovalStageRun
            {
                InstanceId = inst.Id, Seq = seq, StageCode = FallbackStageCode, NameSnapshot = ApprovalMessages.FallbackStageName, Mode = ApprovalModes.Any,
                NodeId = previous.NodeId, NodeKind = previous.NodeKind, BranchKey = previous.BranchKey, InEdgeId = previous.InEdgeId
            };
            await StartAsync(inst, restarted, fallback.Seats, null, ct, NotificationKinds.ApprovalReturned);
            return null;
        }
        if (previous.StageCode is null)
        {
            // 退回的目標是加簽關卡：同一組加簽人、同樣的方式，重新解析
            var (seats, failure) = await ResolveAddSignAsync(inst, module, def, previous, values, ct);
            if (failure is not null) return failure;
            var again = new ApprovalStageRun
            {
                InstanceId = inst.Id, Seq = seq, StageCode = null, NameSnapshot = ApprovalMessages.AddSignStageName, Mode = previous.Mode,
                AddedByUserId = previous.AddedByUserId, AddSignUserIds = [.. previous.AddSignUserIds ?? []],
                NodeId = previous.NodeId, NodeKind = previous.NodeKind, BranchKey = previous.BranchKey, InEdgeId = previous.InEdgeId
            };
            await StartAsync(inst, again, seats, null, ct, NotificationKinds.ApprovalReturned);
            await RefreshCurrentAsync(inst, def, ct);
            return null;
        }
        if (ApprovalDefinitionRefs.Steps(def).FirstOrDefault(s => s.Code == previous.StageCode) is not { } s) return ServiceResult.Conflict(ApprovalMessages.StageDefMissing);
        var ctx = await resolver.ContextAsync(module, inst.InitiatorUserId, values, inst.Picks.GetValueOrDefault(s.Code), ct);
        var resolution = ApproverResolver.Resolve(ctx, s.Name, s.Mode, s.Approvers, def.FallbackApprovers, s.OnEmpty);
        if (resolution.Error is { } error) return ServiceResult.Conflict(error);
        if (resolution.AllUnitsEmpty) return ServiceResult.Conflict(ApprovalMessages.NoApprover(s.Name, s.Code));
        // graph 欄位從被退回的目標複製（J10）；sequence 都是 null
        var run = new ApprovalStageRun
        {
            InstanceId = inst.Id, Seq = seq, StageCode = s.Code, NameSnapshot = s.Name, Mode = s.Mode,
            NodeId = previous.NodeId, NodeKind = previous.NodeKind, BranchKey = previous.BranchKey, InEdgeId = previous.InEdgeId
        };
        await StartAsync(inst, run, resolution.Seats, s.DueHours, ct, NotificationKinds.ApprovalReturned);
        await RefreshCurrentAsync(inst, def, ct);
        return null;
    }

    /// <summary>graph 實例的目前關卡是 Seq 最小的 active（J15）；StartAsync 一律把剛啟動的那列設成目前，並行時不對，重啟後要重算。sequence 不動。</summary>
    private async Task RefreshCurrentAsync(ApprovalInstance inst, ApprovalDefinition def, CancellationToken ct)
    {
        if (!ApprovalGraphMode.Applies(inst, def)) return;
        inst.CurrentStageRunId = (await repo.ListRunsAsync(inst.Id, ct)).Where(r => r.Status == ApprovalStageStatus.Active).MinBy(r => r.Seq)?.Id;
    }

    public async Task SkipAsync(ApprovalInstance inst, ApprovalStageRun run, string reason, CancellationToken ct)
    {
        run.Status = ApprovalStageStatus.Skipped;
        run.SkipReason = reason;
        run.ActivatedTime = run.CompletedTime = time.Now;
        repo.AddRun(run);
        await uow.CommitAsync(ct);
        Log(inst, ApprovalActions.Skip, SystemActor, run.Id, data: JsonSerializer.Serialize(new { reason }));
    }

    /// <summary>
    /// 執行期再驗失敗：登記警示（回滾後另開交易通知系統管理員），回 409（設計 §5.2 末段）。
    /// message 為 null 時回第一版的 <see cref="ApprovalMessages.RuntimeInvalid"/>（sequence 行為不變）；graph 傳圖專用的訊息（J7、J22）。
    /// </summary>
    public ServiceResult RuntimeInvalid(ApprovalInstance inst, string reason, MessageRef? message = null)
    {
        alert.Raise(inst, reason);
        return ServiceResult.Conflict(message ?? ApprovalMessages.RuntimeInvalid);
    }

    /// <summary>實例 approved、寫 complete、以 runner 執行 From → To；失敗回結果（呼叫端回給使用者，整筆回滾）。</summary>
    private async Task<ServiceResult?> CompleteAsync(ApprovalInstance inst, CancellationToken ct)
    {
        inst.Status = ApprovalInstanceStatus.Approved;
        inst.CompletedTime = time.Now;
        inst.CurrentStageRunId = null;
        context.Touch(inst.RecordId);
        Log(inst, ApprovalActions.Complete, SystemActor);
        // 執行前再驗（設計 §8.3）：要走的轉移仍是狀態機上已定義的轉移
        var module = await modules.GetByIdAsync(inst.ModuleId, includeChildren: true, ct);
        var reason = module?.Workflow is { } wf ? ApprovalRuntimeCheck.Verify(wf, inst.FromOptionId, inst.ToOptionId, isReject: false) : "模組沒有狀態機";
        if (reason is not null) return RuntimeInvalid(inst, reason);
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
