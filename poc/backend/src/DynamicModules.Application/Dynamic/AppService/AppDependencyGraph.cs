namespace DynamicModules.Application.Dynamic.AppService;

/// <summary>應用程式之間 requires 的循環檢查：A 要求 B、B 要求 A 永遠開不起來。</summary>
public static class AppDependencyGraph
{
    /// <summary>一份套件的相依邊：requires.apps 加上 requires.masters（主檔之間、主檔與應用程式之間都不得循環）。</summary>
    public static IReadOnlyList<string> RequiresOf(Data.AppManifestRequires requires) =>
        [.. requires.Apps.Select(a => a.Id), .. requires.Masters.Select(m => m.Id)];

    /// <summary>已開啟的應用程式記下的相依邊，同 <see cref="RequiresOf(Data.AppManifestRequires)"/>。</summary>
    public static IReadOnlyList<string> RequiresOf(DynamicModules.Domain.Entities.Dynamic.AppRequires requires) =>
        [.. requires.Apps.Select(a => a.Id), .. requires.Masters.Select(m => m.Id)];

    /// <summary>回傳第一個找到的循環（如 a → b → a）；沒有循環回 null。邊指向圖外的節點忽略。</summary>
    public static IReadOnlyList<string>? FindCycle(IEnumerable<(string Id, IReadOnlyList<string> Requires)> nodes)
    {
        var edges = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var (id, requires) in nodes) edges[id] = requires;
        var state = new Dictionary<string, int>(); // 1 = 走訪中，2 = 完成
        var stack = new List<string>();

        IReadOnlyList<string>? Visit(string id)
        {
            state[id] = 1;
            stack.Add(id);
            foreach (var next in edges[id].Where(edges.ContainsKey))
            {
                if (state.GetValueOrDefault(next) == 1) return [.. stack.SkipWhile(s => s != next), next];
                if (state.GetValueOrDefault(next) == 0 && Visit(next) is { } cycle) return cycle;
            }
            stack.RemoveAt(stack.Count - 1);
            state[id] = 2;
            return null;
        }

        foreach (var id in edges.Keys.Order(StringComparer.Ordinal))
            if (state.GetValueOrDefault(id) == 0 && Visit(id) is { } found) return found;
        return null;
    }
}
