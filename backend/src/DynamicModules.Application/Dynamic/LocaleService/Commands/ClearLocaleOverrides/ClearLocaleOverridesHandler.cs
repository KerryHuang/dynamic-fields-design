using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.LocaleService.Packs;
using MediatR;

namespace DynamicModules.Application.Dynamic.LocaleService.Commands.ClearLocaleOverrides;

/// <summary>目前版本來就是空的（或沒有任何版本）409；否則加一個空版本（被覆寫遮住的內建字會重新露出來，所以照樣模擬撞字）。</summary>
public sealed class ClearLocaleOverridesHandler(LocaleVersionSwitch versions, ICurrentUser current) : IRequestHandler<ClearLocaleOverridesRequest, ServiceResult>
{
    public Task<ServiceResult> Handle(ClearLocaleOverridesRequest request, CancellationToken ct) =>
        versions.SwitchAsync(request.Code, (_, _, overlay, now) =>
                overlay is null || (overlay.Ui.Count == 0 && overlay.Server.Count == 0) ? null : LocalePackVersions.Empty(request.Code, current.UserId, now),
            ServiceResult.Conflict(LocalePackMessages.NoOverrides, LocalePackMessages.CodeNoOverrides), ct);
}
