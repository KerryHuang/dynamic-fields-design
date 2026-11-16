using DynamicModules.Application.Common;
using DynamicModules.Domain.Reports;
using MediatR;

namespace DynamicModules.Application.Dynamic.DashboardService.Queries.RunCard;

/// <summary>FilterRange 是儀表板上方暫時的日期篩選（不存檔）。</summary>
public record RunCardRequest(long DashboardId, string CardId, ReportDateRange? FilterRange, string Locale) : IRequest<ServiceResult>;
