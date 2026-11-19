using DynamicModules.Domain.Authorization;
using DynamicModules.Domain.Data;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.TestSupport;

/// <summary>
/// 測試專用的寫入許可（權限軸設計 §7.4 的 Test 種類）：fixture 直接寫授權表（群組權限、個人權限、欄位權限、成員…）時包在這裡面。
/// 只在測試專案；清單測試禁止 src 出現 RunForTestsAsync。
/// </summary>
public static class TestPermits
{
    public static Task RunForTestsAsync(Func<Task> body) =>
        AuthorizationWriteScope.RunAsync(AuthorizationPermit.ForTests(), () => false, body);

    public static Task<T> RunForTestsAsync<T>(Func<Task<T>> body) =>
        AuthorizationWriteScope.RunAsync(AuthorizationPermit.ForTests(), () => false, body);

    /// <summary>在測試許可內存檔：fixture 先 Add 授權列，再呼叫這個取代 SaveChangesAsync。</summary>
    public static Task<int> SaveForTestsAsync(this DbContext db, CancellationToken ct = default) =>
        AuthorizationWriteScope.RunAsync(AuthorizationPermit.ForTests(),
            () => AuthorizationSaveChangesInterceptor.HasPendingGuardChanges(db), () => db.SaveChangesAsync(ct));

    public static int SaveForTests(this DbContext db) => SaveForTestsAsync(db).GetAwaiter().GetResult();
}
