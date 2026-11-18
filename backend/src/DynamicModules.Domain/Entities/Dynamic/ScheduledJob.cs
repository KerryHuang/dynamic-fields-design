namespace DynamicModules.Domain.Entities.Dynamic;

/// <summary>排程工作最近一次執行的結果（維運設計 §10.4）。</summary>
public enum ScheduledJobStatus { Succeeded, PartiallyFailed, Failed, Cancelled }

/// <summary>
/// 一個登記的背景工作一列，只存目前狀態（不存每次執行歷程）：租約、最近一次的結果、下次預定時間（維運設計 §10.2.8、§10.4）。
/// 時間一律 UTC（名稱以 Utc 結尾，PostgreSQL 上是 timestamptz）；工作代碼一律小寫。
/// </summary>
public class ScheduledJob
{
    public string JobKey { get; set; } = "";
    /// <summary>目前持有租約的實例（<c>OpsInstance.InstanceId</c>）；沒有人在跑時為 null。</summary>
    public string? LeaseOwner { get; set; }
    public DateTime? LeaseUntilUtc { get; set; }
    public DateTime? LastStartedUtc { get; set; }
    public DateTime? LastFinishedUtc { get; set; }
    public ScheduledJobStatus? LastStatus { get; set; }
    /// <summary>最近一次錯誤摘要：例外型別＋正規化訊息，截斷到 <see cref="LastErrorMax"/>；不含記錄值。</summary>
    public string? LastError { get; set; }
    public int ConsecutiveFailures { get; set; }
    public DateTime NextDueUtc { get; set; }

    /// <summary>技術限制（維運設計 §12）。</summary>
    public const int LastErrorMax = 1000;
}

/// <summary>一次執行結束時要寫回的結果；<see cref="NextDueUtc"/> 由排程器依間隔政策算好。</summary>
public sealed record JobRunResult(ScheduledJobStatus Status, string? Error, DateTime FinishedUtc, DateTime NextDueUtc);
