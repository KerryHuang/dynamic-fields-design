using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.FieldService.Commands.ReorderFields;

public record ReorderFieldsRequest(long BlockId, List<long> FieldIds) : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, IMasterDesignRequest, IAuditedRequest
{
    public AuditAction Audit => AuditAction.For("field.reorder", AuditTargets.Block, BlockId);
    public IReadOnlyList<MasterDesignTarget> MasterTargets => [new(BlockId: BlockId)];
    public PermissionAction RequiredDesignerAction => PermissionAction.Edit;
}
