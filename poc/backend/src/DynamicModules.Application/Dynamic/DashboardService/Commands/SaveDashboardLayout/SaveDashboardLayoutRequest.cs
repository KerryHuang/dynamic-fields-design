using DynamicModules.Application.Common;
using DynamicModules.Domain.Reports;
using MediatR;

namespace DynamicModules.Application.Dynamic.DashboardService.Commands.SaveDashboardLayout;

/// <summary>自己的儀表板直接存（Name 給了就改名）；指派版則寫入觀看者的個人版，第一次存檔時另存，Name 不適用。</summary>
public record SaveDashboardLayoutRequest(long Id, string? Name, DashboardLayout Layout) : IRequest<ServiceResult>, ITransactionalRequest;
