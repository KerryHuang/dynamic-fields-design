using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DynamicModules.WebAPITests;

public class AuthApiTests
{
    private const string Auth = "/api/v3/dynamic/auth";
    private const string Base = "/api/v3/dynamic";
    private const string AdminPassword = "initial-pass-1";
    private const string UserPassword = "alice-pass-1";
    private const string FailureMessage = "帳號或密碼錯誤，或帳號暫時無法登入";

    private static TestApp NewApp() => new(realAuth: true);

    private static void AddUser(TestApp app, string id, string? password = UserPassword,
        bool active = true, DateTimeOffset? lockedUntil = null, bool mustChange = false)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
        db.Set<AppUser>().Add(new AppUser
        {
            UserId = id, DisplayName = id, IsActive = active, LockedUntil = lockedUntil, MustChangePassword = mustChange,
            PasswordHash = password is null ? null : new PasswordHasher<AppUser>().HashPassword(new AppUser(), password)
        });
        db.SaveChanges();
    }

    private static Task<HttpResponseMessage> PostLogin(HttpClient c, string user, string password) =>
        c.PostAsJsonAsync($"{Auth}/login", new { userId = user, password });

    private static async Task<HttpClient> LoggedIn(TestApp app, string user = "alice", string password = UserPassword)
    {
        var client = app.CreateClient();
        (await PostLogin(client, user, password)).StatusCode.Should().Be(HttpStatusCode.OK);
        return client;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage res) =>
        await res.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task XUserHeader_WithoutCookie_Returns401()
    {
        using var app = NewApp();
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-User", "admin");

        (await client.GetAsync($"{Base}/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Users_Anonymous_Returns401()
    {
        using var app = NewApp();

        (await app.CreateClient().GetAsync($"{Base}/users")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_Correct_SetsHttpOnlySameSiteLaxCookie_MeReturnsUser()
    {
        using var app = NewApp();
        var client = app.CreateClient();

        var res = await PostLogin(client, "admin", AdminPassword);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var cookie = res.Headers.GetValues("Set-Cookie").Single().ToLowerInvariant();
        cookie.Should().Contain("httponly").And.Contain("samesite=lax");
        cookie.Should().NotContain("expires=", "不設 IsPersistent，關閉瀏覽器就失效");
        var me = await Json(await client.GetAsync($"{Base}/me"));
        me.GetProperty("userId").GetString().Should().Be("admin");
    }

    [Fact]
    public async Task Login_NormalizesCaseAndWhitespace()
    {
        using var app = NewApp();

        (await PostLogin(app.CreateClient(), " Admin ", AdminPassword)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await PostLogin(app.CreateClient(), "admin", " " + AdminPassword)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await PostLogin(app.CreateClient(), "admin", AdminPassword + " ")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_Failures_AllReturnSameMessage()
    {
        using var app = NewApp();
        AddUser(app, "alice");
        AddUser(app, "disabled", active: false);
        AddUser(app, "locked", lockedUntil: app.Time.GetUtcNow().AddMinutes(10));
        var attempts = new[] { ("ghost", UserPassword), ("alice", "wrong-pass-1"), ("disabled", UserPassword), ("locked", UserPassword) };

        foreach (var (user, password) in attempts)
        {
            var res = await PostLogin(app.CreateClient(), user, password);

            res.StatusCode.Should().Be(HttpStatusCode.Unauthorized, user);
            (await Json(res)).GetProperty("message").GetString().Should().Be(FailureMessage, user);
        }
    }

    [Fact]
    public async Task Login_FiveFailures_Locks15Minutes()
    {
        using var app = NewApp();
        AddUser(app, "alice");
        for (var i = 0; i < 5; i++)
            (await PostLogin(app.CreateClient(), "alice", "wrong-pass-1")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        (await PostLogin(app.CreateClient(), "alice", UserPassword)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        app.Time.Advance(TimeSpan.FromMinutes(15));

        (await PostLogin(app.CreateClient(), "alice", UserPassword)).StatusCode.Should().Be(HttpStatusCode.OK);
        using var scope = app.Services.CreateScope();
        var user = await scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<AppUser>().AsNoTracking()
            .SingleAsync(u => u.UserId == "alice");
        user.FailedLoginCount.Should().Be(0);
        user.LockedUntil.Should().BeNull();
    }

    [Fact]
    public async Task Login_UserWithoutPassword_Fails()
    {
        using var app = NewApp();
        AddUser(app, "nopass", password: null);

        (await PostLogin(app.CreateClient(), "nopass", "")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await PostLogin(app.CreateClient(), "nopass", UserPassword)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task MustChange_BlocksApiExceptAuthAndMe()
    {
        using var app = NewApp();
        var client = await LoggedIn(app, "admin", AdminPassword);

        var menu = await client.GetAsync($"{Base}/menu");
        menu.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await Json(menu);
        body.GetProperty("code").GetString().Should().Be("passwordChangeRequired");
        body.GetProperty("message").GetString().Should().Be("請先變更密碼");
        var me = await client.GetAsync($"{Base}/me");
        me.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(me)).GetProperty("mustChangePassword").GetBoolean().Should().BeTrue();

        var change = await client.PostAsJsonAsync($"{Auth}/change-password",
            new { oldPassword = AdminPassword, newPassword = "brand-new-pass-1" });
        change.StatusCode.Should().Be(HttpStatusCode.OK);

        (await client.GetAsync($"{Base}/menu")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("wrong-old-pass", "brand-new-pass-1")]
    [InlineData(UserPassword, UserPassword)]
    [InlineData(UserPassword, "short")]
    public async Task ChangePassword_Invalid_Returns400(string oldPassword, string newPassword)
    {
        using var app = NewApp();
        AddUser(app, "alice");
        var client = await LoggedIn(app);

        var res = await client.PostAsJsonAsync($"{Auth}/change-password", new { oldPassword, newPassword });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PostLogin(app.CreateClient(), "alice", UserPassword)).StatusCode.Should().Be(HttpStatusCode.OK, "密碼沒有被改");
    }

    [Fact]
    public async Task ChangePassword_InvalidatesOtherSessions_KeepsCurrent()
    {
        using var app = NewApp();
        AddUser(app, "alice");
        var a = await LoggedIn(app);
        var b = await LoggedIn(app);

        (await a.PostAsJsonAsync($"{Auth}/change-password",
            new { oldPassword = UserPassword, newPassword = "brand-new-pass-1" })).StatusCode.Should().Be(HttpStatusCode.OK);

        (await a.GetAsync($"{Base}/me")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await b.GetAsync($"{Base}/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Logout_ThenMe_401()
    {
        using var app = NewApp();
        AddUser(app, "alice");
        var client = await LoggedIn(app);

        (await client.PostAsync($"{Auth}/logout", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await client.GetAsync($"{Base}/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Bootstrap_SecondStartup_DoesNotOverwritePassword()
    {
        using var app = NewApp();
        _ = app.Services; // 第一次啟動
        using var second = app.WithWebHostBuilder(b => b.UseSetting("Auth:InitialAdminPassword", "other-pass-2"));

        (await PostLogin(second.CreateClient(), "admin", AdminPassword)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await PostLogin(second.CreateClient(), "admin", "other-pass-2")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Bootstrap_NoEnvPassword_LogsGeneratedPassword()
    {
        using var app = NewApp();
        app.InitialAdminPassword = null;
        _ = app.Services;

        var warning = app.Logs.Entries.Single(e => e.Level == LogLevel.Warning && e.Message.Contains("初始管理員密碼"));
        var generated = Regex.Match(warning.Message, @"：(\S+)$").Groups[1].Value;
        generated.Should().HaveLength(16);
        (await PostLogin(app.CreateClient(), "admin", generated)).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
