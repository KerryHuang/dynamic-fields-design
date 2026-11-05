using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Repositories.Dynamic;

public interface IAccountRepository
{
    /// <summary>以追蹤模式載入使用者，呼叫端修改後由 IUnitOfWork 提交；不存在回 null。</summary>
    Task<AppUser?> GetTrackedAsync(string userId, CancellationToken ct);

    /// <summary>Demo 一鍵登入清單：啟用中的 admin 排第一，其後是啟用中的示範帳號（依帳號排序）；唯讀。</summary>
    Task<IReadOnlyList<AppUser>> ListDemoLoginAccountsAsync(CancellationToken ct);
}
