using DynamicModules.Application.Common;
using DynamicModules.Persistence;
using Microsoft.AspNetCore.Diagnostics;

namespace DynamicModules.WebAPI.Errors;

/// <summary>
/// 資料庫寫入鎖等不到（排隊逾時，或別的行程占著）回 503 加 Retry-After：這是可重試的暫時狀況，不是伺服器錯誤。
/// 判斷涵蓋任何層的 SQLITE_BUSY／LOCKED，包含 EF 之外直接下 SQL 的路徑。
/// </summary>
public class DatabaseBusyExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        if (!DatabaseBusy.Is(exception)) return false;
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.RetryAfter = "2";
        await context.Response.WriteAsJsonAsync(new { traceId = context.TraceIdentifier, message = MessageText.Get(context.RequestServices.GetService<ICurrentLocale>()?.Locale, MessageText.DatabaseBusy) }, ct);
        return true;
    }
}
