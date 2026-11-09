using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.FieldService.Commands.AddBlock;

public class AddBlockHandler(
    IModuleRepository modules, IUnitOfWork uow, ICurrentUser user, IBusinessTime time,
    Dynamic.AppService.SharedMasters.MasterDesigner masterDesigner)
    : IRequestHandler<AddBlockRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(AddBlockRequest request, CancellationToken ct)
    {
        var module = await modules.GetByKeyAsync(request.ModuleKey, includeChildren: true, ct);
        if (module is null) return ServiceResult.NotFound("找不到模組");

        var now = time.Now;
        var block = new Block
        {
            LabelI18n = request.Label,
            Sequence = module.Blocks.Select(b => b.Sequence).DefaultIfEmpty(0).Max() + 1
        };
        block.StampCreated(user.UserId, now);
        module.Blocks.Add(block);
        module.TouchVersion(user.UserId, now);
        await uow.CommitAsync(ct);
        if (request.Designer) await masterDesigner.BindBlockAsync(module, block, ct);
        return ServiceResult.Created(block.ToResponse());
    }
}
