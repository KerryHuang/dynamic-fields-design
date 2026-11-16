using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Repositories.Dynamic;

/// <summary>某張儀表板（或個人版）上有卡片引用某報表；OwnerUserId 為 null 是指派版。</summary>
public record DashboardReportReference(long DashboardId, string DashboardName, string? OwnerUserId, bool IsPersonalVersion, string? PersonalUserId);

public interface IDashboardRepository
{
    /// <summary>追蹤中的儀表板（含 Assignments），供修改或刪除。</summary>
    Task<Dashboard?> GetAsync(long id, CancellationToken ct);
    /// <summary>指派給 groupIds 任一群組的指派版（含 Assignments，依 Sequence、Id，不追蹤）。</summary>
    Task<List<Dashboard>> ListAssignedToAsync(IReadOnlyCollection<long> groupIds, CancellationToken ct);
    /// <summary>所有指派版（含 Assignments，依 Sequence、Id，不追蹤），管理畫面用。</summary>
    Task<List<Dashboard>> ListAssignedAsync(CancellationToken ct);
    /// <summary>該使用者自己的儀表板（不追蹤，依 Id）。</summary>
    Task<List<Dashboard>> ListOwnedAsync(string userId, CancellationToken ct);
    /// <summary>該使用者在這些指派版上的個人版（不追蹤）。</summary>
    Task<List<DashboardPersonalVersion>> ListPersonalAsync(string userId, IReadOnlyCollection<long> dashboardIds, CancellationToken ct);
    /// <summary>追蹤中的個人版，沒有回 null。</summary>
    Task<DashboardPersonalVersion?> GetPersonalAsync(long dashboardId, string userId, CancellationToken ct);
    Task<int> MaxSequenceAsync(CancellationToken ct);
    /// <summary>是否已有同名指派版，Demo 產生前的衝突檢查用。</summary>
    Task<bool> AssignedNameExistsAsync(string name, CancellationToken ct);
    /// <summary>所有引用該報表的儀表板與個人版（含他人的），供刪除報表時檢查。</summary>
    Task<List<DashboardReportReference>> ListReferencesAsync(long reportId, CancellationToken ct);
    /// <summary>引用該報表的、屬於 userId 自己的儀表板與個人版（追蹤中），供刪除報表時移除卡片。</summary>
    Task<(List<Dashboard> Owned, List<DashboardPersonalVersion> Personal)> ListOwnReferencingTrackedAsync(
        long reportId, string userId, CancellationToken ct);
    void Add(Dashboard dashboard);
    void AddPersonal(DashboardPersonalVersion version);
    void RemovePersonal(DashboardPersonalVersion version);
    /// <summary>連同它的指派與所有人的個人版一併移除。</summary>
    Task RemoveAsync(Dashboard dashboard, CancellationToken ct);
}
