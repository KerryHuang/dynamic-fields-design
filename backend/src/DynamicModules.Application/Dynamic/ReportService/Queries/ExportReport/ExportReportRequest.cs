using DynamicModules.Application.Common;
using DynamicModules.Domain.Reports;
using MediatR;

namespace DynamicModules.Application.Dynamic.ReportService.Queries.ExportReport;

public record ExportReportRequest(long Id, string? Locale, ReportDateRange? Range = null,
    IReadOnlyList<long>? DepartmentIds = null, bool IncludeSubDepartments = true) : IRequest<ServiceResult>;
