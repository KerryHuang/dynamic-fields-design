using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Domain.Reports;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.ReportService.Commands.DeleteReport;

/// <summary>被他人的儀表板、指派版或任何人的個人版引用時不能刪（列出引用，spec §7）；只有刪除者自己的儀表板與個人版引用時一併移除卡片。</summary>
public class DeleteReportHandler(IReportRepository reports, IDashboardRepository dashboards, IUnitOfWork uow, ReportAccess access)
    : IRequestHandler<DeleteReportRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(DeleteReportRequest request, CancellationToken ct)
    {
        var (report, viewer) = await access.FindAsync(request.Id, ct);
        if (report is null) return ServiceResult.NotFound(ReportRules.ReportNotFound);
        // 系統管理員可刪有分享的報表（看得到才會走到這裡）；其他人只有擁有者
        if (report.OwnerUserId != viewer.UserId && !viewer.IsAdmin) return ServiceResult.Forbidden();

        var others = (await dashboards.ListReferencesAsync(report.Id, ct))
            .Where(r => !(r.IsPersonalVersion ? r.PersonalUserId == viewer.UserId : r.OwnerUserId == viewer.UserId))
            .ToList();
        if (others.Count > 0)
        {
            var names = await access.UserNamesAsync(ct);
            string Owner(DashboardReportReference r) =>
                r.IsPersonalVersion ? names.GetValueOrDefault(r.PersonalUserId!, r.PersonalUserId!)
                : r.OwnerUserId is null ? "" : names.GetValueOrDefault(r.OwnerUserId, r.OwnerUserId);
            return ServiceResult.ConflictWith("報表還有儀表板在使用，不能刪除",
                new { References = others.Select(r => new ReportReference(r.DashboardName, Owner(r), r.OwnerUserId is null, r.IsPersonalVersion)).ToList() });
        }

        var (owned, personal) = await dashboards.ListOwnReferencingTrackedAsync(report.Id, viewer.UserId, ct);
        foreach (var d in owned) d.LayoutJson = DashboardLayout.Serialize(DashboardLayout.Parse(d.LayoutJson).WithoutReport(report.Id));
        foreach (var p in personal) p.LayoutJson = DashboardLayout.Serialize(DashboardLayout.Parse(p.LayoutJson).WithoutReport(report.Id));

        reports.Remove(report);
        await uow.CommitAsync(ct);
        return ServiceResult.Success();
    }
}

/// <summary>刪除報表被擋時列出的引用：儀表板名稱、擁有者（指派版為空白）、是否指派版、是否某人的個人版。</summary>
public record ReportReference(string DashboardName, string OwnerName, bool IsAssigned, bool IsPersonalVersion);
