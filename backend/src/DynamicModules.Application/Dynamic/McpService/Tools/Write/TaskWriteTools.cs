using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.McpService.Writes;
using DynamicModules.Application.Dynamic.TaskService;
using MediatR;

namespace DynamicModules.Application.Dynamic.McpService.Tools.Write;

/// <summary>
/// <c>create_task</c>（mcp2a 計畫判斷 2、16）：一個工具涵蓋三種建法，依參數送畫面同一條 request——沒帶目標是個人任務
/// （<see cref="CreateTaskRequest"/>）、<c>recordId</c> 是記錄任務（<see cref="CreateRecordTaskRequest"/>）、<c>parentTaskId</c> 是子任務
/// （<see cref="CreateSubtaskRequest"/>）；兩個都帶是參數錯誤。系統事件、參與者、通知由 <c>TaskWriter</c> 照畫面做。
/// </summary>
public sealed class CreateTaskTool(ISender sender)
    : WriteTool(sender, "create_task", McpTexts.CreateTaskSchema, McpTexts.CreateTaskDescription)
{
    public override async Task<McpToolOutcome> RunAsync(McpToolCall call, CancellationToken ct)
    {
        var a = McpArgs.Of(call.Arguments);
        var title = a.String("title", required: true, maxLength: TaskEditParser.TitleMax);
        var description = a.String("description", maxLength: TaskEditParser.DescriptionMax);
        var assignee = a.String("assigneeUserId");
        var due = a.Date("dueDate");
        var start = a.Date("startDate");
        var recordId = a.Long("recordId");
        var parentId = a.Long("parentTaskId");
        if (recordId is not null && parentId is not null) a.Fail("parentTaskId");
        if (a.HasErrors) return a.Invalid();

        var input = new TaskInput(title!, description, assignee, due, start);
        IRequest<ServiceResult> request = recordId is { } rid ? new CreateRecordTaskRequest(rid, input)
            : parentId is { } pid ? new CreateSubtaskRequest(pid, input)
            : new CreateTaskRequest(input);
        var r = await Sender.Send(request, ct);
        if (r.IsSuccess) return Done(r, null, McpWriteTargets.Task, McpWriteTargets.IdOf(r.Data));
        // 失敗時稽核目標是要掛上去的對象（記錄或父任務）；個人任務沒有目標
        return recordId is { } failedRecord ? Fail(r) with { TargetKind = McpWriteTargets.Record, TargetId = Id(failedRecord) }
            : parentId is { } failedParent ? Fail(r) with { TargetKind = McpWriteTargets.Task, TargetId = Id(failedParent) }
            : Fail(r);
    }

    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// <c>update_task</c>（mcp2a 計畫判斷 2、11、16）：送畫面同一條 <see cref="UpdateTaskRequest"/>。<c>Patch</c> 由工具逐鍵組出，
/// 只放狀態、負責人、到期日、開始日、連動方式；標題與描述不開放、多帶的參數不轉送。帶了才放進 Patch，帶 null 是清除（同 REST）。
/// 主管派給自己看不到的人時，結果改寫成 <c>{ taskId, hiddenAfterWrite: true }</c>，不給 <c>undoToken</c>（含冪等回放；寫死清單：安全）。
/// </summary>
public sealed class UpdateTaskTool(ISender sender)
    : WriteTool(sender, "update_task", McpTexts.UpdateTaskSchema, McpTexts.UpdateTaskDescription)
{
    public override async Task<McpToolOutcome> RunAsync(McpToolCall call, CancellationToken ct)
    {
        var a = McpArgs.Of(call.Arguments);
        var id = a.Long("taskId", required: true);
        var patch = new JsonObject();
        if (a.Has("status"))
        {
            var status = a.String("status");
            if (status is null || !TaskEditParser.StatusValues.Contains(status)) a.Fail("status");
            else patch["status"] = status;
        }
        if (a.Has("assigneeUserId")) patch["assigneeUserId"] = a.String("assigneeUserId");
        foreach (var date in new[] { "dueDate", "startDate" })
            if (a.Has(date)) patch[date] = a.Date(date)?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (a.Has("cascade") && a.String("cascade") is { } cascade)
        {
            if (TaskEditParser.TryParseCascade(cascade, out _)) patch["cascade"] = cascade;
            else a.Fail("cascade");
        }
        if (a.HasErrors) return a.Invalid();

        var r = await Sender.Send(new UpdateTaskRequest(id!.Value, JsonSerializer.SerializeToElement(patch)), ct);
        var target = id.Value.ToString(CultureInfo.InvariantCulture);
        if (r.IsSuccess && HiddenAfterWrite(r.Data) is { } hidden)
            return McpToolOutcome.Ok(new { taskId = hidden, hiddenAfterWrite = true }) with
            {
                TargetKind = McpWriteTargets.Task, TargetId = target, ResultCount = 1,
            };
        return Done(r, null, McpWriteTargets.Task, target);
    }

    /// <summary>
    /// 寫入後看不到的結果（判斷 11）回任務 id，否則 null。第一次是型別物件 <see cref="TaskHiddenAfterWrite"/>；冪等回放是
    /// <see cref="JsonElement"/> <c>{ id, visible: false, undoToken }</c>：兩種形狀都要辨識，回放也不能原樣交出（會帶出 undoToken）。
    /// 任務詳細沒有 <c>visible</c>、<c>undoToken</c> 這兩個鍵，帶了任一個就當成看不到的結果。
    /// </summary>
    private static long? HiddenAfterWrite(object? data) => data switch
    {
        TaskHiddenAfterWrite h => h.Id,
        JsonElement { ValueKind: JsonValueKind.Object } e when e.TryGetProperty("undoToken", out _)
            || (e.TryGetProperty("visible", out var visible) && visible.ValueKind == JsonValueKind.False)
            => e.TryGetProperty("id", out var tid) && tid.TryGetInt64(out var n) ? n : 0,
        _ => null,
    };
}

/// <summary>
/// <c>add_task_comment</c>（mcp2a 計畫判斷 2、5）：送畫面同一條 <see cref="AddTaskCommentRequest"/>，附件固定空清單（不收附件，
/// 所以 MCP 的留言一律有請求層冪等）。<c>@提及</c> 的解析、參與者與通知由既有 handler 做。
/// </summary>
public sealed class AddTaskCommentTool(ISender sender)
    : WriteTool(sender, "add_task_comment", McpTexts.AddTaskCommentSchema, McpTexts.AddTaskCommentDescription)
{
    public override async Task<McpToolOutcome> RunAsync(McpToolCall call, CancellationToken ct)
    {
        var a = McpArgs.Of(call.Arguments);
        var id = a.Long("taskId", required: true);
        var body = a.String("body", required: true, maxLength: TaskCommentRules.BodyMax);
        if (a.HasErrors) return a.Invalid();

        var r = await Sender.Send(new AddTaskCommentRequest(id!.Value, body!, []), ct);
        return Done(r, null, McpWriteTargets.Task, id.Value.ToString(CultureInfo.InvariantCulture));
    }
}
