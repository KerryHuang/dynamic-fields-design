using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.McpService.Confirmations;
using DynamicModules.Application.Dynamic.McpService.Tools;
using DynamicModules.Application.Dynamic.McpService.Writes;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Mcp;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Record = DynamicModules.Domain.Entities.Dynamic.Record;

namespace DynamicModules.ApplicationTests.Dynamic.Mcp;

// ───────────── 測試專用的寫入工具與請求（不進正式程式，mcp2a 判斷 3） ─────────────

/// <summary>寫入測試工具的行為（每個測試一份）：預設送一個 <see cref="TouchRecordWriteRequest"/>。</summary>
public sealed class WriteKnobs
{
    public Func<ISender, JsonElement, CancellationToken, Task<McpToolOutcome>>? Run { get; set; }
    public bool ToolCalled { get; set; }
    public bool ArmedWhenCalled { get; set; }
    public string? OperationIdInHandler { get; set; }
    public bool HandlerFails { get; set; }
    public bool HandlerThrows { get; set; }
}

/// <summary>寫入測試工具：參數 <c>{ recordId, value, idempotencyKey? }</c>；稽核代碼借用已登記的 <c>mcp.updateRecord</c>。</summary>
public sealed class TestWriteTool(ISender sender, WriteKnobs knobs, IMcpWriteScope writes) : IMcpTool
{
    public const string Name = "test_write";
    public McpToolDescriptor Descriptor { get; } =
        new(Name, "mcp.updateRecord", McpScopes.BusinessWrite, McpRisk.Write, LoopTargetArg: "recordId");
    public string InputSchemaJson => "{}";
    public string Description => "test write tool";

    public async Task<McpToolOutcome> RunAsync(McpToolCall call, CancellationToken ct)
    {
        (knobs.ToolCalled, knobs.ArmedWhenCalled) = (true, writes.IsArmed);
        if (knobs.Run is { } run) return await run(sender, call.Arguments, ct);
        var r = await sender.Send(TouchRecordWriteRequest.From(call.Arguments), ct);
        return r.IsSuccess ? McpToolOutcome.Ok(r.Data) : McpToolOutcome.From(r);
    }
}

/// <summary>改一筆記錄的 ValuesJson（測試用寫入請求）。</summary>
public sealed record TouchRecordWriteRequest(long RecordId, string Value) : IRequest<ServiceResult>, IRecordWriteRequest
{
    public static TouchRecordWriteRequest From(JsonElement args) =>
        new(args.GetProperty("recordId").GetInt64(), args.GetProperty("value").GetString()!);
}

public sealed class TouchRecordWriteHandler(DynamicDbContext db, WriteKnobs knobs, IOperationContext operation)
    : IRequestHandler<TouchRecordWriteRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(TouchRecordWriteRequest request, CancellationToken ct)
    {
        knobs.OperationIdInHandler = operation.OperationId;
        var record = await db.Set<Record>().FirstAsync(r => r.Id == request.RecordId, ct);
        record.ValuesJson = JsonSerializer.Serialize(new { touched = request.Value });
        record.RowVersion = Guid.NewGuid().ToString();
        await db.SaveChangesAsync(ct);
        if (knobs.HandlerThrows) throw new InvalidOperationException("handler boom");
        return knobs.HandlerFails ? ServiceResult.Invalid("value", MessageRef.Literal("不正確")) : ServiceResult.Success(new { id = request.RecordId, touched = request.Value });
    }
}

/// <summary>一般交易型請求（不是 IIdempotentRequest，模擬簽核警示）：改記錄 ValuesJson。</summary>
public sealed record PlainTransactionalForTestRequest(long RecordId) : IRequest<ServiceResult>, ITransactionalRequest;

public sealed class PlainTransactionalForTestHandler(DynamicDbContext db) : IRequestHandler<PlainTransactionalForTestRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(PlainTransactionalForTestRequest request, CancellationToken ct)
    {
        var record = await db.Set<Record>().FirstAsync(r => r.Id == request.RecordId, ct);
        record.ValuesJson = """{"plain":true}""";
        await db.SaveChangesAsync(ct);
        return ServiceResult.Success();
    }
}

/// <summary>故意違規：寫入請求同時有稽核宣告。</summary>
public sealed record AuditedWriteForTestRequest : IRequest<ServiceResult>, IRecordWriteRequest, IAuditedRequest
{
    public AuditAction Audit => new("mcp.updateRecord");
}

public sealed class AuditedWriteForTestHandler : IRequestHandler<AuditedWriteForTestRequest, ServiceResult>
{
    public Task<ServiceResult> Handle(AuditedWriteForTestRequest request, CancellationToken ct) => Task.FromResult(ServiceResult.Success());
}

