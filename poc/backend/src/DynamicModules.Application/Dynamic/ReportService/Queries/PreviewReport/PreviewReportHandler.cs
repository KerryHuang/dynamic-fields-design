using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ReportService.Data;
using MediatR;

namespace DynamicModules.Application.Dynamic.ReportService.Queries.PreviewReport;

/// <summary>未存檔試跑：與存檔同一套驗證，之後照執行者權限跑。</summary>
public class PreviewReportHandler(ReportDefinitionValidator validator, ReportRunner runner) : IRequestHandler<PreviewReportRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(PreviewReportRequest request, CancellationToken ct)
    {
        var (definition, failure) = await validator.ValidateAsync(null, request.Definition, ct);
        if (definition is null) return failure!;
        return ServiceResult.Success(ReportRules.CapDetail(await runner.RunAsync(definition, new ReportRunOptions(null, null, request.Locale), ct)));
    }
}
