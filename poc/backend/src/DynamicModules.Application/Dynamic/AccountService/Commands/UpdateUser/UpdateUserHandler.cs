using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.AccountService.Data;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.AccountService.Commands.UpdateUser;

public class UpdateUserHandler(IAccountRepository accounts, IUnitOfWork uow) : IRequestHandler<UpdateUserRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(UpdateUserRequest request, CancellationToken ct)
    {
        var user = await accounts.GetTrackedAsync(request.UserId, ct);
        if (user is null) return ServiceResult.NotFound("使用者不存在");
        var groupIds = request.GroupIds ?? [];
        if (!await accounts.GroupsExistAsync(groupIds, ct))
            return ServiceResult.Invalid(new Dictionary<string, string[]> { ["groupIds"] = ["群組不存在"] });
        if (user is { IsActive: true, IsSystemAdmin: true } && !request.IsSystemAdmin && await accounts.CountActiveAdminsAsync(ct) <= 1)
            return ServiceResult.Invalid(new Dictionary<string, string[]> { ["isSystemAdmin"] = ["不能取消最後一位系統管理員"] });

        user.DisplayName = request.DisplayName.Trim();
        user.IsSystemAdmin = request.IsSystemAdmin;
        await accounts.SetMembershipsAsync(user.UserId, groupIds, ct);
        await uow.CommitAsync(ct);
        return ServiceResult.Success(new AdminUserResponse(
            user.UserId, user.DisplayName, user.IsActive, user.IsSystemAdmin, user.IsDemoAccount, false, groupIds.Distinct().Order().ToList()));
    }
}
