using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.ModuleService;

/// <summary>
/// 重組標題（改標題樣板、換標題欄位、改樣板用到的選項名稱）會改寫模組裡每一筆記錄的 Title。
/// 只要有一筆記錄已經固定（處於狀態機的唯讀狀態，或已產生事件事實），就整筆回 409，樣板、選項與既有標題都不變。
/// 明細列自己沒有狀態與事實，鎖在表頭上（同 FactBackstopStage），所以明細模組看它的表頭。
/// 這兩種請求不是記錄寫入（不經 RecordEffectsBehavior 的後盾），所以在 handler 裡先檢查。
/// </summary>
public class TitleRewriteGuard(IModuleRepository modules, IRecordRepository records, IEventFactRepository facts)
{
    public const string Message = "模組裡有唯讀或已記帳的記錄，改標題會改寫它們的標題，所以不能改";

    /// <summary>有固定的記錄就回 409；否則 null。<paramref name="rows"/> 是模組的全部記錄。</summary>
    public async Task<ServiceResult?> RejectAsync(Module module, IReadOnlyList<Record> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return null;
        if (module.Workflow is { } wf && rows.Any(r => wf.IsLocked(RecordValueProcessor.Parse(r.ValuesJson))))
            return ServiceResult.Conflict(Message);
        if ((await facts.ListSourceIdsInModuleAsync(module.Id, ct)).Count > 0) return ServiceResult.Conflict(Message);

        if (ModuleRelations.DetailFieldOf(module) is not { } parentField
            || ModuleRelations.ParentKeyOf(module) is not { } parentKey
            || await modules.GetByKeyAsync(parentKey, includeChildren: true, ct) is not { } parent)
            return null;
        var headerIds = rows.Select(r => RecordValueProcessor.Parse(r.ValuesJson).TryGetValue(parentField.Key, out var v)
                && v is { ValueKind: System.Text.Json.JsonValueKind.Number } n && n.TryGetInt64(out var id) ? id : (long?)null)
            .OfType<long>().Distinct().ToList();
        if (headerIds.Count == 0) return null;
        if ((await facts.ListFrozenBeforeAsync(headerIds, ct)).Count > 0) return ServiceResult.Conflict(Message);
        if (parent.Workflow is { } hwf
            && (await records.ListByIdsAsync(parent.Id, headerIds, ct)).Any(h => hwf.IsLocked(RecordValueProcessor.Parse(h.ValuesJson))))
            return ServiceResult.Conflict(Message);
        return null;
    }
}
