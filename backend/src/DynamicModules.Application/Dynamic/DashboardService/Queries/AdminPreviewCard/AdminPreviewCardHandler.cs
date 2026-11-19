using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.DashboardService.Data;
using DynamicModules.Application.Dynamic.ReportService;
using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Domain.Reports;
using MediatR;

namespace DynamicModules.Application.Dynamic.DashboardService.Queries.AdminPreviewCard;

public class AdminPreviewCardHandler(DashboardCardRunner runner, ReportLimitReader limitReader) : IRequestHandler<AdminPreviewCardRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(AdminPreviewCardRequest request, CancellationToken ct)
    {
        var errors = DashboardRules.CheckLayout(new DashboardLayout([request.Card]), await limitReader.GetAsync(ct), await limitReader.MaxCardsAsync(ct));
        return errors.Count > 0
            ? ServiceResult.Invalid(ReportRules.ToErrors("card", errors))
            : await runner.PreviewAsync(request.Card, request.FilterRange, request.Locale, ct);
    }
}
