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

    public static readonly Counter<long> ClientErrors = Meter.CreateCounter<long>("formwright.client_errors");

    /// <summary>未處理例外且能歸屬到某條規則的次數；tag <c>ruleKind</c>。</summary>
    public static readonly Counter<long> RuleFailures = Meter.CreateCounter<long>("formwright.rule.failures");
}
