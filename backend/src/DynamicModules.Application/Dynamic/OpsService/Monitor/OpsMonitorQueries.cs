using System.Globalization;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.OpsService.Audit;
using DynamicModules.Application.Dynamic.OpsService.Health;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Ops;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.OpsService.Monitor;

public sealed record GetOpsOverviewRequest : IRequest<ServiceResult>, ISystemAdminRequest;
public sealed record ListHealthChecksRequest : IRequest<ServiceResult>, ISystemAdminRequest;
public sealed record GetHealthCheckResultsRequest(string Code, DateTime? FromUtc, DateTime? ToUtc, int? Limit) : IRequest<ServiceResult>, ISystemAdminRequest;
public sealed record ListIncidentsRequest(string? Status, string? Category, string? Severity, string? Cursor, int? Limit)
    : IRequest<ServiceResult>, ISystemAdminRequest;
public sealed record GetIncidentRequest(long Id) : IRequest<ServiceResult>, ISystemAdminRequest;

/// <summary>監控頁的組裝（維運設計 §17.5）：目前投影讀資料庫，本實例記憶體裡較新的結果蓋上去並標 fromMemory。</summary>
public sealed class OpsMonitorReader(
    IHealthCheckRepository checks, IOpsIncidentRepository incidents, IDatabaseHealthProbe dbProbe, HealthCheckEngine engine, IOpsEnvironment env,
    AuthSettings auth, TimeProvider time)
{
    public const int NeedsAttentionMax = 10;

    public bool Applicable(HealthCheckDefinition d) => SupportedByDatabase(d) && !DemoExcluded(d);

    /// <summary>這個資料庫種類適不適用（PostgreSQL 的 WAL 檔大小不適用、畫面不列）。</summary>
    public bool SupportedByDatabase(HealthCheckDefinition d) => !d.RequiresFileDatabase || dbProbe.SupportsFileChecks;

    /// <summary>Demo 站（開 Demo 登入）不適用、出廠關閉：畫面照列、標「不適用（Demo 站）」。</summary>
    public bool DemoExcluded(HealthCheckDefinition d) => d.NotOnDemoSite && auth.DemoLoginEnabled;

    public async Task<List<HealthCheckState>> StatesAsync(IReadOnlyList<OpsIncident>? unresolved, CancellationToken ct)
    {
        var rows = (await checks.ListAsync(ct)).ToDictionary(r => r.Code, StringComparer.Ordinal);
        unresolved ??= await incidents.ListUnresolvedAsync(ct);
        var result = new List<HealthCheckState>();
        foreach (var def in HealthCheckCatalog.All)
            result.Add(await StateAsync(def, rows.GetValueOrDefault(def.Code), unresolved, ct));
        return result;
    }

    public async Task<HealthCheckState> StateAsync(HealthCheckDefinition def, HealthCheck? row, IReadOnlyList<OpsIncident> unresolved, CancellationToken ct)
    {
        var fingerprint = ErrorFingerprint.ForInfra("infra", def.Code);
        var incidentId = unresolved.FirstOrDefault(i => i.Fingerprint == fingerprint)?.Id;
        var common = (Category: Camel(def.Category), Runner: def.Runner == HealthRunner.Watchdog ? "watchdog" : "scheduler",
            Scope: def.Scope == HealthScope.Instance ? "instance" : "global");
        var nextDue = def.Runner == HealthRunner.Scheduler ? row?.NextDueUtc : null;
        if (!Applicable(def))
            return new(def.Code, common.Category, common.Runner, common.Scope, "notApplicable", null, def.Unit,
                DemoExcluded(def) ? HealthCheckCatalog.DemoSiteMessage : null, null, null, false, null, null);

        IReadOnlyList<HealthInstanceState>? instances = null;
        if (def.Scope == HealthScope.Instance && row is not null)
        {
            var now = time.GetUtcNow().UtcDateTime;
            var settings = EffectiveHealthSettings.Of(def, row);
            var latest = await checks.LatestPerInstanceAsync(def.Code, now,
                now - TimeSpan.FromMinutes((double)settings.IntervalMinutes * HealthCheckCatalog.OnlineIntervals), ct);
            if (latest.Count >= 2)
                instances = [.. latest.Select(r => new HealthInstanceState(r.InstanceId, HealthCheckCatalog.Camel(r.Status), r.Value, r.MessageCode, r.CheckedUtc))];
        }
        // 記憶體的結果比資料庫的投影新（例如資料庫寫不進去時）就以記憶體為準
        if (engine.Board.TryGetValue(def.Code, out var mem) && (row?.LastCheckedUtc is not { } at || mem.CheckedUtc > at))
            return new(def.Code, common.Category, common.Runner, common.Scope, HealthCheckCatalog.Camel(mem.Status), mem.Value, def.Unit,
                mem.MessageCode, mem.CheckedUtc, nextDue, true, incidentId, instances);
        return new(def.Code, common.Category, common.Runner, common.Scope,
            row?.LastStatus is { } st ? HealthCheckCatalog.Camel(st) : "pending", row?.LastValue, def.Unit, row?.LastMessageCode,
            row?.LastCheckedUtc, nextDue, false, incidentId, instances);
    }

    public IncidentSummary Summary(OpsIncident i) => new(
        i.Id, i.Fingerprint, Camel(i.Source), Camel(i.Category), Camel(i.Severity), Camel(i.Status), i.TitleKey, Incidents.IncidentTitles.FromJson(i.TitleParamsJson),
        i.Code, i.ModuleKey, i.RuleKind, i.RuleId, i.Route, i.OccurrenceCount, i.FirstSeenUtc, i.LastSeenUtc, i.FirstVersion, i.LastVersion,
        i.Regression, i.ReopenCount, i.AcknowledgedBy, i.AcknowledgedUtc, i.ResolvedBy, i.ResolvedUtc,
        i.ResolvedReason is { } r ? Camel(r) : null, Handler(i), ManualResolvable(i));

    private string Handler(OpsIncident i) => HandlerOf(i.Category, env.Edition);

    /// <summary>處理者標示（§17.2.2；監控頁與告警信共用）：O2 還沒有任何東西送出，產品缺陷不能寫「已回報」。</summary>
    public static string HandlerOf(IncidentCategory category, string edition) => category switch
    {
        IncidentCategory.Config => "designer",
        IncidentCategory.Infra => edition == "Cloud" ? "vendor" : "it",
        IncidentCategory.Security => "admin",
        _ => "contactVendor",
    };

    /// <summary>健康檢查、通道、規則暫停三類只能由系統自己結案（§17.2.2、O2-U4a）。</summary>
    public static bool ManualResolvable(OpsIncident i) => i.Source switch
    {
        IncidentSource.Server or IncidentSource.Client or IncidentSource.Security => true,
        IncidentSource.Config => i.Code != "autoPaused",
        _ => false,
    };

    public static string Camel<T>(T value) where T : struct, Enum
    {
        var s = value.ToString();
        return char.ToLowerInvariant(s[0]) + s[1..];
    }
}

