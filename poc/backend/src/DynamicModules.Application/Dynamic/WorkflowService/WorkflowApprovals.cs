using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Application.Dynamic.WorkflowService;

/// <summary>狀態機與簽核流程的交會點：鎖定欄位的計算，委派給 <see cref="ApprovalLockService"/>。</summary>
public sealed class WorkflowApprovals(ApprovalLockService lockService)
{
    public async Task<IReadOnlySet<long>> LockedFieldIdsAsync(Module module, Workflow wf, CancellationToken ct) =>
        (await lockService.ForModuleAsync(module, wf, ct)).FieldIds;
}
