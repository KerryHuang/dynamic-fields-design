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
        var run = pending?.CurrentStageRunId is { } runId ? (await repo.ListRunsAsync([pending.Id], ct)).FirstOrDefault(r => r.Id == runId) : null;
        return new RecordApprovalSummary(latest?.Status ?? NoInstance, run?.NameSnapshot, run?.Seq, targets.Count > 0);
    }

    public static bool InScope(EffectivePermissions p, string ownerUserId) => p.OwnerUserIds is null || p.OwnerUserIds.Contains(ownerUserId);
}

/// <summary>
/// 記錄的簽核狀態與歷程（設計 §10.2）：對記錄有 Read（含資料範圍）或簽核管理權（Approve 軸）→ 全部實例；
/// 否則只有自己送出的、或自己有任務的實例（只給歷程，不給記錄內容，也不給送簽目標）；一個都沒有 → 404。
/// </summary>
public class GetRecordApprovalHandler(
    IRecordRepository records, IModuleRepository modules, IApprovalInstanceRepository repo, IDynamicAccessGuard guard,
    IPermissionRepository permissions, ApprovalReader reader, ApprovalReadGrant grant, ICurrentUser user)
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
        var approve = readable ? (await guard.AuthorizeAsync(module, PermissionAction.Approve, ct)).Permissions : null;
        var canRead = read is not null && ApprovalReader.InScope(read, record.OwnerUserId);
        var manager = approve is not null && ApprovalReader.InScope(approve, record.OwnerUserId);

        var instances = await repo.ListByRecordAsync(record.Id, ct);
        var ids = instances.Select(i => i.Id).ToList();
        var tasks = await repo.ListTasksAsync(ids, ct);
        // 沒有 Read／Approve 的任務負責人只看自己處理過的關卡（設計 §10.2）：open 或動作過的任務所在關卡；
        // 從沒動作就被關閉的任務不算。發起人看整個實例。
        // 標題快照的遮蔽（同記錄頁的 TitleVisibleTo）：發起人、在該實例有任務的人、簽核管理者一律看得到；
        // 其他人（例如有 Read、但標題欄位對他隱藏的旁觀者）要標題對他可見才給
        var titleVisible = read is not null && module.TitleVisibleTo(read);
        bool ShowTitle(ApprovalInstance i) => titleVisible || manager || i.InitiatorUserId == me
            || tasks.Any(t => t.InstanceId == i.Id && (t.AssigneeUserId == me || t.OriginalUserId == me));
        var ownStages = new Dictionary<long, HashSet<long>>();
        if (!canRead && !manager)
            foreach (var i in instances.Where(i => i.InitiatorUserId != me))
                ownStages[i.Id] = tasks.Where(t => t.InstanceId == i.Id && (t.AssigneeUserId == me || t.OriginalUserId == me)
                    && (t.Status == ApprovalTaskStatus.Open || t.ActedTime is not null)).Select(t => t.StageRunId).ToHashSet();
        var visible = instances.Where(i => !ownStages.TryGetValue(i.Id, out var own) || own.Count > 0).ToList();
        if (!canRead && !manager && visible.Count == 0) return ServiceResult.NotFound(RecordMapper.RecordNotFound);
        bool Shown(long instanceId, long? stageRunId) =>
            !ownStages.TryGetValue(instanceId, out var own) || stageRunId is { } run && own.Contains(run);

        // 沒有 Read 時，持臨時讀取權（open 任務的可動作者）的人另外看得到進行中實例的 content-changed（設計 §5.5 第 7 點：金額為什麼變了）
        var granted = read is null ? await grant.ForRecordAsync(module, record, ct) : null;
        var pendingId = instances.FirstOrDefault(i => i.Status == ApprovalInstanceStatus.Pending)?.Id;
        var visibleIds = visible.Select(i => i.Id).ToList();
        var runs = (await repo.ListRunsAsync(visibleIds, ct)).Where(r => Shown(r.InstanceId, r.Id)).ToList();
        var actions = (await repo.ListActionsAsync(visibleIds, ct)).Where(a => Shown(a.InstanceId, a.StageRunId)
            || granted is not null && a.InstanceId == pendingId && a.Action == ApprovalActions.ContentChanged).ToList();
        tasks = tasks.Where(t => Shown(t.InstanceId, t.StageRunId)).ToList();
        var names = await ApprovalViews.UserNamesAsync(permissions, ct);
        var writable = module.IsWritable();
        // 歷程的欄位遮蔽（設計 §8.5）：有 Read 用它；沒有時用臨時讀取權，再沒有就用自己的群組欄位例外（含明細來源隱藏）；
        // 讀不到權限輸入是 null（全部遮蔽）
        var hidden = await grant.HiddenFieldIdsAsync(module, read, granted, ct);
        var unwritable = await grant.UnwritableFieldIdsAsync(module, read, granted, ct);
        var views = visible.Select(i => ApprovalViews.Instance(i, runs, tasks, actions, names, me, writable, manager, hidden, unwritable, module.Workflow, ShowTitle(i))).ToList();
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
        foreach (var r in rows.Skip((page - 1) * size).Take(size))
        {
            var module = await ModuleOf(r.Inst.ModuleId);
            var pending = r.Inst.Status == ApprovalInstanceStatus.Pending;
            var stage = pending && r.Inst.CurrentStageRunId is { } runId && runById.TryGetValue(runId, out var run) ? run.NameSnapshot : null;
            items.Add(new ApprovalInboxItem(r.Task?.Id ?? 0, r.Inst.Id, r.Inst.RecordId, module?.Key ?? "", module?.NameI18n ?? new I18nText(),
                r.Inst.TitleSnapshot, stage, r.Inst.InitiatorUserId, names.GetValueOrDefault(r.Inst.InitiatorUserId) ?? r.Inst.InitiatorUserId,
                r.Inst.SubmittedTime, r.Due, pending && r.Due < now, module is null || !module.IsWritable(), r.Inst.Status));
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
        IReadOnlySet<long>? hiddenFieldIds, IReadOnlySet<long>? unwritableFieldIds, Workflow? workflow, bool showTitle)
    {
        string Name(string id) => names.GetValueOrDefault(id) ?? id;
        var pending = i.Status == ApprovalInstanceStatus.Pending;
        var runs = allRuns.Where(r => r.InstanceId == i.Id).OrderBy(r => r.Seq).ToList();
        var tasks = allTasks.Where(t => t.InstanceId == i.Id).OrderBy(t => t.Id).ToList();
        var def = ApprovalEngine.DefinitionOf(i);
        // 可編輯欄位只給能動作的人，而且送出時的狀態不能是簽核鎖定狀態（設計 §8.5）
        var editableAllowed = workflow is not null && ApprovalRuntimeCheck.VerifyEditable(workflow, i.FromOptionId) is null;
        var stages = runs.Select(r =>
        {
            var views = tasks.Where(t => t.StageRunId == r.Id).Select(t => new ApprovalTaskView(t.Id, t.AssigneeUserId, Name(t.AssigneeUserId), t.OriginalUserId,
                t.Reason, t.Status, t.ActedTime,
                pending && writable && t.Status == ApprovalTaskStatus.Open && ApprovalActionRules.IsActor(t, me)
                && !ApprovalActionSupport.AlreadyApprovedInStage(tasks, t, me),
                pending && ApprovalActionRules.PreviousRun(def, runs, r) is not null)).ToList();
            return new ApprovalStageView(r.Id, r.Seq, r.StageCode, r.NameSnapshot ?? new I18nText(), r.Mode, r.Status, r.SkipReason, r.DueTime, views,
                EditableKeys(def, r, views.Any(v => v.CanAct), editableAllowed, unwritableFieldIds));
        }).ToList();
        var actions = allActions.Where(a => a.InstanceId == i.Id).OrderBy(a => a.Id).Select(a => new ApprovalActionView(a.Action, a.ActorUserId, Name(a.ActorUserId),
            a.OnBehalfOfUserId, a.TargetUserIds ?? [], a.Comment, Data(a, hiddenFieldIds), a.CreatedTime)).ToList();
        // 撤回資格同 WithdrawApprovalHandler：目前關卡還沒有任何同意或退回
        var current = runs.FirstOrDefault(r => r.Id == i.CurrentStageRunId) ?? runs.LastOrDefault(r => r.Status == ApprovalStageStatus.Active);
        var canWithdraw = pending && writable && i.InitiatorUserId == me
            && !tasks.Any(t => t.StageRunId == current?.Id && t.Status is ApprovalTaskStatus.Approved or ApprovalTaskStatus.Rejected);
        var flowName = i.FlowSetSnapshot.FirstOrDefault(e => e.FlowId == i.FlowId)?.Name;
        return new ApprovalInstanceView(i.Id, i.Status, i.RouteKind, flowName, i.FromOptionId, i.ToOptionId, i.InitiatorUserId, i.SubmittedTime, i.CompletedTime,
            i.RowVersion, stages, actions, canWithdraw, pending && writable && manager, showTitle ? i.TitleSnapshot : "");
    }

    /// <summary>
    /// 這一關讀者在同意時能修改的欄位 key（f_{id}）：關卡進行中、讀者在這一關能動作、送出時的狀態不是鎖定狀態，
    /// 並扣掉讀者不能寫的欄位（隱藏、欄位例外唯讀、應用程式關閉而凍結；unwritable 為 null＝讀不到權限，全部扣掉）。
    /// </summary>
    public static IReadOnlyList<string> EditableKeys(ApprovalDefinition? def, ApprovalStageRun run, bool canAct, bool editableAllowed,
        IReadOnlySet<long>? unwritable)
    {
        if (def is null || unwritable is null || !canAct || !editableAllowed || run.Status != ApprovalStageStatus.Active) return [];
        var stage = def.Stages.FirstOrDefault(s => s.Code == run.StageCode);
        return (stage?.EditableFields ?? []).Where(id => !unwritable.Contains(id)).Select(id => $"f_{id}").ToList();
    }
}
