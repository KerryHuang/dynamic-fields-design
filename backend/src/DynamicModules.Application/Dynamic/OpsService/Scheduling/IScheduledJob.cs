using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;

namespace DynamicModules.Application.Dynamic.OpsService.Scheduling;

/// <summary>一次執行的結果；Error 是給 LastError 的摘要（不含記錄值）。</summary>
public sealed record JobOutcome(ScheduledJobStatus Status, string? Error = null);

/// <summary>
/// 一個背景工作（維運設計 §10.2.8）。註冊為 singleton；<see cref="RunAsync"/> 的 services 是這次執行專用的 DI scope，
/// 逐人處理的工作要自己為每個人另開 scope 與 ActingScope（主控判斷 B3）。
/// </summary>
public interface IScheduledJob
{
    /// <summary>工作代碼，一律小寫（字串主鍵不依賴大小寫比對，§10.7）。</summary>
    string Key { get; }

    /// <summary>間隔政策（分鐘、只限全系統）；下次預定時間＝結束時間＋生效值。</summary>
    PolicyDefinition Interval { get; }

    Task<JobOutcome> RunAsync(IServiceProvider services, CancellationToken ct);
}
