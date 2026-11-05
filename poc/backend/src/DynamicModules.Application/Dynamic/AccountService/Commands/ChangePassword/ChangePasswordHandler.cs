using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.AccountService.Data;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;
using Microsoft.Extensions.Logging;

namespace DynamicModules.Application.Dynamic.AccountService.Commands.ChangePassword;

public class ChangePasswordHandler(
    ICurrentUser current, IAccountRepository accounts, IPasswordService passwords, IUnitOfWork uow,
    ILogger<ChangePasswordHandler> logger) : IRequestHandler<ChangePasswordRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(ChangePasswordRequest request, CancellationToken ct)
    {
        var user = await accounts.GetTrackedAsync(current.UserId, ct);
        if (user is null) return ServiceResult.NotFound("使用者不存在");

        if (user.PasswordHash is null || !passwords.Verify(user.PasswordHash, request.OldPassword))
            return Invalid("oldPassword", "舊密碼錯誤");
        if (request.NewPassword == request.OldPassword)
            return Invalid("newPassword", "新密碼不可與舊密碼相同");

        user.PasswordHash = passwords.Hash(request.NewPassword);
        user.MustChangePassword = false;
        user.FailedLoginCount = 0;
        user.LockedUntil = null;
        // 換 stamp 讓其他 session 在下一個請求失效；呼叫端用新 stamp 重發當前 session 的 cookie
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        await uow.CommitAsync(ct);
        logger.LogInformation("帳號 {UserId} 已變更密碼", user.UserId);
        return ServiceResult.Success(new AuthUserResponse(user.UserId, user.DisplayName, user.MustChangePassword, user.SecurityStamp));
    }

    private static ServiceResult Invalid(string key, string message) =>
        ServiceResult.Invalid(new Dictionary<string, string[]> { [key] = [message] });
}
