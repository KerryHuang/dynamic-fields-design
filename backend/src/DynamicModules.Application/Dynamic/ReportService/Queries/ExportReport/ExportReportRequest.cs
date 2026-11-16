using DynamicModules.Application.Common;
using MediatR;

namespace DynamicModules.Application.Dynamic.ReportService.Queries.ExportReport;

public record ExportReportRequest(long Id, string Locale) : IRequest<ServiceResult>;
