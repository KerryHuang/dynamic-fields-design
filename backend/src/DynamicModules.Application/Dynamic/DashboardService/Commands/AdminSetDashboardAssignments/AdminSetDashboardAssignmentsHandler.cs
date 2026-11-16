using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.DashboardService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.DashboardService.Commands.AdminSetDashboardAssignments;

/// <summary>整組取代指派。不碰版本號，也不動個人版：取消指派後個人版保留，重新指派時恢復（spec §8）。</summary>
public class AdminSetDashboardAssignmentsHandler(IDashboardRepository dashboards, IAccountRepository accounts, IUnitOfWork uow)
    : IRequestHandler<AdminSetDashboardAssignmentsRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(AdminSetDashboardAssignmentsRequest request, CancellationToken ct)
    {
        if (await dashboards.GetAsync(request.Id, ct) is not { OwnerUserId: null } d) return ServiceResult.NotFound(DashboardRules.NotFound);
        var groupIds = request.GroupIds.Distinct().ToList();
        if (!await accounts.GroupsExistAsync(groupIds, ct))
            return ServiceResult.Invalid(new Dictionary<string, string[]> { ["groupIds"] = ["群組不存在"] });

        // 只動差異：同一個 key 先刪再加會讓追蹤中的實體衝突
        d.Assignments.RemoveAll(a => !groupIds.Contains(a.GroupId));
        foreach (var id in groupIds.Where(id => d.Assignments.All(a => a.GroupId != id)))
            d.Assignments.Add(new DashboardAssignment { DashboardId = d.Id, GroupId = id });
        await uow.CommitAsync(ct);
        return ServiceResult.Success(d.ToAdminDetail());
    }
}
