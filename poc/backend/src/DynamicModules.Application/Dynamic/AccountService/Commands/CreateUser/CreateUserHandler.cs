using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.AccountService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.AccountService.Commands.CreateUser;

public class CreateUserHandler(IAccountRepository accounts, IPasswordService passwords, IUnitOfWork uow)
    : IRequestHandler<CreateUserRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(CreateUserRequest request, CancellationToken ct)
    {
        var id = request.UserId.ToLowerInvariant();
        var groupIds = request.GroupIds ?? [];
        if (await accounts.UserExistsAsync(id, ct)) return ServiceResult.Conflict($"帳號「{id}」已存在");
        if (!await accounts.GroupsExistAsync(groupIds, ct))
            return ServiceResult.Invalid(new Dictionary<string, string[]> { ["groupIds"] = ["群組不存在"] });

        accounts.AddUser(new AppUser
        {
            UserId = id, DisplayName = request.DisplayName.Trim(), IsSystemAdmin = request.IsSystemAdmin,
            PasswordHash = passwords.Hash(request.Password), MustChangePassword = true
        });
        await accounts.SetMembershipsAsync(id, groupIds, ct);
        await uow.CommitAsync(ct);
        return ServiceResult.Created(new AdminUserResponse(
            id, request.DisplayName.Trim(), true, request.IsSystemAdmin, false, false, groupIds.Distinct().Order().ToList()));
    }
}
