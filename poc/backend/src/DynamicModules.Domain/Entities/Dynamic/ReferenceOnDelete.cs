using System.Text.Json;
using System.Text.Json.Serialization;

namespace DynamicModules.Domain.Entities.Dynamic;

/// <summary>被參照的記錄刪除時，參照欄位的處理方式；JSON 為 restrict、setNull、cascade。</summary>
[JsonConverter(typeof(ReferenceOnDeleteConverter))]
public enum ReferenceOnDelete { Restrict, SetNull, Cascade }

public sealed class ReferenceOnDeleteConverter : JsonConverter<ReferenceOnDelete>
{
    public override ReferenceOnDelete Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Enum.TryParse<ReferenceOnDelete>(reader.GetString(), ignoreCase: true, out var value)
            ? value
            : throw new JsonException($"Unknown ReferenceOnDelete: {reader.GetString()}");

    public override void Write(Utf8JsonWriter writer, ReferenceOnDelete value, JsonSerializerOptions options) =>
        writer.WriteStringValue(JsonNamingPolicy.CamelCase.ConvertName(value.ToString()));
}
