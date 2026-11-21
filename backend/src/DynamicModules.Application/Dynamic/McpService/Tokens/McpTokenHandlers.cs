using System.Globalization;
using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Mcp;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;
using Microsoft.Extensions.Logging;

namespace DynamicModules.Application.Dynamic.McpService.Tokens;

/// <summary>權杖 API 共用：狀態、清單列、營運時區日期換算、帳號資格（mcp1a 計畫判斷 7）。</summary>
public sealed class McpTokenViews(
    IMcpTokenRepository tokens, IAuditLogRepository audits, CachedSystemPolicy system, IBusinessTime business, TimeProvider time)
{
    /// <summary>計入每日次數與「今日呼叫」的動作：12 個工具加不存在的工具（判斷 13；被政策擋下的 <c>mcp.access</c> 不算）。</summary>
    public static readonly IReadOnlyList<string> CallActions =
        [.. McpToolCatalog.Production.Select(t => t.AuditCode), McpToolCatalog.UnknownToolAuditCode];

    public DateTime NowUtc => time.GetUtcNow().UtcDateTime;

    public DateOnly Today => business.Today;

    /// <summary>營運時區某天 00:00 換成 UTC。</summary>
    public DateTime StartOfBusinessDayUtc(DateOnly day) =>
        DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue) - system.BusinessOffset, DateTimeKind.Utc);

    /// <summary>到期日當天營運時區結束時失效＝次日 00:00（§5.1）。</summary>
    public DateTime ExpiresUtcOf(DateOnly expiresOn) => StartOfBusinessDayUtc(expiresOn.AddDays(1));

    public DateOnly ExpiresOnOf(DateTime expiresUtc) => DateOnly.FromDateTime(expiresUtc + system.BusinessOffset).AddDays(-1);

    public static string StatusOf(McpToken t, DateTime nowUtc) => t.RevokedUtc is not null ? "revoked" : t.ExpiresUtc <= nowUtc ? "expired" : "active";

    public static string Camel(string name) => JsonNamingPolicy.CamelCase.ConvertName(name);

    /// <summary>帳號能不能用 AI 連線：存在、啟用、不是示範帳號、不必改密碼。</summary>
    public static bool AccountUsable(AppUser? user) => user is { IsActive: true, IsDemoAccount: false, MustChangePassword: false };

    public async Task<McpTokenItem> ItemAsync(McpToken t, CancellationToken ct)
    {
        var now = NowUtc;
        var today = await audits.CountForTokenSinceAsync(t.Id, StartOfBusinessDayUtc(Today), CallActions, ct);
        McpScopes.TryParse(t.Scopes, out var scopes);
        return new McpTokenItem(t.Id, t.UserId, t.Name, t.Last4, scopes, t.CreatedUtc, t.ExpiresUtc, ExpiresOnOf(t.ExpiresUtc), t.LastUsedUtc,
            t.LastUsedIp, StatusOf(t, now), t.RevokedUtc, t.RevokedBy, t.RevokeReason is { } r ? Camel(r.ToString()) : null, today);
    }

    public async Task<List<McpTokenItem>> ItemsAsync(IEnumerable<McpToken> list, CancellationToken ct)
    {
        var items = new List<McpTokenItem>();
        foreach (var t in list) items.Add(await ItemAsync(t, ct));
        return items;
    }

    /// <summary>撤銷；已撤銷的不改原因與時間（冪等）。回傳這次有沒有改。</summary>
    public bool Revoke(McpToken t, string by, McpRevokeReason reason)
    {
        if (t.RevokedUtc is not null) return false;
        t.RevokedUtc = NowUtc;
        t.RevokedBy = by;
        t.RevokeReason = reason;
        return true;
    }

    public Task<McpToken?> GetTrackedAsync(long id, CancellationToken ct) => tokens.GetTrackedAsync(id, ct);
}

