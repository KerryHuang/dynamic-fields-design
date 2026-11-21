using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.McpService.Tools;
using DynamicModules.Application.Dynamic.McpService.Writes;
using DynamicModules.Application.Dynamic.TaskService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Mcp;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic.Mcp;

/// <summary>唯讀測試工具：記下工具執行時 <see cref="IOperationContext.ClientKey"/> 的值（冪等鍵不留到下一次呼叫）。</summary>
public sealed class KeyProbeTool(IOperationContext operation, KeyProbe probe) : IMcpTool
{
    public const string Name = "test_key_probe";
    public McpToolDescriptor Descriptor { get; } = new(Name, "mcp.getRecord", McpScopes.BusinessRead, McpRisk.Read);
    public string InputSchemaJson => "{}";
    public string Description => "test key probe";

    public Task<McpToolOutcome> RunAsync(McpToolCall call, CancellationToken ct)
    {
        (probe.Called, probe.ClientKey) = (true, operation.ClientKey);
        return Task.FromResult(McpToolOutcome.Ok(new { }));
    }
}

public sealed class KeyProbe
{
    public bool Called { get; set; }
    public string? ClientKey { get; set; }
}

/// <summary>雜湊探針（測試用寫入請求，handler 不寫資料）：比對畫面與 MCP 通道存下的請求雜湊。</summary>
public sealed record HashProbeWriteRequest(string Value) : IRequest<ServiceResult>, IRecordWriteRequest;

public sealed class HashProbeWriteHandler : IRequestHandler<HashProbeWriteRequest, ServiceResult>
{
    public Task<ServiceResult> Handle(HashProbeWriteRequest request, CancellationToken ct) =>
        Task.FromResult(ServiceResult.Success(new { value = request.Value }));
}

