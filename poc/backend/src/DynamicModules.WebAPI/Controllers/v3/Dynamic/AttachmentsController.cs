using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.AttachmentService;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace DynamicModules.WebAPI.Controllers.v3.Dynamic;

/// <summary>
/// 附件的上傳、下載與清掃。檔名只當顯示用；下載一律以 octet-stream、attachment、nosniff 回應，
/// 並加上沙箱 CSP，瀏覽器不會在站內解譯或執行檔案內容。
/// </summary>
[ApiController]
[Route("api/v3/dynamic")]
public class AttachmentsController(IMediator mediator) : ControllerBase
{
    [HttpGet("attachments/settings")]
    public async Task<IActionResult> Settings() => (await mediator.Send(new GetAttachmentSettingsRequest())).ToActionResult(this);

    /// <summary>multipart 的 file 欄位。<see cref="UploadGuardFilter"/> 在收進主體之前先擋（關閉、過大、無權限）；實際寫入時再逐位元組檢查大小。</summary>
    [HttpPost("records/{recordId:long}/attachments/{fieldId:long}")]
    [TypeFilter(typeof(UploadGuardFilter))]
    public async Task<IActionResult> Upload(long recordId, long fieldId, IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return ServiceResult.Invalid(new Dictionary<string, string[]> { ["file"] = [AttachmentRules.NameRequired] }).ToActionResult(this);
        await using var stream = file.OpenReadStream();
        return (await mediator.Send(new UploadAttachmentRequest(recordId, fieldId, stream, file.FileName), ct)).ToActionResult(this);
    }

    [HttpGet("records/{recordId:long}/attachments/{fieldId:long}/{fileId}")]
    public async Task<IActionResult> Download(long recordId, long fieldId, string fileId, CancellationToken ct)
    {
        var result = await mediator.Send(new DownloadAttachmentRequest(recordId, fieldId, fileId), ct);
        if (result.Data is not AttachmentDownload file) return result.ToActionResult(this);

        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Content-Security-Policy"] = "sandbox; default-src 'none'";
        Response.Headers.CacheControl = "private, no-store";
        // 固定 octet-stream：不依檔名猜 MIME，也就不會被瀏覽器當成 HTML 或 SVG 開在站內
        return File(file.Content, "application/octet-stream", file.Name);
    }

    [HttpPost("attachments/sweep")]
    public async Task<IActionResult> Sweep([FromQuery] int graceMinutes = 60, CancellationToken ct = default) =>
        (await mediator.Send(new SweepAttachmentsRequest(TimeSpan.FromMinutes(Math.Max(0, graceMinutes))), ct)).ToActionResult(this);
}
