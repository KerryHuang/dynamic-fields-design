using System.Diagnostics.Metrics;

namespace DynamicModules.Application.Common.Observability;

/// <summary>
/// 維運自訂指標（.NET 內建 Meter，不裝 OpenTelemetry 套件，見 O1 計畫判斷 J30）。
/// 目前沒有匯出端；看的話用 <c>dotnet-counters monitor --counters Formwright</c>。
/// </summary>
public static class OpsMetrics
{
    public const string MeterName = "Formwright";

    public static readonly Meter Meter = new(MeterName);

    /// <summary>排程工作執行次數；tag <c>job</c>、<c>result</c>（succeeded／partiallyFailed／failed／timeout／cancelled／leaseLost）。</summary>
    public static readonly Counter<long> JobRuns = Meter.CreateCounter<long>("formwright.job.runs");

    public static readonly Histogram<double> JobDurationMs = Meter.CreateHistogram<double>("formwright.job.duration", unit: "ms");

    /// <summary>被 LoginThrottle 擋下的登入嘗試；不逐筆寫稽核（O2-T3：匿名者能觸發的寫入要有上限）。</summary>
    public static readonly Counter<long> LoginThrottled = Meter.CreateCounter<long>("formwright.login.throttled");

    public static readonly Counter<long> ClientErrors = Meter.CreateCounter<long>("formwright.client_errors");

    /// <summary>未處理例外且能歸屬到某條規則的次數；tag <c>ruleKind</c>。</summary>
    public static readonly Counter<long> RuleFailures = Meter.CreateCounter<long>("formwright.rule.failures");

    /// <summary>收件匣滿了丟掉的指紋（維運設計 §17.2.3）。</summary>
    public static readonly Counter<long> IncidentsDropped = Meter.CreateCounter<long>("formwright.incident.dropped");

    /// <summary>健康檢查待補寫佇列滿了丟掉的筆數（§17.2.4 第 3 步）。</summary>
    public static readonly Counter<long> PendingDropped = Meter.CreateCounter<long>("formwright.incident.pendingDropped");

    /// <summary>前端錯誤收件被限流（429）的次數（§17.2.3）。</summary>
    public static readonly Counter<long> ClientErrorsThrottled = Meter.CreateCounter<long>("formwright.client_errors.throttled");
}