/// <summary>寫入工具的冪等鍵（mcp2a 判斷 5；設計 §7.2「IdempotencyBehavior 自動接手」）。並行重送在 WebAPI 的 McpIdempotencyApiTests。</summary>
[Trait("Category", "Postgres")]
public class McpIdempotencyTests : McpWriteTestBase
{
    private readonly KeyProbe _probe = new();

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        services.AddSingleton(_probe);
        services.AddScoped<IMcpTool, KeyProbeTool>();
        services.AddTransient<IRequestHandler<HashProbeWriteRequest, ServiceResult>, HashProbeWriteHandler>();
    }

    private static bool? Replayed(McpFormattedResult r) =>
        r.Structured.GetProperty("meta").TryGetProperty("replayed", out var v) ? v.GetBoolean() : null;

    private static string DataOf(McpFormattedResult r) => r.Structured.GetProperty("data").GetRawText();

    private int Operations(string key) => Db.Context.Set<OperationRecord>().AsNoTracking().Count(o => o.Key == key);

    [Fact]
    public async Task SameKey_Replays_OneWrite_MetaReplayed()
    {
        var key = NewKey();

        var first = await CallWrite(WriteArgs("once", key: key));
        Write.OperationIdInHandler = null;
        var second = await CallWrite(WriteArgs("once", key: key));

        first.IsError.Should().BeFalse(first.Text);
        second.IsError.Should().BeFalse(second.Text);
        (Replayed(first), Replayed(second)).Should().Be((false, true));
        Write.OperationIdInHandler.Should().BeNull("回放不再執行 handler");
        DataOf(second).Should().Be(DataOf(first));
        Operations(key).Should().Be(1);
    }

    [Fact]
    public async Task SameKey_Comment_OneWrite_SameData()
    {
        Channel.Channel = RequestChannel.Ui;
        var created = await SendDirect(new CreateTaskRequest(new TaskInput("留言冪等", null, null, null)));
        Channel.Channel = RequestChannel.Mcp;
        var taskId = long.Parse(McpWriteTargets.IdOf(created.Data)!);
        Write.Run = async (sender, args, ct) =>
        {
            var r = await sender.Send(new AddTaskCommentRequest(taskId, args.GetProperty("value").GetString()!, []), ct);
            return r.IsSuccess ? McpToolOutcome.Ok(r.Data) : McpToolOutcome.From(r);
        };
        var key = NewKey();

        var first = await CallWrite(WriteArgs("留言內容", key: key));
        var second = await CallWrite(WriteArgs("留言內容", key: key));

        first.IsError.Should().BeFalse(first.Text);
        (Replayed(first), Replayed(second)).Should().Be((false, true));
        Db.Context.Set<TaskComment>().AsNoTracking().Count(c => c.TaskId == taskId && c.Kind == TaskCommentKind.Comment).Should().Be(1);
        DataOf(second).Should().Be(DataOf(first));
    }

    [Fact]
    public async Task SameKey_Replay_AuditRow_Replayed()
    {
        var key = NewKey();
        await CallWrite(WriteArgs(key: key));
        await CallWrite(WriteArgs(key: key));

        (await McpRows()).Select(r => (r.Result, r.ReasonCode)).Should()
            .Equal((AuditResult.Success, null), (AuditResult.Success, McpWriteAuditBehavior<object, object>.Replayed));
    }

    [Fact]
    public async Task SameKey_DifferentArgs_IdempotencyKeyReused()
    {
        var key = NewKey();
        (await CallWrite(WriteArgs("first", key: key))).IsError.Should().BeFalse();

        var r = await CallWrite(WriteArgs("second", key: key));

        CodeOf(r).Should().Be(McpErrorCodes.IdempotencyKeyReused);
        ValuesOf(Target).Should().Contain("first").And.NotContain("second");
        var last = (await McpRows()).Last();
        (last.Result, last.ReasonCode).Should().Be((AuditResult.Invalid, McpErrorCodes.IdempotencyKeyReused));
    }

    [Theory]
    [InlineData("""{"recordId":1,"value":"v"}""")]
    [InlineData("""{"recordId":1,"value":"v","idempotencyKey":null}""")]
    public async Task Key_Missing_InvalidArguments(string args)
    {
        var r = await CallWrite(args);

        CodeOf(r).Should().Be(McpErrorCodes.InvalidArguments);
        r.Structured.GetProperty("error").GetProperty("fields").GetProperty(McpArgs.IdempotencyKey).GetString()
            .Should().Contain("idempotencyKey").And.Contain("8");
        Write.ToolCalled.Should().BeFalse();
    }

    [Theory]
    [InlineData("\"short\"")]
    [InlineData("\"has space 123\"")]
    [InlineData("123")]
    public async Task Key_BadFormat_InvalidArguments(string key)
    {
        var r = await CallWrite($$"""{"recordId":1,"value":"v","idempotencyKey":{{key}}}""");

        CodeOf(r).Should().Be(McpErrorCodes.InvalidArguments);
        r.Structured.GetProperty("error").GetProperty("fields").TryGetProperty(McpArgs.IdempotencyKey, out _).Should().BeTrue();
        Write.ToolCalled.Should().BeFalse();
    }

    [Fact]
    public async Task Key_IsolatedPerUser()
    {
        var key = NewKey();
        (await CallWrite(WriteArgs("a", key: key))).IsError.Should().BeFalse();
        As("wenhui");

        var other = await CallWrite(WriteArgs("a", key: key));

        other.IsError.Should().BeFalse(other.Text);
        Replayed(other).Should().BeFalse();
        Operations(key).Should().Be(2);
    }

    [Fact]
    public async Task Key_SharedAcrossTokensOfSameUser()
    {
        var key = NewKey();
        (await CallWrite(WriteArgs("a", key: key))).IsError.Should().BeFalse();
        Channel.TokenId = 8;

        var other = await CallWrite(WriteArgs("a", key: key));

        Replayed(other).Should().BeTrue("key 以使用者隔離、不以權杖隔離");
        Operations(key).Should().Be(1);
    }

    [Fact]
    public async Task Key_UiThenMcp_NotReplayed()
    {
        var key = NewKey();
        Channel.Channel = RequestChannel.Ui;
        ClientKey = key;
        (await SendDirect(new TouchRecordWriteRequest(Target, "same"))).IsSuccess.Should().BeTrue();
        ClientKey = null;
        Channel.Channel = RequestChannel.Mcp;
        Write.OperationIdInHandler = null;

        var r = await CallWrite(WriteArgs("same", key: key));

        CodeOf(r).Should().Be(McpErrorCodes.IdempotencyKeyReused, "雜湊含通道：不回放畫面通道的結果");
        Write.OperationIdInHandler.Should().BeNull();
    }

    /// <summary>畫面通道的雜湊算法不變：型別全名＋換行＋請求 JSON 的 SHA-256（既有紀錄照常回放）；MCP 通道多一個 <c>\nmcp</c>。</summary>
    [Fact]
    public async Task UiHash_Unchanged()
    {
        static string Sha(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
        var request = new HashProbeWriteRequest("x");
        var basis = typeof(HashProbeWriteRequest).FullName + "\n" + """{"value":"x"}""";
        var (uiKey, mcpKey) = (NewKey(), NewKey());

        Channel.Channel = RequestChannel.Ui;
        ClientKey = uiKey;
        (await SendDirect(request)).IsSuccess.Should().BeTrue();
        ClientKey = null;
        Channel.Channel = RequestChannel.Mcp;
        Write.Run = async (sender, _, ct) => McpToolOutcome.Ok((await sender.Send(request, ct)).Data);
        (await CallWrite(WriteArgs(key: mcpKey))).IsError.Should().BeFalse();

        string HashOf(string key) => Db.Context.Set<OperationRecord>().AsNoTracking().Single(o => o.Key == key).RequestHash;
        HashOf(uiKey).Should().Be(Sha(basis));
        HashOf(mcpKey).Should().Be(Sha(basis + "\nmcp"));
    }

    [Fact]
    public async Task FailedFirst_RetryExecutes()
    {
        var key = NewKey();
        Write.HandlerFails = true;
        CodeOf(await CallWrite(WriteArgs("retry", key: key))).Should().Be(McpErrorCodes.Invalid);
        Operations(key).Should().Be(0);

        Write.HandlerFails = false;
        var r = await CallWrite(WriteArgs("retry", key: key));

        r.IsError.Should().BeFalse(r.Text);
        Replayed(r).Should().BeFalse();
        ValuesOf(Target).Should().Contain("retry");
    }

    /// <summary>同一個 DI scope 連續兩次呼叫：前一次的冪等鍵、或 HTTP 標頭帶進來的鍵，都不會被下一次（唯讀工具）看到。</summary>
    [Fact]
    public async Task Key_NotLeakingToNextCall()
    {
        using var origin = AuditOrigin.Begin(AuditChannel.Mcp, TokenId);
        using var scope = NewScope();
        var invoker = scope.ServiceProvider.GetRequiredService<McpToolInvoker>();
        var operation = scope.ServiceProvider.GetRequiredService<IOperationContext>();
        async Task<McpFormattedResult> Run(string tool, string args)
        {
            Db.Context.ChangeTracker.Clear();
            return await invoker.InvokeAsync(tool, JsonDocument.Parse(args).RootElement.Clone(), new McpCallContext(), default);
        }

        (await Run(TestWriteTool.Name, WriteArgs())).IsError.Should().BeFalse();
        (await Run(KeyProbeTool.Name, "{}")).IsError.Should().BeFalse();
        (_probe.Called, _probe.ClientKey).Should().Be((true, (string?)null));

        operation.ClientKey = "header-key-0001";
        (await Run(KeyProbeTool.Name, "{}")).IsError.Should().BeFalse();
        _probe.ClientKey.Should().BeNull("MCP 只認工具參數的 idempotencyKey，不認 HTTP 標頭");
    }
}
