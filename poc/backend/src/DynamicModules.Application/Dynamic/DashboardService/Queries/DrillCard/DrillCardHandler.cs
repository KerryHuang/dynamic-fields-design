using DynamicModules.Application.Common;
using MediatR;

namespace DynamicModules.Application.Dynamic.DashboardService.Queries.DrillCard;

public class DrillCardHandler(DashboardCardRunner runner) : IRequestHandler<DrillCardRequest, ServiceResult>
{
    public Task<ServiceResult> Handle(DrillCardRequest request, CancellationToken ct) =>
        runner.RunAsync(request.DashboardId, request.CardId, request.FilterRange, request.GroupKeys, request.Locale, ct);
}