public sealed class GetOpsOverviewHandler(
    OpsMonitorReader reader, IOpsIncidentRepository incidents, IScheduledJobRepository jobs, IAlertChannelRepository channels, IMailSettingsRepository mailSettings,
    HealthCheckEngine engine, IOpsEnvironment env, IDatabaseInfo database) : IRequestHandler<GetOpsOverviewRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetOpsOverviewRequest request, CancellationToken ct)
    {
        var unresolved = await incidents.ListUnresolvedAsync(ct);
        var states = await reader.StatesAsync(unresolved, ct);
        var worst = states.Select(s => s.Status).ToList();
        var overall = worst.Contains("critical") ? "critical"
            : worst.Contains("warning") || worst.Contains("error") ? "warning"
            : worst.Contains("ok") ? "ok" : "unknown";
        var counts = new OpenIncidentCounts(
            unresolved.Count(i => i.Severity == IncidentSeverity.Critical), unresolved.Count(i => i.Severity == IncidentSeverity.Warning),
            unresolved.Count(i => i.Severity == IncidentSeverity.Info),
            Enum.GetValues<IncidentCategory>().ToDictionary(OpsMonitorReader.Camel, c => unresolved.Count(i => i.Category == c)));
        var attention = unresolved.Where(i => i.Severity >= IncidentSeverity.Warning)
            .OrderByDescending(i => i.Severity).ThenByDescending(i => i.LastSeenUtc).ThenByDescending(i => i.Id)
            .Take(OpsMonitorReader.NeedsAttentionMax).Select(reader.Summary).ToList();
        var jobInfos = (await jobs.ListAsync(ct)).Select(j => new ScheduledJobInfo(
            j.JobKey, j.LastStartedUtc, j.LastFinishedUtc, j.LastStatus?.ToString(), j.ConsecutiveFailures, j.NextDueUtc, j.LeaseOwner)).ToList();
        // 通道區塊：沒設定或停用時畫面顯示「未設定，告警只在站內」（§17.2.6）；設定好＝寄信設定好且通道有收件人
        var channel = await channels.GetAsync(AlertChannel.Smtp, ct);
        return ServiceResult.Success(new OpsOverview(
            overall, engine.PendingCount > 0, engine.PendingCount, engine.PendingDropped, states, counts, attention, jobInfos,
            new ChannelOverview(AlertChannel.Smtp, Alerts.SmtpSettings.From(await mailSettings.GetAsync(ct), channel, requireEnabled: false) is not null, channel?.Enabled ?? false,
                channel?.LastSuccessUtc, channel?.LastFailureUtc, channel?.ConsecutiveFailures ?? 0),
            env.Version, env.Commit, env.Edition, database.Provider));
    }
}

