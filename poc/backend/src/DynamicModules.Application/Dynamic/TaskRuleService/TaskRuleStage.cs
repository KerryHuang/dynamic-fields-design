using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Behaviors;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Application.Dynamic.TaskService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Application.Dynamic.TaskRuleService;

/// <summary>
/// 記錄寫入後的任務規則（Order 12，排在事件、後盾、跨記錄檢查、任務同步之後；設計 §5.2、§5.4、§5.5）。
/// 讀整個請求合併後的日誌，靜默寫入也算，略過寫入後已刪除的記錄。每條啟用中、應用程式開啟中的規則：
/// 沒觸發過就看這次是否觸發；once 只同步；everyTime 再次觸發時，上一輪還有未完成的任務就留系統記錄、不產生新批次。
/// 快取只放在單次 <see cref="RunAsync"/> 的區域變數，不跨請求。
/// </summary>
public class TaskRuleStage(
    IWriteJournal journal, ITaskRuleRepository rules, IModuleRepository modules, IRecordRepository records,
    IAppRepository apps, IPermissionRepository permissions, ITaskRepository tasks, TaskRuleApplier applier, TaskWriter writer,
    FieldTypeRegistry registry, IBusinessTime time) : IWriteStage
{
    public int Order => 12;

    public async Task<ServiceResult?> RunAsync(CancellationToken ct)
    {
        var merged = JournalView.Merge(journal.Entries);
        if (merged.Count == 0) return null;

        var rulesByModule = new Dictionary<long, List<TaskRule>>();
        Dictionary<long, AppStatus>? appStatus = null;
        HashSet<string>? userIds = null;
        HashSet<string>? activeUsers = null;
        var today = time.Today;

        foreach (var entry in merged)
        {
            if (entry.AfterDeleted) continue;
            if (!rulesByModule.TryGetValue(entry.ModuleId, out var list))
            {
                list = await rules.ListActiveByModuleAsync(entry.ModuleId, ct);
                if (list.Any(r => r.AppId is not null))
                {
                    appStatus ??= (await apps.ListAsync(ct)).ToDictionary(a => a.Id, a => a.Status);
                    list = list.Where(r => r.AppId is not { } appId || appStatus.GetValueOrDefault(appId, AppStatus.Closed) == AppStatus.Open).ToList();
                }
                rulesByModule[entry.ModuleId] = list;
            }
            if (list.Count == 0) continue;
            var module = await modules.GetByIdAsync(entry.ModuleId, includeChildren: true, ct);
            if (module is null) continue;

            userIds ??= await modules.ListUserIdsAsync(ct);
            activeUsers ??= (await permissions.ListUsersAsync(ct)).Where(u => u.IsActive).Select(u => u.UserId).ToHashSet(StringComparer.Ordinal);
            var added = entry.Kind == JournalKind.Added;
            var after = RecordValueProcessor.Parse(entry.AfterValues);
            IReadOnlyDictionary<string, JsonElement?>? before = added || entry.BeforeValues is null ? null : RecordValueProcessor.Parse(entry.BeforeValues);
            var title = (await records.GetAsync(entry.RecordId, ct))?.Title ?? "";

            foreach (var rule in list)
            {
                var holdsAfter = TaskConditions.Holds(rule.WhenJson, module, after, registry, today, userIds);
                bool? holdsBefore = before is null ? null : TaskConditions.Holds(rule.WhenJson, module, before, registry, today, userIds);
                // 規則條件是整條規則的篩選：任何觸發方式都要這次寫入後成立
                var edge = holdsAfter && TaskRuleEvaluator.Triggers(rule.Trigger, added, holdsBefore, holdsAfter, before, after);
                var latest = await rules.GetLatestOccurrenceAsync(rule.Id, entry.RecordId, ct);

                if (latest is not { } occurrence)
                {
                    if (edge) await applier.GenerateAsync(rule, module, entry.RecordId, 1, title, after, activeUsers, userIds, ct);
                    continue;
                }
                if (rule.Repeat == TaskRuleRepeat.EveryTime && edge)
                {
                    if (!await tasks.AnyOpenByRuleAndRecordAsync(rule.Id, entry.RecordId, ct))
                    {
                        await applier.GenerateAsync(rule, module, entry.RecordId, occurrence + 1, title, after, activeUsers, userIds, ct);
                        continue;
                    }
                    foreach (var open in (await tasks.ListByRuleOccurrenceAsync(rule.Id, entry.RecordId, occurrence, ct)).Where(t => t.IsOpen))
                        writer.AddSystemEntry(open, TaskSystemEvents.RetriggerSkipped);
                }
                await applier.ReconcileAsync(rule, module, entry.RecordId, occurrence, title, before, after, activeUsers, userIds, ct);
            }
        }

        try
        {
            await writer.FlushAsync(ct);
        }
        catch (DbUpdateException e) when (TaskRuleConflicts.IsUnique(e))
        {
            return ServiceResult.Conflict(MessageRef.Of(MessageText.TaskRuleAlreadyRun));
        }
        return null;
    }
}

internal static class TaskRuleConflicts
{
    /// <summary>TaskRuleRun 主鍵或任務 (RuleId, RecordId, RuleOccurrence, RuleTaskCode) 唯一索引衝突：另一個請求已經套用（同 EffectApplier 的判斷）。</summary>
    public static bool IsUnique(DbUpdateException e) =>
        e.InnerException?.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true;
}
