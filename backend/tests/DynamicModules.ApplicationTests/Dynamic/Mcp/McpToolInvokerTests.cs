using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.McpService;
using DynamicModules.Application.Dynamic.McpService.Tools;
using DynamicModules.ApplicationTests.Common;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Mcp;
using DynamicModules.Domain.Policies;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DynamicModules.ApplicationTests.Dynamic.Mcp;

/// <summary>可設定的 MCP 通道（權杖 id 與範圍）。</summary>
public sealed class SettableMcpChannel : IRequestChannel
{
    public RequestChannel Channel { get; set; } = RequestChannel.Mcp;
    public long? TokenId { get; set; } = 7;
    public IReadOnlyList<string> Scopes { get; set; } = [McpScopes.BusinessRead];
}

/// <summary>假工具：預設回一筆成功結果，行為可換（卡住、丟例外、回失敗）。</summary>
public sealed class FakeTool(string name, string auditCode = "mcp.queryRecords", string? scope = McpScopes.BusinessRead) : IMcpTool
{
    public McpToolDescriptor Descriptor { get; } = new(name, auditCode, scope, McpRisk.Read);
    public string InputSchemaJson => "{}";
    public string Description => "fake";
    public Func<McpToolCall, CancellationToken, Task<McpToolOutcome>> Behavior { get; set; } =
        (_, _) => Task.FromResult(McpToolOutcome.Ok(new { ok = true }));
    private int _runs;
    public int Runs => Volatile.Read(ref _runs);

    public Task<McpToolOutcome> RunAsync(McpToolCall call, CancellationToken ct)
    {
        Interlocked.Increment(ref _runs);
        return Behavior(call, ct);
    }
}

/// <summary>執行器測試共用：通道是 MCP（權杖 7、business.read），假工具 fake_list（business.read）與 fake_design（design.read）。</summary>
public abstract class McpInvokerTestBase : McpTokenTestBase
{
    protected const long TokenId = 7;
    protected readonly SettableMcpChannel Channel = new();
    protected readonly FakeTool List = new("fake_list");
    protected readonly FakeTool Design = new("fake_design", "mcp.describeModule", McpScopes.DesignRead);

    protected readonly SwitchableAudit Audit = new();
    protected readonly List<(LogLevel Level, string Message)> Logs = [];
    protected readonly SettableLocale Locale = new();

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        services.AddSingleton<IRequestChannel>(Channel);
        services.AddSingleton<IMcpTool>(List);
        services.AddSingleton<IMcpTool>(Design);
        services.AddScoped<AuditLog>();
        services.AddScoped<IAuditLog>(sp => new SwitchableAuditLog(sp.GetRequiredService<AuditLog>(), Audit));
        services.AddSingleton<ILogger<McpToolInvoker>>(new CaptureLogger<McpToolInvoker>(Logs));
        services.AddSingleton<ICurrentLocale>(Locale);
    }

    protected async Task<McpFormattedResult> Invoke(string name = "fake_list", string args = "{}", McpCallContext? context = null,
        CancellationToken ct = default)
    {
        // 閘門在 /mcp 換身分時開的稽核來源（通道 MCP、權杖 id）
        using var origin = AuditOrigin.Begin(AuditChannel.Mcp, TokenId);
        using var scope = NewScope();
        return await scope.ServiceProvider.GetRequiredService<McpToolInvoker>()
            .InvokeAsync(name, JsonDocument.Parse(args).RootElement.Clone(), context ?? new McpCallContext("test-client", "1.0"), ct);
    }

    /// <summary>執行器寫的稽核列（權杖 7、MCP 通道；不含測試直接寫的已提交列）。</summary>
    protected Task<List<AuditLogEntry>> McpRows() =>
        Db.Context.Set<AuditLogEntry>().AsNoTracking().Where(a => a.TokenId == TokenId && a.Channel == AuditChannel.Mcp && a.TargetLabel != null)
            .OrderBy(a => a.Id).ToListAsync();

    protected static string? CodeOf(McpFormattedResult r) =>
        r.Structured.TryGetProperty("error", out var e) ? e.GetProperty("code").GetString() : null;

    /// <summary>直接寫稽核列（模擬已提交的呼叫）。</summary>
    protected async Task AddCalls(int n, string action = "mcp.queryRecords", DateTime? atUtc = null, long tokenId = TokenId)
    {
        for (var i = 0; i < n; i++)
            Db.Context.Add(new AuditLogEntry
            {
                OccurredUtc = atUtc ?? NowUtc, Action = action, RiskLevel = AuditRiskLevel.Read, Result = AuditResult.Success,
                Channel = AuditChannel.Mcp, ActorKind = AuditActorKind.User, ActorUserId = "wenhui", TokenId = tokenId,
            });
        await Db.Context.SaveChangesAsync();
    }

    /// <summary>營運時區今天 00:00（UTC）：時鐘是 2026-11-04 09:00 +08:00。</summary>
    protected static readonly DateTime TodayStartUtc = new(2026, 11, 3, 16, 0, 0, DateTimeKind.Utc);
}

