using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.ConversionService.Commands.DeleteConversion;

public record DeleteConversionRequest(string Key) : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, IMasterDesignRequest
{
    public IReadOnlyList<MasterDesignTarget> MasterTargets => [new(ConversionKey: Key)];
    public PermissionAction RequiredDesignerAction => PermissionAction.Edit;
}

/// <summary>刪除定義不影響已轉出的單據：追溯欄位存在單據自己身上。</summary>
public class DeleteConversionHandler(IConversionRepository conversions, IUnitOfWork uow)
    : IRequestHandler<DeleteConversionRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(DeleteConversionRequest request, CancellationToken ct)
    {
        var conversion = await conversions.GetByKeyAsync(request.Key, ct);
        if (conversion is null) return ServiceResult.NotFound(MessageRef.Of(MessageText.ConversionNotFound));
        conversions.Remove(conversion);
        await uow.CommitAsync(ct);
        return ServiceResult.Success();
    }
}
