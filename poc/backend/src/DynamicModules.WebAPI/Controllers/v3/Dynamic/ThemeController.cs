using DynamicModules.Application.Dynamic.ThemeService.Commands.UpdateMyTheme;
using DynamicModules.Application.Dynamic.ThemeService.Commands.UpdateSiteTheme;
using DynamicModules.Application.Dynamic.ThemeService.Queries.GetThemeDefaults;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace DynamicModules.WebAPI.Controllers.v3.Dynamic;

[ApiController]
[Route("api/v3/dynamic")]
public class ThemeController(IMediator mediator) : ControllerBase
{
    /// <summary>匿名可讀（登入頁使用），只回全站預設的兩個值。</summary>
    [HttpGet("theme")]
    public async Task<IActionResult> Defaults() => (await mediator.Send(new GetThemeDefaultsRequest())).ToActionResult(this);

    public record ThemeBody(string? Mode, string? Style);

    [HttpPut("me/theme")]
    public async Task<IActionResult> SaveMine(ThemeBody body) =>
        (await mediator.Send(new UpdateMyThemeRequest(body.Mode, body.Style))).ToActionResult(this);

    [HttpPut("system/theme")]
    public async Task<IActionResult> SaveSite(ThemeBody body) =>
        (await mediator.Send(new UpdateSiteThemeRequest(body.Mode, body.Style))).ToActionResult(this);
}
