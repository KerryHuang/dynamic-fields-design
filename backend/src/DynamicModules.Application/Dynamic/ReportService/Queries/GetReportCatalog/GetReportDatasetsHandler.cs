using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Localization;
using DynamicModules.Domain.Reports;
using MediatR;

namespace DynamicModules.Application.Dynamic.ReportService.Queries.GetReportCatalog;

public record CatalogDataset(string Dataset, string Name);

/// <summary>任何登入者都看得到清單；列的範圍與欄位選項依觀看者另外決定（目錄端點、執行）。</summary>
public class GetReportDatasetsHandler(RequestLocale requestLocale, IMessageCatalog messages) : IRequestHandler<GetReportDatasetsRequest, ServiceResult>
{
    public Task<ServiceResult> Handle(GetReportDatasetsRequest request, CancellationToken ct)
    {
        var lc = requestLocale.For(request.Locale);
        return Task.FromResult(ServiceResult.Success(new[] { ReportDatasets.Tasks, ReportDatasets.TaskEvents }
            .Select(d => new CatalogDataset(d, messages.Get(lc, TaskReportFields.DatasetNameKeys[d]))).ToList()));
    }
}
