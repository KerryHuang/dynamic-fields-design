using DynamicModules.Application.Common;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Common;

/// <summary>
/// 測試用的「安裝／移除一個語系」（i4a 計畫判斷 21）：直接寫 <c>Dynamic_Locale</c> 一列，再讓語系快照立刻換新。
/// i4a 沒有安裝 API，這是唯一的造資料方式；i4d1 有 API 後可改走 API。
/// </summary>
public static class LocaleTestKit
{
    public static async Task InstallAsync(IServiceProvider services, string code, string nativeName, string? fallback = null)
    {
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
            var now = DateTime.UtcNow;
            var row = new InstalledLocale
            {
                Code = code, NativeName = nativeName, FallbackCode = fallback, Origin = LocaleOrigin.Installed, CreatedUtc = now, ModifiedUtc = now,
            };
            db.Set<InstalledLocale>().Add(row);
            await db.SaveChangesAsync();
            db.Entry(row).State = EntityState.Detached;
        }
        await RefreshAsync(services);
    }

    public static async Task RemoveAsync(IServiceProvider services, string code)
    {
        using (var scope = services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<InstalledLocale>().Where(l => l.Code == code).ExecuteDeleteAsync();
        await RefreshAsync(services);
    }

    private static async Task RefreshAsync(IServiceProvider services)
    {
        var registry = services.GetRequiredService<LocaleRegistry>();
        registry.Invalidate();
        await registry.EnsureFreshAsync(CancellationToken.None);
    }
}
