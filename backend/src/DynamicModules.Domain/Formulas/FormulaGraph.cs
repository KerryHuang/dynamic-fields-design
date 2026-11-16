using System.Text.RegularExpressions;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Formulas;

/// <summary>公式欄位之間的相依關係；邊以公式文字中的 {id} 抽取，運算式有誤時仍能判斷循環。</summary>
public static partial class FormulaGraph
{
    [GeneratedRegex(@"\{(\d+)\}")]
    private static partial Regex RefPattern();

    /// <summary>啟用中的公式欄位，被引用者在前；同層維持模組內的原順序。</summary>
    public static IReadOnlyList<Field> EvaluationOrder(Module module)
    {
        var formulas = ActiveFormulas(module);
        var byId = formulas.ToDictionary(f => f.Id);
        var edges = formulas.ToDictionary(f => f.Id, f => RefsOf(f).Where(byId.ContainsKey).ToList());
        var ordered = new List<Field>();
        var state = new Dictionary<long, bool>(); // false = 進行中，true = 完成

        void Visit(long id)
        {
            if (state.ContainsKey(id)) return; // 已完成或循環中，略過避免無窮遞迴
            state[id] = false;
            foreach (var dep in edges[id]) Visit(dep);
            state[id] = true;
            ordered.Add(byId[id]);
        }

        foreach (var f in formulas) Visit(f.Id);
        return ordered;
    }

    /// <summary>假設 fieldId 改成引用 newRefs，是否形成循環。</summary>
    public static bool HasCycle(Module module, long fieldId, IReadOnlySet<long> newRefs)
    {
        var edges = ActiveFormulas(module).Where(f => f.Id != fieldId).ToDictionary(f => f.Id, RefsOf);
        var seen = new HashSet<long>();
        var stack = new Stack<long>(newRefs);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (id == fieldId) return true;
            if (!seen.Add(id) || !edges.TryGetValue(id, out var next)) continue;
            foreach (var n in next) stack.Push(n);
        }
        return false;
    }

    private static List<Field> ActiveFormulas(Module module) =>
        module.Blocks.OrderBy(b => b.Sequence).SelectMany(b => b.Fields.OrderBy(f => f.Sequence))
            .Where(f => f.Type == FieldType.Formula && f.Status == FieldStatus.Active).ToList();

    /// <summary>公式文字中以 {id} 引用的欄位 Id。</summary>
    public static HashSet<long> RefsOf(Field f) =>
        RefPattern().Matches(f.Config.Formula ?? "").Select(m => long.Parse(m.Groups[1].Value)).ToHashSet();
}
