using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;

namespace DynamicModules.Application.Dynamic.OpsService.Scheduling;

/// <summary>一次執行的結果；Error 是給 LastError 的摘要（不含記錄值）。</summary>
public sealed record JobOutcome(ScheduledJobStatus Status, string? Error = null);

/// <summary>
/// 一個背景工作（維運設計 §10.2.8）。註冊為 singleton；逐人處理的工作要自己為每個人另開 scope 與 ActingScope（主控判斷 B3）。
/// 工作的寫入交易要帶 <see cref="JobRunContext.Fence"/>，在交易內確認仍持有租約，失效就回滾並丟 <see cref="JobLeaseLostException"/>。
/// </summary>
public interface IScheduledJob
{
    /// <summary>工作代碼，一律小寫（字串主鍵不依賴大小寫比對，§10.7）。</summary>
    string Key { get; }

    /// <summary>間隔政策（分鐘、只限全系統）；下次預定時間＝結束時間＋生效值。</summary>
    PolicyDefinition Interval { get; }

    Task<JobOutcome> RunAsync(JobRunContext context, CancellationToken ct);
}

/// <summary>這次執行的環境：專用的 DI scope、工作代碼、租約持有者與時鐘。</summary>
public sealed class JobRunContext(IServiceProvider services, string jobKey, string owner, TimeProvider time)
{
    public IServiceProvider Services { get; } = services;
    public string JobKey { get; } = jobKey;
    public string Owner { get; } = owner;

    /// <summary>給寫入交易用的租約檢查條件（取當下時間）。</summary>
    public JobLeaseFence Fence() => new(JobKey, Owner, time.GetUtcNow().UtcDateTime);
}

/// <summary>
/// 寫入交易內的租約檢查：這個持有者仍持有、而且租約還沒過期（條件式更新，影響列數 1 才算）。
/// PostgreSQL 上這個 UPDATE 持有工作列的列鎖到交易結束，別的實例要等它提交後才搶得到租約，所以工作的寫入不會和接手者重疊。
/// </summary>
public sealed record JobLeaseFence(string JobKey, string Owner, DateTime NowUtc)
{
    /// <summary>租約已失效時寫入請求回的 Conflict 代碼。</summary>
    public const string LostCode = "jobLeaseLost";
}

/// <summary>工作在寫入時發現租約已失效（別的實例已接手或已過期）：中止，不寫結果。</summary>
public sealed class JobLeaseLostException(string jobKey) : Exception($"排程工作 {jobKey} 的租約已失效");
