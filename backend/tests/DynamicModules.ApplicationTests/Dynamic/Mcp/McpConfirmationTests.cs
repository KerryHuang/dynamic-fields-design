using System.Reflection;
using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.McpService;
using DynamicModules.Application.Dynamic.McpService.Confirmations;
using DynamicModules.Application.Dynamic.McpService.Tools;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Mcp;
using DynamicModules.Domain.Policies;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Module = DynamicModules.Domain.Entities.Dynamic.Module;
using Record = DynamicModules.Domain.Entities.Dynamic.Record;

namespace DynamicModules.ApplicationTests.Dynamic.Mcp;

// ───────────── 測試專用的高風險工具與請求（不進正式程式，計畫判斷 18） ─────────────

/// <summary>測試工具與 handler 的可調行為（每個測試一份）。</summary>
public sealed class HighRiskKnobs
{
    /// <summary>成功時回的資料（預設 <c>{ touched = id }</c>）。</summary>
    public Func<object>? ResultData { get; set; }
    public bool ThrowBeforeSend { get; set; }
    /// <summary>Send 回來之後、回結果之前（例如取消、丟例外）。</summary>
    public Action? AfterSend { get; set; }
    /// <summary>handler：消耗之前（例如模擬另一個確認先用掉同一個碼）。</summary>
    public Func<DynamicDbContext, Task>? BeforeConsume { get; set; }
    public bool SkipConsume { get; set; }
    /// <summary>handler：消耗與寫入之後再送的請求（巢狀）。</summary>
    public Func<ISender, Task>? Inner { get; set; }
    /// <summary>取出並清掉 <see cref="Inner"/>（只跑一次，免得內層請求的 handler 又遞迴送自己）。</summary>
    public Func<ISender, Task>? TakeInner()
    {
        var inner = Inner;
        Inner = null;
        return inner;
    }
    public bool ThrowAfterConsume { get; set; }
}

/// <summary>高風險測試工具：參數 <c>{ recordId, value }</c>；預演讀記錄版本，執行送 <see cref="TouchRecordForTestRequest"/>。</summary>
public abstract class HighRiskToolBase(ISender sender, HighRiskKnobs knobs, string name) : IMcpTool
{
    // 測試請求的稽核代碼借用已登記的 mcp.getRecord（只為測試，不在正式目錄加測試代碼）
    public McpToolDescriptor Descriptor { get; } = new(name, "mcp.getRecord", McpScopes.BusinessRead, McpRisk.HighRisk);
    public string InputSchemaJson => "{}";
    public string Description => "test high-risk tool";
    public Task<McpToolOutcome> RunAsync(McpToolCall call, CancellationToken ct) => throw new NotSupportedException();

    private static (long Id, string Value) Parse(McpToolCall call) =>
        (call.Arguments.GetProperty("recordId").GetInt64(), call.Arguments.GetProperty("value").GetString()!);

    public async Task<McpPreview> PreviewAsync(McpToolCall call, CancellationToken ct)
    {
        var (id, value) = Parse(call);
        var r = await sender.Send(new GetRecordVersionForTestRequest(id), ct);
        return r.IsSuccess ? new McpPreview(new { recordId = id, value }, (string)r.Data!, "record", id.ToString())
            : McpPreview.Failed(McpToolOutcome.From(r));
    }

    public async Task<McpToolOutcome> ExecuteAsync(McpToolCall call, McpConfirmationTicket ticket, CancellationToken ct)
    {
        if (knobs.ThrowBeforeSend) throw new InvalidOperationException("before send");
        var (id, value) = Parse(call);
        var r = await sender.Send(new TouchRecordForTestRequest(id, value, ticket), ct);
        knobs.AfterSend?.Invoke();
        ct.ThrowIfCancellationRequested();
        return r.IsSuccess ? McpToolOutcome.Ok(knobs.ResultData?.Invoke() ?? new { touched = id }) : McpToolOutcome.From(r);
    }
}

