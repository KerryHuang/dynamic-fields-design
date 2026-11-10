using DynamicModules.Application.Dynamic.AttachmentService;
using DynamicModules.Application.Dynamic.TaskService;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace DynamicModules.WebAPI.Controllers.v3.Dynamic;

/// <summary>
/// 帶附件的留言（multipart）的 resource filter，在 model binding 收進檔案之前執行：上傳關閉回 409；
/// 主體超過「單檔上限 × 5 ＋ multipart 開銷」回 413，其餘請求把主體上限壓到同一個值。檔案個數、檔名與大小由處理器檢查。
/// </summary>
public sealed class TaskCommentUploadGuardFilter(AttachmentSettings settings) : IAsyncResourceFilter
{
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        if (!settings.UploadEnabled)
        {
            var controller = new Controller { ControllerContext = new ControllerContext(context) };
            context.Result = AttachmentUploadGate.UploadDisabled(settings).ToActionResult(controller);
            return;
        }
        if (UploadGuard.ApplyBodyLimit(context.HttpContext, settings, TaskCommentRules.MaxFiles) is { } tooLarge)
        {
            context.Result = tooLarge;
            return;
        }
        await next();
    }

    private sealed class Controller : ControllerBase;
}
