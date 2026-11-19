using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Security;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.AccountService.SystemPrivileges;

public class UpdateGroupSystemPrivilegesHandler(
    IAccountRepository accounts, IPermissionRepository permissions, SystemPrivilegeHolders holders, IUnitOfWork uow, AuditContext audit)
    : IRequestHandler<UpdateGroupSystemPrivilegesRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(UpdateGroupSystemPrivilegesRequest request, CancellationToken ct)
    {
        if (await accounts.GetGroupTrackedAsync(request.GroupId, ct) is null)
            return ServiceResult.NotFound(MessageRef.Of(MessageText.ResourceNotFound));

        var rows = await permissions.LoadSystemPrivilegeRowsAsync(ct);
        var before = SystemPrivilegeRules.Direct(rows.Groups.Where(r => r.GroupId == request.GroupId));
        var holdersBefore = await holders.SnapshotAsync(ct);
        await permissions.ReplaceSystemRowsAsync(request.GroupId, null,
            SystemPrivilegeRules.ToRows(request.Body, () => new GroupProgramPrivilege { GroupId = request.GroupId }), ct);
        await uow.CommitAsync(ct);
        holders.Report(holdersBefore, await holders.SnapshotAsync(ct));

        var after = SystemPrivilegeRules.Direct((await permissions.LoadSystemPrivilegeRowsAsync(ct)).Groups.Where(r => r.GroupId == request.GroupId));
        audit.Detail("before", before);
        audit.Detail("after", after);
        return ServiceResult.Success(after);
    }
}

public class UpdateUserSystemPrivilegesHandler(
    IAccountRepository accounts, IPermissionRepository permissions, SystemPrivilegeHolders holders, IUnitOfWork uow, AuditContext audit)
    : IRequestHandler<UpdateUserSystemPrivilegesRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(UpdateUserSystemPrivilegesRequest request, CancellationToken ct)
    {
        if (!await accounts.UserExistsAsync(request.UserId, ct))
            return ServiceResult.NotFound(MessageRef.Of(MessageText.ResourceNotFound));

        var rows = await permissions.LoadSystemPrivilegeRowsAsync(ct);
        var before = SystemPrivilegeRules.Direct(rows.Users.Where(r => r.UserId == request.UserId));
        var holdersBefore = await holders.SnapshotAsync(ct);
        await permissions.ReplaceSystemRowsAsync(null, request.UserId,
            SystemPrivilegeRules.ToRows(request.Body, () => new UserProgramPrivilege { UserId = request.UserId }), ct);
        await uow.CommitAsync(ct);
        holders.Report(holdersBefore, await holders.SnapshotAsync(ct));

        var after = SystemPrivilegeRules.Direct((await permissions.LoadSystemPrivilegeRowsAsync(ct)).Users.Where(r => r.UserId == request.UserId));
        audit.Detail("before", before);
        audit.Detail("after", after);
        return ServiceResult.Success(after);
    }
}
