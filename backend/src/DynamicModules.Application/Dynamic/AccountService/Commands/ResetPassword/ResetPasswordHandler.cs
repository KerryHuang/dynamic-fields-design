using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.McpService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;
using Microsoft.Extensions.Logging;

namespace DynamicModules.Application.Dynamic.AccountService.Commands.ResetPassword;

public class ResetPasswordHandler(
    ICurrentUser current, IAccountRepository accounts, IPasswordService passwords, McpTokenRevoker mcpTokens, IUnitOfWork uow,
    AuditContext audit, ILogger<ResetPasswordHandler> logger) : IRequestHandler<ResetPasswordRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(ResetPasswordRequest request, CancellationToken ct)
    {
        var user = await accounts.GetTrackedAsync(request.UserId, ct);
        if (user is null) return ServiceResult.NotFound(MessageRef.Of(MessageText.UserNotFound));

        user.PasswordHash = passwords.Hash(request.Password);
        user.MustChangePassword = true;
        user.FailedLoginCount = 0;
        user.LockedUntil = null;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        // 同一交易撤銷全部 AI 連線權杖（設計 §13.2）；本人自己改密碼不撤銷
        var revoked = await mcpTokens.RevokeAllAsync(user.UserId, McpRevokeReason.PasswordReset, current.UserId, ct);
        await uow.CommitAsync(ct);
        audit.Detail("revokedMcpTokens", revoked);
        logger.LogInformation("管理員 {Admin} 重設了帳號 {UserId} 的密碼", current.UserId, user.UserId);
        return ServiceResult.Success();
    }
}
