using DynamicModules.Application.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.WebAPI.Errors;

/// <summary>EF 的 concurrency token 擋下後寫入者時（SQL Server 讀已提交下會發生）回 409，而不是 500。</summary>
public class ConcurrencyExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        if (exception is not DbUpdateConcurrencyException) return false;
        context.Response.StatusCode = StatusCodes.Status409Conflict;
        await context.Response.WriteAsJsonAsync(
            new { traceId = context.TraceIdentifier, message = MessageText.Get(context.RequestServices.GetService<ICurrentLocale>()?.Locale, MessageText.Stale), code = "stale" }, ct);
        return true;
    }
}
