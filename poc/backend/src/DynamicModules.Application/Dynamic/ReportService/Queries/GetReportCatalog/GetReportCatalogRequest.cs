using DynamicModules.Application.Common;
using MediatR;

namespace DynamicModules.Application.Dynamic.ReportService.Queries.GetReportCatalog;

/// <summary>可當主模組的模組清單。</summary>
public record GetReportMainModulesRequest(string Locale) : IRequest<ServiceResult>;

/// <summary>某主模組可選的欄位、帶出欄位與子模組。</summary>
public record GetReportCatalogRequest(string ModuleKey, string Locale) : IRequest<ServiceResult>;
