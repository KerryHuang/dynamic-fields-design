using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.Extensions.Logging;

namespace DynamicModules.Application.Dynamic.LocaleService.Packs;

/// <summary>
/// 回到上一版與移除覆寫共用的整版切換（i4d1 計畫判斷 20）：交易內讀 <see cref="LocaleWorld"/>，模擬切換後的是／否撞字，
/// 切換後多出撞字就 409（Q12），否則以 <see cref="ILocalePackRepository.AddVersion"/> 新增一列（新的 Id）並提交；語系列不動。
/// </summary>
public sealed class LocaleVersionSwitch(
    LocaleWorldReader worlds, ILocaleRepository locales, ILocalePackRepository packs, IUnitOfWork uow, TimeProvider time,
    AuditContext audit, AfterCommitActions afterCommit, LocaleRegistry registry, LocalePackSettings settings,
    ILogger<LocaleVersionSwitch> logger)
{
    /// <summary><paramref name="next"/> 依目前版與上一版產生新列；回 null＝不能切換（<paramref name="refusal"/> 給 409）。</summary>
    public async Task<ServiceResult> SwitchAsync(string code, Func<LocalePack?, LocalePack?, LocaleOverlay?, DateTime, LocalePack?> next,
        ServiceResult refusal, CancellationToken ct)
    {
        if (!settings.ImportEnabled) return ServiceResult.Forbidden(LocalePackMessages.ImportDisabled, LocalePackMessages.CodeImportDisabled);
        if (await locales.FindAsync(code, ct) is null) return ServiceResult.NotFound(LocalePackMessages.NotFound, LocalePackMessages.CodeNotFound);
        var now = time.GetUtcNow().UtcDateTime;
        var world = await worlds.ReadAsync(ct);
        var currentPack = await packs.CurrentAsync(code, ct);
        var previous = await packs.PreviousAsync(code, ct);
        if (next(currentPack, previous, world.Overlays.GetValueOrDefault(code), now) is not { } row) return refusal;

        var overlays = new Dictionary<string, LocaleOverlay>(world.Overlays, StringComparer.Ordinal);
        overlays.Remove(code);
        foreach (var (k, o) in LocaleRegistry.OverlaysFrom([row], world.Installed, logger)) overlays[k] = o;
        if (NewCollision(world, world with { Overlays = overlays }, code) is { } collision)
            return ServiceResult.Conflict(collision, LocalePackMessages.CodeBooleanCollision);

        packs.AddVersion(row, currentPack, previous, now);
        await uow.CommitAsync(ct);
        audit.Target(code, world.Installed.First(i => i.Code == code).NativeName);
        audit.Detail("locale", code);
        audit.Detail("fromPackId", currentPack?.Id);
        audit.Detail("packId", row.Id);
        afterCommit.Add("locale-registry", registry.Invalidate);
        return ServiceResult.Success(new LocaleVersionResponse(code, row.Id));
    }

    /// <summary>
    /// 切換後才出現的撞字（以字比較，不分大小寫，同 <c>BooleanWords</c>）：訊息的第一個語系是被切換的語系（它在撞字的哪一邊就用哪一邊），
    /// 第二個是另一邊的第一個語系；被切換的語系不在撞字裡（例如沿退回鏈繼承它的下游語系撞了）就列兩邊各自的第一個。
    /// </summary>
    internal static MessageRef? NewCollision(LocaleWorld before, LocaleWorld after, string code)
    {
        var old = LocalePackAnalyzer.BooleanCollisions(before, null).Collisions.Select(c => c.Word).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hit = LocalePackAnalyzer.BooleanCollisions(after, null).Collisions.FirstOrDefault(c => !old.Contains(c.Word));
        if (hit is null) return null;
        var (mine, others) = hit.NoLocales.Contains(code) ? (code, hit.YesLocales)
            : hit.YesLocales.Contains(code) ? (code, hit.NoLocales)
            : (hit.NoLocales.FirstOrDefault() ?? code, hit.YesLocales);
        var other = others.FirstOrDefault(o => o != mine) ?? others.FirstOrDefault() ?? "";
        string Name(string c) => after.Installed.FirstOrDefault(i => i.Code == c)?.NativeName ?? c;
        return LocalePackMessages.BooleanCollision(Name(mine), Name(other), hit.Word);
    }
}

/// <summary>回到上一版、移除覆寫的回應：<c>{ code, packId }</c>（切換後的目前版 Id）。</summary>
public sealed record LocaleVersionResponse(string Code, long PackId);
