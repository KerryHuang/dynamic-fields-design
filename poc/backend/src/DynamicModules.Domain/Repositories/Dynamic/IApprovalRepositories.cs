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

/// <summary>進行中實例的摘要：整批重算固定記錄、定義守衛用，不讀記錄值。</summary>
public sealed record PendingInstanceRef(
    long InstanceId, long RecordId, long ModuleId, long FromOptionId, long ToOptionId, long? FlowVersionId, string TitleSnapshot);

/// <summary>簽核實例、關卡實例、任務與歷程（設計 §4.3）。標「追蹤」的回傳值改了會在 SaveChanges 時寫回。</summary>
public interface IApprovalInstanceRepository
{
    /// <summary>不追蹤：這些模組 pending 的實例摘要。</summary>
    Task<List<PendingInstanceRef>> ListPendingByModulesAsync(IReadOnlyCollection<long> moduleIds, CancellationToken ct);

    /// <summary>追蹤。</summary>
    Task<ApprovalInstance?> GetAsync(long id, CancellationToken ct);

    /// <summary>追蹤。</summary>
    Task<ApprovalInstance?> GetPendingByRecordAsync(long recordId, CancellationToken ct);

    /// <summary>這些記錄裡有 pending 實例的。</summary>
    Task<HashSet<long>> ListPendingRecordIdsAsync(IReadOnlyCollection<long> recordIds, CancellationToken ct);

    /// <summary>條件更新 RowVersion（WHERE RowVersion = expected）；沒有更新到回 false。</summary>
    Task<bool> TryBumpRowVersionAsync(long instanceId, string expected, string next, CancellationToken ct);

    /// <summary>追蹤，依 Seq。</summary>
    Task<List<ApprovalStageRun>> ListRunsAsync(long instanceId, CancellationToken ct);

    /// <summary>追蹤。</summary>
    Task<List<ApprovalTask>> ListTasksAsync(long instanceId, CancellationToken ct);

    /// <summary>追蹤。</summary>
    Task<ApprovalTask?> GetTaskAsync(long taskId, CancellationToken ct);

    /// <summary>不追蹤，SubmittedTime、Id 降冪。</summary>
    Task<List<ApprovalInstance>> ListByRecordAsync(long recordId, CancellationToken ct);

    /// <summary>不追蹤，依 Id。</summary>
    Task<List<ApprovalAction>> ListActionsAsync(IReadOnlyCollection<long> instanceIds, CancellationToken ct);

    /// <summary>不追蹤。</summary>
    Task<List<ApprovalStageRun>> ListRunsAsync(IReadOnlyCollection<long> instanceIds, CancellationToken ct);

    /// <summary>不追蹤。</summary>
    Task<List<ApprovalTask>> ListTasksAsync(IReadOnlyCollection<long> instanceIds, CancellationToken ct);

    /// <summary>不追蹤，依 Id。</summary>
    Task<List<ApprovalInstance>> ListInstancesAsync(IReadOnlyCollection<long> ids, CancellationToken ct);

    /// <summary>不追蹤：這位使用者送出的實例。</summary>
    Task<List<ApprovalInstance>> ListByInitiatorAsync(string userId, CancellationToken ct);

    /// <summary>不追蹤：這些模組的實例。</summary>
    Task<List<ApprovalInstance>> ListByModulesAsync(IReadOnlyCollection<long> moduleIds, CancellationToken ct);

    /// <summary>有實例的模組 Id。</summary>
    Task<List<long>> ListInstanceModuleIdsAsync(CancellationToken ct);

    /// <summary>不追蹤：這位使用者可動作的 open 任務（負責人，或代理時的原簽核人）。</summary>
    Task<List<ApprovalTask>> ListOpenTasksOfAsync(string userId, CancellationToken ct);

    /// <summary>這位使用者是這筆記錄某個 open 任務的可動作者：負責人，或 Reason = delegated 時的原簽核人（設計 §10.1）。</summary>
    Task<bool> IsOpenActorAsync(long recordId, string userId, CancellationToken ct);

    /// <summary>不追蹤：這位使用者動作過的任務（ActedTime 不為 null）。</summary>
    Task<List<ApprovalTask>> ListActedTasksOfAsync(string userId, CancellationToken ct);

    void Add(ApprovalInstance i);
    void AddRun(ApprovalStageRun r);
    void AddTask(ApprovalTask t);
    void AddAction(ApprovalAction a);
    void AddSnapshotRows(IEnumerable<ApprovalInstanceFlow> rows);
}
