using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.LocaleService.Commands.RemoveLocale;

/// <summary>移除匯入的語系（<c>DELETE i18n/locales/{code}</c>；語系包設計 §4.5、i4d1 計畫判斷 19）：語系列、覆寫層版本與預覽一起刪，設計資料裡的文字不動。</summary>
public sealed record RemoveLocaleRequest(string Code) : IRequest<ServiceResult>, ITransactionalRequest, ISystemAdminRequest, IAuditedRequest
{
    public AuditAction Audit => AuditAction.For("locale.remove", AuditTargets.Locale, Code);
}
