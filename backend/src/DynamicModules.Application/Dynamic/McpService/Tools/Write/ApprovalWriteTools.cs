using System.Globalization;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.McpService.Writes;
using MediatR;

namespace DynamicModules.Application.Dynamic.McpService.Tools.Write;

/// <summary>
/// <c>submit_approval</c>（mcp2a 計畫判斷 2、9）：
/// <list type="bullet">
/// <item><c>previewOnly: true</c> 只送唯讀的 <see cref="PreviewRecordApprovalRequest"/>，回 <c>data.preview</c>，不寫入；執行器已上膛但沒有
/// 寫入請求所以不會提交，工具回 <see cref="McpAuditReasons.Preview"/>，執行器事後寫一列（迴圈不計）。</item>
/// <item>沒帶 <c>previewOnly</c> 不先預覽，直接送畫面同一條 <see cref="SubmitApprovalRequest"/>：先預覽的話，第一次成功後的同 key 重送
/// 會被預覽的「已在簽核中」擋下，冪等沒機會回放。</item>
/// </list>
/// 意見長度是政策（<c>ApprovalPolicies.CommentLengthAsync</c>），工具不另驗，由 handler 判斷。
/// </summary>
public sealed class SubmitApprovalTool(ISender sender)
    : WriteTool(sender, "submit_approval", McpTexts.SubmitApprovalSchema, McpTexts.SubmitApprovalDescription)
{
    public override async Task<McpToolOutcome> RunAsync(McpToolCall call, CancellationToken ct)
    {
        var a = McpArgs.Of(call.Arguments);
        var id = a.Long("recordId", required: true);
        var to = a.Long("to", required: true);
        var previewOnly = a.Bool("previewOnly") ?? false;
        var rowVersion = a.String("rowVersion", required: !previewOnly);
        var picks = a.StringLists("picks");
        var comment = a.String("comment", maxLength: int.MaxValue);
        if (a.HasErrors) return a.Invalid();

        var target = id!.Value.ToString(CultureInfo.InvariantCulture);
        if (previewOnly)
        {
            var preview = await Sender.Send(new PreviewRecordApprovalRequest(id.Value, to!.Value), ct);
            return (preview.IsSuccess
                    ? McpToolOutcome.Ok(new { preview = preview.Data }) with { ResultCount = 1, AuditReason = McpAuditReasons.Preview }
                    : Fail(preview))
                with { TargetKind = McpWriteTargets.Record, TargetId = target };
        }
        var r = await Sender.Send(new SubmitApprovalRequest(id.Value, to!.Value, rowVersion!, picks, comment), ct);
        return Done(r, null, McpWriteTargets.Record, target);
    }
}

/// <summary>
/// <c>withdraw_approval</c>（mcp2a 計畫判斷 2）：送畫面同一條 <see cref="WithdrawApprovalRequest"/>。實例版本過期時 handler 回的是
/// <c>conflict</c>（StageHandled），不是 <c>stale</c>；工具描述提醒先用 get_record_approval 重讀。
/// </summary>
public sealed class WithdrawApprovalTool(ISender sender)
    : WriteTool(sender, "withdraw_approval", McpTexts.WithdrawApprovalSchema, McpTexts.WithdrawApprovalDescription)
{
    public override async Task<McpToolOutcome> RunAsync(McpToolCall call, CancellationToken ct)
    {
        var a = McpArgs.Of(call.Arguments);
        var id = a.Long("instanceId", required: true);
        var rowVersion = a.String("rowVersion", required: true);
        var comment = a.String("comment", maxLength: int.MaxValue);
        if (a.HasErrors) return a.Invalid();

        var r = await Sender.Send(new WithdrawApprovalRequest(id!.Value, rowVersion!, comment), ct);
        return Done(r, null, McpWriteTargets.ApprovalInstance, id.Value.ToString(CultureInfo.InvariantCulture));
    }
}
