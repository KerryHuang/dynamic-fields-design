using DynamicModules.Application.Common;
using MediatR;

namespace DynamicModules.Application.Dynamic.ThemeService.Commands.UpdateMyTheme;

/// <summary>整份取代個人設定；null 代表改回跟隨全站預設。</summary>
public record UpdateMyThemeRequest(string? Mode, string? Style) : IRequest<ServiceResult>, ITransactionalRequest;
