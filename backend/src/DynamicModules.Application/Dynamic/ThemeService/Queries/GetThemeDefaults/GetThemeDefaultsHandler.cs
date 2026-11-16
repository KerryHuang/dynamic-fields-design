using DynamicModules.Application.Common;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.ThemeService.Queries.GetThemeDefaults;

public class GetThemeDefaultsHandler(ISystemSettingRepository settings) : IRequestHandler<GetThemeDefaultsRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetThemeDefaultsRequest request, CancellationToken ct) =>
        ServiceResult.Success(await ThemeResolver.ReadDefaultsAsync(settings, ct));
}
