using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.ViewService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.Modules;
using MediatR;

namespace DynamicModules.Application.Dynamic.ViewService.Queries.GetViews;

public class GetViewsHandler(IModuleRepository modules, IListViewRepository views, ICurrentUser user, IDynamicAccessGuard guard)
    : IRequestHandler<GetViewsRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetViewsRequest request, CancellationToken ct)
    {
        var module = await modules.GetByKeyAsync(request.ModuleKey, includeChildren: true, ct);
        if (!module.IsReadable() || ModuleRelations.IsDetail(module))
            return ServiceResult.NotFound(RecordMapper.ModuleNotFound);

        var access = await guard.AuthorizeAsync(module, PermissionAction.Read, ct);
        if (access.Permissions is not { } p) return access.Denied!;

        var list = await views.ListVisibleAsync(module.Id, user.UserId, ct);
        return ServiceResult.Success(list.Select(v => v.ToResponse(module, user.UserId, p)).ToList());
    }
}
