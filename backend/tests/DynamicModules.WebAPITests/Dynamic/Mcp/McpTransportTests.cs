using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.McpService;
using DynamicModules.Application.Dynamic.McpService.Tools;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Mcp;
using DynamicModules.Domain.Policies;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using static DynamicModules.WebAPITests.Dynamic.Mcp.McpTestKit;

namespace DynamicModules.WebAPITests.Dynamic.Mcp;

/// <summary>
/// <c>/mcp</c> 的傳輸與工具清單（mcp1a 計畫判斷 11、12，Task 6）：SDK client 連線、<c>tools/list</c> 依範圍過濾與快取提示、
/// 非 POST 回 405、<c>whoami</c>、不存在的工具、工具內的通道與稽核來源。
/// </summary>
[Trait("Category", "Postgres")]
public class McpTransportTests : IDisposable
{
    private TestApp _app;

    public McpTransportTests()
    {
        _app = NewApp();
        EnableMcp(_app);
    }

    public void Dispose() => _app.Dispose();

    private static JsonElement Structured(CallToolResult r) => r.StructuredContent!.Value;

    private static string TextOf(CallToolResult r) => ((TextContentBlock)r.Content.Single()).Text;

    [Fact]
    public async Task SdkClient_Connects_WithServerInstructions()
    {
        await using var client = await ConnectAsync(_app, IssueToken(_app).Raw);
        client.ServerInstructions.Should().Be(McpTexts.ServerInstructions);
        client.ServerInfo!.Name.Should().Be("formwright");
    }

    /// <summary>business.read 列出已登記的唯讀正式工具（Task 9 補齊 12 個後由 McpToolsApiTests 驗數量），每個都在正式目錄、唯讀。</summary>
    [Fact]
    public async Task ListTools_BusinessRead_ListsRegisteredReadOnlyTools()
    {
        await using var client = await ConnectAsync(_app, IssueToken(_app).Raw);
        var tools = await client.ListToolsAsync();

        // 寫入工具（business.write）不在 business.read 權杖的列表（mcp2a 判斷 14）
        var registered = _app.Services.CreateScope().ServiceProvider.GetServices<IMcpTool>()
            .Where(t => t.Descriptor.Scope is null || t.Descriptor.Scope == McpScopes.BusinessRead)
            .Select(t => t.Descriptor.Name).Order(StringComparer.Ordinal);
        tools.Select(t => t.Name).Should().Equal(registered).And.Contain("whoami");
        tools.Select(t => t.Name).Should().OnlyContain(n => McpToolCatalog.Find(n) != null);
        foreach (var tool in tools)
        {
            tool.ProtocolTool.Annotations!.ReadOnlyHint.Should().BeTrue();
            tool.ProtocolTool.Annotations.DestructiveHint.Should().BeFalse();
            tool.ProtocolTool.Annotations.OpenWorldHint.Should().BeFalse();
        }
    }

    /// <summary>權杖只有一個還沒有工具的範圍（模擬未來範圍）：只列不需範圍的 whoami。</summary>
    [Fact]
    public async Task ListTools_FutureScopeOnly_OnlyWhoami()
    {
        await using var client = await ConnectAsync(_app, IssueToken(_app, scopes: McpScopes.DesignRead).Raw);
        (await client.ListToolsAsync()).Select(t => t.Name).Should().Equal("whoami");
    }

    /// <summary>B16：ttlMs 60000、cacheScope private；工具描述不含任何模組名稱（§9.2）。</summary>
    [Fact]
    public async Task RawToolsList_CacheHints_NoModuleNames()
    {
        // 測試主機沒有 Demo 資料：先建一個有繁中名稱的模組（ApiTestKit 的名稱是「測試模組」）
        await ApiTestKit.CreateModule(_app.AsUser());
        using var res = await RawPostAsync(_app.CreateClient(), IssueToken(_app).Raw);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadAsStringAsync();
        var result = (await ReadSseMessageText(body))["result"]!;
        result["ttlMs"]!.GetValue<long>().Should().Be(McpLimits.ListToolsTtlSeconds * 1000L);
        result["cacheScope"]!.GetValue<string>().Should().Be("private");

        var names = Query(_app, db => db.Set<Module>().AsEnumerable().Select(m => m.NameI18n.Resolve("zh-TW")).Where(n => n.Length > 0).ToList());
        names.Should().NotBeEmpty("要有模組名稱可比對");
        foreach (var name in names) body.Should().NotContain(name);
    }

