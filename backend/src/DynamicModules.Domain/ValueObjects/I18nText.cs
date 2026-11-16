using System.Text.Json;
using System.Text.Json.Serialization;

namespace DynamicModules.Domain.ValueObjects;

/// <summary>多語系文字。JSON 形狀是以語系為鍵的扁平物件，如 {"zh-TW":"訂單","en-US":"Order"}。</summary>
[JsonConverter(typeof(I18nTextJsonConverter))]
public class I18nText
{
    public const string FallbackLocale = "zh-TW";

    public static readonly string[] SupportedLocales = ["zh-TW", "en-US", "th", "vi"];

    private readonly Dictionary<string, string> _values;

    public I18nText() => _values = new();

    public I18nText(IEnumerable<KeyValuePair<string, string>> values) => _values = new(values);

    public IReadOnlyDictionary<string, string> Values => _values;

    public void Set(string locale, string text) => _values[locale] = text;

    /// <summary>取指定語系文字；缺值或空字串退回 zh-TW。</summary>
    public string Resolve(string locale) =>
        _values.TryGetValue(locale, out var text) && !string.IsNullOrEmpty(text)
            ? text
            : _values.GetValueOrDefault(FallbackLocale) ?? "";

    /// <summary>zh-TW 必填；不得出現不支援的語系鍵。</summary>
    public bool IsValid(out string? error)
    {
        if (string.IsNullOrWhiteSpace(_values.GetValueOrDefault(FallbackLocale)))
        {
            error = $"{FallbackLocale} 為必填";
            return false;
        }

        var unknown = _values.Keys.FirstOrDefault(k => !SupportedLocales.Contains(k));
        if (unknown is not null)
        {
            error = $"不支援的語系：{unknown}";
            return false;
        }

        error = null;
        return true;
    }
}

internal sealed class I18nTextJsonConverter : JsonConverter<I18nText>
{
    public override I18nText Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(JsonSerializer.Deserialize<Dictionary<string, string>>(ref reader, (JsonSerializerOptions?)null) ?? []);

    public override void Write(Utf8JsonWriter writer, I18nText value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value.Values, (JsonSerializerOptions?)null);
}
