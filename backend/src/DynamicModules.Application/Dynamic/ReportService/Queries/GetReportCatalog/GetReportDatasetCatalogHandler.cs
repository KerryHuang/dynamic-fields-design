using DynamicModules.Application.Common;
using DynamicModules.Domain.Reports;
using MediatR;

namespace DynamicModules.Application.Dynamic.ReportService.Queries.GetReportCatalog;

/// <summary>
/// 只列定義可以引用的欄位（<see cref="TaskReportFields.Selectable"/>），順序照代碼由 -1 往下；選項依觀看者（所屬模組、任務規則只列可讀的模組，J14、J22）。
/// 不認得的資料來源回 404。
/// </summary>
public class GetReportDatasetCatalogHandler(TaskReportSource source, RequestLocale requestLocale) : IRequestHandler<GetReportDatasetCatalogRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetReportDatasetCatalogRequest request, CancellationToken ct)
    {
        if (!ReportDatasets.IsTask(request.Dataset)) return ServiceResult.NotFound("資料來源不存在");
        var selectable = TaskReportFields.Selectable(request.Dataset);
        var module = await source.CatalogAsync(request.Dataset, ct);
        var fields = module.Blocks.SelectMany(b => b.Fields).Where(f => selectable.Contains(f.Id)).OrderByDescending(f => f.Id)
            .Select(f => GetReportCatalogHandler.ToField(f, requestLocale.For(request.Locale).Locale)).ToList();
        return ServiceResult.Success(new ModuleCatalog(fields, [], []));
    }
}