/// <summary>可切換成寫不進去的稽核（包住真的 AuditLog）。</summary>
public sealed class SwitchableAudit
{
    public bool Fail { get; set; }
    /// <summary>每次 <c>WriteNowAsync</c> 進來、交給真的稽核之前呼叫（例如在稽核提交途中取消請求）。</summary>
    public Action? BeforeWrite { get; set; }
}

public sealed class SwitchableAuditLog(AuditLog inner, SwitchableAudit state) : IAuditLog
{
    public AuditLogEntry Add(AuditEntry entry) => inner.Add(entry);

    public Task WriteNowAsync(AuditEntry entry, CancellationToken ct)
    {
        state.BeforeWrite?.Invoke();
        return state.Fail ? throw new AuditWriteException(new InvalidOperationException("db down")) : inner.WriteNowAsync(entry, ct);
    }

    public Task<bool> TryWriteNowAsync(AuditEntry entry, CancellationToken ct) =>
        state.Fail ? Task.FromResult(false) : inner.TryWriteNowAsync(entry, ct);
}

public sealed class SettableLocale : ICurrentLocale { public string Locale { get; set; } = "zh-TW"; }

public sealed class CaptureLogger<T>(List<(LogLevel Level, string Message)> sink) : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (sink) sink.Add((logLevel, formatter(state, exception) + (exception is null ? "" : " | " + exception.Message)));
    }
}

