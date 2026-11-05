using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.AccountService.Queries.AdminListUsers;

public record AdminListUsersRequest : IRequest<ServiceResult>, ISystemAdminRequest;
