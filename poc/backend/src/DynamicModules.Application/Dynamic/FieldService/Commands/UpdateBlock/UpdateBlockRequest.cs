using DynamicModules.Application.Common;
using DynamicModules.Domain.ValueObjects;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.FieldService.Commands.UpdateBlock;

/// <param name="Columns">每列欄數；null 表示不變。</param>
public record UpdateBlockRequest(long BlockId, I18nText Label, bool Collapsed, int? Columns = null) : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest
{
    public PermissionAction RequiredDesignerAction => PermissionAction.Edit;
}
