using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Domain.ValueObjects;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.FieldService.Commands.UpdateOption;

public record UpdateOptionRequest(long OptionId, I18nText Label, string? Color, bool Active) : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, IMasterDesignRequest, IAuditedRequest
{
    public AuditAction Audit => AuditAction.For("option.update", AuditTargets.Option, OptionId);
    public IReadOnlyList<MasterDesignTarget> MasterTargets => [new(OptionId: OptionId)];
    public PermissionAction RequiredDesignerAction => PermissionAction.Edit;
}
