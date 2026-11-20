using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using DynamicModules.Domain.Localization;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Common.Localization;

/// <summary>內嵌的內建語系原始資源：代碼 → 原始 JSON 字串；不符資源名契約的名稱另列（給自檢與測試注入）。</summary>
public sealed record BuiltinResourceSet(
    IReadOnlyDictionary<string, string> UiJson,
    IReadOnlyDictionary<string, string> LocaleJson,
    string? QuasarLangsJson,
    IReadOnlyList<string> UnexpectedResourceNames);

/// <summary>
/// 內建介面文字的內建層（語系包設計 §8.1；i4b 計畫判斷 3、4）：唯一來源是 repo 的 <c>shared/i18n/&lt;code&gt;/{ui,locale}.json</c>
/// 與 <c>shared/quasar-langs.json</c>，由 csproj 內嵌（建置檢查擋住檔數不齊）。<see cref="Verify"/> 是啟動自檢，Program 在碰資料庫前呼叫，
/// 有問題就拒絕啟動。讀取都是 <see cref="Lazy{T}"/>，不在 static 建構子讀，避免資源出錯時變成型別初始化例外。
/// </summary>
public static partial class BuiltinLocaleResources
{
    private const string QuasarLangsName = "quasar-langs.json";
    private const int MaxListedKeys = 20;

    /// <summary>資源名契約：<c>i18n/&lt;代碼&gt;/(ui|locale).json</c>（Windows 的反斜線先換成斜線）。</summary>
    [GeneratedRegex(@"^i18n/([A-Za-z0-9-]+)/(ui|locale)\.json$")]
    private static partial Regex ResourceName();

    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> UiLazy = new(() =>
        Load().UiJson.ToDictionary(kv => kv.Key, kv => UiTextMerge.Flatten(Parse(kv.Value)), StringComparer.Ordinal));

    private static readonly Lazy<IReadOnlyList<string>> BaseKeysLazy = new(() =>
        Ui[I18nText.FallbackLocale].Keys.Order(StringComparer.Ordinal).ToArray());

    private static readonly Lazy<IReadOnlySet<string>> QuasarLangsLazy = new(() =>
        ParseQuasarLangs(Load().QuasarLangsJson ?? throw new InvalidOperationException("缺少內嵌資源 quasar-langs.json")));

    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlySet<string>>> UiPlaceholderNamesLazy = new(() =>
        BaseKeys.ToDictionary(k => k, k => (IReadOnlySet<string>)Ui.Values
            .SelectMany(texts => texts.TryGetValue(k, out var t) ? PlaceholderNames(t) : [])
            .ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal));

    /// <summary>
    /// 每個基準鍵在內建四語用到的佔位符名稱聯集（i4d1 計畫判斷 4）：語系包的介面文字只能用這些名稱（少用不擋）。
    /// 有幾句四語的名稱集合不同（例 <c>approval.stagesMore</c>），所以取聯集而不是只看繁中。
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> UiPlaceholderNames => UiPlaceholderNamesLazy.Value;

    /// <summary>去掉字面量 <c>{'…'}</c> 與 <c>{{…}}</c> 之後的 <c>{名稱}</c>（同前端 locales.vitest.spec.ts 的 placeholders）。</summary>
    private static IEnumerable<string> PlaceholderNames(string text)
        => PlaceholderName().Matches(LiteralOrMustache().Replace(text, "")).Select(m => m.Groups[1].Value);

    [GeneratedRegex(@"\{\{.*?\}\}|\{'.*?'\}")]
    private static partial Regex LiteralOrMustache();

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex PlaceholderName();

    /// <summary>代碼 → 壓平的內建層。</summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Ui => UiLazy.Value;

    /// <summary>基準鍵集：內建繁中壓平後的鍵（Ordinal 排序）。</summary>
    public static IReadOnlyList<string> BaseKeys => BaseKeysLazy.Value;

    /// <summary>Quasar 語言包名稱（<c>shared/quasar-langs.json</c>，由 scripts/i18n/gen-quasar-langs.mjs 產生）。</summary>
    public static IReadOnlySet<string> QuasarLangs => QuasarLangsLazy.Value;

    /// <summary>讀本組件的內嵌資源。</summary>
    public static BuiltinResourceSet Load()
    {
        var asm = typeof(BuiltinLocaleResources).Assembly;
        var ui = new Dictionary<string, string>(StringComparer.Ordinal);
        var locale = new Dictionary<string, string>(StringComparer.Ordinal);
        var unexpected = new List<string>();
        string? quasar = null;
        foreach (var raw in asm.GetManifestResourceNames())
        {
            var name = raw.Replace('\\', '/');
            if (name == QuasarLangsName) { quasar = Read(asm, raw); continue; }
            if (!name.StartsWith("i18n/", StringComparison.Ordinal)) continue;
            var m = ResourceName().Match(name);
            if (!m.Success) { unexpected.Add(name); continue; }
            (m.Groups[2].Value == "ui" ? ui : locale)[m.Groups[1].Value] = Read(asm, raw);
        }
        return new BuiltinResourceSet(ui, locale, quasar, unexpected);
    }

