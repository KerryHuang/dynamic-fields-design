using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Behaviors;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.EventService;

/// <summary>
/// 事實鎖的後盾（設計 §2 步驟 9、§4.7）：請求開始前就有事實的來源記錄，除狀態欄位與彙總之外的值被改（含公式重算、連帶清空）或被軟刪除，
/// 整筆回滾 409。本次請求才剛記帳的來源不誤擋（事實的 OperationId 就是本次請求）。不依賴每個 handler 都記得擋。
/// </summary>
public class FactBackstopStage(IWriteJournal journal, IEventFactRepository facts, IModuleRepository modules, IOperationContext operation) : IWriteStage
{
    public int Order => 9;

    public async Task<ServiceResult?> RunAsync(CancellationToken ct)
    {
        var candidates = JournalView.Merge(journal.Entries).Where(e => e.Kind == JournalKind.Modified && e.BeforeValues is not null).ToList();
        if (candidates.Count == 0) return null;
        var frozen = await facts.ListFrozenBeforeAsync(candidates.Select(c => c.RecordId).ToList(), operation.OperationId, ct);
        foreach (var entry in candidates.Where(c => frozen.Contains(c.RecordId)))
        {
            var message = $"記錄 {entry.RecordId} 已經記帳（產生過連動記錄），這次請求改動了它的內容，已取消";
            if (entry.AfterDeleted) return ServiceResult.Conflict(message);
            var module = await modules.GetByIdAsync(entry.ModuleId, includeChildren: true, ct);
            if (module is null) continue;
            var skip = module.Blocks.SelectMany(b => b.Fields)
                .Where(f => f.Id == module.Workflow?.StatusFieldId || f.Type == FieldType.Rollup
                            || f.Type == FieldType.Formula && ModuleRelations.DependsOnRollup(module, f))
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
