using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Localization;
using DynamicModules.Domain.Localization;

namespace DynamicModules.Application.Dynamic.LocaleService.Packs;

/// <summary>
/// 語系的完整度（i4d2 計畫判斷 4，從 <see cref="LocalePackAnalyzer"/> 抽出；預覽報告與語系清單共用同一個定義）：
/// 「該語系自己有句子」＝覆寫層（已濾過基準鍵）或該語系內建層有非空值，不含走退回鏈補的。
/// 缺最多的分類：介面文字取鍵的第一段（<c>approval.panel.x</c> → <c>approval</c>）；後端訊息 <c>report.</c> → <c>report</c>、
/// <c>alert.</c> → <c>alert</c>、其他 → <c>message</c>。依缺數遞減、同數依分類名 Ordinal，取前 <see cref="LocalePackLimits.MaxMissingCategories"/> 組。
/// </summary>
public static class LocaleCompleteness
{
    public static CompletenessReport Of(string code, LocaleOverlay? overlay)
    {
        var uiMissing = UiMissing(code, overlay);
        var serverMissing = ServerMissing(code, overlay);
        int ui = BuiltinLocaleResources.BaseKeys.Count, server = BuiltinServerText.BaseKeys.Count;
        return new CompletenessReport(new CompletenessCount(ui - uiMissing.Count, ui), new CompletenessCount(server - serverMissing.Count, server))
        {
            TopMissing = Top(uiMissing, serverMissing, LocalePackLimits.MaxMissingCategories),
        };
    }

    internal static IReadOnlyList<MissingCategory> TopMissing(string code, LocaleOverlay? overlay, int take) =>
        Top(UiMissing(code, overlay), ServerMissing(code, overlay), take);

    private static List<string> UiMissing(string code, LocaleOverlay? overlay)
    {
        var builtin = BuiltinLocaleResources.Ui.GetValueOrDefault(code);
        return BuiltinLocaleResources.BaseKeys.Where(k => !Has(overlay?.Ui, k) && !Has(builtin, k)).ToList();
    }

    private static List<string> ServerMissing(string code, LocaleOverlay? overlay) =>
        BuiltinServerText.BaseKeys.Where(k => !Has(overlay?.Server, k) && !(BuiltinServerText.TryGet(code, k, out var t) && t.Length > 0)).ToList();

    private static IReadOnlyList<MissingCategory> Top(IEnumerable<string> uiMissing, IEnumerable<string> serverMissing, int take) =>
        uiMissing.GroupBy(UiCategory).Select(g => new MissingCategory(LocaleWorldText.Ui, g.Key, g.Count()))
            .Concat(serverMissing.GroupBy(ServerCategory).Select(g => new MissingCategory(LocaleWorldText.Server, g.Key, g.Count())))
            .OrderByDescending(c => c.Missing).ThenBy(c => c.Category, StringComparer.Ordinal).ThenBy(c => c.Segment, StringComparer.Ordinal)
            .Take(take).ToList();

    private static string UiCategory(string key) => key.Split('.')[0];

    private static string ServerCategory(string key) =>
        key.StartsWith("report.", StringComparison.Ordinal) ? "report" : key.StartsWith("alert.", StringComparison.Ordinal) ? "alert" : "message";

    private static bool Has(IReadOnlyDictionary<string, string>? layer, string key) => layer is not null && layer.TryGetValue(key, out var t) && t.Length > 0;
}
