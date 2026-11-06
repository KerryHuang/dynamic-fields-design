using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.DashboardService.Data;
using DynamicModules.Application.Dynamic.ReportService;
using MediatR;

namespace DynamicModules.Application.Dynamic.DashboardService.Queries.GetDashboard;

public class GetDashboardHandler(DashboardAccess access, ReportAccess reportAccess) : IRequestHandler<GetDashboardRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetDashboardRequest request, CancellationToken ct)
    {
        var viewer = await reportAccess.ViewerAsync(ct);
        return await access.FindAsync(request.Id, viewer, ct) is { } scope
            ? ServiceResult.Success(await access.DetailAsync(scope, viewer, ct))
            : ServiceResult.NotFound(DashboardRules.NotFound);
    }
}
