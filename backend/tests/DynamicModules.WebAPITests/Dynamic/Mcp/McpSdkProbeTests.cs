using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Security;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.WebAPI.Mcp;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MediatR;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DynamicModules.WebAPITests.Dynamic.Mcp;

/// <summary>
/// MCP C# SDK 2.4.0 的探針（mcp1a 計畫 Task 0 Step 3、補充 C 協定逐項、補充 D 身分與隔離）：在本 repo 的 TestApp 上驗 SDK 行為，結果寫進交棒說明。
/// 暫時的測試：Task 6 改寫成正式的 <c>McpTransportTests</c> 後刪掉。
/// </summary>
public sealed class McpSdkProbeTests : IDisposable
{
    private const string ProbeUserId = "mcp-probe-user";
    private const long ProbeTokenId = 4242;
    private const string OtherUserId = "mcp-probe-other";
    private const long OtherTokenId = 4343;
    /// <summary>假權杖 → 身分（比照 Task 6 的 McpGateMiddleware 以權杖查使用者）；沒帶 Authorization 時用第一把。</summary>
    private static readonly Dictionary<string, (string UserId, long TokenId)> ProbeTokens = new(StringComparer.Ordinal)
    {
        ["probe-a"] = (ProbeUserId, ProbeTokenId),
        ["probe-b"] = (OtherUserId, OtherTokenId),
    };
    private const string ProbeTool = "probe_identity";
    private static readonly TimeSpan ListTtl = TimeSpan.FromSeconds(60);

    private readonly TestApp _app = new(seedDemoPeople: false);
    /// <summary>SPA 路由探針的暫存 content root（含 wwwroot/index.html），Dispose 時刪除。</summary>
    private readonly string _spaRoot = Path.Combine(Path.GetTempPath(), $"dm-mcp-probe-{Guid.NewGuid():N}");

    public McpSdkProbeTests()
    {
        _app.ExtraServices = s =>
        {
            s.AddSingleton<IStartupFilter, ProbeMcpBranch>();
            s.AddTransient<IRequestHandler<ProbeIdentityRequest, JsonObject>, ProbeIdentityHandler>();
            s.AddMcpServer(o => o.ServerInstructions = "probe instructions")
                .WithHttpTransport(o => o.Stateless = true)
                .WithListToolsHandler(ListTools)
                .WithCallToolHandler(CallTool);
        };
    }

    public void Dispose()
    {
        _app.Dispose();
        if (Directory.Exists(_spaRoot)) Directory.Delete(_spaRoot, recursive: true);
    }

    private static ValueTask<ListToolsResult> ListTools(RequestContext<ListToolsRequestParams> context, CancellationToken ct) =>
        ValueTask.FromResult(new ListToolsResult
        {
            Tools = [new Tool { Name = ProbeTool, Description = "probe", InputSchema = JsonSerializer.Deserialize<JsonElement>("""{"type":"object"}""") }],
            TimeToLive = ListTtl,
            CacheScope = CacheScope.Private,
        });

    /// <summary>
    /// 假工具：回 IHttpContextAccessor 看到的身分、SDK request context 的身分、AuditOrigin 的通道與權杖 id，
    /// 以及經 MediatR 送進 handler 後讀到的身分（補充 D 第 1 項）。引數 <c>auditMarker</c> 有值時以真的 <see cref="IAuditLog.WriteNowAsync"/>
    /// 寫一列（補充 D 第 2 項），標記放在 TargetId 供讀回。
    /// </summary>
    private static async ValueTask<CallToolResult> CallTool(RequestContext<CallToolRequestParams> context, CancellationToken ct)
    {
        var services = context.Services!;
        var http = services.GetRequiredService<IHttpContextAccessor>().HttpContext;
        var payload = new JsonObject
        {
            ["httpContextUser"] = http?.User.FindFirstValue(ClaimTypes.NameIdentifier),
            ["contextUser"] = context.User?.FindFirstValue(ClaimTypes.NameIdentifier),
            ["channel"] = AuditOrigin.Channel.ToString(),
            ["tokenId"] = AuditOrigin.TokenId,
            ["handler"] = await services.GetRequiredService<ISender>().Send(new ProbeIdentityRequest(), ct),
        };
        if (context.Params?.Arguments?.TryGetValue("auditMarker", out var marker) == true)
            await services.GetRequiredService<IAuditLog>().WriteNowAsync(
                new AuditEntry("mcp.whoami", AuditResult.Success, TargetKind: "probe", TargetId: marker.GetString()), ct);
        return new CallToolResult { Content = [new TextContentBlock { Text = payload.ToJsonString() }] };
    }

