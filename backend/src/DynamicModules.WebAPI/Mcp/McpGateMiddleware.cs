using System.Globalization;
using System.Net;
using System.Security.Claims;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Dynamic.McpService;
using DynamicModules.Application.Dynamic.McpService.Auth;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Mcp;
using DynamicModules.WebAPI.Errors;
using DynamicModules.WebAPI.Security;
using MediatR;
using Microsoft.Net.Http.Headers;

namespace DynamicModules.WebAPI.Mcp;

/// <summary>
/// <c>/mcp</c> 的閘門（mcp1a 計畫判斷 11；寫死清單：安全），只掛在 <c>/mcp</c>。依序：
/// query string 不准帶權杖 → Origin 只認 <c>Ops:PublicUrl</c> 與可信 loopback 的固定 origin → HTTPS → IP 降速 → Bearer 權杖認證 →
/// 換成權杖身分（cookie 與 <c>X-User</c> 的身分丟掉）→ 開 <see cref="AuditOrigin"/>(Mcp) → 通過交給 SDK，政策擋下寫 <c>mcp.access</c> 後回 403。
/// 不讀 DbContext：全部經 MediatR 與 <see cref="IAuditLog"/>。這裡是唯一發 <see cref="McpClaims"/> 的地方（架構測試鎖住）。
/// </summary>
public sealed class McpGateMiddleware(RequestDelegate next, McpAuthThrottle throttle, IConfiguration config, ILogger<McpGateMiddleware> logger)
{
    private static readonly string[] QueryTokenKeys = ["access_token", "token", "api_key"];

    public async Task InvokeAsync(HttpContext ctx, ISender sender, IAuditLog audit, ClientIpResolver ips)
    {
        if (TokenInQuery(ctx.Request.Query))
        {
            await Reject(ctx, StatusCodes.Status401Unauthorized, McpMessages.CodeTokenInQuery, McpMessages.TokenInQuery);
            return;
        }

        var trustForwarded = config.GetValue<bool>("Auth:TrustForwardedHeaders");
        var loopback = IsTrustedLoopback(ctx.Connection.RemoteIpAddress, trustForwarded);
        if (ctx.Request.Headers.TryGetValue(HeaderNames.Origin, out var origin) && !AllowedOrigins(ctx, loopback).Contains(origin.ToString()))
        {
            await Reject(ctx, StatusCodes.Status403Forbidden, McpMessages.CodeOriginDenied, McpMessages.OriginDenied);
            return;
        }
        if (!loopback && !ctx.Request.IsHttps && !config.GetValue<bool>("Mcp:AllowHttp"))
        {
            await Reject(ctx, StatusCodes.Status403Forbidden, McpMessages.CodeHttpsRequired, McpMessages.HttpsRequired);
            return;
        }

        var ip = ips.Resolve(ctx) ?? "unknown";
        if (!throttle.TryBegin(ip, out var retryAfter))
        {
            ctx.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
            await Reject(ctx, StatusCodes.Status429TooManyRequests, McpMessages.CodeAuthThrottled, McpMessages.Unauthorized);
            return;
        }

        McpAuthResult result;
        try
        {
            // 沒帶或格式不符也算一次失敗（同壞權杖，一律 401、不透露原因）
            result = BearerOf(ctx.Request) is { } raw
                ? await sender.Send(new AuthenticateMcpTokenRequest(raw, ip), ctx.RequestAborted)
                : McpAuthResult.Unauthorized;
        }
        catch
        {
            throttle.Release(ip);
            throw;
        }
        throttle.Complete(ip, result.Status != McpAuthStatus.Unauthorized);
        if (result.Status == McpAuthStatus.Unauthorized)
        {
            logger.LogInformation("MCP 權杖認證失敗，來源 {ClientIp}", ip);
            await Reject(ctx, StatusCodes.Status401Unauthorized, McpMessages.CodeUnauthorized, McpMessages.Unauthorized);
            return;
        }

        // 權杖與帳號已驗過（Ok 或帶可信身分的 Forbidden）：換身分，之後的 ICurrentUser、IRequestChannel、稽核都是權杖擁有者
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, result.UserId!),
            new Claim(McpClaims.Channel, McpClaims.McpValue),
            new Claim(McpClaims.Token, result.TokenId!.Value.ToString(CultureInfo.InvariantCulture)),
            .. (result.Scopes ?? []).Select(s => new Claim(McpClaims.Scope, s)),
        ], "McpToken"));

        using (AuditOrigin.Begin(AuditChannel.Mcp, result.TokenId, AuditActorKind.User))
        {
            if (result.Status == McpAuthStatus.Ok)
            {
                await next(ctx);
                return;
            }

            var code = result.Code!;
            try
            {
                await audit.WriteNowAsync(new AuditEntry("mcp.access", AuditResult.Denied, AuditTargets.User, result.UserId, ReasonCode: code),
                    CancellationToken.None);
            }
            catch (AuditWriteException e)
            {
                // 稽核寫不進去照樣拒絕
                logger.LogError(e.InnerException, "MCP 連線被政策擋下（{Code}），稽核寫不進去", code);
            }
            await Reject(ctx, StatusCodes.Status403Forbidden, code, McpMessages.Denial(code));
        }
    }

    private static bool TokenInQuery(IQueryCollection query) =>
        query.Any(q => QueryTokenKeys.Contains(q.Key, StringComparer.OrdinalIgnoreCase)
                       || q.Value.Any(v => v?.StartsWith(McpSecrets.TokenPrefix, StringComparison.Ordinal) == true));

    /// <summary>
    /// 可信 loopback：連線來源是 loopback，而且沒開轉送標頭（開著代表前面有代理，代理同機時所有請求都會是 loopback）。
    /// 看連線本身，不看 URL、Host 或 X-Forwarded-For。
    /// </summary>
    private static bool IsTrustedLoopback(IPAddress? remote, bool trustForwarded)
    {
        if (trustForwarded || remote is null) return false;
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        return IPAddress.IsLoopback(remote);
    }

    /// <summary>允許的 Origin：<c>Ops:PublicUrl</c> 的 origin（有設才有）；可信 loopback 連線再加三個固定的 loopback origin。不含請求的 Host。</summary>
    private HashSet<string> AllowedOrigins(HttpContext ctx, bool loopback)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (Uri.TryCreate(config["Ops:PublicUrl"], UriKind.Absolute, out var publicUrl)) set.Add(publicUrl.GetLeftPart(UriPartial.Authority));
        if (loopback)
        {
            var port = ctx.Connection.LocalPort.ToString(CultureInfo.InvariantCulture);
            set.Add($"http://127.0.0.1:{port}");
            set.Add($"http://localhost:{port}");
            set.Add($"http://[::1]:{port}");
        }
        return set;
    }

    private static string? BearerOf(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && header.Length > prefix.Length ? header[prefix.Length..].Trim() : null;
    }

    /// <summary>所有拒絕回應的唯一出口；401 一律帶 <c>WWW-Authenticate: Bearer</c>（設計 §5.3）。</summary>
    private static Task Reject(HttpContext ctx, int status, string code, MessageRef message)
    {
        ctx.Response.StatusCode = status;
        if (status == StatusCodes.Status401Unauthorized) ctx.Response.Headers.WWWAuthenticate = "Bearer";
        return ctx.Response.WriteAsJsonAsync(new { traceId = ctx.TraceIdentifier, message = RequestMessages.Text(ctx, message), code });
    }
}
