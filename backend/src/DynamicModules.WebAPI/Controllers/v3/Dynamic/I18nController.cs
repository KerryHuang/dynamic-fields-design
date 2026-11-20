using DynamicModules.Application.Dynamic.LocaleService.Queries.GetUiMessages;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace DynamicModules.WebAPI.Controllers.v3.Dynamic;

[ApiController]
[Route("api/v3/dynamic/i18n")]
public class I18nController(IMediator mediator) : ControllerBase
{
    /// <summary>
    /// 補齊後的介面文字（扁平 JSON）。快取標頭（i4b 計畫判斷 8）：啟用語系且 <c>v</c> 等於這台的雜湊 → 一年 immutable；
    /// <c>v</c> 缺或不等（前端還不知道雜湊、或打到還沒換新的另一台）→ no-store；已登入取未啟用語系 → private, no-cache。
    /// 一律帶 <c>X-UI-Hash</c>。31536000 秒是 immutable 網址的慣用值（技術限制，設計 §14）。
    /// </summary>
    [HttpGet("ui/{code}")]
    public async Task<IActionResult> Ui(string code, [FromQuery] string? v)
    {
        var result = await mediator.Send(new GetUiMessagesRequest(code, User.Identity?.IsAuthenticated == true));
        if (!result.IsSuccess || result.Data is not UiMessagesResult r) return result.ToActionResult(this);
        Response.Headers.CacheControl = !r.Enabled ? "private, no-cache"
            : v == r.Hash ? "public, max-age=31536000, immutable"
            : "no-store";
        Response.Headers["X-UI-Hash"] = r.Hash;
        return File(r.Json, "application/json; charset=utf-8");
    }
}
