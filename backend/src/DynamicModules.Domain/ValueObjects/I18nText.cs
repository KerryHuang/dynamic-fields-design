using System.Text.Json;
using System.Text.Json.Serialization;
using DynamicModules.Domain.Localization;

namespace DynamicModules.Domain.ValueObjects;

/// <summary>多語系文字。JSON 形狀是以語系為鍵的扁平物件，如 {"zh-TW":"訂單","en-US":"Order"}。</summary>
[JsonConverter(typeof(I18nTextJsonConverter))]
public class I18nText
{
    public const string FallbackLocale = "zh-TW";

    private readonly Dictionary<string, string> _values;

    public I18nText() => _values = new();

    public I18nText(IEnumerable<KeyValuePair<string, string>> values) => _values = new(values);

    public IReadOnlyDictionary<string, string> Values => _values;

    public void Set(string locale, string text) => _values[locale] = text;

    /// <summary>
    /// 取指定語系文字（判斷 13）：要求的語系 → <paramref name="fallbacks"/> 依序（null 時只有繁中）→ <see cref="BuiltinLocales"/> 順序中
    /// 第一個有值的（安全網）→ 空字串。缺值與空字串都往下退。請求的輸出由呼叫端組好退回鏈（<see cref="LocaleChain"/>）傳入；
    /// 沒安裝的語系鍵不會被挑中（U6：存起來但不顯示）。
    /// </summary>
    public string Resolve(string locale, IReadOnlyList<string>? fallbacks)
    {
        if (Text(locale) is { } text) return text;
        foreach (var f in fallbacks ?? [FallbackLocale])
            if (Text(f) is { } t) return t;
        foreach (var l in BuiltinLocales.Codes)
            if (Text(l) is { } t) return t;
        return "";
    }

    /// <summary>退回鏈只有繁中（中文訊息與套件格式用；請求的輸出用帶退回鏈的版本）。獨立多載而非選擇性參數：運算式樹不能用選擇性參數。</summary>
    public string Resolve(string locale) => Resolve(locale, null);

    /// <summary>依 <see cref="LocaleContext"/>（要求的語系＋退回鏈）取文字，同 <see cref="Resolve(string, IReadOnlyList{string})"/>。</summary>
    public string Resolve(LocaleContext lc) => Resolve(lc.Locale, lc.Fallbacks);

    private string? Text(string locale) => _values.TryGetValue(locale, out var text) && !string.IsNullOrEmpty(text) ? text : null;

    /// <summary>
    /// 格式正確：沒有格式不對的語系鍵（<see cref="LocaleCode"/>；沒安裝的語系也收，M1），且至少一個語系有非空白文字。
    /// 必填哪些語系由 Application 依政策 locale.requiredText 檢查。
    /// </summary>
    public bool IsWellFormed(out string? error)
    {
        if (InvalidLocale(_values.Keys) is { } invalid)
        {
            error = $"不支援的語系：{invalid}";
            return false;
        }
        if (!_values.Values.Any(v => !string.IsNullOrWhiteSpace(v)))
        {
            error = "至少要有一種語系的文字";
            return false;
        }
        error = null;
        return true;
    }

    /// <summary>第一個格式不對的語系鍵（不是正規形式的語系代碼）；全部合法回 null。不判斷有沒有安裝（語系包設計 §17.4 M1）。</summary>
    public static string? InvalidLocale(IEnumerable<string> keys) => keys.FirstOrDefault(k => !LocaleCode.IsValid(k));

    /// <summary>這個語系有非空白文字。</summary>
    public bool HasText(string locale) => !string.IsNullOrWhiteSpace(_values.GetValueOrDefault(locale));

    /// <summary><paramref name="required"/> 中沒有文字的語系（保留傳入順序）。</summary>
    public IReadOnlyList<string> MissingLocales(IEnumerable<string> required) => required.Where(l => !HasText(l)).ToList();

    /// <summary>內容相同：各語系去前後空白、忽略空值後比對（§3.5「改了才檢查」）。</summary>
    public bool SameAs(I18nText? other)
    {
        if (other is null) return false;
        var a = Normalized(this);
        var b = Normalized(other);
        return a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);
    }

    private static Dictionary<string, string> Normalized(I18nText t) =>
        t._values.Where(kv => !string.IsNullOrWhiteSpace(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value.Trim(), StringComparer.Ordinal);
}

internal sealed class I18nTextJsonConverter : JsonConverter<I18nText>
{
    public override I18nText Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(JsonSerializer.Deserialize<Dictionary<string, string>>(ref reader, (JsonSerializerOptions?)null) ?? []);

    public override void Write(Utf8JsonWriter writer, I18nText value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value.Values, (JsonSerializerOptions?)null);
}
