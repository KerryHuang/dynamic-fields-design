using DynamicModules.Application.Common;
using DynamicModules.Domain.Reports;
using MediatR;

namespace DynamicModules.Application.Dynamic.ReportService.Queries.PreviewReport;

public record PreviewReportRequest(ReportDefinition Definition, string Locale) : IRequest<ServiceResult>;
