using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Localization;

namespace DynamicModules.Application.Dynamic.LocaleService.Packs;

/// <summary>
/// 語系的完整度（i4d2 計畫判斷 4，從 <see cref="LocalePackAnalyzer"/> 抽出；預覽報告與語系清單共用同一個定義）：
/// 「該語系自己有句子」＝覆寫層（已濾過基準鍵）或該語系內建層有非空值，不含走退回鏈補的。
/// </summary>
public static class LocaleCompleteness
{
    public static CompletenessReport Of(string code, LocaleOverlay? overlay)
    {
        var builtin = BuiltinLocaleResources.Ui.GetValueOrDefault(code);
        return new CompletenessReport(
            new CompletenessCount(BuiltinLocaleResources.BaseKeys.Count(k => Has(overlay?.Ui, k) || Has(builtin, k)), BuiltinLocaleResources.BaseKeys.Count),
            new CompletenessCount(BuiltinServerText.BaseKeys.Count(k => Has(overlay?.Server, k) || BuiltinServerText.TryGet(code, k, out var t) && t.Length > 0),
                BuiltinServerText.BaseKeys.Count));
    }

    private static bool Has(IReadOnlyDictionary<string, string>? layer, string key) => layer is not null && layer.TryGetValue(key, out var t) && t.Length > 0;
}
