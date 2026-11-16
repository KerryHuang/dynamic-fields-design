using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.AccountService.Data;
using DynamicModules.Application.Dynamic.DemoService;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.AccountService.Queries.GetAuthOptions;

public class GetAuthOptionsHandler(AuthSettings settings, IAccountRepository accounts, IDemoDataRunner demo)
    : IRequestHandler<GetAuthOptionsRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetAuthOptionsRequest request, CancellationToken ct)
    {
        if (!settings.DemoLoginEnabled) return ServiceResult.Success(new AuthOptionsResponse(false, false, []));

        var list = await accounts.ListDemoLoginAccountsAsync(ct);
        return ServiceResult.Success(new AuthOptionsResponse(
            true, demo.Current.State == DemoRunState.Running,
            [.. list.Select(a => new DemoAccountResponse(a.UserId, a.DisplayName))]));
    }
}
