using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.ViewService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.Modules;
using MediatR;

namespace DynamicModules.Application.Dynamic.ViewService.Commands.DeleteView;

public class DeleteViewHandler(
    IModuleRepository modules, IListViewRepository views, IUnitOfWork uow, ICurrentUser user, IDynamicAccessGuard guard)
    : IRequestHandler<DeleteViewRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(DeleteViewRequest request, CancellationToken ct)
    {
        var view = await views.GetAsync(request.Id, ct);
        if (view is null || (view.OwnerUserId is { } owner && owner != user.UserId))
            return ServiceResult.NotFound(ViewRules.ViewNotFound);
        var module = await modules.GetByIdAsync(view.ModuleId, includeChildren: true, ct);
        if (module is not { Status: ModuleStatus.Active }) return ServiceResult.NotFound(RecordMapper.ModuleNotFound);
        if (ModuleRelations.IsDetail(module)) return ServiceResult.NotFound(ViewRules.ViewNotFound);

        var access = await guard.AuthorizeAsync(module, PermissionAction.Read, ct);
        if (access.Permissions is null) return access.Denied!;
        if (view.OwnerUserId is null && !(await guard.DesignerActionsAsync(ct)).Contains(PermissionAction.Edit))
            return ServiceResult.Forbidden();

        views.Remove(view);
        await uow.CommitAsync(ct);
        return ServiceResult.Success();
    }
}
