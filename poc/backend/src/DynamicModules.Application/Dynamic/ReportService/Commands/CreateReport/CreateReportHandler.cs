using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Reports;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.ReportService.Commands.CreateReport;

public class CreateReportHandler(
    IReportRepository reports, IUnitOfWork uow, ICurrentUser user, IBusinessTime time, ReportAccess access,
    ReportDefinitionValidator validator) : IRequestHandler<CreateReportRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(CreateReportRequest request, CancellationToken ct)
    {
        var (definition, failure) = await validator.ValidateAsync(request.Name, request.Definition, ct);
        if (definition is null) return failure!;

        var report = new Report
        {
            OwnerUserId = user.UserId, Name = request.Name.Trim(), DefinitionJson = ReportDefinition.Serialize(definition),
            RowVersion = Guid.NewGuid().ToString()
        };
        report.StampCreated(user.UserId, time.Now);
        reports.Add(report);
        await uow.CommitAsync(ct);
        return ServiceResult.Created(report.ToDetail(await access.ViewerAsync(ct), await access.UserNamesAsync(ct)));
    }
}
