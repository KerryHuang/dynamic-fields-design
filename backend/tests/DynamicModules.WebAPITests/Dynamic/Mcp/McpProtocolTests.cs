using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DynamicModules.Application.Dynamic.McpService;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.Mcp.McpTestKit;

namespace DynamicModules.WebAPITests.Dynamic.Mcp;

/// <summary>
/// 協定層的 SDK 行為，在正式 <c>/mcp</c>（經閘門與權杖）上驗（Task 0 補充 C 的案例搬回正式測試，mcp1a-3 審查必修 2）：
/// 舊協定 initialize、<c>server/discover</c> 的 capabilities、<c>Mcp-Method</c>／<c>Mcp-Name</c> 標頭檢查、<c>resultType</c>，
/// 以及成功、錯誤、通知三種回應的 Content-Type（實測值）。升 SDK 版本時這裡會先紅。
/// </summary>
[Trait("Category", "Postgres")]
public class McpProtocolTests : IDisposable
{
    private readonly TestApp _app;
    private readonly string _token;

    public McpProtocolTests()
    {
        _app = NewApp();
        EnableMcp(_app);
        _token = IssueToken(_app).Raw;
    }

    public void Dispose() => _app.Dispose();

    private Task<HttpResponseMessage> Post(string method, string paramsJson = "{}", string? methodHeader = null, string? nameHeader = null,
        bool notification = false) =>
        _app.CreateClient().SendAsync(RawPost(_token, method, paramsJson, methodHeader: methodHeader, nameHeader: nameHeader,
            notification: notification));

    /// <summary>成功回應：text/event-stream（單一 event: message）、不發 session、2026-07-28 的結果帶 resultType。</summary>
    [Fact]
    public async Task ToolsList_Success_EventStream_WithResultType()
    {
        using var res = await Post("tools/list");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        res.Headers.Contains("Mcp-Session-Id").Should().BeFalse("stateless 不發 session");
        (await ReadSseMessage(res))["result"]!["resultType"]!.GetValue<string>().Should().Be("complete");
    }

    [Fact]
    public async Task ToolsCall_Success_HasResultType()
    {
        using var res = await Post("tools/call", """{"name":"whoami","arguments":{}}""");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        (await ReadSseMessage(res))["result"]!["resultType"]!.GetValue<string>().Should().Be("complete");
    }

    /// <summary>server/discover 由 SDK 實作：有 capabilities.tools（掛了工具處理器）、支援 2026-07-28、帶伺服器說明。</summary>
    [Fact]
    public async Task Discover_HasCapabilities()
    {
        using var res = await Post("server/discover");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = (await ReadSseMessage(res))["result"]!;
        result["supportedVersions"]!.AsArray().Select(v => v!.GetValue<string>()).Should().Contain("2026-07-28");
        result["capabilities"]!["tools"].Should().NotBeNull();
        result["instructions"]!.GetValue<string>().Should().Be(McpTexts.ServerInstructions);
    }

    /// <summary>2026-07-28 的請求缺 Mcp-Method、或與 body 不符 → 400。</summary>
    [Theory]
    [InlineData(OmitHeader)]
    [InlineData("tools/call")]
    public async Task McpMethodHeader_MissingOrMismatched_Is400(string header)
    {
        using var res = await Post("tools/list", methodHeader: header);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>tools/call 的 Mcp-Name 與 body 的工具名不符 → 400；相符照常 200（對照組）。</summary>
    [Theory]
    [InlineData("whoami", HttpStatusCode.OK)]
    [InlineData("list_modules", HttpStatusCode.BadRequest)]
    public async Task McpNameHeader_MustMatchBody(string header, HttpStatusCode expected)
    {
        using var res = await Post("tools/call", """{"name":"whoami","arguments":{}}""", nameHeader: header);

        res.StatusCode.Should().Be(expected);
    }

    /// <summary>
    /// 錯誤與通知的 Content-Type（實測）：標頭檢查擋下的錯誤是 400、application/json（JSON-RPC -32020）；
    /// 分派後的 JSON-RPC 錯誤（不存在的方法）是 404、text/event-stream（-32601）；通知（沒有 id）是 202、沒有 Content-Type、沒有內容。
    /// </summary>
    [Fact]
    public async Task ErrorAndNotification_ContentTypes()
    {
        using var headerError = await Post("tools/list", methodHeader: OmitHeader);
        using var methodError = await Post("no/suchMethod");
        using var notification = await Post("notifications/cancelled", """{"requestId":99}""", notification: true);

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

    /// <summary>舊協定（握手版）的 client 在 stateless 模式照常 initialize，不發 Mcp-Session-Id。</summary>
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
                ["clientInfo"] = new JsonObject { ["name"] = "legacy-client", ["version"] = "1.0" },
            },
        }.ToJsonString();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_token}");

        using var res = await _app.CreateClient().SendAsync(request);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Headers.Contains("Mcp-Session-Id").Should().BeFalse();
        (await ReadSseMessage(res))["result"]!["protocolVersion"]!.GetValue<string>().Should().Be(version);
    }
}
