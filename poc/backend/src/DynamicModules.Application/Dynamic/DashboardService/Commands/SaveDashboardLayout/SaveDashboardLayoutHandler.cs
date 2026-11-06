using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.DashboardService.Data;
using DynamicModules.Application.Dynamic.ReportService;
using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Reports;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.DashboardService.Commands.SaveDashboardLayout;

public class SaveDashboardLayoutHandler(IDashboardRepository dashboards, IUnitOfWork uow, IBusinessTime time, DashboardAccess access, ReportAccess reportAccess)
    : IRequestHandler<SaveDashboardLayoutRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(SaveDashboardLayoutRequest request, CancellationToken ct)
    {
        var viewer = await reportAccess.ViewerAsync(ct);
        var scope = await access.FindAsync(request.Id, viewer, ct);
        if (scope is null) return ServiceResult.NotFound(DashboardRules.NotFound);

        var errors = DashboardRules.CheckLayout(request.Layout);
        if (!scope.IsAssigned && request.Name is not null && ReportRules.CheckName(request.Name) is { } nameError) errors.Add(nameError);
        if (errors.Count == 0)
        {
            // 原本就在版面裡的報表不重驗可見性（取消分享後的卡片、指派版的卡片）
            var existing = scope.EffectiveLayout.ReportIds().Concat(scope.IsAssigned ? DashboardLayout.Parse(scope.Dashboard.LayoutJson).ReportIds() : []);
            if (await access.CheckReportsAsync(request.Layout, existing, viewer, ct) is { } reportError) errors.Add(reportError);
        }
        if (errors.Count > 0) return ServiceResult.Invalid(ReportRules.ToErrors("layout", errors));

        var json = DashboardLayout.Serialize(request.Layout);
        var d = scope.Dashboard;
        if (!scope.IsAssigned)
        {
            d.LayoutJson = json;
            if (request.Name is not null) d.Name = request.Name.Trim();
            d.StampModified(viewer.UserId, time.Now);
            await uow.CommitAsync(ct);
            return ServiceResult.Success(await access.DetailAsync(scope, viewer, ct));
        }

        var personal = scope.Personal;
        if (personal is null)
        {
            personal = new DashboardPersonalVersion { DashboardId = d.Id, UserId = viewer.UserId, BaseVersion = d.Version };
            dashboards.AddPersonal(personal);
        }
        personal.LayoutJson = json;
        await uow.CommitAsync(ct);
        return ServiceResult.Success(await access.DetailAsync(scope with { Personal = personal }, viewer, ct));
    }
}
