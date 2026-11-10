using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using MediatR;

namespace DynamicModules.Application.Dynamic.ApprovalService;

/// <summary>記錄的簽核摘要與「現在能送簽的目標」；GET records/{id} 與 GET records/{id}/approval 共用。</summary>
public sealed class ApprovalReader(IApprovalInstanceRepository repo, IDynamicAccessGuard guard, IPermissionRepository permissions, ICurrentUser user)
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
                if (inputs is null || !(inputs.User.IsSystemAdmin || inputs.GroupIds.Any(t.GroupIds.Contains))) continue;
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
    IPermissionRepository permissions, ApprovalReader reader, ICurrentUser user)
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
        var visible = canRead || manager
            ? instances
            : instances.Where(i => i.InitiatorUserId == me || tasks.Any(t => t.InstanceId == i.Id && (t.AssigneeUserId == me || t.OriginalUserId == me)))
                .ToList();
        if (!canRead && !manager && visible.Count == 0) return ServiceResult.NotFound(RecordMapper.RecordNotFound);

        var visibleIds = visible.Select(i => i.Id).ToList();
        var runs = await repo.ListRunsAsync(visibleIds, ct);
        var actions = await repo.ListActionsAsync(visibleIds, ct);
        var names = await ApprovalViews.UserNamesAsync(permissions, ct);
        var writable = module.IsWritable();
        var views = visible.Select(i => ApprovalViews.Instance(i, runs, tasks, actions, names, me, writable, manager)).ToList();
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
            return ServiceResult.Invalid(new Dictionary<string, string[]> { ["box"] = ["分頁只能是 todo、done、submitted、all"] });
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
                if (request.Box == ApprovalInboxBoxes.Todo) instances = instances.Where(i => i.Status == ApprovalInstanceStatus.Pending).ToList();
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
                    var owners = scopes[group.Key].OwnerUserIds;
                    if (owners is null) { instances.AddRange(group); continue; }
                    var inScope = await records.ListVisibleIdsAsync(group.Key, group.Select(i => i.RecordId).Distinct().ToList(), owners, ct);
                    instances.AddRange(group.Where(i => inScope.Contains(i.RecordId)));
                }
                break;
        }

        instances = instances.Where(i => (onlyModule is null || i.ModuleId == onlyModule)
            && (request.From is not { } from || DateOnly.FromDateTime(i.SubmittedTime) >= from)
            && (request.To is not { } to || DateOnly.FromDateTime(i.SubmittedTime) <= to)).ToList();
        var byId = instances.ToDictionary(i => i.Id);
        var runs = await repo.ListRunsAsync(byId.Keys.ToList(), ct);
        var runById = runs.ToDictionary(r => r.Id);
        DateTime? CurrentDue(ApprovalInstance i) => i.CurrentStageRunId is { } r && runById.TryGetValue(r, out var run) ? run.DueTime : null;

        var rows = tasks is null
            ? instances.Select(i => new Row(i, null, CurrentDue(i))).OrderByDescending(r => r.Inst.SubmittedTime).ThenByDescending(r => r.Inst.Id).ToList()
            : tasks.Where(t => byId.ContainsKey(t.InstanceId)).Select(t => new Row(byId[t.InstanceId], t, t.DueTime)).ToList();
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
    public static async Task<Dictionary<string, string>> UserNamesAsync(IPermissionRepository permissions, CancellationToken ct) =>
        (await permissions.ListUsersAsync(ct)).ToDictionary(u => u.UserId, u => string.IsNullOrEmpty(u.DisplayName) ? u.UserId : u.DisplayName);

    public static ApprovalInstanceView Instance(ApprovalInstance i, IReadOnlyList<ApprovalStageRun> allRuns, IReadOnlyList<ApprovalTask> allTasks,
        IReadOnlyList<ApprovalAction> allActions, IReadOnlyDictionary<string, string> names, string me, bool writable, bool manager)
    {
        string Name(string id) => names.GetValueOrDefault(id) ?? id;
        var pending = i.Status == ApprovalInstanceStatus.Pending;
        var runs = allRuns.Where(r => r.InstanceId == i.Id).OrderBy(r => r.Seq).ToList();
        var tasks = allTasks.Where(t => t.InstanceId == i.Id).OrderBy(t => t.Id).ToList();
        var stages = runs.Select(r => new ApprovalStageView(r.Id, r.Seq, r.StageCode, r.NameSnapshot ?? new I18nText(), r.Mode, r.Status, r.SkipReason, r.DueTime,
            tasks.Where(t => t.StageRunId == r.Id).Select(t => new ApprovalTaskView(t.Id, t.AssigneeUserId, Name(t.AssigneeUserId), t.OriginalUserId, t.Reason,
                t.Status, t.ActedTime,
                pending && writable && t.Status == ApprovalTaskStatus.Open && ApprovalActionRules.IsActor(t, me)
                && !ApprovalActionSupport.AlreadyApprovedInStage(tasks, t, me))).ToList())).ToList();
        var actions = allActions.Where(a => a.InstanceId == i.Id).OrderBy(a => a.Id).Select(a => new ApprovalActionView(a.Action, a.ActorUserId, Name(a.ActorUserId),
            a.OnBehalfOfUserId, a.TargetUserIds ?? [], a.Comment, a.DataJson is null ? null : JsonDocument.Parse(a.DataJson).RootElement.Clone(),
            a.CreatedTime)).ToList();
        // 撤回資格同 WithdrawApprovalHandler：目前關卡還沒有任何同意或退回
        var current = runs.FirstOrDefault(r => r.Id == i.CurrentStageRunId) ?? runs.LastOrDefault(r => r.Status == ApprovalStageStatus.Active);
        var canWithdraw = pending && writable && i.InitiatorUserId == me
            && !tasks.Any(t => t.StageRunId == current?.Id && t.Status is ApprovalTaskStatus.Approved or ApprovalTaskStatus.Rejected);
        var flowName = i.FlowSetSnapshot.FirstOrDefault(e => e.FlowId == i.FlowId)?.Name;
        return new ApprovalInstanceView(i.Id, i.Status, i.RouteKind, flowName, i.FromOptionId, i.ToOptionId, i.InitiatorUserId, i.SubmittedTime, i.CompletedTime,
            i.RowVersion, stages, actions, canWithdraw, pending && writable && manager);
    }
}
