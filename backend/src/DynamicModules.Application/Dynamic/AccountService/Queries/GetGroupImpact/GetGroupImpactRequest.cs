using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.AccountService.Queries.GetGroupImpact;

public record GetGroupImpactRequest(long GroupId) : IRequest<ServiceResult>, ISystemAdminRequest;
