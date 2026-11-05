using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.ReportService.Commands.CopyReport;

/// <summary>被分享者要改就另存一份：定義照抄，擁有者換成自己，不帶分享。</summary>
public class CopyReportHandler(IReportRepository reports, IUnitOfWork uow, IBusinessTime time, ReportAccess access)
    : IRequestHandler<CopyReportRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(CopyReportRequest request, CancellationToken ct)
    {
        var (source, viewer) = await access.FindAsync(request.Id, ct);
        if (source is null) return ServiceResult.NotFound(ReportRules.ReportNotFound);

        var copy = new Report { OwnerUserId = viewer.UserId, Name = source.Name, DefinitionJson = source.DefinitionJson, RowVersion = Guid.NewGuid().ToString() };
        copy.StampCreated(viewer.UserId, time.Now);
        reports.Add(copy);
        await uow.CommitAsync(ct);
        return ServiceResult.Created(copy.ToDetail(viewer, await access.UserNamesAsync(ct)));
    }
}
