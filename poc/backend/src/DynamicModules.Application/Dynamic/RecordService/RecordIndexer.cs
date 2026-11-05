using System.Text.Json;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;

namespace DynamicModules.Application.Dynamic.RecordService;

/// <summary>把記錄值依欄位設定投影成 RecordIndex 列。</summary>
public static class RecordIndexer
{
    public static bool ShouldIndex(Field field) =>
        field.Status == FieldStatus.Active
        && (field.Config.IsFilterable || field.Config.IsSortable || field.Type is FieldType.MultiEnum or FieldType.Reference);

    public static List<RecordIndex> Build(
        Module module, IReadOnlyDictionary<string, JsonElement?> values, FieldTypeRegistry registry) =>
        module.Blocks.SelectMany(b => b.Fields).Where(ShouldIndex)
            .SelectMany(f => BuildField(f, values, registry)).ToList();

    public static List<RecordIndex> BuildField(
        Field field, IReadOnlyDictionary<string, JsonElement?> values, FieldTypeRegistry registry)
    {
        if (!values.TryGetValue(field.Key, out var value) || value is null || value.Value.ValueKind == JsonValueKind.Null)
            return [];
        return registry.Get(field.Type).ToIndex(value.Value, field).Select(v => new RecordIndex
        {
            FieldId = field.Id, Seq = v.Seq, TextValue = v.TextValue, NumberValue = v.NumberValue,
            DateValue = v.DateValue, RefKey = v.RefKey
        }).ToList();
    }
}