public sealed class TestHighRiskTool(ISender sender, HighRiskKnobs knobs) : HighRiskToolBase(sender, knobs, Name)
{
    public const string Name = "test_high_risk";
}

public sealed class OtherHighRiskTool(ISender sender, HighRiskKnobs knobs) : HighRiskToolBase(sender, knobs, Name)
{
    public const string Name = "test_high_risk_b";
}

public sealed record GetRecordVersionForTestRequest(long Id) : IRequest<ServiceResult>;

public sealed class GetRecordVersionForTestHandler(DynamicDbContext db) : IRequestHandler<GetRecordVersionForTestRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetRecordVersionForTestRequest request, CancellationToken ct) =>
        await db.Set<Record>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == request.Id, ct) is { } r
            ? ServiceResult.Success(r.RowVersion) : ServiceResult.NotFound(null);
}

/// <summary>改一筆記錄的 ValuesJson（測試用確認請求）。</summary>
public sealed record TouchRecordForTestRequest(long RecordId, string Value, McpConfirmationTicket? Confirmation)
    : IRequest<ServiceResult>, ITransactionalRequest, IMcpConfirmedRequest;

public sealed class TouchRecordForTestHandler(DynamicDbContext db, IMcpConfirmationScope scope, HighRiskKnobs knobs, ISender sender)
    : IRequestHandler<TouchRecordForTestRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(TouchRecordForTestRequest request, CancellationToken ct)
    {
        var record = await db.Set<Record>().FirstAsync(r => r.Id == request.RecordId, ct);
        if (knobs.BeforeConsume is { } before) await before(db);
        if (!knobs.SkipConsume && !await scope.ConsumeAsync(record.RowVersion, ct)) return McpConfirmationResults.Invalid();
        record.ValuesJson = JsonSerializer.Serialize(new { touched = request.Value });
        record.RowVersion = Guid.NewGuid().ToString();
        await db.SaveChangesAsync(ct);
        if (knobs.TakeInner() is { } inner) await inner(sender);
        if (knobs.ThrowAfterConsume) throw new InvalidOperationException("after consume");
        return ServiceResult.Success();
    }
}

/// <summary>一般交易型的外層請求：先改記錄 A，再跑 <see cref="HighRiskKnobs.Inner"/>。</summary>
public sealed record OuterForTestRequest(long RecordId) : IRequest<ServiceResult>, ITransactionalRequest;

public sealed class OuterForTestHandler(DynamicDbContext db, HighRiskKnobs knobs, ISender sender) : IRequestHandler<OuterForTestRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(OuterForTestRequest request, CancellationToken ct)
    {
        var record = await db.Set<Record>().FirstAsync(r => r.Id == request.RecordId, ct);
        record.ValuesJson = """{"outer":true}""";
        await db.SaveChangesAsync(ct);
        if (knobs.TakeInner() is { } inner) await inner(sender);
        return ServiceResult.Success();
    }
}

/// <summary>故意違規：確認請求同時有稽核宣告（架構測試與 behavior 入口都要抓到）。</summary>
public sealed record AuditedConfirmedForTestRequest(McpConfirmationTicket? Confirmation)
    : IRequest<ServiceResult>, ITransactionalRequest, IMcpConfirmedRequest, IAuditedRequest
{
    public AuditAction Audit => new("mcp.getRecord");
}

public sealed class AuditedConfirmedForTestHandler : IRequestHandler<AuditedConfirmedForTestRequest, ServiceResult>
{
    public Task<ServiceResult> Handle(AuditedConfirmedForTestRequest request, CancellationToken ct) => Task.FromResult(ServiceResult.Success());
}

/// <summary>開著時，新增稽核列的存檔丟例外（模擬稽核表寫不進去；確認稽核與執行器的 WriteNowAsync 都會被擋）。</summary>
public sealed class AuditInsertFault : SaveChangesInterceptor
{
    public bool On { get; set; }

