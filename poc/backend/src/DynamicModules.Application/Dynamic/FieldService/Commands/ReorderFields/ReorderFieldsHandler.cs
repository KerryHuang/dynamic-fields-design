using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.FieldService.Data;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.FieldService.Commands.ReorderFields;

public class ReorderFieldsHandler(IModuleRepository modules, IUnitOfWork uow, ICurrentUser user, IBusinessTime time)
    : IRequestHandler<ReorderFieldsRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(ReorderFieldsRequest request, CancellationToken ct)
    {
        var module = await modules.GetByBlockIdAsync(request.BlockId, ct);
        if (module is null) return ServiceResult.NotFound("找不到區塊");
        var block = module.Blocks.Single(b => b.Id == request.BlockId);

        // 清單須恰為區塊內全部欄位（含停用），不重複。
        var expected = block.Fields.Select(f => f.Id).Order();
        if (!request.FieldIds.Order().SequenceEqual(expected))
            return FieldRules.Invalid("FieldIds", "欄位清單與區塊內欄位不一致");

        var now = time.Now;
        for (var i = 0; i < request.FieldIds.Count; i++)
        {
            var field = block.Fields.Single(f => f.Id == request.FieldIds[i]);
            field.Sequence = i + 1;
            field.StampModified(user.UserId, now);
        }

        module.TouchVersion(user.UserId, now);
        await uow.CommitAsync(ct);
        return ServiceResult.Success(block.ToResponse());
    }
}
