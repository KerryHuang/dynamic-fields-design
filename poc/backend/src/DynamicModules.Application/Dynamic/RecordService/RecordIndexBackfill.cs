using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.RecordService;

/// <summary>重建單一欄位在模組內未刪除記錄的索引列（欄位不需索引時即清空）。</summary>
public static class RecordIndexBackfill
{
    public static async Task RebuildAsync(
        IRecordRepository records, Module module, Field field, FieldTypeRegistry registry, CancellationToken ct)
    {
        var rows = new List<RecordIndex>();
        if (RecordIndexer.ShouldIndex(field))
        {
            foreach (var record in (await records.ListAllByModuleAsync(module.Id, ct)).Where(r => !r.DeleteFlag))
            {
                var values = RecordValueProcessor.Parse(record.ValuesJson);
                foreach (var row in RecordIndexer.BuildField(field, values, registry))
                {
                    row.RecordId = record.Id;
                    rows.Add(row);
                }
            }
        }
        await records.ReplaceFieldIndexAsync(field.Id, rows, ct);
    }
}
