using DynamicModules.Application.Common;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.AccountService.Commands.SetGroupMembers;

public class SetGroupMembersHandler(IAccountRepository accounts, IUnitOfWork uow) : IRequestHandler<SetGroupMembersRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(SetGroupMembersRequest request, CancellationToken ct)
    {
        if (await accounts.GetGroupTrackedAsync(request.GroupId, ct) is null) return ServiceResult.NotFound("群組不存在");
        if (!await accounts.UsersExistAsync(request.UserIds, ct))
            return ServiceResult.Invalid("userIds", MessageRef.Of(MessageText.UserNotFound));

        await accounts.SetGroupMembersAsync(request.GroupId, request.UserIds, ct);
        await uow.CommitAsync(ct);
        return ServiceResult.Success();
    }
}
