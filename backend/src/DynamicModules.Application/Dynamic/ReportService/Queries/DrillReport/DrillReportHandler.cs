using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Domain.Reports;
using MediatR;

namespace DynamicModules.Application.Dynamic.ReportService.Queries.DrillReport;

public class DrillReportHandler(ReportAccess access, ReportRunner runner, ReportLimitReader limitReader) : IRequestHandler<DrillReportRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(DrillReportRequest request, CancellationToken ct)
    {
        var (report, _) = await access.FindAsync(request.Id, ct);
        if (report is null) return ServiceResult.NotFound(ReportRules.ReportNotFound);
        var limits = await limitReader.GetAsync(ct);
        if (ReportRules.CheckRange(request.Range, limits.MaxRangeDays) is { } error)
            return ServiceResult.Invalid(ReportRules.ToErrors("range", [error]));

        var result = await runner.RunAsync(
            ReportDefinition.Parse(report.DefinitionJson), new ReportRunOptions(request.Range, request.GroupKeys, request.Locale,
                DepartmentIds: request.DepartmentIds, IncludeSubDepartments: request.IncludeSubDepartments), ct);
        if (result.DetailRows is not { } rows || rows.Count <= limits.DrillLimit) return ServiceResult.Success(new DrillResult(result, false));
        return ServiceResult.Success(new DrillResult(ReportRules.TakeDetail(result, limits.DrillLimit), true));
    }
}