    private void Check(DbContext? context)
    {
        if (On && context?.ChangeTracker.Entries<AuditLogEntry>().Any(e => e.State == EntityState.Added) == true)
            throw new InvalidOperationException("audit table unavailable");
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Check(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Check(eventData.Context);
        return ValueTask.FromResult(result);
    }
}

/// <summary>確認碼測試共用：一個模組兩筆記錄（目標與記錄 A），高風險測試工具兩個。</summary>
public abstract class McpConfirmationTestBase : McpInvokerTestBase
{
    protected readonly HighRiskKnobs Knobs = new();
    protected readonly AuditInsertFault Fault = new();
    protected long Target, RecordA;

    protected override IReadOnlyList<IInterceptor> ExtraInterceptors => [Fault];

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        services.AddSingleton(Knobs);
        services.AddScoped<IMcpTool, TestHighRiskTool>();
        services.AddScoped<IMcpTool, OtherHighRiskTool>();
        services.AddTransient<IRequestHandler<GetRecordVersionForTestRequest, ServiceResult>, GetRecordVersionForTestHandler>();
        services.AddTransient<IRequestHandler<TouchRecordForTestRequest, ServiceResult>, TouchRecordForTestHandler>();
        services.AddTransient<IRequestHandler<OuterForTestRequest, ServiceResult>, OuterForTestHandler>();
        services.AddTransient<IRequestHandler<AuditedConfirmedForTestRequest, ServiceResult>, AuditedConfirmedForTestHandler>();
    }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        Channel.Channel = RequestChannel.Ui;
        await NewModule("mcpconf", activate: true);
        await CreateRecord("mcpconf");
        await CreateRecord("mcpconf");
        var moduleId = Db.Context.Set<Module>().Single(m => m.Key == "mcpconf").Id;
        var ids = Db.Context.Set<Record>().Where(r => r.ModuleId == moduleId).OrderBy(r => r.Id).Select(r => r.Id).ToList();
        (Target, RecordA) = (ids[0], ids[1]);
        Channel.Channel = RequestChannel.Mcp;
    }

    protected string Args(string value = "v1", long? record = null, string? code = null) =>
        JsonSerializer.Serialize(code is null
            ? new Dictionary<string, object> { ["recordId"] = record ?? Target, ["value"] = value }
            : new Dictionary<string, object> { ["recordId"] = record ?? Target, ["value"] = value, ["confirmationCode"] = code });

    /// <summary>每次呼叫之間清掉共用 DbContext 的追蹤（正式環境每個請求一個 DbContext）。</summary>
    protected async Task<McpFormattedResult> Call(string args, string tool = TestHighRiskTool.Name, CancellationToken ct = default)
    {
        Db.Context.ChangeTracker.Clear();
        try { return await Invoke(tool, args, ct: ct); }
        finally { Db.Context.ChangeTracker.Clear(); }
    }

    protected async Task<string> Preview(string value = "v1", string tool = TestHighRiskTool.Name)
    {
        var r = await Call(Args(value), tool);
        r.IsError.Should().BeFalse(r.Text);
        return r.Structured.GetProperty("meta").GetProperty("confirmation").GetProperty("code").GetString()!;
    }

    protected string ValuesOf(long id) => Db.Context.Set<Record>().AsNoTracking().Single(r => r.Id == id).ValuesJson;

    protected McpConfirmation ConfirmationRow(string code) =>
        Db.Context.Set<McpConfirmation>().AsNoTracking().Single(c => c.CodeHash == McpSecrets.Hash(code));

    protected McpConfirmationTicket Ticket(string code, string value = "v1", string tool = TestHighRiskTool.Name) =>
        new(code, TokenId, tool, McpArguments.Hash(JsonDocument.Parse(Args(value)).RootElement), "mcp.getRecord", null);

    protected async Task<ServiceResult> SendDirect<T>(T request) where T : IRequest<ServiceResult>
    {
        Db.Context.ChangeTracker.Clear();
        try { return await Send(request); }
        finally { Db.Context.ChangeTracker.Clear(); }
    }
}

