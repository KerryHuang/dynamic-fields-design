using System.Text.Json;
using DynamicModules.Domain.Conditions;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Rules;
using FluentAssertions;

namespace DynamicModules.DomainTests.Rules;

/// <summary>解析器擋掉型別不合的運算元，所以不可比的值只能直接建節點測；前端 cmpHolds 對不可比一律 false。</summary>
public class ConditionEvaluatorIncomparableTests
{
    private static readonly FieldTypeRegistry Registry = FieldTypeRegistry.CreateDefault();

    [Theory]
    [InlineData(FilterOperator.Gt)]
    [InlineData(FilterOperator.Gte)]
    [InlineData(FilterOperator.Lt)]
    [InlineData(FilterOperator.Lte)]
    [InlineData(FilterOperator.Between)]
    [InlineData(FilterOperator.Eq)]
    public void IncomparableValues_OrderingAndEqAreFalse(FilterOperator op)
    {
        var field = new Field { Id = 1, Key = "f_1", Type = FieldType.Text, Status = FieldStatus.Active, Config = new FieldConfig() };
        var values = new Dictionary<string, JsonElement?> { ["f_1"] = JsonDocument.Parse("\"abc\"").RootElement.Clone() };
        var number = new IndexValue(0, null, 5, null, null);
        var node = new FieldCondition(field, op, [number, number], null);

        ConditionEvaluator.Evaluate(node, values, Registry, new DateOnly(2026, 11, 3)).Should().BeFalse();
    }
}
