using DynamicModules.Application.Dynamic.AttachmentService;
using DynamicModules.Application.Dynamic.EventService;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.DeleteService;

/// <summary>
/// 實際清除並移除一個模組，附事後條件（模組刪除後不可殘留記錄列，否則丟例外讓外層交易回滾）。
/// 模組層級的刪除與應用程式層級的刪除共用這一份，兩邊的判斷條件才會是同一份程式。
/// </summary>
public class ModuleRemover(
    IModuleRepository modules, IRecordRepository records, IDeletionRepository deletion, IConversionRepository conversions,
    IEventRepository events, ICheckRepository checks, ILedgerRepository ledgers, ITaskRepository tasks, IAttachmentStore store,
    IUnitOfWork uow)
{
    public async Task RemoveAsync(long moduleId, CancellationToken ct)
    {
        var module = (await modules.GetByIdAsync(moduleId, includeChildren: true, ct))!;
        var taskFiles = await tasks.ListAttachmentKeysByModuleAsync(moduleId, ct);
        await deletion.PurgeModuleAsync(module, ct);
        // 轉換定義對來源、目標模組的外鍵是 Restrict；走到這裡代表盤點已確認沒有應用程式外的引用，定義一起移除
        foreach (var c in await conversions.ListTouchingAsync(moduleId, ct))
            conversions.Remove(c);
        // 事件、檢查、分錄設定同樣是 Restrict 外鍵；盤點已確認引用都在這次一起刪除的集合內
        foreach (var e in await events.ListAsync(ct))
            if (EffectReferences.Of(e, (await modules.GetByIdAsync(e.SourceModuleId, includeChildren: false, ct))?.Key ?? "").ModuleKeys.Contains(module.Key))
                events.Remove((await events.GetByKeyAsync(e.Key, ct))!);
        foreach (var c in await checks.ListByModuleAsync(moduleId, ct)) checks.Remove((await checks.GetByKeyAsync(c.Key, ct))!);
        if (await ledgers.GetByModuleAsync(moduleId, ct) is { } ledger) ledgers.Remove(ledger);
        modules.Remove(module);
        await uow.CommitAsync(ct);
        // 事後條件：讀已提交的資料庫上，檢查與清除之間可能有人寫入
        if (await records.AnyByModuleAsync(moduleId, ct))
            throw new InvalidOperationException("刪除模組後仍有記錄，已回滾");
        // 任務留言附件的檔案：盡力而為；外層交易之後若回滾，資料列還在但檔案沒了，下載只會回找不到附件
        foreach (var key in taskFiles) store.Delete(key);
    }
}
