using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using DynamicModules.Application.Dynamic.PermissionService.Data;
using DynamicModules.Application.Dynamic.TemplateService.Data;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Application.Dynamic.TemplateService;

/// <summary>範本檔的讀寫設定：camelCase、省略 null、FieldType 小寫、其他列舉 camelCase 字串（同 Program.cs）。</summary>
public static class TemplateJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        // 匯出的檔案給人讀：中文不跳脫
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // 須在 JsonStringEnumConverter 之前：先到先贏
        Converters = { new LowercaseEnumConverter<FieldType>(), new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        // PrivilegesDto.IsEmpty 是計算屬性，範本檔不寫（API 回應照舊）
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers =
            {
                ti =>
                {
                    if (ti.Type != typeof(PrivilegesDto)) return;
                    var isEmpty = ti.Properties.FirstOrDefault(p => p.Name == "isEmpty");
                    if (isEmpty is not null) ti.Properties.Remove(isEmpty);
                }
            }
        }
    };

    public static (TemplatePackage? Package, string? Error) Parse(string json)
    {
        try
        {
            var package = JsonSerializer.Deserialize<TemplatePackage>(json, Options);
            return package is null ? (null, "範本格式錯誤：內容是空的") : (package, null);
        }
        catch (JsonException e)
        {
            return (null, $"範本格式錯誤：{e.Message}");
        }
    }
}
