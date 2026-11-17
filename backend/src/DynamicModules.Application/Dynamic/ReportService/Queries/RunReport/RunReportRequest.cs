using DynamicModules.Application.Common;
using DynamicModules.Domain.Reports;
using MediatR;

namespace DynamicModules.Application.Dynamic.ReportService.Queries.RunReport;

/// <summary>DepartmentIds、IncludeSubDepartments 是任務資料來源的暫時部門篩選（看板設計 §8.2），模組報表忽略。</summary>
public record RunReportRequest(long Id, ReportDateRange? Range, string Locale, IReadOnlyList<long>? DepartmentIds = null, bool IncludeSubDepartments = true) : IRequest<ServiceResult>;
