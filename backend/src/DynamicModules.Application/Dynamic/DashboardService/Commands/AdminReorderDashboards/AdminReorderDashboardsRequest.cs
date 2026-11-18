using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.DashboardService.Commands.AdminReorderDashboards;

public record AdminReorderDashboardsRequest(List<long> Ids) : IRequest<ServiceResult>, ITransactionalRequest, ISystemAdminRequest, IAuditedRequest
{
    public AuditAction Audit => AuditAction.For("dashboard.reorder", AuditTargets.Dashboard, null);
}
