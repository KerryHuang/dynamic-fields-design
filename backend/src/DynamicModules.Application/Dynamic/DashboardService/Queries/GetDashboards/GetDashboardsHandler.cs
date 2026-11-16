using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.DashboardService.Data;
using DynamicModules.Application.Dynamic.ReportService;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.DashboardService.Queries.GetDashboards;

/// <summary>左側清單：指派給我的（依管理員順序）、我自己的、分享給我的報表。管理員也只看自己群組的指派版。</summary>
public class GetDashboardsHandler(IDashboardRepository dashboards, IReportRepository reports, ReportAccess reportAccess)
    : IRequestHandler<GetDashboardsRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetDashboardsRequest request, CancellationToken ct)
    {
        var viewer = await reportAccess.ViewerAsync(ct);
        var assigned = await dashboards.ListAssignedToAsync(viewer.GroupIds, ct);
        var personal = (await dashboards.ListPersonalAsync(viewer.UserId, assigned.Select(d => d.Id).ToList(), ct)).ToDictionary(p => p.DashboardId);
        var mine = await dashboards.ListOwnedAsync(viewer.UserId, ct);

        var visible = await reports.ListVisibleAsync(viewer.UserId, viewer.GroupIds, ct);
        if (viewer.IsAdmin)
            visible.AddRange((await reports.ListSharedAsync(ct)).Where(r => visible.All(v => v.Id != r.Id)));
        var names = await reportAccess.UserNamesAsync(ct);

        return ServiceResult.Success(new DashboardList(
            assigned.Select(d => new DashboardListItem(d.Id, d.Name, d.Version, personal.ContainsKey(d.Id),
                personal.TryGetValue(d.Id, out var p) && p.BaseVersion < d.Version)).ToList(),
            mine.Select(d => new MyDashboardItem(d.Id, d.Name)).ToList(),
            visible.Where(r => r.OwnerUserId != viewer.UserId).OrderBy(r => r.Id)
                .Select(r => new SharedReportItem(r.Id, r.Name, names.GetValueOrDefault(r.OwnerUserId, r.OwnerUserId))).ToList()));
    }
}
