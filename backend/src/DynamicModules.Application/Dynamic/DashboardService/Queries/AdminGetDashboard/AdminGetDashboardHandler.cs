using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.DashboardService.Data;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.DashboardService.Queries.AdminGetDashboard;

/// <summary>管理員編輯用：指派版本體（不是管理員自己的個人版）。</summary>
public class AdminGetDashboardHandler(IDashboardRepository dashboards) : IRequestHandler<AdminGetDashboardRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(AdminGetDashboardRequest request, CancellationToken ct) =>
        await dashboards.GetAsync(request.Id, ct) is { OwnerUserId: null } d
            ? ServiceResult.Success(d.ToAdminDetail())
            : ServiceResult.NotFound(DashboardRules.NotFound);
}
