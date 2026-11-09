using DynamicModules.Application.Common;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.FieldService.Commands.UpdateField;

public record UpdateFieldRequest(long FieldId, FieldType Type, I18nText Label, FieldConfig Config, long BlockId) : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, IMasterDesignRequest
{
    public IReadOnlyList<MasterDesignTarget> MasterTargets => [new(FieldId: FieldId)];
    public PermissionAction RequiredDesignerAction => PermissionAction.Edit;
}
