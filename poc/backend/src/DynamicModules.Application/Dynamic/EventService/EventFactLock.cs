using DynamicModules.Application.Common;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.EventService;

/// <summary>
/// 事件事實鎖（設計 §4.7）：一筆來源記錄只要產生過事實就是「已記帳」，除狀態欄位外不能修改、不能刪除。
/// 一律查事實表，不看模組現在有沒有事件定義：鎖的是資料（這筆已經產生連動記錄），不是定義。
/// </summary>
public class EventFactLock(IEventFactRepository facts)
{
    public const string EditMessage = "這筆記錄已經產生連動記錄（已記帳），只能變更狀態；要更正請走轉移狀態與沖銷";
    public const string DeleteMessage = "這筆記錄已經產生連動記錄（已記帳），不能刪除；要作廢請轉移到作廢狀態";

    public Task<bool> IsFrozenAsync(long recordId, CancellationToken ct) => facts.AnyBySourceAsync(recordId, ct);

    /// <summary>已記帳的記錄，請求若碰到狀態欄位以外的值或明細就回 409；否則 null。</summary>
    public ServiceResult? RejectEdit(Module module, IEnumerable<string> touchedKeys, bool touchesDetails)
    {
        var status = module.Workflow?.StatusFieldKey;
        return touchesDetails || touchedKeys.Any(k => k != status) ? ServiceResult.Conflict(EditMessage) : null;
    }
}