/// <summary>確認碼機制（mcp1a 計畫判斷 18、Task 8；設計 §6.3、§13.8「確認碼」）。</summary>
[Trait("Category", "Postgres")]
public class McpConfirmationTests : McpConfirmationTestBase
{
    [Fact]
    public async Task TwoStep_Success()
    {
        var before = ValuesOf(Target);
        var preview = await Call(Args());

        preview.IsError.Should().BeFalse();
        var meta = preview.Structured.GetProperty("meta");
        var code = meta.GetProperty("confirmation").GetProperty("code").GetString()!;
        code.Should().StartWith(McpSecrets.ConfirmationPrefix);
        meta.GetProperty("confirmation").TryGetProperty("expiresAt", out _).Should().BeTrue();
        meta.GetProperty("instruction").GetString().Should().Be(McpTexts.ConfirmationInstruction);
        preview.Structured.GetProperty("data").GetProperty("preview").GetProperty("value").GetString().Should().Be("v1");
        ValuesOf(Target).Should().Be(before);
        var previewRow = (await McpRows()).Should().ContainSingle().Subject;
        (previewRow.Result, previewRow.ConfirmationOutcome).Should().Be((AuditResult.Success, AuditConfirmationOutcome.Preview));

        var done = await Call(Args(code: code));

        done.IsError.Should().BeFalse(done.Text);
        ValuesOf(Target).Should().Contain("v1");
        ConfirmationRow(code).UsedUtc.Should().NotBeNull();
        var rows = await McpRows();
        rows.Should().HaveCount(2);
        (rows[1].Result, rows[1].ConfirmationOutcome).Should().Be((AuditResult.Success, AuditConfirmationOutcome.Confirmed));
        rows[1].ConfirmationId.Should().Be(rows[0].ConfirmationId).And.NotBeNull();
        rows[1].ArgumentsHash.Should().Be(rows[0].ArgumentsHash, "確認碼不進參數雜湊");
    }

    private async Task ShouldBeMismatch(McpFormattedResult r, string code, string before)
    {
        r.IsError.Should().BeTrue();
        CodeOf(r).Should().Be(McpErrorCodes.ConfirmationInvalid);
        r.Structured.GetProperty("error").TryGetProperty("fields", out _).Should().BeFalse("不說明是哪一項不符");
        r.Structured.GetProperty("error").GetProperty("message").GetString().Should().Be("確認碼無效或已過期，請重新預覽");
        ValuesOf(Target).Should().Be(before);
        var last = (await McpRows()).Last();
        (last.Result, last.ConfirmationOutcome).Should().Be((AuditResult.Invalid, AuditConfirmationOutcome.Mismatch));
        _ = code;
    }

    [Fact]
    public async Task ArgsDiffer_Invalid()
    {
        var code = await Preview();
        var before = ValuesOf(Target);

        await ShouldBeMismatch(await Call(Args("v2", code: code)), code, before);
        ConfirmationRow(code).UsedUtc.Should().BeNull();
    }

    [Fact]
    public async Task OtherToken_Invalid()
    {
        var code = await Preview();
        var before = ValuesOf(Target);
        Channel.TokenId = 8;

        await ShouldBeMismatch(await Call(Args(code: code)), code, before);
        ConfirmationRow(code).UsedUtc.Should().BeNull();
    }

    [Fact]
    public async Task Expired_Invalid()
    {
        var code = await Preview();
        var before = ValuesOf(Target);
        Clock.Advance(TimeSpan.FromSeconds(int.Parse(PolicyCatalog.McpConfirmTtlSeconds.Factory) + 1));

        await ShouldBeMismatch(await Call(Args(code: code)), code, before);
    }

