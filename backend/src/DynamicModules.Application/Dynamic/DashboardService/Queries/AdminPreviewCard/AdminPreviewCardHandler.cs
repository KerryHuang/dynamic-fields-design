using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.DashboardService.Data;
using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Domain.Reports;
using MediatR;

namespace DynamicModules.Application.Dynamic.DashboardService.Queries.AdminPreviewCard;

public class AdminPreviewCardHandler(DashboardCardRunner runner) : IRequestHandler<AdminPreviewCardRequest, ServiceResult>
{
    public Task<ServiceResult> Handle(AdminPreviewCardRequest request, CancellationToken ct)
    {
        var errors = DashboardRules.CheckLayout(new DashboardLayout([request.Card]));
        return errors.Count > 0
            ? Task.FromResult(ServiceResult.Invalid(ReportRules.ToErrors("card", errors)))
            : runner.PreviewAsync(request.Card, request.FilterRange, request.Locale, ct);
    }
}
