using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Observability;
using DynamicModules.Application.Common.Security;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Application.Dynamic.AccountService.Data;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;
using Microsoft.Extensions.Logging;

namespace DynamicModules.Application.Dynamic.AccountService.Commands.DemoLogin;

/// <summary>
/// Demo 站一鍵登入：不驗密碼、不看鎖定。只收啟用中的示範帳號與 admin。
/// 平常不寫資料庫（Demo 產生器的寫入交易會鎖表，第一位訪客喚醒站台時不能被擋住）。
/// 失敗與密碼登入共用 <see cref="LoginThrottle"/>：公開站台的匿名者不能靠不存在的帳號無限新增稽核列；
/// 另在查資料庫前限制每來源總嘗試量（成功也算，<see cref="LoginThrottle.TryDemoAttempt"/>），逐次登入公開帳號也灌不了。被擋下的只加計數器、不寫稽核。
/// </summary>
public class DemoLoginHandler(
    AuthSettings settings, IAccountRepository accounts, IUnitOfWork uow, LoginThrottle throttle, IAuditLog audit, ILogger<DemoLoginHandler> logger)
    : IRequestHandler<DemoLoginRequest, ServiceResult>
{
    private static readonly string? Method = new AuditSummary().Add("method", "demo").ToJson();

    public async Task<ServiceResult> Handle(DemoLoginRequest request, CancellationToken ct)
    {
        if (!settings.DemoLoginEnabled) return ServiceResult.NotFound(MessageRef.Of(MessageText.ResourceNotFound));

        // 登入的稽核身分情境（維運設計 §17.2.1）；寫不進稽核不擋登入
        using var signIn = AuditOrigin.BeginSignIn();
        var userId = (request.UserId ?? "").Trim().ToLowerInvariant();
        var ip = string.IsNullOrEmpty(request.ClientIp) ? "unknown" : request.ClientIp;
        var key = userId.Length > 128 ? userId[..128] : userId;
        if (!throttle.TryDemoAttempt(ip) || !throttle.TryBegin(key, ip))
        {
            OpsMetrics.LoginThrottled.Add(1);
            logger.LogWarning("一鍵登入失敗：來源或帳號嘗試過於頻繁");
            return ServiceResult.Unauthorized(MessageRef.Of(MessageText.LoginFailed));
        }
        ServiceResult result;
        try
        {
            result = await AttemptAsync(userId, ct);
        }
        catch
        {
            throttle.Release(key, ip);   // 例外不算失敗，歸還名額
            throw;
        }
        throttle.Complete(key, ip, result.IsSuccess);
        return result;
    }

    private async Task<ServiceResult> AttemptAsync(string userId, CancellationToken ct)
    {
        var user = await accounts.GetTrackedAsync(userId, ct);
        if (user is null || !user.IsActive || !(user.IsDemoAccount || user.UserId == "admin"))
        {
            // 帳號不存在時不記錄匿名輸入
            if (user is null) logger.LogWarning("一鍵登入失敗：帳號不存在（不記錄輸入）");
            else logger.LogWarning("帳號 {UserId} 一鍵登入失敗", user.UserId);
            var reason = user is null ? "unknownAccount" : !user.IsActive ? "inactive" : "notDemoAccount";
            await audit.TryWriteNowAsync(new AuditEntry("login.failed", AuditResult.Denied, user is null ? null : AuditTargets.User,
                user?.UserId, ReasonCode: reason, Summary: Method), ct);
            return ServiceResult.Unauthorized(MessageRef.Of(MessageText.LoginFailed));
        }

        if (user.MustChangePassword)
        {
            user.MustChangePassword = false;
            await uow.CommitAsync(ct);
        }

        logger.LogInformation("帳號 {UserId} 一鍵登入成功", user.UserId);
        await audit.TryWriteNowAsync(new AuditEntry("login.succeeded", AuditResult.Success, AuditTargets.User, user.UserId, user.DisplayName,
            Summary: Method, ActorOverride: new ActorOverride(user.UserId)), ct);
        return ServiceResult.Success(new AuthUserResponse(user.UserId, user.DisplayName, false, user.SecurityStamp));
    }
}
