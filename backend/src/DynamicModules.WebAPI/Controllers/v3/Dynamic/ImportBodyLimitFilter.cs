using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Policies;
using Microsoft.AspNetCore.Mvc.Filters;

namespace DynamicModules.WebAPI.Controllers.v3.Dynamic;

/// <summary>
/// 匯入 CSV 的 resource filter，在 model binding 收進檔案之前執行：請求上限＝兩倍檔案上限（政策 import.maxFileBytes）＋multipart 開銷，
/// 讓略超過檔案上限的檔案走到處理器拿到「檔案不能超過 N MB」，而不是框架的 413；Content-Length 超過請求上限直接 413、不讀主體。
/// </summary>
public sealed class ImportBodyLimitFilter(IPolicyReader policies) : IAsyncResourceFilter
{
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var maxBytes = await policies.IntAsync(PolicyCatalog.ImportMaxFileBytes, null, context.HttpContext.RequestAborted);
        if (UploadGuard.ApplyBodyLimit(context.HttpContext, maxBytes, files: 2) is { } tooLarge)
        {
            context.Result = tooLarge;
            return;
        }
        await next();
    }
}
