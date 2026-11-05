using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Reports;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.ReportService.Commands.CopyReport;

/// <summary>被分享者要改就另存一份：定義照抄，擁有者換成自己，不帶分享。</summary>
public class CopyReportHandler(IReportRepository reports, IUnitOfWork uow, IBusinessTime time, ReportAccess access, ReportDefinitionValidator validator)
    : IRequestHandler<CopyReportRequest, ServiceResult>
{
    private const string UnseeableMessage = "這份報表用到你看不到的欄位或已不存在的設定，無法另存";

    public async Task<ServiceResult> Handle(CopyReportRequest request, CancellationToken ct)
    {
        var (source, viewer) = await access.FindAsync(request.Id, ct);
        if (source is null) return ServiceResult.NotFound(ReportRules.ReportNotFound);

        // 另存要求看得到定義裡的一切：看不全就拒絕，不剝除條件另存；訊息不點名欄位
        var (valid, failure) = await validator.ValidateAsync(null, ReportDefinition.Parse(source.DefinitionJson), ct);
        if (valid is null)
            return failure is { IsForbidden: true } ? failure : ServiceResult.Invalid(ReportRules.ToErrors("definition", [UnseeableMessage]));

        var copy = new Report { OwnerUserId = viewer.UserId, Name = source.Name, DefinitionJson = source.DefinitionJson, RowVersion = Guid.NewGuid().ToString() };
        copy.StampCreated(viewer.UserId, time.Now);
        reports.Add(copy);
        await uow.CommitAsync(ct);
        return ServiceResult.Created(await access.DetailAsync(copy, viewer, ct));
    }
}