    private static Task<JsonObject> ReadSseMessageText(string text)
    {
        var data = text.Split('\n').Single(l => l.StartsWith("data: ", StringComparison.Ordinal));
        return Task.FromResult(JsonNode.Parse(data["data: ".Length..])!.AsObject());
    }

    [Fact]
    public async Task WhoAmI_ReturnsIdentityTokenAndLimits()
    {
        SetPolicy(_app, PolicyCatalog.McpMaxRows, "37");
        var (raw, tokenId) = IssueToken(_app);
        await using var client = await ConnectAsync(_app, raw);

        var r = await client.CallToolAsync("whoami");

        r.IsError.Should().NotBe(true);
        var s = Structured(r);
        s.GetProperty("contentTrust").GetString().Should().Be(McpTexts.ContentTrust);
        s.GetProperty("meta").GetProperty("tool").GetString().Should().Be("whoami");
        var data = s.GetProperty("data");
        data.GetProperty("userId").GetString().Should().Be(UserId);
        data.GetProperty("token").GetProperty("id").GetInt64().Should().Be(tokenId);
        data.GetProperty("token").GetProperty("scopes").EnumerateArray().Select(e => e.GetString()).Should().Equal(McpScopes.BusinessRead);
        data.GetProperty("limits").GetProperty("maxRows").GetInt32().Should().Be(37);
        data.GetProperty("limits").GetProperty("maxResultChars").GetInt32().Should().Be(McpLimits.MaxResultChars);
        var text = TextOf(r);
        text.Split('\n')[0].Should().Be(McpTexts.UntrustedNotice);
        JsonDocument.Parse(text[(text.IndexOf('\n') + 1)..]).RootElement.GetProperty("data").GetProperty("userId").GetString().Should().Be(UserId);
    }

    [Fact]
    public async Task UnknownTool_IsErrorResult_NotJsonRpcError()
    {
        await using var client = await ConnectAsync(_app, IssueToken(_app).Raw);

        var r = await client.CallToolAsync("drop_table");

        r.IsError.Should().BeTrue();
        Structured(r).GetProperty("error").GetProperty("code").GetString().Should().Be(McpErrorCodes.UnknownTool);
    }

    /// <summary>工具內 handler 看到的通道是 MCP、AuditOrigin 的權杖 id 是這把權杖（替身 handler 記下觀察值）。</summary>
    [Fact]
    public async Task Channel_IsMcp_InsideTool()
    {
        var seen = new ProbeObservations();
        Use(NewApp(services: s =>
        {
            s.AddSingleton(seen);
            s.AddTransient<IRequestHandler<GetMcpWhoAmIRequest, ServiceResult>, ProbeWhoAmIHandler>();
        }));
        EnableMcp(_app);
        var (raw, tokenId) = IssueToken(_app);
        await using var client = await ConnectAsync(_app, raw);

        (await client.CallToolAsync("whoami")).IsError.Should().NotBe(true);

        (seen.Channel, seen.TokenId, seen.AuditChannel, seen.AuditTokenId, seen.UserId)
            .Should().Be((RequestChannel.Mcp, tokenId, AuditChannel.Mcp, tokenId, UserId));
    }

    public sealed class ProbeObservations
    {
        public RequestChannel Channel;
        public long? TokenId, AuditTokenId;
        public AuditChannel AuditChannel;
        public string? UserId;
    }

    public sealed class ProbeWhoAmIHandler(ProbeObservations seen, IRequestChannel channel, ICurrentUser user)
        : IRequestHandler<GetMcpWhoAmIRequest, ServiceResult>
    {
        public Task<ServiceResult> Handle(GetMcpWhoAmIRequest request, CancellationToken ct)
        {
            (seen.Channel, seen.TokenId, seen.AuditChannel, seen.AuditTokenId, seen.UserId) =
                (channel.Channel, channel.TokenId, AuditOrigin.Channel, AuditOrigin.TokenId, user.UserId);
            return Task.FromResult(ServiceResult.Success(new McpWhoAmIResponse(user.UserId, null,
                new McpWhoAmIToken(channel.TokenId ?? 0, "probe", channel.Scopes, DateTime.UtcNow), new McpWhoAmILimits(1, 1, 1, 1), "probe")));
        }
    }

