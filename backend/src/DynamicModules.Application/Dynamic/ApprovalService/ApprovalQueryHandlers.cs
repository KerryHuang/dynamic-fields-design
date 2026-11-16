using System.Text.Json;
using System.Text.Json.Nodes;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using MediatR;

namespace DynamicModules.Application.Dynamic.ApprovalService;

/// <summary>記錄的簽核摘要與「現在能送簽的目標」；GET records/{id} 與 GET records/{id}/approval 共用。</summary>
public sealed class ApprovalReader(IApprovalInstanceRepository repo, IDynamicAccessGuard guard, IPermissionRepository permissions, ICurrentUser user,
    WorkflowService.TransitionAccess access)
{
    public const string NoInstance = "none";

    /// <summary>
    /// 從記錄目前狀態出發、需簽核、目前使用者有資格送出的目標狀態（資格同 SubmitApprovalHandler：模組可寫、Edit 含資料範圍、轉移限定群組）。
    /// 已在簽核中回空。
    /// </summary>
    public async Task<List<long>> SubmitTargetsAsync(Module module, Record record, bool pending, CancellationToken ct)
    {
        if (pending || !module.IsWritable() || module.Workflow is not { } wf) return [];
        var from = wf.StateOf(RecordValueProcessor.Parse(record.ValuesJson)) ?? wf.InitialOptionId;
        var candidates = wf.Transitions.Where(t => t.From == from && t.Approval is { Required: true }).ToList();
        if (candidates.Count == 0) return [];
        var edit = (await guard.AuthorizeAsync(module, PermissionAction.Edit, ct)).Permissions;
        if (edit is null || !InScope(edit, record.OwnerUserId)) return [];
        var targets = new List<long>();
        PermissionInputs? inputs = null;
        foreach (var t in candidates)
        {
            if (t.GroupIds.Count > 0)
            {
                inputs ??= await permissions.LoadInputsAsync(user.UserId, module, ct);
                if (inputs is null || !(await access.BypassGroupsAsync(inputs, ct) || inputs.GroupIds.Any(t.GroupIds.Contains))) continue;
            }
            targets.Add(t.To);
        }
        return targets;
    }

    /// <summary>沒有任何實例、也沒有能送簽的目標時回 null（詳情頁不顯示簽核徽章）。</summary>
    public async Task<RecordApprovalSummary?> SummaryAsync(Module module, Record record, CancellationToken ct)
    {
        var latest = (await repo.ListByRecordAsync(record.Id, ct)).FirstOrDefault();
        var pending = latest is { Status: ApprovalInstanceStatus.Pending } ? latest : null;
        var targets = await SubmitTargetsAsync(module, record, pending is not null, ct);
        if (latest is null && targets.Count == 0) return null;
        var runs = pending is null ? [] : await repo.ListRunsAsync([pending.Id], ct);
        var run = runs.FirstOrDefault(r => r.Id == pending?.CurrentStageRunId);
        var seq = run?.Seq;
        // graph：序號只數簽核節點，和時間軸（只列簽核節點，U6）看到的順序一致；Seq 把開始、分支、匯合等結構節點也算進去，不能直接給使用者
        if (run is not null && ApprovalEngine.DefinitionOf(pending!) is { } def && ApprovalGraphMode.Applies(pending!, def))
            seq = runs.Count(r => r.NodeKind == ApprovalNodeKinds.Approval && r.Seq <= run.Seq);
        // 並行時列出進行中的節點（fg4 U9）；只給看得到整張圖的讀者（M2）：有臨時讀取權的簽核人維持單一名稱，和圖的遮蔽一致
        var active = pending is not null && ApprovalEngine.DefinitionOf(pending) is { } graphDef && ApprovalGraphMode.Applies(pending, graphDef)
            ? ActiveStages(graphDef, runs) : null;
        if (active is not null && !await SeesWholeAsync(module, record, pending!, ct)) active = null;

        return new RecordApprovalSummary(latest?.Status ?? NoInstance, run?.NameSnapshot, seq, targets.Count > 0, active);
    }

    /// <summary>
    /// graph 實例進行中的簽核節點與加簽（依 Seq 排），兩個以上不同節點才回清單、否則 null（U9）。
    /// 以（節點代號、是否為加簽）去重：只有同一個節點不重複列，名稱相同但不同節點各列；加簽項目的名稱是它掛的簽核節點名稱。
    /// </summary>
    internal static List<ApprovalActiveStage>? ActiveStages(ApprovalDefinition? def, IEnumerable<ApprovalStageRun> runsOfInstance)
    {
        var active = runsOfInstance.Where(r => r.NodeKind == ApprovalNodeKinds.Approval && r.Status == ApprovalStageStatus.Active)
            .OrderBy(r => r.Seq).DistinctBy(r => (r.NodeId, AddSign: r.StageCode is null)).ToList();
        if (active.Count < 2) return null;
        return active.Select(r =>
        {
            var addSign = r.StageCode is null;
            var name = addSign ? def?.Nodes?.FirstOrDefault(n => n.Id == r.NodeId)?.Name ?? r.NameSnapshot : r.NameSnapshot;
            return new ApprovalActiveStage(name ?? new I18nText(), addSign);
        }).ToList();
    }

    /// <summary>
    /// 讀者看得到這個實例的整張圖：記錄在 Read 範圍內、簽核管理員、或發起人；條件是 <see cref="GetRecordApprovalHandler"/> 的 ownStages 的反面。
    /// 只因任務取得臨時讀取權的簽核人不算（只看得到自己的關卡）。
    /// </summary>
    private async Task<bool> SeesWholeAsync(Module module, Record record, ApprovalInstance inst, CancellationToken ct)
    {
        if (inst.InitiatorUserId == user.UserId) return true;
        if (module.IsReadable() && (await guard.AuthorizeAsync(module, PermissionAction.Read, ct)).Permissions is { } read && InScope(read, record.OwnerUserId)) return true;
        return await IsManagerAsync(module, record, ct);
    }

    /// <summary>模組可讀、Approve 動作軸、記錄在資料範圍內（簽核管理員，設計 §7.8）。</summary>
    public async Task<bool> IsManagerAsync(Module module, Record record, CancellationToken ct) =>
        module.IsReadable() && (await guard.AuthorizeAsync(module, PermissionAction.Approve, ct)).Permissions is { } approve
        && InScope(approve, record.OwnerUserId);

    /// <summary>動作前的管理員閘門：模組已關閉時 guard 擋 Approve 軸，改看 Read（含資料範圍），讓讀得到的人得到 409、其他人 403。</summary>
    public async Task<bool> ManagerGateAsync(Module module, Record record, CancellationToken ct) =>
        module.Status == ModuleStatus.Closed
            ? (await guard.AuthorizeAsync(module, PermissionAction.Read, ct)).Permissions is { } read && InScope(read, record.OwnerUserId)
            : await IsManagerAsync(module, record, ct);

    public static bool InScope(EffectivePermissions p, string ownerUserId) => p.OwnerUserIds is null || p.OwnerUserIds.Contains(ownerUserId);

    /// <summary>
    /// 沒有 Read／簽核管理權的人在各實例看得到的關卡（設計 §10.2）；發起人的實例不列（看整個實例）。
    /// open 的任務給可動作者（負責人、代理任務的本人）；結束的任務給實際處理的人（ApprovalActions.TaskHandled），
    /// 以及動作過的任務的席位本人（代理任務的 Original、其他任務的負責人）：沒動作的代理人不再保有。從沒動作就被關閉的任務不算。
    /// </summary>
    public static Dictionary<long, HashSet<long>> OwnStages(IEnumerable<ApprovalInstance> instances, IReadOnlyList<ApprovalTask> tasks,
        IReadOnlyList<ApprovalAction> actions, string me)
    {
        var handled = actions.Where(a => a.TaskId is not null && a.ActorUserId == me && ApprovalActions.TaskHandled.Contains(a.Action))
            .Select(a => a.TaskId!.Value).ToHashSet();
        bool Sees(ApprovalTask t) => t.Status == ApprovalTaskStatus.Open
            ? ApprovalActionRules.IsActor(t, me)
            : handled.Contains(t.Id) || t.ActedTime is not null && (t.Reason == ApprovalTaskReason.Delegated ? t.OriginalUserId : t.AssigneeUserId) == me;
        return instances.Where(i => i.InitiatorUserId != me)
            .ToDictionary(i => i.Id, i => tasks.Where(t => t.InstanceId == i.Id && Sees(t)).Select(t => t.StageRunId).ToHashSet());
    }

    /// <summary>
    /// 讀者參與過這筆記錄的簽核（GET records/{id}/approval 會給他歷程）：簽核管理者、某個實例的發起人，或在某個實例有看得到的關卡。
    /// 沒有 Read 也沒有臨時讀取權時，記錄端點靠它決定回 403（前端改顯示只看簽核歷程）或與不存在相同的 404（不洩漏存在性）。
    /// </summary>
    public async Task<bool> ParticipatesAsync(Module module, Record record, CancellationToken ct)
    {
        var me = user.UserId;
        var instances = await repo.ListByRecordAsync(record.Id, ct);
        if (instances.Count == 0) return false;
        if (instances.Any(i => i.InitiatorUserId == me)) return true;
        var ids = instances.Select(i => i.Id).ToList();
        if (OwnStages(instances, await repo.ListTasksAsync(ids, ct), await repo.ListActionsAsync(ids, ct), me).Values.Any(s => s.Count > 0)) return true;
        return await IsManagerAsync(module, record, ct);
    }
}

