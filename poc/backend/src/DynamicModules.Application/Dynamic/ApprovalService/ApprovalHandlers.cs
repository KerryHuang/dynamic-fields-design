using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.WorkflowService;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Application.Dynamic.ApprovalService;

/// <summary>送出簽核（設計 §5.2）：資格同原本執行這條轉移；比對流程、驗自選與使用者欄位、建實例並啟動第一關。</summary>
public class SubmitApprovalHandler(
    IModuleRepository modules, ApprovalSubmitEligibility eligibility, ICurrentUser user,
    IBusinessTime time, IOperationContext op, IApprovalFlowRepository flows, IApprovalInstanceRepository instances, ApprovalEngine engine,
    ApprovalTransitionRunner runner, ApproverResolver resolver, ApprovalWriteContext context, FieldTypeRegistry registry, IUnitOfWork uow)
    : IRequestHandler<SubmitApprovalRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(SubmitApprovalRequest request, CancellationToken ct)
    {
        var (record, module, denied) = await eligibility.LoadAsync(request.RecordId, ct);
        if (record is null || module is null) return denied!;
        // 先查「已在簽核中」再比對版本：並發送出的第二個請求讀到的是第一個換過的版本（設計 §8.4）
        if (await instances.GetPendingByRecordAsync(record.Id, ct) is not null) return ServiceResult.Conflict(ApprovalMessages.AlreadyPending);
        if (record.RowVersion != request.RecordRowVersion) return ServiceResult.Stale();
        if (ApprovalActionRules.CommentError(request.Comment, required: false) is { } commentError)
            return ServiceResult.Invalid("comment", commentError);

        var (target, invalid) = await eligibility.TransitionAsync(record, module, request.To, ct);
        if (invalid is not null) return invalid;
        var (_, _, wf, from, approval, values) = target!;
        Dictionary<string, MessageRef[]> Bad(MessageRef message) => new() { [wf.StatusFieldKey] = [message] };

        var snapshot = (await flows.ListCurrentByModuleAsync(module.Id, ct)).Where(f => f.Flow.ToOptionId == request.To)
            .Select(f => new FlowSnapshotEntry(f.Flow.Id, f.Flow.CurrentVersionId!.Value, f.Flow.Priority, f.Flow.Active, f.Flow.NameI18n, f.Definition))
            .ToList();
        var userIds = await modules.ListUserIdsAsync(ct);
        var (match, undecidable) = ApprovalRouter.Match(snapshot, module, values, registry, time.Today, userIds);
        if (undecidable is not null) return ServiceResult.Conflict(ApprovalMessages.FlowConditionUndecidable(undecidable.Name));
        if (match is null)
        {
            if (approval.NoMatch != ApprovalNoMatch.Direct) return ServiceResult.Invalid(Bad(ApprovalMessages.NoMatchingFlow));
            var direct = await runner.RunAsync(new ApprovalInstance { RecordId = record.Id, ModuleId = module.Id }, request.To, null, ct);
            return direct.IsSuccess ? ServiceResult.Success(new SubmitApprovalResponse(ApprovalOutcomes.Direct, null, record.RowVersion)) : direct;
        }

        var picks = request.Picks ?? [];
        var ctx = await resolver.ContextAsync(module, user.UserId, values, null, ct);
        if (PickErrors(match.Definition, picks, ctx) is { Count: > 0 } pickErrors) return ServiceResult.Invalid(pickErrors);
        if (UserFieldErrors(match.Definition, module, values, ctx) is { Count: > 0 } fieldErrors) return ServiceResult.Invalid(fieldErrors);

        var inst = new ApprovalInstance
        {
            RecordId = record.Id, ModuleId = module.Id, FlowId = match.FlowId, FlowVersionId = match.FlowVersionId, FlowSetSnapshot = snapshot,
            RouteKind = ApprovalRouteKind.Flow, FromOptionId = from, ToOptionId = request.To, InitiatorUserId = user.UserId,
            Status = ApprovalInstanceStatus.Pending, TitleSnapshot = Truncate(record.Title, 200),
            Picks = picks.ToDictionary(kv => kv.Key, kv => kv.Value.ToList()), SubmittedTime = time.Now, RowVersion = Guid.NewGuid().ToString(),
            OperationId = op.OperationId
        };
        instances.Add(inst);
        record.RowVersion = Guid.NewGuid().ToString();   // 送出鎖住記錄：舊畫面的存檔會收到 409（設計 §8.4）
        context.Touch(record.Id);
        try
        {
            await uow.CommitAsync(ct);
        }
        catch (DbUpdateException)
        {
            return ServiceResult.Conflict(ApprovalMessages.AlreadyPending);   // 部分唯一索引：同一筆記錄同時只能有一個 pending 實例
        }
        instances.AddSnapshotRows(snapshot.Select(e => new ApprovalInstanceFlow { InstanceId = inst.Id, FlowId = e.FlowId, FlowVersionId = e.FlowVersionId }));
        engine.Log(inst, ApprovalActions.Submit, user.UserId, comment: string.IsNullOrWhiteSpace(request.Comment) ? null : request.Comment);

        var (run, failure) = await engine.ActivateNextAsync(inst, module, values, ct);
        if (failure is not null) return failure;
        var outcome = ApprovalOutcomes.Submitted;
        if (run is null)
        {
            if (await engine.CompleteAsync(inst, ct) is { } completeFailure) return completeFailure;
            outcome = ApprovalOutcomes.Approved;
        }
        await uow.CommitAsync(ct);
        return ServiceResult.Created(new SubmitApprovalResponse(outcome, inst.Id, record.RowVersion));
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    /// <summary>發起人自選（設計 §6.1、Review Focus 3）：只能帶 initiatorPick 關卡的代號；不重複、候選群組內、啟用中、不是發起人；人數在 min..max。
    /// 候選群組對不上的關卡不收自選（與 ApproverResolver 一致）。</summary>
    private static Dictionary<string, MessageRef[]> PickErrors(ApprovalDefinition def, Dictionary<string, List<string>> picks, ResolveContext ctx)
    {
        var errors = new Dictionary<string, MessageRef[]>();
        var pickStages = def.Stages.Where(s => s.Approvers.Source == ApprovalSources.InitiatorPick).ToDictionary(s => s.Code);
        foreach (var code in picks.Keys.Where(k => !pickStages.ContainsKey(k))) errors[$"picks.{code}"] = [ApprovalMessages.PickInvalid];
        foreach (var (code, stage) in pickStages)
        {
            var src = stage.Approvers;
            var list = picks.GetValueOrDefault(code) ?? [];
            // 候選群組對不上（開啟套件時略過）：解析一律當空單位交給備援，自選不會生效 → 不要求自選，帶了就回 400 說明
            if (src.CandidatesUnmatched)
            {
                if (list.Count > 0) errors[$"picks.{code}"] = [ApprovalMessages.PickCandidatesUnmatched];
                continue;
            }
            var candidates = ApproverResolver.Candidates(ctx, src);
            if (list.Distinct(StringComparer.Ordinal).Count() != list.Count
                || list.Any(u => u == ctx.InitiatorUserId || !ctx.Org.ActiveUsers.Contains(u) || (candidates is not null && !candidates.Contains(u))))
                errors[$"picks.{code}"] = [ApprovalMessages.PickInvalid];
            else if (list.Count < (src.Min ?? 1) || list.Count > (src.Max ?? ApprovalLimits.MaxApprovers))
                errors[$"picks.{code}"] = [ApprovalMessages.PickCount(src.Min ?? 1, src.Max ?? ApprovalLimits.MaxApprovers)];
        }
        return errors;
    }

    /// <summary>使用者欄位來源（設計 §6.1）：欄位填的人不在候選群組、或是發起人自己 → 400（鍵為該欄位）。空值交給關卡啟動時的備援。</summary>
    private static Dictionary<string, MessageRef[]> UserFieldErrors(ApprovalDefinition def, Module module, IReadOnlyDictionary<string, JsonElement?> values,
        ResolveContext ctx)
    {
        var errors = new Dictionary<string, MessageRef[]>();
        foreach (var stage in def.Stages.Where(s => s.Approvers.Source == ApprovalSources.UserField))
        {
            var field = module.Blocks.SelectMany(b => b.Fields).FirstOrDefault(f => f.Id == stage.Approvers.FieldId);
            if (field is null || !values.TryGetValue(field.Key, out var v) || v is not { ValueKind: JsonValueKind.String } s || s.GetString() is not { } who)
                continue;
            if (who == ctx.InitiatorUserId) errors.TryAdd(field.Key, [ApprovalMessages.UserFieldIsInitiator]);
            else if (ApproverResolver.Candidates(ctx, stage.Approvers) is { } candidates && !candidates.Contains(who))
                errors.TryAdd(field.Key, [ApprovalMessages.UserFieldNotCandidate]);
        }
        return errors;
    }
}

