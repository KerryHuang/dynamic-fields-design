using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Time;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.ApprovalService;

/// <summary>延後寫的「待簽」通知（J6）：一個關卡啟動時建出的任務；exceptUserId 是這次動作的人，不通知自己。</summary>
public sealed record PendingNotice(string Kind, IReadOnlyList<long> TaskIds, string? ExceptUserId = null);

public enum GraphAdvance { Stopped, ReachedEnd }

/// <summary>ApprovalEngine 提供給圖引擎的單關步驟；以方法參數傳遞，不經 DI，避免 ApprovalEngine ↔ ApprovalGraphEngine 循環（J22）。</summary>
public interface IApprovalStageSteps
{
    Task<ApprovalStageRun> StartAsync(ApprovalInstance inst, ApprovalStageRun run, IReadOnlyList<SeatPlan> seats, int? dueHours,
        CancellationToken ct, string kind = NotificationKinds.ApprovalPending, List<PendingNotice>? defer = null);

    Task SkipAsync(ApprovalInstance inst, ApprovalStageRun run, string reason, CancellationToken ct);

    Task<(IReadOnlyList<SeatPlan> Seats, ServiceResult? Failure)> ResolveAddSignAsync(ApprovalInstance inst, Module module,
        ApprovalDefinition def, ApprovalStageRun addSign, IReadOnlyDictionary<string, JsonElement?> values, CancellationToken ct);

    ApprovalAction Log(ApprovalInstance inst, string action, string actor, long? runId = null, long? taskId = null, string? comment = null,
        List<string>? targets = null, string? data = null, string? onBehalfOf = null);

    /// <summary>登記異常通知並回 409；message 為 null 時回第一版的 <see cref="ApprovalMessages.RuntimeInvalid"/>（sequence 行為不變）。</summary>
    ServiceResult RuntimeInvalid(ApprovalInstance inst, string reason, MessageRef? message = null);
}

