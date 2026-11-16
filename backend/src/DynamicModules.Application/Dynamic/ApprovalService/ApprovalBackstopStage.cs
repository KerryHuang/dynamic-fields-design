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
/// 這次請求把記錄轉出鎖定狀態（解鎖）時，逐筆以 <c>max(InputSeqEnd, 第一個轉出鎖定狀態的項目 Seq)</c> 為界：界線（含）之前的變動
/// （使用者輸入，或事件、整合在解鎖前或解鎖同一筆寫入裡做的）改鎖定欄位仍 400，界線之後由解鎖轉移觸發的事件效果放行；
/// 表頭解鎖時明細的計算欄位照目前公式重算也放行；表頭本身被軟刪除時明細規則略過，跟著刪除規則走。
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

    /// <summary>
    /// 鎖定檢查（見類別說明）。簽核中的記錄已在前面處理，這裡跳過。
    /// 解鎖界線逐筆算：<c>max(InputSeqEnd, 該筆第一個轉出鎖定狀態的項目 Seq)</c>。界線（含）之前的變動是使用者輸入，或事件、整合在解鎖前
    /// 或解鎖同一筆寫入裡做的，鎖定欄位被改一律 400；界線之後是解鎖轉移觸發的事件效果，放行。
    /// </summary>
    private async Task<ServiceResult?> RejectLockedAsync(
        IReadOnlyList<JournalEntry> entries, Dictionary<long, List<long>> headersOf, IReadOnlySet<long> inApproval,
        Func<long, Task<Module?>> moduleOf, CancellationToken ct)
    {
        var raw = journal.Entries;

        // 記錄在 upTo（含）之前的日誌項目合併
        JournalEntry? Effective(long recordId, int upTo) =>
            JournalView.Merge(raw.Where(x => x.RecordId == recordId && x.Seq <= upTo)).FirstOrDefault();

        // 這筆記錄第一個把它轉出鎖定狀態的項目 Seq → 解鎖界線；沒轉出回 null
        int? UnlockBoundary(Workflow wf, ApprovalLock locked, long recordId)
        {
            var u = raw.FirstOrDefault(x => x.RecordId == recordId && x.Kind == JournalKind.Modified && x.BeforeValues is not null
                && locked.IsLockedState(wf.StateOf(RecordValueProcessor.Parse(x.BeforeValues)))
                && !locked.IsLockedState(wf.StateOf(RecordValueProcessor.Parse(x.AfterValues))));
            return u is null ? null : Math.Max(journal.InputSeqEnd, u.Seq);
        }

        var parents = new Dictionary<string, (Module Module, Workflow Wf, ApprovalLock Lock)?>();
        async Task<(Module Module, Workflow Wf, ApprovalLock Lock)?> ParentOf(string key)
        {
            if (parents.TryGetValue(key, out var cached)) return cached;
            (Module, Workflow, ApprovalLock)? result = null;
            if (await modules.GetByKeyAsync(key, includeChildren: false, ct) is { } stub && await moduleOf(stub.Id) is { Workflow: { } pwf } parent)
                result = (parent, pwf, await lockService.ForModuleAsync(parent, null, ct));
            return parents[key] = result;
        }

        // 表頭的狀態（以請求開始前為準）：(是否鎖定, 解鎖界線)；表頭不存在或不在鎖定狀態回 null
        var headerStates = new Dictionary<long, (int? Boundary, bool Deleted)?>();
        async Task<(int? Boundary, bool Deleted)?> HeaderState(long headerId, Module parent, Workflow pwf, ApprovalLock parentLock)
        {
            if (headerStates.TryGetValue(headerId, out var cached)) return cached;
            (int?, bool)? result = null;
            var header = entries.FirstOrDefault(x => x.RecordId == headerId);
            if (header?.Kind != JournalKind.Added)
            {
                var (hb, ha) = header is not null ? (header.BeforeValues, header.AfterValues)
                    : ((await records.ListByIdsAsync(parent.Id, [headerId], ct)).FirstOrDefault()?.ValuesJson is { } v ? (v, v) : (null, null));
                if (hb is not null && ha is not null && parentLock.IsLockedState(pwf.StateOf(RecordValueProcessor.Parse(hb))))
                    result = (parentLock.IsLockedState(pwf.StateOf(RecordValueProcessor.Parse(ha))) ? null : UnlockBoundary(pwf, parentLock, headerId),
                        header is { AfterDeleted: true, BeforeDeleted: false });
            }
            return headerStates[headerId] = result;
        }

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
                    var boundary = locked.IsLockedState(wf.StateOf(RecordValueProcessor.Parse(e.AfterValues))) ? null : UnlockBoundary(wf, locked, e.RecordId);
                    var lockedFields = module.Blocks.SelectMany(b => b.Fields).Where(f => locked.FieldIds.Contains(f.Id)).OrderBy(f => f.Id).ToList();

                    // 一般欄位：解鎖時只看界線以前，之後的事件效果放行
                    var view = boundary is { } upTo ? Effective(e.RecordId, upTo) : e;
                    if (view?.BeforeValues is not null)
                    {
                        var vb = RecordValueProcessor.Parse(view.BeforeValues);
                        var va = RecordValueProcessor.Parse(view.AfterValues);
                        foreach (var f in lockedFields.Where(f => !IsComputed(f)))
                            if (Raw(vb, f.Key) != Raw(va, f.Key))   // 簽核自己的豁免只發給進行中實例的記錄，那些在前面已跳過
                                return ServiceResult.Invalid(f.Key, ApprovalMessages.FieldLocked(f.LabelI18n));
                    }

                    // 計算欄位：解鎖後照目前公式重算所以放行；其餘只放行靜默項目（彙總、參照重算，§8.5）
                    if (boundary is null)
                        foreach (var entry in raw.Where(x => x.RecordId == e.RecordId && x.Kind == JournalKind.Modified && x.BeforeValues is not null && !x.Silent))
                        {
                            var rb = RecordValueProcessor.Parse(entry.BeforeValues!);
                            var ra = RecordValueProcessor.Parse(entry.AfterValues);
                            foreach (var f in lockedFields.Where(IsComputed))
                                if (Raw(rb, f.Key) != Raw(ra, f.Key))
                                    return ServiceResult.Invalid(f.Key, ApprovalMessages.FieldLocked(f.LabelI18n));
                        }
                }
            }

            // 明細列：表頭在鎖定狀態而且明細一起鎖
            if (!headersOf.TryGetValue(e.RecordId, out var headers) || ModuleRelations.DetailFieldOf(module) is not { } parentField
                || parentField.Config.RefTarget?.Key is not { } parentKey || await ParentOf(parentKey) is not { } p
                || !p.Lock.DetailRowsLocked) continue;
            foreach (var headerId in headers)
            {
                // 表頭沒有鎖定、是新增的，或這次被刪除（明細跟著表頭的刪除規則走，§8.5）：不在這裡擋
                if (await HeaderState(headerId, p.Module, p.Wf, p.Lock) is not { } hs || hs.Deleted) continue;
                var boundary = hs.Boundary;
                var row = boundary is { } upTo ? Effective(e.RecordId, upTo) : e;
                if (row is null || row.Silent) continue;
                if (row.Kind == JournalKind.Added || (row.AfterDeleted && !row.BeforeDeleted)
                    || (row.BeforeValues is not null && DetailValuesChanged(module, row, skipComputed: boundary is not null)))
                    return ServiceResult.Invalid($"details.{module.Key}", ApprovalMessages.FieldLocked(module.NameI18n));
            }
        }
        return null;
    }

    /// <summary>明細列有欄位被改；<paramref name="skipComputed"/>（表頭這次解鎖）時計算欄位不算，解鎖後照目前公式重算。</summary>
    private static bool DetailValuesChanged(Module module, JournalEntry row, bool skipComputed)
    {
        var skip = skipComputed ? module.Blocks.SelectMany(b => b.Fields).Where(IsComputed).Select(f => f.Key).ToHashSet() : [];
        var before = RecordValueProcessor.Parse(row.BeforeValues!);
        var after = RecordValueProcessor.Parse(row.AfterValues);
        return before.Keys.Union(after.Keys).Any(k => !skip.Contains(k) && Raw(before, k) != Raw(after, k));
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
