using DynamicModules.Domain.Approvals;

namespace DynamicModules.Domain.Repositories.Dynamic;

/// <summary>簽核流程與版本（設計 docs/superpowers/specs/2026-11-10-approval-workflow-design.md §4.3、§4.4）。</summary>
public interface IApprovalFlowRepository
{
    /// <summary>模組上所有流程（含停用）與目前版本的定義；不追蹤。</summary>
    Task<List<FlowState>> ListCurrentByModuleAsync(long moduleId, CancellationToken ct);

    /// <summary>模組上所有流程（含停用）的所有版本定義；toOptionIds 不為 null 時只取掛在這些目標狀態上的流程。</summary>
    Task<List<ApprovalDefinition>> ListAllVersionDefinitionsAsync(long moduleId, CancellationToken ct, IReadOnlyCollection<long>? toOptionIds = null);
}
