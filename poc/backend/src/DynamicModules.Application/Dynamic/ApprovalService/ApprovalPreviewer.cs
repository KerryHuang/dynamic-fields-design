using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.Rules;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.ApprovalService;

/// <summary>送出的目標：記錄、模組、狀態機、目前狀態、轉移的簽核設定與記錄值。</summary>
public sealed record SubmitTarget(Record Record, Module Module, Workflow Workflow, long From, TransitionApproval Approval,
    Dictionary<string, JsonElement?> Values);

/// <summary>送出資格（設計 §5.2 第 1 點）：送出與預覽共用，預覽的資格就是送出的資格。</summary>
public sealed class ApprovalSubmitEligibility(
    IModuleRepository modules, IRecordRepository records, IDynamicAccessGuard guard, IPermissionRepository permissions, ICurrentUser user,
    WorkflowService.TransitionAccess access)
{
    /// <summary>記錄存在、是表頭、模組可寫、有 Edit 且在資料範圍內。</summary>
    public async Task<(Record? Record, Module? Module, ServiceResult? Failure)> LoadAsync(long recordId, CancellationToken ct)
    {
        var record = await records.GetAsync(recordId, ct);
        if (record is null) return (null, null, ServiceResult.NotFound(RecordMapper.RecordNotFound));
        var module = await modules.GetByIdAsync(record.ModuleId, includeChildren: true, ct);
        if (module is null || ModuleRelations.IsDetail(module)) return (null, null, ServiceResult.NotFound(RecordMapper.RecordNotFound));
        if (await ModuleGate.RejectUnwritableAsync(module, guard, ct) is { } blocked) return (null, null, blocked);
        var access = await guard.AuthorizeAsync(module, PermissionAction.Edit, ct);
        if (access.Permissions is not { } perms) return (null, null, access.Denied!);
        if (perms.OwnerUserIds is not null && !perms.OwnerUserIds.Contains(record.OwnerUserId))
            return (null, null, ServiceResult.NotFound(RecordMapper.RecordNotFound));
        return (record, module, null);
    }

    /// <summary>從目前狀態到 to 是已定義、需簽核的轉移，而且使用者在轉移的群組內（或為系統管理員）。</summary>
    public async Task<(SubmitTarget? Target, ServiceResult? Failure)> TransitionAsync(Record record, Module module, long to, CancellationToken ct)
    {
        if (module.Workflow is not { } wf) return (null, ServiceResult.Invalid("to", ApprovalMessages.NotApprovalTransition));
        ServiceResult Bad(MessageRef message) => ServiceResult.Invalid(new Dictionary<string, MessageRef[]> { [wf.StatusFieldKey] = [message] });
        var values = RecordValueProcessor.Parse(record.ValuesJson);
        var from = wf.StateOf(values) ?? wf.InitialOptionId;
        if (wf.Find(from, to) is not { } transition) return (null, Bad(MessageRef.Of(MessageText.WorkflowNotAllowed)));
        if (transition.Approval is not { Required: true } approval) return (null, Bad(ApprovalMessages.NotApprovalTransition));
        if (transition.GroupIds.Count > 0)
        {
            var inputs = await permissions.LoadInputsAsync(user.UserId, module, ct);
            if (inputs is null || !(await access.BypassGroupsAsync(inputs, ct) || inputs.GroupIds.Any(transition.GroupIds.Contains)))
                return (null, Bad(MessageRef.Of(MessageText.WorkflowNotPermitted)));
        }
        return (new SubmitTarget(record, module, wf, from, approval, values), null);
    }
}

