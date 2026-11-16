using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Application.Dynamic.EventService;

/// <summary>事件循環判定（模組層級、偏保守，設計 §4.6）。節點是事件，邊是「A 的某個效果可能觸發 B」。</summary>
public static class EventGraph
{
    /// <summary>事件圖的節點資訊：來源模組鍵與每個效果可能寫入的 (模組鍵, 新增或更新, 更新時設了哪些欄位, 是否動到狀態欄位)。</summary>
    public sealed record Node(string Key, string SourceModuleKey, EventTrigger Trigger, List<Write> Writes);

    public sealed record Write(string ModuleKey, bool Create, HashSet<long> SetFields, bool TouchesStatus);

    /// <summary>A 的寫入是否可能觸發 B。</summary>
    public static bool MayTrigger(Write w, Node b)
    {
        if (b.SourceModuleKey != w.ModuleKey) return false;
        return b.Trigger.On switch
        {
            "create" => w.Create,
            // 更新才會觸發 update；有 watch 時，只有寫入的欄位與 watch 有交集才可能觸發
            "update" => !w.Create && (b.Trigger.Watch is not { Count: > 0 } || w.SetFields.Overlaps(b.Trigger.Watch)),
            // 狀態轉移：更新時動到狀態欄位才可能觸發
            "transition" => !w.Create && w.TouchesStatus,
            _ => false
        };
    }

    /// <summary>找出包含 start 的環，回傳環上事件鍵（頭尾相接，第一個與最後一個都是 start）；沒有環回 null。</summary>
    public static List<string>? FindCycleThrough(string start, IReadOnlyCollection<Node> nodes)
    {
        var byKey = nodes.ToDictionary(n => n.Key);
        if (!byKey.TryGetValue(start, out var origin)) return null;
        var path = new List<string> { start };
        var dead = new HashSet<string>();
        return Dfs(origin);

        List<string>? Dfs(Node node)
        {
            foreach (var next in nodes.Where(n => node.Writes.Any(w => MayTrigger(w, n))))
            {
                if (next.Key == start) return [.. path, start];
                if (path.Contains(next.Key) || dead.Contains(next.Key)) continue;
                path.Add(next.Key);
                if (Dfs(next) is { } found) return found;
                path.RemoveAt(path.Count - 1);
                dead.Add(next.Key);
            }
            return null;
        }
    }
}
