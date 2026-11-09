using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Application.Dynamic.Rollups;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.FieldService;

/// <summary>公式新增或變更後，對模組內每筆未刪除、未固定的記錄重算公式、重建索引並換新 RowVersion；
/// 之後以這個模組為來源的彙總跟著重算（決策 8）。</summary>
public static class FormulaRecalc
{
    public static async Task RecalculateAsync(
        Module module, IRecordRepository records, FieldTypeRegistry registry, RollupRecalc rollups, IEventFactRepository facts,
        DateOnly today, CancellationToken ct, Common.Security.FieldLocks locks)
    {
        var frozen = await FrozenRecords.LoadAsync(module, facts, ct);   // 已固定的記錄保留原值（設計 §10.4）
        var locked = await locks.FrozenAsync(module, ct);                 // 凍結的貢獻欄位不重算（共用主檔設計 §7）
        foreach (var record in (await records.ListAllByModuleAsync(module.Id, ct)).Where(r => !r.DeleteFlag && !frozen.Contains(r)))
        {
            var values = RecordValueProcessor.Parse(record.ValuesJson);
            RecordValueProcessor.ApplyFormulas(module, values, today, locked);
            record.ValuesJson = RecordValueProcessor.Serialize(values);
            record.RowVersion = Guid.NewGuid().ToString();
            await records.ReplaceIndexAsync(record, RecordIndexer.Build(module, values, registry), ct);
        }
        await rollups.RecalculateForSourceModuleAsync(module, today, ct);
    }
}
