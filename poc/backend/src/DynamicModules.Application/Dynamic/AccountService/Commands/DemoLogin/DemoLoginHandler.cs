using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.AccountService.Commands.Login;
using DynamicModules.Application.Dynamic.AccountService.Data;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;
using Microsoft.Extensions.Logging;

namespace DynamicModules.Application.Dynamic.AccountService.Commands.DemoLogin;

/// <summary>
/// Demo 站一鍵登入：不驗密碼、不看鎖定。只收啟用中的示範帳號與 admin。
/// 平常不寫資料庫（Demo 產生器的寫入交易會鎖表，第一位訪客喚醒站台時不能被擋住）。
/// </summary>
public class DemoLoginHandler(AuthSettings settings, IAccountRepository accounts, IUnitOfWork uow, ILogger<DemoLoginHandler> logger)
    : IRequestHandler<DemoLoginRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(DemoLoginRequest request, CancellationToken ct)
    {
        if (!settings.DemoLoginEnabled) return ServiceResult.NotFound("找不到資源");

        var userId = (request.UserId ?? "").Trim().ToLowerInvariant();
        var user = await accounts.GetTrackedAsync(userId, ct);
        if (user is null || !user.IsActive || !(user.IsDemoAccount || user.UserId == "admin"))
        {
            logger.LogWarning("帳號 {UserId} 一鍵登入失敗", userId);
            return ServiceResult.Unauthorized(LoginHandler.FailureMessage);
        }

        if (user.MustChangePassword)
        {
            user.MustChangePassword = false;
            await uow.CommitAsync(ct);
        }

        logger.LogInformation("帳號 {UserId} 一鍵登入成功", userId);
        return ServiceResult.Success(new AuthUserResponse(user.UserId, user.DisplayName, false, user.SecurityStamp));
    }
}
