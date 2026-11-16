using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.DashboardService.Data;
using DynamicModules.Application.Dynamic.ReportService;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.DashboardService.Commands.DeleteDashboard;

/// <summary>只能刪自己的；指派版走管理路由。</summary>
public class DeleteDashboardHandler(IDashboardRepository dashboards, IUnitOfWork uow, DashboardAccess access, ReportAccess reportAccess)
    : IRequestHandler<DeleteDashboardRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(DeleteDashboardRequest request, CancellationToken ct)
    {
        var scope = await access.FindAsync(request.Id, await reportAccess.ViewerAsync(ct), ct);
        if (scope is not { IsAssigned: false }) return ServiceResult.NotFound(DashboardRules.NotFound);
        await dashboards.RemoveAsync(scope.Dashboard, ct);
        await uow.CommitAsync(ct);
        return ServiceResult.Success();
    }
}
