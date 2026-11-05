using System.Text.Json;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.DomainTests.Formulas;

internal static class FormulaTestKit
{
    public static Field Fld(long id, FieldType type, FieldConfig? config = null, FieldStatus status = FieldStatus.Active) =>
        new() { Id = id, Key = $"f_{id}", Type = type, Config = config ?? new FieldConfig(), Status = status };

    public static Field Formula(long id, string expr, string resultType = "number", int? scale = null) =>
        Fld(id, FieldType.Formula, new FieldConfig { Formula = expr, ResultType = resultType, Scale = scale });

    public static Module Mod(params Field[] fields) =>
        new() { Blocks = [new Block { Fields = [.. fields] }] };

    /// <summary>fields 1 integer、2 currency、3 percent、4 date、5 text、6 formula(number)、7 datetime。</summary>
    public static Module Standard() => Mod(
        Fld(1, FieldType.Integer), Fld(2, FieldType.Currency), Fld(3, FieldType.Percent),
        Fld(4, FieldType.Date), Fld(5, FieldType.Text), Formula(6, "{1}*{2}"), Fld(7, FieldType.DateTime));

    public static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    public static Dictionary<string, JsonElement?> Values(params (long id, string json)[] items) =>
        items.ToDictionary(i => $"f_{i.id}", i => (JsonElement?)J(i.json));
}
