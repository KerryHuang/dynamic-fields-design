using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.ReportService.Queries.GetReports;

/// <summary>我的與分享給我的；系統管理員另含所有有分享的報表。</summary>
public class GetReportsHandler(IReportRepository reports, ReportAccess access) : IRequestHandler<GetReportsRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetReportsRequest request, CancellationToken ct)
    {
        var viewer = await access.ViewerAsync(ct);
        var visible = await reports.ListVisibleAsync(viewer.UserId, viewer.GroupIds, ct);
        if (viewer.IsAdmin)
            visible.AddRange((await reports.ListSharedAsync(ct)).Where(r => visible.All(v => v.Id != r.Id)));
        var names = await access.UserNamesAsync(ct);
        return ServiceResult.Success(visible.OrderBy(r => r.Id).Select(r => r.ToItem(viewer, names)).ToList());
    }
}
