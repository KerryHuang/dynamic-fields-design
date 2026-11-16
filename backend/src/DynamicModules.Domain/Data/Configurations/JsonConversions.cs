using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DynamicModules.Domain.Data.Configurations;

internal static class JsonConversions
{
    // camelCase 屬性名（FieldConfig 等）；I18nText 由自身 converter 輸出扁平物件。
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static PropertyBuilder<T> HasJsonConversion<T>(this PropertyBuilder<T> builder) where T : class, new() =>
        builder.HasConversion(
            v => Serialize(v),
            v => JsonSerializer.Deserialize<T>(v, Options) ?? new T(),
            // 以序列化結果比較、以 JSON 來回做快照，才偵測得到就地修改（如 Values["en-US"] = ...）。
            new ValueComparer<T>(
                (a, b) => Serialize(a) == Serialize(b),
                v => Serialize(v).GetHashCode(),
                v => JsonSerializer.Deserialize<T>(Serialize(v), Options)!))
            .IsRequired();

    /// <summary>沒有無參數建構式的型別（例如 positional record）：必填，讀到 null 時丟例外，不默默給空值。</summary>
    public static PropertyBuilder<T> HasJsonRecordConversion<T>(this PropertyBuilder<T> builder) where T : class =>
        builder.HasConversion(
            v => Serialize(v),
            v => DeserializeRequired<T>(v),
            new ValueComparer<T>(
                (a, b) => Serialize(a) == Serialize(b),
                v => Serialize(v).GetHashCode(),
                v => JsonSerializer.Deserialize<T>(Serialize(v), Options)!))
            .IsRequired();

    public static PropertyBuilder<T?> HasJsonNullableConversion<T>(this PropertyBuilder<T?> builder) where T : class =>
        builder.HasConversion(
            v => v == null ? null : Serialize(v),
            v => v == null ? null : JsonSerializer.Deserialize<T>(v, Options),
            new ValueComparer<T?>(
                (a, b) => Serialize(a) == Serialize(b),
                v => Serialize(v).GetHashCode(),
                v => v == null ? null : JsonSerializer.Deserialize<T>(Serialize(v), Options)));

    private static string Serialize<T>(T? value) => JsonSerializer.Serialize(value, Options);

    private static T DeserializeRequired<T>(string json) where T : class =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new InvalidOperationException($"{typeof(T).Name} 不能是 null");

    public static void ConfigureAudit<T>(this EntityTypeBuilder<T> b) where T : Entities.Dynamic.AuditableEntity
    {
        b.Property(x => x.CreatedUserId).HasMaxLength(50).IsRequired();
        b.Property(x => x.ModifiedUserId).HasMaxLength(50);
    }
}
