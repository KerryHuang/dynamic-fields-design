using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.LocaleService.Commands.RollbackLocalePack;

/// <summary>回到上一版（<c>POST i18n/locales/{code}/rollback</c>；語系包設計 §6.4、i4d1 計畫判斷 20）：上一版內容複製成新的目前版。</summary>
public sealed record RollbackLocalePackRequest(string Code) : IRequest<ServiceResult>, ITransactionalRequest, ISystemAdminRequest, IAuditedRequest
{
    public AuditAction Audit => AuditAction.For("locale.pack.rollback", AuditTargets.Locale, Code);
}
