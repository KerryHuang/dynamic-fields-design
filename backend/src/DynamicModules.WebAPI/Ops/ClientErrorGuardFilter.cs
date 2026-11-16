using DynamicModules.WebAPI.Security;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace DynamicModules.WebAPI.Ops;

/// <summary>前端錯誤收件的前置檢查（model binding 之前）：先限流（429），再擋過大的主體（413）。</summary>
public sealed class ClientErrorGuardFilter(ClientErrorThrottle throttle, ClientIpResolver clientIp) : IAsyncResourceFilter
{
    public Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var http = context.HttpContext;
        if (!throttle.TryAcquire(clientIp.Resolve(http) ?? "unknown"))
        {
            context.Result = new StatusCodeResult(StatusCodes.Status429TooManyRequests);
            return Task.CompletedTask;
        }
        if (http.Request.ContentLength > ClientErrorThrottle.MaxBodyBytes)
        {
            context.Result = new StatusCodeResult(StatusCodes.Status413PayloadTooLarge);
            return Task.CompletedTask;
        }
        // 沒有 Content-Length（chunked）時由 Kestrel 在讀取時丟 BadHttpRequestException（413）
        var feature = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = ClientErrorThrottle.MaxBodyBytes;
        return next();
    }
}
