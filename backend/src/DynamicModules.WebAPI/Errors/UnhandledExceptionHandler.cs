using System.Security.Claims;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Observability;
using DynamicModules.Application.Dynamic.OpsService;
using DynamicModules.Application.Dynamic.OpsService.Incidents;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Ops;
using DynamicModules.WebAPI.Ops;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace DynamicModules.WebAPI.Errors;

/// <summary>
/// 最後一道例外處理（註冊在其他 handler 之後）：回 ProblemDetails 500 加四語 message，並記帶指紋的 Error log。
/// 框架自己的 4xx（<see cref="BadHttpRequestException"/>：主體超限、格式壞掉）保留原狀態碼，只記 Information。
/// </summary>
public sealed class UnhandledExceptionHandler(
    ILogger<UnhandledExceptionHandler> logger, IProblemDetailsService problems, IIncidentInbox inbox, IOpsEnvironment env, TimeProvider time)
    : IExceptionHandler
{
    public static readonly EventId UnhandledEvent = new(5000, "UnhandledException");
    public static readonly EventId FrameworkRejectedEvent = new(5002, "FrameworkRejected");

    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        // 使用者中止的請求不是錯誤（.NET 8 的框架通常先處理掉，這裡只是防禦）
        if (exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested) return true;

        if (exception is BadHttpRequestException bad)
        {
            // Message 是框架的固定文字（例如 Request body too large），不含輸入值
            logger.LogInformation(FrameworkRejectedEvent, "請求被框架拒絕 {StatusCode}：{Reason}", bad.StatusCode, bad.Message);
            context.Response.StatusCode = bad.StatusCode;
            await WriteAsync(context, bad.StatusCode, null);
            return true;
        }

        var ops = context.Features.Get<RequestOpsFeature>();
        // ExceptionHandlerMiddleware 已清掉 endpoint，路由樣板讀請求開始時存下的 feature
        var route = ops?.Route ?? (context.Features.Get<IExceptionHandlerFeature>()?.Endpoint as RouteEndpoint)?.RoutePattern.RawText;
        var (ruleKind, ruleId) = RuleFailureTag.Read(exception);
        if (ruleKind is not null) OpsMetrics.RuleFailures.Add(1, new KeyValuePair<string, object?>("ruleKind", ruleKind));
        var fingerprint = ServerErrorFingerprint.Of(exception, route);
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        logger.Log(LogLevel.Error, UnhandledEvent, exception,
            "未處理的例外 {ExceptionType} 指紋 {Fingerprint} 版本 {FingerprintVersion} 路由 {Route} 使用者 {UserId} 模組 {ModuleKey} 規則 {RuleKind}/{RuleId}",
            exception.GetType().FullName, fingerprint, ErrorFingerprint.Version, route, userId, ops?.ModuleKey, ruleKind, ruleId);
        PostIncident(context, exception, fingerprint, route, userId, ops?.ModuleKey, ruleKind, ruleId);
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await WriteAsync(context, StatusCodes.Status500InternalServerError, MessageText.ServerError);
        return true;
    }

    /// <summary>
    /// 產品缺陷事件（維運設計 §17.2.2–§17.2.3）：只放進記憶體收件匣，由背景 flush 寫入，請求本身不碰資料庫。
    /// 樣本留原始訊息與堆疊（只有管理員看得到）；事件列上的正規化訊息再過一次敏感字串遮罩。
    /// </summary>
    private void PostIncident(HttpContext context, Exception e, string fingerprint, string? route, string? userId, string? moduleKey,
        string? ruleKind, long? ruleId)
    {
        var type = e.GetType().FullName ?? e.GetType().Name;
        var now = time.GetUtcNow().UtcDateTime;
        inbox.Post(new IncidentSignal
        {
            Fingerprint = fingerprint, Source = IncidentSource.Server, Category = IncidentCategory.Defect, Severity = IncidentSeverity.Warning,
            Code = Cut(IncidentTitles.ShortName(type), 100), TitleKey = IncidentTitles.ServerError,
            TitleParams = IncidentTitles.Server(type, route, moduleKey),
            NormalizedMessage = SensitiveTextMask.Apply(ErrorFingerprint.NormalizeMessage(SensitiveTextMask.Apply(e.Message))),
            ModuleKey = moduleKey, RuleKind = ruleKind, RuleId = ruleId, Route = route, OccurredUtc = now, Version = env.Version,
            RequestId = context.TraceIdentifier,
            Sample = new IncidentSampleData(now, context.TraceIdentifier, userId, env.Version, route, RawMessage: Cut(e.Message, 4000),
                RawStack: Cut(e.ToString(), 4000)),
        });
    }

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];

    private async ValueTask WriteAsync(HttpContext context, int status, string? messageKey)
    {
        var details = new ProblemDetails { Status = status };
        if (messageKey is not null)
            details.Extensions["message"] = RequestMessages.Text(context, messageKey);
        // Accept 不含 JSON 時預設 writer 會不寫（WriteAsync 反而丟例外），改 TryWriteAsync 並自己補 JSON body
        if (await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = details })) return;
        details.Extensions["traceId"] = context.TraceIdentifier;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsJsonAsync(details, options: null, contentType: "application/problem+json");
    }
}
