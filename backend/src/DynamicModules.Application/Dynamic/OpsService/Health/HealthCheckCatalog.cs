using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Application.Dynamic.OpsService.Health;

public enum HealthRunner { Watchdog, Scheduler }

/// <summary>多實例的彙總範圍（§17.2.4）：instance＝在線實例最糟者，global＝最新一筆。</summary>
public enum HealthScope { Instance, Global }

public enum HealthDirection { HigherIsWorse, LowerIsWorse, PassFail }

/// <summary>門檻的出廠值與技術上下限。</summary>
public sealed record ThresholdRange(long Factory, long Min, long Max);

/// <summary>一個內建檢查的登記（出廠值與技術上下限只寫在這裡，T12）。</summary>
public sealed record HealthCheckDefinition(
    string Code, string ZhName, HealthRunner Runner, HealthScope Scope, IncidentCategory Category, string Unit, HealthDirection Direction,
    bool CanDisable, int IntervalFactory, int IntervalMin, int IntervalMax, ThresholdRange? Warn, ThresholdRange? Critical,
    bool RequiresFileDatabase = false)
{
    public bool EnabledFactory => true;
}

/// <summary>生效的設定：規則表的值，null 的欄位讀出廠值。</summary>
public sealed record EffectiveHealthSettings(string Code, bool Enabled, int IntervalMinutes, long? Warn, long? Critical)
{
    public static EffectiveHealthSettings Of(HealthCheckDefinition d, HealthCheck? row) => new(
        d.Code, d.CanDisable ? row?.Enabled ?? d.EnabledFactory : true, row?.IntervalMinutes ?? d.IntervalFactory,
        d.Warn is null ? null : row?.WarnThreshold ?? d.Warn.Factory, d.Critical is null ? null : row?.CriticalThreshold ?? d.Critical.Factory);
}

/// <summary>
/// 健康檢查目錄（維運設計 §17.2.4、§17.6.2）。七個內建檢查；看門狗型的彙總範圍是 instance，排程型是 global。
/// </summary>
public static class HealthCheckCatalog
{
    public const string DatabaseWritable = "database.writable", StorageDiskFree = "storage.diskFree", SchedulerJobs = "scheduler.jobs",
        DatabaseWalSize = "database.walSize", DatabaseIntegrity = "database.integrity", BackupFreshness = "backup.freshness",
        SecurityLoginFailures = "security.loginFailures";

    /// <summary>單一檢查最長執行時間（技術限制，§17.9）；逾時記 error。</summary>
    public static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(60);

    /// <summary>實例幾個檢查間隔沒有回報就視為下線、退出彙總（技術限制，§17.9）。</summary>
    public const int OnlineIntervals = 3;

    private const long Mb = 1024L * 1024;

    public static IReadOnlyList<HealthCheckDefinition> All { get; } =
    [
        new(DatabaseWritable, "資料庫可寫", HealthRunner.Watchdog, HealthScope.Instance, IncidentCategory.Infra, "none", HealthDirection.PassFail,
            CanDisable: false, 1, 1, 60, Warn: new(3, 1, 60), Critical: null),
        new(StorageDiskFree, "磁碟剩餘空間", HealthRunner.Watchdog, HealthScope.Instance, IncidentCategory.Infra, "basisPoints", HealthDirection.LowerIsWorse,
            CanDisable: true, 5, 1, 1440, Warn: new(2000, 100, 5000), Critical: new(1000, 50, 5000)),
        new(SchedulerJobs, "排程器", HealthRunner.Watchdog, HealthScope.Instance, IncidentCategory.Defect, "count", HealthDirection.HigherIsWorse,
            CanDisable: true, 5, 1, 1440, Warn: new(3, 1, 100), Critical: new(10, 1, 1000)),
        new(DatabaseWalSize, "資料庫 WAL 檔大小", HealthRunner.Scheduler, HealthScope.Global, IncidentCategory.Infra, "bytes", HealthDirection.HigherIsWorse,
            CanDisable: true, 15, 5, 1440, Warn: new(256 * Mb, 16 * Mb, 16 * 1024 * Mb), Critical: new(1024 * Mb, 16 * Mb, 64 * 1024 * Mb),
            RequiresFileDatabase: true),
        new(DatabaseIntegrity, "資料完整性", HealthRunner.Scheduler, HealthScope.Global, IncidentCategory.Infra, "count", HealthDirection.HigherIsWorse,
            CanDisable: true, 1440, 60, 10080, Warn: null, Critical: new(1, 1, 1)),
        new(BackupFreshness, "備份新鮮度", HealthRunner.Scheduler, HealthScope.Global, IncidentCategory.Infra, "hours", HealthDirection.HigherIsWorse,
            CanDisable: true, 60, 15, 1440, Warn: new(26, 1, 720), Critical: new(50, 1, 720)),
        new(SecurityLoginFailures, "登入失敗次數", HealthRunner.Scheduler, HealthScope.Global, IncidentCategory.Security, "count", HealthDirection.HigherIsWorse,
            CanDisable: true, 5, 1, 60, Warn: new(20, 1, 10000), Critical: new(100, 1, 100000)),
    ];

    private static readonly Dictionary<string, HealthCheckDefinition> ByCode = All.ToDictionary(d => d.Code, StringComparer.Ordinal);

    public static HealthCheckDefinition? Find(string code) => ByCode.GetValueOrDefault(code);

    /// <summary>依門檻與方向判斷狀態（數值檢查用）。</summary>
    public static HealthStatus Evaluate(HealthCheckDefinition d, EffectiveHealthSettings s, long value) => d.Direction switch
    {
        HealthDirection.HigherIsWorse when s.Critical is { } c && value >= c => HealthStatus.Critical,
        HealthDirection.HigherIsWorse when s.Warn is { } w && value >= w => HealthStatus.Warning,
        HealthDirection.LowerIsWorse when s.Critical is { } c && value <= c => HealthStatus.Critical,
        HealthDirection.LowerIsWorse when s.Warn is { } w && value <= w => HealthStatus.Warning,
        _ => HealthStatus.Ok,
    };

    /// <summary>彙總與事件用的嚴重程度排序：error 當警告處理（§17.2.4），不開事件的狀態是 0。</summary>
    public static int Rank(HealthStatus s) => s switch
    {
        HealthStatus.Critical => 3, HealthStatus.Warning => 2, HealthStatus.Error => 1, _ => 0,
    };

    public static string Camel(HealthStatus s) => s switch
    {
        HealthStatus.Ok => "ok", HealthStatus.Warning => "warning", HealthStatus.Critical => "critical", HealthStatus.Error => "error",
        HealthStatus.NotConfigured => "notConfigured", HealthStatus.NotApplicable => "notApplicable", _ => "pending",
    };
}
