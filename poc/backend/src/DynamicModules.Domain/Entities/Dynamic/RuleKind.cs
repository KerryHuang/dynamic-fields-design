using System.Text.Json;
using System.Text.Json.Serialization;

namespace DynamicModules.Domain.Entities.Dynamic;

/// <summary>JSON 序列化成 camelCase 字串：visibility、required、readOnly、validation、optionFilter。</summary>
[JsonConverter(typeof(RuleKindConverter))]
public enum RuleKind { Visibility, Required, ReadOnly, Validation, OptionFilter }

public sealed class RuleKindConverter : JsonConverter<RuleKind>
{
    public override RuleKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Enum.TryParse<RuleKind>(reader.GetString(), ignoreCase: true, out var value)
            ? value
            : throw new JsonException($"Unknown RuleKind: {reader.GetString()}");

    public override void Write(Utf8JsonWriter writer, RuleKind value, JsonSerializerOptions options) =>
        writer.WriteStringValue(JsonNamingPolicy.CamelCase.ConvertName(value.ToString()));
}
