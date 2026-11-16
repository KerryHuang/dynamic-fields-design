using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.WorkflowService;

/// <summary>
/// 狀態轉移的群組限制對系統管理員是否放行（政策 approval.adminBypassTransitionGroups，出廠放行）。只管轉移群組，
/// 不影響系統管理員的其他權限。系統身分（事件、簽核完成的轉移）在 WorkflowEnforcer 已先放行，不經這裡。
/// </summary>
public sealed class TransitionAccess(IPolicyReader policies)
{
    public async Task<bool> BypassGroupsAsync(PermissionInputs inputs, CancellationToken ct) =>
        inputs.User.IsSystemAdmin && await policies.BoolAsync(PolicyCatalog.ApprovalAdminBypassTransitionGroups, null, ct);
}
