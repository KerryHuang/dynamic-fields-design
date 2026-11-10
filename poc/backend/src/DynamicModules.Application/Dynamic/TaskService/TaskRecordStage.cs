using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Behaviors;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.TaskService;

/// <summary>
/// 記錄寫入後同步掛在上面的任務（Order 11，排在事件、後盾、跨記錄檢查之後）：記錄被軟刪除（含連帶刪除）時取消未完成的任務；
/// 其餘記錄的標題變了就更新快照。讀整個請求的日誌（靜默寫入也算）；這些記錄上沒有任務就直接結束，大量匯入不多花成本。
/// </summary>
public sealed class TaskRecordStage(IWriteJournal journal, ITaskRepository tasks, IRecordRepository records, TaskWriter writer) : IWriteStage
{
    public int Order => 11;

    public async Task<ServiceResult?> RunAsync(CancellationToken ct)
    {
        var merged = JournalView.Merge(journal.Entries);
        if (merged.Count == 0) return null;
        var items = await tasks.ListByRecordIdsAsync(merged.Select(e => e.RecordId).ToList(), ct);
        if (items.Count == 0) return null;

        var byRecord = merged.ToDictionary(e => e.RecordId);
        var titles = new Dictionary<long, string?>();
        foreach (var task in items)
        {
            var recordId = task.RecordId!.Value;
            var entry = byRecord[recordId];
            if (entry.AfterDeleted)
            {
                if (!entry.BeforeDeleted) writer.CancelBySystem(task, TaskSystemEvents.RecordDeleted);
                continue;
            }
            if (!titles.TryGetValue(recordId, out var title))
                titles[recordId] = title = (await records.GetAsync(recordId, ct))?.Title;
            if (title is not null && title != task.RecordTitleSnapshot) task.RecordTitleSnapshot = title;
        }
        await writer.FlushAsync(ct);
        return null;
    }
}
