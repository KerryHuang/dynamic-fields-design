using DynamicModules.Application.Common;
using MediatR;

namespace DynamicModules.Application.Dynamic.DashboardService.Queries.RunCard;

public class RunCardHandler(DashboardCardRunner runner) : IRequestHandler<RunCardRequest, ServiceResult>
{
    public Task<ServiceResult> Handle(RunCardRequest request, CancellationToken ct) =>
        runner.RunAsync(request.DashboardId, request.CardId, request.FilterRange, null, request.Locale, ct);
}
