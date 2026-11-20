using DynamicModules.Application.Common;
using DynamicModules.Domain.Reports;
using MediatR;

namespace DynamicModules.Application.Dynamic.ReportService.Queries.DrillReport;

public record DrillReportRequest(long Id, List<string> GroupKeys, ReportDateRange? Range, string? Locale,
    IReadOnlyList<long>? DepartmentIds = null, bool IncludeSubDepartments = true) : IRequest<ServiceResult>;
