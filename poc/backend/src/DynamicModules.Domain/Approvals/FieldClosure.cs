using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Formulas;
using DynamicModules.Domain.Rules;

namespace DynamicModules.Domain.Approvals;

/// <summary>
/// FieldIds：閉包內的欄位；IncludesOwnDetailRollup：閉包含來源是本模組明細的彙總（明細列因此要一起鎖）；
/// Paths：從某個 seed 走到該欄位的欄位 Id 序列（含頭尾），給錯誤訊息列出經由哪些公式或帶值。
/// </summary>
public sealed record ClosureResult(IReadOnlySet<long> FieldIds, bool IncludesOwnDetailRollup, IReadOnlyDictionary<long, IReadOnlyList<long>> Paths);

/// <summary>簽核條件與簽核人來源引用的欄位，以及它們的上游相依閉包（設計 §9.2、§9.3）；純函式。</summary>
public static class FieldClosure
{
    /// <summary>
    /// 上游閉包：seeds 本身、公式引用（遞迴）、帶值目的欄位對應的參照欄位（Config.Fill）、彙總本身。
    /// excludeFieldId（狀態欄位）不列入也不往下走。
    /// isOwnDetailRollup 判斷彙總的來源是不是本模組的明細；沒給時一律當成是（偏向多鎖，不放行）。
    /// </summary>
    public static ClosureResult Upstream(Module module, IEnumerable<long> seeds, long? excludeFieldId,
        Func<Field, bool>? isOwnDetailRollup = null)
    {
        var fields = module.Blocks.SelectMany(b => b.Fields).Where(f => f.Status == FieldStatus.Active).ToList();
        var byId = fields.ToDictionary(f => f.Id);
        // 目的欄位 → 帶值來源的參照欄位
        var fillSources = fields
            .Where(f => f.Type == FieldType.Reference && f.Config.Fill is not null)
            .SelectMany(r => r.Config.Fill!.Map.Select(m => (m.To, Ref: r.Id)))
            .ToLookup(x => x.To, x => x.Ref);

        var paths = new Dictionary<long, IReadOnlyList<long>>();
        var queue = new Queue<long>();
        foreach (var s in seeds.Distinct())
        {
            if (s == excludeFieldId || paths.ContainsKey(s)) continue;
            paths[s] = [s];
            queue.Enqueue(s);
        }

        var ownDetailRollup = false;
        while (queue.TryDequeue(out var id))
        {
            var next = new List<long>();
            if (byId.TryGetValue(id, out var f))
            {
                if (f.Type == FieldType.Formula) next.AddRange(FormulaGraph.RefsOf(f).Order());
                if (f.Type == FieldType.Rollup && (isOwnDetailRollup?.Invoke(f) ?? true)) ownDetailRollup = true;
            }
            next.AddRange(fillSources[id]);
            foreach (var n in next)
            {
                if (n == excludeFieldId || paths.ContainsKey(n)) continue;
                paths[n] = [.. paths[id], n];
                queue.Enqueue(n);
            }
        }

        return new ClosureResult(paths.Keys.ToHashSet(), ownDetailRollup, paths);
    }

    /// <summary>定義直接引用的欄位：流程條件、各關條件、userField 的欄位（不含可編輯欄位）。</summary>
    public static IEnumerable<long> DirectFields(ApprovalDefinition d)
    {
        var ids = new HashSet<long>();
        if (d.When is { } when) ids.UnionWith(RuleReferences.ConditionFieldIdsOf(when));
        foreach (var s in d.Stages ?? [])
        {
            if (s.When is { } sw) ids.UnionWith(RuleReferences.ConditionFieldIdsOf(sw));
            if (s.Approvers is { Source: ApprovalSources.UserField, FieldId: { } fid }) ids.Add(fid);
        }
        return ids;
    }
}
