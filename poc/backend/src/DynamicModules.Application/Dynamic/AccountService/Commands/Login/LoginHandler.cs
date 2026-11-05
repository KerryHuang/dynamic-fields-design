using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.AccountService.Data;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;
using Microsoft.Extensions.Logging;

namespace DynamicModules.Application.Dynamic.AccountService.Commands.Login;

public class LoginHandler(
    IAccountRepository accounts, IPasswordService passwords, IUnitOfWork uow, TimeProvider time, ILogger<LoginHandler> logger)
    : IRequestHandler<LoginRequest, ServiceResult>
{
    public const string FailureMessage = "帳號或密碼錯誤，或帳號暫時無法登入";
    private const int MaxFailures = 5;
    private static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(15);

    public async Task<ServiceResult> Handle(LoginRequest request, CancellationToken ct)
    {
        var userId = (request.UserId ?? "").Trim().ToLowerInvariant();
        var user = await accounts.GetTrackedAsync(userId, ct);
        if (user is null) return Fail(userId, "帳號不存在");
        if (!user.IsActive) return Fail(userId, "帳號已停用");

        var now = time.GetUtcNow();
        if (user.LockedUntil is { } until && until > now) return Fail(userId, "帳號鎖定中");
        if (user.PasswordHash is null) return Fail(userId, "帳號尚未設定密碼");

        if (!passwords.Verify(user.PasswordHash, request.Password ?? ""))
        {
            user.FailedLoginCount++;
            if (user.FailedLoginCount >= MaxFailures)
            {
                user.LockedUntil = now + LockDuration;
                logger.LogWarning("帳號 {UserId} 連續輸錯 {Count} 次，鎖定到 {Until}", userId, user.FailedLoginCount, user.LockedUntil);
            }
            await uow.CommitAsync(ct);
            return Fail(userId, "密碼錯誤");
        }

        if (user.FailedLoginCount != 0 || user.LockedUntil is not null)
        {
            user.FailedLoginCount = 0;
            user.LockedUntil = null;
            await uow.CommitAsync(ct);
        }
        logger.LogInformation("帳號 {UserId} 登入成功", userId);
        return ServiceResult.Success(new AuthUserResponse(user.UserId, user.DisplayName, user.MustChangePassword, user.SecurityStamp));
    }

    private ServiceResult Fail(string userId, string reason)
    {
        logger.LogWarning("帳號 {UserId} 登入失敗：{Reason}", userId, reason);
        return ServiceResult.Unauthorized(FailureMessage);
    }
}
