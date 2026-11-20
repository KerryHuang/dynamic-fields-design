using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.AccountService.Data;
using DynamicModules.Application.Common;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.AccountService.Queries.GetGroupImpact;

public class GetGroupImpactHandler(IAccountRepository accounts, IPermissionRepository permissions, DynamicModules.Application.Common.RequestLocale requestLocale) : IRequestHandler<GetGroupImpactRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetGroupImpactRequest request, CancellationToken ct)
    {
        if (await accounts.GetGroupTrackedAsync(request.GroupId, ct) is null) return ServiceResult.NotFound("群組不存在");
        var i = await accounts.GetGroupImpactAsync(request.GroupId, requestLocale.Current, ct);
        var system = SystemPrivilegeRules.Direct((await permissions.LoadSystemPrivilegeRowsAsync(ct)).Groups.Where(r => r.GroupId == request.GroupId));
        return ServiceResult.Success(new GroupImpactResponse(
            i.MemberCount, i.Modules, i.FieldPermissionCount, i.DataScopeCount, i.BlockingOptions, i.Reports, i.Dashboards, system));
    }
}
