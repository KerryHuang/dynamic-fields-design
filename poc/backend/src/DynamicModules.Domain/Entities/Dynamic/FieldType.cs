using System.Text.Json;
using System.Text.Json.Serialization;

namespace DynamicModules.Domain.Entities.Dynamic;

/// <summary>JSON 序列化成小寫字串：text、multienum、datetime……</summary>
[JsonConverter(typeof(LowercaseEnumConverter<FieldType>))]
public enum FieldType
{
    Text, Textarea, Integer, Decimal, Currency, Percent, Date, DateTime, Time,
    Boolean, Email, Phone, Url, Enum, MultiEnum, User,
    Formula, AutoNumber, Reference, Rollup
}

public sealed class LowercaseEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Enum.TryParse<T>(reader.GetString(), ignoreCase: true, out var value)
            ? value
            : throw new JsonException($"Unknown {typeof(T).Name}: {reader.GetString()}");

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString().ToLowerInvariant());
}