/// <summary>
/// 送出預覽與設計器試算（設計 §5.4）：和送出相同的比對與解析，不寫入。
/// 記錄預覽：條件引用對發起人隱藏的欄位 → conditional、不列簽核人；條件不成立的關卡不列（不說會跳過、也不說理由）。
/// 設計器試算：所有關卡都列，跳過的標 skip，用了備援的標 UsesFallback。
/// </summary>
public sealed class ApprovalPreviewer(
    IApprovalFlowRepository flows, IModuleRepository modules, ApproverResolver resolver, IPermissionRepository permissions,
    FieldTypeRegistry registry, IBusinessTime time, IDynamicAccessGuard guard, ApprovalHiddenFields hiddenFields)
{
    public async Task<ServiceResult> PreviewAsync(Module module, long to, TransitionApproval approval,
        IReadOnlyDictionary<string, JsonElement?> values, string initiator, bool designer, CancellationToken ct)
    {
        var snapshot = (await flows.ListCurrentByModuleAsync(module.Id, ct)).Where(f => f.Flow.ToOptionId == to)
            .Select(f => new FlowSnapshotEntry(f.Flow.Id, f.Flow.CurrentVersionId!.Value, f.Flow.Priority, f.Flow.Active, f.Flow.NameI18n, f.Definition))
            .ToList();
        var userIds = await modules.ListUserIdsAsync(ct);
        var (match, undecidable) = ApprovalRouter.Match(snapshot, module, values, registry, time.Today, userIds);
        if (undecidable is not null) return ServiceResult.Conflict(ApprovalMessages.FlowConditionUndecidable(undecidable.Name));
        if (match is null)
            return ServiceResult.Success(new ApprovalPreviewResponse(
                approval.NoMatch == ApprovalNoMatch.Direct ? ApprovalPreviewOutcomes.Direct : ApprovalPreviewOutcomes.Blocked, null, [], []));

        var users = await permissions.ListUsersAsync(ct);
        var names = users.ToDictionary(u => u.UserId, u => string.IsNullOrEmpty(u.DisplayName) ? u.UserId : u.DisplayName);
        UserRef Ref(string id) => new(id, names.GetValueOrDefault(id) ?? id);
        // 記錄預覽的發起人就是目前使用者：有 Read 用 guard 的隱藏集合（含明細來源隱藏），沒有就自己算；
        // 讀不到權限輸入時所有條件都當成看不到（fail closed）
        IReadOnlySet<long>? hidden = null;
        if (!designer)
            hidden = (await guard.AuthorizeAsync(module, PermissionAction.Read, ct)).Permissions?.HiddenFieldIds
                ?? await hiddenFields.ForUserAsync(module, initiator, ct);

        var ctx = await resolver.ContextAsync(module, initiator, values, null, ct);
        var def = match.Definition;
        var stages = new List<PreviewStage>();
        var picks = new List<PickRequirement>();
        void AddPick(ApprovalStageDef s)
        {
            if (picks.Any(p => p.StageCode == s.Code)) return;
            var allowed = ApproverResolver.Candidates(ctx, s.Approvers);
            var candidates = ctx.Org.ActiveUsers.Where(u => (ctx.AllowSelfApproval || u != initiator) && (allowed is null || allowed.Contains(u)))
                .Order(StringComparer.Ordinal).Select(Ref).ToList();
            picks.Add(new PickRequirement(s.Code, s.Name, s.Approvers.Min ?? 1, s.Approvers.Max ?? ctx.MaxApprovers, candidates));
        }
        foreach (var s in def.Stages)
        {
            // 送出驗證（PickErrors）對所有自選關卡都要求人選，不論條件是否成立、是否引用隱藏欄位：預覽一律列出人選要求（只說要選人、不說會不會進入）
            if (!designer && s.Approvers.Source == ApprovalSources.InitiatorPick && !s.Approvers.CandidatesUnmatched) AddPick(s);
            // 條件引用隱藏欄位，或簽核人取自隱藏的使用者欄位 → conditional、不列簽核人
            if (!designer && (s.When is { ValueKind: JsonValueKind.Object } w && (hidden is null || RuleReferences.ConditionFieldIdsOf(w).Any(hidden.Contains))
                || s.Approvers is { Source: ApprovalSources.UserField, FieldId: var fid } && (hidden is null || fid is { } f && hidden.Contains(f))))
            {
                stages.Add(new PreviewStage(s.Code, s.Name, PreviewStatuses.Conditional, [], false));
                continue;
            }
            if (!ApprovalRouter.Holds(s.When, module, values, registry, time.Today, userIds))
            {
                if (designer) stages.Add(new PreviewStage(s.Code, s.Name, PreviewStatuses.Skip, [], false));
                continue;
            }
            if (s.Approvers.Source == ApprovalSources.InitiatorPick && !s.Approvers.CandidatesUnmatched)
            {
                AddPick(s);
                stages.Add(new PreviewStage(s.Code, s.Name, PreviewStatuses.Pick, [], false));
                continue;
            }
            var resolution = ApproverResolver.Resolve(ctx, s.Name, s.Mode, s.Approvers, def.FallbackApprovers, s.OnEmpty);
            if (resolution.AllUnitsEmpty)
            {
                if (designer) stages.Add(new PreviewStage(s.Code, s.Name, PreviewStatuses.Skip, [], false));
                continue;
            }
            // 解析失敗（找不到人、超過上限）送出時會回 409；預覽照列這一關、不列簽核人
            var approvers = resolution.Seats.SelectMany(x => x.UserIds).Distinct(StringComparer.Ordinal).Select(Ref).ToList();
            stages.Add(new PreviewStage(s.Code, s.Name, PreviewStatuses.Enter, approvers,
                resolution.Seats.Any(x => x.Reason == ApprovalTaskReason.Fallback)));
        }
        return ServiceResult.Success(new ApprovalPreviewResponse(ApprovalPreviewOutcomes.Flow, match.Name, stages, picks));
    }
}