/// <summary>
/// 記錄的簽核狀態與歷程（設計 §10.2）：對記錄有 Read（含資料範圍）或簽核管理權（Approve 軸）→ 全部實例；
/// 否則只有自己送出的、或自己有任務的實例（只給歷程，不給記錄內容，也不給送簽目標）；一個都沒有 → 404。
/// </summary>
public class GetRecordApprovalHandler(
    IRecordRepository records, IModuleRepository modules, IApprovalInstanceRepository repo, IDynamicAccessGuard guard,
    IPermissionRepository permissions, ApprovalReader reader, ApprovalReadGrant grant, ICurrentUser user, ApprovalPolicies policies)
    : IRequestHandler<GetRecordApprovalRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetRecordApprovalRequest request, CancellationToken ct)
    {
        var record = await records.GetAsync(request.RecordId, ct);
        if (record is null) return ServiceResult.NotFound(RecordMapper.RecordNotFound);
        var module = await modules.GetByIdAsync(record.ModuleId, includeChildren: true, ct);
        if (module is null || ModuleRelations.IsDetail(module)) return ServiceResult.NotFound(RecordMapper.RecordNotFound);

        var me = user.UserId;
        var readable = module.IsReadable();
        var read = readable ? (await guard.AuthorizeAsync(module, PermissionAction.Read, ct)).Permissions : null;
        var canRead = read is not null && ApprovalReader.InScope(read, record.OwnerUserId);
        var manager = await reader.IsManagerAsync(module, record, ct);

        var instances = await repo.ListByRecordAsync(record.Id, ct);
        var ids = instances.Select(i => i.Id).ToList();
        var tasks = await repo.ListTasksAsync(ids, ct);
        var everyAction = await repo.ListActionsAsync(ids, ct);
        // 沒有 Read／Approve 的簽核人只看自己的關卡（設計 §10.2，規則見 ApprovalReader.OwnStages）；發起人看整個實例。
        // 標題快照的遮蔽（同記錄頁的 TitleVisibleTo）：發起人、在該實例有任務的人、簽核管理者一律看得到；
        // 其他人（例如有 Read、但標題欄位對他隱藏的旁觀者）要標題對他可見才給
        var titleVisible = read is not null && module.TitleVisibleTo(read);
        bool ShowTitle(ApprovalInstance i) => titleVisible || manager || i.InitiatorUserId == me
            || tasks.Any(t => t.InstanceId == i.Id && (t.AssigneeUserId == me || t.OriginalUserId == me));
        var ownStages = !canRead && !manager ? ApprovalReader.OwnStages(instances, tasks, everyAction, me) : new Dictionary<long, HashSet<long>>();
        var visible = instances.Where(i => !ownStages.TryGetValue(i.Id, out var own) || own.Count > 0).ToList();
        if (!canRead && !manager && visible.Count == 0) return ServiceResult.NotFound(RecordMapper.RecordNotFound);
        bool Shown(long instanceId, long? stageRunId) =>
            !ownStages.TryGetValue(instanceId, out var own) || stageRunId is { } run && own.Contains(run);

        // 沒有 Read 時，持臨時讀取權（open 任務的可動作者）的人另外看得到進行中實例的 content-changed（設計 §5.5 第 7 點：金額為什麼變了）
        var granted = read is null ? await grant.ForRecordAsync(module, record, ct) : null;
        var pendingId = instances.FirstOrDefault(i => i.Status == ApprovalInstanceStatus.Pending)?.Id;
        var visibleIds = visible.Select(i => i.Id).ToList();
        // 未過濾的關卡實例與歷程只用來算「能不能退回上一關」「還能不能加簽」（a3c 審查 M3）：只輸出 bool，不帶出其他關卡的內容
        var allRuns = await repo.ListRunsAsync(visibleIds, ct);
        var allActions = everyAction.Where(a => visibleIds.Contains(a.InstanceId)).ToList();
        var runs = allRuns.Where(r => Shown(r.InstanceId, r.Id)).ToList();
        var actions = allActions.Where(a => Shown(a.InstanceId, a.StageRunId)
            || granted is not null && a.InstanceId == pendingId && a.Action == ApprovalActions.ContentChanged).ToList();
        // 進度圖的狀態要看全部任務（退回發起人時被退回的那列是 closed，只能靠被退回的任務認出來）；遮蔽在 ApprovalViews.Instance 依 ownStages 做
        var rejectedRunIds = tasks.Where(t => t.Status == ApprovalTaskStatus.Rejected).Select(t => t.StageRunId).ToHashSet();
        tasks = tasks.Where(t => Shown(t.InstanceId, t.StageRunId)).ToList();
        var names = await ApprovalViews.UserNamesAsync(permissions, ct);
        var writable = module.IsWritable();
        // 歷程的欄位遮蔽（設計 §8.5）：有 Read 用它；沒有時用臨時讀取權，再沒有就用自己的群組欄位例外（含明細來源隱藏）；
        // 讀不到權限輸入是 null（全部遮蔽）
        var hidden = await grant.HiddenFieldIdsAsync(module, read, granted, ct);
        var unwritable = await grant.UnwritableFieldIdsAsync(module, read, granted, ct);
        var addSignLimit = await policies.MaxAddSignStagesAsync(module.Id, ct);
        var views = visible.Select(i => ApprovalViews.Instance(i, runs, tasks, actions, names, me, writable, manager, hidden, unwritable, module.Workflow, ShowTitle(i), addSignLimit,
            allRuns, allActions, rejectedRunIds, ownStages.GetValueOrDefault(i.Id))).ToList();
        var pending = instances.Any(i => i.Status == ApprovalInstanceStatus.Pending);
        var targets = canRead ? await reader.SubmitTargetsAsync(module, record, pending, ct) : [];
        return ServiceResult.Success(new RecordApprovalResponse(
            views.FirstOrDefault(v => v.Status == ApprovalInstanceStatus.Pending), views.Where(v => v.Status != ApprovalInstanceStatus.Pending).ToList(), targets));
    }
}

