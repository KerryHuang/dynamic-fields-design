using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.DashboardService.Data;
using DynamicModules.Application.Dynamic.ReportService;
using MediatR;

namespace DynamicModules.Application.Dynamic.DashboardService.Queries.GetDashboard;

public class GetDashboardHandler(DashboardAccess access, ReportAccess reportAccess) : IRequestHandler<GetDashboardRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetDashboardRequest request, CancellationToken ct) =>
        await access.FindAsync(request.Id, await reportAccess.ViewerAsync(ct), ct) is { } scope
            ? ServiceResult.Success(scope.ToDetail())
            : ServiceResult.NotFound(DashboardRules.NotFound);
}
