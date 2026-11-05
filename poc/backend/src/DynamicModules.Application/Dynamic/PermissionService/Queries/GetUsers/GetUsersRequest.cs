using DynamicModules.Application.Common;
using MediatR;

namespace DynamicModules.Application.Dynamic.PermissionService.Queries.GetUsers;

public record GetUsersRequest : IRequest<ServiceResult>;
