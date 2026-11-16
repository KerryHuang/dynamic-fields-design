using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.DashboardService.Queries.AdminGetDashboard;

public record AdminGetDashboardRequest(long Id) : IRequest<ServiceResult>, ISystemAdminRequest;
