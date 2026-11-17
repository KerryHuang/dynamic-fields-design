using DynamicModules.Application.Common;
using MediatR;

namespace DynamicModules.Application.Dynamic.ReportService.Queries.GetReportCatalog;

/// <summary>系統資料來源的欄位目錄（看板設計 §12，J11）；形狀同 <see cref="ModuleCatalog"/>，沒有帶出欄位與子模組。</summary>
public record GetReportDatasetCatalogRequest(string Dataset, string Locale) : IRequest<ServiceResult>;
