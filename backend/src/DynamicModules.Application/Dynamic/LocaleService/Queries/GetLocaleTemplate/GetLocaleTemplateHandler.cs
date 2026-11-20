using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.LocaleService.Packs;
using DynamicModules.Application.Dynamic.OpsService;
using MediatR;

namespace DynamicModules.Application.Dynamic.LocaleService.Queries.GetLocaleTemplate;

/// <summary>
/// 讀資料庫（和語系清單同一個來源；世界與目前版原始列同一次讀取，i4d2 計畫判斷 6）→ <see cref="LocaleTemplateBuilder.Validate"/>（不過 → 400
/// <c>localePackInvalid</c>，訊息在 <c>errors.file</c>）→ <see cref="LocaleTemplateBuilder.Build"/>。
/// </summary>
public sealed class GetLocaleTemplateHandler(LocaleWorldReader worlds, IOpsEnvironment ops) : IRequestHandler<GetLocaleTemplateRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetLocaleTemplateRequest request, CancellationToken ct)
    {
        var (world, current) = await worlds.ReadWithCurrentAsync(ct);
        if (LocaleTemplateBuilder.Validate(world, request.Options) is { } error)
            return ServiceResult.Invalid(LocalePackMessages.FileField, error, LocalePackMessages.CodeInvalid);
        var (fileName, content) = LocaleTemplateBuilder.Build(world, current, request.Options, ops.Version);
        return ServiceResult.Success(new LocaleTemplateFile(fileName, content));
    }
}
