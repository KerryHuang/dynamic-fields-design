using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Application.Common;

namespace DynamicModules.Application.Dynamic.LedgerService;

/// <summary>分錄閘（設計 §2 步驟 2、§6.4）：分錄模組及其明細模組的修改與刪除一律擋下。不快取，每次問資料庫（表很小、有索引）。</summary>
public class LedgerGate(ILedgerRepository ledgers, IModuleRepository modules)
{
    public const string Message = "分錄不可修改，請新增沖銷分錄";

    /// <summary>module 要已載入欄位（判斷是否為明細）。</summary>
    public async Task<bool> IsLedgerAsync(Module module, CancellationToken ct)
    {
        if (await ledgers.IsLedgerModuleAsync(module.Id, ct)) return true;
        if (ModuleRelations.ParentKeyOf(module) is not { } parentKey) return false;
        return await modules.GetByKeyAsync(parentKey, includeChildren: false, ct) is { } parent
               && await ledgers.IsLedgerModuleAsync(parent.Id, ct);
    }

    public async Task<ServiceResult?> RejectWriteAsync(Module module, CancellationToken ct) =>
        await IsLedgerAsync(module, ct) ? ServiceResult.Conflict(Message) : null;
}

/// <summary>
/// 設計器的定義變更遇到分錄模組（設計 §10.3）：會改寫既有記錄值的操作（換標題欄位、新增或變更公式與彙總、刪除仍有值的欄位或區塊）
/// 在模組已有記錄時回 409，保證已寫入的資料不隨定義改變。不改寫值的操作照常放行。
/// </summary>
public class ImmutableDataGuard(LedgerGate gate, IRecordRepository records)
{
    public const string Message = "這是分錄模組，這項變更會改寫既有分錄的資料，不允許";

    public async Task<ServiceResult?> RejectAsync(IEnumerable<Module> affected, CancellationToken ct)
    {
        foreach (var m in affected)
            if (await gate.IsLedgerAsync(m, ct) && await records.AnyByModuleAsync(m.Id, ct))
                return ServiceResult.Conflict(Message);
        return null;
    }

    public Task<ServiceResult?> RejectAsync(Module affected, CancellationToken ct) => RejectAsync([affected], ct);
}
