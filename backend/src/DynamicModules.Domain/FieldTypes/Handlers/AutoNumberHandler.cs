using System.Text.Json;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.FieldTypes.Handlers;

/// <summary>自動編號欄位：系統產生，不接受輸入；索引放 TextValue。</summary>
public sealed class AutoNumberHandler : IFieldTypeHandler
{
    public FieldType Type => FieldType.AutoNumber;

    public IReadOnlyList<FilterOperator> Operators => HandlerHelpers.TextOperators;

    public IReadOnlyList<IndexValue> ToIndex(JsonElement value, Field field) =>
        HandlerHelpers.SingleText(value);

    public FieldValueResult Normalize(JsonElement? input, Field field, FieldTypeContext ctx) =>
        FieldValueResult.Ok(null);
}