    /// <summary>啟動自檢：回傳問題清單（每項一句，含代碼與鍵名），空＝通過。</summary>
    public static IReadOnlyList<string> Verify(BuiltinResourceSet set)
    {
        var problems = new List<string>();
        foreach (var name in set.UnexpectedResourceNames)
            problems.Add($"內嵌資源名稱不符 i18n/<代碼>/(ui|locale).json：{name}");

        CheckCodes(set.UiJson.Keys, "ui.json", problems);
        CheckCodes(set.LocaleJson.Keys, "locale.json", problems);

        foreach (var b in BuiltinLocales.All)
        {
            if (!set.LocaleJson.TryGetValue(b.Code, out var json)) continue;
            try
            {
                var e = Parse(json);
                foreach (var (field, expected) in new[] { ("code", b.Code), ("nativeName", b.NativeName), ("quasarLang", b.QuasarLang) })
                {
                    var actual = e.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                    if (actual != expected) problems.Add($"{b.Code} 的 locale.json 欄位 {field} 是「{actual}」，與程式內建的「{expected}」不同");
                }
            }
            catch (JsonException ex) { problems.Add($"{b.Code} 的 locale.json 無法解析：{ex.Message}"); }
        }

        var flat = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var (code, json) in set.UiJson.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            try { flat[code] = UiTextMerge.Flatten(Parse(json)); }
            catch (Exception ex) when (ex is JsonException or FormatException) { problems.Add($"{code} 的 ui.json 無法解析：{ex.Message}"); }
        }
        foreach (var (code, texts) in flat)
        {
            var empty = texts.Where(kv => kv.Value.Length == 0).Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList();
            if (empty.Count > 0) problems.Add($"{code} 的 ui.json 有空字串：{List(empty)}");
        }
        if (flat.TryGetValue(I18nText.FallbackLocale, out var baseTexts))
        {
            foreach (var (code, texts) in flat)
            {
                if (code == I18nText.FallbackLocale) continue;
                var missing = baseTexts.Keys.Except(texts.Keys).Order(StringComparer.Ordinal).ToList();
                var extra = texts.Keys.Except(baseTexts.Keys).Order(StringComparer.Ordinal).ToList();
                if (missing.Count > 0) problems.Add($"{code} 的 ui.json 缺少 {missing.Count} 個鍵：{List(missing)}");
                if (extra.Count > 0) problems.Add($"{code} 的 ui.json 多出 {extra.Count} 個 {I18nText.FallbackLocale} 沒有的鍵：{List(extra)}");
            }
        }

        if (set.QuasarLangsJson is null) problems.Add("缺少內嵌資源 quasar-langs.json");
        else
        {
            try
            {
                var langs = ParseQuasarLangs(set.QuasarLangsJson);
                foreach (var b in BuiltinLocales.All.Where(b => !langs.Contains(b.QuasarLang)))
                    problems.Add($"quasar-langs.json 沒有內建語系 {b.Code} 的 Quasar 語言包 {b.QuasarLang}");
            }
            catch (Exception ex) when (ex is JsonException or FormatException) { problems.Add($"quasar-langs.json 無法解析：{ex.Message}"); }
        }
        return problems;
    }

    private static void CheckCodes(IEnumerable<string> codes, string file, List<string> problems)
    {
        var set = codes.ToHashSet(StringComparer.Ordinal);
        foreach (var c in BuiltinLocales.Codes.Where(c => !set.Contains(c))) problems.Add($"缺少內建語系 {c} 的 {file}");
        foreach (var c in set.Where(c => !BuiltinLocales.Contains(c)).Order(StringComparer.Ordinal)) problems.Add($"多出非內建語系 {c} 的 {file}");
    }

    private static string List(IReadOnlyList<string> keys)
        => string.Join("、", keys.Take(MaxListedKeys)) + (keys.Count > MaxListedKeys ? $"⋯（另 {keys.Count - MaxListedKeys} 個）" : "");

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static IReadOnlySet<string> ParseQuasarLangs(string json)
    {
        var e = Parse(json);
        if (e.ValueKind != JsonValueKind.Array || e.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String))
            throw new FormatException("quasar-langs.json 必須是字串陣列");
        return e.EnumerateArray().Select(x => x.GetString()!).ToHashSet(StringComparer.Ordinal);
    }

    private static string Read(Assembly asm, string name)
    {
        using var s = asm.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
