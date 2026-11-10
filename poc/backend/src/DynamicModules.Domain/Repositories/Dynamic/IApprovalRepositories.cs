using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Repositories.Dynamic;

/// <summary>
/// 簽核流程與版本（設計 docs/superpowers/specs/2026-11-10-approval-workflow-design.md §4.3、§4.4）。
/// 「被用過」：被任何實例綁定（Instance.FlowVersionId），或出現在任何實例的流程快照（InstanceFlow）裡。
/// </summary>
public interface IApprovalFlowRepository
{
    /// <summary>追蹤。</summary>
    Task<ApprovalFlow?> GetByKeyAsync(string key, CancellationToken ct);

    /// <summary>流程所在模組的代號；找不到流程回 null。</summary>
    Task<string?> ModuleKeyOfAsync(string flowKey, CancellationToken ct);

    /// <summary>模組上所有流程（含停用）與目前版本的定義；不追蹤，依目標狀態、優先序排序。</summary>
    Task<List<FlowState>> ListCurrentByModuleAsync(long moduleId, CancellationToken ct);

    /// <summary>同一目標狀態上的流程；追蹤（重排優先序用）。</summary>
    Task<List<ApprovalFlow>> ListByTargetTrackedAsync(long moduleId, long toOptionId, CancellationToken ct);

    /// <summary>模組上所有流程（含停用）的所有版本定義；toOptionIds 不為 null 時只取掛在這些目標狀態上的流程。</summary>
    Task<List<ApprovalDefinition>> ListAllVersionDefinitionsAsync(long moduleId, CancellationToken ct, IReadOnlyCollection<long>? toOptionIds = null);

    /// <summary>追蹤。</summary>
    Task<ApprovalFlowVersion?> GetVersionAsync(long versionId, CancellationToken ct);

    Task<int> MaxVersionAsync(long flowId, CancellationToken ct);

    Task<bool> IsUsedAsync(long flowId, CancellationToken ct);
    Task<bool> IsVersionUsedAsync(long versionId, CancellationToken ct);

    /// <summary>綁定這個流程、狀態 pending 的實例數。</summary>
    Task<int> CountPendingAsync(long flowId, CancellationToken ct);

    /// <summary>模組流程的目前版本，加上被 pending 實例綁定或出現在其快照裡的版本（設計 §8.6）。</summary>
    Task<List<(long FlowId, string FlowKey, ApprovalDefinition Definition)>> ListDefinitionsInUseAsync(long moduleId, CancellationToken ct);

    /// <summary>目前版本或 pending 實例用到的版本裡，簽核人來源、候選群組或備援引用這個群組的流程鍵；只被已結束實例用過的版本不算。</summary>
    Task<List<string>> ListFlowKeysReferencingGroupAsync(long groupId, CancellationToken ct);

    void Add(ApprovalFlow f);
    void AddVersion(ApprovalFlowVersion v);

    /// <summary>連同所有版本一起刪除（呼叫端已確認沒被用過）。</summary>
    Task RemoveAsync(ApprovalFlow f, CancellationToken ct);
}
