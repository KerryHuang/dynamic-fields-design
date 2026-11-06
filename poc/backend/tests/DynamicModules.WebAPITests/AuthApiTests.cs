using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using DynamicModules.Application.Common.Security;
using DynamicModules.Domain.Data;
using DynamicModules.WebAPI.Security;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    public async Task Login_UnknownAccount_LogsFixedMessageNeverTheInput()
    {
        using var app = NewApp();
        AddUser(app, "alice");
        var typedPassword = "Secret-typed-in-account-field\nforged line " + new string('x', 200_000);

        (await PostLogin(app.CreateClient(), typedPassword, "whatever-1")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await PostLogin(app.CreateClient(), "ghost.user", "whatever-1")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var all = string.Join("\n", app.Logs.Entries.Select(e => e.Message));
        all.Should().NotContain("Secret-typed").And.NotContain("forged line").And.NotContain("ghost.user");
        app.Logs.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("登入失敗") && e.Message.Contains("帳號不存在"));
    }

    [Fact]
    public async Task Login_ExistingAccountFailure_LogsStoredUserId()
    {
        using var app = NewApp();
        AddUser(app, "alice");

        await PostLogin(app.CreateClient(), " ALICE ", "wrong-pass-1");

        app.Logs.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("帳號 alice 登入失敗"));
    }

    [Fact]
    public void Bootstrap_InitialAdminPassword_TooShort_FailsStartup()
    {
        using var app = NewApp();
        app.InitialAdminPassword = "short";

        FluentActions.Invoking(() => app.CreateClient()).Should().Throw<Exception>()
            .Where(e => e.ToString().Contains("Auth:InitialAdminPassword"));
    }

    [Fact]
    public async Task Login_FiveFailuresFromSameIp_BlocksThatIpFor15Minutes()
    {
        using var app = NewApp();
        AddUser(app, "alice");
        for (var i = 0; i < 5; i++)
            (await PostLogin(app.CreateClient(), "alice", "wrong-pass-1")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        (await PostLogin(app.CreateClient(), "alice", UserPassword)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        app.Time.Advance(TimeSpan.FromMinutes(15));

        (await PostLogin(app.CreateClient(), "alice", UserPassword)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private const string IpHeader = "X-Test-Client-Ip";

    private static async Task<HttpResponseMessage> PostLoginFrom(WebApplicationFactory<Program> f, string ip, string user, string password)
    {
        var client = f.CreateClient();
        client.DefaultRequestHeaders.Add(IpHeader, ip);
        return await PostLogin(client, user, password);
    }

    private static WebApplicationFactory<Program> WithIpHeader(TestApp app) =>
        app.WithWebHostBuilder(b => b.UseSetting("Auth:ClientIpHeader", IpHeader));

    [Fact]
    public async Task Login_AnonymousFailuresFromOtherIp_CannotLockOutTheRealUser()
    {
        using var app = NewApp();
        using var f = WithIpHeader(app);
        for (var i = 0; i < 10; i++)
            (await PostLoginFrom(f, "203.0.113.9", "admin", "wrong-pass-1")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        (await PostLoginFrom(f, "198.51.100.7", "admin", AdminPassword)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await PostLoginFrom(f, "203.0.113.9", "admin", AdminPassword)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_OneIpSprayingManyAccounts_IsBlockedAfter20Failures()
    {
        using var app = NewApp();
        using var f = WithIpHeader(app);
        AddUser(app, "alice");
        for (var i = 0; i < 20; i++)
            await PostLoginFrom(f, "203.0.113.9", $"ghost{i}", "wrong-pass-1");

        (await PostLoginFrom(f, "203.0.113.9", "alice", UserPassword)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await PostLoginFrom(f, "198.51.100.7", "alice", UserPassword)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Login_ManyIpsGuessingOneAccount_SlowsThatAccountButDoesNotLockIt()
    {
        using var app = NewApp();
        using var f = WithIpHeader(app);
        AddUser(app, "alice");
        for (var i = 0; i < 50; i++)
            await PostLoginFrom(f, $"192.0.2.{i / 4}", "alice", "wrong-pass-1");

        (await PostLoginFrom(f, "198.51.100.7", "alice", UserPassword)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await PostLoginFrom(f, "198.51.100.8", "alice", UserPassword)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "高門檻保底：同帳號 10 秒內只放行一次嘗試");
        app.Time.Advance(TimeSpan.FromSeconds(10));
        (await PostLoginFrom(f, "198.51.100.8", "alice", UserPassword)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Login_ClientIpHeaderNotConfigured_IgnoresSpoofedHeader()
    {
        using var app = NewApp();
        AddUser(app, "alice");
        var client = app.CreateClient();
        for (var i = 0; i < 5; i++)
        {
            client.DefaultRequestHeaders.Remove(IpHeader);
            client.DefaultRequestHeaders.Add(IpHeader, $"192.0.2.{i}");
            await PostLogin(client, "alice", "wrong-pass-1");
        }

        client.DefaultRequestHeaders.Remove(IpHeader);
        client.DefaultRequestHeaders.Add(IpHeader, "192.0.2.99");
        (await PostLogin(client, "alice", UserPassword)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "沒設定 header 時偽造值不得換到新額度");
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

    [Fact]
    public async Task Login_ParallelWrongPasswords_AllRejected_AndNeverRecordedOnTheAccount()
    {
        using var app = NewApp();
        AddUser(app, "alice");

        var results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => PostLogin(app.CreateClient(), "alice", "wrong-pass-1")));

        results.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Unauthorized);
        (await PostLogin(app.CreateClient(), "alice", UserPassword)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var scope = app.Services.CreateScope();
        var user = await scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<AppUser>().AsNoTracking()
            .SingleAsync(u => u.UserId == "alice");
        user.FailedLoginCount.Should().Be(0, "不再累計帳號層級的失敗，也就不會被外人鎖住");
        user.LockedUntil.Should().BeNull();
    }

    [Fact]
    public async Task Login_Success_ResetsFailedCount()
    {
        using var app = NewApp();
        AddUser(app, "alice");
        for (var i = 0; i < 4; i++) await PostLogin(app.CreateClient(), "alice", "wrong-pass-1");

        (await PostLogin(app.CreateClient(), "alice", UserPassword)).StatusCode.Should().Be(HttpStatusCode.OK);

        using (var scope = app.Services.CreateScope())
        {
            var user = await scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<AppUser>().AsNoTracking()
                .SingleAsync(u => u.UserId == "alice");
            user.FailedLoginCount.Should().Be(0);
        }
        (await PostLogin(app.CreateClient(), "alice", "wrong-pass-1")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await PostLogin(app.CreateClient(), "alice", UserPassword)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Login_AfterLockExpires_OneWrongPasswordDoesNotRelock()
    {
        using var app = NewApp();
        AddUser(app, "alice");
        for (var i = 0; i < 5; i++) await PostLogin(app.CreateClient(), "alice", "wrong-pass-1");
        app.Time.Advance(TimeSpan.FromMinutes(15));

        (await PostLogin(app.CreateClient(), "alice", "wrong-pass-1")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        (await PostLogin(app.CreateClient(), "alice", UserPassword)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Login_UnusableAccounts_StillRunOneVerify()
    {
        using var app = NewApp();
        AddUser(app, "disabled", active: false);
        AddUser(app, "locked", lockedUntil: app.Time.GetUtcNow().AddMinutes(10));
        AddUser(app, "nopass", password: null);
        var counting = new CountingPasswordService(new PasswordService());
        using var factory = app.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<IPasswordService>();
            s.AddSingleton<IPasswordService>(counting);
        }));

        foreach (var user in new[] { "ghost", "disabled", "locked", "nopass" })
        {
            var before = counting.VerifyCalls;
            (await PostLogin(factory.CreateClient(), user, UserPassword)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (counting.VerifyCalls - before).Should().Be(1, user);
        }
    }

    [Fact]
    public async Task Bootstrap_ResetAdminPassword_RestoresLockedOutSoleAdmin()
    {
        using var app = NewApp();
        var oldSession = await LoggedIn(app, "admin", AdminPassword);
        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<AppUser>()
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false).SetProperty(u => u.FailedLoginCount, 5)
                    .SetProperty(u => u.LockedUntil, app.Time.GetUtcNow().AddMinutes(10)));
        using var restarted = app.WithWebHostBuilder(b => b.UseSetting("Auth:ResetAdminPassword", "reset-pass-99"));

        var res = await PostLogin(restarted.CreateClient(), "admin", "reset-pass-99");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(res)).GetProperty("mustChangePassword").GetBoolean().Should().BeTrue();
        (await PostLogin(restarted.CreateClient(), "admin", AdminPassword)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await oldSession.GetAsync($"{Base}/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        app.Logs.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("Auth:ResetAdminPassword")
            && !e.Message.Contains("reset-pass-99"));
    }

    [Fact]
    public void Bootstrap_ResetAdminPassword_TooShort_FailsStartup()
    {
        using var app = NewApp();
        _ = app.Services;
        using var restarted = app.WithWebHostBuilder(b => b.UseSetting("Auth:ResetAdminPassword", "short"));

        FluentActions.Invoking(() => restarted.CreateClient()).Should().Throw<Exception>()
            .Where(e => e.ToString().Contains("Auth:ResetAdminPassword"));
    }

    [Fact]
    public async Task Swagger_NotExposedOutsideDevelopment()
    {
        using var app = NewApp();
        using var production = app.WithWebHostBuilder(b => b.UseEnvironment("Production"));

        var res = await production.CreateClient().GetAsync("/swagger/v1/swagger.json");

        res.Content.Headers.ContentType?.MediaType.Should().NotBe("application/json");
        res.StatusCode.Should().NotBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DoubleSlashPrefix_Anonymous_DoesNotLeakUsers()
    {
        using var app = NewApp();

        var res = await app.CreateClient().GetAsync(new Uri("http://localhost//api/v3/dynamic/users"));

        res.StatusCode.Should().NotBe(HttpStatusCode.OK);
        (await res.Content.ReadAsStringAsync()).Should().NotContain("admin");
    }
}

internal sealed class CountingPasswordService(IPasswordService inner) : IPasswordService
{
    private int _verifyCalls;

    public int VerifyCalls => _verifyCalls;

    public string Hash(string password) => inner.Hash(password);

    public bool Verify(string hash, string password)
    {
        Interlocked.Increment(ref _verifyCalls);
        return inner.Verify(hash, password);
    }
}
