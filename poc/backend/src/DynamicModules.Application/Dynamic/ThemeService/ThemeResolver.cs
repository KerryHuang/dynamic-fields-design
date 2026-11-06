using DynamicModules.Application.Dynamic.ThemeService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.ThemeService;

/// <summary>外觀解析：個人設定 > 全站預設 > 出廠預設，兩個欄位各自判斷；未知值視為沒有設定。</summary>
public static class ThemeResolver
{
    public static async Task<ThemeDefaultsResponse> ReadDefaultsAsync(ISystemSettingRepository settings, CancellationToken ct) =>
        new(ThemeOptions.NormalizeMode(await settings.GetValueAsync(SystemSetting.ThemeDefaultMode, ct)) ?? ThemeOptions.FactoryMode,
            ThemeOptions.NormalizeStyle(await settings.GetValueAsync(SystemSetting.ThemeDefaultStyle, ct)) ?? ThemeOptions.FactoryStyle);

    /// <summary>Demo 站的外觀只存在瀏覽器，所以忽略資料庫裡的個人設定且不能儲存。</summary>
    public static MeThemeResponse ForUser(AppUser user, ThemeDefaultsResponse defaults, bool demoSite)
    {
        var mode = demoSite ? null : ThemeOptions.NormalizeMode(user.ThemeMode);
        var style = demoSite ? null : ThemeOptions.NormalizeStyle(user.ThemeStyle);
        return new(mode, style, mode ?? defaults.Mode, style ?? defaults.Style, defaults.Mode, defaults.Style, !demoSite);
    }
}
