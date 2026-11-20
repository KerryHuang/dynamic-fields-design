using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using DynamicModules.Application.Common.Localization;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Localization;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.LocaleService.Packs;

/// <summary>
/// 一段覆寫層的檢視（i4d2 計畫判斷 3）：計數是全部，鍵清單 Ordinal 排序、各取前 <see cref="LocalePackLimits.MaxListedItems"/> 筆。
/// <see cref="RuntimeFallback"/> 是 <see cref="ParamsChanged"/> 的子集，介面文字恆為 0（前端不會因佔位符不符退回內建層）。
/// </summary>
public sealed record OverlayReviewSection(int BaseChanged, int ParamsChanged, int RuntimeFallback, int Orphaned,
    IReadOnlyList<string> BaseChangedKeys, IReadOnlyList<string> ParamsChangedKeys, IReadOnlyList<string> RuntimeFallbackKeys, IReadOnlyList<string> OrphanedKeys)
{
    /// <summary>完整的「原文已更新」鍵集合（不截 50 筆）：確認與樣板篩選用它，不用畫面清單。</summary>
    [JsonIgnore] internal IReadOnlySet<string> AllBaseChanged { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>完整的「參數不符」鍵集合（不截 50 筆）。</summary>
    [JsonIgnore] internal IReadOnlySet<string> AllParamsChanged { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    public static OverlayReviewSection Empty { get; } = new(0, 0, 0, 0, [], [], [], []);
}

public sealed record OverlayReview(OverlayReviewSection Ui, OverlayReviewSection Server)
{
    public static OverlayReview Empty { get; } = new(OverlayReviewSection.Empty, OverlayReviewSection.Empty);
}

/// <summary>
/// 從目前版的原始列（不經 <see cref="Common.LocaleRegistry"/> 的濾鍵）算覆寫層檢視（i4d2 計畫判斷 3），純函式：
/// <list type="bullet">
/// <item>原文已更新：仍是基準鍵、<c>BaseHashesJson</c> 有記雜湊、而且和現在內建 zh-TW 原文的雜湊不同；沒記雜湊不算。</item>
/// <item>參數不符：用現在的規則重驗覆寫句（後端 <see cref="ServerTemplateSyntax"/>、介面 <see cref="UiMessageSyntax"/>），不依賴雜湊。</item>
/// <item>執行期退回內建翻譯（只有後端）：現在 zh 範本至少一個參數，而且以「zh 最大參數編號＋1」個引數格式化覆寫句會丟 <see cref="FormatException"/>
/// （<c>MessageCatalog.Get</c> 只在這時退回；零引數時原樣回範本、引數多於範本用到的不丟例外）。引數個數是推算（寫死清單）。</item>
/// <item>已失效：原始 JSON 裡不是基準鍵的鍵。</item>
/// </list>
/// 空字串的值不算覆寫；JSON 解析失敗（只可能是資料修補）回全 0，不丟例外。
/// </summary>
public static class LocaleOverlayReview
{
    private static readonly IReadOnlySet<string> NoNames = new HashSet<string>();
    private static readonly Lazy<HashSet<string>> UiBase = new(() => BuiltinLocaleResources.BaseKeys.ToHashSet(StringComparer.Ordinal));
    private static readonly Lazy<HashSet<string>> ServerBase = new(() => BuiltinServerText.BaseKeys.ToHashSet(StringComparer.Ordinal));

    public static OverlayReview Of(LocalePack? current)
    {
        if (current is null) return OverlayReview.Empty;
        var ui = Layer(current.UiJson);
        var server = Layer(current.ServerJson);
        if (ui is null || server is null) return OverlayReview.Empty;
        var hashes = LocalePackVersions.ParseHashes(current.BaseHashesJson);
        return new OverlayReview(
            Section(ui, UiBase.Value, hashes.GetValueOrDefault(LocaleWorldText.Ui), UiZh, UiParamsChanged, static (_, _) => false),
            Section(server, ServerBase.Value, hashes.GetValueOrDefault(LocaleWorldText.Server), ServerZh, ServerParamsChanged, ServerRuntimeFallback));
    }

    /// <summary>已失效的鍵與舊譯文（給樣板的 <c>review.orphaned</c>）；<paramref name="segment"/> 是 <c>ui</c> 或 <c>server</c>。</summary>
    public static IReadOnlyDictionary<string, string> OrphanedTexts(LocalePack current, string segment)
    {
        var layer = Layer(segment == LocaleWorldText.Ui ? current.UiJson : current.ServerJson);
        var baseKeys = segment == LocaleWorldText.Ui ? UiBase.Value : ServerBase.Value;
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, text) in layer ?? [])
            if (text.Length > 0 && !baseKeys.Contains(key)) result[key] = text;
        return result;
    }

    private static OverlayReviewSection Section(Dictionary<string, string> layer, HashSet<string> baseKeys, Dictionary<string, string>? hashes,
        Func<string, string> zh, Func<string, string, bool> paramsChanged, Func<string, string, bool> runtimeFallback)
    {
        var baseChanged = new HashSet<string>(StringComparer.Ordinal);
        var paramsSet = new HashSet<string>(StringComparer.Ordinal);
        var fallback = new List<string>();
        var orphaned = new List<string>();
        foreach (var (key, text) in layer)
        {
            if (text.Length == 0) continue;
            if (!baseKeys.Contains(key)) { orphaned.Add(key); continue; }
            var original = zh(key);
            if (hashes?.GetValueOrDefault(key) is { } recorded && recorded != LocalePackVersions.BaseHash(original)) baseChanged.Add(key);
            if (paramsChanged(key, text))
            {
                paramsSet.Add(key);
                if (runtimeFallback(text, original)) fallback.Add(key);
            }
        }
        return new OverlayReviewSection(baseChanged.Count, paramsSet.Count, fallback.Count, orphaned.Count,
            Listed(baseChanged), Listed(paramsSet), Listed(fallback), Listed(orphaned))
        {
            AllBaseChanged = baseChanged, AllParamsChanged = paramsSet,
        };
    }

    private static IReadOnlyList<string> Listed(IEnumerable<string> keys) => keys.Order(StringComparer.Ordinal).Take(LocalePackLimits.MaxListedItems).ToList();

    private static string UiZh(string key) => BuiltinLocaleResources.Ui[I18nText.FallbackLocale].GetValueOrDefault(key) ?? "";

    private static string ServerZh(string key) => BuiltinServerText.TryGet(I18nText.FallbackLocale, key, out var zh) ? zh : "";

    private static bool UiParamsChanged(string key, string text) =>
        UiMessageSyntax.Check(text, BuiltinLocaleResources.UiPlaceholderNames.GetValueOrDefault(key) ?? NoNames) is not null;

    private static bool ServerParamsChanged(string key, string text) => ServerTemplateSyntax.Check(text, ServerZh(key)) is not null;

    private static bool ServerRuntimeFallback(string text, string zh)
    {
        var count = System.Text.RegularExpressions.Regex.Matches(zh, @"(?<!\{)\{(\d+)\}").Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .DefaultIfEmpty(-1).Max() + 1;
        if (count == 0) return false;
        var args = new object[count];
        Array.Fill(args, "x");
        try { _ = string.Format(CultureInfo.InvariantCulture, text, args); return false; }
        catch (FormatException) { return true; }
    }

    private static Dictionary<string, string>? Layer(string json)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(json); }
        catch (Exception e) when (e is JsonException or NotSupportedException) { return null; }
    }
}
