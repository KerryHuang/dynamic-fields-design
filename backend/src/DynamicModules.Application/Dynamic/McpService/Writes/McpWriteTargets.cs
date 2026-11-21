using System.Text.Json;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.McpService.Tools;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Application.Dynamic.TaskService;

namespace DynamicModules.Application.Dynamic.McpService.Writes;

/// <summary>
/// 寫入工具送的請求 → 稽核目標（mcp2a 計畫判斷 3）。純函式。回應可能是型別物件，也可能是冪等回放的 <see cref="JsonElement"/>，
/// 兩種都從序列化後的 <c>id</c> 讀。不認得的請求型別回 (null, null)。
/// </summary>
public static class McpWriteTargets
{
    public const string Record = "record", Task = "task", ApprovalInstance = "approvalInstance";

    /// <summary>涵蓋的請求型別（架構測試據此檢查：不是稽核請求、不是確認請求、都是 <c>IIdempotentRequest</c>）。</summary>
    public static IReadOnlyList<Type> Covered { get; } =
    [
        typeof(CreateRecordRequest), typeof(UpdateRecordRequest), typeof(CreateTaskRequest), typeof(CreateRecordTaskRequest),
        typeof(CreateSubtaskRequest), typeof(UpdateTaskRequest), typeof(AddTaskCommentRequest), typeof(SubmitApprovalRequest),
        typeof(WithdrawApprovalRequest),
    ];

    public static (string? Kind, string? Id) Of(object request, object? data) => request switch
    {
        CreateRecordRequest => (Record, IdOf(data)),
        UpdateRecordRequest r => (Record, r.Id.ToString()),
        CreateTaskRequest or CreateRecordTaskRequest or CreateSubtaskRequest => (Task, IdOf(data)),
        UpdateTaskRequest r => (Task, r.Id.ToString()),
        AddTaskCommentRequest r => (Task, r.TaskId.ToString()),
        SubmitApprovalRequest r => (Record, r.RecordId.ToString()),
        WithdrawApprovalRequest r => (ApprovalInstance, r.InstanceId.ToString()),
        _ => (null, null),
    };

    /// <summary>回應的 <c>id</c>（數字或字串）；讀不到回 null。</summary>
    public static string? IdOf(object? data)
    {
        if (data is null) return null;
        var e = data is JsonElement j ? j : JsonSerializer.SerializeToElement(data, data.GetType(), McpResultFormatter.Json);
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("id", out var id)) return null;
        return id.ValueKind switch
        {
            JsonValueKind.Number => id.GetRawText(),
            JsonValueKind.String => id.GetString(),
            _ => null,
        };
    }
}
