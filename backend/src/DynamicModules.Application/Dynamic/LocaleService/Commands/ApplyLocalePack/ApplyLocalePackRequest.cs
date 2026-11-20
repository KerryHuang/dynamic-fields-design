using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.LocaleService.Commands.ApplyLocalePack;

/// <summary>
/// 套用預覽過的語系包（<c>POST i18n/packs/apply</c>；語系包設計 §6.3 第 3 步、i4d1 計畫判斷 16）。稽核宣告 <c>locale.pack.import</c>，
/// 新安裝時 handler 換成 <c>locale.install</c>（判斷 17）。
/// </summary>
public sealed record ApplyLocalePackRequest(string PreviewId, string FileHash)
    : IRequest<ServiceResult>, ITransactionalRequest, ISystemAdminRequest, IAuditedRequest
{
    public AuditAction Audit => AuditAction.For("locale.pack.import", AuditTargets.Locale, null);
}
