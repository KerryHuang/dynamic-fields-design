using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.ApprovalService;

/// <summary>
/// 設計器的定義變更遇到停在簽核鎖定狀態的記錄（設計 §8.6）：不能整批解鎖，也不能把鎖定欄位的值清掉。
/// 判斷只看記錄目前停在哪個狀態，不看使用者身分；與 <see cref="PendingApprovalGuard"/> 掛在同一組檢查點。
/// </summary>
public sealed class LockedDataGuard(ApprovalLockService locks, IRecordRepository records, IModuleRepository modules)
{
    /// <summary>
    /// 存狀態機（candidate）或刪除狀態機（candidate 為 null）。舊鎖定狀態 − 新鎖定狀態＝被解鎖的狀態；有記錄停在其中就回 409。
    /// 狀態欄位換掉時，舊狀態值不再是狀態，一律視為全部解鎖。
    /// </summary>
    public async Task<ServiceResult?> RejectUnlockAsync(Module module, Workflow? candidate, CancellationToken ct)
    {
        if (module.Workflow is not { } old) return null;
        var unlocked = ApprovalTopology.LockedStates(old).ToHashSet();
        if (unlocked.Count == 0) return null;
        if (candidate is not null && candidate.StatusFieldId == old.StatusFieldId) unlocked.ExceptWith(ApprovalTopology.LockedStates(candidate));
        if (unlocked.Count == 0) return null;
        var count = (await records.ListLiveValuesAsync(module.Id, ct))
            .Count(r => old.StateOf(RecordValueProcessor.Parse(r.ValuesJson)) is { } s && unlocked.Contains(s));
        return count == 0 ? null : ServiceResult.Conflict(ApprovalMessages.UnlockBlockedByRecords(count));
    }

    /// <summary>
    /// 刪除欄位或區塊會清掉記錄上的值：被刪的欄位屬於鎖定欄位，而停在鎖定狀態的記錄在這些欄位有值（空字串、空陣列、null 不算）就回 409。
    /// 本模組是明細、表頭鎖定時明細列一起鎖：停在鎖定狀態的表頭，其明細列在這些欄位有值也回 409。
    /// </summary>
    public async Task<ServiceResult?> RejectPurgeAsync(Module module, IReadOnlyCollection<Field> fields, CancellationToken ct)
    {
        if (fields.Count == 0) return null;
        if (module.Workflow is { } wf)
        {
            var own = await locks.ForModuleAsync(module, null, ct);
            var targets = fields.Where(f => own.FieldIds.Contains(f.Id)).ToList();
            if (targets.Count > 0 && own.LockedStateIds.Count > 0
                && Hit(targets, (await records.ListLiveValuesAsync(module.Id, ct))
                    .Select(r => RecordValueProcessor.Parse(r.ValuesJson)).Where(v => own.IsLockedState(wf.StateOf(v)))) is { } hit)
                return ServiceResult.Conflict(ApprovalMessages.PurgeBlockedByLocked(Label(hit.Field), hit.Count));
        }

        if (ModuleRelations.DetailFieldOf(module) is not { } parentField || ModuleRelations.ParentKeyOf(module) is not { } headerKey
            || await modules.GetByKeyAsync(headerKey, includeChildren: true, ct) is not { Workflow: { } hwf } header) return null;
        var headerLock = await locks.ForModuleAsync(header, null, ct);
        if (!headerLock.DetailRowsLocked || headerLock.LockedStateIds.Count == 0) return null;
        var lockedHeaders = (await records.ListLiveValuesAsync(header.Id, ct))
            .Where(r => headerLock.IsLockedState(hwf.StateOf(RecordValueProcessor.Parse(r.ValuesJson)))).Select(r => r.Id).ToHashSet();
        if (lockedHeaders.Count == 0) return null;
        var rows = (await records.ListLiveValuesAsync(module.Id, ct)).Select(r => RecordValueProcessor.Parse(r.ValuesJson))
            .Where(v => v.TryGetValue(parentField.Key, out var raw) && raw is { ValueKind: JsonValueKind.Number } n && n.TryGetInt64(out var id) && lockedHeaders.Contains(id));
        return Hit([.. fields], rows) is { } rowHit
            ? ServiceResult.Conflict(ApprovalMessages.PurgeBlockedByLocked(Label(rowHit.Field), rowHit.Count))
            : null;
    }

    /// <summary>依欄位順序回第一個在 rows 中有值的欄位與有值的筆數。</summary>
    private static (Field Field, int Count)? Hit(IReadOnlyList<Field> fields, IEnumerable<Dictionary<string, JsonElement?>> rows)
    {
        var list = rows.ToList();
        foreach (var f in fields)
        {
            var n = list.Count(v => v.TryGetValue(f.Key, out var raw) && raw is { } e && !IsEmpty(e));
            if (n > 0) return (f, n);
        }
        return null;
    }

    private static bool IsEmpty(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => true,
        JsonValueKind.String => string.IsNullOrWhiteSpace(v.GetString()),
        JsonValueKind.Array => v.GetArrayLength() == 0,
        _ => false
    };

    private static string Label(Field f) => f.LabelI18n.Resolve(I18nText.FallbackLocale);
}
