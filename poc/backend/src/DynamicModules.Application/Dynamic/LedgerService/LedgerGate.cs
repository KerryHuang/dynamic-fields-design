using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.DeleteService.Data;

namespace DynamicModules.Application.Dynamic.LedgerService;

/// <summary>分錄閘（設計 §2 步驟 2、§6.4）：分錄模組及其明細模組的修改與刪除一律擋下。不快取，每次問資料庫（表很小、有索引）。</summary>
public class LedgerGate(ILedgerRepository ledgers, IModuleRepository modules)
{
    public static readonly MessageRef Rejected = MessageRef.Of(MessageText.LedgerImmutable);

    /// <summary>module 要已載入欄位（判斷是否為明細）。</summary>
    public async Task<bool> IsLedgerAsync(Module module, CancellationToken ct)
    {
        if (await ledgers.IsLedgerModuleAsync(module.Id, ct)) return true;
        if (ModuleRelations.ParentKeyOf(module) is not { } parentKey) return false;
        return await modules.GetByKeyAsync(parentKey, includeChildren: false, ct) is { } parent
               && await ledgers.IsLedgerModuleAsync(parent.Id, ct);
    }

    public async Task<ServiceResult?> RejectWriteAsync(Module module, CancellationToken ct) =>
        await IsLedgerAsync(module, ct) ? ServiceResult.Conflict(Rejected) : null;
}

/// <summary>
/// 設計器的定義變更遇到分錄模組（設計 §10.3）：會改寫既有記錄值的操作（換標題欄位、新增或變更公式與彙總、刪除仍有值的欄位或區塊）
/// 在模組已有記錄時回 409，保證已寫入的資料不隨定義改變。不改寫值的操作照常放行。
/// </summary>
public class ImmutableDataGuard(LedgerGate gate, IRecordRepository records)
{
    private static readonly MessageRef Rejected = MessageRef.Of(MessageText.LedgerSchemaImmutable);

    public async Task<ServiceResult?> RejectAsync(IEnumerable<Module> affected, CancellationToken ct)
    {
        foreach (var m in affected)
            if (await gate.IsLedgerAsync(m, ct) && await records.AnyByModuleAsync(m.Id, ct))
                return ServiceResult.Conflict(Rejected);
        return null;
    }

    public Task<ServiceResult?> RejectAsync(Module affected, CancellationToken ct) => RejectAsync([affected], ct);

    /// <summary>刪除欄位或區塊：Purge 非零，或被刪的公式、彙總欄位在記錄上有值（Purge 為 0 但值會被清掉）才擋；什麼都不清的刪除照常放行。</summary>
    public async Task<ServiceResult?> RejectFieldPurgeAsync(Module module, PurgeCounts purge, IReadOnlyCollection<Field> fields, CancellationToken ct)
    {
        if (ComputedValueProbe.NothingPurged(purge) && !await ComputedValueProbe.HasValuesAsync(records, module, fields, ct)) return null;
        return await RejectAsync(module, ct);
    }
}