/// <summary>
/// 簽核中心（設計 §13.4）：todo＝我可動作的 open 任務（DueTime 升冪、null 最後）；done＝我動作過的任務（動作時間降冪）；
/// submitted＝我送出的實例；all＝我有 Approve 軸的模組的實例（含資料範圍；指定模組沒有 Approve、或沒有任何模組有 → 403）。
/// 後三者依送出時間降冪。分頁在記憶體做（POC 規模）。
/// </summary>
public class GetApprovalTasksHandler(
    IApprovalInstanceRepository repo, IModuleRepository modules, IRecordRepository records, IDynamicAccessGuard guard,
    IPermissionRepository permissions, ICurrentUser user, IBusinessTime time)
    : IRequestHandler<GetApprovalTasksRequest, ServiceResult>
{
    private sealed record Row(ApprovalInstance Inst, ApprovalTask? Task, DateTime? Due);

    public async Task<ServiceResult> Handle(GetApprovalTasksRequest request, CancellationToken ct)
    {
        if (!ApprovalInboxBoxes.Values.Contains(request.Box))
            return ServiceResult.Invalid("box", ApprovalMessages.BoxInvalid);
        if (request.Status is { } status && !ApprovalInstanceStatuses.Values.Contains(status))
            return ServiceResult.Invalid("status", ApprovalMessages.StatusInvalid);
        var page = Math.Max(request.CurrentPage, 1);
        var size = Math.Clamp(request.PagesSize, 1, 100);
        var me = user.UserId;
        var moduleCache = new Dictionary<long, Module?>();
        async Task<Module?> ModuleOf(long id)
        {
            if (!moduleCache.TryGetValue(id, out var m)) moduleCache[id] = m = await modules.GetByIdAsync(id, includeChildren: true, ct);
            return m;
        }

        long? onlyModule = null;
        if (request.ModuleKey is { } key)
        {
            if (await modules.GetByKeyAsync(key, includeChildren: false, ct) is not { } filter)
                return ServiceResult.Success(new PagedResponse<ApprovalInboxItem>([], 0, page, size));
            onlyModule = filter.Id;
        }

        List<ApprovalInstance> instances;
        List<ApprovalTask>? tasks = null;
        switch (request.Box)
        {
            case ApprovalInboxBoxes.Todo:
            case ApprovalInboxBoxes.Done:
                tasks = request.Box == ApprovalInboxBoxes.Todo ? await repo.ListOpenTasksOfAsync(me, ct) : await repo.ListActedTasksOfAsync(me, ct);
                instances = await repo.ListInstancesAsync(tasks.Select(t => t.InstanceId).Distinct().ToList(), ct);
                if (request.Box == ApprovalInboxBoxes.Todo)
                {
                    instances = instances.Where(i => i.Status == ApprovalInstanceStatus.Pending).ToList();
                    // 同一關已用別的席位同意過的任務不能動作（會 409），不列
                    var stageTasks = await repo.ListTasksAsync(instances.Select(i => i.Id).ToList(), ct);
                    tasks = tasks.Where(t => !ApprovalActionSupport.AlreadyApprovedInStage(stageTasks.Where(x => x.InstanceId == t.InstanceId), t, me)).ToList();
                }
                break;
            case ApprovalInboxBoxes.Submitted:
                instances = await repo.ListByInitiatorAsync(me, ct);
                break;
            default:
                var candidates = onlyModule is { } only ? [only] : await repo.ListInstanceModuleIdsAsync(ct);
                var scopes = new Dictionary<long, EffectivePermissions>();
                foreach (var id in candidates)
                    if (await ModuleOf(id) is { } m && m.IsReadable() && (await guard.AuthorizeAsync(m, PermissionAction.Approve, ct)).Permissions is { } p)
                        scopes[id] = p;
                if (scopes.Count == 0) return ServiceResult.Forbidden();
                instances = [];
                foreach (var group in (await repo.ListByModulesAsync(scopes.Keys.ToList(), ct)).GroupBy(i => i.ModuleId))
                {
                    // 不限範圍時也走 ListVisibleIdsAsync：排除已刪除的記錄，和限範圍一致
                    var owners = scopes[group.Key].OwnerUserIds;
                    var inScope = await records.ListVisibleIdsAsync(group.Key, group.Select(i => i.RecordId).Distinct().ToList(), owners, ct);
                    instances.AddRange(group.Where(i => inScope.Contains(i.RecordId)));
                }
                break;
        }

        instances = instances.Where(i => (onlyModule is null || i.ModuleId == onlyModule)
            && (request.From is not { } from || DateOnly.FromDateTime(i.SubmittedTime) >= from)
            && (request.To is not { } to || DateOnly.FromDateTime(i.SubmittedTime) <= to)
            && (request.Status is not { } wanted || i.Status == wanted)).ToList();
        var byId = instances.ToDictionary(i => i.Id);
        var runs = await repo.ListRunsAsync(byId.Keys.ToList(), ct);
        var runById = runs.ToDictionary(r => r.Id);
        DateTime? CurrentDue(ApprovalInstance i) => i.CurrentStageRunId is { } r && runById.TryGetValue(r, out var run) ? run.DueTime : null;

        var rows = tasks is null
            ? instances.Select(i => new Row(i, null, CurrentDue(i))).OrderByDescending(r => r.Inst.SubmittedTime).ThenByDescending(r => r.Inst.Id).ToList()
            : tasks.Where(t => byId.ContainsKey(t.InstanceId)).Select(t => new Row(byId[t.InstanceId], t,
                request.Box == ApprovalInboxBoxes.Done ? CurrentDue(byId[t.InstanceId]) : t.DueTime)).ToList();   // 已處理列的期限與逾期看單據目前關卡，不看自己那個已結束的任務
        if (request.Box == ApprovalInboxBoxes.Todo)
            rows = rows.OrderBy(r => r.Due is null).ThenBy(r => r.Due).ThenBy(r => r.Inst.SubmittedTime).ThenBy(r => r.Task!.Id).ToList();
        else if (request.Box == ApprovalInboxBoxes.Done)
            rows = rows.OrderByDescending(r => r.Task!.ActedTime).ThenByDescending(r => r.Task!.Id).ToList();

        var names = await ApprovalViews.UserNamesAsync(permissions, ct);
        var now = time.Now;
        var items = new List<ApprovalInboxItem>();
        var runsByInstance = runs.ToLookup(x => x.InstanceId);
        var scopeCache = new Dictionary<(long, PermissionAction), EffectivePermissions?>();
        // 「已處理」列要逐列判斷讀者看不看得到整張圖：發起人，或對該記錄有 Read／Approve 的資料範圍（同「全部」分頁的 ListVisibleIdsAsync）
        async Task<bool> SeesWhole(ApprovalInstance inst)
        {
            if (inst.InitiatorUserId == me) return true;
            if (await ModuleOf(inst.ModuleId) is not { } m || !m.IsReadable()) return false;
            foreach (var action in new[] { PermissionAction.Read, PermissionAction.Approve })
            {
                if (!scopeCache.TryGetValue((m.Id, action), out var p))
                    scopeCache[(m.Id, action)] = p = (await guard.AuthorizeAsync(m, action, ct)).Permissions;
                if (p is not null && (await records.ListVisibleIdsAsync(m.Id, [inst.RecordId], p.OwnerUserIds, ct)).Contains(inst.RecordId)) return true;
            }
            return false;
        }
        foreach (var r in rows.Skip((page - 1) * size).Take(size))
        {
            var module = await ModuleOf(r.Inst.ModuleId);
            var pending = r.Inst.Status == ApprovalInstanceStatus.Pending;
            // 「待簽」列取自己任務所在的關卡（U5）：sequence 的待簽任務一定在目前關卡，結果不變；graph 並行時目前關卡可能是另一條分支的節點。其餘照舊取目前關卡
            var runId = request.Box == ApprovalInboxBoxes.Todo && r.Task is { } mine ? mine.StageRunId : r.Inst.CurrentStageRunId;
            var stage = pending && runId is { } rid && runById.TryGetValue(rid, out var run) ? run.NameSnapshot : null;
            // 並行時三個分頁列出進行中的節點（fg4 U10）；「待簽」維持自己的節點（U26），「已處理」只給看得到整張圖的讀者（M2）
            var def = pending && request.Box != ApprovalInboxBoxes.Todo ? ApprovalEngine.DefinitionOf(r.Inst) : null;
            var active = def is not null && ApprovalGraphMode.Applies(r.Inst, def) ? ApprovalReader.ActiveStages(def, runsByInstance[r.Inst.Id]) : null;
            if (active is not null && request.Box == ApprovalInboxBoxes.Done && !await SeesWhole(r.Inst)) active = null;
            items.Add(new ApprovalInboxItem(r.Task?.Id ?? 0, r.Inst.Id, r.Inst.RecordId, module?.Key ?? "", module?.NameI18n ?? new I18nText(),
                r.Inst.TitleSnapshot, stage, r.Inst.InitiatorUserId, names.GetValueOrDefault(r.Inst.InitiatorUserId) ?? r.Inst.InitiatorUserId,
                r.Inst.SubmittedTime, r.Due, pending && r.Due < now, module is null || !module.IsWritable(), r.Inst.Status, active));
        }
        return ServiceResult.Success(new PagedResponse<ApprovalInboxItem>(items, rows.Count, page, size));
    }
}

