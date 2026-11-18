namespace DynamicModules.Application.Dynamic.OpsService.Scheduling;

/// <summary>
/// 排程器的時間上限，技術限制（維運設計 §12）：檢查週期 30 秒、租約 5 分鐘、續租每 1 分鐘、單次執行最長 10 分鐘。
/// 只有測試會傳別的值。
/// </summary>
public sealed record SchedulerLimits(TimeSpan Tick, TimeSpan Lease, TimeSpan Renew, TimeSpan MaxRun)
{
    public static readonly SchedulerLimits Default =
        new(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10));
}
