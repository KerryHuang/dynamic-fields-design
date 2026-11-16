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
    public async Task Login_failure_response_is_identical_for_existing_and_unknown_accounts()
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
        foreach (var locale in new string?[] { "en-US", null })
        {
            var wrongPassword = await client.SendAsync(Req(HttpMethod.Post, $"{Auth}/login", new { userId = "alice", password = "wrong-pass-1" }, locale));
            var unknown = await client.SendAsync(Req(HttpMethod.Post, $"{Auth}/login", new { userId = "nobody", password = "wrong-pass-1" }, locale));
            wrongPassword.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            unknown.StatusCode.Should().Be(wrongPassword.StatusCode);
            // 回應 body 逐字相同：不因帳號是否存在而不同，日後有人把失敗原因分成不同的鍵時才擋得住
            (await unknown.Content.ReadAsStringAsync()).Should().Be(await wrongPassword.Content.ReadAsStringAsync(), $"locale {locale ?? "(none)"}");
        }
    }

    [Fact]
    public async Task Empty_new_password_gets_the_table_message_not_the_built_in_english()
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

        var en = await client.SendAsync(Req(HttpMethod.Post, $"{Auth}/change-password", new { oldPassword = "alice-pass-1", newPassword = "" }, "en-US"));
        en.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var messages = (await JsonOf(en)).GetProperty("errors").GetProperty("NewPassword").EnumerateArray().Select(e => e.GetString()).ToList();
        messages.Should().Equal("The new password must be at least 8 characters");

        var zh = await client.SendAsync(Req(HttpMethod.Post, $"{Auth}/change-password", new { oldPassword = "alice-pass-1", newPassword = "" }, null));
        (await JsonOf(zh)).GetProperty("errors").GetProperty("NewPassword").EnumerateArray().Select(e => e.GetString()).Should().Equal("新密碼至少 8 碼");
    }

    [Fact]
    public async Task Admin_user_validators_give_table_messages_for_empty_passwords()
    {
        using var app = new TestApp();
        var admin = app.AsUser("admin");
        var create = await admin.SendAsync(Req(HttpMethod.Post, $"{Base}/admin/users",
            new { userId = "newbie", displayName = "Newbie", password = "", groupIds = Array.Empty<long>(), isSystemAdmin = false }, "en-US"));
        create.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await JsonOf(create)).GetProperty("errors").GetProperty("Password").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("The password must be at least 8 characters");

        var reset = await admin.SendAsync(Req(HttpMethod.Post, $"{Base}/admin/users/sales01/reset-password", new { password = "" }, "en-US"));
        reset.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await JsonOf(reset)).GetProperty("errors").GetProperty("Password").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("The password must be at least 8 characters");
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

    private static Dictionary<string, string> ZhName(string s) => new() { ["zh-TW"] = s };

    [Fact]
    public async Task Workflow_rejections_follow_the_header()
    {
        using var app = new TestApp();
        var admin = app.AsUser("admin");
        var key = await CreateModule(admin);
        var status = await AddField(admin, key, "enum");
        async Task<long> Option(string label) => (await JsonOf(await admin.PostAsJsonAsync($"{Base}/fields/{status.Id}/options", new { label = ZhName(label), color = (string?)null }))).GetProperty("id").GetInt64();
        var draft = await Option("草稿");
        var approved = await Option("已核准");
        await Activate(admin, key);
        var groups = await JsonOf(await admin.GetAsync($"{Base}/groups"));
        long Group(string k) => groups.EnumerateArray().Single(g => g.GetProperty("key").GetString() == k).GetProperty("id").GetInt64();
        var priv = new { read = true, add = true, edit = true, delete = true, printExport = false, pricing = true, approve = false };
        (await admin.PutAsJsonAsync($"{Base}/modules/{key}/permissions", new
        {
            groups = new[] { new { groupId = Group("sales"), privileges = priv, scope = "all" }, new { groupId = Group("sales-manager"), privileges = priv, scope = "all" } },
            users = Array.Empty<object>(), fields = Array.Empty<object>(), options = Array.Empty<object>()
        })).EnsureSuccessStatusCode();
        (await admin.PutAsJsonAsync($"{Base}/modules/{key}/workflow", new
        {
            statusFieldId = status.Id, initialOptionId = draft,
            states = new object[] { new { optionId = draft }, new { optionId = approved, readOnly = true } },
            transitions = new[] { new { from = draft, to = approved, groupIds = new[] { Group("sales-manager") } } }
        })).EnsureSuccessStatusCode();
        var title = await TitleKey(admin, key);

        // 新增不是起始狀態
        var sales = app.AsUser("sales01");
        var notInitial = await sales.SendAsync(Req(HttpMethod.Post, $"{Base}/modules/{key}/records",
            new { values = new Dictionary<string, object> { [title] = "t", [status.Key] = $"opt_{approved}" } }, "en-US"));
        notInitial.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await JsonOf(notInitial)).GetProperty("errors").GetProperty(status.Key)[0].GetString().Should().Be("Only the initial status can be used when creating");

        var created = await JsonOf(await sales.PostAsJsonAsync($"{Base}/modules/{key}/records", new { values = new Dictionary<string, object> { [title] = "t" } }));
        var url = $"{Base}/records/{created.GetProperty("id").GetInt64()}";
        var version = created.GetProperty("rowVersion").GetString();

        // 不在轉移群組
        var denied = await sales.SendAsync(Req(HttpMethod.Put, url, new { values = new Dictionary<string, object> { [status.Key] = $"opt_{approved}" }, rowVersion = version }, "en-US"));
        denied.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await JsonOf(denied)).GetProperty("errors").GetProperty(status.Key)[0].GetString().Should().Be("You do not have permission to make this status change");
        var zh = await sales.SendAsync(Req(HttpMethod.Put, url, new { values = new Dictionary<string, object> { [status.Key] = $"opt_{approved}" }, rowVersion = version }, null));
        (await JsonOf(zh)).GetProperty("errors").GetProperty(status.Key)[0].GetString().Should().Be("你沒有權限執行這個狀態轉移");

        // 核准後是唯讀狀態：不能刪除
        var ok = await app.AsUser("manager01").PutAsJsonAsync(url, new { values = new Dictionary<string, object> { [status.Key] = $"opt_{approved}" }, rowVersion = version });
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        var current = (await JsonOf(await sales.GetAsync(url))).GetProperty("rowVersion").GetString();
        var del = await sales.SendAsync(Req(HttpMethod.Delete, $"{url}?rowVersion={current}", null, "en-US"));
        del.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await JsonOf(del)).GetProperty("message").GetString().Should().Be("Records in this status cannot be deleted");
    }

    [Fact]
    public async Task Blocked_delete_and_missing_record_follow_the_header()
    {
        using var app = new TestApp();
        var admin = app.AsUser("admin");
        var key = await CreateModule(admin);
        var self = await AddField(admin, key, "reference", new { refTarget = new { kind = "module", key } });
        await Activate(admin, key);
        var title = await TitleKey(admin, key);
        async Task<JsonElement> Create(Dictionary<string, object> values) =>
            await JsonOf(await admin.PostAsJsonAsync($"{Base}/modules/{key}/records", new { values }));
        var a = await Create(new() { [title] = "a" });
        var aId = a.GetProperty("id").GetInt64();
        await Create(new() { [title] = "b", [self.Key] = aId });

        var blocked = await admin.SendAsync(Req(HttpMethod.Delete, $"{Base}/records/{aId}?rowVersion={a.GetProperty("rowVersion").GetString()}", null, "en-US"));
        blocked.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await JsonOf(blocked)).GetProperty("message").GetString().Should().Be("Other records still reference this record, so it cannot be deleted");

        var missing = await admin.SendAsync(Req(HttpMethod.Get, $"{Base}/records/999999999", null, "en-US"));
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await JsonOf(missing)).GetProperty("message").GetString().Should().Be("Record not found");
        var missingZh = await admin.SendAsync(Req(HttpMethod.Get, $"{Base}/records/999999999", null, null));
        (await JsonOf(missingZh)).GetProperty("message").GetString().Should().Be("記錄不存在");
    }

    [Fact]
    public async Task Field_value_and_required_errors_follow_the_header()
    {
        using var app = new TestApp();
        var admin = app.AsUser("admin");
        var key = await CreateModule(admin);
        var qty = await AddField(admin, key, "integer");
        var email = await AddField(admin, key, "text", new { maxLength = 3 });
        await Activate(admin, key);
        var title = await TitleKey(admin, key);

        var res = await admin.SendAsync(Req(HttpMethod.Post, $"{Base}/modules/{key}/records",
            new { values = new Dictionary<string, object> { [qty.Key] = "x", [email.Key] = "abcd" } }, "en-US"));
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var errors = (await JsonOf(res)).GetProperty("errors");
        errors.GetProperty(qty.Key)[0].GetString().Should().Be("Must be an integer");
        errors.GetProperty(email.Key)[0].GetString().Should().Be("Exceeds 3 characters");
        errors.GetProperty(title)[0].GetString().Should().Be("This field is required");

        var zh = await admin.PostAsJsonAsync($"{Base}/modules/{key}/records", new { values = new Dictionary<string, object> { [qty.Key] = "x", [email.Key] = "abcd" } });
        var zhErrors = (await JsonOf(zh)).GetProperty("errors");
        zhErrors.GetProperty(qty.Key)[0].GetString().Should().Be("必須是整數");
        zhErrors.GetProperty(email.Key)[0].GetString().Should().Be("超過 3 個字元");
        zhErrors.GetProperty(title)[0].GetString().Should().Be("此欄位必填");
    }
}