/// <summary>查詢回應的組裝。</summary>
internal static class ApprovalViews
{
    /// <summary>
    /// 歷程的 Data；content-changed 只留讀者看得到的欄位，reroute 的 editedKeys 只留讀者看得到的鍵（hidden 為 null 時全部遮蔽）。
    /// </summary>
    private static JsonElement? Data(ApprovalAction a, IReadOnlySet<long>? hidden)
    {
        if (a.DataJson is null) return null;
        bool Visible(long fieldId) => hidden is not null && !hidden.Contains(fieldId);
        if (a.Action == ApprovalActions.Reroute && JsonNode.Parse(a.DataJson) is JsonObject reroute && reroute["editedKeys"] is JsonArray keys)
        {
            reroute["editedKeys"] = new JsonArray(keys.Select(k => k?.GetValue<string>()).OfType<string>()
                .Where(k => k.StartsWith("f_") && long.TryParse(k.AsSpan(2), out var fid) && Visible(fid)).Select(k => (JsonNode?)k).ToArray());
            return JsonSerializer.SerializeToElement(reroute);
        }
        using var doc = JsonDocument.Parse(a.DataJson);
        if (a.Action != ApprovalActions.ContentChanged || doc.RootElement.ValueKind != JsonValueKind.Array) return doc.RootElement.Clone();
        var visible = doc.RootElement.EnumerateArray()
            .Where(e => e.TryGetProperty("fieldId", out var id) && id.TryGetInt64(out var fid) && Visible(fid))
            .Select(e => e.Clone()).ToList();
        return JsonSerializer.SerializeToElement(visible);
    }

