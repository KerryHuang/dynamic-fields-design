using DynamicModules.Application.Common;
using MediatR;

namespace DynamicModules.Application.Dynamic.PermissionService.Queries.GetMe;

public record GetMeRequest : IRequest<ServiceResult>;
