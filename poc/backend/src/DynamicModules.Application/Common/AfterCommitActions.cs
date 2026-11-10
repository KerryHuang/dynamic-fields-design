using Microsoft.Extensions.Logging;

namespace DynamicModules.Application.Common;

/// <summary>
/// 最外層交易提交之後才做的收尾（例如刪實體檔）：回滾時整批丟掉；巢狀的交易型請求只由最外層執行。
/// 單一動作失敗只記 log，不丟例外——資料已經提交，不能再回滾。每個請求一份，執行或丟掉後清空，不跨請求保留。
/// 不在交易型請求裡登記的動作不會執行（留給後續的清掃）。
/// </summary>
public sealed class AfterCommitActions(ILogger<AfterCommitActions> logger)
{
    private readonly List<(string Label, Action Action)> _actions = [];
    private int _depth;

    public void Add(string label, Action action) => _actions.Add((label, action));

    internal void Enter() => _depth++;

    /// <summary>離開一層交易；回到最外層時，提交成功就執行、否則丟掉。</summary>
    internal void Leave(bool committed)
    {
        if (--_depth > 0) return;
        var batch = _actions.ToList();
        _actions.Clear();
        if (!committed) return;
        foreach (var (label, action) in batch)
        {
            try { action(); }
            catch (Exception ex) { logger.LogWarning(ex, "交易提交後的收尾失敗：{Label}", label); }
        }
    }
}