    [Fact]
    public async Task Reuse_Invalid()
    {
        var code = await Preview();
        (await Call(Args(code: code))).IsError.Should().BeFalse();
        var after = ValuesOf(Target);

        await ShouldBeMismatch(await Call(Args(code: code)), code, after);
    }

    [Fact]
    public async Task TargetVersionChanged_Invalid()
    {
        var code = await Preview();
        // 預演之後別人改了記錄（RowVersion 變）
        var record = Db.Context.Set<Record>().Single(r => r.Id == Target);
        record.RowVersion = Guid.NewGuid().ToString();
        record.ValuesJson = """{"other":1}""";
        await Db.Context.SaveChangesAsync();
        var before = ValuesOf(Target);

        await ShouldBeMismatch(await Call(Args(code: code)), code, before);
        ConfirmationRow(code).UsedUtc.Should().BeNull("要重新預演，碼不被這次失敗用掉");
        (await Call(Args(code: await Preview()))).IsError.Should().BeFalse("重新預演後可以執行");
    }

    [Fact]
    public async Task UnknownCode_Mismatch_SameMessage()
    {
        await Preview();
        var before = ValuesOf(Target);

        await ShouldBeMismatch(await Call(Args(code: McpSecrets.NewConfirmationCode())), "", before);
    }

    [Fact]
    public async Task HandlerThrowsAfterConsume_RollsBack()
    {
        var code = await Preview();
        var before = ValuesOf(Target);
        Knobs.ThrowAfterConsume = true;

        var r = await Call(Args(code: code));

        CodeOf(r).Should().Be(McpErrorCodes.InternalError);
        ValuesOf(Target).Should().Be(before);
        ConfirmationRow(code).UsedUtc.Should().BeNull();
        var last = (await McpRows()).Last();
        (last.Result, last.ReasonCode).Should().Be((AuditResult.Failed, "exception"));

        Knobs.ThrowAfterConsume = false;
        (await Call(Args(code: code))).IsError.Should().BeFalse("同一個碼可以重試成功");
        ValuesOf(Target).Should().Contain("v1");
    }

    [Fact]
    public async Task HandlerForgetsConsume_Throws()
    {
        var code = await Preview();
        var before = ValuesOf(Target);
        Knobs.SkipConsume = true;

        await FluentActions.Awaiting(() => SendDirect(new TouchRecordForTestRequest(Target, "v1", Ticket(code))))
            .Should().ThrowAsync<InvalidOperationException>();

        ValuesOf(Target).Should().Be(before);
        ConfirmationRow(code).UsedUtc.Should().BeNull();
    }

    [Fact]
    public async Task OpenLimit20()
    {
        var codes = new List<string>();
        for (var i = 0; i < McpLimits.MaxOpenConfirmations + 1; i++) codes.Add(await Preview());

        ConfirmationRow(codes[0]).VoidedUtc.Should().NotBeNull();
        var before = ValuesOf(Target);
        await ShouldBeMismatch(await Call(Args(code: codes[0])), codes[0], before);
        (await Call(Args(code: codes[^1]))).IsError.Should().BeFalse();
    }

    /// <summary>
    /// 兩個確認同時用同一碼只有一個成功。ApplicationTests 的測試主機共用一個 DbContext，無法真的並行兩個交易，
    /// 所以以確定性的交錯重現競爭：behavior 驗碼通過之後、handler 消耗之前，「另一個確認」先把碼標成已用；
    /// 這次的條件式更新影響 0 列 → 不符、記錄沒變。PostgreSQL 上並行請求另有全庫寫入鎖序列化。
    /// </summary>
    [Fact]
    public async Task ConcurrentConfirm_OnlyOne()
    {
        var code = await Preview();
        var before = ValuesOf(Target);
        var id = ConfirmationRow(code).Id;
        Knobs.BeforeConsume = db => db.Set<McpConfirmation>().Where(c => c.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UsedUtc, NowUtc));

