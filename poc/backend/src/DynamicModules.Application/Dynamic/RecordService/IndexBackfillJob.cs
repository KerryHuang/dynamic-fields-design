using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.RecordService;

/// <summary>
/// 一次性的索引回填（設計 §8.2）：升級前建立的索引沒有精確的 ScaledValue 與文字截斷標記。
/// 啟動時以 <see cref="SystemSetting.IndexBackfillVersion"/> 守門，版本達標就什麼都不做；回填完成前報表一律走記憶體路徑。
/// 不在欄位設定放任何旗標。依目前的欄位設定與記錄原值逐批重算，沒做完就中斷時下次啟動從頭再來（冪等）。
/// </summary>
public class IndexBackfillJob(IModuleRepository modules, IRecordRepository records, ISystemSettingRepository settings, IUnitOfWork uow, FieldTypeRegistry registry)
{
    public const int BatchSize = 1000;

    /// <returns>這次是否真的跑了回填。</returns>
    public async Task<bool> RunAsync(CancellationToken ct)
    {
        if (await settings.GetValueAsync(SystemSetting.IndexBackfillVersion, ct) == RecordIndexer.BackfillVersion) return false;
        foreach (var listed in await modules.ListAsync(ct))
        {
            var module = (await modules.GetByIdAsync(listed.Id, includeChildren: true, ct))!;
            foreach (var field in module.Blocks.SelectMany(b => b.Fields).Where(NeedsRefresh))
                await RefreshAsync(module, field, ct);
        }
        await settings.SetAsync(SystemSetting.IndexBackfillVersion, RecordIndexer.BackfillVersion, ct);
        await uow.CommitAsync(ct);
        return true;
    }

    /// <summary>數值欄位（ScaledValue）與文字會被索引截斷的型別（TextTruncated）。</summary>
    private static bool NeedsRefresh(Field f) =>
        RecordIndexer.ShouldIndex(f)
        && (ScaledNumber.Applies(f) || f.Type is FieldType.Text or FieldType.Phone or FieldType.Email or FieldType.Url or FieldType.Time or FieldType.AutoNumber);

    private async Task RefreshAsync(Module module, Field field, CancellationToken ct)
    {
        await records.ClearFieldIndexAsync(field.Id, ct);
        long after = 0;
        while (true)
        {
            var batch = await records.ListBatchByModuleAsync(module.Id, after, BatchSize, ct);
            if (batch.Count == 0) break;
            var rows = new List<RecordIndex>();
            foreach (var record in batch)
                foreach (var row in RecordIndexer.BuildField(field, RecordValueProcessor.Parse(record.ValuesJson), registry))
                {
                    row.RecordId = record.Id;
                    rows.Add(row);
                }
            records.AddIndexRows(rows);
            await uow.CommitAsync(ct);
            records.ReleaseTracked(batch);
            after = batch[^1].Id;
        }
    }
}
