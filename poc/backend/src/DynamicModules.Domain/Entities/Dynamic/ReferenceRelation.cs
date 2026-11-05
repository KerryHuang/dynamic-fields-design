using System.Text.Json;
using System.Text.Json.Serialization;

namespace DynamicModules.Domain.Entities.Dynamic;

/// <summary>參照欄位的關係：一般參照或明細（子記錄）；JSON 為 lookup、detail。</summary>
[JsonConverter(typeof(ReferenceRelationConverter))]
public enum ReferenceRelation { Lookup, Detail }

public sealed class ReferenceRelationConverter : JsonConverter<ReferenceRelation>
{
    public override ReferenceRelation Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Enum.TryParse<ReferenceRelation>(reader.GetString(), ignoreCase: true, out var value)
            ? value
            : throw new JsonException($"Unknown ReferenceRelation: {reader.GetString()}");

    public override void Write(Utf8JsonWriter writer, ReferenceRelation value, JsonSerializerOptions options) =>
        writer.WriteStringValue(JsonNamingPolicy.CamelCase.ConvertName(value.ToString()));
}