public sealed class ListHealthChecksHandler(OpsMonitorReader reader, IHealthCheckRepository checks, IOpsIncidentRepository incidents)
    : IRequestHandler<ListHealthChecksRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(ListHealthChecksRequest request, CancellationToken ct)
    {
        var rows = (await checks.ListAsync(ct)).ToDictionary(r => r.Code, StringComparer.Ordinal);
        var unresolved = await incidents.ListUnresolvedAsync(ct);
        var list = new List<HealthCheckConfig>();
        foreach (var def in HealthCheckCatalog.All)
        {
            // 不適用於這個資料庫種類的檢查不列（§17.2.4 表：PostgreSQL 的 WAL 檔大小）
            if (!reader.SupportedByDatabase(def)) continue;
            var row = rows.GetValueOrDefault(def.Code);
            var s = EffectiveHealthSettings.Of(def, row);
            // Demo 站不適用的照列、applicable 為假，出廠關閉（存了開啟也不跑）
            var demoExcluded = reader.DemoExcluded(def);
            list.Add(new HealthCheckConfig(def.Code, OpsMonitorReader.Camel(def.Category),
                def.Runner == HealthRunner.Watchdog ? "watchdog" : "scheduler", def.Unit, OpsMonitorReader.Camel(def.Direction), !demoExcluded, def.CanDisable,
                demoExcluded ? new Setting<bool>(false, false, row?.Enabled is null) : new Setting<bool>(s.Enabled, def.EnabledFactory, row?.Enabled is null),
                new Setting<int>(s.IntervalMinutes, def.IntervalFactory, row?.IntervalMinutes is null, def.IntervalMin, def.IntervalMax),
                def.Warn is { } w ? new Setting<long>(s.Warn!.Value, w.Factory, row?.WarnThreshold is null, w.Min, w.Max) : null,
                def.Critical is { } c ? new Setting<long>(s.Critical!.Value, c.Factory, row?.CriticalThreshold is null, c.Min, c.Max) : null,
                await reader.StateAsync(def, row, unresolved, ct)));
        }
        return ServiceResult.Success(list);
    }
}

public sealed class GetHealthCheckResultsHandler(IHealthCheckRepository checks, TimeProvider time)
    : IRequestHandler<GetHealthCheckResultsRequest, ServiceResult>
{
    // 技術限制（§17.5）：每次最多 500 筆、預設 288 筆（5 分鐘一筆的一天）
    public const int MaxLimit = 500, DefaultLimit = 288;

    public async Task<ServiceResult> Handle(GetHealthCheckResultsRequest request, CancellationToken ct)
    {
        if (HealthCheckCatalog.Find(request.Code) is null) return ServiceResult.NotFound(MessageRef.Of(MessageText.HealthCheckNotFound));
        var limit = request.Limit ?? DefaultLimit;
        if (limit is < 1 or > MaxLimit) return ServiceResult.Invalid("limit", MessageRef.Of(MessageText.AuditLimitOutOfRange, MaxLimit));
        var to = request.ToUtc?.ToUniversalTime() ?? time.GetUtcNow().UtcDateTime;
        var from = request.FromUtc?.ToUniversalTime() ?? to.AddHours(-24);
        if (from > to) return ServiceResult.Invalid("fromUtc", MessageRef.Of(MessageText.AuditRangeReversed));
        var rows = await checks.ResultsAsync(request.Code, from, to, limit + 1, ct);
        return ServiceResult.Success(new HealthResultsResponse(
            rows.Take(limit).Select(r => new HealthResultPoint(r.CheckedUtc, HealthCheckCatalog.Camel(r.Status), r.Value, r.DurationMs)).ToList(),
            rows.Count > limit));
    }
}

public sealed class ListIncidentsHandler(OpsMonitorReader reader, IOpsIncidentRepository incidents) : IRequestHandler<ListIncidentsRequest, ServiceResult>
{
    public const int MaxLimit = 100, DefaultLimit = 50;

