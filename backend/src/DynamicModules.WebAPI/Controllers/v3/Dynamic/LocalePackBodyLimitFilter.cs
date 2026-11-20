using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Policies;
using Microsoft.AspNetCore.Mvc.Filters;

namespace DynamicModules.WebAPI.Controllers.v3.Dynamic;

/// <summary>
/// 語系包預覽的 resource filter（語系包 i4d1 計畫判斷 12，照 <see cref="ImportBodyLimitFilter"/>）：請求上限＝兩倍 A38（政策 locale.packMaxBytes）＋
/// multipart 開銷，Content-Length 超過直接 413、不讀主體；上限另放進 <see cref="HttpContext.Items"/>，controller 讀主體時也照它截斷（沒有 Content-Length 的串流也擋得住）。
/// 略超過 A38 的檔案走到處理器，拿到「語系包不能超過 N MB」。
/// </summary>
public sealed class LocalePackBodyLimitFilter(IPolicyReader policies) : IAsyncResourceFilter
{
    public const string LimitItem = "localePackBodyLimit";

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var maxBytes = await policies.IntAsync(PolicyCatalog.LocalePackMaxBytes, null, context.HttpContext.RequestAborted);
        if (UploadGuard.ApplyBodyLimit(context.HttpContext, maxBytes, files: 2) is { } tooLarge)
        {
            context.Result = tooLarge;
            return;
        }
        context.HttpContext.Items[LimitItem] = maxBytes * 2L + UploadGuard.MultipartOverhead;
        await next();
    }
}
