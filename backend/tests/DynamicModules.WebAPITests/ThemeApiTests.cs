using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.WebAPITests;

/// <summary>主題：個人設定與全站預設逐欄解析、匿名端點只回兩個值、Demo 站不能儲存。</summary>
public class ThemeApiTests
{
    private const string Base = "/api/v3/dynamic";

    private sealed class DemoSiteApp : TestApp
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Demo:LoginEnabled", "true");
        }
    }

    private static void Db(TestApp app, Action<DynamicDbContext> act)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
        act(db);
        db.SaveChanges();
    }

    private static void SetUser(TestApp app, string userId, string? mode, string? style) =>
        Db(app, db =>
        {
            var u = db.Set<AppUser>().Single(x => x.UserId == userId);
            u.ThemeMode = mode;
            u.ThemeStyle = style;
        });

    private static void SetSite(TestApp app, string key, string value) =>
        Db(app, db =>
        {
            var row = db.Set<SystemSetting>().SingleOrDefault(x => x.Key == key);
            if (row is null) db.Set<SystemSetting>().Add(new SystemSetting { Key = key, Value = value });
            else row.Value = value;
        });

    private static async Task<JsonElement> MeTheme(HttpClient client)
    {
        var res = await client.GetAsync($"{Base}/me");
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("theme");
    }

    [Fact]
    public async Task Me_NoChoice_ReturnsExplicitNullsAndFactoryEffective()
    {
        using var app = new TestApp();
        var theme = await MeTheme(app.AsUser("sales01"));

        theme.GetProperty("mode").ValueKind.Should().Be(JsonValueKind.Null);
        theme.GetProperty("style").ValueKind.Should().Be(JsonValueKind.Null);
        theme.GetProperty("effectiveMode").GetString().Should().Be("system");
        theme.GetProperty("effectiveStyle").GetString().Should().Be("workbench");
        theme.GetProperty("defaultMode").GetString().Should().Be("system");
        theme.GetProperty("defaultStyle").GetString().Should().Be("workbench");
        theme.GetProperty("canSave").GetBoolean().Should().BeTrue();
        Db(app, db => db.Set<SystemSetting>().Any(s => s.Key.StartsWith("theme.")).Should().BeFalse());
    }

    [Fact]
    public async Task Me_ResolvesEachFieldIndependently()
    {
        using var app = new TestApp();
        SetUser(app, "sales01", "dark", null);
        SetSite(app, "theme.defaultStyle", "airy");
        var theme = await MeTheme(app.AsUser("sales01"));
        theme.GetProperty("effectiveMode").GetString().Should().Be("dark");
        theme.GetProperty("effectiveStyle").GetString().Should().Be("airy");

        SetSite(app, "theme.defaultStyle", "enterprise");
        theme = await MeTheme(app.AsUser("sales01"));
        theme.GetProperty("effectiveStyle").GetString().Should().Be("enterprise");
        theme.GetProperty("effectiveMode").GetString().Should().Be("dark");
    }

    [Theory]
    [InlineData("neon")]
    [InlineData("Dark")]
    [InlineData("")]
    public async Task Me_UnknownPersonalMode_FallsBackAndReportsNull(string stored)
    {
        using var app = new TestApp();
        SetSite(app, "theme.defaultMode", "light");
        SetUser(app, "sales01", stored, "airy");
        var theme = await MeTheme(app.AsUser("sales01"));

        theme.GetProperty("mode").ValueKind.Should().Be(JsonValueKind.Null);
        theme.GetProperty("effectiveMode").GetString().Should().Be("light");
        theme.GetProperty("style").GetString().Should().Be("airy");
    }

    [Fact]
    public async Task Me_UnknownSiteDefault_FallsBackToFactoryPerField()
    {
        using var app = new TestApp();
        SetSite(app, "theme.defaultMode", "neon");
        SetSite(app, "theme.defaultStyle", "airy");
        var theme = await MeTheme(app.AsUser("sales01"));

        theme.GetProperty("defaultMode").GetString().Should().Be("system");
        theme.GetProperty("defaultStyle").GetString().Should().Be("airy");
    }

    [Fact]
    public async Task Theme_Anonymous_ReturnsExactlyModeAndStyle()
    {
        using var app = new TestApp();
        SetSite(app, "theme.defaultMode", "dark");
        var res = await app.AsUser(null).GetAsync($"{Base}/theme");

        res.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["mode", "style"]);
        body.GetProperty("mode").GetString().Should().Be("dark");
        body.GetProperty("style").GetString().Should().Be("workbench");
    }

    [Fact]
    public async Task Me_DemoSite_CannotSaveAndIgnoresStoredChoice()
    {
        using var app = new DemoSiteApp();
        SetUser(app, "sales01", "dark", null);
        var theme = await MeTheme(app.AsUser("sales01"));

        theme.GetProperty("mode").ValueKind.Should().Be(JsonValueKind.Null);
        theme.GetProperty("canSave").GetBoolean().Should().BeFalse();
        theme.GetProperty("effectiveMode").GetString().Should().Be(theme.GetProperty("defaultMode").GetString());
    }

    private static string? Stored(TestApp app, string userId, bool style = false)
    {
        string? v = null;
        using var scope = app.Services.CreateScope();
        var u = scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<AppUser>().AsNoTracking().Single(x => x.UserId == userId);
        v = style ? u.ThemeStyle : u.ThemeMode;
        return v;
    }

    private static string? Site(TestApp app, string key)
    {
        using var scope = app.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<SystemSetting>().AsNoTracking()
            .SingleOrDefault(s => s.Key == key)?.Value;
    }

    private static Task<HttpResponseMessage> Put(HttpClient c, string path, string? mode, string? style) =>
        c.PutAsJsonAsync($"{Base}{path}", new { mode, style });

    [Fact]
    public async Task PutMe_SavesAndPersists_NullRestoresFollowing()
    {
        using var app = new TestApp();
        var client = app.AsUser("sales01");

        var res = await Put(client, "/me/theme", "dark", "airy");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("effectiveMode").GetString().Should().Be("dark");
        var theme = await MeTheme(client);
        theme.GetProperty("mode").GetString().Should().Be("dark");
        theme.GetProperty("style").GetString().Should().Be("airy");

        (await Put(client, "/me/theme", null, "airy")).StatusCode.Should().Be(HttpStatusCode.OK);
        theme = await MeTheme(client);
        theme.GetProperty("mode").ValueKind.Should().Be(JsonValueKind.Null);
        theme.GetProperty("effectiveMode").GetString().Should().Be("system");
    }

    [Theory]
    [InlineData("neon")]
    [InlineData("")]
    [InlineData("Dark")]
    public async Task PutMe_InvalidValue_Returns400AndSavesNothing(string bad)
    {
        using var app = new TestApp();
        SetUser(app, "sales01", "light", "airy");

        var res = await Put(app.AsUser("sales01"), "/me/theme", bad, "workbench");
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").TryGetProperty("mode", out _).Should().BeTrue();
        Stored(app, "sales01").Should().Be("light");
        Stored(app, "sales01", style: true).Should().Be("airy");
    }

    [Fact]
    public async Task PutSystem_AdminOnly_FollowersChange_OverridesStay()
    {
        using var app = new TestApp();
        SetUser(app, "sales01", null, "industrial");

        (await Put(app.AsUser("sales01"), "/system/theme", "dark", "enterprise")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var res = await Put(app.AsUser("admin"), "/system/theme", "dark", "enterprise");
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var anon = await app.AsUser(null).GetFromJsonAsync<JsonElement>($"{Base}/theme");
        anon.GetProperty("mode").GetString().Should().Be("dark");
        anon.GetProperty("style").GetString().Should().Be("enterprise");
        var sales = await MeTheme(app.AsUser("sales01"));
        sales.GetProperty("effectiveMode").GetString().Should().Be("dark");
        sales.GetProperty("effectiveStyle").GetString().Should().Be("industrial");
        var other = await MeTheme(app.AsUser("admin"));
        other.GetProperty("effectiveStyle").GetString().Should().Be("enterprise");
    }

    [Theory]
    [InlineData(null, "airy")]
    [InlineData("dark", null)]
    [InlineData("dark", "neon")]
    public async Task PutSystem_NullOrInvalid_Returns400(string? mode, string? style)
    {
        using var app = new TestApp();

        (await Put(app.AsUser("admin"), "/system/theme", mode, style)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Site(app, "theme.defaultMode").Should().BeNull();
        Site(app, "theme.defaultStyle").Should().BeNull();
    }

    [Fact]
    public async Task PutSystem_NonAdminWithInvalidBody_Returns403()
    {
        using var app = new TestApp();

        (await Put(app.AsUser("sales01"), "/system/theme", "neon", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PutMe_Anonymous_Returns401()
    {
        using var app = new TestApp();

        (await Put(app.AsUser(null), "/me/theme", "dark", "airy")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("dark")]
    [InlineData("neon")]
    public async Task DemoSite_BothPuts_Return403EvenForInvalidBody(string mode)
    {
        using var app = new DemoSiteApp();
        SetUser(app, "sales01", "light", "airy");

        foreach (var user in new[] { "admin", "sales01" })
        {
            (await Put(app.AsUser(user), "/me/theme", mode, "airy")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await Put(app.AsUser(user), "/system/theme", mode, "airy")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        Stored(app, "sales01").Should().Be("light");
        Stored(app, "admin").Should().BeNull();
        Site(app, "theme.defaultMode").Should().BeNull();
    }

    [Fact]
    public async Task PutMe_WhileMustChangePassword_IsBlocked()
    {
        using var app = new TestApp(realAuth: true);
        var client = app.CreateClient();
        (await client.PostAsJsonAsync($"{Base}/auth/login", new { userId = "admin", password = "initial-pass-1" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var res = await Put(client, "/me/theme", "dark", "airy");
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().Should().Be("passwordChangeRequired");
        Stored(app, "admin").Should().BeNull();
    }
}
