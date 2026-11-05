using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.ModuleService.Commands.DeactivateModule;

public class DeactivateModuleHandler(
    IModuleRepository modules, IProgramRepository programs, IUnitOfWork uow, ICurrentUser user, IBusinessTime time)
    : IRequestHandler<DeactivateModuleRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(DeactivateModuleRequest request, CancellationToken ct)
    {
        var module = await modules.GetByKeyAsync(request.Key, includeChildren: false, ct);
        if (module is null) return ServiceResult.NotFound("找不到模組");

        module.Status = ModuleStatus.Inactive;
        var program = await programs.GetAsync(module.ProgramCode, ct);
        if (program is not null) program.Active = false;
        module.TouchVersion(user.UserId, time.Now);
        await uow.CommitAsync(ct);
        return ServiceResult.Success();
    }
}
