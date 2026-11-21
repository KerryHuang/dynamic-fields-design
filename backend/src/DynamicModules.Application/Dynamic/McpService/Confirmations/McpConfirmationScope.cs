using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.McpService.Confirmations;

/// <summary>
/// 一次確認的狀態（scoped，計畫判斷 18）。handler 用 <see cref="ConsumeAsync"/> 消耗確認碼；執行器用 <see cref="BeginCall"/> 取得本次
/// <c>tools/call</c> 的序號，只認這個序號的 <see cref="IsCommitted"/>，前一次呼叫的提交不會被當成這一次的。
/// </summary>
public interface IMcpConfirmationScope
{
    /// <summary>執行器在每次 <c>tools/call</c> 一開始呼叫；清掉前一次的狀態並回傳本次序號。</summary>
    long BeginCall();
    /// <summary>目標的現在版本與預演時相同，且條件式標記已用成功才回 true；同一次確認只能成功一次。</summary>
    Task<bool> ConsumeAsync(string currentTargetVersion, CancellationToken ct);
    bool Consumed { get; }
    /// <summary>本次呼叫的確認已寫稽核、最外層交易也提交了。</summary>
    bool Committed { get; }
    bool IsCommitted(long call);
    long? ConfirmationId { get; }
}

public sealed class McpConfirmationScope(IMcpConfirmationRepository confirmations, TimeProvider time) : IMcpConfirmationScope
{
    private enum Mode { None, Required, NotRequired }

    private long _call;
    private long _committedCall;
    private Mode _mode;
    private McpConfirmation? _row;

    public bool Consumed { get; private set; }
    public long? ConfirmationId => _row?.Id;
    public bool Committed => IsCommitted(_call);
    public bool IsCommitted(long call) => call > 0 && _committedCall == call;

    public long BeginCall()
    {
        Reset();
        _committedCall = 0;
        return ++_call;
    }

    /// <summary>behavior 在每次頂層確認請求開始時呼叫：MCP 通道載入驗過的確認列；回傳發起時的呼叫序號。</summary>
    internal long BeginRequired(McpConfirmation row)
    {
        Reset();
        _mode = Mode.Required;
        _row = row;
        return _call;
    }

    /// <summary>畫面通道沒有確認碼：消耗一律成功，不碰資料庫。</summary>
    internal void BeginNotRequired()
    {
        Reset();
        _mode = Mode.NotRequired;
    }

    /// <summary>最外層交易提交後由 <c>AfterCommitActions</c> 呼叫，只標記發起時的序號。</summary>
    internal Action CommitMarker(long call) => () => _committedCall = call;

    public async Task<bool> ConsumeAsync(string currentTargetVersion, CancellationToken ct)
    {
        if (_mode == Mode.NotRequired) return Consumed = true;
        // 沒經過 behavior（不是確認請求卻呼叫消耗）或已消耗過：fail closed
        if (_mode != Mode.Required || _row is null || Consumed) return false;
        if (!string.Equals(_row.TargetVersion, currentTargetVersion, StringComparison.Ordinal)) return false;
        if (await confirmations.TryConsumeAsync(_row.Id, time.GetUtcNow().UtcDateTime, ct) != 1) return false;
        return Consumed = true;
    }

    private void Reset()
    {
        _mode = Mode.None;
        _row = null;
        Consumed = false;
    }
}
