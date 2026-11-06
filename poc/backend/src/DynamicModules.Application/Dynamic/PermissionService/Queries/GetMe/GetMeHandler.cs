using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.PermissionService.Data;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.PermissionService.Queries.GetMe;

public class GetMeHandler(ICurrentUser current, IPermissionRepository permissions, IDynamicAccessGuard guard)
    : IRequestHandler<GetMeRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetMeRequest request, CancellationToken ct)
    {
        var user = await permissions.GetUserAsync(current.UserId, ct);
        if (user is null) return ServiceResult.NotFound("使用者不存在");

        var mine = (await permissions.ListMembershipsAsync(ct)).Where(m => m.UserId == user.UserId).Select(m => m.GroupId).ToHashSet();
        var groups = (await permissions.ListGroupsAsync(ct)).Where(g => mine.Contains(g.Id))
            .Select(g => new GroupBriefResponse(g.Id, g.Key, g.NameI18n)).ToList();
        var actions = await guard.DesignerActionsAsync(ct);
        var designer = new DesignerActionsResponse(
            actions.Contains(PermissionAction.Read), actions.Contains(PermissionAction.Add),
            actions.Contains(PermissionAction.Edit), actions.Contains(PermissionAction.Delete));
        return ServiceResult.Success(new MeResponse(user.UserId, user.DisplayName, user.IsSystemAdmin, user.MustChangePassword, groups, designer, user.IsDemoAccount));
    }
}
