using DynamicModules.Application.Common;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.WebAPITests;

/// <summary>
/// 測試用的「安裝／移除一個語系」（i4a 計畫判斷 21）：直接寫 <c>Dynamic_Locale</c> 一列，再讓語系快照立刻換新
/// （政策快照看到 generation 不同，下一個請求就重載）。i4a 沒有安裝 API，這是唯一的造資料方式；i4d1 有 API 後可改走 API。
/// </summary>
internal static class LocaleTestKit
{
    public static async Task InstallAsync(TestApp app, string code, string nativeName, string? fallback = null)
    {
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
            var now = DateTime.UtcNow;
            db.Set<InstalledLocale>().Add(new InstalledLocale
            {
                Code = code, NativeName = nativeName, FallbackCode = fallback, Origin = LocaleOrigin.Installed, CreatedUtc = now, ModifiedUtc = now,
            });
            await db.SaveChangesAsync();
        }
        await RefreshAsync(app);
    }

    public static async Task RemoveAsync(TestApp app, string code)
    {
        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<InstalledLocale>().Where(l => l.Code == code).ExecuteDeleteAsync();
        await RefreshAsync(app);
    }

    private static async Task RefreshAsync(TestApp app)
    {
        var registry = app.Services.GetRequiredService<LocaleRegistry>();
        registry.Invalidate();
        await registry.EnsureFreshAsync(CancellationToken.None);
    }
}
