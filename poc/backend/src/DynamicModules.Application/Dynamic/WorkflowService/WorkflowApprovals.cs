using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.WorkflowService;

/// <summary>狀態機與簽核流程的交會點：鎖定欄位的計算。沒有需簽核轉移的狀態機不查資料庫。</summary>
public sealed class WorkflowApprovals(IApprovalFlowRepository flows)
{
    public async Task<IReadOnlySet<long>> LockedFieldIdsAsync(Module module, Workflow wf, CancellationToken ct)
    {
        var targets = ApprovalTopology.ApprovalTargets(wf);
        if (targets.Count == 0) return new HashSet<long>();
        var defs = await flows.ListAllVersionDefinitionsAsync(module.Id, ct, [.. targets]);
        return ApprovalTopology.LockedFields(module, wf, defs);
    }
}
