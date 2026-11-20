using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.LocaleService.Packs;
using MediatR;

namespace DynamicModules.Application.Dynamic.LocaleService.Commands.RollbackLocalePack;

/// <summary>沒有上一版 409；上一版內容的複本成為新的目前版（新 Id），原本的目前版成為上一版，可以再切回來。</summary>
public sealed class RollbackLocalePackHandler(LocaleVersionSwitch versions) : IRequestHandler<RollbackLocalePackRequest, ServiceResult>
{
    public Task<ServiceResult> Handle(RollbackLocalePackRequest request, CancellationToken ct) =>
        versions.SwitchAsync(request.Code, (_, previous, _, now) => previous is null ? null : LocalePackVersions.CopyOf(previous, now),
            ServiceResult.Conflict(LocalePackMessages.NoPrevious, LocalePackMessages.CodeNoPrevious), ct);
}
