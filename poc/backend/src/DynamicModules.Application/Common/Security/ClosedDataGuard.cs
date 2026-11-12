using DynamicModules.Application.Dynamic.DeleteService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Common.Security;

/// <summary>
/// 設計器的定義寫入遇到 Closed 模組的關卡。設計文件 §6.3 明文：權限設定仍可修改，欄位與規則的編輯與 Inactive 模組相同，
/// 所以定義的修改預設放行。唯一要擋的是「會連帶改寫既有記錄值」的變更（換標題欄位、公式與彙總重算、刪除欄位或區塊時清值），
/// 因為它們違反「資料唯讀」。判斷只看模組狀態，不看使用者身分，系統管理員也一樣被擋。
/// </summary>
public static class ClosedDataGuard
{
    private static readonly MessageRef Rejected = MessageRef.Of(MessageText.AppClosedNoRewrite);

    /// <summary>任一模組是 Closed 就回 409；否則 null，可以繼續。</summary>
    public static ServiceResult? Reject(IEnumerable<Module> affected) =>
        affected.Any(m => m.Status == ModuleStatus.Closed) ? ServiceResult.Conflict(Rejected) : null;

    public static ServiceResult? Reject(Module affected) => Reject([affected]);

    /// <summary>
    /// 刪除欄位或區塊：Purge 非零，或被刪的公式、彙總欄位在記錄上有值（Purge 為 0 但值會被清掉）而模組是 Closed 就擋。
    /// </summary>
    public static async Task<ServiceResult?> RejectFieldPurgeAsync(Module module, PurgeCounts purge, IReadOnlyCollection<Field> fields, IRecordRepository records, CancellationToken ct)
    {
        if (module.Status != ModuleStatus.Closed) return null;
        if (ComputedValueProbe.NothingPurged(purge) && !await ComputedValueProbe.HasValuesAsync(records, module, fields, ct)) return null;
        return Reject(module);
    }

    /// <summary>
    /// 公式重算會改寫模組自己的記錄，並連帶重算以它為來源的彙總所在的父模組（FormulaRecalc），所以兩者都算。
    /// </summary>
    public static async Task<ServiceResult?> RejectFormulaRecalcAsync(Module module, IModuleRepository modules, CancellationToken ct) =>
        Reject([module, .. (await modules.ListRollupsBySourceModuleAsync(module.Id, ct)).Select(x => x.Parent)]);
}
