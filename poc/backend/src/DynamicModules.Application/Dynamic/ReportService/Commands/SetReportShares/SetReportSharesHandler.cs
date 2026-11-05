using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.ReportService.Commands.SetReportShares;

public class SetReportSharesHandler(IAccountRepository accounts, IUnitOfWork uow, IBusinessTime time, ReportAccess access)
    : IRequestHandler<SetReportSharesRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(SetReportSharesRequest request, CancellationToken ct)
    {
        var (report, viewer) = await access.FindAsync(request.Id, ct);
        if (report is null) return ServiceResult.NotFound(ReportRules.ReportNotFound);
        if (report.OwnerUserId != viewer.UserId) return ServiceResult.Forbidden();

        var groupIds = request.GroupIds.Distinct().ToList();
        if (!await accounts.GroupsExistAsync(groupIds, ct))
            return ServiceResult.Invalid(new Dictionary<string, string[]> { ["groupIds"] = ["群組不存在"] });

        // 只動差異：同一個 key 先刪再加會讓追蹤中的實體衝突
        report.Shares.RemoveAll(s => !groupIds.Contains(s.GroupId));
        foreach (var id in groupIds.Where(id => report.Shares.All(s => s.GroupId != id)))
            report.Shares.Add(new ReportShare { ReportId = report.Id, GroupId = id });
        report.StampModified(viewer.UserId, time.Now);
        await uow.CommitAsync(ct);
        return ServiceResult.Success(report.ToDetail(viewer, await access.UserNamesAsync(ct)));
    }
}
