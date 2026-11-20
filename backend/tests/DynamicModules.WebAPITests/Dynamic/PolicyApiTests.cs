using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;
using DynamicModules.TestSupport;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>政策設定 API（政策設計 §7）：權限、值的驗證、null 回到上一層、生效值。</summary>
public class PolicyApiTests : IDisposable
{
    private const string Rules = "task.maxRulesPerModule";
    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    private T Db<T>(Func<DynamicDbContext, T> read)
    {
        using var scope = _app.Services.CreateScope();
        return read(scope.ServiceProvider.GetRequiredService<DynamicDbContext>());
    }

    private void Grant(string userId, bool edit)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
        var row = db.Set<UserProgramPrivilege>().SingleOrDefault(p => p.UserId == userId && p.ProgramCode == "DYN00000");
        if (row is null)
        {
            row = new UserProgramPrivilege { UserId = userId, ProgramCode = "DYN00000" };
            db.Set<UserProgramPrivilege>().Add(row);
        }
        row.CanRead = true;
        row.CanAdd = edit;
        row.CanEdit = edit;
        row.CanDelete = edit;
        db.SaveForTests();
    }

    private static JsonElement Item(JsonElement list, string code) =>
        list.EnumerateArray().Single(e => e.GetProperty("code").GetString() == code);

    private static Task<HttpResponseMessage> PutSystem(HttpClient c, string code, object body) =>
        c.PutAsJsonAsync($"{Base}/system/policies/{code}", body);

    private static Task<HttpResponseMessage> PutModule(HttpClient c, string key, string code, object body) =>
        c.PutAsJsonAsync($"{Base}/modules/{key}/policies/{code}", body);

    private static async Task<string> FirstError(HttpResponseMessage res, string key) =>
        (await JsonOf(res)).GetProperty("errors").GetProperty(key)[0].GetString()!;

    [Fact]
    public async Task System_Get_AdminOnly()
    {
        (await _app.AsUser("sales01").GetAsync($"{Base}/system/policies")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var res = await _app.AsUser().GetAsync($"{Base}/system/policies");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var list = await JsonOf(res);
        list.EnumerateArray().Select(e => e.GetProperty("code").GetString())
            .Should().Equal(DynamicModules.Domain.Policies.PolicyCatalog.All.Select(d => d.Code));
        var item = Item(list, Rules);
        item.GetProperty("factory").GetInt32().Should().Be(20);
        item.GetProperty("effective").GetInt32().Should().Be(20);
        item.GetProperty("kind").GetString().Should().Be("integer");
        item.GetProperty("scope").GetString().Should().Be("module");
        item.GetProperty("category").GetString().Should().Be("task");
        item.GetProperty("min").GetInt32().Should().Be(1);
        item.GetProperty("max").GetInt32().Should().Be(100);
        Item(list, "task.maxRulesPerPackage").GetProperty("scope").GetString().Should().Be("system");
    }

    [Fact]
    public async Task System_Get_UnsetValueIsAbsent()
    {
        var item = Item(await JsonOf(await _app.AsUser().GetAsync($"{Base}/system/policies")), Rules);
        item.TryGetProperty("value", out _).Should().BeFalse();
        item.TryGetProperty("options", out _).Should().BeFalse();
    }

    [Fact]
    public async Task System_Put_OutOfRange_400WithRange()
    {
        var admin = _app.AsUser();
        var res = await PutSystem(admin, Rules, new { value = 0 });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await FirstError(res, "value")).Should().Be("必須是 1 到 100 的整數");
        (await PutSystem(admin, Rules, new { value = "20" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PutSystem(admin, Rules, new { value = 2.5 })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task System_TimeZone_RoundTrip()
    {
        var admin = _app.AsUser();
        var item = Item(await JsonOf(await admin.GetAsync($"{Base}/system/policies")), "locale.timeZone");
        item.GetProperty("kind").GetString().Should().Be("utcOffset");
        item.GetProperty("factory").GetString().Should().Be("+08:00");
        var options = item.GetProperty("options").EnumerateArray().Select(e => e.GetString()).ToList();
        options.Should().HaveCount(38).And.Contain("+05:45");

        var ok = await PutSystem(admin, "locale.timeZone", new { value = "+07:00" });
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonOf(ok)).GetProperty("value").GetString().Should().Be("+07:00");

        var bad = await PutSystem(admin, "locale.timeZone", new { value = "+07:15" });
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await FirstError(bad, "value")).Should().Be("必須是 UTC 偏移，例如 +08:00");
        (await PutSystem(admin, "locale.timeZone", new { value = 7 })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        Item(await JsonOf(await admin.GetAsync($"{Base}/system/policies")), "organization.maxDepth")
            .GetProperty("factory").GetInt32().Should().Be(10);
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task LocaleRules_DisableDefault_400()
    {
        var res = await PutSystem(_app.AsUser(), "locale.enabled", new { value = new[] { "en-US" } });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await FirstError(res, "value")).Should().Be("預設語系必須是啟用中的語系；要停用它，請先改預設語系");
        Db(db => db.Set<SystemSetting>().Any(s => s.Key == "policy.locale.enabled")).Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task LocaleRules_SwitchToEnglishInOrder()
    {
        var admin = _app.AsUser();
        (await PutSystem(admin, "locale.default", new { value = "en-US" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await PutSystem(admin, "locale.requiredText", new { value = new[] { "en-US" } })).StatusCode.Should().Be(HttpStatusCode.OK);
        var enabled = await PutSystem(admin, "locale.enabled", new { value = new[] { "en-US" } });
        enabled.StatusCode.Should().Be(HttpStatusCode.OK, await enabled.Content.ReadAsStringAsync());
        // 預設語系回到出廠（繁中）時，繁中已不在啟用語系
        var back = await PutSystem(admin, "locale.default", new { value = (string?)null });
        back.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await FirstError(back, "value")).Should().Be("預設語系必須是啟用中的語系；要停用它，請先改預設語系");
        var empty = await PutSystem(admin, "locale.requiredText", new { value = Array.Empty<string>() });
        (await FirstError(empty, "value")).Should().Be("至少要有一種必填語系");
        var notEnabled = await PutSystem(admin, "locale.requiredText", new { value = new[] { "en-US", "th" } });
        (await FirstError(notEnabled, "value")).Should().Be("必填語系必須都是啟用中的語系；要停用它，請先從必填語系拿掉");
        var none = await PutSystem(admin, "locale.enabled", new { value = Array.Empty<string>() });
        (await FirstError(none, "value")).Should().Be("至少要啟用一種語系");
    }

    [Fact]
    public async Task LocaleEnabled_UnsupportedLocale_SaysLocale()
    {
        var admin = _app.AsUser();
        var res = await PutSystem(admin, "locale.enabled", new { value = new[] { "zh-TW", "ja" } });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await FirstError(res, "value")).Should().Be("只能從以下語系挑選：zh-TW、en-US、th、vi");
        var roles = await PutSystem(admin, "task.notify.commented", new { value = new[] { "owner" } });
        (await FirstError(roles, "value")).Should().Be("只能從以下角色挑選：assignee、creator");
        var item = Item(await JsonOf(await admin.GetAsync($"{Base}/system/policies")), "locale.default");
        item.GetProperty("options").EnumerateArray().Select(e => e.GetString()).Should().Equal("zh-TW", "en-US", "th", "vi");
    }

    private sealed class ThreeLocales : DynamicModules.Application.Common.IInstalledLocales
    {
        public IReadOnlyList<string> Codes { get; } = ["zh-TW", "en-US", "ja"];
    }

    [Fact]
    public async Task LocaleOptions_FromInstalledLocales()
    {
        // i4a 的接口：已安裝語系換掉後，語系類政策的選項跟著換
        using var app = new TestApp
        {
            ExtraServices = s =>
            {
                s.RemoveAll<DynamicModules.Application.Common.IInstalledLocales>();
                s.AddSingleton<DynamicModules.Application.Common.IInstalledLocales, ThreeLocales>();
            }
        };
        var admin = app.AsUser();
        var item = Item(await JsonOf(await admin.GetAsync($"{Base}/system/policies")), "locale.enabled");
        item.GetProperty("options").EnumerateArray().Select(e => e.GetString()).Should().Contain("ja");
        var res = await PutSystem(admin, "locale.enabled", new { value = new[] { "zh-TW", "ja" } });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        (await JsonOf(res)).GetProperty("value").EnumerateArray().Select(e => e.GetString()).Should().Equal("zh-TW", "ja");
    }

    [Fact]
    public async Task System_Put_ThenNull_BackToFactory()
    {
        var admin = _app.AsUser();
        var res = await PutSystem(admin, Rules, new { value = 10 });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var body = await JsonOf(res);
        body.GetProperty("effective").GetInt32().Should().Be(10);
        body.GetProperty("value").GetInt32().Should().Be(10);
        Db(db => db.Set<SystemSetting>().Single(s => s.Key == "policy." + Rules).Value).Should().Be("10");

        res = await PutSystem(admin, Rules, new { value = (int?)null });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonOf(res)).GetProperty("effective").GetInt32().Should().Be(20);
        Db(db => db.Set<SystemSetting>().Any(s => s.Key == "policy." + Rules)).Should().BeFalse();
    }

    [Fact]
    public async Task System_Put_EmptyBody_SameAsNull()
    {
        var admin = _app.AsUser();
        (await PutSystem(admin, Rules, new { value = 10 })).StatusCode.Should().Be(HttpStatusCode.OK);
        var res = await PutSystem(admin, Rules, new { });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonOf(res)).GetProperty("effective").GetInt32().Should().Be(20);
    }

    [Fact]
    public async Task System_Put_UnknownCode_404() =>
        (await PutSystem(_app.AsUser(), "task.nope", new { value = 1 })).StatusCode.Should().Be(HttpStatusCode.NotFound);

    [Fact]
    public async Task Module_Get_NeedsDesignerRead()
    {
        var key = await CreateModule(_app.AsUser());
        (await _app.AsUser("sales01").GetAsync($"{Base}/modules/{key}/policies")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        Grant("planner01", edit: false);
        var res = await _app.AsUser("planner01").GetAsync($"{Base}/modules/{key}/policies");
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var list = await JsonOf(res);
        var item = Item(list, Rules);
        item.GetProperty("systemValue").GetInt32().Should().Be(20);
        item.GetProperty("effective").GetInt32().Should().Be(20);
        item.TryGetProperty("override", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Module_Put_NeedsDesignerEdit()
    {
        var key = await CreateModule(_app.AsUser());
        Grant("planner01", edit: false);
        var planner = _app.AsUser("planner01");
        (await PutModule(planner, key, Rules, new { value = 3 })).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        Grant("planner01", edit: true);
        var res = await PutModule(planner, key, Rules, new { value = 3 });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var body = await JsonOf(res);
        body.GetProperty("override").GetInt32().Should().Be(3);
        body.GetProperty("effective").GetInt32().Should().Be(3);
        Db(db => db.Set<ModulePolicy>().Single().ModifiedUserId).Should().Be("planner01");

        res = await PutModule(planner, key, Rules, new { value = (int?)null });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        body = await JsonOf(res);
        body.TryGetProperty("override", out _).Should().BeFalse();
        body.GetProperty("effective").GetInt32().Should().Be(20);
    }

    [Fact]
    public async Task Module_Put_EmptyBody_SameAsNull()
    {
        var admin = _app.AsUser();
        var key = await CreateModule(admin);
        (await PutModule(admin, key, Rules, new { value = 3 })).StatusCode.Should().Be(HttpStatusCode.OK);
        var res = await PutModule(admin, key, Rules, new { });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var body = await JsonOf(res);
        body.TryGetProperty("override", out _).Should().BeFalse();
        body.GetProperty("effective").GetInt32().Should().Be(20);
        Db(db => db.Set<ModulePolicy>().Count()).Should().Be(0);
    }

    [Fact]
    public async Task Module_Put_OutOfRange_400()
    {
        var admin = _app.AsUser();
        var key = await CreateModule(admin);
        var res = await PutModule(admin, key, "task.maxTasksPerRule", new { value = 51 });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await FirstError(res, "value")).Should().Be("必須是 1 到 50 的整數");
    }

    [Fact]
    public async Task Module_Put_UnknownModuleOrCode_404()
    {
        var admin = _app.AsUser();
        var key = await CreateModule(admin);
        (await PutModule(admin, "m-nope", Rules, new { value = 3 })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await PutModule(admin, key, "task.nope", new { value = 3 })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await admin.GetAsync($"{Base}/modules/m-nope/policies")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Module_Put_SystemScope_400()
    {
        var admin = _app.AsUser();
        var key = await CreateModule(admin);
        var res = await PutModule(admin, key, "task.maxRulesPerPackage", new { value = 10 });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await FirstError(res, "code")).Should().Be("這個政策只能在系統設定改，不能由模組覆寫");
        var list = await JsonOf(await admin.GetAsync($"{Base}/modules/{key}/policies"));
        list.EnumerateArray().Select(e => e.GetProperty("code").GetString()).Should().NotContain("task.maxRulesPerPackage");
    }

    /// <summary>真的明細模組：子模組有 Relation = Detail 的參照欄位指向表頭（不是只設前端的 detailOf）。</summary>
    private static async Task<(string Parent, string Child)> DetailModules(HttpClient admin)
    {
        var parent = await CreateModule(admin);
        var child = await CreateModule(admin);
        await AddField(admin, child, "reference", new { refTarget = new { kind = "module", key = parent }, relation = "detail" });
        return (parent, child);
    }

    private static async Task<List<string>> ModuleCodes(HttpClient c, string key)
    {
        var res = await c.GetAsync($"{Base}/modules/{key}/policies");
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return (await JsonOf(res)).EnumerateArray().Select(e => e.GetProperty("code").GetString()!).ToList();
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task Module_Get_DetailModule_ListsOnlyApplicable()
    {
        var admin = _app.AsUser();
        var (parent, child) = await DetailModules(admin);

        var detailCodes = await ModuleCodes(admin, child);
        detailCodes.Should().Contain("form.defaultColumns").And.NotContain(Rules);
        var mainCodes = await ModuleCodes(admin, parent);
        mainCodes.Should().Contain("form.defaultColumns").And.Contain(Rules);
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task Module_Put_TargetMismatch_400()
    {
        var admin = _app.AsUser();
        var (parent, child) = await DetailModules(admin);

        var res = await PutModule(admin, child, Rules, new { value = 3 });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await FirstError(res, "code")).Should().Be("這個政策不適用於這種模組");
        Db(db => db.Set<ModulePolicy>().Count()).Should().Be(0);

        (await PutModule(admin, parent, Rules, new { value = 3 })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await PutModule(admin, child, "form.defaultColumns", new { value = 2 })).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task Module_Put_DetailPolicyOnMainModule_400()
    {
        var admin = _app.AsUser();
        var (parent, child) = await DetailModules(admin);

        var res = await PutModule(admin, parent, "record.maxDetailRows", new { value = 2 });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await FirstError(res, "code")).Should().Be("這個政策不適用於這種模組");
        (await PutModule(admin, child, "record.maxDetailRows", new { value = 2 })).StatusCode.Should().Be(HttpStatusCode.OK);

        (await ModuleCodes(admin, parent)).Should().NotContain("record.maxDetailRows");
        (await ModuleCodes(admin, child)).Should().Contain("record.maxDetailRows");
    }

    [Fact]
    public async Task Effective_AnyLoggedInUser()
    {
        var admin = _app.AsUser();
        var key = await CreateModule(admin);
        (await PutSystem(admin, Rules, new { value = 7 })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await PutModule(admin, key, Rules, new { value = 3 })).StatusCode.Should().Be(HttpStatusCode.OK);
        var sales = _app.AsUser("sales01");

        var res = await sales.GetAsync($"{Base}/policies/effective?module={key}");
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var body = await JsonOf(res);
        body.GetProperty("moduleKey").GetString().Should().Be(key);
        body.GetProperty("values").GetProperty(Rules).GetInt32().Should().Be(3);
        body.GetProperty("values").GetProperty("task.maxRulesPerPackage").GetInt32().Should().Be(50);

        var system = await JsonOf(await sales.GetAsync($"{Base}/policies/effective"));
        system.GetProperty("values").GetProperty(Rules).GetInt32().Should().Be(7);

        (await sales.GetAsync($"{Base}/policies/effective?module=m-nope")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _app.AsUser(null).GetAsync($"{Base}/policies/effective")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