/// <summary>工具執行器：範圍、速率與每日名額保留（mcp1a 計畫判斷 13、19，Task 7）。</summary>
[Trait("Category", "Postgres")]
public class McpToolInvokerTests : McpInvokerTestBase
{
    [Fact]
    public async Task Success_FormatsMetaAndData()
    {
        var r = await Invoke();
        r.IsError.Should().BeFalse();
        r.Structured.GetProperty("meta").GetProperty("tool").GetString().Should().Be("fake_list");
        r.Structured.GetProperty("data").GetProperty("ok").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task ScopeDenied()
    {
        var r = await Invoke("fake_design");
        CodeOf(r).Should().Be(McpErrorCodes.ScopeDenied);
        Design.Runs.Should().Be(0);
    }

    [Fact]
    public async Task Channel_NotMcp_Throws()
    {
        Channel.Channel = RequestChannel.Ui;
        await FluentActions.Awaiting(() => Invoke()).Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task PageSize_CappedByMaxRows()
    {
        await SetSystem(PolicyCatalog.McpMaxRows, "50");
        int? received = null;
        List.Behavior = (call, _) =>
        {
            var args = McpArgs.Of(call.Arguments);
            received = args.PageSize(toolMax: 1000, call.Limits.MaxRows);
            return Task.FromResult(McpToolOutcome.Ok(new { }, new { pageSize = received }));
        };

        var r = await Invoke(args: """{"pageSize":500}""");

        received.Should().Be(50);
        r.Structured.GetProperty("meta").GetProperty("pageSize").GetInt32().Should().Be(50);
    }

    [Fact]
    public async Task RateLimit_Minute()
    {
        await SetSystem(PolicyCatalog.McpCallsPerMinute, "3");
        for (var i = 0; i < 3; i++) (await Invoke()).IsError.Should().BeFalse();

        var blocked = await Invoke();
        CodeOf(blocked).Should().Be(McpErrorCodes.RateLimited);
        blocked.Structured.GetProperty("error").GetProperty("retryAfterSeconds").GetInt32().Should().Be(60, "時鐘在整分，下一窗是 60 秒後");
        List.Runs.Should().Be(3);

        Clock.Advance(TimeSpan.FromSeconds(60));
        (await Invoke()).IsError.Should().BeFalse();
    }

    [Fact]
    public async Task RateLimit_PolicyChange_NextCall()
    {
        await SetSystem(PolicyCatalog.McpCallsPerMinute, "1");
        (await Invoke()).IsError.Should().BeFalse();
        CodeOf(await Invoke()).Should().Be(McpErrorCodes.RateLimited);

        await SetSystem(PolicyCatalog.McpCallsPerMinute, "2");
        (await Invoke()).IsError.Should().BeFalse();
    }

    /// <summary>今天（營運時區）的工具呼叫才算；昨天的、mcp.access、mcp.token.* 不算。</summary>
    [Fact]
    public async Task RateLimit_Day()
    {
        await SetSystem(PolicyCatalog.McpCallsPerDay, "100");
        await AddCalls(98);
        await AddCalls(1, "mcp.unknownTool");
        await AddCalls(5, atUtc: TodayStartUtc.AddMinutes(-1));
        await AddCalls(5, "mcp.access");
        await AddCalls(1, "mcp.token.create");
        await AddCalls(3, tokenId: 8);
        (await Invoke()).IsError.Should().BeFalse("今天只有 99 筆計入");

        await AddCalls(1);
        var blocked = await Invoke();
        CodeOf(blocked).Should().Be(McpErrorCodes.RateLimited);
        blocked.Structured.GetProperty("error").GetProperty("retryAfterSeconds").GetInt32().Should().Be(15 * 3600, "到營運時區明天 00:00");
    }

    /// <summary>剩一個名額，兩個呼叫並行：A 放行後卡在工具內，B 被擋；全程恰一個執行。</summary>
    [Fact]
    public async Task RateLimit_Day_LastSlot_Concurrent()
    {
        await SetSystem(PolicyCatalog.McpCallsPerDay, "100");
        await AddCalls(99);
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        List.Behavior = async (_, _) =>
        {
            entered.SetResult();
            await release.Task;
            return McpToolOutcome.Ok(new { });
        };

        var a = Invoke();
        await entered.Task;
        var b = await Invoke();
        release.SetResult();

        (await a).IsError.Should().BeFalse();
        CodeOf(b).Should().Be(McpErrorCodes.RateLimited);
        List.Runs.Should().Be(1);
    }

    /// <summary>新建一個 McpRateGate（模擬重啟）：進行中的保留歸零，但已提交的稽核列還在，照樣擋。</summary>
    [Fact]
    public async Task RateLimit_Day_AfterRestart()
    {
        await AddCalls(100);
        using var scope = NewScope();
        var usage = scope.ServiceProvider.GetRequiredService<McpUsage>();

        var d = await new McpRateGate(Clock).TryReserveAsync(TokenId, 60, 100, c => usage.CountTodayAsync(TokenId, c), CancellationToken.None);

        (d.Allowed, d.Denial).Should().Be((false, McpRateDenial.Day));
    }

    [Fact]
    public async Task Reservation_ReleasedOnException()
    {
        await SetSystem(PolicyCatalog.McpCallsPerDay, "100");
        await AddCalls(98);   // 丟例外的那次也寫一列 Failed 稽核、計入今天；名額沒釋放的話下一次會被擋
        List.Behavior = (_, _) => throw new InvalidOperationException("boom");
        CodeOf(await Invoke()).Should().Be(McpErrorCodes.InternalError);

        List.Behavior = (_, _) => Task.FromResult(McpToolOutcome.Ok(new { }));
        (await Invoke()).IsError.Should().BeFalse("例外後名額已釋放");
    }
}

/// <summary>工具執行器的稽核結束路徑（判斷 17：每次呼叫恰好一列）與結果大小（判斷 14）。</summary>
[Trait("Category", "Postgres")]
public class McpToolInvokerAuditTests : McpInvokerTestBase
{
    private async Task<AuditLogEntry> SingleRow()
    {
        var rows = await McpRows();
        rows.Should().ContainSingle();
        return rows[0];
    }

    [Fact]
    public async Task Success_WritesAudit()
    {
        const string args = """{"module":"orders","keyword":"王小明","filter":{"all":[{"field":"amount","op":"gt","value":12345}]},"confirmationCode":"fwc_x"}""";
        List.Behavior = (_, _) => Task.FromResult(McpToolOutcome.Ok(new { rows = 3 }) with
        {
            TargetKind = "module", TargetId = "orders", ResultCount = 3, RecordIds = Enumerable.Range(1, 25).Select(i => (long)i).ToList(),
        });

        var r = await Invoke(args: args, context: new McpCallContext(new string('c', 150), "1.2", "nightly"));

        r.IsError.Should().BeFalse();
        var row = await SingleRow();
        (row.Action, row.TargetLabel, row.Result, row.TargetKind, row.TargetId, row.ResultCount)
            .Should().Be(("mcp.queryRecords", "fake_list", AuditResult.Success, "module", "orders", 3));
        row.ArgumentsHash.Should().Be(McpArguments.Hash(JsonDocument.Parse(args).RootElement));
        var summary = JsonDocument.Parse(row.SummaryJson!).RootElement;
        summary.GetProperty("ids").GetArrayLength().Should().Be(20);
        summary.GetProperty("client").GetProperty("name").GetString().Should().HaveLength(100);
        summary.GetProperty("job").GetString().Should().Be("nightly");
        summary.TryGetProperty("ms", out _).Should().BeTrue();
        row.SummaryJson.Should().NotContain("12345").And.NotContain("王小明").And.NotContain("fwc_x");
    }

    [Fact]
    public async Task UnknownTool_Audited()
    {
        CodeOf(await Invoke("no_such_tool")).Should().Be(McpErrorCodes.UnknownTool);
        var row = await SingleRow();
        (row.Action, row.Result, row.TargetLabel, row.ReasonCode).Should().Be(("mcp.unknownTool", AuditResult.Invalid, "no_such_tool", "unknown_tool"));
    }

    [Fact]
    public async Task ToolThrows_AuditedFailed_InternalError()
    {
        List.Behavior = (_, _) => throw new InvalidOperationException("secret-detail");

        var r = await Invoke();

        CodeOf(r).Should().Be(McpErrorCodes.InternalError);
        r.Text.Should().NotContain("secret-detail");
        var row = await SingleRow();
        (row.Result, row.ReasonCode).Should().Be((AuditResult.Failed, "exception"));
        Logs.Count(l => l.Level == LogLevel.Error).Should().Be(1);
    }

    [Fact]
    public async Task Cancelled_AuditedFailed()
    {
        using var cts = new CancellationTokenSource();
        List.Behavior = async (_, ct) =>
        {
            cts.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            return McpToolOutcome.Ok(new { });
        };

        await FluentActions.Awaiting(() => Invoke(ct: cts.Token)).Should().ThrowAsync<OperationCanceledException>();

        var row = await SingleRow();
        (row.Result, row.ReasonCode).Should().Be((AuditResult.Failed, "cancelled"));
    }

    [Fact]
    public async Task Cancelled_ToolReturnsNormally_StillOneRow()
    {
        // 工具看到取消後照常回傳（不丟例外）：稽核寫入不能被同一個 ct 中斷（審查必修 1）
        using var cts = new CancellationTokenSource();
        List.Behavior = (_, _) =>
        {
            cts.Cancel();
            return Task.FromResult(McpToolOutcome.Ok(new { ok = true }));
        };

        await FluentActions.Awaiting(() => Invoke(ct: cts.Token)).Should().ThrowAsync<OperationCanceledException>();

        (await McpRows()).Should().ContainSingle();
    }

    [Fact]
    public async Task Cancelled_DuringAuditCommit_StillOneRow()
    {
        // 格式化之後、稽核提交途中才取消：稽核仍要寫進去，名額才不會白白釋放（審查必修 1）
        using var cts = new CancellationTokenSource();
        Audit.BeforeWrite = cts.Cancel;

        await FluentActions.Awaiting(() => Invoke(ct: cts.Token)).Should().ThrowAsync<OperationCanceledException>();

        var row = (await McpRows()).Should().ContainSingle().Subject;
        row.Result.Should().Be(AuditResult.Success);
    }

    [Fact]
    public async Task ErrorResult_CappedAtMaxResultChars()
    {
        // 錯誤結果也受單次結果上限（審查建議）：handler 回的超長驗證訊息不能原樣送出
        var longText = new string('x', McpLimits.MaxResultChars);
        List.Behavior = (_, _) => Task.FromResult(McpToolOutcome.From(ServiceResult.Invalid(
            new Dictionary<string, string[]> { ["a"] = [longText], ["b"] = [longText] })));

        var r = await Invoke();

        r.IsError.Should().BeTrue();
        r.Size.Should().BeLessThanOrEqualTo(McpLimits.MaxResultChars);
        CodeOf(r).Should().Be(McpErrorCodes.Invalid);
        (await SingleRow()).Result.Should().Be(AuditResult.Invalid);
    }

    [Fact]
    public async Task ResultTooLarge_AuditedFailed()
    {
        List.Behavior = (_, _) => Task.FromResult(McpToolOutcome.Ok(new { text = new string('x', McpLimits.MaxResultChars) }));

        CodeOf(await Invoke()).Should().Be(McpErrorCodes.ResultTooLarge);
        var row = await SingleRow();
        (row.Result, row.ReasonCode).Should().Be((AuditResult.Failed, "result_too_large"));
    }

    [Fact]
    public async Task AuditFails_NoData()
    {
        Audit.Fail = true;

        var r = await Invoke();

        r.IsError.Should().BeTrue();
        CodeOf(r).Should().Be(McpErrorCodes.AuditUnavailable);
        r.Structured.TryGetProperty("data", out _).Should().BeFalse();
        Logs.Count(l => l.Level == LogLevel.Error).Should().Be(1);
        List.Runs.Should().Be(1);
    }

    [Fact]
    public async Task ScopeDenied_Audited()
    {
        await Invoke("fake_design");
        var row = await SingleRow();
        (row.Action, row.Result, row.ReasonCode).Should().Be(("mcp.describeModule", AuditResult.Denied, "scope_denied"));
    }

    [Fact]
    public async Task InvalidArgs_Audited()
    {
        List.Behavior = (call, _) =>
        {
            var a = McpArgs.Of(call.Arguments);
            a.PageSize(toolMax: 100, call.Limits.MaxRows);
            return Task.FromResult(a.Invalid());
        };

        var r = await Invoke(args: """{"pageSize":0}""");

        CodeOf(r).Should().Be(McpErrorCodes.InvalidArguments);
        r.Structured.GetProperty("error").GetProperty("fields").TryGetProperty("pageSize", out _).Should().BeTrue();
        var row = await SingleRow();
        (row.Result, row.ReasonCode).Should().Be((AuditResult.Invalid, "invalid_arguments"));
    }

    [Fact]
    public async Task Forbidden_NoExistenceLeak()
    {
        List.Behavior = (_, _) => Task.FromResult(McpToolOutcome.From(ServiceResult.Forbidden(MessageRef.Literal("模組 secret-orders 不開放"))));
        var forbidden = await Invoke();
        List.Behavior = (_, _) => Task.FromResult(McpToolOutcome.From(ServiceResult.NotFound(MessageRef.Literal("找不到 secret-orders"))));
        var notFound = await Invoke();

        (CodeOf(forbidden), Message(forbidden)).Should().Be((McpErrorCodes.Forbidden, "沒有權限執行此操作"));
        (CodeOf(notFound), Message(notFound)).Should().Be((McpErrorCodes.NotFound, "找不到資源"));
        forbidden.Text.Should().NotContain("secret-orders");
        notFound.Text.Should().NotContain("secret-orders");
        (await McpRows()).Select(r => (r.Result, r.ReasonCode)).Should()
            .Equal((AuditResult.Denied, "forbidden"), (AuditResult.Invalid, "not_found"));
    }

    [Fact]
    public async Task Conflict_Stale()
    {
        List.Behavior = (_, _) => Task.FromResult(McpToolOutcome.From(ServiceResult.Stale()));
        CodeOf(await Invoke()).Should().Be(McpErrorCodes.Stale);
        (await SingleRow()).Result.Should().Be(AuditResult.Invalid);
    }

    [Fact]
    public async Task RateLimited_AuditedDenied()
    {
        await SetSystem(PolicyCatalog.McpCallsPerMinute, "1");
        await Invoke();
        await Invoke();
        (await McpRows()).Select(r => (r.Result, r.ReasonCode)).Should().Equal((AuditResult.Success, null), (AuditResult.Denied, "rate_limited"));
    }

    [Fact]
    public async Task ErrorMessage_FollowsRequestLocale()
    {
        Locale.Locale = "en-US";
        Message(await Invoke("no_such_tool")).Should().Be("There is no such tool");
    }

    private static string? Message(McpFormattedResult r) => r.Structured.GetProperty("error").GetProperty("message").GetString();
}

/// <summary>McpRateGate 的同步契約（判斷 13 第三、四輪必修）：清理不拆分保留、讀取順序、釋放冪等。已提交數用可暫停的假實作。</summary>
public class McpRateGateTests
{
    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 11, 4, 0, 59, 30, TimeSpan.Zero));
    private const long A = 1, Other = 2;
    private int _committed;

