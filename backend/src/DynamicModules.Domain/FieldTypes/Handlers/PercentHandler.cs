using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.FieldTypes.Handlers;

public sealed class PercentHandler : IFieldTypeHandler
{
    public FieldType Type => FieldType.Percent;

    public IReadOnlyList<FilterOperator> Operators => HandlerHelpers.NumberOperators;

    public IReadOnlyList<IndexValue> ToIndex(JsonElement value, Field field) =>
        HandlerHelpers.SingleNumber(value.GetDouble());

    public FieldValueResult Normalize(JsonElement? input, Field field, FieldTypeContext ctx) =>
        HandlerHelpers.NormalizeDecimal(input, field.Config.Scale ?? 2, ctx.EnforceWriteLimits);
}
