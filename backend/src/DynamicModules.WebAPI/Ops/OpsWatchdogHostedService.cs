using DynamicModules.Application.Dynamic.OpsService.Health;
using DynamicModules.Application.Dynamic.OpsService.Incidents;

namespace DynamicModules.WebAPI.Ops;

/// <summary>
/// 看門狗的迴圈（維運設計 §17.2.4）：每個實例都跑、不經排程器，所以 <c>Ops:Scheduler:Enabled</c> 關掉時照跑（C07）。
/// 部署設定 <c>Ops:Watchdog:Enabled</c> 為 false 時整個不跑（測試主機關掉，改直接呼叫 <see cref="OpsWatchdog.TickAsync"/>）。
/// 第二個迴圈寄直送信（§17.2.6，同樣每 60 秒）：和檢查分開跑，SMTP 卡住（最長 30 秒一封）不拖住檢查。
/// </summary>
public sealed class OpsWatchdogHostedService(OpsWatchdog watchdog, IConfiguration configuration, TimeProvider time, ILogger<OpsWatchdogHostedService> logger)
    : BackgroundService
{
    public const string EnabledKey = "Ops:Watchdog:Enabled";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue(EnabledKey, true))
        {
            logger.LogInformation("看門狗已由 {Setting} 關閉", EnabledKey);
            return;
        }
        await Task.WhenAll(LoopAsync(watchdog.TickAsync, "看門狗這一輪失敗", stoppingToken),
            LoopAsync(watchdog.SendDirectAsync, "直送信這一輪失敗", stoppingToken));
    }

    private async Task LoopAsync(Func<CancellationToken, Task> round, string failure, CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(OpsWatchdog.Tick, time);
        try
        {
            do
            {
                try
                {
                    await round(stoppingToken);
                }
                catch (Exception e) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(e, "{Failure}", failure);
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}

/// <summary>
/// 事件收件匣的定時 flush（維運設計 §17.2.3）：每 5 秒一次，關機時最後再 flush 一次（graceful shutdown 時間內）。
/// 不受 <c>Ops:Watchdog:Enabled</c> 影響（§17.2.8、§17.2.11 的看門狗開關只停看門狗；審查修正）：500 與前端錯誤一定會被寫進資料庫。
/// 測試主機把這個 hosted service 拿掉（TestApp），測試直接呼叫 <see cref="IncidentInboxDrain.FlushAsync"/>。
/// </summary>
public sealed class IncidentInboxFlusher(IncidentInboxDrain drain, TimeProvider time, ILogger<IncidentInboxFlusher> logger)
    : BackgroundService
{
    /// <summary>flush 頻率（技術限制，§17.9）。</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await FlushQuietlyAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await FlushQuietlyAsync(cancellationToken);
    }

    private async Task FlushQuietlyAsync(CancellationToken ct)
    {
        try
        {
            await drain.FlushAsync(ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(e, "事件收件匣 flush 失敗，下一輪再試");
        }
    }
}

/// <summary>以 <see cref="DriveInfo"/> 量目錄所在磁碟的剩餘空間（§17.12：macOS 實測回目錄所在的那顆磁碟；目錄不存在回 null）。</summary>
public sealed class DriveInfoDiskSpace : IDiskSpace
{
    public DiskUsage? Measure(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return null;
            var drive = new DriveInfo(directory);
            return drive.TotalSize > 0 ? new DiskUsage(drive.AvailableFreeSpace, drive.TotalSize) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
