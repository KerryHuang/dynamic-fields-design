using DynamicModules.Application.Common;
using MediatR;

namespace DynamicModules.Application.Dynamic.DashboardService.Queries.GetDashboards;

public record GetDashboardsRequest : IRequest<ServiceResult>;