    public static async Task<Dictionary<string, string>> UserNamesAsync(IPermissionRepository permissions, CancellationToken ct) =>
        (await permissions.ListUsersAsync(ct)).ToDictionary(u => u.UserId, u => string.IsNullOrEmpty(u.DisplayName) ? u.UserId : u.DisplayName);

    public static ApprovalInstanceView Instance(ApprovalInstance i, IReadOnlyList<ApprovalStageRun> allRuns, IReadOnlyList<ApprovalTask> allTasks,
        IReadOnlyList<ApprovalAction> allActions, IReadOnlyDictionary<string, string> names, string me, bool writable, bool manager,
        IReadOnlySet<long>? hiddenFieldIds, IReadOnlySet<long>? unwritableFieldIds, Workflow? workflow, bool showTitle, int addSignLimit,
        IReadOnlyList<ApprovalStageRun> unfilteredRuns, IReadOnlyList<ApprovalAction> unfilteredActions,
        IReadOnlySet<long> rejectedRunIds, IReadOnlySet<long>? ownRunIds)
    {
        string Name(string id) => names.GetValueOrDefault(id) ?? id;
        var pending = i.Status == ApprovalInstanceStatus.Pending;
        var runs = allRuns.Where(r => r.InstanceId == i.Id).OrderBy(r => r.Seq).ToList();
        var tasks = allTasks.Where(t => t.InstanceId == i.Id).OrderBy(t => t.Id).ToList();
        var def = ApprovalEngine.DefinitionOf(i);
        // graph：關卡列只放簽核節點與加簽（U6）；退回上一關要讀圖，結構不合法回 false，和 handler 一致（U11）
        var graph = def is not null && ApprovalGraphMode.Applies(i, def);
        var runtime = graph ? ApprovalGraphRuntime.Build(def!, ApprovalGraphMode.SupportedKinds, out _) : null;
        if (graph) runs = runs.Where(r => r.NodeKind == ApprovalNodeKinds.Approval).ToList();
        // 可編輯欄位只給能動作的人，而且送出時的狀態不能是簽核鎖定狀態（設計 §8.5）
        var editableAllowed = workflow is not null && ApprovalRuntimeCheck.VerifyEditable(workflow, i.FromOptionId) is null;
        // 上一關與加簽筆數用未過濾的資料算：只看得到自己關卡的簽核人（沒有 Read）也要和 handler 的判斷一致
        var pathRuns = unfilteredRuns.Where(r => r.InstanceId == i.Id).ToList();
        var canAddSignMore = unfilteredActions.Count(a => a.InstanceId == i.Id && a.Action == ApprovalActions.AddSign) < addSignLimit;
        var stages = runs.Select(r =>
        {
            var views = tasks.Where(t => t.StageRunId == r.Id).Select(t =>
            {
                var canAct = pending && writable && t.Status == ApprovalTaskStatus.Open && ApprovalActionRules.IsActor(t, me)
                    && !ApprovalActionSupport.AlreadyApprovedInStage(tasks, t, me);
                return new ApprovalTaskView(t.Id, t.AssigneeUserId, Name(t.AssigneeUserId), t.OriginalUserId, t.Reason, t.Status, t.ActedTime, canAct,
                    pending && (graph ? runtime is not null && ApprovalGraphPath.Previous(pathRuns, r) is not null : ApprovalPath.Previous(pathRuns, r) is not null),
                    t.OriginalUserId != t.AssigneeUserId ? Name(t.OriginalUserId) : null, canAct && canAddSignMore);
            }).ToList();
            return new ApprovalStageView(r.Id, r.Seq, r.StageCode, r.NameSnapshot ?? new I18nText(), r.Mode, r.Status, r.SkipReason, r.DueTime, views,
                EditableKeys(def, r, views.Any(v => v.CanAct), editableAllowed, unwritableFieldIds), r.AddedByUserId is { } addedBy ? Name(addedBy) : null,
                // 排隊中的加簽關卡還沒有任務，列出加簽對象；啟動後以任務為準（a3c 審查 S2）
                r.Status == ApprovalStageStatus.Queued ? (r.AddSignUserIds ?? []).Select(Name).ToList() : null,
                graph ? r.NodeId : null, graph ? r.BranchKey : null, graph ? r.CloseReason : null);
        }).ToList();
        var actions = allActions.Where(a => a.InstanceId == i.Id).OrderBy(a => a.Id).Select(a => new ApprovalActionView(a.Action, a.ActorUserId, Name(a.ActorUserId),
            a.OnBehalfOfUserId, a.TargetUserIds ?? [], a.Comment, Data(a, hiddenFieldIds), a.CreatedTime,
            (a.TargetUserIds ?? []).Select(Name).ToList(), a.OnBehalfOfUserId is { } behalf ? Name(behalf) : null,
            // 只看得到自己關卡的讀者：看不到的那一列（例如臨時讀取權看到的 content-changed 掛在別人的關卡）不帶出它的代號
            ownRunIds is not null && a.StageRunId is { } actionRun && !ownRunIds.Contains(actionRun) ? null : a.StageRunId)).ToList();
        // 撤回資格同 WithdrawApprovalHandler：sequence 是目前關卡還沒有任何同意或退回；graph 是每個 active 的簽核節點都還沒有（不讀圖，U11）
        var current = runs.FirstOrDefault(r => r.Id == i.CurrentStageRunId) ?? runs.LastOrDefault(r => r.Status == ApprovalStageStatus.Active);
        var canWithdraw = pending && writable && i.InitiatorUserId == me
            && (graph ? ApprovalGraphPath.CanWithdraw(pathRuns, tasks)
                : !tasks.Any(t => t.StageRunId == current?.Id && t.Status is ApprovalTaskStatus.Approved or ApprovalTaskStatus.Rejected));
        var flowName = i.FlowSetSnapshot.FirstOrDefault(e => e.FlowId == i.FlowId)?.Name;
        return new ApprovalInstanceView(i.Id, i.Status, i.RouteKind, flowName, i.FromOptionId, i.ToOptionId, i.InitiatorUserId, i.SubmittedTime, i.CompletedTime,
            i.RowVersion, stages, actions, canWithdraw, pending && writable && manager, showTitle ? i.TitleSnapshot : "",
            ProgressGraph(i, def, pathRuns, rejectedRunIds, ownRunIds));
    }

