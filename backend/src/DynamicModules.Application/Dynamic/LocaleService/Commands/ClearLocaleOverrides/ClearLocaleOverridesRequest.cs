using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.LocaleService.Commands.ClearLocaleOverrides;

/// <summary>移除覆寫（<c>POST i18n/locales/{code}/clear</c>；語系包設計 §6.5、i4d1 計畫判斷 20、Q5）：新增一個空的版本，可用回到上一版復原。</summary>
public sealed record ClearLocaleOverridesRequest(string Code) : IRequest<ServiceResult>, ITransactionalRequest, ISystemAdminRequest, IAuditedRequest
{
    public AuditAction Audit => AuditAction.For("locale.pack.clear", AuditTargets.Locale, Code);
}
