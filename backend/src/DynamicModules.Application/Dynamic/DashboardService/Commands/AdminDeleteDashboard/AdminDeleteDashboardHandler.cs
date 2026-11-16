using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.DashboardService.Data;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.DashboardService.Commands.AdminDeleteDashboard;

/// <summary>刪除指派版，連同指派與所有人的個人版。</summary>
public class AdminDeleteDashboardHandler(IDashboardRepository dashboards, IUnitOfWork uow) : IRequestHandler<AdminDeleteDashboardRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(AdminDeleteDashboardRequest request, CancellationToken ct)
    {
        if (await dashboards.GetAsync(request.Id, ct) is not { OwnerUserId: null } d) return ServiceResult.NotFound(DashboardRules.NotFound);
        await dashboards.RemoveAsync(d, ct);
        await uow.CommitAsync(ct);
        return ServiceResult.Success();
    }
}