    private McpRateGate NewGate() => new(_clock);

    private Task<McpRateDecision> Reserve(McpRateGate gate, long token = A, int perMinute = 60, Func<CancellationToken, Task<int>>? count = null) =>
        gate.TryReserveAsync(token, perMinute, 100, count ?? (_ => Task.FromResult(token == A ? _committed : 0)), CancellationToken.None);

    /// <summary>第四輪必修 1：B 先擷取 Reserved 再查已提交數；A 在兩者之間提交並釋放，B 仍被擋（只剩一個名額）。</summary>
    [Fact]
    public async Task ReadOrder_SnapshotReservedBeforeCount()
    {
        var gate = NewGate();
        _committed = 99;
        var a = await Reserve(gate);
        a.Allowed.Should().BeTrue();

        var counting = new TaskCompletionSource();
        var resume = new TaskCompletionSource();
        var b = Reserve(gate, count: async _ =>
        {
            var seen = _committed;   // A 提交前讀到 99
            counting.SetResult();
            await resume.Task;
            return seen;
        });
        await counting.Task;
        _committed = 100;            // A 提交稽核
        a.Reservation!.Release();    // 並釋放保留
        resume.SetResult();

        (await b).Allowed.Should().BeFalse();
    }

    /// <summary>A 放行後卡在工具內；窗過了、到了掃描時間，清理不移除 A 的項目，同一把權杖的 B 仍被擋。放開 A、提交後再掃就移除，重建後照樣擋。</summary>
    [Fact]
    public async Task Cleanup_WhileToolStuck_NoSplit_ThenRemovedAfterRelease()
    {
        var gate = NewGate();
        _committed = 99;
        var a = await Reserve(gate);
        a.Allowed.Should().BeTrue();

        _clock.Advance(TimeSpan.FromMinutes(2));
        (await Reserve(gate, Other)).Reservation!.Release();
        gate.TrackedTokenCount.Should().Be(2, "A 還拿著保留，不移除");
        (await Reserve(gate)).Allowed.Should().BeFalse("保留沒有被拆成兩份");

        _committed = 100;
        a.Reservation!.Release();
        _clock.Advance(TimeSpan.FromMinutes(2));
        (await Reserve(gate, Other)).Reservation!.Release();
        gate.TrackedTokenCount.Should().Be(1, "A 已釋放、窗已過，移除");

        (await Reserve(gate)).Allowed.Should().BeFalse("重建後已提交數來自稽核表");
    }

