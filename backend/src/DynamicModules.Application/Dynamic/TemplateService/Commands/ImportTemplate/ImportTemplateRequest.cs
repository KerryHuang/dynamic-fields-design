using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Domain.ValueObjects;
using MediatR;

namespace DynamicModules.Application.Dynamic.TemplateService.Commands.ImportTemplate;

/// <summary>Json 是範本檔原文。</summary>
public record ImportTemplateRequest(string Json) : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest
{
    public PermissionAction RequiredDesignerAction => PermissionAction.Add;
}
