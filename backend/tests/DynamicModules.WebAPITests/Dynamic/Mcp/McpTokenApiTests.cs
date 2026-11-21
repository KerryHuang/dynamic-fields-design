using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic.Mcp;

/// <summary>權杖 REST API（mcp1a 計畫判斷 7、16）：八條路由的狀態碼與 code、語系、登入與強制改密碼。</summary>
[Trait("Category", "Postgres")]
public class McpTokenApiTests : IDisposable
{
    private const string AdminPassword = "initial-pass-1";
    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    private void Db(Action<DynamicDbContext> act)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
        act(db);
        db.SaveChanges();
    }

    private void Enable() => Db(db => db.Set<SystemSetting>().Add(new SystemSetting { Key = PolicyCatalog.McpEnabled.SettingKey, Value = "true" }));

    private static string Tomorrow(int days = 1) => DateOnly.FromDateTime(DateTime.Now).AddDays(days).ToString("yyyy-MM-dd");

    private static Task<HttpResponseMessage> Create(HttpClient c, string password = AdminPassword, string name = "筆電") =>
        c.PostAsJsonAsync($"{Base}/me/mcp/tokens", new { name, scopes = new[] { "business.read" }, expiresOn = Tomorrow(30), password });

    private static async Task<string?> Code(HttpResponseMessage res) =>
        (await JsonOf(res)).TryGetProperty("code", out var c) ? c.GetString() : null;

    [Fact]
    public async Task Anonymous_401()
    {
        var anon = _app.AsUser(null);
        (await anon.GetAsync($"{Base}/me/mcp")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anon.PostAsJsonAsync($"{Base}/me/mcp/tokens", new { })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anon.GetAsync($"{Base}/system/mcp/tokens")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task MustChangePassword_OnlyGetMe()
    {
        Db(db => db.Set<AppUser>().Add(new AppUser { UserId = "meiling", DisplayName = "張美玲", MustChangePassword = true }));
        var c = _app.AsUser("meiling");
        (await c.GetAsync($"{Base}/me")).StatusCode.Should().Be(HttpStatusCode.OK);
        var res = await c.GetAsync($"{Base}/me/mcp");
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Code(res)).Should().Be("passwordChangeRequired");
        (await c.GetAsync($"{Base}/me/mcp/tokens")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Disabled_CreateForbidden_ListStillWorks_LocalizedMessage()
    {
        var admin = _app.AsUser();
        var res = await Create(admin);
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Code(res)).Should().Be("mcpDisabled");

        var en = _app.AsUser();
        en.DefaultRequestHeaders.Add("X-Locale", "en-US");
        var body = await JsonOf(await Create(en));
        body.GetProperty("message").GetString().Should().MatchRegex("^[ -~]+$", "英文訊息不含中文");

        (await admin.GetAsync($"{Base}/me/mcp/tokens")).StatusCode.Should().Be(HttpStatusCode.OK);
        var info = await JsonOf(await admin.GetAsync($"{Base}/me/mcp"));
        (info.GetProperty("enabled").GetBoolean(), info.GetProperty("canUse").GetBoolean(), info.GetProperty("reason").GetString())
            .Should().Be((false, false, "disabled"));
        info.GetProperty("serverUrl").GetString().Should().EndWith("/mcp");
    }

    [Fact]
    public async Task Lifecycle_EightRoutes()
    {
        Enable();
        var admin = _app.AsUser();

        var info = await JsonOf(await admin.GetAsync($"{Base}/me/mcp"));
        info.GetProperty("canUse").GetBoolean().Should().BeTrue();
        info.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()).Should().Equal("business.read", "business.write");
        info.GetProperty("defaultScopes").EnumerateArray().Select(s => s.GetString()).Should().Equal("business.read");

        var wrong = await Create(admin, password: "nope-nope");
        wrong.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await JsonOf(wrong)).GetProperty("errors").TryGetProperty("password", out _).Should().BeTrue();

        var created = await Create(admin);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var createdBody = await JsonOf(created);
        var plaintext = createdBody.GetProperty("token").GetString()!;
        plaintext.Should().StartWith("fwp_");
        var id = createdBody.GetProperty("item").GetProperty("id").GetInt64();

        var listRes = await admin.GetAsync($"{Base}/me/mcp/tokens");
        var listText = await listRes.Content.ReadAsStringAsync();
        listText.Should().NotContain(plaintext).And.NotContain("tokenHash");
        var item = JsonDocument.Parse(listText).RootElement.EnumerateArray().Single();
        (item.GetProperty("status").GetString(), item.GetProperty("todayCalls").GetInt32()).Should().Be(("active", 0));

        var calls = await admin.GetAsync($"{Base}/me/mcp/tokens/{id}/calls");
        calls.StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonOf(calls)).GetArrayLength().Should().Be(0);

        // 別人（示範帳號 sales01）看不到、撤不了，也建不了
        var sales = _app.AsUser("sales01");
        (await sales.PostAsync($"{Base}/me/mcp/tokens/{id}/revoke", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await sales.GetAsync($"{Base}/me/mcp/tokens/{id}/calls")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var blocked = await Create(sales);
        blocked.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Code(blocked)).Should().Be("mcpAccountBlocked");
        (await sales.GetAsync($"{Base}/system/mcp/tokens")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await sales.PostAsync($"{Base}/system/mcp/tokens/{id}/revoke", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await sales.PostAsync($"{Base}/system/mcp/users/admin/revoke-all", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var all = await JsonOf(await admin.GetAsync($"{Base}/system/mcp/tokens?userId=admin&status=active"));
        all.GetArrayLength().Should().Be(1);
        (await admin.GetAsync($"{Base}/system/mcp/tokens?status=bogus")).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await admin.PostAsync($"{Base}/me/mcp/tokens/{id}/revoke", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.PostAsync($"{Base}/system/mcp/tokens/{id}/revoke", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        var revoked = (await JsonOf(await admin.GetAsync($"{Base}/system/mcp/tokens?status=revoked"))).EnumerateArray().Single();
        revoked.GetProperty("revokeReason").GetString().Should().Be("owner", "已撤銷的再撤不改原因");

        var second = (await JsonOf(await Create(admin, name: "第二把"))).GetProperty("item").GetProperty("id").GetInt64();
        var revokeAll = await admin.PostAsync($"{Base}/system/mcp/users/admin/revoke-all", null);
        revokeAll.StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonOf(revokeAll)).GetProperty("revoked").GetInt32().Should().Be(1);
        var secondItem = (await JsonOf(await admin.GetAsync($"{Base}/me/mcp/tokens"))).EnumerateArray().Single(t => t.GetProperty("id").GetInt64() == second);
        secondItem.GetProperty("revokeReason").GetString().Should().Be("admin");
        (await admin.PostAsync($"{Base}/system/mcp/users/nobody/revoke-all", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await admin.PostAsync($"{Base}/system/mcp/tokens/987654/revoke", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task WrongPassword_Throttled_429()
    {
        Enable();
        var admin = _app.AsUser();
        for (var i = 0; i < 5; i++) (await Create(admin, password: "nope-nope")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var res = await Create(admin);
        res.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await Code(res)).Should().Be("mcpPasswordThrottled");
    }

    [Fact]
    public async Task Validation_Codes()
    {
        Enable();
        var admin = _app.AsUser();
        var badExpiry = await admin.PostAsJsonAsync($"{Base}/me/mcp/tokens",
            new { name = "x", scopes = new[] { "business.read" }, expiresOn = Tomorrow(0), password = AdminPassword });
        badExpiry.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await JsonOf(badExpiry)).GetProperty("errors").GetProperty("expiresOn")[0].GetString().Should().Contain(Tomorrow(1));

        var badScope = await admin.PostAsJsonAsync($"{Base}/me/mcp/tokens",
            new { name = "x", scopes = new[] { "design.read" }, expiresOn = Tomorrow(), password = AdminPassword });
        (await JsonOf(badScope)).GetProperty("errors").TryGetProperty("scopes", out _).Should().BeTrue();

        Db(db => db.Set<SystemSetting>().Add(new SystemSetting { Key = PolicyCatalog.McpTokenMaxPerUser.SettingKey, Value = "1" }));
        (await Create(admin)).StatusCode.Should().Be(HttpStatusCode.Created);
        var limit = await Create(admin);
        limit.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Code(limit)).Should().Be("mcpTokenLimit");
    }
}
