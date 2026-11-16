using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.LedgerService;

public record LedgerInput(long ReversalFieldId, List<long> Negate, List<List<long>> Swap, bool ConfirmFreeze);

public record LedgerResponse(string ModuleKey, long ReversalFieldId, List<long> Negate, List<List<long>> Swap, int RecordCount);

public record SaveLedgerRequest(string ModuleKey, LedgerInput Input) : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, IMasterDesignRequest
{
    public IReadOnlyList<MasterDesignTarget> MasterTargets => [new(ModuleKey: ModuleKey)];
    public PermissionAction RequiredDesignerAction => PermissionAction.Edit;
}

public record DeleteLedgerRequest(string ModuleKey) : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, IMasterDesignRequest
{
    public IReadOnlyList<MasterDesignTarget> MasterTargets => [new(ModuleKey: ModuleKey)];
    public PermissionAction RequiredDesignerAction => PermissionAction.Edit;
}

public record GetLedgerRequest(string ModuleKey) : IRequest<ServiceResult>, IDesignerRequest
{
    public PermissionAction RequiredDesignerAction => PermissionAction.Read;
}
