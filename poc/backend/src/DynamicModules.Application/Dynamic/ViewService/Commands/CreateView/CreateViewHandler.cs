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

namespace DynamicModules.Application.Dynamic.ViewService.Commands.CreateView;

public class CreateViewHandler(
    IModuleRepository modules, IListViewRepository views, IUnitOfWork uow, FieldTypeRegistry registry,
    ICurrentUser user, IBusinessTime time, IDynamicAccessGuard guard) : IRequestHandler<CreateViewRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(CreateViewRequest request, CancellationToken ct)
    {
        var module = await modules.GetByKeyAsync(request.ModuleKey, includeChildren: true, ct);
        if (module is not { Status: ModuleStatus.Active } || ModuleRelations.IsDetail(module))
            return ServiceResult.NotFound(RecordMapper.ModuleNotFound);

        var access = await guard.AuthorizeAsync(module, PermissionAction.Read, ct);
        if (access.Permissions is not { } p) return access.Denied!;
        // 共用檢視影響所有人，需要設計器的 Edit
        if (request.Shared && !(await guard.DesignerActionsAsync(ct)).Contains(PermissionAction.Edit))
            return ServiceResult.Forbidden();

        var errors = ViewRules.Check(module, registry, await modules.ListUserIdsAsync(ct),
            request.Columns, request.Filter, request.Sort, p.CanSee);
        if (errors.Count > 0) return ServiceResult.Invalid(errors);

        var owner = request.Shared ? null : user.UserId;
        if (request.IsDefault) await views.ClearDefaultsAsync(module.Id, owner, except: null, user.UserId, time.Now, ct);

        var view = new ListView
        {
            ModuleId = module.Id,
            OwnerUserId = owner,
            NameI18n = request.Name,
            Columns = request.Columns,
            Filter = ViewRules.NormalizeFilter(request.Filter),
            Sort = request.Sort,
            IsDefault = request.IsDefault
        };
        view.StampCreated(user.UserId, time.Now);
        views.Add(view);
        await uow.CommitAsync(ct);
        return ServiceResult.Created(view.ToResponse(module, user.UserId, p));
    }
}