    /// <summary>
    /// 兩把不同使用者的權杖並行呼叫，工具內用屏障等兩邊都進來才往下（請求確定重疊）；屏障 await 前後各記一次身分，
    /// 兩邊都只看到自己的使用者、權杖 id 與稽核來源（scoped 身分與 AsyncLocal 不串線）。
    /// </summary>
    [Fact]
    public async Task ConcurrentClients_IdentityIsolated_AcrossAwait()
    {
        var barrier = new BarrierState(2);
        Use(NewApp(services: s =>
        {
            s.AddSingleton(barrier);
            s.AddScoped<IMcpTool, BarrierProbeTool>();
        }));
        EnableMcp(_app);
        var (rawA, idA) = IssueToken(_app);
        var (rawB, idB) = IssueToken(_app, OtherUserId);
        await using var a = await ConnectAsync(_app, rawA);
        await using var b = await ConnectAsync(_app, rawB);

        var results = await Task.WhenAll(a.CallToolAsync(BarrierProbeTool.Name).AsTask(), b.CallToolAsync(BarrierProbeTool.Name).AsTask());

        barrier.Overlapped.Should().BeTrue("兩個請求同時在工具內");
        foreach (var (r, user, token) in new[] { (results[0], UserId, idA), (results[1], OtherUserId, idB) })
        {
            r.IsError.Should().NotBe(true, TextOf(r));
            var data = Structured(r).GetProperty("data");
            foreach (var phase in new[] { "before", "after" })
            {
                var seen = data.GetProperty(phase);
                (seen.GetProperty("userId").GetString(), seen.GetProperty("tokenId").GetInt64(), seen.GetProperty("auditTokenId").GetInt64())
                    .Should().Be((user, token, token), phase);
            }
        }
    }

    /// <summary>補充 D：兩個 client 交錯並行各呼叫 20 次，每次 await 前後看到的都只有自己那把的身分與權杖（審查必修 4）。</summary>
    [Fact]
    public async Task ConcurrentClients_Twenty_Each_NeverCross()
    {
        Use(NewApp(services: s =>
        {
            s.AddSingleton(new BarrierState(1));
            s.AddScoped<IMcpTool, BarrierProbeTool>();
        }));
        EnableMcp(_app);
        var (rawA, idA) = IssueToken(_app);
        var (rawB, idB) = IssueToken(_app, OtherUserId);
        await using var a = await ConnectAsync(_app, rawA);
        await using var b = await ConnectAsync(_app, rawB);

        // 同一 IP 進行中的認證嘗試也計入降速（15 分鐘 20 次，寫死清單）；40 個同時送會被當成猜測擋下 429，所以同時最多 8 個
        using var gate = new SemaphoreSlim(8);
        async Task Check(McpClient client, string user, long token)
        {
            await gate.WaitAsync();
            CallToolResult r;
            try { r = await client.CallToolAsync(BarrierProbeTool.Name); }
            finally { gate.Release(); }
            r.IsError.Should().NotBe(true, TextOf(r));
            var data = Structured(r).GetProperty("data");
            foreach (var phase in new[] { "before", "after" })
            {
                var seen = data.GetProperty(phase);
                (seen.GetProperty("userId").GetString(), seen.GetProperty("tokenId").GetInt64(), seen.GetProperty("auditTokenId").GetInt64())
                    .Should().Be((user, token, token), phase);
            }
        }

        await Task.WhenAll(Enumerable.Range(0, 20).SelectMany(_ => new[]
        {
            Task.Run(() => Check(a, UserId, idA)), Task.Run(() => Check(b, OtherUserId, idB)),
        }));
    }

    /// <summary>經 SDK 呼叫工具成功 → 執行器真的以 WriteNowAsync 寫稽核 → 資料庫的那一列行為者、管道、權杖都是這把權杖（審查必修 3）。</summary>
    [Fact]
    public async Task SdkCall_AuditRow_HasTokenIdentity()
    {
        var (raw, tokenId) = IssueToken(_app, OtherUserId);
        await using var client = await ConnectAsync(_app, raw);

        (await client.CallToolAsync("whoami")).IsError.Should().NotBe(true);

        var row = Query(_app, db => db.Set<AuditLogEntry>().Where(a => a.Action == "mcp.whoami").ToList()).Should().ContainSingle().Subject;
        (row.ActorUserId, row.ActorKind, row.Channel, row.TokenId, row.Result, row.TargetLabel)
            .Should().Be((OtherUserId, AuditActorKind.User, AuditChannel.Mcp, tokenId, AuditResult.Success, "whoami"));
    }

