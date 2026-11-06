using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.DashboardService.Data;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.DashboardService.Queries.AdminGetDashboards;

public class AdminGetDashboardsHandler(IDashboardRepository dashboards) : IRequestHandler<AdminGetDashboardsRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(AdminGetDashboardsRequest request, CancellationToken ct) =>
        ServiceResult.Success((await dashboards.ListAssignedAsync(ct)).Select(d => d.ToAdminItem()).ToList());
}