    /// <summary>備援實例的進度圖定義（U8）：開始 → 備援簽核 → 結束；節點實例以 StageCode = <see cref="ApprovalEngine.FallbackStageCode"/> 對應。</summary>
    private static readonly ApprovalDefinition FallbackDefinition = new(ApprovalShapes.Graph, null, null, null, [],
        [new(StartId, ApprovalNodeKinds.Start), new(ApprovalEngine.FallbackStageCode, ApprovalNodeKinds.Approval, ApprovalMessages.FallbackStageName, Mode: ApprovalModes.Any),
         new(EndId, ApprovalNodeKinds.End)],
        [new("e1", StartId, ApprovalEngine.FallbackStageCode), new("e2", ApprovalEngine.FallbackStageCode, EndId)]);

    private const string StartId = "start", EndId = "end";

    /// <summary>
    /// 進度圖（J5、J10）：顯示用的定義先看路線類型（備援不論綁定哪種版本都是三個節點，否則 FromSequence 會把 graph 原樣回傳、畫出整張沒走的舊圖），
    /// 再看形狀。資料對不上或快照遺失回 null，時間軸照常。ownRunIds 非 null 是只看得到自己關卡的讀者：照 J7 遮蔽，判斷與時間軸用同一組 Shown。
    /// </summary>
    private static ApprovalProgressGraph? ProgressGraph(ApprovalInstance i, ApprovalDefinition? def, IReadOnlyList<ApprovalStageRun> runs,
        IReadOnlySet<long> rejectedRunIds, IReadOnlySet<long>? ownRunIds)
    {
        if (def is null) return null;
        var byStageCode = i.RouteKind == ApprovalRouteKind.Fallback || def.Shape != ApprovalShapes.Graph;
        var shown = i.RouteKind == ApprovalRouteKind.Fallback ? FallbackDefinition : ApprovalGraphConversion.FromSequence(def);
        if (ApprovalGraphProgress.Of(shown, runs, rejectedRunIds, byStageCode, i.Status == ApprovalInstanceStatus.Approved) is not { } p) return null;
        var nodes = p.Nodes.Select(n => ProgressNodeView(n, ownRunIds)).ToList();
        var edges = p.Edges.Select(e => new ApprovalProgressEdge(e.Id, e.From, e.To, e.Label, ownRunIds is null && e.Traversed)).ToList();
        if (ownRunIds is null) return new ApprovalProgressGraph(nodes, edges);

        // 加簽是執行中才產生的資料、不是流程結構：只看得到自己關卡的讀者，只留他至少看得到一列的加簽小節點；
        // 其他的拿掉，串線一起拿掉，後面的線改回從前一個還在的節點出發（不然多一個框就洩漏「那一關有人加簽」）
        var dropped = nodes.Where(n => n.Kind == ApprovalProgressKinds.AddSign && n.State == ApprovalProgressStates.Hidden).Select(n => n.Id).ToHashSet();
        var before = edges.Where(e => dropped.Contains(e.To)).ToDictionary(e => e.To, e => e.From);
        string Resolve(string from) { while (dropped.Contains(from)) from = before[from]; return from; }

        // 留下的小節點在每個主節點上重新從 1 編號：原本的序號會透露同一主節點上還有別人的加簽
        var smallIds = nodes.Where(n => n.Kind == ApprovalProgressKinds.AddSign).Select(n => n.Id).ToHashSet();
        var taken = nodes.Where(n => !smallIds.Contains(n.Id)).Select(n => n.Id).Concat(edges.Where(e => !smallIds.Contains(e.To)).Select(e => e.Id)).ToHashSet();
        var renamed = new Dictionary<string, string>();
        foreach (var group in nodes.Where(n => n.Kind == ApprovalProgressKinds.AddSign && !dropped.Contains(n.Id)).GroupBy(n => n.HostId))
        {
            var ordinal = 1;
            foreach (var n in group)
            {
                while (taken.Contains($"as-{group.Key}-{ordinal}") || taken.Contains($"as-{group.Key}-{ordinal}-in")) ordinal++;
                renamed[n.Id] = $"as-{group.Key}-{ordinal++}";
            }
        }
        string Rename(string id) => renamed.GetValueOrDefault(id, id);
        return new ApprovalProgressGraph(
            [.. nodes.Where(n => !dropped.Contains(n.Id)).Select(n => n with { Id = Rename(n.Id) })],
            [.. edges.Where(e => !dropped.Contains(e.To)).Select(e =>
                e with { Id = renamed.TryGetValue(e.To, out var to) && e.Id.EndsWith("-in", StringComparison.Ordinal) ? $"{to}-in" : e.Id, From = Rename(Resolve(e.From)), To = Rename(e.To) })]);
    }