/// <summary>送出前預覽（設計 §5.4）：資格與「已在簽核中」同送出；以目前使用者當發起人。</summary>
public class PreviewRecordApprovalHandler(ApprovalSubmitEligibility eligibility, IApprovalInstanceRepository instances, ApprovalPreviewer previewer,
    ICurrentUser user) : IRequestHandler<PreviewRecordApprovalRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(PreviewRecordApprovalRequest request, CancellationToken ct)
    {
        var (record, module, denied) = await eligibility.LoadAsync(request.RecordId, ct);
        if (record is null || module is null) return denied!;
        if (await instances.GetPendingByRecordAsync(record.Id, ct) is not null) return ServiceResult.Conflict(ApprovalMessages.AlreadyPending);
        var (target, invalid) = await eligibility.TransitionAsync(record, module, request.To, ct);
        if (invalid is not null) return invalid;
        return await previewer.PreviewAsync(module, request.To, target!.Approval, target.Values, user.UserId, designer: false, ct);
    }
}

/// <summary>
/// 設計器試算：流程所在目標狀態上的全部流程照送出規則比對；值以記錄（必須在同一模組）為底、Values 覆蓋。
/// noMatch 取記錄目前狀態（沒有記錄時是起始狀態）進入目標狀態的轉移，找不到就取任一條進入它的需簽核轉移，再沒有就當 block。
/// </summary>
public class PreviewApprovalFlowHandler(IApprovalFlowRepository flows, IModuleRepository modules, IRecordRepository records, ApprovalPreviewer previewer)
    : IRequestHandler<PreviewApprovalFlowRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(PreviewApprovalFlowRequest request, CancellationToken ct)
    {
        if (await flows.GetByKeyAsync(request.Key, ct) is not { } flow) return ServiceResult.NotFound(MessageRef.Of(MessageText.ResourceNotFound));
        if (await modules.GetByIdAsync(flow.ModuleId, includeChildren: true, ct) is not { Workflow: { } wf } module)
            return ServiceResult.NotFound(MessageRef.Of(MessageText.ResourceNotFound));
        var values = new Dictionary<string, JsonElement?>();
        if (request.RecordId is { } id)
        {
            if (await records.GetAsync(id, ct) is not { } record || record.ModuleId != module.Id)
                return ServiceResult.NotFound(RecordMapper.RecordNotFound);
            values = RecordValueProcessor.Parse(record.ValuesJson);
        }
        foreach (var (k, v) in request.Values ?? []) values[k] = v;
        var from = wf.StateOf(values) ?? wf.InitialOptionId;
        var approval = wf.Find(from, flow.ToOptionId)?.Approval
            ?? wf.Transitions.FirstOrDefault(t => t.To == flow.ToOptionId && t.Approval is { Required: true })?.Approval
            ?? new TransitionApproval(true, ApprovalNoMatch.Block);
        return await previewer.PreviewAsync(module, flow.ToOptionId, approval, values, request.InitiatorUserId, designer: true, ct);
    }
}
