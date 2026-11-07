using System.Text.Json;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.FieldTypes.Handlers;

/// <summary>
/// 附件欄位：值是檔案描述陣列。一般存檔（含匯入、直接打 API）只能<b>保留或移除</b>目前已有的檔案，
/// 送來的 id 不在舊值裡一律拒絕、檔名與大小以舊值為準，所以沒有任何方式用記錄存檔偽造一個檔案參照；
/// 新檔案只能經上傳端點加入。不索引，不可篩選與排序。
/// </summary>
public sealed class AttachmentHandler : IFieldTypeHandler
{
    public const string ForgedMessage = "附件只能透過上傳加入";

    public FieldType Type => FieldType.Attachment;

    public IReadOnlyList<FilterOperator> Operators => [];

    public IReadOnlyList<IndexValue> ToIndex(JsonElement value, Field field) => [];

    public FieldValueResult Normalize(JsonElement? input, Field field, FieldTypeContext ctx)
    {
        if (input is not { } v || v.ValueKind == JsonValueKind.Null) return FieldValueResult.Ok(null);
        if (v.ValueKind != JsonValueKind.Array) return FieldValueResult.Fail("附件的值必須是檔案清單");
        var existing = AttachmentValue.Parse(ctx.PreviousValue).ToDictionary(f => f.Id);
        var kept = new List<AttachmentRef>();
        foreach (var e in v.EnumerateArray())
        {
            var id = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String
                ? i.GetString() : null;
            if (id is null || !existing.TryGetValue(id, out var file)) return FieldValueResult.Fail(ForgedMessage);
            if (kept.All(k => k.Id != file.Id)) kept.Add(file);
        }
        return kept.Count == 0 ? FieldValueResult.Ok(null) : FieldValueResult.Ok(AttachmentValue.ToJson(kept));
    }
}
