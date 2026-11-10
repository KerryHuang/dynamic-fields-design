using System.Text.Json;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Rules;

/// <summary>任務規則引用了哪些欄位：觸發欄位、各任務的負責人與到期基準欄位，以及規則與任務條件裡的欄位。</summary>
public static class TaskRuleReferences
{
    public static HashSet<long> FieldIdsOf(TaskRule rule)
    {
        var ids = new HashSet<long>();
        if (rule.Trigger.FieldId is { } trigger) ids.Add(trigger);
        AddCondition(rule.WhenJson, ids);
        foreach (var t in rule.Tasks)
        {
            ids.Add(t.AssigneeFieldId);
            if (t.DueFieldId is { } due) ids.Add(due);
            AddCondition(t.WhenJson, ids);
        }
        return ids;
    }

    private static void AddCondition(string? whenJson, HashSet<long> ids)
    {
        if (string.IsNullOrWhiteSpace(whenJson)) return;
        try
        {
            using var doc = JsonDocument.Parse(whenJson);
            ids.UnionWith(RuleReferences.ConditionFieldIdsOf(doc.RootElement));
        }
        catch (JsonException)
        {
            // 壞掉的條件文字沒有可引用的欄位
        }
    }
}
