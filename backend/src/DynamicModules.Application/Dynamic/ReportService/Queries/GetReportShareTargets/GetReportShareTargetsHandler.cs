using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.ReportService.Queries.GetReportShareTargets;

/// <summary>
/// 分享對話框要勾的群組：只回 id 與名稱，不回代碼與成員。任何登入者可讀（要分享就得知道群組名稱），
/// 不放寬設計器專用的群組清單。
/// </summary>
public class GetReportShareTargetsHandler(IPermissionRepository permissions) : IRequestHandler<GetReportShareTargetsRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetReportShareTargetsRequest request, CancellationToken ct) =>
        ServiceResult.Success((await permissions.ListGroupsAsync(ct)).Select(g => new ReportShareTarget(g.Id, g.NameI18n)).ToList());
}
