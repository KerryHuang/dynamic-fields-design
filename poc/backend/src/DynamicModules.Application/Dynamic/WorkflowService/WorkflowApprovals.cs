using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Application.Dynamic.WorkflowService;

/// <summary>狀態機與簽核流程的交會點：鎖定欄位的計算，委派給 <see cref="ApprovalLockService"/>。</summary>
public sealed class WorkflowApprovals(ApprovalLockService lockService, ApprovalEditableFields editableFields)
{
    public async Task<IReadOnlySet<long>> LockedFieldIdsAsync(Module module, Workflow wf, CancellationToken ct) =>
        (await lockService.ForModuleAsync(module, wf, ct)).FieldIds;

    /// <summary>設計器可選的可編輯欄位候選（簽核關卡的可編輯欄位）。</summary>
    public Task<IReadOnlyList<long>> EditableFieldIdsAsync(Module module, Workflow wf, CancellationToken ct) =>
        editableFields.CandidatesAsync(module, wf, ct);
}
