using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Repositories.Dynamic;

/// <summary>
/// 排程工作表（維運設計 §10.2.8）。租約、續租、結束都是條件式更新（影響列數 1 才算成功），
/// SQLite 與 PostgreSQL 同一份寫法，多實例時正確性不靠 WriteGate。
/// </summary>
public interface IScheduledJobRepository
{
    /// <summary>全部工作，不追蹤，依 JobKey 排序。</summary>
    Task<List<ScheduledJob>> ListAsync(CancellationToken ct);
    Task<bool> ExistsAsync(string jobKey, CancellationToken ct);
    /// <summary>加進追蹤，由呼叫端存檔。</summary>
    void Add(ScheduledJob job);
    /// <summary>
    /// 到期（NextDueUtc ≤ now）而且沒有人持有或租約已過期時，把租約給 owner、記開始時間。
    /// 條件含「到期」：慢一步讀到「到期」的實例，在別人跑完、清掉租約後不會再搶到一次而重跑。
    /// </summary>
    Task<bool> TryAcquireAsync(string jobKey, string owner, DateTime nowUtc, DateTime leaseUntilUtc, CancellationToken ct);
    /// <summary>仍是 owner 持有才延長租約。</summary>
    Task<bool> RenewAsync(string jobKey, string owner, DateTime leaseUntilUtc, CancellationToken ct);
    /// <summary>
    /// 仍是 owner 持有才寫結果並清租約：Succeeded 把連續失敗歸零，Failed／PartiallyFailed 加一，Cancelled 不動。
    /// </summary>
    Task<bool> FinishAsync(string jobKey, string owner, JobRunResult result, CancellationToken ct);
}
