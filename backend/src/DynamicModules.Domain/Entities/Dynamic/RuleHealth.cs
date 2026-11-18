namespace DynamicModules.Domain.Entities.Dynamic;

/// <summary>
/// 規則的健康狀態（維運設計 §17.4 <c>Dynamic_RuleHealth</c>、§17.2.7）：主鍵（規則種類、規則 id），O2 只有 <c>taskRule</c>。
/// 不設外鍵（日後的規則種類在不同表），刪規則時由 handler 一起刪。暫停只看 <see cref="PausedUtc"/>，不動規則的 Active。
/// </summary>
public class RuleHealth
{
    public string RuleKind { get; set; } = "";
    public long RuleId { get; set; }
    /// <summary>最近一次成功之後的失敗總數（只顯示用，暫停看 <see cref="RuleFailure"/>）。</summary>
    public int ConsecutiveFailures { get; set; }
    /// <summary>最近一次成功的評估時間或恢復的時間；不晚於它的失敗不算；只往後推。</summary>
    public DateTime? StreakResetUtc { get; set; }
    /// <summary>已補記的失敗裡最晚的評估時間。</summary>
    public DateTime? LastFailureUtc { get; set; }
    /// <summary>例外型別短名或錯誤碼（不存訊息文字）。</summary>
    public string? LastErrorCode { get; set; }
    public string? LastRequestId { get; set; }
    /// <summary>非空＝系統暫停中。</summary>
    public DateTime? PausedUtc { get; set; }
    /// <summary>最近一次暫停的開始時間，恢復後保留。</summary>
    public DateTime? LastPausedUtc { get; set; }
    public int PauseCount { get; set; }
    /// <summary>最近一次暫停期間略過幾次，下次暫停時歸零。</summary>
    public int SkippedWhilePaused { get; set; }
    public DateTime? ResumedUtc { get; set; }
    public string? ResumedBy { get; set; }
}

/// <summary>滑動窗口用的失敗時間（§17.4 <c>Dynamic_RuleFailure</c>；O2-U12）；不設外鍵（同 <see cref="RuleHealth"/>）。</summary>
public class RuleFailure
{
    public long Id { get; set; }
    public string RuleKind { get; set; } = "";
    public long RuleId { get; set; }
    /// <summary>失敗的規則評估時間，不是補記時間。</summary>
    public DateTime FailedUtc { get; set; }
    public string? RequestId { get; set; }
}
