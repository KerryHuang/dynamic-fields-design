using DynamicModules.Application.Common;
using DynamicModules.Domain.Reports;
using MediatR;

namespace DynamicModules.Application.Dynamic.ReportService.Queries.RunReport;

public record RunReportRequest(long Id, ReportDateRange? Range, string Locale) : IRequest<ServiceResult>;
