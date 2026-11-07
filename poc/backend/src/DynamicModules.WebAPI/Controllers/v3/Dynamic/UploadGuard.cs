using DynamicModules.Application.Dynamic.AttachmentService;
using MediatR;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace DynamicModules.WebAPI.Controllers.v3.Dynamic;

/// <summary>
/// 附件上傳的 resource filter：在 model binding（IFormFile 會把整個 multipart 收進暫存）之前執行。
/// 順序：上傳關閉直接回 409；Content-Length 超過「檔案大小上限＋multipart 開銷」直接回 413；
/// 其餘請求把 Kestrel 的主體上限壓到同一個值（chunked 沒有 Content-Length 也受限）；
/// 最後在收進檔案之前先驗權限與欄位可寫。上傳的檔案大小與數量仍由處理器在實際寫入時檢查。
/// </summary>
public sealed class UploadGuardFilter(AttachmentSettings settings, IMediator mediator) : IAsyncResourceFilter
{
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        if (!settings.UploadEnabled)
        {
            context.Result = Respond(context, AttachmentUploadGate.UploadDisabled(settings));
            return;
        }
        if (UploadGuard.ApplyBodyLimit(context.HttpContext, settings) is { } tooLarge)
        {
            context.Result = tooLarge;
            return;
        }
        if (context.RouteData.Values["recordId"] is { } recordId && context.RouteData.Values["fieldId"] is { } fieldId
            && long.TryParse(recordId.ToString(), out var r) && long.TryParse(fieldId.ToString(), out var f))
        {
            var check = await mediator.Send(new CheckAttachmentUploadRequest(r, f), context.HttpContext.RequestAborted);
            if (!check.IsSuccess || check.IsUnauthorized || check.IsForbidden || check.IsNotFound || check.IsConflict)
            {
                context.Result = Respond(context, check);
                return;
            }
        }
        await next();
    }

    private static IActionResult Respond(ResourceExecutingContext context, Application.Common.ServiceResult result)
    {
        var controller = new Controller();
        controller.ControllerContext = new ControllerContext(context);
        return result.ToActionResult(controller);
    }

    private sealed class Controller : ControllerBase;
}

public static class UploadGuard
{
    /// <summary>multipart 的邊界與欄位標頭開銷；檔案本身的上限由設定決定。</summary>
    public const long MultipartOverhead = 64 * 1024;

    /// <summary>
    /// Content-Length 超過上限回 413（IActionResult）；否則設定 <see cref="IHttpMaxRequestBodySizeFeature"/> 與表單 multipart 長度上限後回 null。
    /// </summary>
    public static IActionResult? ApplyBodyLimit(HttpContext http, AttachmentSettings settings)
    {
        var limit = settings.MaxBytes + MultipartOverhead;
        if (http.Request.ContentLength > limit) return new StatusCodeResult(StatusCodes.Status413PayloadTooLarge);

        var feature = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = limit;
        // 表單讀取器自己也限制 multipart 長度（沒有 Kestrel 時，例如測試主機，靠這個）
        http.Features.Set<IFormFeature>(new FormFeature(http.Request, new FormOptions { MultipartBodyLengthLimit = limit }));
        return null;
    }
}
