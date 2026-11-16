using DynamicModules.Application.Dynamic.AttachmentService;
using DynamicModules.Application.Dynamic.TaskService;
using DynamicModules.Domain.Policies;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace DynamicModules.WebAPI.Controllers.v3.Dynamic;

/// <summary>
/// 帶附件的留言（multipart）的 resource filter，在 model binding 收進檔案之前執行：上傳關閉回 409；
/// 先驗任務存取（看不到或不存在 404、凍結 409）並取得任務所屬模組的上限；
/// 主體超過「單檔上限 × 留言附件數上限 ＋ multipart 開銷」回 413，其餘請求把主體上限壓到同一個值，比照記錄附件的 <see cref="UploadGuardFilter"/>。檔案個數、檔名與大小由處理器檢查。
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
        // 路由值解析不出來時（處理器之後會回 404）用技術上限當主體上限
        var limit = new UploadBodyLimit(PolicyCatalog.AttachmentMaxFileBytes.Max, PolicyCatalog.AttachmentMaxFilesPerComment.Max);
        if (context.RouteData.Values["id"] is { } id && long.TryParse(id.ToString(), out var taskId))
        {
            var check = await mediator.Send(new CheckTaskCommentUploadRequest(taskId), context.HttpContext.RequestAborted);
            if (!check.IsSuccess || check.IsNotFound || check.IsConflict)
            {
                context.Result = Respond(context, check);
                return;
            }
            if (check.Data is UploadBodyLimit fromCheck) limit = fromCheck;
        }
        if (UploadGuard.ApplyBodyLimit(context.HttpContext, limit.MaxBytes, limit.Files) is { } tooLarge)
        {
            context.Result = tooLarge;
            return;
        }
        await next();
    }

    private static IActionResult Respond(ResourceExecutingContext context, Application.Common.ServiceResult result) =>
        result.ToActionResult(new Controller { ControllerContext = new ControllerContext(context) });

    private sealed class Controller : ControllerBase;
}
