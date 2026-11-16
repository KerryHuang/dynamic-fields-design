using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Behaviors;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.EventService;

/// <summary>
/// 事實鎖的後盾（設計 §2 步驟 9、§4.7）：請求開始前就有事實的來源記錄，除狀態欄位與彙總之外的值被改（含公式重算、連帶清空）或被軟刪除，
/// 整筆回滾 409。本次請求才剛記帳的來源不誤擋（以本請求自己加入的事實判斷，不看 OperationId：Idempotency-Key 是客戶端給的，別的請求可以重用）。不依賴每個 handler 都記得擋。
/// </summary>
public class FactBackstopStage(IWriteJournal journal, IEventFactRepository facts, IModuleRepository modules) : IWriteStage
{
    public int Order => 9;

    public async Task<ServiceResult?> RunAsync(CancellationToken ct)
    {
        var candidates = JournalView.Merge(journal.Entries).Where(e => e.Kind == JournalKind.Modified && e.BeforeValues is not null).ToList();
        if (candidates.Count == 0) return null;

        // 明細列自己沒有事實，鎖在表頭上（設計 §4.7）：把明細的表頭 Id 一併拿去查
        var modulesById = new Dictionary<long, Module?>();
        var headerOf = new Dictionary<long, long>();
        foreach (var c in candidates)
        {
            if (!modulesById.TryGetValue(c.ModuleId, out var m)) modulesById[c.ModuleId] = m = await modules.GetByIdAsync(c.ModuleId, includeChildren: true, ct);
            if (m is not null && ModuleRelations.DetailFieldOf(m) is { } parentField
                && RecordValueProcessor.Parse(c.BeforeValues!).TryGetValue(parentField.Key, out var raw)
                && raw is { ValueKind: System.Text.Json.JsonValueKind.Number } n && n.TryGetInt64(out var headerId))
                headerOf[c.RecordId] = headerId;
        }
        var frozen = await facts.ListFrozenBeforeAsync(candidates.Select(c => c.RecordId).Concat(headerOf.Values).Distinct().ToList(), ct);
        foreach (var entry in candidates)
        {
            var viaHeader = headerOf.TryGetValue(entry.RecordId, out var header) && frozen.Contains(header);
            if (!frozen.Contains(entry.RecordId) && !viaHeader) continue;
            var message = $"記錄 {entry.RecordId} 已經記帳（產生過連動記錄），這次請求改動了它的內容，已取消";
            if (entry.AfterDeleted) return ServiceResult.Conflict(message);
            var module = modulesById[entry.ModuleId];
            if (module is null) continue;
            // 狀態欄位與彙總照常可變；公式不在豁免內：已記帳的來源公式保留記帳當時的值（§4.7），不隨彙總重算
            var skip = module.Blocks.SelectMany(b => b.Fields)
                .Where(f => f.Id == module.Workflow?.StatusFieldId || f.Type == FieldType.Rollup)
                .Select(f => f.Key).ToHashSet();
            var before = RecordValueProcessor.Parse(entry.BeforeValues!);
            var after = RecordValueProcessor.Parse(entry.AfterValues);
            if (before.Keys.Union(after.Keys).Where(k => !skip.Contains(k)).Any(k => Raw(before, k) != Raw(after, k)))
                return ServiceResult.Conflict(message);
        }
        return null;
    }

    private static string? Raw(IReadOnlyDictionary<string, System.Text.Json.JsonElement?> d, string key) =>
        d.TryGetValue(key, out var v) && v is { ValueKind: not System.Text.Json.JsonValueKind.Null } e ? e.GetRawText() : null;
}
