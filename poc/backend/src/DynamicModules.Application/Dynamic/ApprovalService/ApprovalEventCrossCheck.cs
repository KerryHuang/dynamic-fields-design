using System.Text.Json;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Application.Dynamic.ApprovalService;

/// <summary>事件不能推動需簽核的轉移（設計 §5.3）：定義時的交叉檢查。</summary>
public static class ApprovalEventCrossCheck
{
    /// <summary>事件的 update 效果把目標模組的狀態欄位 set 成常數 opt_S，而 S 有需簽核的入口 → 回傳 (EventKey, S)。</summary>
    public static IReadOnlyList<(string EventKey, long To)> Find(Module target, Workflow wf, IEnumerable<ModuleEvent> events)
    {
        var targets = ApprovalTopology.ApprovalTargets(wf);
        if (targets.Count == 0) return [];
        var hits = new List<(string, long)>();
        foreach (var ev in events)
            foreach (var fx in ev.Definition.Effects.Where(fx => fx.Kind == "update" && fx.TargetModuleKey == target.Key))
                foreach (var set in fx.Set ?? [])
                    if (set.To == wf.StatusFieldId && OptionOf(set.Const) is { } to && targets.Contains(to) && !hits.Contains((ev.Key, to)))
                        hits.Add((ev.Key, to));
        return hits;
    }

    /// <summary>單一事件是否會推動任何目標模組的需簽核轉移（逐一載入 update 效果的目標模組）。</summary>
    public static async Task<bool> PushesAsync(ModuleEvent ev, Domain.Repositories.Dynamic.IModuleRepository modules, CancellationToken ct)
    {
        foreach (var key in ev.Definition.Effects.Where(fx => fx.Kind == "update").Select(fx => fx.TargetModuleKey).OfType<string>().Distinct())
            if (await modules.GetByKeyAsync(key, includeChildren: true, ct) is { Workflow: { } wf } target && Find(target, wf, [ev]).Count > 0)
                return true;
        return false;
    }

    private static long? OptionOf(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.String } s && s.GetString() is { } text
        && text.StartsWith("opt_", StringComparison.Ordinal) && long.TryParse(text.AsSpan(4), out var id) ? id : null;
}
