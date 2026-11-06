using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.ReportService.Commands.DeleteReport;

public class DeleteReportHandler(IReportRepository reports, IUnitOfWork uow, ReportAccess access)
    : IRequestHandler<DeleteReportRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(DeleteReportRequest request, CancellationToken ct)
    {
        var (report, viewer) = await access.FindAsync(request.Id, ct);
        if (report is null) return ServiceResult.NotFound(ReportRules.ReportNotFound);
        // 系統管理員可刪有分享的報表（看得到才會走到這裡）；其他人只有擁有者
        if (report.OwnerUserId != viewer.UserId && !viewer.IsAdmin) return ServiceResult.Forbidden();

        reports.Remove(report);
        await uow.CommitAsync(ct);
        return ServiceResult.Success();
    }
}
