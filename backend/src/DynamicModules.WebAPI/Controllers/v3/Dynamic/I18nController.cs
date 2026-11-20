using DynamicModules.Application.Dynamic.LocaleService.Commands.ApplyLocalePack;
using DynamicModules.Application.Dynamic.LocaleService.Commands.PreviewLocalePack;
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

    /// <summary>
    /// 預覽語系包（語系包設計 §6.3；i4d1 計畫判斷 12）：本文是原始檔案位元組（Content-Type 不限）。主體有上限地讀
    /// （<see cref="LocalePackBodyLimitFilter"/> 給的上限，超過就停止讀取回 413），不整包收進記憶體再判斷。
    /// </summary>
    [HttpPost("packs/preview")]
    [TypeFilter(typeof(LocalePackBodyLimitFilter))]
    public async Task<IActionResult> PreviewPack([FromQuery] string? fileName)
    {
        var limit = HttpContext.Items[LocalePackBodyLimitFilter.LimitItem] as long? ?? 0;
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int n;
        while ((n = await Request.Body.ReadAsync(chunk, HttpContext.RequestAborted)) > 0)
        {
            if (buffer.Length + n > limit) return StatusCode(StatusCodes.Status413PayloadTooLarge);
            buffer.Write(chunk, 0, n);
        }
        var result = await mediator.Send(new PreviewLocalePackRequest(buffer.ToArray(), fileName), HttpContext.RequestAborted);
        return result.ToActionResult(this);
    }

    /// <summary>套用預覽過的語系包（i4d1 計畫判斷 12、16）：本文 <c>{ previewId, fileHash }</c>，成功回 <see cref="ApplyLocalePackResponse"/>。</summary>
    [HttpPost("packs/apply")]
    public async Task<IActionResult> ApplyPack([FromBody] ApplyLocalePackBody body) =>
        (await mediator.Send(new ApplyLocalePackRequest(body.PreviewId ?? "", body.FileHash ?? ""), HttpContext.RequestAborted)).ToActionResult(this);
}

public sealed record ApplyLocalePackBody(string? PreviewId, string? FileHash);
