using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;
using Microsoft.Extensions.Logging;

namespace DynamicModules.Application.Dynamic.AccountService.Commands.SetUserActive;

public class SetUserActiveHandler(
    ICurrentUser current, IAccountRepository accounts, IUnitOfWork uow, ILogger<SetUserActiveHandler> logger)
    : IRequestHandler<SetUserActiveRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(SetUserActiveRequest request, CancellationToken ct)
    {
        var user = await accounts.GetTrackedAsync(request.UserId, ct);
        if (user is null) return ServiceResult.NotFound("使用者不存在");

        if (!request.IsActive && user.IsActive)
        {
            if (user.IsSystemAdmin && await accounts.CountActiveAdminsAsync(ct) <= 1)
                return Invalid("不能停用最後一位系統管理員");
            if (user.UserId == current.UserId) return Invalid("不能停用自己");
            // 換 stamp 讓既有 session 在下一個請求失效（SessionMiddleware 另外也擋 !IsActive）
            user.SecurityStamp = Guid.NewGuid().ToString("N");
        }

        user.IsActive = request.IsActive;
        await uow.CommitAsync(ct);
        logger.LogInformation("管理員 {Admin} 將帳號 {UserId} 設為{State}", current.UserId, user.UserId, request.IsActive ? "啟用" : "停用");
        return ServiceResult.Success();
    }

    private static ServiceResult Invalid(string message) =>
        ServiceResult.Invalid(new Dictionary<string, string[]> { ["userId"] = [message] });
}
