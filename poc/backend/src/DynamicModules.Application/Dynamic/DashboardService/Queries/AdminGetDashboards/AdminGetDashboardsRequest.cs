using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.DashboardService.Queries.AdminGetDashboards;

public record AdminGetDashboardsRequest : IRequest<ServiceResult>, ISystemAdminRequest;
