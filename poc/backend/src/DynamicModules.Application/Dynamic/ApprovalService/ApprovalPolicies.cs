using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Policies;

namespace DynamicModules.Application.Dynamic.ApprovalService;

/// <summary>簽核類政策的讀值（存流程時的上限）；同一個 scope 內由 <see cref="IPolicyReader"/> 快取。</summary>
public sealed class ApprovalPolicies(IPolicyReader policies)
{
    /// <summary>意見字數上限（全系統，上限 1000 是資料庫欄位長度）。</summary>
    public Task<int> CommentLengthAsync(CancellationToken ct) => policies.IntAsync(PolicyCatalog.ApprovalMaxCommentLength, null, ct);

    /// <summary>發起人在關卡名單內時是否保留（模組範圍）。</summary>
    public Task<bool> AllowSelfApprovalAsync(long moduleId, CancellationToken ct) =>
        policies.BoolAsync(PolicyCatalog.ApprovalAllowSelfApproval, moduleId, ct);

    public async Task<ApprovalLimitSet> LimitsAsync(long moduleId, CancellationToken ct) => new(
        await policies.IntAsync(PolicyCatalog.ApprovalMaxStages, moduleId, ct),
        await policies.IntAsync(PolicyCatalog.ApprovalMaxApprovers, moduleId, ct),
        await policies.IntAsync(PolicyCatalog.ApprovalMaxDueHours, moduleId, ct),
        await policies.IntAsync(PolicyCatalog.ApprovalMaxManagerLevel, moduleId, ct));
}
