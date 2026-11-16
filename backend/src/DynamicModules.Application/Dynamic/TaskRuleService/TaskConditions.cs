using System.Text.Json;
using DynamicModules.Domain.Conditions;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Rules;

namespace DynamicModules.Application.Dynamic.TaskRuleService;

public static class TaskConditions
{
    /// <summary>規則或任務的條件在這組記錄值下是否成立。null 條件為 true；解析失敗為 false（同 EventStage.WhenHoldsAsync）。</summary>
    public static bool Holds(
        string? whenJson, Module module, IReadOnlyDictionary<string, JsonElement?> values,
        FieldTypeRegistry registry, DateOnly today, IReadOnlySet<string> userIds)
    {
        if (string.IsNullOrWhiteSpace(whenJson)) return true;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(whenJson); }
        catch (JsonException) { return false; }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            var parsed = ConditionParser.Parse(doc.RootElement, module, registry, ConditionUsage.Rule, userIds);
            return parsed.Node is not null && ConditionEvaluator.Evaluate(parsed.Node, values, registry, today);
        }
    }
}
