using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.WebAPI.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests;

/// <summary>X-Locale 標頭決定後端訊息的語系（缺值或不認得退回繁中）；併發衝突與分錄凍結確認另帶 code 讓前端判斷。</summary>
public class LocaleApiTests
{
    private const string Auth = "/api/v3/dynamic/auth";

    [Theory]
    [InlineData("en-US", "en-US")]
    [InlineData("th", "th")]
    [InlineData("vi", "vi")]
    [InlineData("zh-TW", "zh-TW")]
    [InlineData("fr-FR", "zh-TW")]
    [InlineData("", "zh-TW")]
    [InlineData(null, "zh-TW")]
    public void XLocale_header_is_used_when_supported_otherwise_zh_TW(string? header, string expected)
    {
        var ctx = new DefaultHttpContext();
        if (header is not null) ctx.Request.Headers["X-Locale"] = header;
        new HeaderCurrentLocale(new HttpContextAccessor { HttpContext = ctx }).Locale.Should().Be(expected);
    }

    [Fact]
    public void Without_a_request_the_locale_is_zh_TW() =>
        new HeaderCurrentLocale(new HttpContextAccessor()).Locale.Should().Be("zh-TW");

    private static HttpRequestMessage Req(HttpMethod method, string url, object? body, string? locale)
    {
        var req = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        if (locale is not null) req.Headers.Add("X-Locale", locale);
        return req;
    }

