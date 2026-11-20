using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Localization;
using MediatR;

namespace DynamicModules.Application.Dynamic.LocaleService.Queries.GetUiMessages;

/// <summary>
/// 代碼格式不合法、沒安裝、匿名取未啟用一律 404（不帶句子，不讓匿名者探出裝了哪些語系）；
/// 補齊用目前的預設語系（<see cref="LocaleRegistry.Ui"/>）。
/// </summary>
public class GetUiMessagesHandler(LocaleRegistry registry, CachedSystemPolicy policies)
    : IRequestHandler<GetUiMessagesRequest, ServiceResult>
{
    public Task<ServiceResult> Handle(GetUiMessagesRequest request, CancellationToken ct)
    {
        var code = request.Code;
        if (!LocaleCode.IsValid(code) || registry.Find(code) is null) return Task.FromResult(ServiceResult.NotFound(null));
        var enabled = policies.EnabledLocales.Contains(code);
        if (!enabled && !request.SignedIn) return Task.FromResult(ServiceResult.NotFound(null));
        var bundle = registry.Ui(code, policies.DefaultLocale);
        return Task.FromResult(bundle is null
            ? ServiceResult.NotFound(null)
            : ServiceResult.Success(new UiMessagesResult(bundle.Hash, bundle.Json, enabled)));
    }
}
