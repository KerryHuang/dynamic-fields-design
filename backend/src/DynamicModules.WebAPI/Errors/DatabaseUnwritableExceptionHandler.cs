using DynamicModules.Application.Common;
using Microsoft.AspNetCore.Diagnostics;

namespace DynamicModules.WebAPI.Errors;

/// <summary>
/// 資料庫唯讀、空間滿、連不上（維運設計 §17.2.9、O2-U8）：回 503 加 <c>Retry-After: 60</c>，代碼 <c>databaseUnwritable</c>／<c>databaseUnavailable</c>。
/// 記 Warning、不算 500、不開產品缺陷事件（同一個原因已由 <c>database.writable</c> 開了一件）。
/// 排在 <see cref="DatabaseBusyExceptionHandler"/> 之後、<see cref="UnhandledExceptionHandler"/> 之前；SessionMiddleware 讀帳號時丟的同類例外也走這裡。
/// </summary>
public sealed class DatabaseUnwritableExceptionHandler(IDatabaseErrors errors, ILogger<DatabaseUnwritableExceptionHandler> logger) : IExceptionHandler
{
    public static readonly EventId UnwritableEvent = new(5003, "DatabaseUnwritable");
    public const string UnwritableCode = "databaseUnwritable", UnavailableCode = "databaseUnavailable";

    /// <summary>這類狀況通常要人處理，一分鐘後再試（技術限制，§17.9）。</summary>
    public const int RetryAfterSeconds = 60;

    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        string code, messageKey;
        if (errors.IsReadOnly(exception) || errors.IsStorageFull(exception)) (code, messageKey) = (UnwritableCode, MessageText.DatabaseUnwritable);
        else if (errors.IsUnavailable(exception)) (code, messageKey) = (UnavailableCode, MessageText.DatabaseUnavailable);
        else return false;
        logger.LogWarning(UnwritableEvent, exception, "資料庫無法使用（{Code}），請求回 503", code);
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.RetryAfter = RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await context.Response.WriteAsJsonAsync(new
        {
            traceId = context.TraceIdentifier, code,
            message = RequestMessages.Text(context, messageKey),
        }, ct);
        return true;
    }
}