    public sealed class BarrierState(int parties)
    {
        private int _arrived;
        private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Overlapped => _all.Task.IsCompletedSuccessfully;

        public Task ArriveAndWaitAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref _arrived) == parties) _all.TrySetResult();
            return _all.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        }
    }

    /// <summary>測試專用工具（不需範圍）：屏障前後記下 scoped 身分與稽核來源。</summary>
    public sealed class BarrierProbeTool(BarrierState barrier, ICurrentUser user, IRequestChannel channel) : IMcpTool
    {
        public const string Name = "probe_barrier";
        public McpToolDescriptor Descriptor { get; } = new(Name, "mcp.whoami", null, McpRisk.Read);
        public string InputSchemaJson => McpTexts.NoArgumentsSchema;
        public string Description => "test probe";

        public async Task<McpToolOutcome> RunAsync(McpToolCall call, CancellationToken ct)
        {
            object Seen() => new { userId = user.UserId, tokenId = channel.TokenId, auditTokenId = AuditOrigin.TokenId };
            var before = Seen();
            await barrier.ArriveAndWaitAsync(ct);
            await Task.Yield();
            return McpToolOutcome.Ok(new { before, after = Seen() });
        }
    }

    private void Use(TestApp app)
    {
        _app.Dispose();
        _app = app;
    }

    /// <summary>B19：正式主機上，通過閘門的 GET /mcp 回 405、Allow: POST。</summary>
    [Fact]
    public async Task GetMcp_WithToken_405()
    {
        var client = _app.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {IssueToken(_app).Raw}");

        using var res = await client.GetAsync("/mcp");

        res.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        res.Content.Headers.Allow.Should().Equal("POST");
    }

    /// <summary>
    /// B19（Task 0 探針搬來）：有 wwwroot 的主機照 Program.cs 的順序（MapFormwrightMcp 之後才是 UseStaticFiles、/api 404、MapFallbackToFile），
    /// GET /mcp 不掉進 SPA 的 fallback，回 405。TestApp 沒有 wwwroot，另起最小主機實測。
    /// </summary>
    [Fact]
    public async Task GetMcp_WithSpaFallback_IsNotServedBySpa()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dm-mcp-spa-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "wwwroot"));
            File.WriteAllText(Path.Combine(root, "wwwroot", "index.html"), "<html>spa</html>");
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root, WebRootPath = Path.Combine(root, "wwwroot") });
            builder.WebHost.UseTestServer();
            builder.Services.AddMcpServer().WithHttpTransport(o => o.Stateless = true);
            await using var host = builder.Build();
            DynamicModules.WebAPI.Mcp.McpEndpoints.MapFormwrightMcp(host);
            host.UseDefaultFiles();
            host.UseStaticFiles();
            host.Map("/api/{**rest}", () => Results.NotFound());
            host.MapFallbackToFile("index.html");
            await host.StartAsync();
            var client = host.GetTestClient();

            using var get = await client.GetAsync("/mcp");
            using var delete = await client.DeleteAsync("/mcp");
            using var spa = await client.GetAsync("/some/page");

            spa.Content.Headers.ContentType!.MediaType.Should().Be("text/html", "對照組：其他路徑照常交給 SPA");
            get.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
            delete.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// 只有閘門會產生 MCP 權杖身分（判斷 9）：claim 字串常值只出現在 McpClaims，發出這些 claim（<c>new Claim(McpClaims.…</c>）只在 McpGateMiddleware。
    /// cookie 登入與 REST 都不會發，HttpRequestChannel 讀到 MCP 通道就代表真的經過權杖認證。
    /// </summary>
    [Fact]
    public void ClaimStrings_OnlyInMcpClaimsAndGate()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DynamicModules.sln"))) dir = dir.Parent;
        var sep = Path.DirectorySeparatorChar;
        var files = Directory.GetFiles(Path.Combine(dir!.FullName, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}obj{sep}") && !f.Contains($"{sep}bin{sep}"))
            .Select(f => (Name: Path.GetFileName(f), Text: File.ReadAllText(f))).ToList();

        files.Where(f => f.Text.Contains("\"fw:")).Select(f => f.Name).Should().Equal("McpClaims.cs");
        files.Where(f => f.Text.Contains("new Claim(McpClaims.")).Select(f => f.Name).Should().Equal("McpGateMiddleware.cs");
    }
}
