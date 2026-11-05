using System.Text.Json;
using DynamicModules.Domain.Conditions;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Domain.Rules;

/// <summary>
/// 對一筆記錄求值模組的啟用中規則。語意（已定決策 3、4）：
/// Visibility 全部成立才顯示；Required、ReadOnly 任一成立就生效；OptionFilter 成立的各給一組選項取交集；Validation 各自檢查。
/// ReadOnly 用已儲存的值求值（新增時 stored 為 null，一律不成立），其餘用 current。
/// 條件解析失敗的規則略過：設計時已驗證過，只可能因資料被直接改動，不能讓存檔失敗。
/// </summary>
public static class RuleEngine
{
    public static RuleOutcome Evaluate(
        Module module,
        IReadOnlyDictionary<string, JsonElement?> current,
        IReadOnlyDictionary<string, JsonElement?>? stored,
        FieldTypeRegistry registry,
        IReadOnlySet<string> userIds,
        DateOnly today)
    {
        var fields = module.Blocks.SelectMany(b => b.Fields).Where(f => f.Status == FieldStatus.Active).ToDictionary(f => f.Id);
        var hidden = new HashSet<long>();
        var required = fields.Values.Where(f => f.Config.Required).Select(f => f.Id).ToHashSet();
        var readOnly = new HashSet<long>();
        var readOnlyAll = false;
        var allowed = new Dictionary<long, IReadOnlySet<long>>();
        var failed = new List<(IReadOnlyList<long>, I18nText)>();

        foreach (var rule in module.Rules.Where(r => r.Active).OrderBy(r => r.Sequence))
        {
            var parsed = ConditionParser.Parse(rule.Condition, module, registry, ConditionUsage.Rule, userIds);
            if (parsed.Error is not null) continue;
            var values = rule.Kind == RuleKind.ReadOnly ? stored : current;
            if (values is null) continue; // 新增時沒有已儲存的值，ReadOnly 不成立
            var holds = ConditionEvaluator.Evaluate(parsed.Node, values, registry, today);

            switch (rule.Kind)
            {
                case RuleKind.Visibility when !holds:
                    hidden.UnionWith(Expand(rule, module, fields));
                    break;
                case RuleKind.Required when holds:
                    required.UnionWith(Expand(rule, module, fields));
                    break;
                case RuleKind.ReadOnly when holds:
                    readOnly.UnionWith(Expand(rule, module, fields));
                    readOnlyAll |= rule.Targets.Contains("*");
                    break;
                case RuleKind.OptionFilter when holds:
                    foreach (var id in Expand(rule, module, fields))
                    {
                        var ids = rule.OptionIds.ToHashSet();
                        allowed[id] = allowed.TryGetValue(id, out var prev) ? prev.Intersect(ids).ToHashSet() : ids;
                    }
                    break;
                case RuleKind.Validation when !holds:
                    failed.Add((Expand(rule, module, fields).ToList(), rule.MessageI18n ?? new I18nText()));
                    break;
            }
        }

        required.ExceptWith(hidden);
        return new RuleOutcome(hidden, required, readOnly, readOnlyAll, allowed, failed);
    }

    /// <summary>目標展開成欄位 Id（依目標順序）：block 變成區塊內的啟用欄位，不存在或停用的欄位略過，* 不產生欄位。</summary>
    private static IEnumerable<long> Expand(Rule rule, Module module, Dictionary<long, Field> fields)
    {
        var seen = new HashSet<long>();
        foreach (var raw in rule.Targets)
        {
            if (!RuleTarget.TryParse(raw, out var target)) continue;
            var ids = target switch
            {
                FieldTarget f => [f.FieldId],
                BlockTarget b => module.Blocks.Where(x => x.Id == b.BlockId).SelectMany(x => x.Fields).Select(x => x.Id).ToArray(),
                _ => Array.Empty<long>()
            };
            foreach (var id in ids)
                if (fields.ContainsKey(id) && seen.Add(id)) yield return id;
        }
    }
}
