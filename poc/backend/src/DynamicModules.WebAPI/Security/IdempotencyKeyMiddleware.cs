using System.Text.RegularExpressions;
using DynamicModules.Application.Common;

namespace DynamicModules.WebAPI.Security;

/// <summary>
/// 讀取 Idempotency-Key 標頭放進該請求的 <see cref="IOperationContext"/>（設計 §3.1）；格式不對直接 400，不讓請求往下走。
/// 回放先前成功的結果時加回應標頭 Idempotent-Replay: true。沒帶標頭的請求不受影響。
/// </summary>
public class IdempotencyKeyMiddleware(RequestDelegate next)
{
    public const string Header = "Idempotency-Key";
    public const string ReplayHeader = "Idempotent-Replay";
    private static readonly Regex Pattern = new(OperationContext.KeyPattern, RegexOptions.Compiled);

    public async Task InvokeAsync(HttpContext http)
    {
        if (!http.Request.Headers.TryGetValue(Header, out var values))
        {
            await next(http);
            return;
        }
        var key = values.ToString();
        if (!Pattern.IsMatch(key))
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsJsonAsync(new { message = "Idempotency-Key 必須是 8 到 64 個英數字、底線或連字號" });
            return;
        }

        var operation = http.RequestServices.GetRequiredService<IOperationContext>();
        operation.ClientKey = key;
        http.Response.OnStarting(() =>
        {
            if (operation.Replayed) http.Response.Headers[ReplayHeader] = "true";
            return Task.CompletedTask;
        });
        await next(http);
    }
}
