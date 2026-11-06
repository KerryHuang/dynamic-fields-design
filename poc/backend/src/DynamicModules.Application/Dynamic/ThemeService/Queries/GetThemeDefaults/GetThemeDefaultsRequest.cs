using DynamicModules.Application.Common;
using MediatR;

namespace DynamicModules.Application.Dynamic.ThemeService.Queries.GetThemeDefaults;

public record GetThemeDefaultsRequest : IRequest<ServiceResult>;
