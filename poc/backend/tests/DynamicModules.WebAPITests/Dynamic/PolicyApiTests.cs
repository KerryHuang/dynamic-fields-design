using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

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
        db.SaveChanges();
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
            .Should().Equal(Rules, "task.maxTasksPerRule", "task.maxRulesPerPackage");
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
