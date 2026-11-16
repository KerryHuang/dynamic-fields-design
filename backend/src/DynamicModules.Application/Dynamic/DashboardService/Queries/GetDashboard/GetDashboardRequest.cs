using DynamicModules.Application.Common;
using MediatR;

namespace DynamicModules.Application.Dynamic.DashboardService.Queries.GetDashboard;

public record GetDashboardRequest(long Id) : IRequest<ServiceResult>;
