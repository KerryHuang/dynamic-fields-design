using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Repositories.Dynamic;

public interface IAccountRepository
{
    /// <summary>以追蹤模式載入使用者，呼叫端修改後由 IUnitOfWork 提交；不存在回 null。</summary>
    Task<AppUser?> GetTrackedAsync(string userId, CancellationToken ct);

    /// <summary>Demo 一鍵登入清單：啟用中的 admin 排第一，其後是啟用中的示範帳號（依帳號排序）；唯讀。</summary>
    Task<IReadOnlyList<AppUser>> ListDemoLoginAccountsAsync(CancellationToken ct);

    void AddUser(AppUser user);

    Task<bool> UserExistsAsync(string userId, CancellationToken ct);

    /// <summary>啟用中的系統管理員人數；在交易內呼叫，判斷「最後一位」才不會被並行請求繞過。</summary>
    Task<int> CountActiveAdminsAsync(CancellationToken ct);

    /// <summary>把使用者的群組成員關係整組換成 groupIds；寫入由 IUnitOfWork 提交。</summary>
    Task SetMembershipsAsync(string userId, IReadOnlyCollection<long> groupIds, CancellationToken ct);

    /// <summary>groupIds 全部都是存在的群組。</summary>
    Task<bool> GroupsExistAsync(IReadOnlyCollection<long> groupIds, CancellationToken ct);

    /// <summary>以追蹤模式載入群組；不存在回 null。</summary>
    Task<PermissionGroup?> GetGroupTrackedAsync(long groupId, CancellationToken ct);

    void AddGroup(PermissionGroup group);

    /// <summary>該語系已有群組用同名（忽略大小寫）；excludeId 排除自己（改名用）。</summary>
    Task<bool> GroupNameExistsAsync(string locale, string name, long? excludeId, CancellationToken ct);

    /// <summary>userIds 全部都是存在的使用者。</summary>
    Task<bool> UsersExistAsync(IReadOnlyCollection<string> userIds, CancellationToken ct);

    /// <summary>把群組的成員整組換成 userIds；寫入由 IUnitOfWork 提交。</summary>
    Task SetGroupMembersAsync(long groupId, IReadOnlyCollection<string> userIds, CancellationToken ct);

    /// <summary>刪除群組前的影響統計；名稱以 I18nText.FallbackLocale 解析。</summary>
    Task<GroupImpact> GetGroupImpactAsync(long groupId, CancellationToken ct);

    /// <summary>明確刪除成員對應、模組授權、欄位例外、資料範圍、選項範圍與群組本身（無 FK cascade）；寫入由 IUnitOfWork 提交。</summary>
    Task DeleteGroupAsync(PermissionGroup group, CancellationToken ct);
}

public record BlockingOption(string Module, string Field, string Option);

public record GroupImpact(
    int MemberCount, List<string> Modules, int FieldPermissionCount, int DataScopeCount, List<BlockingOption> BlockingOptions);
