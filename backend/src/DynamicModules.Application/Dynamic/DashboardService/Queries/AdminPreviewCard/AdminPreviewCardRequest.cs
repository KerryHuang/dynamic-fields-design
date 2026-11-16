using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Domain.Reports;
using MediatR;

namespace DynamicModules.Application.Dynamic.DashboardService.Queries.AdminPreviewCard;

/// <summary>指派版編輯時預覽一張（可能還沒存檔的）卡片；以管理員自己的權限計算。</summary>
public record AdminPreviewCardRequest(DashboardCard Card, ReportDateRange? FilterRange, string Locale) : IRequest<ServiceResult>, ISystemAdminRequest;
