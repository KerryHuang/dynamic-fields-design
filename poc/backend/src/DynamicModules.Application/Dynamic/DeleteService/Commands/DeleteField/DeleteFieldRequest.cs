using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.DeleteService.Commands.DeleteField;

/// <summary>Purged 是呼叫端看到的「將清除的軟刪除記錄筆數」，與交易內重算的結果不符就拒絕。</summary>
public record DeleteFieldRequest(long FieldId, int? Purged) : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest
{
    public PermissionAction RequiredDesignerAction => PermissionAction.Delete;
}
