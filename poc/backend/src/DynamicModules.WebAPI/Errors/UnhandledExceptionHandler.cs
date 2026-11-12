using System.Security.Claims;
using DynamicModules.Application.Common;
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
public sealed class UnhandledExceptionHandler(ILogger<UnhandledExceptionHandler> logger, IProblemDetailsService problems) : IExceptionHandler
{
    public static readonly EventId UnhandledEvent = new(5000, "UnhandledException");
    // 規則歸屬標記的 e.Data 鍵（Task 5 收斂到 RuleFailureTag）
    internal const string RuleKindKey = "formwright.ruleKind", RuleIdKey = "formwright.ruleId";

    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        // 使用者中止的請求不是錯誤
        if (exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested) return true;

        if (exception is BadHttpRequestException bad)
        {
            logger.LogInformation("請求被框架拒絕 {StatusCode}：{Reason}", bad.StatusCode, bad.GetType().Name);
            context.Response.StatusCode = bad.StatusCode;
            await WriteAsync(context, bad.StatusCode, null);
            return true;
        }

        var ops = context.Features.Get<RequestOpsFeature>();
        // ExceptionHandlerMiddleware 已清掉 endpoint，路由樣板讀請求開始時存下的 feature
        var route = ops?.Route ?? (context.Features.Get<IExceptionHandlerFeature>()?.Endpoint as RouteEndpoint)?.RoutePattern.RawText;
        var ruleKind = exception.Data[RuleKindKey] as string;
        var ruleId = exception.Data[RuleIdKey] as long?;
        logger.Log(LogLevel.Error, UnhandledEvent, exception,
            "未處理的例外 {ExceptionType} 指紋 {Fingerprint} 版本 {FingerprintVersion} 路由 {Route} 使用者 {UserId} 模組 {ModuleKey} 規則 {RuleKind}/{RuleId}",
            exception.GetType().FullName, ServerErrorFingerprint.Of(exception, route), ErrorFingerprint.Version,
            route, context.User.FindFirstValue(ClaimTypes.NameIdentifier), ops?.ModuleKey, ruleKind, ruleId);
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await WriteAsync(context, StatusCodes.Status500InternalServerError, MessageText.ServerError);
        return true;
    }

    private async ValueTask WriteAsync(HttpContext context, int status, string? messageKey)
    {
        var details = new ProblemDetails { Status = status };
        if (messageKey is not null)
            details.Extensions["message"] = MessageText.Get(context.RequestServices.GetService<ICurrentLocale>()?.Locale, messageKey);
        await problems.WriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = details });
    }
}
