using System.Text.RegularExpressions;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Observability;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Application.Dynamic.OpsService.Incidents;
using DynamicModules.Domain.Ops;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;
using Microsoft.Extensions.Logging;

namespace DynamicModules.Application.Dynamic.OpsService;

public class GetOpsInfoHandler(
    IOpsEnvironment env, OpsInstance instance, IDatabaseInfo database, ISystemSettingRepository settings, IScheduledJobRepository jobs)
    : IRequestHandler<GetOpsInfoRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetOpsInfoRequest request, CancellationToken ct) =>
        ServiceResult.Success(new OpsInfoResponse(
            env.Version, env.Commit, env.BuiltAt, env.Edition, instance.StartedAtUtc, instance.InstanceId,
            database.Provider, await database.LatestMigrationAsync(ct),
            await settings.GetValueAsync(SystemSetting.IndexBackfillVersion, ct),
            Jobs: (await jobs.ListAsync(ct)).Select(j => new ScheduledJobInfo(
                j.JobKey, j.LastStartedUtc, j.LastFinishedUtc, j.LastStatus?.ToString(), j.ConsecutiveFailures, j.NextDueUtc, j.LeaseOwner)).ToList()));
}

public partial class ReportClientErrorHandler(
    CachedSystemPolicy policy, IIncidentInbox inbox, IOpsEnvironment env, TimeProvider time, ILogger<ReportClientErrorHandler> logger)
    : IRequestHandler<ReportClientErrorRequest, ServiceResult>
{
    public static readonly EventId ClientError = new(5001, "ClientError");

    public async Task<ServiceResult> Handle(ReportClientErrorRequest request, CancellationToken ct)
    {
        var r = request.Report;
        if (!ClientErrorLimits.Kinds.Contains(r.Kind, StringComparer.Ordinal))
            return ServiceResult.Invalid(new Dictionary<string, string[]> { ["kind"] = [$"kind 必須是 {string.Join("、", ClientErrorLimits.Kinds)} 之一"] });
        // 政策關閉：收下但不記（前端不必知道）
        if (!await policy.BoolAsync(PolicyCatalog.OpsClientErrorCollection, ct)) return ServiceResult.Success();

        var name = Cut(r.Name, ClientErrorLimits.NameMax);
        var message = Cut(r.Message, ClientErrorLimits.MessageMax);
        var stack = Cut(r.Stack, ClientErrorLimits.StackMax);
        var route = CleanRoute(r.Route);
        var frames = ErrorFingerprint.ClientFrames(stack);
        var normalized = ErrorFingerprint.NormalizeMessage(message);
        var fingerprint = ErrorFingerprint.ForClient(r.Kind, name, message, frames, route);
        var lastRequestId = r.LastRequestId is not null && RequestIdPattern().IsMatch(r.LastRequestId) ? r.LastRequestId : null;

        logger.Log(LogLevel.Warning, ClientError,
            "前端錯誤 {ErrorKind} {ErrorName} {NormalizedMessage} 指紋 {Fingerprint} 版本 {FingerprintVersion} 路由 {Route} 模組 {ModuleKey} 前端版本 {ClientVersion} 語系 {Locale} 瀏覽器 {Browser} 最近失敗請求 {LastRequestId} 使用者 {UserId} 原文 {RawMessage} 堆疊 {RawStack}",
            r.Kind, name, normalized, fingerprint, ErrorFingerprint.Version, route,
            Cut(r.ModuleKey, ClientErrorLimits.ModuleKeyMax), Cut(r.Version, ClientErrorLimits.VersionMax),
            Cut(r.Locale, ClientErrorLimits.LocaleMax), Cut(r.Browser, ClientErrorLimits.BrowserMax),
            lastRequestId, request.UserId, message, stack);
        OpsMetrics.ClientErrors.Add(1);
        // 事件（維運設計 §17.2.2）：產品缺陷、資訊級（O2-U5，不通知）；只放進記憶體收件匣，不在請求裡寫資料庫
        var moduleKey = Cut(r.ModuleKey, ClientErrorLimits.ModuleKeyMax);
        var now = time.GetUtcNow().UtcDateTime;
        inbox.Post(new IncidentSignal
        {
            Fingerprint = fingerprint, Source = IncidentSource.Client, Category = IncidentCategory.Defect, Severity = IncidentSeverity.Info,
            Code = Cut($"{r.Kind}:{name ?? "Error"}", 100)!, TitleKey = IncidentTitles.ClientError, TitleParams = IncidentTitles.Client(name, route, moduleKey),
            NormalizedMessage = SensitiveTextMask.Apply(ErrorFingerprint.NormalizeMessage(SensitiveTextMask.Apply(message))),
            ModuleKey = moduleKey, Route = IncidentTitles.RouteTemplate(route), OccurredUtc = now, Version = Cut(r.Version, ClientErrorLimits.VersionMax) ?? env.Version,
            RequestId = lastRequestId,
            Sample = new IncidentSampleData(now, lastRequestId, request.UserId, Cut(r.Version, ClientErrorLimits.VersionMax), route,
                RawMessage: message, RawStack: stack, Browser: Cut(r.Browser, ClientErrorLimits.BrowserMax), Locale: Cut(r.Locale, ClientErrorLimits.LocaleMax)),
        });
        return ServiceResult.Success();
    }

    private static string? Cut(string? value, int max) => value is null ? null : value.Length > max ? value[..max] : value;

    /// <summary>去掉 ? 與 # 之後的部分；不是 / 開頭就丟掉（route 會進指紋，不能讓匿名使用者灌任意值）。</summary>
    private static string? CleanRoute(string? raw)
    {
        if (raw is null) return null;
        var cut = raw.IndexOfAny(['?', '#']);
        var route = cut >= 0 ? raw[..cut] : raw;
        return route.StartsWith('/') ? Cut(route, ClientErrorLimits.RouteMax) : null;
    }

    [GeneratedRegex("^[0-9a-f]{32}$")] private static partial Regex RequestIdPattern();
}
