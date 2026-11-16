using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.FieldTypes.Handlers;

public sealed class UserHandler : IFieldTypeHandler
{
    public FieldType Type => FieldType.User;

    public IReadOnlyList<FilterOperator> Operators => HandlerHelpers.PickOperators;

    public IReadOnlyList<IndexValue> ToIndex(JsonElement value, Field field) =>
        [new(0, null, null, null, value.GetString()!)];

    public FieldValueResult Normalize(JsonElement? input, Field field, FieldTypeContext ctx)
    {
        if (HandlerHelpers.IsNull(input)) return FieldValueResult.Ok(null);
        var s = input!.Value.ValueKind == JsonValueKind.String ? input.Value.GetString()! : "";
        return ctx.UserIds.Contains(s)
            ? FieldValueResult.Ok(HandlerHelpers.ToElement(s))
            : FieldValueResult.Fail(FieldErrors.UserNotFound);
    }
}
