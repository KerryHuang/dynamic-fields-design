using System.Text.Json;
using DynamicModules.Application.Common;
using Microsoft.AspNetCore.Mvc;

namespace DynamicModules.WebAPI.Controllers;

public static class ServiceResultExtensions
{
    public static IActionResult ToActionResult(
        this ServiceResult r, ControllerBase c, string? createdAtAction = null, object? routeValues = null)
    {
        if (r.IsForbidden) return c.StatusCode(StatusCodes.Status403Forbidden, new { message = r.Message });
        if (r.IsNotFound) return c.NotFound(new { message = r.Message });
        if (r.IsConflict) return c.Conflict(r.Data is null ? new { message = r.Message } : ConflictBody(r));
        if (!r.IsSuccess)
            return c.BadRequest(new { traceId = c.HttpContext.TraceIdentifier, errors = r.Errors });
        if (r.IsCreated)
            return createdAtAction is null
                ? c.StatusCode(StatusCodes.Status201Created, r.Data)
                : c.CreatedAtAction(createdAtAction, routeValues, r.Data);
        return r.Data is null ? c.Ok() : c.Ok(r.Data);
    }

    /// <summary>{ message, ...data }：data 的公開屬性以 camelCase 鍵展開，值交給 MVC 的 JSON 設定序列化。</summary>
    private static Dictionary<string, object?> ConflictBody(ServiceResult r)
    {
        var body = new Dictionary<string, object?> { ["message"] = r.Message };
        foreach (var p in r.Data!.GetType().GetProperties())
            body[JsonNamingPolicy.CamelCase.ConvertName(p.Name)] = p.GetValue(r.Data);
        return body;
    }
}
