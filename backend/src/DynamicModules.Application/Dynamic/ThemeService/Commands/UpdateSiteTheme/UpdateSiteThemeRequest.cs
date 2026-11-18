using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.ThemeService.Commands.UpdateSiteTheme;

public record UpdateSiteThemeRequest(string? Mode, string? Style)
    : IRequest<ServiceResult>, ITransactionalRequest, ISystemAdminRequest, IAuditedRequest
{
    public AuditAction Audit => AuditAction.For("theme.update", AuditTargets.Theme, null);
}
