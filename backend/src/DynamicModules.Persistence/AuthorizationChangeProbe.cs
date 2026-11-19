using DynamicModules.Domain.Authorization;
using DynamicModules.Domain.Data;

namespace DynamicModules.Persistence;

/// <summary>寫入許可結束時查這個請求的 context 有沒有未存的守衛變更（權限軸設計 §7.4）。</summary>
public sealed class AuthorizationChangeProbe(DynamicDbContext db) : IAuthorizationChangeProbe
{
    public bool HasPendingGuardChanges() => AuthorizationSaveChangesInterceptor.HasPendingGuardChanges(db);
}
