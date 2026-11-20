using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.LocaleService.Packs;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.LocaleService.Queries.GetLocales;

public sealed class GetLocalesHandler(LocaleWorldReader worlds, ILocalePackRepository packs, IPolicyReader policies, LocalePackSettings settings)
    : IRequestHandler<GetLocalesRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetLocalesRequest request, CancellationToken ct)
    {
        // 世界與目前版原始列出自同一次讀取（i4d2 計畫判斷 6）；PreviousAsync 是對記憶體清單逐一 await，不是列舉查詢時再查
        var (world, current) = await worlds.ReadWithCurrentAsync(ct);
        var items = new List<LocaleAdminItem>();
        foreach (var info in world.Installed)
        {
            var overlay = world.Overlays.GetValueOrDefault(info.Code);
            var pack = current.GetValueOrDefault(info.Code);
            var hasPrevious = pack is not null && await packs.PreviousAsync(info.Code, ct) is not null;
            items.Add(new LocaleAdminItem(info.Code, info.NativeName, info.Builtin ? "builtin" : "installed", info.FallbackCode,
                world.Enabled.Contains(info.Code), world.DefaultLocale == info.Code, world.Required.Contains(info.Code),
                new LocaleOverrideCounts(overlay?.Ui.Count ?? 0, overlay?.Server.Count ?? 0),
                pack is null ? null : new LocalePackSummary(pack.PackVersion, pack.FileName, pack.ImportedUtc, pack.ImportedUserId),
                hasPrevious, LocaleCompleteness.Of(info.Code, overlay), LocaleOverlayReview.Of(pack), pack?.BuiltFor));
        }
        return ServiceResult.Success(new LocaleAdminList(settings.ImportEnabled,
            await policies.IntAsync(PolicyCatalog.LocalePackMaxBytes, null, ct), await policies.IntAsync(PolicyCatalog.LocaleMaxInstalled, null, ct), items));
    }
}
