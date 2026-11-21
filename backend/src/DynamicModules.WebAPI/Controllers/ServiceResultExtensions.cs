using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.WebAPI.Controllers;

public static class ServiceResultExtensions
{
    public static IActionResult ToActionResult(
        this ServiceResult r, ControllerBase c, string? createdAtAction = null, object? routeValues = null)
    {
        // 訊息在這裡才依請求語系（X-Locale）沿退回鏈經 catalog 解析（語系包 i4c 計畫判斷 5）；
        // 單元測試的 HttpContext 沒有 RequestServices 時走只認內建四語的舊路徑（繁中，或 RequestLocale 有、catalog 沒有時的那個語系）
        var services = c.HttpContext.RequestServices;
        var lc = services?.GetService<RequestLocale>()?.Current;
        var messages = services?.GetService<IMessageCatalog>();
        string? Message() => lc is not null && messages is not null ? r.MessageIn(messages, lc) : r.MessageIn(lc?.Locale, lc?.Fallbacks);
        IReadOnlyDictionary<string, string[]> Errors() => lc is not null && messages is not null ? r.ErrorsIn(messages, lc) : r.ErrorsIn(lc?.Locale, lc?.Fallbacks);
        if (r.IsUnauthorized) return c.Unauthorized(Body(r, Message()));
        if (r.IsForbidden) return c.StatusCode(StatusCodes.Status403Forbidden, Body(r, Message()));
        if (r.IsTooManyRequests) return c.StatusCode(StatusCodes.Status429TooManyRequests, Body(r, Message()));
        if (r.IsUnprocessable) return c.StatusCode(StatusCodes.Status422UnprocessableEntity, Body(r, Message()));
        if (r.IsNotFound) return c.NotFound(Body(r, Message()));
        if (r.IsConflict) return c.Conflict(Body(r, Message()));
        if (!r.IsSuccess)
            return c.BadRequest(r.Code is null
                ? new { traceId = c.HttpContext.TraceIdentifier, errors = Errors() }
                : (object)new { traceId = c.HttpContext.TraceIdentifier, code = r.Code, errors = Errors() });
        if (r.IsCreated)
            return createdAtAction is null
                ? c.StatusCode(StatusCodes.Status201Created, r.Data)
                : c.CreatedAtAction(createdAtAction, routeValues, r.Data);
        return r.Data is null ? c.Ok() : c.Ok(r.Data);
    }

    /// <summary>{ message, code?, params?, ...data }：409 的 data 公開屬性以 camelCase 鍵展開，值交給 MVC 的 JSON 設定序列化。</summary>
    private static Dictionary<string, object?> Body(ServiceResult r, string? message)
    {
        var body = new Dictionary<string, object?>();
        if (message is not null) body["message"] = message;   // 訊息為 null 時不輸出 message 鍵（與改成字典之前的回應形狀相同）
        if (r.Code is not null) body["code"] = r.Code;
        if (r.Params is not null) body["params"] = r.Params;
        if (r.IsConflict && r.Data is { } data)
            foreach (var p in data.GetType().GetProperties())
                body[JsonNamingPolicy.CamelCase.ConvertName(p.Name)] = p.GetValue(data);
        return body;
    }
}