/// <summary>
/// 流程圖執行引擎（流程圖設計 §8.1–§8.3、§8.8；計畫 fg2 J4–J6、J10、J11、J13、J15、J22）：每一步在呼叫端的交易內，失敗由呼叫端回傳、整筆回滾。
/// 不注入 <see cref="ApprovalEngine"/>：單關步驟（啟動、略過、加簽解析、歷程、異常通知）由 steps 參數傳入；走到結束只回 <see cref="GraphAdvance.ReachedEnd"/>，
/// 不呼叫 CompleteAsync（唯一完成者是 <see cref="ApprovalEngine.AdvanceAsync"/>）。
/// 建圖一律用 <see cref="ApprovalGraphMode.SupportedKinds"/>、條件求值用 <see cref="ApprovalGraphMode.Eval"/>；引擎內不引入任何範本、槽位概念（§16）。
/// </summary>
public sealed class ApprovalGraphEngine(IApprovalInstanceRepository repo, ApproverResolver resolver, FieldTypeRegistry registry,
    IModuleRepository modules, IBusinessTime time, ApprovalNotifier notifier, IUnitOfWork uow)
{
    /// <summary>送出或重來後從 start 推進。</summary>
    public Task<(GraphAdvance Result, ServiceResult? Failure)> StartAsync(ApprovalInstance inst, Module module,
        IReadOnlyDictionary<string, JsonElement?> values, IApprovalStageSteps steps, CancellationToken ct) =>
        AdvanceAsync(inst, module, null, values, steps, ct);

    /// <summary>簽核節點或其加簽完成後：同段同主節點的排隊加簽先啟動，否則沿主節點出線推進（J11）。</summary>
    public Task<(GraphAdvance Result, ServiceResult? Failure)> AfterApprovedAsync(ApprovalInstance inst, Module module, ApprovalStageRun done,
        IReadOnlyDictionary<string, JsonElement?> values, IApprovalStageSteps steps, CancellationToken ct) =>
        AdvanceAsync(inst, module, done, values, steps, ct);

    /// <summary>一次推進的狀態：節點實例（含這次新寫的）、Seq 計數、延後的待簽通知、是否已走到結束。</summary>
    private sealed class Pass(ApprovalInstance inst, Module module, ApprovalDefinition def, ApprovalGraphRuntime g,
        IReadOnlyDictionary<string, JsonElement?> values, IApprovalStageSteps steps, List<ApprovalStageRun> runs, IReadOnlySet<string> userIds,
        Func<JsonElement, ConditionTruth> eval, CancellationToken ct)
    {
        public ApprovalInstance Inst { get; } = inst;
        public Module Module { get; } = module;
        public ApprovalDefinition Def { get; } = def;
        public ApprovalGraphRuntime G { get; } = g;
        public IReadOnlyDictionary<string, JsonElement?> Values { get; } = values;
        public IApprovalStageSteps Steps { get; } = steps;
        public List<ApprovalStageRun> Runs { get; } = runs;
        public IReadOnlySet<string> UserIds { get; } = userIds;
        public Func<JsonElement, ConditionTruth> Eval { get; } = eval;
        public CancellationToken Ct { get; } = ct;
        public int Seq { get; set; } = runs.Count == 0 ? 0 : runs.Max(r => r.Seq);
        public List<PendingNotice> Defer { get; } = [];
        public ResolveContext? Resolve { get; set; }
        public bool Ended { get; set; }
    }

    private static string KeyOf(ApprovalStageRun r) => r.BranchKey ?? "";

    private async Task<(GraphAdvance Result, ServiceResult? Failure)> AdvanceAsync(ApprovalInstance inst, Module module, ApprovalStageRun? done,
        IReadOnlyDictionary<string, JsonElement?> values, IApprovalStageSteps steps, CancellationToken ct)
    {
        if (ApprovalEngine.DefinitionOf(inst) is not { } def) return (GraphAdvance.Stopped, ServiceResult.Conflict(ApprovalMessages.FlowVersionMissing));
        var g = ApprovalGraphRuntime.Build(def, ApprovalGraphMode.SupportedKinds, out var errors);
        if (g is null)
            return (GraphAdvance.Stopped, steps.RuntimeInvalid(inst, $"流程圖結構不合法：{string.Join("、", errors.Select(e => $"{e.Code} {e.Key}"))}",
                ApprovalMessages.GraphRuntimeInvalid));
        var userIds = await modules.ListUserIdsAsync(ct);
        var p = new Pass(inst, module, def, g, values, steps, await repo.ListRunsAsync(inst.Id, ct), userIds,
            ApprovalGraphMode.Eval(module, values, registry, time.Today, userIds), ct);

        var failure = done is null ? await EnterAsync(p, g.Start, null, "") : await AfterApprovedAsync(p, done);
        if (failure is not null) return (GraphAdvance.Stopped, failure);

        await FlushNoticesAsync(p);
        inst.CurrentStageRunId = p.Runs.Where(r => r.Status == ApprovalStageStatus.Active).MinBy(r => r.Seq)?.Id;   // J15
        await uow.CommitAsync(ct);
        return (p.Ended ? GraphAdvance.ReachedEnd : GraphAdvance.Stopped, null);
    }

    private async Task<ServiceResult?> AfterApprovedAsync(Pass p, ApprovalStageRun done)
    {
        if (done.NodeId is null) return p.Steps.RuntimeInvalid(p.Inst, "完成的節點實例沒有 NodeId", ApprovalMessages.GraphRuntimeInvalid);
        var key = KeyOf(done);
        // J11：只看同段（BranchKey）同主節點的排隊加簽，Seq 小的先；其他分支或其他節點的排隊加簽一律不碰
        if (p.Runs.Where(r => r.Status == ApprovalStageStatus.Queued && r.NodeId == done.NodeId && KeyOf(r) == key).MinBy(r => r.Seq) is { } queued)
        {
            var (seats, failure) = await p.Steps.ResolveAddSignAsync(p.Inst, p.Module, p.Def, queued, p.Values, p.Ct);
            if (failure is not null) return failure;
            await p.Steps.StartAsync(p.Inst, queued, seats, null, p.Ct, NotificationKinds.ApprovalAddSigned, p.Defer);
            return null;
        }
        return await FollowAsync(p, p.G.Out(done.NodeId).Single(), key);
    }

    private Task<ServiceResult?> FollowAsync(Pass p, ApprovalEdgeDef edge, string key) => EnterAsync(p, p.G.Node(edge.To), edge.Id, key);

    /// <summary>執行線經 inEdgeId（start 沒有）進入節點，依型別寫節點實例並往下走；回非 null 就是失敗，呼叫端整筆回滾。</summary>
    private Task<ServiceResult?> EnterAsync(Pass p, ApprovalNodeDef node, string? inEdgeId, string key) => node.Kind switch
    {
        ApprovalNodeKinds.Approval => EnterApprovalAsync(p, node, inEdgeId, key),
        ApprovalNodeKinds.Condition => EnterConditionAsync(p, node, inEdgeId, key),
        ApprovalNodeKinds.Fork => EnterForkAsync(p, node, inEdgeId, key),
        ApprovalNodeKinds.Join => EnterJoinAsync(p, node, inEdgeId!, key),
        ApprovalNodeKinds.End => EnterEndAsync(p, node, inEdgeId, key),
        // start 之外的瞬間節點只有條件、分支、匯合、結束；不支援的型別（通知）在 Build 就被擋下，到不了這裡
        ApprovalNodeKinds.Start => EnterStartAsync(p, node, inEdgeId, key),
        _ => Task.FromResult<ServiceResult?>(p.Steps.RuntimeInvalid(p.Inst, $"不支援的節點型別 {node.Kind}（{node.Id}）", ApprovalMessages.GraphRuntimeInvalid))
    };

    /// <summary>J10：每個節點實例都帶 NodeId、NodeKind、BranchKey、InEdgeId，StageCode = NodeId；簽核節點的 Mode 照節點，其他填 any。</summary>
    private static ApprovalStageRun NewRun(Pass p, ApprovalNodeDef node, string key, string? inEdgeId) => new()
    {
        InstanceId = p.Inst.Id, Seq = ++p.Seq, StageCode = node.Id, NodeId = node.Id, NodeKind = node.Kind, BranchKey = key, InEdgeId = inEdgeId,
        NameSnapshot = node.Name, Mode = node.Kind == ApprovalNodeKinds.Approval ? node.AsStage().Mode : ApprovalModes.Any
    };

    /// <summary>瞬間節點：寫一列 passed（開始、條件、分支、結束）。匯合的到達列另外處理。</summary>
    private ApprovalStageRun PassedRun(Pass p, ApprovalNodeDef node, string key, string? inEdgeId)
    {
        var run = NewRun(p, node, key, inEdgeId);
        run.Status = ApprovalStageStatus.Passed;
        run.ActivatedTime = run.CompletedTime = time.Now;
        repo.AddRun(run);
        p.Runs.Add(run);
        return run;
    }

    private Task<ServiceResult?> EnterStartAsync(Pass p, ApprovalNodeDef node, string? inEdgeId, string key)
    {
        PassedRun(p, node, key, inEdgeId);
        return FollowAsync(p, p.G.Out(node.Id).Single(), key);
    }

    private async Task<ServiceResult?> EnterApprovalAsync(Pass p, ApprovalNodeDef node, string? inEdgeId, string key)
    {
        var s = node.AsStage();
        var run = NewRun(p, node, key, inEdgeId);
        var next = p.G.Out(node.Id).Single();
        // 略過條件解析不了視為成立（寧可多簽一關），沿用第一版的 ApprovalRouter.Holds（J27）
        if (!ApprovalRouter.Holds(s.When, p.Module, p.Values, registry, time.Today, p.UserIds))
        {
            await p.Steps.SkipAsync(p.Inst, run, ApprovalSkipReasons.Condition, p.Ct);
            p.Runs.Add(run);
            return await FollowAsync(p, next, key);
        }
        p.Resolve ??= await resolver.ContextAsync(p.Module, p.Inst.InitiatorUserId, p.Values, null, p.Ct);
        var resolution = ApproverResolver.Resolve(p.Resolve with { Picks = p.Inst.Picks.GetValueOrDefault(s.Code) }, s.Name, s.Mode, s.Approvers,
            p.Def.FallbackApprovers, s.OnEmpty);
        if (resolution.Error is { } error) return ServiceResult.Conflict(error);
        if (resolution.AllUnitsEmpty)
        {
            await p.Steps.SkipAsync(p.Inst, run, ApprovalSkipReasons.Empty, p.Ct);
            p.Runs.Add(run);
            return await FollowAsync(p, next, key);
        }
        await p.Steps.StartAsync(p.Inst, run, resolution.Seats, s.DueHours, p.Ct, NotificationKinds.ApprovalPending, p.Defer);   // 這條執行線停住
        p.Runs.Add(run);
        return null;
    }

    private async Task<ServiceResult?> EnterConditionAsync(Pass p, ApprovalNodeDef node, string? inEdgeId, string key)
    {
        PassedRun(p, node, key, inEdgeId);
        var (edge, undecidable) = p.G.Choose(node.Id, p.Eval);
        if (undecidable)
            return p.Steps.RuntimeInvalid(p.Inst, $"條件節點 {node.Id} 無法判斷（引用的欄位或選項已不在）",
                ApprovalMessages.ConditionNodeUndecidable((object?)node.Name ?? node.Id));
        return await FollowAsync(p, edge!, key);
    }

    private async Task<ServiceResult?> EnterForkAsync(Pass p, ApprovalNodeDef node, string? inEdgeId, string key)
    {
        PassedRun(p, node, key, inEdgeId);
        var join = p.G.JoinOf(node.Id);
        foreach (var edge in p.G.Out(node.Id))
        {
            // J5：依定義順序一條一條推進；已走到結束、或配對的匯合已是任一到齊且已通過，剩下的分支不再展開（不寫任何節點實例）
            if (p.Ended || (join.Mode == ApprovalModes.Any && JoinPassed(p, join.Id, key))) break;
            if (await FollowAsync(p, edge, ApprovalGraphRuntime.Child(key, edge.Id)) is { } failure) return failure;
        }
        return null;
    }

    private static bool JoinPassed(Pass p, string joinId, string outerKey) =>
        p.Runs.Any(r => r.NodeKind == ApprovalNodeKinds.Join && r.NodeId == joinId && KeyOf(r) == outerKey && r.Status == ApprovalStageStatus.Passed);

    /// <summary>
    /// J4：到達列的 BranchKey 是配對分支節點所在的外層段，InEdgeId 是進線，到達的分支以 <see cref="ApprovalGraphRuntime.ArmOf"/> 識別。
    /// all：到達的分支湊齊配對分支節點的全部出線就整批改 passed 並沿出線繼續；any：第一列直接 passed、取消其他分支後繼續。
    /// </summary>
    private async Task<ServiceResult?> EnterJoinAsync(Pass p, ApprovalNodeDef node, string inEdgeId, string key)
    {
        var arm = p.G.ArmOf(node.Id, inEdgeId);
        var outer = key.LastIndexOf('/') is var slash and >= 0 ? key[..slash] : "";   // 到達的執行線在分支段 Child(outer, 出線) 裡
        // 兩條不變式（違反就是引擎或資料的問題，不是使用者操作）：匯合出線只推進一次；同一分支只留一列到達紀錄
        if (JoinPassed(p, node.Id, outer))
            return p.Steps.RuntimeInvalid(p.Inst, $"匯合 {node.Id} 已通過，卻又有執行線到達", ApprovalMessages.GraphRuntimeInvalid);
        if (ApprovalGraphPath.ArrivedArms(p.G, p.Runs, node.Id, outer).Contains(arm))
            return p.Steps.RuntimeInvalid(p.Inst, $"匯合 {node.Id} 的分支 {arm} 重複到達", ApprovalMessages.GraphRuntimeInvalid);

        var arrival = NewRun(p, node, outer, inEdgeId);
        arrival.Status = ApprovalStageStatus.Waiting;
        arrival.ActivatedTime = time.Now;
        repo.AddRun(arrival);
        p.Runs.Add(arrival);
        var fork = p.G.ForkOf(node.Id);

        if (node.Mode == ApprovalModes.Any)
        {
            arrival.Status = ApprovalStageStatus.Passed;
            arrival.CompletedTime = time.Now;
            await CancelOtherBranchesAsync(p, fork, node, arrival, arm, outer);
        }
        else
        {
            var arrived = ApprovalGraphPath.ArrivedArms(p.G, p.Runs, node.Id, outer);
            if (!p.G.Out(fork.Id).All(e => arrived.Contains(e.Id))) return null;   // 還有分支沒到，這條執行線停在匯合
            foreach (var r in p.Runs.Where(r => r.NodeKind == ApprovalNodeKinds.Join && r.NodeId == node.Id && KeyOf(r) == outer && r.Status == ApprovalStageStatus.Waiting))
            {
                r.Status = ApprovalStageStatus.Passed;
                r.CompletedTime = time.Now;
            }
        }
        return await FollowAsync(p, p.G.Out(node.Id).Single(), outer);
    }

    /// <summary>
    /// 任一到齊通過：其他分支段內還在走的節點實例改 closed（branchCancelled）、它們的 open 任務改 closed，寫一筆 branch-cancelled 歷程（設計 U5；不另通知被取消者）。
    /// 歷程的 comment 固定繁中、只給稽核；畫面依 DataJson 組字，名稱取 U8：到達分支最後一個已核准或略過的簽核節點 → 那條出線的標籤 → 不寫。
    /// </summary>
    private async Task CancelOtherBranchesAsync(Pass p, ApprovalNodeDef fork, ApprovalNodeDef join, ApprovalStageRun arrival, string arm, string outer)
    {
        var armKey = ApprovalGraphRuntime.Child(outer, arm);
        var cancelledKeys = p.G.Out(fork.Id).Where(e => e.Id != arm).Select(e => ApprovalGraphRuntime.Child(outer, e.Id)).ToList();
        var now = time.Now;
        var cancelled = ApprovalGraphPath.Live(p.Runs).Where(r => cancelledKeys.Any(root => ApprovalGraphRuntime.Within(KeyOf(r), root))).ToList();
        foreach (var r in cancelled)
        {
            r.Status = ApprovalStageStatus.Closed;
            r.CloseReason = ApprovalCloseReasons.BranchCancelled;
            r.CompletedTime = now;
        }
        if (cancelled.Count > 0)
        {
            var ids = cancelled.Select(r => r.Id).ToHashSet();
            ApprovalActionSupport.CloseOpen((await repo.ListTasksAsync(p.Inst.Id, p.Ct)).Where(t => ids.Contains(t.StageRunId)));
        }

        var last = p.Runs.Where(r => r.NodeKind == ApprovalNodeKinds.Approval && r.StageCode is not null
                && r.Status is ApprovalStageStatus.Approved or ApprovalStageStatus.Skipped && ApprovalGraphRuntime.Within(KeyOf(r), armKey))
            .MaxBy(r => r.Seq);
        var name = new[] { last?.NameSnapshot, p.G.Edge(arm).Label }.FirstOrDefault(n => n is not null && n.Resolve(I18nText.FallbackLocale) != "");
        var data = new Dictionary<string, object> { ["forkId"] = fork.Id, ["joinId"] = join.Id, ["arrived"] = armKey, ["cancelled"] = cancelledKeys };
        if (name is not null) data["name"] = name;

        await uow.CommitAsync(p.Ct);   // 歷程要掛到達列的 Id
        p.Steps.Log(p.Inst, ApprovalActions.BranchCancelled, ApprovalEngine.SystemActor, arrival.Id,
            comment: ApprovalMessages.BranchCancelledNote(name?.Resolve(I18nText.FallbackLocale) ?? "（未命名分支）"), data: JsonSerializer.Serialize(data));
    }

    private Task<ServiceResult?> EnterEndAsync(Pass p, ApprovalNodeDef node, string? inEdgeId, string key)
    {
        // G8 的防線：到結束時不該還有進行中的執行線
        if (ApprovalGraphPath.Live(p.Runs).Any())
            return Task.FromResult<ServiceResult?>(p.Steps.RuntimeInvalid(p.Inst, "走到結束時還有進行中的執行線", ApprovalMessages.GraphRuntimeInvalid));
        PassedRun(p, node, key, inEdgeId);
        p.Ended = true;
        return Task.FromResult<ServiceResult?>(null);
    }

    /// <summary>J6、U7：推進途中收集的「待簽」通知，推進結束才寫，只寫任務仍是 open 的（被任一到齊取消的分支負責人不會收到）。</summary>
    private async Task FlushNoticesAsync(Pass p)
    {
        if (p.Defer.Count == 0) return;
        var tasks = (await repo.ListTasksAsync(p.Inst.Id, p.Ct)).ToDictionary(t => t.Id);
        foreach (var notice in p.Defer)
        {
            var assignees = notice.TaskIds.Where(id => tasks.TryGetValue(id, out var t) && t.Status == ApprovalTaskStatus.Open).Select(id => tasks[id].AssigneeUserId).ToList();
            if (assignees.Count > 0) await notifier.NotifyAsync(notice.Kind, p.Inst, assignees, notice.ExceptUserId, p.Ct);
        }
    }
}