    /// <summary>A 在 semaphore 內卡在讀已提交數，B 在等 semaphore；清理不移除；放開後合計放行數不超過剩餘名額。</summary>
    [Fact]
    public async Task Cleanup_KeepsWaiter()
    {
        var gate = NewGate();
        _committed = 99;
        var counting = new TaskCompletionSource();
        var resume = new TaskCompletionSource();
        var a = Reserve(gate, count: async _ =>
        {
            counting.TrySetResult();
            await resume.Task;
            return _committed;
        });
        await counting.Task;
        var b = Reserve(gate);

        _clock.Advance(TimeSpan.FromMinutes(2));
        (await Reserve(gate, Other)).Reservation!.Release();
        gate.TrackedTokenCount.Should().Be(2);

        resume.SetResult();
        var results = await Task.WhenAll(a, b);
        results.Count(r => r.Allowed).Should().Be(1);
    }

    /// <summary>沒有使用者但每分鐘窗還沒過：不移除，同一分鐘內的次數照算。</summary>
    [Fact]
    public async Task Cleanup_WindowNotOver_Kept()
    {
        var gate = NewGate();
        (await Reserve(gate, Other)).Reservation!.Release();      // 00:59:30 掃過一次
        _clock.Advance(TimeSpan.FromSeconds(30));                  // 01:00:00
        (await Reserve(gate, perMinute: 1)).Reservation!.Release();
        _clock.Advance(TimeSpan.FromSeconds(31));                  // 01:00:31，距上次掃描 61 秒
        (await Reserve(gate, Other)).Reservation!.Release();

        gate.TrackedTokenCount.Should().Be(2);
        var d = await Reserve(gate, perMinute: 1);
        (d.Allowed, d.Denial, d.RetryAfterSeconds).Should().Be((false, McpRateDenial.Minute, 29));
    }

