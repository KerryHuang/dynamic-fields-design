using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Application.Dynamic.ApprovalService;

/// <summary>
/// 關卡執行列與任務的關閉動作（純函式，只改傳入的實體）。放在引擎與動作 handler 都能依賴的一側：
/// 原本放在 handler 層的 ApprovalActionSupport，ApprovalEngine、ApprovalGraphEngine 反過來依賴它（#26）。
/// </summary>
public static class ApprovalRunClosing
{
    /// <summary>排隊中的加簽關卡改 closed（退回、撤回；設計 §7.6）。</summary>
    public static void CloseQueued(IEnumerable<ApprovalStageRun> runs, DateTime now)
    {
        foreach (var r in runs.Where(r => r.Status == ApprovalStageStatus.Queued))
        {
            r.Status = ApprovalStageStatus.Closed;
            r.CompletedTime = now;
        }
    }

    /// <summary>
    /// graph 實例結束時（退回發起人、撤回、終止、重來）：還在走的執行線（active、waiting、queued）全部 closed，原因 instanceEnded（計畫 fg2 J14）。
    /// sequence 不呼叫，仍照第一版只關 active、queued 且不寫原因。
    /// </summary>
    public static void CloseLive(IEnumerable<ApprovalStageRun> runs, DateTime now)
    {
        foreach (var r in ApprovalGraphPath.Live(runs))
        {
            r.Status = ApprovalStageStatus.Closed;
            r.CloseReason = ApprovalCloseReasons.InstanceEnded;
            r.CompletedTime = now;
        }
    }

    /// <summary>open 任務改 closed（已由他人處理、撤回、退回）。</summary>
    public static void CloseOpen(IEnumerable<ApprovalTask> tasks)
    {
        foreach (var t in tasks.Where(t => t.Status == ApprovalTaskStatus.Open)) t.Status = ApprovalTaskStatus.Closed;
    }
}
