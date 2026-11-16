using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.AccountService.Commands.UpdateUser;

public record UpdateUserRequest(string UserId, string DisplayName, List<long> GroupIds, bool IsSystemAdmin)
    : IRequest<ServiceResult>, ITransactionalRequest, ISystemAdminRequest;
