using DynamicModules.Application.Dynamic.AttachmentService;
using Microsoft.AspNetCore.Mvc;

namespace DynamicModules.WebAPI.Controllers.v3.Dynamic;

/// <summary>
/// 附件下載的共用回應（記錄附件與任務留言附件同一份）：固定 octet-stream、attachment、nosniff、沙箱 CSP、不快取，
/// 瀏覽器不會在站內解譯或執行檔案內容。
/// </summary>
public static class AttachmentFileResult
{
    public static IActionResult From(ControllerBase controller, AttachmentDownload file)
    {
        var headers = controller.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Content-Security-Policy"] = "sandbox; default-src 'none'";
        headers.CacheControl = "private, no-store";
        // 固定 octet-stream：不依檔名猜 MIME，也就不會被瀏覽器當成 HTML 或 SVG 開在站內
        return controller.File(file.Content, "application/octet-stream", file.Name);
    }
}
