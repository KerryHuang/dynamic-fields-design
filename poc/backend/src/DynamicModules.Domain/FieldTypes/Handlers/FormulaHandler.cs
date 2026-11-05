using System.Globalization;
using System.Text.Json;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.FieldTypes.Handlers;

/// <summary>公式欄位：系統計算，不接受輸入；date 結果索引到 DateValue，數字結果索引到 NumberValue。</summary>
public sealed class FormulaHandler : IFieldTypeHandler
{
    public FieldType Type => FieldType.Formula;

    public IReadOnlyList<FilterOperator> Operators => HandlerHelpers.NumberOperators;

    public IReadOnlyList<FilterOperator> OperatorsFor(Field field) =>
        field.Config.ResultType == "date" ? HandlerHelpers.DateOperators : HandlerHelpers.NumberOperators;

    public IReadOnlyList<IndexValue> ToIndex(JsonElement value, Field field) =>
        field.Config.ResultType == "date"
            ? HandlerHelpers.SingleDate(DateTime.ParseExact(value.GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture))
            : HandlerHelpers.SingleNumber(value.GetDouble());

    public FieldValueResult Normalize(JsonElement? input, Field field, FieldTypeContext ctx) =>
        FieldValueResult.Ok(null);
}
