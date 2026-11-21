using System.Text.Json;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.McpService.Writes;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Application.Dynamic.TaskService;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic.Mcp;

/// <summary>寫入請求 → 稽核目標（mcp2a 判斷 3）：回應是型別物件與冪等回放的 JsonElement 都讀得到 id。</summary>
public class McpWriteTargetsTests
{
    private sealed record Created(long Id, string Title);

    private static readonly JsonElement Replayed = JsonDocument.Parse("""{"id":77,"title":"x"}""").RootElement.Clone();
    private static readonly TaskInput Input = new("t", null, null, null, null);

    public static TheoryData<object, object?, string, string> Cases() => new()
    {
        { new CreateRecordRequest("m", []), new Created(11, "a"), McpWriteTargets.Record, "11" },
        { new CreateRecordRequest("m", []), Replayed, McpWriteTargets.Record, "77" },
        { new UpdateRecordRequest(12, [], "rv"), null, McpWriteTargets.Record, "12" },
        { new CreateTaskRequest(Input), new Created(13, "a"), McpWriteTargets.Task, "13" },
        { new CreateRecordTaskRequest(1, Input), Replayed, McpWriteTargets.Task, "77" },
        { new CreateSubtaskRequest(1, Input), new Created(14, "a"), McpWriteTargets.Task, "14" },
        { new UpdateTaskRequest(15, Replayed), null, McpWriteTargets.Task, "15" },
        { new AddTaskCommentRequest(16, "b", []), new Created(99, "c"), McpWriteTargets.Task, "16" },
        { new SubmitApprovalRequest(17, 1, "rv", null, null), null, McpWriteTargets.Record, "17" },
        { new WithdrawApprovalRequest(18, "rv", null), null, McpWriteTargets.ApprovalInstance, "18" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Target(object request, object? data, string kind, string id) =>
        McpWriteTargets.Of(request, data).Should().Be((kind, id));

    [Fact]
    public void Unknown_Null() => McpWriteTargets.Of(new object(), null).Should().Be(((string?)null, (string?)null));

    [Fact]
    public void IdOf_MissingId_Null() => McpWriteTargets.IdOf(new { name = "x" }).Should().BeNull();
}
