using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Application.Dynamic.Rollups;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.FieldService;

/// <summary>公式新增或變更後，對模組內每筆未刪除記錄重算公式、重建索引並換新 RowVersion；
/// 之後以這個模組為來源的彙總跟著重算（決策 8）。</summary>
public static class FormulaRecalc
{
    public static async Task RecalculateAsync(
        Module module, IRecordRepository records, FieldTypeRegistry registry, RollupRecalc rollups, DateOnly today, CancellationToken ct)
    {
        foreach (var record in (await records.ListAllByModuleAsync(module.Id, ct)).Where(r => !r.DeleteFlag))
        {
            var values = RecordValueProcessor.Parse(record.ValuesJson);
            RecordValueProcessor.ApplyFormulas(module, values, today);
            record.ValuesJson = RecordValueProcessor.Serialize(values);
            record.RowVersion = Guid.NewGuid().ToString();
            await records.ReplaceIndexAsync(record, RecordIndexer.Build(module, values, registry), ct);
        }
        await rollups.RecalculateForSourceModuleAsync(module, today, ct);
    }
}
