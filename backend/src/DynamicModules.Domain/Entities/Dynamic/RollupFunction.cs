using System.Text.Json;
using System.Text.Json.Serialization;

namespace DynamicModules.Domain.Entities.Dynamic;

/// <summary>彙總欄位的函數；JSON 為 sum、count、min、max。</summary>
[JsonConverter(typeof(RollupFunctionConverter))]
public enum RollupFunction { Sum, Count, Min, Max }

public sealed class RollupFunctionConverter : JsonConverter<RollupFunction>
{
    public override RollupFunction Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Enum.TryParse<RollupFunction>(reader.GetString(), ignoreCase: true, out var value)
            ? value
            : throw new JsonException($"Unknown RollupFunction: {reader.GetString()}");

    public override void Write(Utf8JsonWriter writer, RollupFunction value, JsonSerializerOptions options) =>
        writer.WriteStringValue(JsonNamingPolicy.CamelCase.ConvertName(value.ToString()));
}
