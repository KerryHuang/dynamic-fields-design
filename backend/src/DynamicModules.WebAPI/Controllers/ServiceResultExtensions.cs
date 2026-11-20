using System.Text.Json;
using DynamicModules.Application.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.WebAPI.Controllers;

public static class ServiceResultExtensions
{
    public static IActionResult ToActionResult(
        this ServiceResult r, ControllerBase c, string? createdAtAction = null, object? routeValues = null)
    {
        // 訊息在這裡才依請求語系（X-Locale）解析；單元測試的 HttpContext 沒有 RequestServices 時是繁中
        var locale = c.HttpContext.RequestServices?.GetService<ICurrentLocale>()?.Locale;
        var fallbacks = c.HttpContext.RequestServices?.GetService<Application.Dynamic.PolicyService.CachedSystemPolicy>()?.LocaleFallbacks;
        if (r.IsUnauthorized) return c.Unauthorized(Body(r, locale, fallbacks));
        if (r.IsForbidden) return c.StatusCode(StatusCodes.Status403Forbidden, Body(r, locale, fallbacks));
        if (r.IsUnprocessable) return c.StatusCode(StatusCodes.Status422UnprocessableEntity, Body(r, locale, fallbacks));
        if (r.IsNotFound) return c.NotFound(Body(r, locale, fallbacks));
        if (r.IsConflict) return c.Conflict(Body(r, locale, fallbacks));
        if (!r.IsSuccess)
            return c.BadRequest(r.Code is null
                ? new { traceId = c.HttpContext.TraceIdentifier, errors = r.ErrorsIn(locale, fallbacks) }
                : (object)new { traceId = c.HttpContext.TraceIdentifier, code = r.Code, errors = r.ErrorsIn(locale, fallbacks) });
        if (r.IsCreated)
            return createdAtAction is null
                ? c.StatusCode(StatusCodes.Status201Created, r.Data)
                : c.CreatedAtAction(createdAtAction, routeValues, r.Data);
        return r.Data is null ? c.Ok() : c.Ok(r.Data);
    }

    /// <summary>{ message, code?, params?, ...data }：409 的 data 公開屬性以 camelCase 鍵展開，值交給 MVC 的 JSON 設定序列化。</summary>
    private static Dictionary<string, object?> Body(ServiceResult r, string? locale, IReadOnlyList<string>? fallbacks)
    {
        var body = new Dictionary<string, object?>();
        if (r.MessageIn(locale, fallbacks) is { } message) body["message"] = message;   // 訊息為 null 時不輸出 message 鍵（與改成字典之前的回應形狀相同）
        if (r.Code is not null) body["code"] = r.Code;
        if (r.Params is not null) body["params"] = r.Params;
        if (r.IsConflict && r.Data is { } data)
            foreach (var p in data.GetType().GetProperties())
                body[JsonNamingPolicy.CamelCase.ConvertName(p.Name)] = p.GetValue(data);
        return body;
    }
}
