using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.PermissionService.Queries.GetModulePermissions;

public record GetModulePermissionsRequest(string Key) : IRequest<ServiceResult>, IDesignerRequest
{
    public PermissionAction RequiredDesignerAction => PermissionAction.Read;
}