/// <summary>寫入稽核與業務同交易（mcp2a 判斷 3；設計 §8.1、T14、B12）。</summary>
[Trait("Category", "Postgres")]
public class McpWriteAuditTests : McpConfirmationTestBase
{
    protected readonly WriteKnobs Write = new();

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        services.AddSingleton(Write);
        services.AddScoped<IMcpTool, TestWriteTool>();
        services.AddTransient<IRequestHandler<TouchRecordWriteRequest, ServiceResult>, TouchRecordWriteHandler>();
        services.AddTransient<IRequestHandler<PlainTransactionalForTestRequest, ServiceResult>, PlainTransactionalForTestHandler>();
        services.AddTransient<IRequestHandler<AuditedWriteForTestRequest, ServiceResult>, AuditedWriteForTestHandler>();
        services.AddTransient<IRequestHandler<OuterWriteForTestRequest, ServiceResult>, OuterWriteForTestHandler>();
    }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        Channel.Scopes = [McpScopes.BusinessRead, McpScopes.BusinessWrite];
    }

    private string WriteArgs(string value = "w1", object? recordId = null, string? key = null) =>
        JsonSerializer.Serialize(key is null
            ? new Dictionary<string, object> { ["recordId"] = recordId ?? Target, ["value"] = value }
            : new Dictionary<string, object> { ["recordId"] = recordId ?? Target, ["value"] = value, ["idempotencyKey"] = key });

    private Task<McpFormattedResult> CallWrite(string? args = null) => Call(args ?? WriteArgs(), TestWriteTool.Name);

    [Fact]
    public async Task Success_OneRow_InTransaction()
    {
        var r = await CallWrite();

        r.IsError.Should().BeFalse(r.Text);
        ValuesOf(Target).Should().Contain("w1");
        var row = (await McpRows()).Should().ContainSingle().Subject;
        (row.Action, row.Result, row.RiskLevel, row.TargetLabel, row.TokenId, row.ReasonCode)
            .Should().Be(("mcp.updateRecord", AuditResult.Success, AuditRiskLevel.Write, TestWriteTool.Name, TokenId, (string?)null));
        // 測試請求不在 McpWriteTargets 涵蓋的型別裡，目標照純函式的結果（正式請求的目標由 McpWriteTargetsTests 與 Task 4 的稽核案例驗）
        (row.TargetKind, row.TargetId).Should().Be(McpWriteTargets.Of(new TouchRecordWriteRequest(Target, "w1"), null));
        var summary = JsonDocument.Parse(row.SummaryJson!).RootElement;
        summary.GetProperty("op").GetString().Should().Be(Write.OperationIdInHandler).And.NotBeNullOrEmpty();
        summary.TryGetProperty("ms", out _).Should().BeFalse("交易內寫的那一列不帶耗時");
        summary.GetProperty("args").GetProperty("value").GetProperty("type").GetString().Should().Be("string", "寫入工具用 strict 摘要");
    }

    [Fact]
    public async Task AuditSaveFails_RollsBackBusiness()
    {
        var before = ValuesOf(Target);
        Fault.On = true;

        var r = await CallWrite();

        Fault.On = false;
        CodeOf(r).Should().Be(McpErrorCodes.AuditUnavailable);
        ValuesOf(Target).Should().Be(before);
        (await McpRows()).Should().BeEmpty();
    }

    [Fact]
    public async Task HandlerFails_InvokerWritesOneRow()
    {
        var before = ValuesOf(Target);
        Write.HandlerFails = true;

        var r = await CallWrite();

        CodeOf(r).Should().Be(McpErrorCodes.Invalid);
        ValuesOf(Target).Should().Be(before);
        (await McpRows()).Should().ContainSingle().Which.Result.Should().Be(AuditResult.Invalid);
    }

    [Fact]
    public async Task HandlerThrows_OneFailedRow()
    {
        var before = ValuesOf(Target);
        Write.HandlerThrows = true;

        CodeOf(await CallWrite()).Should().Be(McpErrorCodes.InternalError);
        ValuesOf(Target).Should().Be(before);
        var row = (await McpRows()).Should().ContainSingle().Subject;
        (row.Result, row.ReasonCode).Should().Be((AuditResult.Failed, "exception"));
    }

    [Fact]
    public async Task TwoTopLevelWrites_Throws()
    {
        Write.Run = async (sender, args, ct) =>
        {
            (await sender.Send(new TouchRecordWriteRequest(Target, "first"), ct)).IsSuccess.Should().BeTrue();
            await sender.Send(new TouchRecordWriteRequest(RecordA, "second"), ct);
            return McpToolOutcome.Ok(new { });
        };

        var r = await CallWrite();

        r.IsError.Should().BeFalse(r.Text);
        r.Structured.GetProperty("meta").GetProperty("dataOmitted").GetString().Should().Be(McpDataOmitted.PostCommitError);
        ValuesOf(Target).Should().Contain("first");
        ValuesOf(RecordA).Should().NotContain("second");
        (await McpRows()).Should().ContainSingle().Which.Result.Should().Be(AuditResult.Success);
    }

    [Fact]
    public async Task NestedTransactional_NotAudited()
    {
        Write.Run = async (sender, args, ct) =>
        {
            Knobs.Inner = s => s.Send(new TouchRecordWriteRequest(RecordA, "nested"));
            var r = await sender.Send(new OuterWriteForTestRequest(Target), ct);
            return r.IsSuccess ? McpToolOutcome.Ok(new { }) : McpToolOutcome.From(r);
        };

        (await CallWrite()).IsError.Should().BeFalse();

        ValuesOf(RecordA).Should().Contain("nested");
        (await McpRows()).Should().ContainSingle().Which.Result.Should().Be(AuditResult.Success);
    }

    [Fact]
    public async Task UiChannel_NoArm_NoRow()
    {
        Channel.Channel = RequestChannel.Ui;
        (await SendDirect(new TouchRecordWriteRequest(Target, "ui"))).IsSuccess.Should().BeTrue();
        ValuesOf(Target).Should().Contain("ui");
        Db.Context.Set<AuditLogEntry>().AsNoTracking().Count(a => a.Action == "mcp.updateRecord").Should().Be(0);
    }

    [Fact]
    public async Task Mcp_UnarmedWrite_Throws()
    {
        var before = ValuesOf(Target);
        await FluentActions.Awaiting(() => SendDirect(new TouchRecordWriteRequest(Target, "unarmed")))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*上膛*");
        ValuesOf(Target).Should().Be(before);

        (await SendDirect(new CreateMcpConfirmationRequest("x", "h", "v", "{}"))).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task FailedWrite_ThenNonIdempotentTransactional_PassesThrough()
    {
        Write.HandlerFails = true;
        Write.Run = async (sender, args, ct) =>
        {
            var failed = await sender.Send(new TouchRecordWriteRequest(Target, "x"), ct);
            (await sender.Send(new PlainTransactionalForTestRequest(RecordA), ct)).IsSuccess.Should().BeTrue();
            return McpToolOutcome.From(failed);
        };

        CodeOf(await CallWrite()).Should().Be(McpErrorCodes.Invalid);

        ValuesOf(RecordA).Should().Contain("plain");
        var rows = await McpRows();
        rows.Should().ContainSingle().Which.Result.Should().Be(AuditResult.Invalid);
    }

    [Fact]
    public async Task Armed_NonIdempotentFirst_ThenWrite_OneSuccessRow()
    {
        Write.Run = async (sender, args, ct) =>
        {
            (await sender.Send(new PlainTransactionalForTestRequest(RecordA), ct)).IsSuccess.Should().BeTrue();
            var r = await sender.Send(new TouchRecordWriteRequest(Target, "after"), ct);
            return McpToolOutcome.Ok(r.Data);
        };

        (await CallWrite()).IsError.Should().BeFalse();

        ValuesOf(Target).Should().Contain("after");
        (await McpRows()).Should().ContainSingle().Which.Result.Should().Be(AuditResult.Success);
    }

    [Fact]
    public async Task AuditReason_WrittenWhenNotCommitted()
    {
        Write.Run = (_, _, _) => Task.FromResult(McpToolOutcome.Ok(new { preview = 1 }) with { AuditReason = McpAuditReasons.Preview });

        (await CallWrite()).IsError.Should().BeFalse();

        var row = (await McpRows()).Should().ContainSingle().Subject;
        (row.Result, row.ReasonCode).Should().Be((AuditResult.Success, McpAuditReasons.Preview));
    }

    [Fact]
    public async Task AuditReason_NotInSet_OrOnError_NotWritten()
    {
        Write.Run = (_, _, _) => Task.FromResult(McpToolOutcome.Ok(new { }) with { AuditReason = "made_up" });
        (await CallWrite()).IsError.Should().BeFalse();
        Write.Run = (_, _, _) => Task.FromResult(McpToolOutcome.From(ServiceResult.NotFound(null)) with { AuditReason = McpAuditReasons.Preview });
        CodeOf(await CallWrite()).Should().Be(McpErrorCodes.NotFound);

        (await McpRows()).Select(r => (r.Result, r.ReasonCode)).Should()
            .Equal((AuditResult.Success, null), (AuditResult.Invalid, McpErrorCodes.NotFound));
    }

    [Fact]
    public async Task AuditReason_IgnoredAfterCommit()
    {
        Write.Run = async (sender, args, ct) =>
        {
            await sender.Send(new TouchRecordWriteRequest(Target, "c"), ct);
            return McpToolOutcome.Ok(new { }) with { AuditReason = McpAuditReasons.Preview };
        };
        (await CallWrite()).IsError.Should().BeFalse();
        (await McpRows()).Should().ContainSingle().Which.ReasonCode.Should().BeNull();
    }

    [Theory]
    [InlineData("""{"recordId":1,"value":"v","idempotencyKey":"short"}""", "idempotencyKey")]
    [InlineData("""{"recordId":1,"value":"v","idempotencyKey":123}""", "idempotencyKey")]
    [InlineData("""{"recordId":0,"value":"v"}""", "recordId")]
    [InlineData("""{"recordId":"12","value":"v"}""", "recordId")]
    public async Task ExecutorArgs_Invalid_NotArmed(string args, string field)
    {
        var r = await CallWrite(args);

        CodeOf(r).Should().Be(McpErrorCodes.InvalidArguments);
        r.Structured.GetProperty("error").GetProperty("fields").TryGetProperty(field, out _).Should().BeTrue();
        Write.ToolCalled.Should().BeFalse();
        (await McpRows()).Should().ContainSingle().Which.Result.Should().Be(AuditResult.Invalid);
    }

    [Fact]
    public async Task ToolSeesArmed()
    {
        (await CallWrite(WriteArgs(key: "key-abcdefgh"))).IsError.Should().BeFalse();
        Write.ArmedWhenCalled.Should().BeTrue();
    }

    [Fact]
    public async Task Committed_ResultTooLarge_NotError_OneRow()
    {
        Write.Run = async (sender, args, ct) =>
        {
            await sender.Send(new TouchRecordWriteRequest(Target, "big"), ct);
            return McpToolOutcome.Ok(new { blob = new string('x', McpLimits.MaxResultChars + 10) });
        };

        var r = await CallWrite();

        r.IsError.Should().BeFalse(r.Text);
        r.Structured.GetProperty("meta").GetProperty("dataOmitted").GetString().Should().Be(McpDataOmitted.ResultTooLarge);
        (await McpRows()).Should().ContainSingle().Which.Result.Should().Be(AuditResult.Success);
    }

    [Fact]
    public async Task Committed_CancelledAfterCommit_OneRow()
    {
        using var cts = new CancellationTokenSource();
        Write.Run = async (sender, args, ct) =>
        {
            await sender.Send(new TouchRecordWriteRequest(Target, "cx"), ct);
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return McpToolOutcome.Ok(new { });
        };

        await FluentActions.Awaiting(() => Call(WriteArgs(), TestWriteTool.Name, cts.Token)).Should().ThrowAsync<OperationCanceledException>();

        ValuesOf(Target).Should().Contain("cx");
        (await McpRows()).Should().ContainSingle().Which.Result.Should().Be(AuditResult.Success);
    }

    [Fact]
    public async Task AuditedRequest_Rejected()
    {
        Write.Run = async (sender, _, ct) => McpToolOutcome.From(await sender.Send(new AuditedWriteForTestRequest(), ct));

        CodeOf(await CallWrite()).Should().Be(McpErrorCodes.InternalError);
        (await McpRows()).Should().ContainSingle().Which.Result.Should().Be(AuditResult.Failed);
        Logs.Should().Contain(l => l.Message.Contains("稽核或確認請求"));
    }
}

/// <summary>寫入型的外層請求：改記錄 A，再跑 <see cref="HighRiskKnobs.Inner"/>（巢狀）。</summary>
public sealed record OuterWriteForTestRequest(long RecordId) : IRequest<ServiceResult>, IRecordWriteRequest;

public sealed class OuterWriteForTestHandler(DynamicDbContext db, HighRiskKnobs knobs, ISender sender)
    : IRequestHandler<OuterWriteForTestRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(OuterWriteForTestRequest request, CancellationToken ct)
    {
        var record = await db.Set<Record>().FirstAsync(r => r.Id == request.RecordId, ct);
        record.ValuesJson = """{"outer":true}""";
        await db.SaveChangesAsync(ct);
        if (knobs.TakeInner() is { } inner) await inner(sender);
        return ServiceResult.Success();
    }
}
