using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.DashboardService.Data;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.DashboardService.Commands.AdminReorderDashboards;

/// <summary>依傳入順序重編 Sequence；Ids 要涵蓋全部指派版，缺一或多一都回 400，避免兩個管理員各排一半造成順序交錯。</summary>
public class AdminReorderDashboardsHandler(IDashboardRepository dashboards, IUnitOfWork uow) : IRequestHandler<AdminReorderDashboardsRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(AdminReorderDashboardsRequest request, CancellationToken ct)
    {
        var all = (await dashboards.ListAssignedAsync(ct)).Select(d => d.Id).ToHashSet();
        if (request.Ids.Distinct().Count() != request.Ids.Count || !all.SetEquals(request.Ids))
            return ServiceResult.Invalid(new Dictionary<string, string[]> { ["ids"] = ["順序必須包含全部指派版各一次"] });
        for (var i = 0; i < request.Ids.Count; i++)
            (await dashboards.GetAsync(request.Ids[i], ct))!.Sequence = i + 1;
        await uow.CommitAsync(ct);
        return ServiceResult.Success();
    }
}
