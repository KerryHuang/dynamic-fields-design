using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.AccountService.Data;
using DynamicModules.Application.Dynamic.DemoService;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.AccountService.Queries.GetAuthOptions;

public class GetAuthOptionsHandler(
    AuthSettings settings, IAccountRepository accounts, IDemoDataRunner demo, CachedSystemPolicy policies, LocaleRegistry registry)
    : IRequestHandler<GetAuthOptionsRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetAuthOptionsRequest request, CancellationToken ct)
    {
        var enabled = policies.EnabledLocales;
        var defaultLocale = policies.DefaultLocale;
        // 登入前只給啟用語系，順序依已安裝語系
        var locales = registry.Locales.Where(l => enabled.Contains(l.Code))
            .Select(l => LocaleInfoResponse.Of(l, registry.UiHash(l.Code, defaultLocale))).ToList();
        if (!settings.DemoLoginEnabled)
            return ServiceResult.Success(new AuthOptionsResponse(false, false, [], defaultLocale, enabled, locales));

        var list = await accounts.ListDemoLoginAccountsAsync(ct);
        return ServiceResult.Success(new AuthOptionsResponse(
            true, demo.Current.State == DemoRunState.Running,
            [.. list.Select(a => new DemoAccountResponse(a.UserId, a.DisplayName))], defaultLocale, enabled, locales));
    }
}
