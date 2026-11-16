using System.Text.Json;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Rules;

/// <summary>規則引用的欄位：條件裡的欄位（含 fieldRef）與 field: 目標。直接掃 JSON，條件有誤時仍能找出引用。</summary>
public static class RuleReferences
{
    /// <summary>只看條件引用的欄位（含 fieldRef），不含目標。</summary>
    public static HashSet<long> ConditionFieldIdsOf(JsonElement condition)
    {
        var ids = new HashSet<long>();
        Collect(condition, ids);
        return ids;
    }

    public static HashSet<long> FieldIdsOf(Rule rule)
    {
        var ids = ConditionFieldIdsOf(rule.Condition);
        foreach (var t in rule.Targets)
            if (RuleTarget.TryParse(t, out var target) && target is FieldTarget f) ids.Add(f.FieldId);
        return ids;
    }

    private static void Collect(JsonElement el, HashSet<long> ids)
    {
        if (el.ValueKind != JsonValueKind.Object) return;
        foreach (var p in el.EnumerateObject())
        {
            switch (p.Name)
            {
                case "field" when p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt64(out var id):
                    ids.Add(id);
                    break;
                case "value" when p.Value.ValueKind == JsonValueKind.Object
                                  && p.Value.TryGetProperty("fieldRef", out var r) && r.ValueKind == JsonValueKind.Number
                                  && r.TryGetInt64(out var refId):
                    ids.Add(refId);
                    break;
                case "all" or "any" when p.Value.ValueKind == JsonValueKind.Array:
                    foreach (var child in p.Value.EnumerateArray()) Collect(child, ids);
                    break;
            }
        }
    }
}
