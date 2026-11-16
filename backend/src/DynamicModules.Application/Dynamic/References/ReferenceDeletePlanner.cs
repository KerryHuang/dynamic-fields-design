using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.References;

/// <summary>Title 由 handler 依使用者權限決定填或 null；規劃器一律給 null。</summary>
public record BlockingRef(string ModuleKey, I18nText ModuleName, I18nText FieldLabel, long RecordId, string? Title);

public record DeletePlan(
    IReadOnlyList<Record> ToDelete, IReadOnlyList<(Record Record, Field Field)> ToClear,
    IReadOnlyList<BlockingRef> Blocking, int BlockingTotal, bool DepthExceeded);

/// <summary>409 的額外資料，WebAPI 展開成 { message, references, total }。</summary>
public record DeleteBlockedResponse(IReadOnlyList<BlockingRef> References, int Total);

/// <summary>刪除被參照記錄前，在記憶體裡規劃整棵連帶刪除樹；不寫入任何東西。</summary>
public class ReferenceDeletePlanner(IModuleRepository modules, IRecordRepository records)
{
    /// <summary>被刪記錄是第 0 層；連帶刪除到第 6 層就拒絕。</summary>
    public const int MaxDepth = 5;

    /// <summary>
    /// BFS：Restrict 來源進 Blocking、SetNull 來源進 ToClear、Cascade 來源進 ToDelete 再往下展開。
    /// visited（根與已排入刪除的記錄）防循環與自我參照；來源不套資料範圍、跨所有模組（含未啟用）。
    /// </summary>
    public async Task<DeletePlan> PlanAsync(Record root, Module rootModule, CancellationToken ct)
    {
        var incoming = new Dictionary<string, IReadOnlyList<(Module Source, Field Field)>>();
        var visited = new HashSet<long> { root.Id };
        var toDelete = new List<Record>();
        var toClear = new List<(Record, Field)>();
        var blocking = new List<BlockingRef>();
        var depthExceeded = false;
        var queue = new Queue<(Record Record, string ModuleKey, int Depth)>();
        queue.Enqueue((root, rootModule.Key, 0));

        while (!depthExceeded && queue.TryDequeue(out var item))
        {
            if (!incoming.TryGetValue(item.ModuleKey, out var fields))
                incoming[item.ModuleKey] = fields = await modules.ListIncomingReferenceFieldsAsync(item.ModuleKey, ct);
            foreach (var (source, field) in fields)
            {
                var sources = (await records.ListByRefAsync(field.Id, item.Record.Id.ToString(), ct))
                    .Where(s => !visited.Contains(s.Id)).ToList();
                switch (field.Config.OnDelete ?? ReferenceOnDelete.Restrict)
                {
                    case ReferenceOnDelete.Restrict:
                        blocking.AddRange(sources.Select(s => new BlockingRef(source.Key, source.NameI18n, field.LabelI18n, s.Id, null)));
                        break;
                    case ReferenceOnDelete.SetNull:
                        toClear.AddRange(sources.Select(s => (s, field)));
                        break;
                    case ReferenceOnDelete.Cascade:
                        foreach (var s in sources)
                        {
                            if (item.Depth + 1 > MaxDepth) { depthExceeded = true; break; }
                            visited.Add(s.Id);
                            toDelete.Add(s);
                            queue.Enqueue((s, source.Key, item.Depth + 1));
                        }
                        break;
                }
                if (depthExceeded) break;
            }
        }

        // 先被列為 Restrict 來源、之後才被連帶刪除的記錄不算擋住。
        // 同一來源記錄經多個欄位或多條路徑命中時只算一筆。
        blocking = blocking.Where(b => !visited.Contains(b.RecordId)).DistinctBy(b => (b.ModuleKey, b.RecordId)).ToList();
        return new DeletePlan(toDelete, toClear, blocking, blocking.Count, depthExceeded);
    }
}
