using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Domain.Reports;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.ReportService.Commands.UpdateReport;

public class UpdateReportHandler(
    IUnitOfWork uow, IBusinessTime time, ReportAccess access, ReportDefinitionValidator validator)
    : IRequestHandler<UpdateReportRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(UpdateReportRequest request, CancellationToken ct)
    {
        var (report, viewer) = await access.FindAsync(request.Id, ct);
        if (report is null) return ServiceResult.NotFound(ReportRules.ReportNotFound);
        if (report.OwnerUserId != viewer.UserId) return ServiceResult.Forbidden();

        var (definition, failure) = await validator.ValidateAsync(request.Name, request.Definition, ct);
        if (definition is null) return failure!;

        report.Name = request.Name.Trim();
        report.DefinitionJson = ReportDefinition.Serialize(definition);
        report.RowVersion = Guid.NewGuid().ToString();
        report.StampModified(viewer.UserId, time.Now);
        await uow.CommitAsync(ct);
        return ServiceResult.Success(report.ToDetail(viewer, await access.UserNamesAsync(ct)));
    }
}