public sealed class GetMyMcpInfoHandler(
    ICurrentUser current, IAccountRepository accounts, AuthSettings auth, McpPolicies policies, McpTokenViews views)
    : IRequestHandler<GetMyMcpInfoRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetMyMcpInfoRequest request, CancellationToken ct)
    {
        var snap = await policies.ReadAsync(ct);
        var user = await accounts.GetTrackedAsync(current.UserId, ct);
        string? reason;
        if (!McpTokenViews.AccountUsable(user)) reason = McpMessages.CodeAccountBlocked;
        else if (auth.DemoLoginEnabled) reason = McpMessages.CodeDemoSite;
        else reason = await policies.AccessDenialAsync(snap, current.UserId, ct);

        var url = request.ServerBaseUrl.TrimEnd('/') + "/mcp";
        var examples = new McpClientExamples(
            $"claude mcp add --transport http formwright {url} --header \"Authorization: Bearer ${{FORMWRIGHT_TOKEN}}\"",
            $"[mcp_servers.formwright]\nurl = \"{url}\"\nbearer_token_env_var = \"FORMWRIGHT_TOKEN\"");
        return ServiceResult.Success(new McpInfoResponse(snap.Enabled && !snap.AccessInvalid, reason is null, ReasonOf(reason), McpScopes.Issuable,
            views.Today.AddDays(snap.TokenDefaultLifetimeDays), views.Today.AddDays(snap.TokenMaxLifetimeDays), url, examples));
    }

    /// <summary>機器碼去掉 <c>mcp</c> 前綴、首字小寫（<c>mcpNotAllowed</c> → <c>notAllowed</c>）。</summary>
    private static string? ReasonOf(string? code) => code is null ? null : char.ToLowerInvariant(code[3]) + code[4..];
}

public sealed class ListMyMcpTokensHandler(ICurrentUser current, IMcpTokenRepository tokens, McpTokenViews views)
    : IRequestHandler<ListMyMcpTokensRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(ListMyMcpTokensRequest request, CancellationToken ct) =>
        ServiceResult.Success(await views.ItemsAsync(await tokens.ListByUserAsync(current.UserId, ct), ct));
}

