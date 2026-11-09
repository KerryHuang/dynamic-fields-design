using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.FieldService.Commands.ReorderFields;

public record ReorderFieldsRequest(long BlockId, List<long> FieldIds) : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, IMasterDesignRequest
{
    public IReadOnlyList<MasterDesignTarget> MasterTargets => [new(BlockId: BlockId)];
    public PermissionAction RequiredDesignerAction => PermissionAction.Edit;
}
