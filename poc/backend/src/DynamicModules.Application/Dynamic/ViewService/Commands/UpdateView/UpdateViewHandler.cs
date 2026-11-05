using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.ViewService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.Modules;
using MediatR;

namespace DynamicModules.Application.Dynamic.ViewService.Commands.UpdateView;

public class UpdateViewHandler(
    IModuleRepository modules, IListViewRepository views, IUnitOfWork uow, FieldTypeRegistry registry,
    ICurrentUser user, IBusinessTime time, IDynamicAccessGuard guard) : IRequestHandler<UpdateViewRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(UpdateViewRequest request, CancellationToken ct)
    {
        var view = await views.GetAsync(request.Id, ct);
        if (view is null || (view.OwnerUserId is { } owner && owner != user.UserId))
            return ServiceResult.NotFound(ViewRules.ViewNotFound);
        var module = await modules.GetByIdAsync(view.ModuleId, includeChildren: true, ct);
        if (module is not { Status: ModuleStatus.Active }) return ServiceResult.NotFound(RecordMapper.ModuleNotFound);
        if (ModuleRelations.IsDetail(module)) return ServiceResult.NotFound(ViewRules.ViewNotFound);

        var access = await guard.AuthorizeAsync(module, PermissionAction.Read, ct);
        if (access.Permissions is not { } p) return access.Denied!;
        if (view.OwnerUserId is null && !(await guard.DesignerActionsAsync(ct)).Contains(PermissionAction.Edit))
            return ServiceResult.Forbidden();
        // 共用檢視含我看不到的欄位：存回會連帶移除該欄位的設定、影響所有人，所以不允許修改
        if (view.OwnerUserId is null && ViewRules.ReferencesHiddenField(view, module, p.CanSee))
            return ServiceResult.Forbidden();

        var errors = ViewRules.Check(module, registry, await modules.ListUserIdsAsync(ct),
            request.Columns, request.Filter, request.Sort, p.CanSee);
        if (errors.Count > 0) return ServiceResult.Invalid(errors);

        if (request.IsDefault)
            await views.ClearDefaultsAsync(module.Id, view.OwnerUserId, view.Id, user.UserId, time.Now, ct);
        view.NameI18n = request.Name;
        view.Columns = request.Columns;
        view.Filter = ViewRules.NormalizeFilter(request.Filter);
        view.Sort = request.Sort;
        view.IsDefault = request.IsDefault;
        view.StampModified(user.UserId, time.Now);
        await uow.CommitAsync(ct);
        return ServiceResult.Success(view.ToResponse(module, user.UserId, p));
    }
}