    public async Task<ServiceResult> Handle(ListIncidentsRequest r, CancellationToken ct)
    {
        var limit = r.Limit ?? DefaultLimit;
        if (limit is < 1 or > MaxLimit) return ServiceResult.Invalid("limit", MessageRef.Of(MessageText.AuditLimitOutOfRange, MaxLimit));
        List<IncidentStatus>? statuses = null;
        if (!string.IsNullOrWhiteSpace(r.Status))
        {
            statuses = [];
            foreach (var part in r.Status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (Parse<IncidentStatus>(part) is { } st) statuses.Add(st);
                else return ServiceResult.Invalid("status", MessageRef.Of(MessageText.AuditFilterInvalid));
        }
        IncidentCategory? category = null;
        if (!string.IsNullOrEmpty(r.Category) && (category = Parse<IncidentCategory>(r.Category)) is null)
            return ServiceResult.Invalid("category", MessageRef.Of(MessageText.AuditFilterInvalid));
        IncidentSeverity? severity = null;
        if (!string.IsNullOrEmpty(r.Severity) && (severity = Parse<IncidentSeverity>(r.Severity)) is null)
            return ServiceResult.Invalid("severity", MessageRef.Of(MessageText.AuditFilterInvalid));
        long? before = null;
        if (r.Cursor is not null)
        {
            if (!long.TryParse(r.Cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var c)) return ServiceResult.Invalid("cursor", MessageRef.Of(MessageText.AuditCursorInvalid));
            before = c;
        }
        var rows = await incidents.PageAsync(new OpsIncidentQuery(statuses, category, severity, before), limit + 1, ct);
        var page = rows.Take(limit).ToList();
        return ServiceResult.Success(new IncidentListResponse(page.Select(reader.Summary).ToList(),
            rows.Count > limit ? page[^1].Id.ToString(CultureInfo.InvariantCulture) : null));
    }

    /// <summary>只認 camelCase 的列舉名稱（區分大小寫）。</summary>
    internal static T? Parse<T>(string value) where T : struct, Enum =>
        Enum.GetValues<T>().Cast<T?>().FirstOrDefault(v => OpsMonitorReader.Camel(v!.Value) == value);
}

public sealed class GetIncidentHandler(
    OpsMonitorReader reader, IOpsIncidentRepository incidents, IHealthCheckRepository checks, IAuditLogRepository auditLog)
    : IRequestHandler<GetIncidentRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetIncidentRequest request, CancellationToken ct)
    {
        if (await incidents.GetAsync(request.Id, ct) is not { } i) return ServiceResult.NotFound(MessageRef.Of(MessageText.IncidentNotFound));
        var samples = (await incidents.ListSamplesAsync(i.Id, ct)).Select(s => new IncidentSampleDto(
            s.OccurredUtc, s.RequestId, s.UserId, s.Version, s.Route, s.RawMessage, s.RawStack, s.Browser, s.Locale, s.AuditLogId)).ToList();
        IReadOnlyList<HealthInstanceState>? instances = null;
        if (i.Source == IncidentSource.Health && HealthCheckCatalog.Find(i.Code) is { } def)
        {
            var row = (await checks.ListAsync(ct)).FirstOrDefault(c => c.Code == def.Code);
            instances = (await reader.StateAsync(def, row, [i], ct)).Instances;
        }
        // 狀態歷程：這件事件的確認與結案（從稽核讀；時間不限，只看這個目標）；多讀一筆判斷有沒有截斷，完整的到稽核頁查
        var entries = await auditLog.PageAsync(new AuditLogQuery(DateTime.UnixEpoch, new DateTime(9999, 1, 1, 0, 0, 0, DateTimeKind.Utc), TargetKind: AuditTargets.Incident,
                TargetId: i.Id.ToString(CultureInfo.InvariantCulture)), IncidentDetail.HistoryMax + 1, ct);
        var history = entries.Take(IncidentDetail.HistoryMax)
            .Select(a => new AuditSummaryResponse(a.Id, a.OccurredUtc, a.ActorUserId, null, a.ActorKind, a.Channel, a.TokenId, a.Action, a.RiskLevel,
                a.TargetKind, a.TargetId, a.TargetLabel, a.Result, a.ReasonCode, a.RequestId, null)).ToList();
        return ServiceResult.Success(new IncidentDetail(reader.Summary(i), i.NormalizedMessage, samples, instances, history,
            entries.Count > IncidentDetail.HistoryMax));
    }
}
