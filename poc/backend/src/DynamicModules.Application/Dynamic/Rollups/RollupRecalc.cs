using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.Rollups;

namespace DynamicModules.Application.Dynamic.Rollups;

/// <summary>
/// 子記錄寫入後重算彙總父記錄（已定決策 8）：從資料庫讀子記錄算彙總、重跑父記錄公式、重建索引；
/// 值有變才寫入並換 RowVersion，不蓋稽核欄位、不跑規則、不遞迴。呼叫端在同一個交易內。
/// </summary>
public class RollupRecalc(
    IModuleRepository modules, IRecordRepository records, IUnitOfWork uow, FieldTypeRegistry registry, IBusinessTime time, IWriteJournal journal,
    IEventFactRepository facts, Common.Security.FieldLocks locks, FrozenRecordsLoader loader)
{
    /// <summary>寫入前後來源欄位指向的父記錄；改指向時新舊兩筆都算，null 忽略。</summary>
    public static IReadOnlySet<(long ParentModuleId, long ParentRecordId)> CollectParents(
        Module source,
        IReadOnlyDictionary<string, JsonElement?>? before,
        IReadOnlyDictionary<string, JsonElement?>? after,
        IReadOnlyList<(Module Parent, Field Rollup, Field Source)> rollups)
    {
        var result = new HashSet<(long, long)>();
        foreach (var (parent, _, field) in rollups.Where(x => x.Source.ModuleId == source.Id))
            foreach (var values in new[] { before, after })
                if (values is not null && values.TryGetValue(field.Key, out var v)
                    && v is { ValueKind: JsonValueKind.Number } n && n.TryGetInt64(out var id))
                    result.Add((parent.Id, id));
        return result;
    }

    /// <summary>handler 寫入後統一呼叫：先提交讓查詢看得到新資料，再重算受影響的父記錄並提交。</summary>
    public async Task AfterWriteAsync(
        Module source,
        IEnumerable<(IReadOnlyDictionary<string, JsonElement?>? Before, IReadOnlyDictionary<string, JsonElement?>? After)> changes,
        IReadOnlySet<long> skipRecordIds, CancellationToken ct)
    {
        await uow.CommitAsync(ct);
        var rollups = await modules.ListRollupsBySourceModuleAsync(source.Id, ct);
        if (rollups.Count == 0) return;
        var parents = changes.SelectMany(c => CollectParents(source, c.Before, c.After, rollups)).ToHashSet();
        await RecalculateAsync(parents, skipRecordIds, time.Today, ct);
        await uow.CommitAsync(ct);
    }

    /// <summary>
    /// 表頭存檔後：表頭自己的參照與每個明細模組的列各走一次 AfterWriteAsync。
    /// 這張表頭的明細彙總已在記憶體算過（決策 4 第 4 步），所以跳過它；明細列其他參照指向的父記錄照常重算。
    /// </summary>
    public async Task AfterHeaderSaveAsync(
        Module header, Record headerRecord, IReadOnlyDictionary<string, JsonElement?>? before,
        IReadOnlyDictionary<string, JsonElement?> after, DetailPlan plan, CancellationToken ct)
    {
        var skip = new HashSet<long> { headerRecord.Id };
        await AfterWriteAsync(header, [(before, after)], skip, ct);
        foreach (var set in plan.Sets.Where(s => s.Changes.Count > 0))
            await AfterWriteAsync(set.Child, set.Changes.Select(c => (c.Before, (IReadOnlyDictionary<string, JsonElement?>?)c.After)), skip, ct);
    }

    /// <summary>重算指定父記錄；skipRecordIds 與已刪除的記錄不動。呼叫端負責 CommitAsync。</summary>
    public async Task RecalculateAsync(
        IEnumerable<(long ParentModuleId, long ParentRecordId)> parents, IReadOnlySet<long> skipRecordIds, DateOnly today,
        CancellationToken ct)
    {
        var userIds = await modules.ListUserIdsAsync(ct);
        foreach (var group in parents.Where(p => !skipRecordIds.Contains(p.ParentRecordId)).GroupBy(p => p.ParentModuleId))
        {
            var parent = (await modules.GetByIdAsync(group.Key, includeChildren: true, ct))!;
            var sources = await SourcesOfAsync(parent, ct);
            foreach (var (_, recordId) in group.Distinct())
                if (await records.GetAsync(recordId, ct) is { } record && record.ModuleId == parent.Id)
                    await RecomputeAsync(parent, record, sources, userIds, today, ct);
        }
    }

    /// <summary>重算一個模組全部未刪除記錄的彙總（彙總欄位新增、修改、重新啟用時）。</summary>
    public async Task RecalculateModuleAsync(Module parent, DateOnly today, CancellationToken ct)
    {
        await uow.CommitAsync(ct);
        var userIds = await modules.ListUserIdsAsync(ct);
        var sources = await SourcesOfAsync(parent, ct);
        var all = await records.ListAllByModuleAsync(parent.Id, ct);
        var frozen = await loader.LoadAsync(parent, all, ct);   // 已固定的記錄不隨定義變更重算（設計 §8.6、§10.4）
        foreach (var record in all.Where(r => !r.DeleteFlag && !frozen.Contains(r)))
            await RecomputeAsync(parent, record, sources, userIds, today, ct);
    }

    /// <summary>source 的值整批變了（公式重算）：以它為來源的彙總所在模組全部重算。</summary>
    public async Task RecalculateForSourceModuleAsync(Module source, DateOnly today, CancellationToken ct)
    {
        await uow.CommitAsync(ct);
        foreach (var parentId in (await modules.ListRollupsBySourceModuleAsync(source.Id, ct)).Select(x => x.Parent.Id).Distinct())
            await RecalculateModuleAsync((await modules.GetByIdAsync(parentId, includeChildren: true, ct))!, today, ct);
    }

    /// <summary>新記錄還沒有子記錄：每個啟用中彙總欄位的空集合結果（決策 7：sum、count 得 0，min、max 得 null）。</summary>
    public async Task<Dictionary<string, JsonElement?>> EmptyValuesAsync(Module module, DateOnly today, CancellationToken ct)
    {
        var userIds = await modules.ListUserIdsAsync(ct);
        return (await SourcesOfAsync(module, ct)).ToDictionary(
            x => x.Rollup.Key, x => RollupCalculator.Compute(x.Rollup, x.SourceModule, [], today, userIds));
    }

    /// <summary>父模組每個啟用中彙總欄位，配上來源參照欄位與來源模組。</summary>
    private async Task<List<(Field Rollup, Field Source, Module SourceModule)>> SourcesOfAsync(Module parent, CancellationToken ct)
    {
        var result = new List<(Field, Field, Module)>();
        foreach (var rollup in parent.Blocks.SelectMany(b => b.Fields)
                     .Where(f => f is { Type: FieldType.Rollup, Status: FieldStatus.Active, Config.Rollup: not null }))
        {
            var sourceModule = await modules.GetByFieldIdAsync(rollup.Config.Rollup!.SourceFieldId, ct);
            var sourceField = sourceModule?.Blocks.SelectMany(b => b.Fields).FirstOrDefault(f => f.Id == rollup.Config.Rollup.SourceFieldId);
            if (sourceField is not null) result.Add((rollup, sourceField, sourceModule!));
        }
        return result;
    }

    private async Task RecomputeAsync(
        Module parent, Record record, List<(Field Rollup, Field Source, Module SourceModule)> sources, IReadOnlySet<string> userIds,
        DateOnly today, CancellationToken ct)
    {
        var values = RecordValueProcessor.Parse(record.ValuesJson);
        foreach (var (rollup, source, sourceModule) in sources)
        {
            var children = (await records.ListByRefAsync(source.Id, record.Id.ToString(), ct))
                .Select(c => (IReadOnlyDictionary<string, JsonElement?>)RecordValueProcessor.Parse(c.ValuesJson));
            if (RollupCalculator.Compute(rollup, sourceModule, children, today, userIds) is { } v) values[rollup.Key] = v;
            else values.Remove(rollup.Key);
        }
        // 已記帳的來源（有事件事實）公式保留記帳當時的值，彙總本身照常更新（設計 §4.7、§10.4）
        var previous = RecordValueProcessor.Parse(record.ValuesJson);
        // 凍結的公式求值時就跳過（同 FormulaRecalc），依賴它的公式才會用保留的值；先算再復原會留下用暫算值算出的結果（審查必修 10）
        var frozen = await locks.FrozenAsync(parent, ct);
        if (!await facts.AnyBySourceAsync(record.Id, ct)) RecordValueProcessor.ApplyFormulas(parent, values, today, frozen);
        Common.Security.FieldLocks.Restore(parent, frozen, previous, values);   // 生命週期鎖第二道：凍結的貢獻欄位保持最後的值
        var json = RecordValueProcessor.Serialize(values);
        if (json == record.ValuesJson) return;
        record.ValuesJson = json;
        record.RowVersion = Guid.NewGuid().ToString();
        journal.MarkSilent(record);   // 彙總重算的寫入不觸發事件（設計 §4.5），但仍列入驗證
        await records.ReplaceIndexAsync(record, RecordIndexer.Build(parent, values, registry), ct);
    }
}
