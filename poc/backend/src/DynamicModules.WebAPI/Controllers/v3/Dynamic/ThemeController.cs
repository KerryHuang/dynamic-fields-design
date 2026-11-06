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
}
