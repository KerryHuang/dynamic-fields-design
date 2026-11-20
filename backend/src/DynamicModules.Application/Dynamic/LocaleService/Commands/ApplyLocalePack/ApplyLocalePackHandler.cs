using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.LocaleService.Packs;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.LocaleService.Commands.ApplyLocalePack;

/// <summary>
/// 交易內（i4d1 計畫判斷 16）：取預覽（不存在、過期、不是自己的 → 404）→ 檔案雜湊 → 該語系的內容指紋與目前版 Id（預覽後被改 → 409）→
/// 用交易內最新的 <see cref="LocaleWorld"/> 重驗上層語系圖、A39（→ 400）與是／否撞字（結果和預覽不同 → 409，Q11）→ 寫語系列 →
/// 目前版覆寫層＋計畫的句子組新版本（覆寫層沒變就不加版本）→ 刪預覽 → <see cref="IUnitOfWork.CommitAsync"/> → 稽核補充；
/// 快照的失效登記在 <see cref="AfterCommitActions"/>，回滾就不執行。句子不重驗：計畫是預覽時驗過的，該語系的基底由第 2 步保證沒變。
/// </summary>
public sealed class ApplyLocalePackHandler(
    LocaleWorldReader worlds, ILocaleRepository locales, ILocalePackRepository packs, IPolicyReader policies, IUnitOfWork uow,
    TimeProvider time, ICurrentUser current, AuditContext audit, AfterCommitActions afterCommit, LocaleRegistry registry,
    LocalePackSettings settings) : IRequestHandler<ApplyLocalePackRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(ApplyLocalePackRequest request, CancellationToken ct)
    {
        if (!settings.ImportEnabled) return ServiceResult.Forbidden(LocalePackMessages.ImportDisabled, LocalePackMessages.CodeImportDisabled);
        var now = time.GetUtcNow().UtcDateTime;
        var preview = await packs.FindPreviewAsync(request.PreviewId ?? "", ct);
        if (preview is null || preview.ExpiresUtc <= now || preview.UserId != current.UserId)
            return ServiceResult.NotFound(LocalePackMessages.PreviewExpired, LocalePackMessages.CodePreviewExpired);
        if (preview.FileHash != request.FileHash) return Stale();

        var row = await locales.FindAsync(preview.Code, ct);
        var currentPack = await packs.CurrentAsync(preview.Code, ct);
        if ((row is null ? null : LocalePackStorage.Fingerprint(row)) != preview.LocaleFingerprint || currentPack?.Id != preview.CurrentPackId)
            return Stale();

        var plan = JsonSerializer.Deserialize<LocalePackPlan>(preview.PlanJson, LocalePackStorage.Json)!;
        var report = JsonSerializer.Deserialize<LocalePackReport>(preview.ReportJson, LocalePackStorage.Json)!;
        var world = await worlds.ReadAsync(ct);
        var recheck = LocalePackAnalyzer.Recheck(plan, world, await policies.IntAsync(PolicyCatalog.LocaleMaxInstalled, null, ct));
        if (recheck.Invalid is not null)
            return ServiceResult.Invalid(LocalePackMessages.FileField, recheck.Invalid, LocalePackMessages.CodeInvalid);
        if (recheck.BooleanChanged) return Stale();

        // 語系列：新安裝，或既有的匯入語系有欄位變動（內建語系的計畫不會帶 Locale，Q3）
        if (plan.Locale is { } l)
        {
            if (row is null)
                locales.Add(new InstalledLocale
                {
                    Code = plan.Code, NativeName = l.NativeName, QuasarLang = l.QuasarLang, FallbackCode = l.FallbackCode, Origin = LocaleOrigin.Installed,
                    CreatedUserId = current.UserId, CreatedUtc = now, ModifiedUserId = current.UserId, ModifiedUtc = now,
                });
            else if (row.Origin == LocaleOrigin.Installed)
            {
                (row.NativeName, row.QuasarLang, row.FallbackCode) = (l.NativeName, l.QuasarLang, l.FallbackCode);
                row.ModifiedUserId = current.UserId;
                row.ModifiedUtc = now;
            }
        }

        // 覆寫層：U5 逐句合併、Q6 與內建相同的拿掉；只有確認原文已更新也要寫（雜湊不寫進新列就沒地方存，i4d2 判斷 7）。
        // 沒有任何變動就不加版本（不為了換 token 把上一版擠掉）；只改語系欄位也不加
        LocalePack? next = null;
        if (plan.OverlayChanged)
        {
            var merged = LocalePackAnalyzer.After(world, plan).Overlays[plan.Code];
            next = new LocalePack
            {
                Code = plan.Code, UiJson = LocalePackVersions.LayerJson(merged.Ui), ServerJson = LocalePackVersions.LayerJson(merged.Server),
                BaseHashesJson = LocalePackVersions.BaseHashesJson(currentPack, merged.Ui, merged.Server,
                    plan.UiSet.Keys.Union(plan.UiConfirm).ToHashSet(StringComparer.Ordinal), plan.ServerSet.Keys.Union(plan.ServerConfirm).ToHashSet(StringComparer.Ordinal)),
                FileHash = preview.FileHash, FileName = preview.FileName, PackVersion = plan.Pack.Version, BuiltFor = plan.Pack.BuiltFor,
                BaseLocale = plan.Pack.BaseLocale, Note = plan.Pack.Note,
                ReportJson = JsonSerializer.Serialize(new { ui = Counts(report.Ui), server = Counts(report.Server), completeness = report.Completeness }, LocalePackStorage.Json),
                ImportedUserId = current.UserId, ImportedUtc = now, ModifiedUtc = now,
            };
            packs.AddVersion(next, currentPack, await packs.PreviousAsync(plan.Code, ct), now);
        }
        packs.RemovePreview(preview);
        await uow.CommitAsync(ct);

        if (plan.IsNew) audit.Action("locale.install");
        audit.Target(plan.Code, plan.Locale?.NativeName ?? world.Installed.First(i => i.Code == plan.Code).NativeName);
        audit.Detail("locale", plan.Code);
        audit.Detail("fileName", preview.FileName);
        audit.Detail("fileHash", preview.FileHash);
        audit.Detail("isNew", plan.IsNew);
        audit.Detail("noChanges", plan.NoChanges);
        audit.Detail("ui", Counts(report.Ui));
        audit.Detail("server", Counts(report.Server));
        audit.Detail("designData", new { count = report.DesignData.Count, applied = 0 });
        afterCommit.Add("locale-registry", registry.Invalidate);

        var enabled = (await worlds.ReadAsync(ct)).Enabled.Contains(plan.Code);
        return ServiceResult.Success(new ApplyLocalePackResponse(plan.Code, plan.IsNew, plan.NoChanges, next?.Id ?? currentPack?.Id, enabled));
    }

    private static ServiceResult Stale() => ServiceResult.Conflict(LocalePackMessages.Stale, LocalePackMessages.CodeStale);

    private static object Counts(SectionReport s) =>
        new { s.Added, s.Changed, s.Unchanged, s.Skipped, s.SameAsBuiltin, s.Extra, s.Rejected, s.Confirmed };
}
