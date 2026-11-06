using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ReportService.Data;
using MediatR;

namespace DynamicModules.Application.Dynamic.ReportService.Queries.GetReport;

public class GetReportHandler(ReportAccess access) : IRequestHandler<GetReportRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetReportRequest request, CancellationToken ct)
    {
        var (report, viewer) = await access.FindAsync(request.Id, ct);
        return report is null
            ? ServiceResult.NotFound(ReportRules.ReportNotFound)
            : ServiceResult.Success(await access.DetailAsync(report, viewer, ct));
    }
}
