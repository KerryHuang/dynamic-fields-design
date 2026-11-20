using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.Extensions.Logging;

namespace DynamicModules.Application.Dynamic.LocaleService.Packs;

/// <summary>
/// 讀 <see cref="LocaleWorld"/>（i4d1 計畫判斷 25）：只在 handler 內（交易開始之後）呼叫，不快取結果。清單與覆寫層的規則和
/// <see cref="LocaleRegistry"/> 的快照相同（<see cref="LocaleRegistry.InfosFrom"/>、<see cref="LocaleRegistry.OverlaysFrom"/>）；
/// 三個語系政策每次現建一個 <see cref="PolicyReader"/>，選項來源換成這份交易內清單（scope 裡的 <see cref="IPolicyReader"/> 用快照濾值，
/// 另一台剛安裝的語系會被濾掉）。
/// </summary>
public sealed class LocaleWorldReader(ILocaleRepository locales, ILocalePackRepository packs, ISystemSettingRepository settings,
    IModulePolicyRepository overrides, PolicyEnvironmentDefaults env, ILogger<PolicyReader> logger)
{
    public async Task<LocaleWorld> ReadAsync(CancellationToken ct) => (await ReadWithCurrentAsync(ct)).World;

    /// <summary>
    /// 世界與各語系目前版的原始列出自同一次 <see cref="ILocalePackRepository.ListCurrentAsync"/>（i4d2 計畫判斷 6）：清單、預覽與樣板要同時看
    /// 濾過的覆寫層與原始 JSON（失效的鍵、原文雜湊），分兩次讀可能在並行匯入時讀到不同版本。<c>Current</c> 含未安裝語系殘留的列（不常見）。
    /// </summary>
    public async Task<(LocaleWorld World, IReadOnlyDictionary<string, LocalePack> Current)> ReadWithCurrentAsync(CancellationToken ct)
    {
        var installed = LocaleRegistry.InfosFrom(await locales.ListAsync(ct));
        var rows = await packs.ListCurrentAsync(ct);
        var overlays = LocaleRegistry.OverlaysFrom(rows, installed, logger);
        var reader = new PolicyReader(settings, overrides, env,
            new PolicyOptionSource(new FixedInstalledLocales(installed.Select(i => i.Code).ToArray())), logger);
        var world = new LocaleWorld(installed, overlays,
            await reader.ChoiceAsync(PolicyCatalog.LocaleDefault, null, ct),
            await reader.ListAsync(PolicyCatalog.LocaleEnabled, null, ct),
            await reader.ListAsync(PolicyCatalog.LocaleRequiredText, null, ct));
        return (world, rows.ToDictionary(p => p.Code, StringComparer.Ordinal));
    }
}

/// <summary>固定的已安裝語系代碼（給 <see cref="PolicyOptionSource"/> 正規化語系政策用）。</summary>
internal sealed record FixedInstalledLocales(IReadOnlyList<string> Codes) : IInstalledLocales;
