using DynamicModules.Application.Common;
using DynamicModules.Domain.Reports;
using MediatR;

namespace DynamicModules.Application.Dynamic.DashboardService.Commands.SaveDashboardLayout;

/// <summary>
/// 自己的儀表板直接存（Name 給了就改名）；指派版則寫入觀看者的個人版，第一次存檔時另存，Name 不適用。
/// Version 是使用者載入畫面時看到的指派版版本：第一次另存個人版時當基準版本（沒帶用目前版本），之後存檔與自己的儀表板都不看它。
/// </summary>
public record SaveDashboardLayoutRequest(long Id, string? Name, DashboardLayout Layout, int? Version = null) : IRequest<ServiceResult>, ITransactionalRequest;
