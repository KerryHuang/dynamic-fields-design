using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.DeleteService;

/// <summary>
/// 實際清除並移除一個模組，附事後條件（模組刪除後不可殘留記錄列，否則丟例外讓外層交易回滾）。
/// 模組層級的刪除與應用程式層級的刪除共用這一份，兩邊的判斷條件才會是同一份程式。
/// </summary>
public class ModuleRemover(IModuleRepository modules, IRecordRepository records, IDeletionRepository deletion, IUnitOfWork uow)
{
    public async Task RemoveAsync(long moduleId, CancellationToken ct)
    {
        var module = (await modules.GetByIdAsync(moduleId, includeChildren: true, ct))!;
        await deletion.PurgeModuleAsync(module, ct);
        modules.Remove(module);
        await uow.CommitAsync(ct);
        // 事後條件：讀已提交的資料庫上，檢查與清除之間可能有人寫入
        if (await records.AnyByModuleAsync(moduleId, ct))
            throw new InvalidOperationException("刪除模組後仍有記錄，已回滾");
    }
}
