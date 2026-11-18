using DynamicModules.Application.Dynamic.OpsService.Scheduling;

namespace DynamicModules.WebAPI.Ops;

/// <summary>
/// 背景排程器的迴圈（維運設計 §10.2.8）：啟動時補齊工作列，先等一個檢查週期（不和冷啟動的第一位訪客搶資源，O1 計畫判斷 J19），
/// 之後每個週期跑一次 <see cref="JobRunner.TickAsync"/>。部署設定 <c>Ops:Scheduler:Enabled</c> 為 false 時整個不跑。
/// 關閉時停止領新工作，執行中的工作收到取消、在 graceful shutdown 時間內寫回結果。
/// </summary>
public sealed class SchedulerHostedService(JobRunner runner, IConfiguration configuration, TimeProvider time, ILogger<SchedulerHostedService> logger)
    : BackgroundService
{
    public const string EnabledKey = "Ops:Scheduler:Enabled";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue(EnabledKey, true))
        {
            logger.LogInformation("背景排程器已由 {Setting} 關閉", EnabledKey);
            return;
        }
        try
        {
            await runner.EnsureJobsAsync(stoppingToken);
            using var timer = new PeriodicTimer(SchedulerLimits.Default.Tick, time);
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await runner.TickAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
