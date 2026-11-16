using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.FieldTypes.Handlers;

public sealed class MultiEnumHandler : IFieldTypeHandler
{
    public FieldType Type => FieldType.MultiEnum;

    public IReadOnlyList<FilterOperator> Operators => HandlerHelpers.MultiPickOperators;

    public IReadOnlyList<IndexValue> ToIndex(JsonElement value, Field field) =>
        value.EnumerateArray().Select((e, i) => EnumHandler.EnumIndex(i, e.GetString()!)).ToList();

    public FieldValueResult Normalize(JsonElement? input, Field field, FieldTypeContext ctx)
    {
        if (HandlerHelpers.IsNull(input)) return FieldValueResult.Ok(null);
        if (input!.Value.ValueKind != JsonValueKind.Array) return FieldValueResult.Fail(EnumHandler.Error);

        var previous = new HashSet<string>();
        if (ctx.PreviousValue is { ValueKind: JsonValueKind.Array } prev)
            foreach (var e in prev.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String) previous.Add(e.GetString()!);

        var values = new List<(string Value, long Id)>();
        foreach (var e in input.Value.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.String) return FieldValueResult.Fail(EnumHandler.Error);
            var s = e.GetString()!;
            if (!EnumHandler.IsAllowed(s, ctx, previous.Contains(s) ? s : null))
                return FieldValueResult.Fail(EnumHandler.Error);
            EnumHandler.TryParseId(s, out var id);
            var canonical = EnumHandler.Canonical(s);
            if (values.All(v => v.Value != canonical)) values.Add((canonical, id));
        }

        var ordered = values
            .OrderBy(v => ctx.OptionSequence.TryGetValue(v.Id, out var seq) ? seq : int.MaxValue)
            .ThenBy(v => v.Id)
            .Select(v => v.Value)
            .ToList();
        return FieldValueResult.Ok(HandlerHelpers.ToElement(ordered));
    }
}
