using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.DashboardService.Data;
using DynamicModules.Application.Dynamic.ReportService;
using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Reports;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.DashboardService.Commands.CreateDashboard;

/// <summary>建立自己的儀表板（不限張數；不能指派、不能分享）。</summary>
public class CreateDashboardHandler(IDashboardRepository dashboards, IUnitOfWork uow, IBusinessTime time, DashboardAccess access, ReportAccess reportAccess)
    : IRequestHandler<CreateDashboardRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(CreateDashboardRequest request, CancellationToken ct)
    {
        var viewer = await reportAccess.ViewerAsync(ct);
        var errors = DashboardRules.CheckLayout(request.Layout);
        if (ReportRules.CheckName(request.Name) is { } nameError) errors.Add(nameError);
        if (errors.Count == 0 && await access.CheckReportsAsync(request.Layout, [], viewer, ct) is { } reportError) errors.Add(reportError);
        if (errors.Count > 0) return ServiceResult.Invalid(ReportRules.ToErrors("layout", errors));

        var dashboard = new Dashboard
        {
            OwnerUserId = viewer.UserId, Name = request.Name.Trim(), LayoutJson = DashboardLayout.Serialize(request.Layout)
        };
        dashboard.StampCreated(viewer.UserId, time.Now);
        dashboards.Add(dashboard);
        await uow.CommitAsync(ct);
        return ServiceResult.Created(await access.DetailAsync(new DashboardScope(dashboard, null), viewer, ct));
    }
}
