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
        if (r.IsUnauthorized) return c.Unauthorized(Body(r, locale));
        if (r.IsForbidden) return c.StatusCode(StatusCodes.Status403Forbidden, Body(r, locale));
        if (r.IsUnprocessable) return c.StatusCode(StatusCodes.Status422UnprocessableEntity, Body(r, locale));
        if (r.IsNotFound) return c.NotFound(Body(r, locale));
        if (r.IsConflict) return c.Conflict(Body(r, locale));
        if (!r.IsSuccess)
            return c.BadRequest(new { traceId = c.HttpContext.TraceIdentifier, errors = r.ErrorsIn(locale) });
        if (r.IsCreated)
            return createdAtAction is null
                ? c.StatusCode(StatusCodes.Status201Created, r.Data)
                : c.CreatedAtAction(createdAtAction, routeValues, r.Data);
        return r.Data is null ? c.Ok() : c.Ok(r.Data);
    }

    /// <summary>{ message, code?, params?, ...data }：409 的 data 公開屬性以 camelCase 鍵展開，值交給 MVC 的 JSON 設定序列化。</summary>
    private static Dictionary<string, object?> Body(ServiceResult r, string? locale)
    {
        var body = new Dictionary<string, object?> { ["message"] = r.MessageIn(locale) };
        if (r.Code is not null) body["code"] = r.Code;
        if (r.Params is not null) body["params"] = r.Params;
        if (r.IsConflict && r.Data is { } data)
            foreach (var p in data.GetType().GetProperties())
                body[JsonNamingPolicy.CamelCase.ConvertName(p.Name)] = p.GetValue(data);
        return body;
    }
}
