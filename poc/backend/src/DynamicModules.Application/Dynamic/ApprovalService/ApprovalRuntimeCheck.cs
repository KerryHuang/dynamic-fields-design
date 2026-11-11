using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Application.Dynamic.ApprovalService;

/// <summary>
/// 執行期再驗（設計 §5.2 末段、§8.3）：系統以系統身分走核准或退回轉移之前再查一次狀態機，fail closed。
/// 回傳的原因寫進給系統管理員的通知（資料庫），固定繁中。
/// </summary>
public static class ApprovalRuntimeCheck
{
    /// <summary>isReject = false：from → to 必須已定義。isReject = true：from → to（rejectTo）必須已定義、不需簽核；to 是鎖定狀態而 from 不是 → 失敗。回傳原因或 null。</summary>
    public static string? Verify(Workflow wf, long from, long to, bool isReject)
    {
        if (wf.Find(from, to) is not { } t) return $"轉移 {from} → {to} 已不在狀態機上";
        if (!isReject) return null;
        if (t.Approval is { Required: true }) return $"退回轉移 {from} → {to} 被設為需簽核";
        if (ApprovalTopology.IsLocked(wf, to) && !ApprovalTopology.IsLocked(wf, from)) return $"退回轉移 {from} → {to} 會讓單據不簽就進入鎖定狀態";
        return null;
    }

    /// <summary>可編輯欄位的豁免（設計 §8.5）：實例的 from 是簽核鎖定狀態時不放行。a2d 的鎖定資訊上線後改用同一個判斷來源。</summary>
    public static string? VerifyEditable(Workflow wf, long from) =>
        ApprovalTopology.IsLocked(wf, from) ? $"送出時的狀態 {from} 是簽核鎖定狀態，不能修改可編輯欄位" : null;
}
