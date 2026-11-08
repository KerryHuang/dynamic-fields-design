using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Behaviors;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.LedgerService;

/// <summary>
/// 寫入後盾（設計 §2 步驟 9、§6.4）：不依賴每個 handler 都記得擋。請求結束時看寫入日誌的合併結果，
/// 「不是這次請求新增的」分錄記錄（含分錄的明細）被修改或軟刪除，整筆回滾回 409。
/// 涵蓋彙總重算、連帶刪除的清空與刪除、事件 update 效果，以及未來新增的任何寫入路徑。
/// </summary>
public class LedgerBackstopStage(IWriteJournal journal, IModuleRepository modules, LedgerGate gate) : IWriteStage
{
    public int Order => 9;

    public async Task<ServiceResult?> RunAsync(CancellationToken ct)
    {
        foreach (var entry in JournalView.Merge(journal.Entries).Where(e => e.Kind == JournalKind.Modified))
        {
            var module = await modules.GetByIdAsync(entry.ModuleId, includeChildren: true, ct);
            if (module is not null && await gate.IsLedgerAsync(module, ct))
                return ServiceResult.Conflict($"{LedgerGate.Message}（模組「{module.Key}」的既有分錄被這次請求改動）");
        }
        return null;
    }
}
