using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.FieldService.Commands.UpdateBlock;

public class UpdateBlockHandler(IModuleRepository modules, IUnitOfWork uow, ICurrentUser user, IBusinessTime time)
    : IRequestHandler<UpdateBlockRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(UpdateBlockRequest request, CancellationToken ct)
    {
        var module = await modules.GetByBlockIdAsync(request.BlockId, ct);
        if (module is null) return ServiceResult.NotFound(MessageRef.Of(MessageText.BlockNotFound));

        var now = time.Now;
        var block = module.Blocks.Single(b => b.Id == request.BlockId);
        block.LabelI18n = request.Label;
        block.Collapsed = request.Collapsed;
        if (request.Columns is { } columns) block.Columns = columns;
        block.StampModified(user.UserId, now);
        module.TouchVersion(user.UserId, now);
        await uow.CommitAsync(ct);
        return ServiceResult.Success(block.ToResponse());
    }
}
