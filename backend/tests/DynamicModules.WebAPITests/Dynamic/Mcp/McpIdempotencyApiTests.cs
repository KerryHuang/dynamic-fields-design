using System.Text.Json;
using DynamicModules.Application.Dynamic.McpService.Tools;
using DynamicModules.Application.Dynamic.TaskService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Mcp;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using static DynamicModules.WebAPITests.Dynamic.Mcp.McpTestKit;

namespace DynamicModules.WebAPITests.Dynamic.Mcp;

/// <summary>
/// 寫入工具的冪等鍵經 <c>/mcp</c> 並行重送（mcp2a 判斷 5）：兩個請求同時帶同一把 key，交易開頭的全庫寫入鎖讓後到者等前者提交，
/// 查到紀錄回放。ApplicationTests 共用一個 DbContext、無法真的並行，所以放在這裡（SQLite 與 PostgreSQL 都跑）。
/// </summary>
[Trait("Category", "Postgres")]
public class McpIdempotencyApiTests : IDisposable
{
    private readonly McpTransportTests.BarrierState _barrier = new(2);
    private readonly TestApp _app;

    public McpIdempotencyApiTests()
    {
        _app = NewApp(services: s =>
        {
            s.AddSingleton(_barrier);
            s.AddScoped<IMcpTool, CreateTaskProbeTool>();
        });
        EnableMcp(_app);
    }

    public void Dispose() => _app.Dispose();

    /// <summary>測試專用寫入工具：兩邊都進到工具才送 <see cref="CreateTaskRequest"/>（請求確定重疊）。</summary>
    public sealed class CreateTaskProbeTool(ISender sender, McpTransportTests.BarrierState barrier) : IMcpTool
    {
        public const string Name = "test_create_task";
        public McpToolDescriptor Descriptor { get; } = new(Name, "mcp.createTask", McpScopes.BusinessWrite, McpRisk.Write);
        public string InputSchemaJson => """{"type":"object"}""";
        public string Description => "test write tool";

        public async Task<McpToolOutcome> RunAsync(McpToolCall call, CancellationToken ct)
        {
            await barrier.ArriveAndWaitAsync(ct);
            var r = await sender.Send(new CreateTaskRequest(new TaskInput(call.Arguments.GetProperty("title").GetString()!, null, null, null)), ct);
            return r.IsSuccess ? McpToolOutcome.Ok(r.Data) : McpToolOutcome.From(r);
        }
    }

    [Fact]
    public async Task ConcurrentSameKey_OneWrite()
    {
        var (raw, _) = IssueToken(_app, scopes: McpScopes.Join([McpScopes.BusinessRead, McpScopes.BusinessWrite]));
        await using var a = await ConnectAsync(_app, raw);
        await using var b = await ConnectAsync(_app, raw);
        var args = new Dictionary<string, object?> { ["title"] = "並行冪等", ["idempotencyKey"] = "k-" + Guid.NewGuid().ToString("N") };

        var results = await Task.WhenAll(a.CallToolAsync(CreateTaskProbeTool.Name, args).AsTask(),
            b.CallToolAsync(CreateTaskProbeTool.Name, args).AsTask());

        _barrier.Overlapped.Should().BeTrue("兩個請求同時在工具內");
        var structured = results.Select(r =>
        {
            r.IsError.Should().NotBe(true, ((TextContentBlock)r.Content.Single()).Text);
            return r.StructuredContent!.Value;
        }).ToList();
        structured.Select(s => s.GetProperty("meta").GetProperty("replayed").GetBoolean()).Should().BeEquivalentTo([false, true]);
        structured.Select(s => s.GetProperty("data").GetProperty("id").GetInt64()).Distinct().Should().ContainSingle();
        Query(_app, db => db.Set<TaskItem>().Count(t => t.Title == "並行冪等")).Should().Be(1);
        Query(_app, db => db.Set<AuditLogEntry>().Count(e => e.Action == "mcp.createTask" && e.Result == AuditResult.Success))
            .Should().Be(2, "兩次呼叫各一列，回放那列 ReasonCode=replayed");
    }
}
