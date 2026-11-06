using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using MediatR;

namespace DynamicModules.Application.Dynamic.ThemeService.Commands.UpdateMyTheme;

/// <summary>
/// 不用 FluentValidation：ValidationBehavior 在 handler 之前執行，Demo 站送無效值會變 400，
/// 但 Demo 站要一律 403。所以順序由這裡掌握：Demo 站 → 允許值 → 寫入。
/// </summary>
public class UpdateMyThemeHandler(
    ICurrentUser current, IAccountRepository accounts, ISystemSettingRepository settings, IUnitOfWork uow, AuthSettings auth)
    : IRequestHandler<UpdateMyThemeRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(UpdateMyThemeRequest request, CancellationToken ct)
    {
        if (auth.DemoLoginEnabled) return ServiceResult.Forbidden();

        var errors = new Dictionary<string, string[]>();
        if (request.Mode is not null && ThemeOptions.NormalizeMode(request.Mode) is null) errors["mode"] = ["不是允許的模式"];
        if (request.Style is not null && ThemeOptions.NormalizeStyle(request.Style) is null) errors["style"] = ["不是允許的風格"];
        if (errors.Count > 0) return ServiceResult.Invalid(errors);

        var user = await accounts.GetTrackedAsync(current.UserId, ct);
        if (user is null) return ServiceResult.NotFound("使用者不存在");

        user.ThemeMode = request.Mode;
        user.ThemeStyle = request.Style;
        await uow.CommitAsync(ct);
        return ServiceResult.Success(ThemeResolver.ForUser(user, await ThemeResolver.ReadDefaultsAsync(settings, ct), demoSite: false));
    }
}