    [Theory]
    [InlineData(null, "帳號或密碼錯誤，或帳號暫時無法登入")]
    [InlineData("en-US", "Incorrect user ID or password, or the account cannot sign in right now")]
    public async Task Login_failure_message_follows_the_header(string? locale, string expected)
    {
        using var app = new TestApp(realAuth: true);
        var res = await app.CreateClient().SendAsync(Req(HttpMethod.Post, $"{Auth}/login", new { userId = "nobody", password = "x" }, locale));
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await JsonOf(res)).GetProperty("message").GetString().Should().Be(expected);
    }

    [Fact]
    public async Task Session_messages_follow_the_header()
    {
        using var app = new TestApp(realAuth: true);
        var client = app.CreateClient();
        var anonymous = await client.SendAsync(Req(HttpMethod.Get, $"{Base}/modules", null, "en-US"));
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await JsonOf(anonymous)).GetProperty("message").GetString().Should().Be("Please sign in first");

        // realAuth 下 admin 帶著「必須改密碼」
        (await client.PostAsJsonAsync($"{Auth}/login", new { userId = "admin", password = "initial-pass-1" })).StatusCode.Should().Be(HttpStatusCode.OK);
        var blocked = await client.SendAsync(Req(HttpMethod.Get, $"{Base}/modules", null, "en-US"));
        blocked.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await JsonOf(blocked);
        body.GetProperty("message").GetString().Should().Be("Please change your password first");
        body.GetProperty("code").GetString().Should().Be("passwordChangeRequired");

        var zh = await client.GetAsync($"{Base}/modules");
        (await JsonOf(zh)).GetProperty("message").GetString().Should().Be("請先變更密碼");
    }

    [Fact]
    public async Task Change_password_errors_follow_the_header()
    {
        using var app = new TestApp(realAuth: true);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
            db.Set<AppUser>().Add(new AppUser
            {
                UserId = "alice", DisplayName = "alice", IsActive = true,
                PasswordHash = new PasswordHasher<AppUser>().HashPassword(new AppUser(), "alice-pass-1")
            });
            db.SaveChanges();
        }
        var client = app.CreateClient();
        (await client.PostAsJsonAsync($"{Auth}/login", new { userId = "alice", password = "alice-pass-1" })).StatusCode.Should().Be(HttpStatusCode.OK);

        var wrong = await client.SendAsync(Req(HttpMethod.Post, $"{Auth}/change-password", new { oldPassword = "nope-nope-1", newPassword = "brand-new-1" }, "en-US"));
        wrong.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await JsonOf(wrong)).GetProperty("errors").GetProperty("oldPassword")[0].GetString().Should().Be("The current password is incorrect");

        var shortPw = await client.SendAsync(Req(HttpMethod.Post, $"{Auth}/change-password", new { oldPassword = "alice-pass-1", newPassword = "short" }, "en-US"));
        shortPw.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await JsonOf(shortPw)).GetProperty("errors").GetProperty("NewPassword")[0].GetString().Should().Be("The new password must be at least 8 characters");

        var zh = await client.PostAsJsonAsync($"{Auth}/change-password", new { oldPassword = "alice-pass-1", newPassword = "short" });
        (await JsonOf(zh)).GetProperty("errors").GetProperty("NewPassword")[0].GetString().Should().Be("新密碼至少 8 碼");
    }

    [Fact]
    public async Task Stale_update_returns_code_and_localized_message()
    {
        using var app = new TestApp();
        var admin = app.AsUser("admin");
        var key = await CreateModule(admin);
        await Activate(admin, key);
        var title = await TitleKey(admin, key);
        var created = await JsonOf(await admin.PostAsJsonAsync($"{Base}/modules/{key}/records",
            new { values = new Dictionary<string, object> { [title] = "a" } }));
        var url = $"{Base}/records/{created.GetProperty("id").GetInt64()}";

        var en = await admin.SendAsync(Req(HttpMethod.Put, url, new { values = new Dictionary<string, object> { [title] = "b" }, rowVersion = "old" }, "en-US"));
        en.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await JsonOf(en);
        body.GetProperty("code").GetString().Should().Be("stale");
        body.GetProperty("message").GetString().Should().Be("This data was changed by someone else. Please reload");

        var zh = await JsonOf(await admin.SendAsync(Req(HttpMethod.Put, url, new { values = new Dictionary<string, object> { [title] = "b" }, rowVersion = "old" }, null)));
        zh.GetProperty("code").GetString().Should().Be("stale");
        zh.GetProperty("message").GetString().Should().Be("資料已被其他人修改，請重新載入");
    }

    [Fact]
    public async Task Ledger_freeze_confirmation_returns_code_and_count()
    {
        using var app = new TestApp();
        var admin = app.AsUser("admin");
        var key = await CreateModule(admin);
        var qty = await AddField(admin, key, "decimal", new { });
        var reversal = await AddField(admin, key, "reference", new { readOnly = true, refTarget = new { kind = "module", key } });
        await Activate(admin, key);
        var title = await TitleKey(admin, key);
        foreach (var t in new[] { "a", "b" })
            (await admin.PostAsJsonAsync($"{Base}/modules/{key}/records",
                new { values = new Dictionary<string, object> { [title] = t, [qty.Key] = 1 } })).StatusCode.Should().Be(HttpStatusCode.Created);

        var body = new { reversalFieldId = reversal.Id, negate = new[] { qty.Id }, swap = Array.Empty<long[]>(), confirmFreeze = false };
        var res = await admin.SendAsync(Req(HttpMethod.Put, $"{Base}/modules/{key}/ledger", body, "en-US"));
        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var json = await JsonOf(res);
        json.GetProperty("code").GetString().Should().Be("ledgerFreezeConfirm");
        json.GetProperty("params").GetProperty("count").GetInt32().Should().Be(2);
        json.GetProperty("message").GetString().Should().StartWith("This module already has 2 record(s).");

        var zh = await JsonOf(await admin.SendAsync(Req(HttpMethod.Put, $"{Base}/modules/{key}/ledger", body, null)));
        zh.GetProperty("message").GetString().Should()
            .Be("這個模組已有 2 筆記錄，設為分錄後它們會立刻變成不可修改、不可刪除，且設定後不能取消；確認請帶 confirmFreeze");
    }

    [Fact]
    public async Task Shared_module_not_found_literal_is_translated()
    {
        using var app = new TestApp();
        var res = await app.AsUser("admin").SendAsync(Req(HttpMethod.Get, $"{Base}/modules/no-such-module", null, "en-US"));
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await JsonOf(res)).GetProperty("message").GetString().Should().Be("Module not found");
    }
}
