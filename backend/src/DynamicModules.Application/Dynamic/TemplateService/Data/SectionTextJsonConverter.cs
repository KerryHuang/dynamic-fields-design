using System.Text.Json;
using System.Text.Json.Serialization;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.TemplateService.Data;

/// <summary>
/// 模組選單分區的 JSON：讀取接受字串（＝只有繁中）或多語系物件；寫出時只有繁中就寫字串，有其他語系才寫物件。
/// 舊版只寫字串的套件與範本照常讀得進來，只有繁中的匯出也和舊版長得一樣，所以格式版號不變。
/// </summary>
public sealed class SectionTextJsonConverter : JsonConverter<I18nText>
{
    public override I18nText Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => new I18nText([KeyValuePair.Create(I18nText.FallbackLocale, reader.GetString()!)]),
            JsonTokenType.StartObject => JsonSerializer.Deserialize<I18nText>(ref reader, (JsonSerializerOptions?)null)!,
            _ => throw new JsonException("section 必須是字串或多語系物件")
        };

    public override void Write(Utf8JsonWriter writer, I18nText value, JsonSerializerOptions options)
    {
        if (value.Values.Count == 1 && value.Values.TryGetValue(I18nText.FallbackLocale, out var zh))
            writer.WriteStringValue(zh);
        else
            JsonSerializer.Serialize(writer, value, (JsonSerializerOptions?)null);
    }
}
