using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.AccountService.Commands.CreateUser;

public record CreateUserRequest(string UserId, string DisplayName, string Password, List<long>? GroupIds, bool IsSystemAdmin)
    : IRequest<ServiceResult>, ITransactionalRequest, ISystemAdminRequest, IPermissionAdminRequest, IAuditedRequest
{
    public AuditAction Audit => AuditAction.For("user.create", AuditTargets.User, UserId, DisplayName);
}
