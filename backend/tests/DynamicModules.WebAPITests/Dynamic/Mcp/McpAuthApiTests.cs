using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Application.Dynamic.McpService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Mcp;
using DynamicModules.Domain.Policies;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Dynamic.Mcp.McpTestKit;

namespace DynamicModules.WebAPITests.Dynamic.Mcp;

/// <summary>
/// <c>/mcp</c> 的閘門（mcp1a 計畫判斷 8、11，Task 6）：權杖認證、cookie 與 X-User 不收、query string 權杖、Origin、HTTPS、IP 降速、log 不含秘密。
/// 以原始 POST 送 <c>tools/list</c>；通過閘門時 SDK 回 200。
/// </summary>
[Trait("Category", "Postgres")]
public class McpAuthApiTests : IDisposable
{
    private TestApp _app;

    public McpAuthApiTests() => _app = NewApp();

    public void Dispose() => _app.Dispose();

    private void Use(TestApp app)
    {
        _app.Dispose();
        _app = app;
    }

    private Task<HttpResponseMessage> Post(string? token, IReadOnlyDictionary<string, string>? headers = null, string path = "/mcp") =>
        RawPostAsync(_app.CreateClient(), token, headers: headers, path: path);

    private static async Task<string?> CodeOf(HttpResponseMessage res) =>
        (await res.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("code", out var c) ? c.GetString() : null;

    private static async Task ShouldBeUnauthorized(HttpResponseMessage res, string code = McpMessages.CodeUnauthorized)
    {
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        // 所有 401 都帶 Bearer challenge（設計 §5.3；審查必修 5：query 權杖原本缺）
        res.Headers.WwwAuthenticate.ToString().Should().Be("Bearer");
        (await CodeOf(res)).Should().Be(code);
    }

    private static async Task ShouldBeForbidden(HttpResponseMessage res, string code)
    {
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await CodeOf(res)).Should().Be(code);
    }

    private List<AuditLogEntry> AccessRows() => Query(_app, db => db.Set<AuditLogEntry>().Where(a => a.Action == "mcp.access").ToList());

