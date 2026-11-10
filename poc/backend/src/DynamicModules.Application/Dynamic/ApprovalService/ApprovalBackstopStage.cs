using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Behaviors;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.ApprovalService;

/// <summary>
/// 簽核的寫入後盾（設計 §5.3、§8.5；這一段只做「簽核中」，鎖定在 a2d 加）。讀寫入日誌：
/// 簽核中（或這次請求動過實例）的記錄被軟刪除、或彙總與公式以外的值被改而且不在豁免內 → 409；明細列的表頭簽核中而明細有任何變動 → 409；
/// 任何記錄走了需簽核的轉移而狀態值不是簽核自己寫的（事件、整合以系統身分推動）→ 409。轉移逐筆看原始日誌，
/// 同一請求內的連鎖轉移（一般 → 需簽核）也擋；豁免比對寫入後的值，不只看記錄與鍵。
/// </summary>
public class ApprovalBackstopStage(IWriteJournal journal, IApprovalInstanceRepository instances, IModuleRepository modules, ApprovalWriteContext context)
    : IWriteStage
{
    public int Order => 9;

    public async Task<ServiceResult?> RunAsync(CancellationToken ct)
    {
        var entries = JournalView.Merge(journal.Entries);
        if (entries.Count == 0) return null;

        var modulesById = new Dictionary<long, Module?>();
        async Task<Module?> ModuleOf(long id)
        {
            if (!modulesById.TryGetValue(id, out var m)) modulesById[id] = m = await modules.GetByIdAsync(id, includeChildren: true, ct);
            return m;
        }

        var headersOf = new Dictionary<long, List<long>>();   // 明細列 → 表頭（換表頭時新舊兩邊都算）
        var anyApproval = context.TouchedRecordIds.Count > 0;
        foreach (var e in entries)
        {
            var m = await ModuleOf(e.ModuleId);
            if (m is null) continue;
            if (m.Workflow is { } wf && ApprovalTopology.ApprovalTargets(wf).Count > 0) anyApproval = true;
            if (ModuleRelations.DetailFieldOf(m) is not { } parentField) continue;
            var headers = new[] { HeaderId(e.BeforeValues, parentField.Key), HeaderId(e.AfterValues, parentField.Key) }
                .OfType<long>().Distinct().ToList();
            if (headers.Count == 0) continue;
            headersOf[e.RecordId] = headers;
            if (parentField.Config.RefTarget?.Key is { } parentKey && await modules.GetByKeyAsync(parentKey, includeChildren: false, ct) is { } parent
                && (await ModuleOf(parent.Id))?.Workflow is { } pwf && ApprovalTopology.ApprovalTargets(pwf).Count > 0)
                anyApproval = true;
        }
        if (!anyApproval) return null;   // 沒有任何牽涉簽核的模組：一般寫入不多查

        var ids = entries.Select(e => e.RecordId).Concat(headersOf.Values.SelectMany(h => h)).Distinct().ToList();
        var inApproval = await instances.ListPendingRecordIdsAsync(ids, ct);
        inApproval.UnionWith(context.TouchedRecordIds);

        foreach (var e in entries)
        {
            var module = await ModuleOf(e.ModuleId);
            if (module is null) continue;
            var before = e.BeforeValues is null ? new Dictionary<string, JsonElement?>() : RecordValueProcessor.Parse(e.BeforeValues);
            var after = RecordValueProcessor.Parse(e.AfterValues);
            var changed = before.Keys.Union(after.Keys).Where(k => Raw(before, k) != Raw(after, k)).ToList();
            var deleted = e.AfterDeleted && !e.BeforeDeleted;

            if (headersOf.TryGetValue(e.RecordId, out var headers) && headers.FirstOrDefault(inApproval.Contains) is var header and > 0
                && (deleted || changed.Count > 0 || e.Kind == JournalKind.Added))
                return ServiceResult.Conflict(ApprovalMessages.EventBlocked(header));

            if (inApproval.Contains(e.RecordId) && e.Kind == JournalKind.Modified)
            {
                if (deleted) return ServiceResult.Conflict(ApprovalMessages.EventBlocked(e.RecordId));
                var computed = module.Blocks.SelectMany(b => b.Fields).Where(f => f.Type is FieldType.Rollup or FieldType.Formula)
                    .Select(f => f.Key).ToHashSet();
                if (changed.Any(k => !computed.Contains(k) && !context.IsExempt(e.RecordId, k, Raw(after, k))))
                    return ServiceResult.Conflict(ApprovalMessages.EventBlocked(e.RecordId));
            }
        }

        // 轉移逐筆看原始日誌：合併後只剩第一個 before 與最後一個 after，中間經過的需簽核轉移會漏掉
        foreach (var e in journal.Entries.Where(x => x.Kind == JournalKind.Modified && x.BeforeValues is not null))
        {
            if ((await ModuleOf(e.ModuleId))?.Workflow is not { } wf) continue;
            var before = RecordValueProcessor.Parse(e.BeforeValues!);
            var after = RecordValueProcessor.Parse(e.AfterValues);
            var key = wf.StatusFieldKey;
            if (Raw(before, key) == Raw(after, key)
                || (context.IsOwnEntry(e.RecordId, e.Seq) && context.IsExempt(e.RecordId, key, Raw(after, key)))) continue;
            if (wf.StateOf(after) is { } to && wf.RequiresApproval(wf.StateOf(before) ?? wf.InitialOptionId, to))
                return ServiceResult.Conflict(ApprovalMessages.TransitionBlocked(e.RecordId));
        }
        return null;
    }

    private static long? HeaderId(string? json, string key) =>
        json is not null && RecordValueProcessor.Parse(json).TryGetValue(key, out var raw)
        && raw is { ValueKind: JsonValueKind.Number } n && n.TryGetInt64(out var id) ? id : null;

    internal static string? Raw(IReadOnlyDictionary<string, JsonElement?> d, string key) =>
        d.TryGetValue(key, out var v) && v is { ValueKind: not JsonValueKind.Null } e ? e.GetRawText() : null;
}
