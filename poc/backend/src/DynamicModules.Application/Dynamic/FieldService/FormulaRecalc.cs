using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Application.Dynamic.Rollups;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.FieldService;

/// <summary>公式新增或變更後，對模組內每筆未刪除、未固定的記錄重算公式、重建索引並換新 RowVersion；
/// 之後以這個模組為來源的彙總跟著重算（決策 8）。回傳本模組被跳過（已固定）的未刪除記錄數；連帶重算的父模組不計。</summary>
public static class FormulaRecalc
{
    public static async Task<int> RecalculateAsync(
        Module module, IRecordRepository records, FieldTypeRegistry registry, RollupRecalc rollups, FrozenRecordsLoader loader,
        DateOnly today, CancellationToken ct, Common.Security.FieldLocks locks)
    {
        var all = await records.ListAllByModuleAsync(module.Id, ct);
        var frozen = await loader.LoadAsync(module, all, ct);             // 已固定的記錄保留原值（設計 §8.6、§10.4）
        var locked = await locks.FrozenAsync(module, ct);                 // 凍結的貢獻欄位不重算（共用主檔設計 §7）
        var live = all.Where(r => !r.DeleteFlag).ToLookup(frozen.Contains);   // 每列只判定一次
        var preserved = live[true].Count();
        foreach (var record in live[false])
        {
            var values = RecordValueProcessor.Parse(record.ValuesJson);
            RecordValueProcessor.ApplyFormulas(module, values, today, locked);
            record.ValuesJson = RecordValueProcessor.Serialize(values);
            record.RowVersion = Guid.NewGuid().ToString();
            await records.ReplaceIndexAsync(record, RecordIndexer.Build(module, values, registry), ct);
        }
        await rollups.RecalculateForSourceModuleAsync(module, today, ct);
        return preserved;
    }
}