    /// <summary>探針用的 MediatR request：handler 回它在 Application 層看到的身分與通道。</summary>
    public sealed record ProbeIdentityRequest : IRequest<JsonObject>;

    public sealed class ProbeIdentityHandler(ICurrentUser current, IRequestChannel channel) : IRequestHandler<ProbeIdentityRequest, JsonObject>
    {
        public Task<JsonObject> Handle(ProbeIdentityRequest request, CancellationToken ct) => Task.FromResult(new JsonObject
        {
            ["currentUser"] = current.UserId,
            ["requestChannel"] = channel.Channel.ToString(),
            ["requestTokenId"] = channel.TokenId,
            ["auditChannel"] = AuditOrigin.Channel.ToString(),
            ["auditTokenId"] = AuditOrigin.TokenId,
        });
    }

    /// <summary>假的 /mcp 中介軟體：換上權杖身分、開 AuditOrigin(Mcp)，再交給 SDK 的 endpoint（比照 Task 6 的 McpGateMiddleware）。</summary>
    private sealed class ProbeMcpBranch : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => builder =>
        {
            builder.Map("/mcp", branch =>
            {
                branch.Use(async (context, nextMiddleware) =>
                {
                    var bearer = context.Request.Headers.Authorization.ToString();
                    var (userId, tokenId) = bearer.StartsWith("Bearer ", StringComparison.Ordinal)
                        ? ProbeTokens[bearer["Bearer ".Length..]] : ProbeTokens["probe-a"];
                    context.User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, userId),
                        new Claim(McpClaims.Channel, McpClaims.McpValue),
                        new Claim(McpClaims.Token, tokenId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    ], "McpToken"));
                    using (AuditOrigin.Begin(AuditChannel.Mcp, tokenId))
                        await nextMiddleware();
                });
                branch.UseRouting();
                branch.UseEndpoints(e => e.MapMcp());
            });
            next(builder);
        };
    }

    private async Task<McpClient> ConnectAsync(string token = "probe-a")
    {
        var http = _app.CreateClient();
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, "/mcp"),
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" },
        }, http);
        return await McpClient.CreateAsync(transport);
    }

    private static async Task<JsonNode> CallProbeAsync(McpClient client, IReadOnlyDictionary<string, object?>? args = null)
    {
        var result = await client.CallToolAsync(ProbeTool, args);
        return JsonNode.Parse(((TextContentBlock)result.Content.Single()).Text)!;
    }

    /// <summary>工具內各處看到的身分都是這把權杖的：IHttpContextAccessor、SDK context、AuditOrigin、MediatR handler 內的 ICurrentUser 與 IRequestChannel。</summary>
    private static void ShouldBeIdentity(JsonNode seen, string userId, long tokenId)
    {
        seen["httpContextUser"]!.GetValue<string>().Should().Be(userId);
        seen["contextUser"]!.GetValue<string>().Should().Be(userId);
        seen["channel"]!.GetValue<string>().Should().Be(nameof(AuditChannel.Mcp));
        seen["tokenId"]!.GetValue<long>().Should().Be(tokenId);
        var handler = seen["handler"]!;
        handler["currentUser"]!.GetValue<string>().Should().Be(userId);
        handler["requestChannel"]!.GetValue<string>().Should().Be(nameof(RequestChannel.Mcp));
        handler["requestTokenId"]!.GetValue<long>().Should().Be(tokenId);
        handler["auditChannel"]!.GetValue<string>().Should().Be(nameof(AuditChannel.Mcp));
        handler["auditTokenId"]!.GetValue<long>().Should().Be(tokenId);
    }

    /// <summary>照 SDK client 實際送出的格式（2026-07-28：每個請求帶 _meta 與標準標頭，不握手）組原始 POST。</summary>
    private static HttpRequestMessage RawPost(
        string method, string? mcpMethodHeader, string accept = "application/json, text/event-stream", string paramsJson = "{}",
        string? mcpNameHeader = null, bool notification = false)
    {
        var parameters = JsonNode.Parse(paramsJson)!.AsObject();
        parameters["_meta"] = new JsonObject
        {
            ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28",
            ["io.modelcontextprotocol/clientInfo"] = new JsonObject { ["name"] = "probe", ["version"] = "1.0" },
            ["io.modelcontextprotocol/clientCapabilities"] = new JsonObject(),
        };
        var body = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters };
        if (!notification) body["id"] = 1;
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("Accept", accept);
        request.Headers.Add("MCP-Protocol-Version", "2026-07-28");
        if (mcpMethodHeader is not null) request.Headers.Add("Mcp-Method", mcpMethodHeader);
        if (mcpNameHeader is not null) request.Headers.Add("Mcp-Name", mcpNameHeader);
        return request;
    }

    /// <summary>SSE 回應只有一個 event: message；取出 data 的 JSON-RPC 物件。</summary>
    private static async Task<JsonObject> ReadSseMessage(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        text.Should().StartWith("event: message");
        var data = text.Split('\n').Where(l => l.StartsWith("data: ", StringComparison.Ordinal)).Should().ContainSingle().Subject;
        return JsonNode.Parse(data["data: ".Length..])!.AsObject();
    }

    /// <summary>1–3：SDK client 協商出 2026-07-28；工具內 IHttpContextAccessor 與 SDK context 都是中介軟體換上的身分，AuditOrigin 有流進工具執行。</summary>
    [Fact]
    public async Task SdkClient_ToolSeesMiddlewareIdentityAndAuditOrigin()
    {
        await using var client = await ConnectAsync();
        client.NegotiatedProtocolVersion.Should().Be("2026-07-28");
        client.ServerInstructions.Should().Be("probe instructions");

        var tools = await client.ListToolsAsync();
        tools.Select(t => t.Name).Should().Equal(ProbeTool);

        ShouldBeIdentity(await CallProbeAsync(client), ProbeUserId, ProbeTokenId);
    }

    /// <summary>補充 D 第 2 項：工具內以真的 IAuditLog.WriteNowAsync 寫一列，讀回的行為者、通道、TokenId 是這把權杖的。</summary>
    [Fact]
    public async Task Tool_WriteNowAsync_RecordsTokenIdentity()
    {
        await using var client = await ConnectAsync("probe-b");
        var marker = Guid.NewGuid().ToString("N");

        await CallProbeAsync(client, new Dictionary<string, object?> { ["auditMarker"] = marker });

        using var scope = _app.Services.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<DynamicModules.Domain.Data.DynamicDbContext>()
            .Set<AuditLogEntry>().AsNoTracking().SingleAsync(a => a.TargetId == marker);
        row.Action.Should().Be("mcp.whoami");
        row.ActorUserId.Should().Be(OtherUserId);
        row.ActorKind.Should().Be(AuditActorKind.User);
        row.Channel.Should().Be(AuditChannel.Mcp);
        row.TokenId.Should().Be(OtherTokenId);
    }

    /// <summary>補充 D 第 3 項：兩把不同使用者的權杖、兩個 client 交錯並行各呼叫 20 次，每次看到的都是自己那把的身分與 TokenId。</summary>
    [Fact]
    public async Task TwoClients_Concurrent_IdentitiesNeverCross()
    {
        await using var a = await ConnectAsync("probe-a");
        await using var b = await ConnectAsync("probe-b");

        var calls = Enumerable.Range(0, 20).SelectMany(_ => new[]
        {
            Task.Run(async () => ShouldBeIdentity(await CallProbeAsync(a), ProbeUserId, ProbeTokenId)),
            Task.Run(async () => ShouldBeIdentity(await CallProbeAsync(b), OtherUserId, OtherTokenId)),
        });

        await Task.WhenAll(calls);
    }

    /// <summary>4、6：tools/list 回 ttlMs／cacheScope／resultType；回應固定是 text/event-stream（單一 event: message）。</summary>
    [Fact]
    public async Task RawToolsList_HasCacheHintsAndResultType_OverEventStream()
    {
        using var response = await _app.CreateClient().SendAsync(RawPost("tools/list", "tools/list"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        response.Headers.Contains("Mcp-Session-Id").Should().BeFalse("stateless 不發 session");
        var result = (await ReadSseMessage(response))["result"]!;
        result["ttlMs"]!.GetValue<long>().Should().Be((long)ListTtl.TotalMilliseconds);
        result["cacheScope"]!.GetValue<string>().Should().Be("private");
        result["resultType"]!.GetValue<string>().Should().Be("complete");
    }

    /// <summary>server/discover 由 SDK 實作、有 capabilities；應用程式沒設快取提示時 SDK 補 ttlMs 0、cacheScope private。</summary>
    [Fact]
    public async Task RawDiscover_ImplementedBySdk_WithDefaultCacheHints()
    {
        using var response = await _app.CreateClient().SendAsync(RawPost("server/discover", "server/discover"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = (await ReadSseMessage(response))["result"]!;
        result["supportedVersions"]!.AsArray().Select(v => v!.GetValue<string>()).Should().Contain("2026-07-28");
        result["capabilities"]!["tools"].Should().NotBeNull("補充 C 第 2 項：discover 要帶 capabilities，掛了工具處理器就有 tools");
        result["instructions"]!.GetValue<string>().Should().Be("probe instructions");
        result["ttlMs"]!.GetValue<long>().Should().Be(0);
        result["cacheScope"]!.GetValue<string>().Should().Be("private");
    }

    /// <summary>4：2026-07-28 的請求缺 Mcp-Method、或與 body 不符時 SDK 回 400。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("tools/call")]
    public async Task RawPost_MissingOrMismatchedMcpMethodHeader_Is400(string? header)
    {
        using var response = await _app.CreateClient().SendAsync(RawPost("tools/list", header));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>補充 C 第 5 項：2026-07-28 的 tools/call，Mcp-Name 與 body 的工具名不符 → 400；相符時照常 200（對照組）。</summary>
    [Theory]
    [InlineData(ProbeTool, HttpStatusCode.OK)]
    [InlineData("other_tool", HttpStatusCode.BadRequest)]
    public async Task RawToolsCall_McpNameHeaderMustMatchBody(string header, HttpStatusCode expected)
    {
        using var response = await _app.CreateClient().SendAsync(
            RawPost("tools/call", "tools/call", paramsJson: $$$"""{"name":"{{{ProbeTool}}}","arguments":{}}""", mcpNameHeader: header));

        response.StatusCode.Should().Be(expected);
    }

    /// <summary>
    /// 補充 C 第 8 項：記下各種回應的 Content-Type（成功回應是 text/event-stream，見上）。實測：
    /// 標頭檢查擋下的錯誤是 HTTP 400、application/json（JSON-RPC -32020）；分派後的 JSON-RPC 錯誤（不存在的方法）是 HTTP 404、
    /// text/event-stream（-32601）；通知（沒有 id）是 202、沒有 Content-Type、沒有內容。
    /// </summary>
    [Fact]
    public async Task RawPost_ErrorAndNotification_ContentTypes()
    {
        using var headerError = await _app.CreateClient().SendAsync(RawPost("tools/list", null));
        using var methodError = await _app.CreateClient().SendAsync(RawPost("no/suchMethod", "no/suchMethod"));
        using var notification = await _app.CreateClient().SendAsync(
            RawPost("notifications/cancelled", "notifications/cancelled", paramsJson: """{"requestId":99}""", notification: true));

        headerError.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        headerError.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        JsonNode.Parse(await headerError.Content.ReadAsStringAsync())!["error"]!["code"]!.GetValue<int>().Should().Be(-32020);

        methodError.StatusCode.Should().Be(HttpStatusCode.NotFound);
        methodError.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        (await ReadSseMessage(methodError))["error"]!["code"]!.GetValue<int>().Should().Be(-32601);

        notification.StatusCode.Should().Be(HttpStatusCode.Accepted);
        notification.Content.Headers.ContentType.Should().BeNull();
        (await notification.Content.ReadAsStringAsync()).Should().BeEmpty();
    }

    /// <summary>6：Accept 只有 application/json 時 SDK 回 406（不能要求固定 JSON 回應）。</summary>
    [Fact]
    public async Task RawPost_AcceptJsonOnly_Is406()
    {
        using var response = await _app.CreateClient().SendAsync(RawPost("tools/list", "tools/list", accept: "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.NotAcceptable);
    }

    /// <summary>
    /// 舊協定（握手版）的 client 在 stateless 模式照常 initialize，不發 Mcp-Session-Id。
    /// 補充 C 第 1 項指定 2025-11-25；2025-06-18 是 Task 0 先測的那版，保留。
    /// </summary>
    [Theory]
    [InlineData("2025-11-25")]
    [InlineData("2025-06-18")]
    public async Task LegacyInitialize_StillServedStatelessly(string version)
    {
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["protocolVersion"] = version, ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "probe", ["version"] = "1.0" },
            },
        }.ToJsonString();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");

        using var response = await _app.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains("Mcp-Session-Id").Should().BeFalse();
        (await ReadSseMessage(response))["result"]!["protocolVersion"]!.GetValue<string>().Should().Be(version);
    }

    /// <summary>
    /// 5：GET /mcp 與 SPA fallback 的路由互動。TestApp 沒有 wwwroot、探針又是掛在分支上，所以另起一個最小主機，
    /// 照 Program.cs 的順序（MapMcp 之後才是 wwwroot 區塊的 UseStaticFiles、/api 404、MapFallbackToFile）實測。
    /// 實測結果：stateless 的 MapMcp 不對應 GET，GET /mcp 掉進 SPA fallback 回 200 index.html；DELETE 回 405。
    /// </summary>
    [Fact]
    public async Task GetMcp_WithSpaFallback_FallsThroughToIndexHtml()
    {
        await using var host = await SpaHostAsync(_spaRoot);
        var client = host.GetTestClient();

        using var get = await client.GetAsync("/mcp");
        using var delete = await client.DeleteAsync("/mcp");
        using var spa = await client.GetAsync("/some/page");

        spa.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        delete.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        get.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
    }

    /// <summary>Task 6 要達成的行為：GET /mcp 不回 SPA。Task 6 在 Program.cs 擋掉後拿掉 Skip，搬進 McpTransportTests。</summary>
    [Fact(Skip = "GET /mcp 目前掉進 SPA fallback（見上一個測試）；留給 mcp1a Task 6 在 /mcp 擋非 POST 的方法")]
    public async Task GetMcp_WithSpaFallback_IsNotServedBySpa()
    {
        await using var host = await SpaHostAsync(_spaRoot);

        using var get = await host.GetTestClient().GetAsync("/mcp");

        get.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
    }

    private static async Task<WebApplication> SpaHostAsync(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "wwwroot"));
        File.WriteAllText(Path.Combine(root, "wwwroot", "index.html"), "<html>spa</html>");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root, WebRootPath = Path.Combine(root, "wwwroot") });
        builder.WebHost.UseTestServer();
        builder.Services.AddMcpServer().WithHttpTransport(o => o.Stateless = true);
        var host = builder.Build();
        host.MapMcp("/mcp");
        host.UseDefaultFiles();
        host.UseStaticFiles();
        host.Map("/api/{**rest}", () => Results.NotFound());
        host.MapFallbackToFile("index.html");
        await host.StartAsync();
        return host;
    }
}
