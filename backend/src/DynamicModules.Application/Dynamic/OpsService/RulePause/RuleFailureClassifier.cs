using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Application.Dynamic.OpsService.RulePause;

/// <summary>
/// 規則失敗是否歸屬規則本身（維運設計 §17.2.7、E13 例外矩陣）：只計規則本身的錯。穿過規則迴圈的例外也可能是取消、並行衝突、
/// 凍結欄位、資料庫忙碌、查詢中斷、唯讀、空間滿、連不上、唯一鍵衝突，這些不是規則失控，計進去會讓一次資料庫故障把所有任務規則都暫停。
/// 型別判斷沿 InnerException 往內找（被包在別的例外裡也一樣不計）；資料庫分類交給 <see cref="IDatabaseErrors"/>（本身就沿內層找）。
/// 新增例外處理器或改 <see cref="IDatabaseErrors"/> 的分類時，要同時在 E13 矩陣加列（測試才會涵蓋）。
/// </summary>
public sealed class RuleFailureClassifier(IDatabaseErrors errors)
{
    public bool IsAttributable(Exception e)
    {
        for (var cur = e; cur is not null; cur = cur.InnerException)
            if (cur is OperationCanceledException or DbUpdateConcurrencyException or FrozenFieldException) return false;
        return !(errors.IsBusyOrConflict(e) || errors.IsQueryCanceled(e) || errors.IsReadOnly(e) || errors.IsStorageFull(e)
                 || errors.IsUnavailable(e) || errors.IsUniqueViolation(e));
    }
}
