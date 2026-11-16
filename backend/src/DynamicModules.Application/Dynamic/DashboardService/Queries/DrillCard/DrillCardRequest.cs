using DynamicModules.Application.Common;
using DynamicModules.Domain.Reports;
using MediatR;

namespace DynamicModules.Application.Dynamic.DashboardService.Queries.DrillCard;

public record DrillCardRequest(long DashboardId, string CardId, List<string> GroupKeys, ReportDateRange? FilterRange, string Locale) : IRequest<ServiceResult>;
