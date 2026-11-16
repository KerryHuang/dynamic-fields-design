using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.ThemeService.Commands.UpdateSiteTheme;

public record UpdateSiteThemeRequest(string? Mode, string? Style)
    : IRequest<ServiceResult>, ITransactionalRequest, ISystemAdminRequest;
