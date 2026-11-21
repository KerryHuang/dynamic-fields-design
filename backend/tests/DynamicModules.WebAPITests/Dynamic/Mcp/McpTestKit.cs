using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Mcp;
using DynamicModules.Domain.Policies;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;

namespace DynamicModules.WebAPITests.Dynamic.Mcp;

/// <summary>
/// <c>/mcp</c> 測試共用（mcp1a 計畫 Task 6）。<see cref="NewApp"/> 掛一個 startup filter 設定連線來源與本機埠
/// （不靠 TestServer 預設值）：預設 127.0.0.1:5180，請求帶 <see cref="RemoteIpHeader"/> 就改用那個 IP。
/// </summary>
internal static class McpTestKit
{
    public const string RemoteIpHeader = "X-Test-Remote-Ip";
    public const int LocalPort = 5180;
    /// <summary>不是示範帳號、不必改密碼的一般使用者（Demo 種子的人員都是示範帳號，不能用 MCP）。</summary>
    public const string UserId = "mcp-user", OtherUserId = "mcp-other";

    public static TestApp NewApp(bool realAuth = false, Action<IServiceCollection>? services = null, Dictionary<string, string?>? settings = null)
    {
        var app = new TestApp(seedDemoPeople: true, realAuth: realAuth);
        foreach (var (k, v) in settings ?? []) app.Settings[k] = v;
        app.ExtraServices = s =>
        {
            s.AddSingleton<IStartupFilter, ConnectionFilter>();
            services?.Invoke(s);
        };
        EnsureUser(app, UserId);
        EnsureUser(app, OtherUserId);
        return app;
    }

    private sealed class ConnectionFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => builder =>
        {
            builder.Use((ctx, nextMiddleware) =>
            {
                ctx.Connection.RemoteIpAddress = IPAddress.Parse(ctx.Request.Headers[RemoteIpHeader].FirstOrDefault() ?? "127.0.0.1");
                ctx.Connection.LocalPort = LocalPort;
                return nextMiddleware();
            });
            next(builder);
        };
    }

    public static void Db(TestApp app, Action<DynamicDbContext> act)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
        act(db);
        db.SaveChanges();
    }

    public static T Query<T>(TestApp app, Func<DynamicDbContext, T> read)
    {
        using var scope = app.Services.CreateScope();
        return read(scope.ServiceProvider.GetRequiredService<DynamicDbContext>());
    }

    public static void EnsureUser(TestApp app, string userId, Action<AppUser>? change = null) => Db(app, db =>
    {
        var user = db.Set<AppUser>().FirstOrDefault(u => u.UserId == userId);
        if (user is null) db.Set<AppUser>().Add(user = new AppUser { UserId = userId, DisplayName = "測試人員 " + userId });
        change?.Invoke(user);
    });

    /// <summary>直接寫一列權杖並回明文（不經密碼重驗；建立流程由權杖 API 測試涵蓋）。</summary>
    public static (string Raw, long Id) IssueToken(TestApp app, string userId = UserId, string scopes = McpScopes.BusinessRead,
        DateTime? expiresUtc = null, DateTime? revokedUtc = null)
    {
        var raw = McpSecrets.NewToken();
        var now = app.Services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;
        var row = new McpToken
        {
            UserId = userId, Name = "測試權杖", TokenHash = McpSecrets.Hash(raw), Last4 = McpSecrets.Last4(raw), Scopes = scopes,
            CreatedUtc = now.AddDays(-1), ExpiresUtc = expiresUtc ?? now.AddDays(30), RevokedUtc = revokedUtc,
            RevokeReason = revokedUtc is null ? null : McpRevokeReason.Owner,
        };
        Db(app, db => db.Set<McpToken>().Add(row));
        return (raw, row.Id);
    }

    public static void SetPolicy(TestApp app, PolicyDefinition def, string? value) => Db(app, db =>
    {
        db.Set<SystemSetting>().RemoveRange(db.Set<SystemSetting>().Where(s => s.Key == def.SettingKey));
        if (value is not null) db.Set<SystemSetting>().Add(new SystemSetting { Key = def.SettingKey, Value = value });
    });

    /// <summary>開 <c>mcp.enabled</c>，可選設定允許對象（直接寫政策列；政策每個請求重讀）。</summary>
    public static void EnableMcp(TestApp app, string? allowed = null)
    {
        SetPolicy(app, PolicyCatalog.McpEnabled, "true");
        if (allowed is not null) SetPolicy(app, PolicyCatalog.McpAllowedGroups, allowed);
    }

    public static async Task<McpClient> ConnectAsync(TestApp app, string? token)
    {
        var http = app.CreateClient();
        var headers = new Dictionary<string, string>();
        if (token is not null) headers["Authorization"] = $"Bearer {token}";
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, "/mcp"), AdditionalHeaders = headers,
        }, http);
        return await McpClient.CreateAsync(transport);
    }

    /// <summary>照 SDK client 實際送出的格式（2026-07-28：每個請求帶 _meta 與標準標頭，不握手）組原始 POST。</summary>
    public static HttpRequestMessage RawPost(string? token, string method = "tools/list", string paramsJson = "{}", string path = "/mcp",
        IReadOnlyDictionary<string, string>? headers = null)
    {
        var parameters = JsonNode.Parse(paramsJson)!.AsObject();
        parameters["_meta"] = new JsonObject
        {
            ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28",
            ["io.modelcontextprotocol/clientInfo"] = new JsonObject { ["name"] = "test-client", ["version"] = "1.0" },
            ["io.modelcontextprotocol/clientCapabilities"] = new JsonObject(),
        };
        var body = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = method, ["params"] = parameters };
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        request.Headers.Add("MCP-Protocol-Version", "2026-07-28");
        request.Headers.Add("Mcp-Method", method);
        if (parameters["name"] is JsonValue name) request.Headers.Add("Mcp-Name", name.GetValue<string>());
        if (token is not null) request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        foreach (var (k, v) in headers ?? new Dictionary<string, string>()) request.Headers.TryAddWithoutValidation(k, v);
        return request;
    }

    public static async Task<HttpResponseMessage> RawPostAsync(HttpClient client, string? token, string method = "tools/list",
        string paramsJson = "{}", IReadOnlyDictionary<string, string>? headers = null, string path = "/mcp") =>
        await client.SendAsync(RawPost(token, method, paramsJson, path, headers));

    /// <summary>SSE 回應只有一個 event: message；取出 data 的 JSON-RPC 物件。</summary>
    public static async Task<JsonObject> ReadSseMessage(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        text.Should().StartWith("event: message");
        var data = text.Split('\n').Where(l => l.StartsWith("data: ", StringComparison.Ordinal)).Should().ContainSingle().Subject;
        return JsonNode.Parse(data["data: ".Length..])!.AsObject();
    }

    public static string Port => LocalPort.ToString(CultureInfo.InvariantCulture);
}
