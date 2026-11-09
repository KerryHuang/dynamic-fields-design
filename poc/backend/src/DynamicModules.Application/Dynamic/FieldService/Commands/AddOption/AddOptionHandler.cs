using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.FieldService.Data;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.FieldService.Commands.AddOption;

public class AddOptionHandler(
    IModuleRepository modules, IUnitOfWork uow, ICurrentUser user, IBusinessTime time,
    Dynamic.AppService.SharedMasters.MasterDesigner masterDesigner)
    : IRequestHandler<AddOptionRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(AddOptionRequest request, CancellationToken ct)
    {
        var module = await modules.GetByFieldIdAsync(request.FieldId, ct);
        if (module is null) return ServiceResult.NotFound("找不到欄位");
        var field = module.Blocks.SelectMany(b => b.Fields).Single(f => f.Id == request.FieldId);
        if (field.Type is not (FieldType.Enum or FieldType.MultiEnum))
            return FieldRules.Invalid("FieldId", "只有下拉欄位可以有選項");
        if (request.Designer && await masterDesigner.RejectAddOptionAsync(module, field, ct) is { } orphaned) return orphaned;

        var now = time.Now;
        var option = new FieldOption
        {
            LabelI18n = request.Label,
            Color = request.Color,
            Sequence = field.Options.Select(o => o.Sequence).DefaultIfEmpty(0).Max() + 1,
            Active = true
        };
        option.StampCreated(user.UserId, now);
        field.Options.Add(option);
        module.TouchVersion(user.UserId, now);
        await uow.CommitAsync(ct);
        if (request.Designer) await masterDesigner.BindOptionAsync(module, field, option, ct);
        return ServiceResult.Created(option.ToResponse());
    }
}
