using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;
using Microsoft.Extensions.Logging;

namespace DynamicModules.Application.Dynamic.AccountService.Commands.ResetPassword;

public class ResetPasswordHandler(
    ICurrentUser current, IAccountRepository accounts, IPasswordService passwords, IUnitOfWork uow,
    ILogger<ResetPasswordHandler> logger) : IRequestHandler<ResetPasswordRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(ResetPasswordRequest request, CancellationToken ct)
    {
        var user = await accounts.GetTrackedAsync(request.UserId, ct);
        if (user is null) return ServiceResult.NotFound("使用者不存在");

        user.PasswordHash = passwords.Hash(request.Password);
        user.MustChangePassword = true;
        user.FailedLoginCount = 0;
        user.LockedUntil = null;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        await uow.CommitAsync(ct);
        logger.LogInformation("管理員 {Admin} 重設了帳號 {UserId} 的密碼", current.UserId, user.UserId);
        return ServiceResult.Success();
    }
}
