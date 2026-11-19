using DynamicModules.Application.Dynamic.AccountService.SystemPrivileges;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.AccountService.Commands.SetGroupMembers;

public class SetGroupMembersHandler(IAccountRepository accounts, IUnitOfWork uow, AuditContext audit, SystemPrivilegeHolders holders) : IRequestHandler<SetGroupMembersRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(SetGroupMembersRequest request, CancellationToken ct)
    {
        if (await accounts.GetGroupTrackedAsync(request.GroupId, ct) is null) return ServiceResult.NotFound("群組不存在");
        if (!await accounts.UsersExistAsync(request.UserIds, ct))
            return ServiceResult.Invalid("userIds", MessageRef.Of(MessageText.UserNotFound));

        var holdersBefore = await holders.SnapshotAsync(ct);
        var oldMembers = await accounts.SetGroupMembersAsync(request.GroupId, request.UserIds, ct);
        await uow.CommitAsync(ct);
        holders.Report(holdersBefore, await holders.SnapshotAsync(ct));
        audit.Describe(new { UserIds = oldMembers }, new { UserIds = request.UserIds.Distinct().Order(StringComparer.Ordinal).ToList() });
        return ServiceResult.Success();
    }
}
