using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Dynamic.LocaleService.Packs;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.LocaleService.Commands.RemoveLocale;

/// <summary>
/// 守衛（資料完整性，判斷 19）：內建語系不能移除；在啟用、預設或必填裡不能移除；被其他語系當成上層不能移除。三個政策與被引用檢查都讀交易內的
/// <see cref="LocaleWorld"/>（不讀快照，也不用 scope 裡以快照濾值的 <c>IPolicyReader</c>）。告警信的語系剛好是它不擋（Q9）。
/// 移除不會造成新的是／否撞字（被移除的語系不在任何語系的鏈上），不必模擬。
/// </summary>
public sealed class RemoveLocaleHandler(
    LocaleWorldReader worlds, ILocaleRepository locales, ILocalePackRepository packs, IUnitOfWork uow, AuditContext audit,
    AfterCommitActions afterCommit, LocaleRegistry registry, LocalePackSettings settings) : IRequestHandler<RemoveLocaleRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(RemoveLocaleRequest request, CancellationToken ct)
    {
        if (!settings.ImportEnabled) return ServiceResult.Forbidden(LocalePackMessages.ImportDisabled, LocalePackMessages.CodeImportDisabled);
        var code = request.Code;
        if (await locales.FindAsync(code, ct) is not { } row) return ServiceResult.NotFound(LocalePackMessages.NotFound, LocalePackMessages.CodeNotFound);
        var world = await worlds.ReadAsync(ct);
        if (row.Origin != LocaleOrigin.Installed || world.Installed.Any(i => i.Code == code && i.Builtin))
            return ServiceResult.Conflict(LocalePackMessages.BuiltinNotRemovable, LocalePackMessages.CodeBuiltinNotRemovable);
        if (world.DefaultLocale == code || world.Enabled.Contains(code) || world.Required.Contains(code))
            return ServiceResult.Conflict(LocalePackMessages.InUse, LocalePackMessages.CodeInUse);
        var dependents = world.Installed.Where(i => i.FallbackCode == code).Select(i => i.NativeName).ToList();
        if (dependents.Count > 0) return ServiceResult.Conflict(LocalePackMessages.IsFallback(dependents), LocalePackMessages.CodeIsFallback);

        locales.Remove(row);
        await packs.DeleteForLocaleAsync(code, ct);
        await uow.CommitAsync(ct);
        audit.Target(code, row.NativeName);
        audit.Detail("locale", code);
        afterCommit.Add("locale-registry", registry.Invalidate);
        return ServiceResult.Success();
    }
}