/// <summary>
/// 建立權杖（判斷 7 的檢查順序）：帳號 → 站台 → 政策 → 密碼（登入降速，判斷 16）→ 名稱 → 範圍 → 到期日 → 每人上限。
/// 明文只在回應出現一次；資料庫存 SHA-256 與末四碼；稽核摘要只記名稱、範圍、到期日與權杖 id。
/// </summary>
public sealed class CreateMcpTokenHandler(
    ICurrentUser current, IAccountRepository accounts, AuthSettings auth, McpPolicies policies, IPasswordService passwords, LoginThrottle throttle,
    IMcpTokenRepository tokens, IUnitOfWork uow, AuditContext audit, McpTokenViews views, ILogger<CreateMcpTokenHandler> logger)
    : IRequestHandler<CreateMcpTokenRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(CreateMcpTokenRequest request, CancellationToken ct)
    {
        var userId = current.UserId;
        var user = await accounts.GetTrackedAsync(userId, ct);
        if (!McpTokenViews.AccountUsable(user)) return Denied(McpMessages.CodeAccountBlocked);
        if (auth.DemoLoginEnabled) return Denied(McpMessages.CodeDemoSite);
        var snap = await policies.ReadAsync(ct);
        if (await policies.AccessDenialAsync(snap, userId, ct) is { } denial) return Denied(denial);

        if (VerifyPassword(user!, request.Password ?? "", request.ClientIp) is { } passwordError) return passwordError;

        var name = (request.Name ?? "").Trim();
        if (name.Length is 0 or > McpLimits.TokenNameMax) return ServiceResult.Invalid(McpMessages.NameField, McpMessages.TokenNameInvalid);

        if (request.Scopes is not { Count: > 0 } || request.Scopes.Any(s => s is null)
            || !McpScopes.TryParse(string.Join(',', request.Scopes), out var scopes) || scopes.Any(s => !McpScopes.Issuable.Contains(s)))
            return ServiceResult.Invalid(McpMessages.ScopesField, McpMessages.TokenScopeInvalid);

        var earliest = views.Today.AddDays(1);
        var latest = views.Today.AddDays(snap.TokenMaxLifetimeDays);
        if (request.ExpiresOn is not { } expiresOn || expiresOn < earliest || expiresOn > latest)
            return ServiceResult.Invalid(McpMessages.ExpiresOnField, McpMessages.TokenExpiryInvalid(earliest, latest));

        var now = views.NowUtc;
        if (await tokens.CountActiveAsync(userId, now, ct) >= snap.TokenMaxPerUser)
            return ServiceResult.Forbidden(McpMessages.TokenLimitReached(snap.TokenMaxPerUser), McpMessages.CodeTokenLimit);

        var secret = McpSecrets.NewToken();
        var token = new McpToken
        {
            UserId = userId, Name = name, TokenHash = McpSecrets.Hash(secret), Last4 = McpSecrets.Last4(secret), Scopes = McpScopes.Join(scopes),
            CreatedUtc = now, ExpiresUtc = views.ExpiresUtcOf(expiresOn),
        };
        tokens.Add(token);
        await uow.CommitAsync(ct);

        audit.Target(userId, user!.DisplayName);
        audit.Detail("tokenId", token.Id);
        audit.Detail("name", name);
        audit.Detail("scopes", token.Scopes);
        audit.Detail("expiresOn", expiresOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        logger.LogInformation("使用者 {UserId} 建立 MCP 權杖 {TokenId}（到期日 {ExpiresOn}）", userId, token.Id, expiresOn);
        return ServiceResult.Created(new McpTokenCreated(secret, await views.ItemAsync(token, ct)));
    }

    private static ServiceResult Denied(string code) => ServiceResult.Forbidden(McpMessages.Denial(code), code);

    /// <summary>與登入共用帳號＋IP、IP、帳號三層降速；例外不算失敗、歸還名額（照 LoginHandler）。</summary>
    private ServiceResult? VerifyPassword(AppUser user, string password, string? clientIp)
    {
        var key = user.UserId.ToLowerInvariant();
        var ip = string.IsNullOrEmpty(clientIp) ? "unknown" : clientIp;
        if (!throttle.TryBegin(key, ip))
            return ServiceResult.TooManyRequests(McpMessages.PasswordThrottled, McpMessages.CodePasswordThrottled);
        bool ok;
        try
        {
            ok = user.PasswordHash is { } hash && passwords.Verify(hash, password);
        }
        catch
        {
            throttle.Release(key, ip);
            throw;
        }
        throttle.Complete(key, ip, ok);
        if (ok) return null;
        logger.LogWarning("使用者 {UserId} 建立 MCP 權杖時密碼錯誤", user.UserId);
        return ServiceResult.Invalid(McpMessages.PasswordField, McpMessages.PasswordWrong);
    }
}

public sealed class RevokeMyMcpTokenHandler(ICurrentUser current, IAccountRepository accounts, IUnitOfWork uow, AuditContext audit, McpTokenViews views)
    : IRequestHandler<RevokeMyMcpTokenRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(RevokeMyMcpTokenRequest request, CancellationToken ct)
    {
        var token = await views.GetTrackedAsync(request.Id, ct);
        // 別人的權杖與不存在的同樣回 404，不透露存在
        if (token is null || token.UserId != current.UserId) return ServiceResult.NotFound(McpMessages.TokenNotFound);
        if (views.Revoke(token, current.UserId, McpRevokeReason.Owner)) await uow.CommitAsync(ct);
        await McpTokenAudit.DescribeAsync(audit, accounts, token, ct);
        return ServiceResult.Success();
    }
}

