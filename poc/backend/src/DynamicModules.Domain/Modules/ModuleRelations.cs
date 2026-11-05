using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Formulas;
using DynamicModules.Domain.Rules;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Domain.Modules;

/// <summary>模組之間的表頭／明細關係與彙總欄位的相依判斷；純函式，只看已載入的 metadata。</summary>
public static class ModuleRelations
{
    /// <summary>啟用中、Relation = Detail 的參照欄位；有它就是明細模組。</summary>
    public static Field? DetailFieldOf(Module m) =>
        m.Blocks.SelectMany(b => b.Fields).FirstOrDefault(f =>
            f is { Type: FieldType.Reference, Status: FieldStatus.Active, Config.Relation: ReferenceRelation.Detail });

    public static bool IsDetail(Module m) => DetailFieldOf(m) is not null;

    /// <summary>明細模組的表頭模組鍵；不是明細模組回 null。</summary>
    public static string? ParentKeyOf(Module m) => DetailFieldOf(m)?.Config.RefTarget?.Key;

    /// <summary>欄位本身是彙總欄位，或是直接、間接引用彙總欄位的公式。</summary>
    public static bool DependsOnRollup(Module m, Field f) => DependsOnRollup(m, f, []);

    private static bool DependsOnRollup(Module m, Field f, HashSet<long> seen)
    {
        if (f.Type == FieldType.Rollup) return true;
        if (f.Type != FieldType.Formula || !seen.Add(f.Id)) return false;
        var byId = m.Blocks.SelectMany(b => b.Fields).ToDictionary(x => x.Id);
        return FormulaGraph.RefsOf(f).Any(id => byId.TryGetValue(id, out var dep) && DependsOnRollup(m, dep, seen));
    }

    /// <summary>彙總設定引用的來源模組欄位：來源參照欄位、彙總對象、條件裡的欄位。</summary>
    public static IReadOnlySet<long> RollupRefsOf(Field rollup)
    {
        var ids = new HashSet<long>();
        if (rollup.Config.Rollup is not { } cfg) return ids;
        ids.Add(cfg.SourceFieldId);
        if (cfg.AggregateFieldId is { } agg) ids.Add(agg);
        if (cfg.Condition is { } cond) ids.UnionWith(RuleReferences.ConditionFieldIdsOf(cond));
        return ids;
    }

    /// <summary>RollupRefsOf 再加上經由公式間接引用的來源模組欄位（遞移閉包）；source 是彙總的來源模組。</summary>
    public static IReadOnlySet<long> RollupDependenciesOf(Module source, Field rollup)
    {
        var byId = source.Blocks.SelectMany(b => b.Fields).ToDictionary(x => x.Id);
        var ids = new HashSet<long>();
        var pending = new Stack<long>(RollupRefsOf(rollup));
        while (pending.TryPop(out var id))
            if (ids.Add(id) && byId.TryGetValue(id, out var f) && f.Type == FieldType.Formula)
                foreach (var dep in FormulaGraph.RefsOf(f)) pending.Push(dep);
        return ids;
    }
}
