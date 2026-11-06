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
    public const string Message = "應用程式已關閉，這項變更會改寫既有資料，請先重新開啟應用程式";

    /// <summary>任一模組是 Closed 就回 409；否則 null，可以繼續。</summary>
    public static ServiceResult? Reject(IEnumerable<Module> affected) =>
        affected.Any(m => m.Status == ModuleStatus.Closed) ? ServiceResult.Conflict(Message) : null;

    public static ServiceResult? Reject(Module affected) => Reject([affected]);

    /// <summary>刪除欄位或區塊要清掉的記錄值、索引列或歷程列任何一項大於 0，而模組是 Closed 就擋；沒有東西要清的刪除照常放行。</summary>
    public static ServiceResult? RejectPurge(Module module, PurgeCounts purge) =>
        purge is { Records: 0, IndexRows: 0, HistoryRows: 0 } ? null : Reject(module);

    /// <summary>
    /// 公式重算會改寫模組自己的記錄，並連帶重算以它為來源的彙總所在的父模組（FormulaRecalc），所以兩者都算。
    /// </summary>
    public static async Task<ServiceResult?> RejectFormulaRecalcAsync(Module module, IModuleRepository modules, CancellationToken ct) =>
        Reject([module, .. (await modules.ListRollupsBySourceModuleAsync(module.Id, ct)).Select(x => x.Parent)]);
}
