using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Repositories.Dynamic;

public interface IAccountRepository
{
    /// <summary>以追蹤模式載入使用者，呼叫端修改後由 IUnitOfWork 提交；不存在回 null。</summary>
    Task<AppUser?> GetTrackedAsync(string userId, CancellationToken ct);
}