    [Fact]
    public async Task ValidToken_PassesGate()
    {
        EnableMcp(_app);
        var (raw, _) = IssueToken(_app);
        (await Post(raw)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    public static TheoryData<string> BadTokenCases => ["none", "garbage", "unknown", "expired", "revoked", "deactivated", "mustChange", "demo", "badScopes"];

    [Theory]
    [MemberData(nameof(BadTokenCases))]
    public async Task BadToken_401_WithBearerChallenge_NoAudit(string kind)
    {
        EnableMcp(_app);
        var now = _app.Services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;
        string? raw = kind switch
        {
            "none" => null,
            "garbage" => "not-a-token",
            "unknown" => McpSecrets.NewToken(),
            "expired" => IssueToken(_app, expiresUtc: now).Raw,
            "revoked" => IssueToken(_app, revokedUtc: now.AddMinutes(-1)).Raw,
            "badScopes" => IssueToken(_app, scopes: "business.read,bogus").Raw,
            _ => IssueToken(_app).Raw,
        };
        if (kind == "deactivated") EnsureUser(_app, UserId, u => u.IsActive = false);
        if (kind == "mustChange") EnsureUser(_app, UserId, u => u.MustChangePassword = true);
        if (kind == "demo") EnsureUser(_app, UserId, u => u.IsDemoAccount = true);

        var res = await Post(raw);

        await ShouldBeUnauthorized(res);
        res.Headers.WwwAuthenticate.ToString().Should().Be("Bearer");
        AccessRows().Should().BeEmpty("401 沒有可信身分，不寫稽核（Q6）");
    }

    public static TheoryData<string, string> PolicyCases => new()
    {
        { "disabled", McpMessages.CodeDisabled },
        { "notAllowed", McpMessages.CodeNotAllowed },
        { "policyInvalid", McpMessages.CodePolicyInvalid },
    };

    [Theory]
    [MemberData(nameof(PolicyCases))]
    public async Task PolicyDenied_403_AuditedWithToken(string kind, string code)
    {
        if (kind == "notAllowed") EnableMcp(_app, $"user:{OtherUserId}");
        if (kind == "policyInvalid") EnableMcp(_app, "everyone!");
        var (raw, tokenId) = IssueToken(_app);

        await ShouldBeForbidden(await Post(raw), code);

        var row = AccessRows().Should().ContainSingle().Subject;
        (row.Result, row.ReasonCode, row.TokenId, row.ActorUserId, row.Channel).Should()
            .Be((AuditResult.Denied, code, tokenId, UserId, AuditChannel.Mcp));
    }

    [Fact]
    public async Task DemoSite_403_Audited()
    {
        Use(NewApp(settings: new() { ["Demo:LoginEnabled"] = "true" }));
        EnableMcp(_app);
        var (raw, tokenId) = IssueToken(_app);

        await ShouldBeForbidden(await Post(raw), McpMessages.CodeDemoSite);
        AccessRows().Should().ContainSingle().Which.TokenId.Should().Be(tokenId);
    }

    /// <summary>A 的 cookie 加 B 被政策擋下的權杖：稽核的行為者是 B、通道 mcp、TokenId 是 B 的（換身分之後才寫）。</summary>
    [Fact]
    public async Task ForbiddenAudit_UsesTokenIdentity()
    {
        Use(NewApp(realAuth: true));
        const string password = "cookie-pass-1";
        EnsureUser(_app, "cookie-a", u => u.PasswordHash = new Microsoft.AspNetCore.Identity.PasswordHasher<AppUser>().HashPassword(new AppUser(), password));
        var client = _app.CreateClient();
        (await client.PostAsJsonAsync("/api/v3/dynamic/auth/login", new { userId = "cookie-a", password })).StatusCode.Should().Be(HttpStatusCode.OK);
        var (raw, tokenId) = IssueToken(_app, OtherUserId);

        await ShouldBeForbidden(await RawPostAsync(client, raw), McpMessages.CodeDisabled);

        var row = AccessRows().Should().ContainSingle().Subject;
        (row.ActorUserId, row.Channel, row.TokenId).Should().Be((OtherUserId, AuditChannel.Mcp, tokenId));
    }

    [Fact]
    public async Task Cookie_NotAccepted()
    {
        Use(NewApp(realAuth: true));
        EnableMcp(_app);
        const string password = "cookie-pass-1";
        EnsureUser(_app, UserId, u => u.PasswordHash = new Microsoft.AspNetCore.Identity.PasswordHasher<AppUser>().HashPassword(new AppUser(), password));
        var client = _app.CreateClient();
        (await client.PostAsJsonAsync("/api/v3/dynamic/auth/login", new { userId = UserId, password })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v3/dynamic/me")).StatusCode.Should().Be(HttpStatusCode.OK, "對照組：cookie 本身有效");

        await ShouldBeUnauthorized(await RawPostAsync(client, null));
    }

    [Fact]
    public async Task XUser_NotAccepted()
    {
        EnableMcp(_app);
        await ShouldBeUnauthorized(await Post(null, new Dictionary<string, string> { ["X-User"] = "admin" }));
    }

    [Fact]
    public async Task Rest_Bearer_NotAccepted()
    {
        EnableMcp(_app);
        var (raw, _) = IssueToken(_app);
        var client = _app.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {raw}");

        (await client.GetAsync("/api/v3/dynamic/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("/mcp?access_token=x")]
    [InlineData("/mcp?Token=x")]
    [InlineData("/mcp?api_key=x")]
    [InlineData("/mcp?q=fwp_abc")]
    public async Task Query_Token_Rejected(string path)
    {
        EnableMcp(_app);
        var (raw, _) = IssueToken(_app);
        var res = await Post(raw, path: path.Replace("fwp_abc", raw));
        await ShouldBeUnauthorized(res, McpMessages.CodeTokenInQuery);
    }

    private async Task<HttpResponseMessage> PostWithValid(Dictionary<string, string> headers)
    {
        EnableMcp(_app);
        return await Post(IssueToken(_app).Raw, headers);
    }

    [Fact]
    public async Task Origin_Foreign_Denied() =>
        await ShouldBeForbidden(await PostWithValid(new() { ["Origin"] = "https://evil.example" }), McpMessages.CodeOriginDenied);

    [Fact]
    public async Task Origin_ArbitraryHostSameOrigin_Denied() =>
        await ShouldBeForbidden(await PostWithValid(new() { ["Host"] = "evil.example", ["Origin"] = "http://evil.example" }),
            McpMessages.CodeOriginDenied);

    [Fact]
    public async Task Origin_Null_Denied() =>
        await ShouldBeForbidden(await PostWithValid(new() { ["Origin"] = "null" }), McpMessages.CodeOriginDenied);

    [Fact]
    public async Task Origin_PublicUrl_Allowed()
    {
        Use(NewApp(settings: new() { ["Ops:PublicUrl"] = "https://erp.example/" }));
        (await PostWithValid(new() { ["Origin"] = "https://erp.example" })).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Origin_Loopback_OnlyOnLoopbackConnection()
    {
        (await PostWithValid(new() { ["Origin"] = $"http://127.0.0.1:{Port}" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await PostWithValid(new() { ["Origin"] = $"http://localhost:{Port}" })).StatusCode.Should().Be(HttpStatusCode.OK);
        await ShouldBeForbidden(await PostWithValid(new() { ["Origin"] = "http://127.0.0.1:9999" }), McpMessages.CodeOriginDenied);
        await ShouldBeForbidden(await PostWithValid(new() { ["Origin"] = $"http://127.0.0.1:{Port}", [RemoteIpHeader] = "192.0.2.10" }),
            McpMessages.CodeOriginDenied);
    }

    [Fact]
    public async Task Origin_Absent_Allowed() => (await PostWithValid(new())).StatusCode.Should().Be(HttpStatusCode.OK);

    [Fact]
    public async Task Https_NonLoopbackHttp_Denied() =>
        await ShouldBeForbidden(await PostWithValid(new() { [RemoteIpHeader] = "192.0.2.10" }), McpMessages.CodeHttpsRequired);

    [Fact]
    public async Task Https_AllowHttp_Passes_AndWarnsAtStartup()
    {
        Use(NewApp(settings: new() { ["Mcp:AllowHttp"] = "true" }));
        (await PostWithValid(new() { [RemoteIpHeader] = "192.0.2.10" })).StatusCode.Should().Be(HttpStatusCode.OK);
        _app.Logs.Entries.Should().Contain(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning && e.Message.Contains("Mcp:AllowHttp"));
    }

    [Fact]
    public async Task Https_ForgedForwardedFor_NoEffect() =>
        await ShouldBeForbidden(await PostWithValid(new() { [RemoteIpHeader] = "192.0.2.10", ["X-Forwarded-For"] = "127.0.0.1" }),
            McpMessages.CodeHttpsRequired);

    [Fact]
    public async Task Https_TrustForwarded_NoLoopbackException()
    {
        Use(NewApp(settings: new() { ["Auth:TrustForwardedHeaders"] = "true" }));
        await ShouldBeForbidden(await PostWithValid(new()), McpMessages.CodeHttpsRequired);
    }

    /// <summary>部署前提（已知限制）：開轉送標頭時，代理覆寫的 X-Forwarded-Proto: https 讓 Request.IsHttps 成立。</summary>
    [Fact]
    public async Task Https_TrustForwarded_ProtoHttps_Allowed()
    {
        Use(NewApp(settings: new() { ["Auth:TrustForwardedHeaders"] = "true" }));
        (await PostWithValid(new() { ["X-Forwarded-Proto"] = "https" })).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AuthFailures_Throttled()
    {
        EnableMcp(_app);
        var bad = McpSecrets.NewToken();
        for (var i = 0; i < McpAuthThrottleMax; i++) await ShouldBeUnauthorized(await Post(bad));

        var blocked = await Post(bad);
        blocked.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        blocked.Headers.RetryAfter.Should().NotBeNull();
        (await Post(IssueToken(_app).Raw)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "同一 IP 被擋時有效權杖也擋");

        await ShouldBeUnauthorized(await Post(bad, new Dictionary<string, string> { [RemoteIpHeader] = "::1" }));
    }

    private const int McpAuthThrottleMax = 20;

    [Fact]
    public async Task AuthFailures_Concurrent()
    {
        EnableMcp(_app);
        var bad = McpSecrets.NewToken();
        var results = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => Post(bad)));

        results.Count(r => r.StatusCode == HttpStatusCode.Unauthorized).Should().BeLessThanOrEqualTo(McpAuthThrottleMax);
        results.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Unauthorized || r.StatusCode == HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Log_NoSecrets()
    {
        EnableMcp(_app);
        var (raw, _) = IssueToken(_app);
        var bad = McpSecrets.NewToken();
        (await Post(raw)).StatusCode.Should().Be(HttpStatusCode.OK);
        await ShouldBeUnauthorized(await Post(bad));

        foreach (var record in _app.Logs.Records)
        {
            var all = string.Join("|", [record.Message, .. record.Values.Select(v => $"{v.Key}={v.Value}")]);
            all.Should().NotContain(raw).And.NotContain(bad).And.NotContain("Bearer ");
        }
    }
}