public sealed class GetMyMcpTokenCallsHandler(ICurrentUser current, IAuditLogRepository audits, McpTokenViews views)
    : IRequestHandler<GetMyMcpTokenCallsRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetMyMcpTokenCallsRequest request, CancellationToken ct)
    {
        var token = await views.GetTrackedAsync(request.Id, ct);
        if (token is null || token.UserId != current.UserId) return ServiceResult.NotFound(McpMessages.TokenNotFound);
        // 權杖建立前不會有它的呼叫；動作限 mcp 分組（建立、撤銷這類畫面操作沒有 TokenId，本來就不會出現）
        var rows = await audits.PageAsync(new AuditLogQuery(token.CreatedUtc, views.NowUtc, TokenId: token.Id,
            Actions: AuditActions.CodesOf(AuditActions.Mcp)), McpLimits.RecentCalls, ct);
        return ServiceResult.Success(rows.Select(r =>
            new McpTokenCall(r.Id, r.Action, r.TargetLabel, r.OccurredUtc, McpTokenViews.Camel(r.Result.ToString()), r.ReasonCode)).ToList());
    }
}

public sealed class AdminListMcpTokensHandler(IMcpTokenRepository tokens, McpTokenViews views)
    : IRequestHandler<AdminListMcpTokensRequest, ServiceResult>
{
    private static readonly string[] Statuses = ["active", "expired", "revoked"];

    public async Task<ServiceResult> Handle(AdminListMcpTokensRequest request, CancellationToken ct)
    {
        var status = string.IsNullOrWhiteSpace(request.Status) ? null : request.Status.Trim();
        if (status is not null && !Statuses.Contains(status, StringComparer.Ordinal))
            return ServiceResult.Invalid("status", McpMessages.InvalidArgument("status"));
        var userId = string.IsNullOrWhiteSpace(request.UserId) ? null : request.UserId.Trim();
        var now = views.NowUtc;
        var list = (await tokens.ListAsync(userId, ct)).Where(t => status is null || McpTokenViews.StatusOf(t, now) == status);
        return ServiceResult.Success(await views.ItemsAsync(list, ct));
    }
}

public sealed class AdminRevokeMcpTokenHandler(ICurrentUser current, IAccountRepository accounts, IUnitOfWork uow, AuditContext audit, McpTokenViews views)
    : IRequestHandler<AdminRevokeMcpTokenRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(AdminRevokeMcpTokenRequest request, CancellationToken ct)
    {
        var token = await views.GetTrackedAsync(request.Id, ct);
        if (token is null) return ServiceResult.NotFound(McpMessages.TokenNotFound);
        if (views.Revoke(token, current.UserId, McpRevokeReason.Admin)) await uow.CommitAsync(ct);
        await McpTokenAudit.DescribeAsync(audit, accounts, token, ct);
        return ServiceResult.Success();
    }
}

public sealed class AdminRevokeAllMcpTokensHandler(
    ICurrentUser current, IAccountRepository accounts, IMcpTokenRepository tokens, IUnitOfWork uow, AuditContext audit, McpTokenViews views)
    : IRequestHandler<AdminRevokeAllMcpTokensRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(AdminRevokeAllMcpTokensRequest request, CancellationToken ct)
    {
        var names = await accounts.GetDisplayNamesAsync([request.UserId], ct);
        if (!names.TryGetValue(request.UserId, out var displayName)) return ServiceResult.NotFound(MessageRef.Of(MessageText.UserNotFound));
        var active = await tokens.ListActiveTrackedAsync(request.UserId, views.NowUtc, ct);
        foreach (var t in active) views.Revoke(t, current.UserId, McpRevokeReason.Admin);
        if (active.Count > 0) await uow.CommitAsync(ct);
        audit.Target(request.UserId, displayName);
        audit.Detail("revoked", active.Count);
        return ServiceResult.Success(new { revoked = active.Count });
    }
}

/// <summary>撤銷的稽核目標是權杖擁有者（目標種類 user），摘要記權杖 id、名稱與撤銷原因。</summary>
internal static class McpTokenAudit
{
    public static async Task DescribeAsync(AuditContext audit, IAccountRepository accounts, McpToken token, CancellationToken ct)
    {
        var names = await accounts.GetDisplayNamesAsync([token.UserId], ct);
        audit.Target(token.UserId, names.GetValueOrDefault(token.UserId));
        audit.Detail("tokenId", token.Id);
        audit.Detail("name", token.Name);
        audit.Detail("reason", token.RevokeReason is { } r ? McpTokenViews.Camel(r.ToString()) : null);
    }
}
