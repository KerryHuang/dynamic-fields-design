namespace DynamicModules.Application.Dynamic.McpService.Writes;

/// <summary>
/// 一次寫入工具呼叫的稽核內容（mcp2a 計畫判斷 3）：動作代碼、<paramref name="TargetLabel"/>（固定是工具名稱，權杖頁「最近呼叫」讀這一欄）、
/// 參數雜湊、摘要（已去值，判斷 15）。目標由 <see cref="McpWriteTargets"/> 依請求與回應算。
/// </summary>
public sealed record McpWriteAudit(string Action, string TargetLabel, string ArgumentsHash, string? Summary);

/// <summary>
/// 寫入稽核的上膛狀態（scoped，mcp2a 計畫判斷 3）。執行器在 <c>tool.RunAsync</c> 之前 <see cref="Arm"/>；
/// <c>McpWriteAuditBehavior</c> 接手本次呼叫的第一個最外層寫入請求，提交後標記 <see cref="IsCommitted"/>。
/// 呼叫序號沿用確認碼的 <c>IMcpConfirmationScope.BeginCall</c>，前一次呼叫的提交不會被當成這一次的。
/// </summary>
public interface IMcpWriteScope
{
    void Arm(long call, McpWriteAudit audit);
    /// <summary>每次呼叫開始與結束時清掉上膛狀態（不清已提交的序號，執行器只問自己那一次）。</summary>
    void Disarm();
    bool IsArmed { get; }
    bool IsCommitted(long call);
}

public sealed class McpWriteScope : IMcpWriteScope
{
    private long _armedCall;
    private McpWriteAudit? _audit;
    private bool _taken;
    private long _committedCall;

    public bool IsArmed => _audit is not null;

    public void Arm(long call, McpWriteAudit audit)
    {
        (_armedCall, _audit, _taken) = (call, audit, false);
    }

    public void Disarm()
    {
        (_armedCall, _audit, _taken) = (0, null, false);
    }

    public bool IsCommitted(long call) => call > 0 && _committedCall == call;

    /// <summary>behavior 接手本次的寫入請求：沒上膛、或這次已接手過一個，回 false（呼叫端丟例外）。</summary>
    internal bool TryTake(out long call, out McpWriteAudit audit)
    {
        (call, audit) = (_armedCall, _audit!);
        if (_audit is null || _taken) return false;
        _taken = true;
        return true;
    }

    /// <summary>最外層交易提交後由 <c>AfterCommitActions</c> 呼叫，只標記接手時的序號。</summary>
    internal Action CommitMarker(long call) => () => _committedCall = call;
}
