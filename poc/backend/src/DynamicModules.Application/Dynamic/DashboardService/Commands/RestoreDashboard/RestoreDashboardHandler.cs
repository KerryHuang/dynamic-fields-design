using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.DashboardService.Data;
using DynamicModules.Application.Dynamic.ReportService;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.DashboardService.Commands.RestoreDashboard;

/// <summary>丟棄個人調整，回到指派版的最新版。沒有個人版也成功（冪等）。自己的儀表板沒有「最新版」可還原，回 404。</summary>
public class RestoreDashboardHandler(IDashboardRepository dashboards, IUnitOfWork uow, DashboardAccess access, ReportAccess reportAccess)
    : IRequestHandler<RestoreDashboardRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(RestoreDashboardRequest request, CancellationToken ct)
    {
        var scope = await access.FindAsync(request.Id, await reportAccess.ViewerAsync(ct), ct);
        if (scope is not { IsAssigned: true }) return ServiceResult.NotFound(DashboardRules.NotFound);
        if (scope.Personal is not null)
        {
            dashboards.RemovePersonal(scope.Personal);
            await uow.CommitAsync(ct);
        }
        return ServiceResult.Success((scope with { Personal = null }).ToDetail());
    }
}