    /// <summary>
    /// J7、U1：簽核與加簽節點最新一列讀者看得到 → 照實際狀態；只看得到較早的幾列 → changed（已處理，之後有變動）；一列都看不到 → hidden；
    /// 開始、條件、分支、匯合、結束一律 hidden。runIds 只留看得到的（對得上 stages）。名稱、型別、匯合方式、掛點是結構，照留。
    /// </summary>
    private static ApprovalProgressNode ProgressNodeView(ProgressNode n, IReadOnlySet<long>? own)
    {
        var hasRuns = n.Kind is ApprovalNodeKinds.Approval or ApprovalProgressKinds.AddSign;
        if (own is null) return new ApprovalProgressNode(n.Id, n.Kind, n.Name, n.State, hasRuns ? n.RunIds : null, n.JoinMode, n.HostId);
        var seen = hasRuns ? n.RunIds.Where(own.Contains).ToList() : [];
        var state = seen.Count == 0 ? ApprovalProgressStates.Hidden : own.Contains(n.RunIds[^1]) ? n.State : ApprovalProgressStates.Changed;
        return new ApprovalProgressNode(n.Id, n.Kind, n.Name, state, seen.Count == 0 ? null : seen, n.JoinMode, n.HostId);
    }

    /// <summary>
    /// 這一關讀者在同意時能修改的欄位 key（f_{id}）：關卡進行中、讀者在這一關能動作、送出時的狀態不是鎖定狀態，
    /// 並扣掉讀者不能寫的欄位（隱藏、欄位例外唯讀、應用程式關閉而凍結；unwritable 為 null＝讀不到權限，全部扣掉）。
    /// </summary>
    public static IReadOnlyList<string> EditableKeys(ApprovalDefinition? def, ApprovalStageRun run, bool canAct, bool editableAllowed,
        IReadOnlySet<long>? unwritable)
    {
        if (def is null || unwritable is null || !canAct || !editableAllowed || run.Status != ApprovalStageStatus.Active) return [];
        var stage = ApprovalDefinitionRefs.Steps(def).FirstOrDefault(s => s.Code == run.StageCode);
        return (stage?.EditableFields ?? []).Where(id => !unwritable.Contains(id)).Select(id => $"f_{id}").ToList();
    }
}
