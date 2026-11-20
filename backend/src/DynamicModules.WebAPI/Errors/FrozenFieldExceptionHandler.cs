using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using Microsoft.AspNetCore.Diagnostics;

namespace DynamicModules.WebAPI.Errors;

/// <summary>寫到凍結的貢獻欄位（貢獻者應用程式已關閉，共用主檔設計 §7）：整個請求 409，交易已由例外回滾。</summary>
public class FrozenFieldExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        if (exception is not FrozenFieldException frozen) return false;
        context.Response.StatusCode = StatusCodes.Status409Conflict;
        var lc = context.RequestServices.GetService<RequestLocale>()?.Current;
        var message = frozen.Text.Resolve(lc?.Locale, fallbacks: lc?.Fallbacks);
        if (!frozen.Visible) await context.Response.WriteAsJsonAsync(new { traceId = context.TraceIdentifier, message }, ct);
        else
            await context.Response.WriteAsJsonAsync(
                new { traceId = context.TraceIdentifier, message, fieldId = frozen.FieldId, lockedBy = frozen.AppKey }, ct);
        return true;
    }
}