        // 模擬的「另一個確認」在同一連線、同一交易內，回滾後它的標記也不在；要驗的是這次條件式更新沒有搶到
        await ShouldBeMismatch(await Call(Args(code: code)), code, before);
        (await McpRows()).Count(a => a.ConfirmationOutcome == AuditConfirmationOutcome.Confirmed).Should().Be(0);
    }

    [Fact]
    public async Task ConfirmationCode_NotStored()
    {
        var code = await Preview();
        await Call(Args(code: code));

        var row = ConfirmationRow(code);
        row.CodeHash.Should().Be(McpSecrets.Hash(code)).And.NotContain(code);
        var audits = Db.Context.Set<AuditLogEntry>().AsNoTracking().ToList();
        foreach (var a in audits)
            $"{a.SummaryJson}|{a.TargetLabel}|{a.TargetId}|{a.ReasonCode}|{a.ArgumentsHash}".Should().NotContain(McpSecrets.ConfirmationPrefix);
        lock (Logs) Logs.Should().NotContain(l => l.Message.Contains(McpSecrets.ConfirmationPrefix));
    }

    [Fact]
    public async Task ConfirmedAudit_Persisted()
    {
        var code = await Preview();
        await Call(Args(code: code));

        Db.Context.ChangeTracker.Clear();
        var confirmed = Db.Context.Set<AuditLogEntry>().AsNoTracking()
            .Where(a => a.ConfirmationOutcome == AuditConfirmationOutcome.Confirmed).ToList();
        confirmed.Should().ContainSingle();
        (confirmed[0].Action, confirmed[0].TokenId, confirmed[0].Channel, confirmed[0].ActorUserId)
            .Should().Be(("mcp.getRecord", TokenId, AuditChannel.Mcp, "admin"));
    }

    [Fact]
    public async Task ConfirmedAudit_SaveFails_RollsBackAll()
    {
        var code = await Preview();
        var before = ValuesOf(Target);
        Fault.On = true;

        var r = await Call(Args(code: code));

        r.IsError.Should().BeTrue();
        ValuesOf(Target).Should().Be(before);
        ConfirmationRow(code).UsedUtc.Should().BeNull();

        Fault.On = false;
        (await Call(Args(code: code))).IsError.Should().BeFalse("拿掉攔截器後同一個碼可以成功");
        ValuesOf(Target).Should().Contain("v1");
    }

    [Fact]
    public async Task Mcp_MissingTicket_Invalid()
    {
        var before = ValuesOf(Target);

        var r = await SendDirect(new TouchRecordForTestRequest(Target, "v1", null));

        r.Code.Should().Be(McpErrorCodes.ConfirmationInvalid);
        ValuesOf(Target).Should().Be(before);
    }

    [Fact]
    public async Task Ui_WithTicket_Throws()
    {
        var code = await Preview();
        Channel.Channel = RequestChannel.Ui;

        await FluentActions.Awaiting(() => SendDirect(new TouchRecordForTestRequest(Target, "v1", Ticket(code))))
            .Should().ThrowAsync<InvalidOperationException>();
        ConfirmationRow(code).UsedUtc.Should().BeNull();
    }

    [Fact]
    public async Task Ui_NoTicket_Allowed()
    {
        Channel.Channel = RequestChannel.Ui;

        (await SendDirect(new TouchRecordForTestRequest(Target, "ui", null))).IsSuccess.Should().BeTrue();
        ValuesOf(Target).Should().Contain("ui");
    }

    [Fact]
    public async Task CrossTool_Invalid()
    {
        var code = await Preview(tool: TestHighRiskTool.Name);
        var before = ValuesOf(Target);

        await ShouldBeMismatch(await Call(Args(code: code), OtherHighRiskTool.Name), code, before);
        ConfirmationRow(code).UsedUtc.Should().BeNull();
    }

    [Fact]
    public async Task OtherUser_Invalid()
    {
        var code = await Preview();
        var before = ValuesOf(Target);
        As("wenhui");

        await ShouldBeMismatch(await Call(Args(code: code)), code, before);
        ConfirmationRow(code).UsedUtc.Should().BeNull();
    }

    [Fact]
    public async Task SameScope_Sequential()
    {
        var code = await Preview();
        using var origin = AuditOrigin.Begin(AuditChannel.Mcp, TokenId);
        using var scope = NewScope();
        var invoker = scope.ServiceProvider.GetRequiredService<McpToolInvoker>();
        async Task<McpFormattedResult> Run(string args)
        {
            Db.Context.ChangeTracker.Clear();
            return await invoker.InvokeAsync(TestHighRiskTool.Name, JsonDocument.Parse(args).RootElement.Clone(), new McpCallContext(), default);
        }

        (await Run(Args(code: code))).IsError.Should().BeFalse();
        CodeOf(await Run(Args(code: code))).Should().Be(McpErrorCodes.ConfirmationInvalid);
        var fresh = await Preview();
        (await Run(Args("v3", code: fresh))).IsError.Should().BeTrue("參數不同於預演");
        (await Run(Args(code: fresh))).IsError.Should().BeFalse();
    }

    /// <summary>同一個 scope：前一次確認成功，下一次在 Send 之前就失敗 → 這次照樣寫一列 Failed（呼叫序號，第四輪必修 2）。</summary>
    [Fact]
    public async Task SameScope_CommittedThenFailsBeforeSend_WritesFailed()
    {
        var code = await Preview();
        using var origin = AuditOrigin.Begin(AuditChannel.Mcp, TokenId);
        using var scope = NewScope();
        var invoker = scope.ServiceProvider.GetRequiredService<McpToolInvoker>();
        Db.Context.ChangeTracker.Clear();
        (await invoker.InvokeAsync(TestHighRiskTool.Name, JsonDocument.Parse(Args(code: code)).RootElement.Clone(), new McpCallContext(), default))
            .IsError.Should().BeFalse();
        var rowsBefore = (await McpRows()).Count;
        Knobs.ThrowBeforeSend = true;

        Db.Context.ChangeTracker.Clear();
        var r = await invoker.InvokeAsync(TestHighRiskTool.Name, JsonDocument.Parse(Args(code: "fwc_x")).RootElement.Clone(),
            new McpCallContext(), default);

        CodeOf(r).Should().Be(McpErrorCodes.InternalError);
        var rows = await McpRows();
        rows.Should().HaveCount(rowsBefore + 1);
        (rows[^1].Result, rows[^1].ReasonCode).Should().Be((AuditResult.Failed, "exception"));
    }

    [Fact]
    public void McpConfirmedRequests_AreTransactional()
    {
        ConfirmedIn(typeof(IMcpConfirmedRequest).Assembly).Concat(ConfirmedIn(typeof(McpConfirmationTests).Assembly))
            .Where(t => !typeof(ITransactionalRequest).IsAssignableFrom(t)).Should().BeEmpty();
        ConfirmedIn(typeof(McpConfirmationTests).Assembly).Should().NotBeEmpty();
    }

    [Fact]
    public void McpConfirmedRequests_NotAudited()
    {
        static IEnumerable<Type> Violations(Assembly a) => ConfirmedIn(a).Where(t => typeof(IAuditedRequest).IsAssignableFrom(t));
        Violations(typeof(IMcpConfirmedRequest).Assembly).Should().BeEmpty();
        Violations(typeof(McpConfirmationTests).Assembly).Should().Equal(typeof(AuditedConfirmedForTestRequest));
    }

    private static IEnumerable<Type> ConfirmedIn(Assembly assembly) =>
        assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IMcpConfirmedRequest).IsAssignableFrom(t));

    [Fact]
    public async Task Confirmed_AuditedRequest_Rejected()
    {
        await FluentActions.Awaiting(() => SendDirect(new AuditedConfirmedForTestRequest(null)))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*稽核宣告*");
    }
}
