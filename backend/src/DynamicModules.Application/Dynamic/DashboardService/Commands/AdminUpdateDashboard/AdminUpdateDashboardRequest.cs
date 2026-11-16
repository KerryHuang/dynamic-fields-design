using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Domain.Reports;
using MediatR;

namespace DynamicModules.Application.Dynamic.DashboardService.Commands.AdminUpdateDashboard;

/// <summary>Version 是管理員載入時的版本號，與目前不同表示有人先改過，回 409。</summary>
public record AdminUpdateDashboardRequest(long Id, string Name, DashboardLayout Layout, int Version)
    : IRequest<ServiceResult>, ITransactionalRequest, ISystemAdminRequest;
