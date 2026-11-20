using DynamicModules.Application.Common;
using MediatR;

namespace DynamicModules.Application.Dynamic.ReportService.Queries.GetReportCatalog;

/// <summary>系統資料來源清單（看板設計 §12，J11）：任務、任務事件。模組清單仍走 <see cref="GetReportMainModulesRequest"/>。</summary>
public record GetReportDatasetsRequest(string? Locale) : IRequest<ServiceResult>;
