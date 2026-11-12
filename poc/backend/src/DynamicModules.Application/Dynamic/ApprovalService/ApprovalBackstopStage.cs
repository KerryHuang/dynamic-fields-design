using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Behaviors;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.ApprovalService;

/// <summary>
/// 簽核的寫入後盾（設計 §5.3、§8.5、§9.3）。讀寫入日誌：
/// 簽核中（或這次請求動過實例）的記錄被軟刪除、或彙總與公式以外的值被改而且不在豁免內 → 409；明細列的表頭簽核中而明細有任何變動 → 409；
/// 任何記錄走了需簽核的轉移而狀態值不是簽核自己寫的（事件、整合以系統身分推動）→ 409。轉移逐筆看原始日誌，
/// 同一請求內的連鎖轉移（一般 → 需簽核）也擋；豁免比對寫入後的值，不只看記錄與鍵。
/// 鎖定（兩道之第二道；第一道在 <c>UpdateRecordHandler</c>，鎖定記錄的計算欄位保留舊值）：以「請求開始前」的狀態判斷記錄是否在簽核鎖定狀態，
/// 鎖定欄位被改 → 400（鍵為欄位鍵）；計算欄位只放行靜默項目（彙總、參照重算）；明細列在表頭鎖定且明細一起鎖時，新增、刪除、改值 → 400。
/// 這次請求把記錄轉出鎖定狀態（解鎖）時，只看 handler 的輸入（<see cref="IWriteJournal.InputSeqEnd"/> 之前的項目），
/// 解鎖轉移上的事件效果放行，使用者或整合直接送來的值同次改鎖定欄位仍 400。
/// 都通過時，簽核中記錄被允許的路徑（彙總、公式）改了值 → 替進行中實例寫一筆 content-changed（設計 §8.5「靜默更新留歷程」，不發通知）。
/// </summary>
public class ApprovalBackstopStage(
    IWriteJournal journal, IApprovalInstanceRepository instances, IModuleRepository modules, ApprovalWriteContext context,
    IBusinessTime time, IOperationContext op, IUnitOfWork uow, ApprovalLockService lockService, IRecordRepository records)
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
        var pendingInDb = await instances.ListPendingRecordIdsAsync(ids, ct);
        var inApproval = new HashSet<long>(pendingInDb);
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

        if (await RejectLockedAsync(entries, headersOf, inApproval, ModuleOf, ct) is { } locked) return locked;

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

        await LogContentChangesAsync(pendingInDb, ModuleOf, ct);
        return null;
    }

    private static bool IsComputed(Field f) => f.Type is FieldType.Formula or FieldType.Rollup;

    /// <summary>鎖定檢查（見類別說明）。簽核中的記錄已在前面處理，這裡跳過。</summary>
    private async Task<ServiceResult?> RejectLockedAsync(
        IReadOnlyList<JournalEntry> entries, Dictionary<long, List<long>> headersOf, IReadOnlySet<long> inApproval,
        Func<long, Task<Module?>> moduleOf, CancellationToken ct)
    {
        // 記錄在 upTo（含）之前的日誌項目合併；upTo 為 null 取全部
        JournalEntry? Effective(long recordId, int? upTo) =>
            JournalView.Merge(journal.Entries.Where(x => x.RecordId == recordId && (upTo is null || x.Seq <= upTo))).FirstOrDefault();

        foreach (var e in entries)
        {
            if (inApproval.Contains(e.RecordId) || await moduleOf(e.ModuleId) is not { } module) continue;

            // 一般記錄：請求開始前在鎖定狀態，鎖定欄位被改
            if (e.Kind == JournalKind.Modified && e.BeforeValues is not null && module.Workflow is { } wf)
            {
                var locked = await lockService.ForModuleAsync(module, null, ct);
                var before = RecordValueProcessor.Parse(e.BeforeValues);
                if (locked.IsLockedState(wf.StateOf(before)))
                {
                    var unlocking = !locked.IsLockedState(wf.StateOf(RecordValueProcessor.Parse(e.AfterValues)));
                    var lockedFields = module.Blocks.SelectMany(b => b.Fields).Where(f => locked.FieldIds.Contains(f.Id)).OrderBy(f => f.Id).ToList();

                    // 一般欄位：解鎖時只看 handler 的輸入，事件效果放行
                    var view = unlocking ? Effective(e.RecordId, journal.InputSeqEnd) : e;
                    if (view?.BeforeValues is not null)
                    {
                        var vb = RecordValueProcessor.Parse(view.BeforeValues);
                        var va = RecordValueProcessor.Parse(view.AfterValues);
                        foreach (var f in lockedFields.Where(f => !IsComputed(f)))
                            if (Raw(vb, f.Key) != Raw(va, f.Key) && !context.IsExempt(e.RecordId, f.Key, Raw(va, f.Key)))
                                return ServiceResult.Invalid(f.Key, ApprovalMessages.FieldLocked(f.LabelI18n));
                    }

                    // 計算欄位：解鎖後照目前公式重算所以放行；其餘只放行靜默項目（彙總、參照重算，§8.5）
                    if (!unlocking)
                        foreach (var raw in journal.Entries.Where(x => x.RecordId == e.RecordId && x.Kind == JournalKind.Modified && x.BeforeValues is not null && !x.Silent))
                        {
                            var rb = RecordValueProcessor.Parse(raw.BeforeValues!);
                            var ra = RecordValueProcessor.Parse(raw.AfterValues);
                            foreach (var f in lockedFields.Where(IsComputed))
                                if (Raw(rb, f.Key) != Raw(ra, f.Key))
                                    return ServiceResult.Invalid(f.Key, ApprovalMessages.FieldLocked(f.LabelI18n));
                        }
                }
            }

            // 明細列：表頭在鎖定狀態而且明細一起鎖
            if (!headersOf.TryGetValue(e.RecordId, out var headers) || ModuleRelations.DetailFieldOf(module) is not { } parentField
                || parentField.Config.RefTarget?.Key is not { } parentKey || await modules.GetByKeyAsync(parentKey, includeChildren: false, ct) is not { } parentStub
                || await moduleOf(parentStub.Id) is not { Workflow: { } pwf } parent) continue;
            var parentLock = await lockService.ForModuleAsync(parent, null, ct);
            if (!parentLock.DetailRowsLocked) continue;
            foreach (var headerId in headers)
            {
                var header = entries.FirstOrDefault(x => x.RecordId == headerId);
                if (header?.Kind == JournalKind.Added) continue;
                var (hb, ha) = header is not null ? (header.BeforeValues, header.AfterValues)
                    : ((await records.ListByIdsAsync(parent.Id, [headerId], ct)).FirstOrDefault()?.ValuesJson is { } v ? (v, v) : (null, null));
                if (hb is null || ha is null || !parentLock.IsLockedState(pwf.StateOf(RecordValueProcessor.Parse(hb)))) continue;
                var unlocking = !parentLock.IsLockedState(pwf.StateOf(RecordValueProcessor.Parse(ha)));
                var row = unlocking ? Effective(e.RecordId, journal.InputSeqEnd) : e;
                if (row is null || row.Silent) continue;
                var changed = row.Kind == JournalKind.Added || (row.AfterDeleted && !row.BeforeDeleted)
                    || (row.BeforeValues is not null && !RecordValueProcessor.Parse(row.BeforeValues).Keys.Union(RecordValueProcessor.Parse(row.AfterValues).Keys)
                        .All(k => Raw(RecordValueProcessor.Parse(row.BeforeValues), k) == Raw(RecordValueProcessor.Parse(row.AfterValues), k)));
                if (changed) return ServiceResult.Invalid($"details.{module.Key}", ApprovalMessages.FieldLocked(module.NameI18n));
            }
        }
        return null;
    }

    /// <summary>
    /// 逐筆看原始日誌、略過簽核自己寫的那幾筆（可編輯欄位帶動的公式不算靜默更新），收集簽核中記錄的彙總與公式欄位
    /// 第一個 before 與最後一個 after；有不同的就寫一筆 content-changed：DataJson = [{fieldId, before, after}]，系統身分。
    /// </summary>
    private async Task LogContentChangesAsync(IReadOnlySet<long> pending, Func<long, Task<Module?>> moduleOf, CancellationToken ct)
    {
        var changes = new Dictionary<long, Dictionary<string, (string? Before, string? After)>>();
        foreach (var e in journal.Entries.Where(x => x.Kind == JournalKind.Modified && x.BeforeValues is not null && pending.Contains(x.RecordId)
                     && !context.IsOwnEntry(x.RecordId, x.Seq)).OrderBy(x => x.Seq))
        {
            if (await moduleOf(e.ModuleId) is not { } module) continue;
            var before = RecordValueProcessor.Parse(e.BeforeValues!);
            var after = RecordValueProcessor.Parse(e.AfterValues);
            if (!changes.TryGetValue(e.RecordId, out var map)) changes[e.RecordId] = map = [];
            foreach (var key in module.Blocks.SelectMany(b => b.Fields).Where(f => f.Type is FieldType.Rollup or FieldType.Formula).Select(f => f.Key))
            {
                var (b, a) = (Raw(before, key), Raw(after, key));
                if (b == a) continue;
                map[key] = map.TryGetValue(key, out var seen) ? (seen.Before, a) : (b, a);
            }
        }
        var wrote = false;
        foreach (var (recordId, map) in changes)
        {
            var diff = map.Where(kv => kv.Value.Before != kv.Value.After).OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new ContentChange(long.Parse(kv.Key.AsSpan(2)), Parse(kv.Value.Before), Parse(kv.Value.After))).ToList();
            if (diff.Count == 0 || await instances.GetPendingByRecordAsync(recordId, ct) is not { } inst) continue;
            instances.AddAction(new ApprovalAction
            {
                InstanceId = inst.Id, StageRunId = inst.CurrentStageRunId, Action = ApprovalActions.ContentChanged, ActorUserId = ApprovalEngine.SystemActor,
                DataJson = JsonSerializer.Serialize(diff, ContentChange.Json), CreatedTime = time.Now, OperationId = op.OperationId
            });
            wrote = true;
        }
        if (wrote) await uow.CommitAsync(ct);
    }

    private static JsonElement? Parse(string? raw)
    {
        if (raw is null) return null;
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.Clone();
    }

    private static long? HeaderId(string? json, string key) =>
        json is not null && RecordValueProcessor.Parse(json).TryGetValue(key, out var raw)
        && raw is { ValueKind: JsonValueKind.Number } n && n.TryGetInt64(out var id) ? id : null;

    /// <summary>content-changed 的一個欄位；讀歷程時依讀者的欄位權限遮蔽（<see cref="ApprovalViews"/>）。</summary>
    internal sealed record ContentChange(long FieldId, JsonElement? Before, JsonElement? After)
    {
        public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    }

    internal static string? Raw(IReadOnlyDictionary<string, JsonElement?> d, string key) =>
        d.TryGetValue(key, out var v) && v is { ValueKind: not JsonValueKind.Null } e ? e.GetRawText() : null;
}
