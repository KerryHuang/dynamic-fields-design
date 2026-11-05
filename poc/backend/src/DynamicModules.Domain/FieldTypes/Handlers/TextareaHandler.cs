using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.FieldTypes.Handlers;

public sealed class TextareaHandler : IFieldTypeHandler
{
    public FieldType Type => FieldType.Textarea;

    public IReadOnlyList<FilterOperator> Operators => HandlerHelpers.NoOperators;

    public IReadOnlyList<IndexValue> ToIndex(JsonElement value, Field field) =>
        HandlerHelpers.NoIndex;

    public FieldValueResult Normalize(JsonElement? input, Field field, FieldTypeContext ctx) =>
        HandlerHelpers.NormalizeText(input, field.Config.MaxLength ?? 4000);
}
