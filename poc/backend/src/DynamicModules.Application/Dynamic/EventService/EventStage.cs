using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Behaviors;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Domain.Conditions;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.Rules;

namespace DynamicModules.Application.Dynamic.EventService;

/// <summary>
/// 寫入後的事件階段（設計 §2 步驟 8、§4.5、§4.6）：依寫入日誌分輪處理。第 0 輪是 handler 結束時日誌裡的項目，
/// 效果寫入的記錄是下一輪的輸入；輪數就是深度，上限 5。同一輪內同一筆記錄的多個項目合併，跨輪不合併。
/// 彙總重算寫入的父記錄（靜默）不觸發。
/// </summary>
public class EventStage(
    IWriteJournal journal, IEventRepository events, IModuleRepository modules, FieldTypeRegistry registry, IBusinessTime time,
    EffectApplier applier) : IWriteStage
{
    public const int MaxDepth = 5;
    public const string TooDeep = "連動事件串聯過深（上限 5 層）";
    public const string Loop = "連動事件形成循環";

    public int Order => 8;

    public async Task<ServiceResult?> RunAsync(CancellationToken ct)
    {
        var cursor = 0;
        var round = 0;
        var visited = new HashSet<string>();
        var eventsByModule = new Dictionary<long, List<ModuleEvent>>();
        var modulesById = new Dictionary<long, Module?>();

        while (journal.Entries.Count > cursor)
        {
            var end = journal.Entries.Count;
            var slice = journal.Entries.Skip(cursor).Take(end - cursor).ToList();
            cursor = end;

            foreach (var entry in JournalView.Merge(slice))
            {
                if (entry.Silent || entry.AfterDeleted) continue;
                if (!eventsByModule.TryGetValue(entry.ModuleId, out var list))
                    eventsByModule[entry.ModuleId] = list = await events.ListActiveBySourceAsync(entry.ModuleId, ct);
                if (list.Count == 0) continue;
                if (!modulesById.TryGetValue(entry.ModuleId, out var module))
                    modulesById[entry.ModuleId] = module = await modules.GetByIdAsync(entry.ModuleId, includeChildren: true, ct);
                if (module is null) continue;

                var after = RecordValueProcessor.Parse(entry.AfterValues);
                var before = entry.BeforeValues is null || entry.Kind == JournalKind.Added ? null : RecordValueProcessor.Parse(entry.BeforeValues);
                foreach (var ev in list)
                {
                    if (!Triggers(ev, module, before, after, entry.Kind)) continue;
                    if (await WhenHoldsAsync(ev, module, after, ct) is false) continue;
                    if (ev.Definition.Effects.Count == 0) continue;

                    if (round >= MaxDepth) return ServiceResult.Conflict(TooDeep);
                    if (!visited.Add($"{ev.Key}|{entry.RecordId}|{ev.Definition.Trigger.On}")) return ServiceResult.Conflict(Loop);
                    if (await applier.ApplyAsync(new EffectRun(ev, module, entry.RecordId, entry.OwnerUserId, after, round + 1), ct) is { } failure)
                        return failure;
                }
            }
            round++;
        }
        return null;
    }

    internal static bool Triggers(
        ModuleEvent ev, Module module, IReadOnlyDictionary<string, JsonElement?>? before, IReadOnlyDictionary<string, JsonElement?> after, JournalKind kind)
    {
        var t = ev.Definition.Trigger;
        switch (t.On)
        {
            case "create":
                return kind == JournalKind.Added;
            case "update":
                if (kind != JournalKind.Modified || before is null) return false;
                var watched = (t.Watch ?? []).Select(id => $"f_{id}").ToList();
                return watched.Count == 0
                    ? Keys(before, after).Any(k => Differs(before, after, k))
                    : watched.Any(k => Differs(before, after, k));
            case "transition":
                if (kind != JournalKind.Modified || before is null || module.Workflow is not { } wf) return false;
                var from = wf.StateOf(before) ?? wf.InitialOptionId;
                var to = wf.StateOf(after);
                return to is not null && to != from && to == t.ToState
                       && (t.FromStates is not { Count: > 0 } || t.FromStates.Contains(from));
            default:
                return false;
        }
    }

    private static IEnumerable<string> Keys(IReadOnlyDictionary<string, JsonElement?> a, IReadOnlyDictionary<string, JsonElement?> b) =>
        a.Keys.Union(b.Keys);

    private static bool Differs(IReadOnlyDictionary<string, JsonElement?> a, IReadOnlyDictionary<string, JsonElement?> b, string key) =>
        Raw(a, key) != Raw(b, key);

    private static string? Raw(IReadOnlyDictionary<string, JsonElement?> d, string key) =>
        d.TryGetValue(key, out var v) && v is { ValueKind: not JsonValueKind.Null } e ? e.GetRawText() : null;

    private async Task<bool?> WhenHoldsAsync(ModuleEvent ev, Module module, IReadOnlyDictionary<string, JsonElement?> after, CancellationToken ct)
    {
        if (ev.Definition.When is not { ValueKind: JsonValueKind.Object } when) return true;
        var parsed = ConditionParser.Parse(when, module, registry, ConditionUsage.Rule, await modules.ListUserIdsAsync(ct));
        return parsed.Node is null ? false : ConditionEvaluator.Evaluate(parsed.Node, after, registry, time.Today);
    }
}
