using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.LocaleService.Packs;
using DynamicModules.Application.Dynamic.OpsService;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Localization;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;
using System.Text.Json;

namespace DynamicModules.Application.Dynamic.LocaleService.Commands.PreviewLocalePack;

/// <summary>
/// 交易內：A38 大小 → 解析 → <see cref="LocaleWorldReader"/>（不讀快照；世界與目前版原始列同一次讀取，i4d2 判斷 6）→ 該語系的內容指紋與目前版 Id
/// → 分析（帶平台版本與目前版的覆寫層檢視，判斷 2、7）→ 會寫新版本時補「這次會清掉的已失效覆寫」（判斷 8）；匯入開放且有計畫時
/// 清掉過期與超量的暫存、新增一筆並 <see cref="IUnitOfWork.CommitAsync"/>（<c>TransactionBehavior</c> 只提交交易、不存檔）。
/// </summary>
public sealed class PreviewLocalePackHandler(
    LocaleWorldReader worlds, ILocaleRepository locales, ILocalePackRepository packs, IPolicyReader policies, IUnitOfWork uow,
    TimeProvider time, LocalePackSettings settings, ICurrentUser current, IOpsEnvironment ops) : IRequestHandler<PreviewLocalePackRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(PreviewLocalePackRequest request, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var maxBytes = await policies.IntAsync(PolicyCatalog.LocalePackMaxBytes, null, ct);
        if (request.Content.Length > maxBytes) return Invalid(LocalePackMessages.TooLarge(maxBytes));
        var (pack, parseError) = LocalePackParser.Parse(request.Content.AsMemory());
        if (pack is null) return Invalid(parseError!);

        var maxInstalled = await policies.IntAsync(PolicyCatalog.LocaleMaxInstalled, null, ct);
        var (world, currents) = await worlds.ReadWithCurrentAsync(ct);
        var row = await locales.FindAsync(pack.Code, ct);
        var currentPack = currents.GetValueOrDefault(pack.Code);
        var review = LocaleOverlayReview.Of(currentPack);
        var analysis = LocalePackAnalyzer.Analyze(pack, world, maxInstalled, ops.Version, review);
        if (analysis.Error is not null) return Invalid(analysis.Error);

        var fileName = LocalePackStorage.SafeFileName(request.FileName);
        var fileHash = LocalePackStorage.FileHash(request.Content);
        var report = analysis.Report! with
        {
            FileName = fileName, FileHash = fileHash, ImportEnabled = settings.ImportEnabled,
            OrphanedDropped = analysis.Plan!.OverlayChanged ? new OrphanedDroppedReport(review.Ui.Orphaned, review.Server.Orphaned) : new OrphanedDroppedReport(0, 0),
        };
        if (!settings.ImportEnabled) return ServiceResult.Success(report);

        await packs.PrunePreviewsAsync(now, LocalePackLimits.MaxPreviews - 1, ct);
        var preview = new LocalePackPreview
        {
            Id = Guid.NewGuid().ToString("N"), UserId = current.UserId, Code = pack.Code, FileHash = fileHash, FileName = fileName,
            LocaleFingerprint = row is null ? null : LocalePackStorage.Fingerprint(row), CurrentPackId = currentPack?.Id,
            CreatedUtc = now, ExpiresUtc = now + LocalePackLimits.PreviewTtl,
        };
        report = report with { PreviewId = preview.Id, ExpiresUtc = preview.ExpiresUtc };
        preview.PlanJson = JsonSerializer.Serialize(analysis.Plan, LocalePackStorage.Json);
        preview.ReportJson = JsonSerializer.Serialize(report, LocalePackStorage.Json);
        packs.AddPreview(preview);
        await uow.CommitAsync(ct);
        return ServiceResult.Success(report);
    }

    private static ServiceResult Invalid(MessageRef message) =>
        ServiceResult.Invalid(LocalePackMessages.FileField, message, LocalePackMessages.CodeInvalid);
}
