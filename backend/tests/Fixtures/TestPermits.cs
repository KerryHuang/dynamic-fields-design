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

    /// <summary>
    /// 同步版：body 用同步的 SaveChanges，RunAsync 同步完成，GetResult 不會真的等待。
    /// 不能改成等 SaveChangesAsync：xUnit 的同步內容只有 CPU 數條執行緒，在上面同步等非同步工作，CPU 少時（CI 2 核）會死鎖。
    /// </summary>
    public static int SaveForTests(this DbContext db) =>
        AuthorizationWriteScope.RunAsync(AuthorizationPermit.ForTests(),
            () => AuthorizationSaveChangesInterceptor.HasPendingGuardChanges(db), () => Task.FromResult(db.SaveChanges()))
            .GetAwaiter().GetResult();
}
