using DynamicModules.Application.Dynamic.AppService.Data;

namespace DynamicModules.Application.Dynamic.AppService;

/// <summary>
/// 內建套件的目錄層靜態檢查（整合設計 §3.3、§8.1）：套件彼此對帳整合引用的夥伴欄位，
/// 並把所有內建事件（含整合事件）放進同一張模組層級的圖找循環。匯入的套件夥伴未知，只能在安裝時擋。
/// </summary>
public static class AppCatalogChecks
{
    public static List<string> Run(IReadOnlyList<AppPackageData> packages) => [.. PartnerSymbols(packages), .. EventCycles(packages)];

    /// <summary>整合事件寫入或讀取的夥伴欄位（&lt;appId&gt;/模組.欄位）要在夥伴套件裡找得到；夥伴不在目錄就略過。</summary>
    private static IEnumerable<string> PartnerSymbols(IReadOnlyList<AppPackageData> packages)
    {
        var byId = packages.ToDictionary(p => p.Manifest.Id);
        foreach (var owner in packages)
            foreach (var integration in owner.Integrations)
                foreach (var symbol in Symbols(integration).Distinct())
                {
                    if (AppPackageValidator.SplitPartner(symbol) is not { } split || !byId.TryGetValue(split.App, out var partner)) continue;
                    var qualified = split.Remainder;
                    var dot = qualified.IndexOf('.');
                    var module = dot < 0 ? qualified : qualified[..dot];
                    var field = dot < 0 ? null : qualified[(dot + 1)..];
                    var found = partner.Modules.FirstOrDefault(m => m.Code == module);
                    if (found is null) yield return $"{owner.Manifest.Id} 的整合 {integration.Code} 引用了 {split.App} 沒有的模組：{symbol}";
                    else if (field is not null && !found.Blocks.SelectMany(b => b.Fields).Any(f => f.Code == field))
                        yield return $"{owner.Manifest.Id} 的整合 {integration.Code} 引用了 {split.App} 沒有的欄位：{symbol}";
                }
    }

    private static IEnumerable<string> Symbols(AppIntegrationDecl integration)
    {
        foreach (var e in integration.Events)
            foreach (var effect in e.Effects)
            {
                if (effect.Target is not null) yield return effect.Target;
                foreach (var set in effect.Set)
                {
                    yield return set.To;
                    if (set.From is { } from) yield return from.Field;
                }
            }
    }

    private sealed record Node(string Key, string Source, string On, List<(string Module, bool Create, bool Update)> Writes);

    /// <summary>A 的某個效果寫入的模組正好是 B 的來源模組、且寫入的種類可能觸發 B 的觸發型態，就連一條邊（偏保守，不看 watch）。</summary>
    private static IEnumerable<string> EventCycles(IReadOnlyList<AppPackageData> packages)
    {
        var nodes = new List<Node>();
        foreach (var p in packages)
        {
            string Module(string code) => code.Contains('/') ? code : $"{p.Manifest.Id}/{code}";
            foreach (var e in p.Events.Concat(p.Integrations.SelectMany(i => i.Events)))
                nodes.Add(new Node($"{p.Manifest.Id}:{e.Code}", Module(e.Source), e.Trigger.On,
                    [.. e.Effects.Where(x => x.Target is not null).Select(x => (Module(x.Target!), x.Kind is "create" or "reverse", x.Kind != "create"))]));
        }
        bool Triggers((string Module, bool Create, bool Update) w, Node b) =>
            b.Source == w.Module && (b.On == "create" ? w.Create : w.Update);

        foreach (var start in nodes)
        {
            var path = new List<string> { start.Key };
            var dead = new HashSet<string>();
            if (Dfs(start) is { } cycle) { yield return $"內建套件的連動事件形成循環：{string.Join(" → ", cycle)}"; yield break; }

            List<string>? Dfs(Node node)
            {
                foreach (var next in nodes.Where(n => node.Writes.Any(w => Triggers(w, n))))
                {
                    if (next.Key == start.Key) return [.. path, start.Key];
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
}
