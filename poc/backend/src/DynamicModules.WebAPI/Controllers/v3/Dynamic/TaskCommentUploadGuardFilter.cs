using DynamicModules.Application.Dynamic.AttachmentService;
using DynamicModules.Application.Dynamic.TaskService;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace DynamicModules.WebAPI.Controllers.v3.Dynamic;

/// <summary>
/// 帶附件的留言（multipart）的 resource filter，在 model binding 收進檔案之前執行：上傳關閉回 409；
/// 主體超過「單檔上限 × 5 ＋ multipart 開銷」回 413，其餘請求把主體上限壓到同一個值；
/// 再驗任務存取（看不到或不存在 404、凍結 409），比照記錄附件的 <see cref="UploadGuardFilter"/>。檔案個數、檔名與大小由處理器檢查。
/// </summary>
public sealed class TaskCommentUploadGuardFilter(AttachmentSettings settings, IMediator mediator) : IAsyncResourceFilter
{
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        if (!settings.UploadEnabled)
        {
            context.Result = Respond(context, AttachmentUploadGate.UploadDisabled(settings));
            return;
        }
        if (UploadGuard.ApplyBodyLimit(context.HttpContext, settings, TaskCommentRules.MaxFiles) is { } tooLarge)
        {
            context.Result = tooLarge;
            return;
        }
        if (context.RouteData.Values["id"] is { } id && long.TryParse(id.ToString(), out var taskId))
        {
            var check = await mediator.Send(new CheckTaskCommentUploadRequest(taskId), context.HttpContext.RequestAborted);
            if (!check.IsSuccess || check.IsNotFound || check.IsConflict)
            {
                context.Result = Respond(context, check);
                return;
            }
        }
        await next();
    }

    private static IActionResult Respond(ResourceExecutingContext context, Application.Common.ServiceResult result) =>
        result.ToActionResult(new Controller { ControllerContext = new ControllerContext(context) });

    private sealed class Controller : ControllerBase;
}
