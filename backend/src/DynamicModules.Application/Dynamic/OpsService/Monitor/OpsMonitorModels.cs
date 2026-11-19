using DynamicModules.Application.Dynamic.OpsService.Audit;

namespace DynamicModules.Application.Dynamic.OpsService.Monitor;

// 維運設計 §17.5 的 DTO（欄位名 camelCase、時間 UTC）；列舉一律送 camelCase 字串

public sealed record HealthInstanceState(string InstanceId, string Status, long? Value, string? MessageCode, DateTime CheckedUtc);

public sealed record HealthCheckState(
    string Code, string Category, string Runner, string Scope, string Status, long? Value, string Unit, string? MessageCode,
    DateTime? CheckedUtc, DateTime? NextDueUtc, bool FromMemory, long? IncidentId, IReadOnlyList<HealthInstanceState>? Instances);

public sealed record Setting<T>(T Value, T Factory, bool IsFactory, long? Min = null, long? Max = null);

public sealed record HealthCheckConfig(
    string Code, string Category, string Runner, string Unit, string Direction, bool Applicable, bool CanDisable,
    Setting<bool> Enabled, Setting<int> IntervalMinutes, Setting<long>? Warn, Setting<long>? Critical, HealthCheckState State);

public sealed record HealthResultPoint(DateTime CheckedUtc, string Status, long? Value, int DurationMs);

public sealed record HealthResultsResponse(IReadOnlyList<HealthResultPoint> Items, bool Truncated);

public record IncidentSummary(
    long Id, string Fingerprint, string Source, string Category, string Severity, string Status, string TitleKey,
    IReadOnlyDictionary<string, object>? TitleParams, string Code, string? ModuleKey, string? RuleKind, long? RuleId, string? Route,
    long OccurrenceCount, DateTime FirstSeenUtc, DateTime LastSeenUtc, string FirstVersion, string LastVersion, bool Regression, int ReopenCount,
    string? AcknowledgedBy, DateTime? AcknowledgedUtc, string? ResolvedBy, DateTime? ResolvedUtc, string? ResolvedReason,
    string Handler, bool ManualResolvable);

public sealed record IncidentSampleDto(
    DateTime OccurredUtc, string? RequestId, string? UserId, string? Version, string? Route, string? RawMessage, string? RawStack,
    string? Browser, string? Locale, long? AuditLogId);

/// <summary>事件詳細＝摘要的全部欄位＋正規化訊息、樣本、各實例狀態、確認與結案歷程（§17.5 <c>IncidentDetail extends IncidentSummary</c>）。</summary>
public sealed record IncidentDetail : IncidentSummary
{
    public IncidentDetail(IncidentSummary summary, string? normalizedMessage, IReadOnlyList<IncidentSampleDto> samples,
        IReadOnlyList<HealthInstanceState>? instances, IReadOnlyList<AuditSummaryResponse> history) : base(summary)
    {
        NormalizedMessage = normalizedMessage;
        Samples = samples;
        Instances = instances;
        History = history;
    }

    public string? NormalizedMessage { get; init; }
    public IReadOnlyList<IncidentSampleDto> Samples { get; init; }
    public IReadOnlyList<HealthInstanceState>? Instances { get; init; }
    public IReadOnlyList<AuditSummaryResponse> History { get; init; }
}

public sealed record IncidentListResponse(IReadOnlyList<IncidentSummary> Items, string? NextCursor);

public sealed record OpenIncidentCounts(int Critical, int Warning, int Info, IReadOnlyDictionary<string, int> ByCategory);

public sealed record ChannelOverview(string Kind, bool Configured, bool Enabled, DateTime? LastSuccessUtc, DateTime? LastFailureUtc, int ConsecutiveFailures);

public sealed record OpsOverview(
    string Overall, bool MemoryOnly, int PendingIncidentWrites, long PendingDropped, IReadOnlyList<HealthCheckState> Checks,
    OpenIncidentCounts OpenIncidents, IReadOnlyList<IncidentSummary> NeedsAttention, IReadOnlyList<ScheduledJobInfo> Jobs,
    ChannelOverview Channel, string Version, string Commit, string Edition, string Database);