    [Fact]
    public async Task Release_Idempotent()
    {
        var gate = NewGate();
        _committed = 99;
        var first = await Reserve(gate);
        first.Reservation!.Release();
        first.Reservation.Release();

        var second = await Reserve(gate);
        second.Allowed.Should().BeTrue();
        (await Reserve(gate)).Allowed.Should().BeFalse("第二次釋放沒有把保留數減成負的");
    }

    [Fact]
    public async Task Cancelled_WhileWaiting_ReleasesUser()
    {
        var gate = NewGate();
        var counting = new TaskCompletionSource();
        var resume = new TaskCompletionSource();
        var a = Reserve(gate, count: async _ =>
        {
            counting.SetResult();
            await resume.Task;
            return 0;
        });
        await counting.Task;
        using var cts = new CancellationTokenSource();
        var b = gate.TryReserveAsync(A, 60, 100, _ => Task.FromResult(0), cts.Token);
        cts.Cancel();
        await FluentActions.Awaiting(() => b).Should().ThrowAsync<OperationCanceledException>();
        resume.SetResult();
        (await a).Reservation!.Release();

        _clock.Advance(TimeSpan.FromMinutes(2));
        (await Reserve(gate, Other)).Reservation!.Release();
        gate.TrackedTokenCount.Should().Be(1, "取消的等待者也減掉了使用數，A 的項目可以移除");
    }
}
