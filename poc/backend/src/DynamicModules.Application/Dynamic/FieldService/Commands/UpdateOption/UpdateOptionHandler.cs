using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.FieldService.Commands.UpdateOption;

public class UpdateOptionHandler(IModuleRepository modules, IUnitOfWork uow, ICurrentUser user, IBusinessTime time)
    : IRequestHandler<UpdateOptionRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(UpdateOptionRequest request, CancellationToken ct)
    {
        var module = await modules.GetByOptionIdAsync(request.OptionId, ct);
        if (module is null) return ServiceResult.NotFound("找不到選項");

        var now = time.Now;
        var option = module.Blocks.SelectMany(b => b.Fields).SelectMany(f => f.Options).Single(o => o.Id == request.OptionId);
        option.LabelI18n = request.Label;
        option.Color = request.Color;
        option.Active = request.Active;
        option.StampModified(user.UserId, now);
        module.TouchVersion(user.UserId, now);
        await uow.CommitAsync(ct);
        return ServiceResult.Success(option.ToResponse());
    }
}
