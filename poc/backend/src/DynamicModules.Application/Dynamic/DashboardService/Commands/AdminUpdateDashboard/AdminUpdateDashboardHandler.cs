using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.DashboardService.Data;
using DynamicModules.Application.Dynamic.ReportService;
using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Domain.Reports;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.DashboardService.Commands.AdminUpdateDashboard;

/// <summary>更新指派版：版本加一，不動任何個人版（個人版的基準版本較舊時使用者會看到「已更新」）。</summary>
public class AdminUpdateDashboardHandler(IDashboardRepository dashboards, IUnitOfWork uow, IBusinessTime time, DashboardAccess access, ReportAccess reportAccess)
    : IRequestHandler<AdminUpdateDashboardRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(AdminUpdateDashboardRequest request, CancellationToken ct)
    {
        var d = await dashboards.GetAsync(request.Id, ct);
        if (d is not { OwnerUserId: null }) return ServiceResult.NotFound(DashboardRules.NotFound);
        if (d.Version != request.Version) return ServiceResult.Conflict("儀表板已被其他人更新，請重新載入後再改");

        var viewer = await reportAccess.ViewerAsync(ct);
        var errors = DashboardRules.CheckLayout(request.Layout);
        if (ReportRules.CheckName(request.Name) is { } nameError) errors.Add(nameError);
        if (errors.Count == 0 && await access.CheckReportsAsync(request.Layout, DashboardLayout.Parse(d.LayoutJson).ReportIds(), viewer, ct) is { } reportError)
            errors.Add(reportError);
        if (errors.Count > 0) return ServiceResult.Invalid(ReportRules.ToErrors("layout", errors));

        d.Name = request.Name.Trim();
        d.LayoutJson = DashboardLayout.Serialize(request.Layout.WithoutFromAssigned());
        d.Version++;
        d.StampModified(viewer.UserId, time.Now);
        await uow.CommitAsync(ct);
        return ServiceResult.Success(d.ToAdminDetail());
    }
}
