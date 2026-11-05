using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Domain.Reports;
using MediatR;

namespace DynamicModules.Application.Dynamic.ReportService.Queries.RunReport;

/// <summary>分享只給定義；資料權限由 runner 依執行者計算，沒權限回 restricted 狀態（HTTP 200）。</summary>
public class RunReportHandler(ReportAccess access, ReportRunner runner) : IRequestHandler<RunReportRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(RunReportRequest request, CancellationToken ct)
    {
        var (report, _) = await access.FindAsync(request.Id, ct);
        if (report is null) return ServiceResult.NotFound(ReportRules.ReportNotFound);
        if (ReportRules.CheckRange(request.Range) is { } error)
            return ServiceResult.Invalid(ReportRules.ToErrors("range", [error]));

        var result = await runner.RunAsync(ReportDefinition.Parse(report.DefinitionJson), new ReportRunOptions(request.Range, null, request.Locale), ct);
        return ServiceResult.Success(result);
    }
}
