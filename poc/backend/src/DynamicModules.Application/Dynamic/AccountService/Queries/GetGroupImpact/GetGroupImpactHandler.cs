using DynamicModules.Application.Common;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.AccountService.Queries.GetGroupImpact;

public class GetGroupImpactHandler(IAccountRepository accounts) : IRequestHandler<GetGroupImpactRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetGroupImpactRequest request, CancellationToken ct) =>
        await accounts.GetGroupTrackedAsync(request.GroupId, ct) is null
            ? ServiceResult.NotFound("群組不存在")
            : ServiceResult.Success(await accounts.GetGroupImpactAsync(request.GroupId, ct));
}
