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
        await RefreshAsync(app, codes => codes.Contains(code));
    }

    public static async Task RemoveAsync(TestApp app, string code)
    {
        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<InstalledLocale>().Where(l => l.Code == code).ExecuteDeleteAsync();
        await RefreshAsync(app, codes => !codes.Contains(code));
    }

    /// <summary>
    /// 等到快照真的反映這次寫入。另一個請求正在換新時 <see cref="LocaleRegistry.EnsureFreshAsync"/> 直接返回（設計如此），
    /// 那次換新可能在寫入前就讀完，所以要重試到看見結果；不重試的話下一個請求會拿到舊快照（CI 偶發）。
    /// </summary>
    private static async Task RefreshAsync(TestApp app, Func<IReadOnlyList<string>, bool> done)
    {
        var registry = app.Services.GetRequiredService<LocaleRegistry>();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            registry.Invalidate();
            await registry.EnsureFreshAsync(CancellationToken.None);
            if (done(registry.Codes)) return;
            if (DateTime.UtcNow > deadline) throw new TimeoutException("語系快照 10 秒內沒有反映寫入");
            await Task.Delay(20);
        }
    }
}
