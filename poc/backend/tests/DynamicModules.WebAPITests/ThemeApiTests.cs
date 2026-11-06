using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
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
}
