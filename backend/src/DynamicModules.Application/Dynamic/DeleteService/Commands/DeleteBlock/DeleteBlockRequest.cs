using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.DeleteService.Commands.DeleteBlock;

/// <summary>Purged 是呼叫端看到的「將清除的軟刪除記錄筆數」，與交易內重算的結果不符就拒絕。</summary>
public record DeleteBlockRequest(long BlockId, int? Purged) : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, IMasterDesignRequest
{
    public IReadOnlyList<MasterDesignTarget> MasterTargets => [new(BlockId: BlockId)];
    public PermissionAction RequiredDesignerAction => PermissionAction.Delete;
}
