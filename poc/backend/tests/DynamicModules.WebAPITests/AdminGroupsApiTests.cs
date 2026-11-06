using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Persistence.Seed;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests;

public class AdminGroupsApiTests
{
    private const string AdminGroups = Base + "/admin/groups";

    private static T Db<T>(TestApp app, Func<DynamicDbContext, T> read)
    {
        using var scope = app.Services.CreateScope();
        return read(scope.ServiceProvider.GetRequiredService<DynamicDbContext>());
    }

    private static long GroupId(TestApp app, string key) => Db(app, db => db.Set<PermissionGroup>().Single(g => g.Key == key).Id);

    private static async Task<long> CreateGroup(TestApp app, string name, string locale = "zh-TW")
    {
        var res = await app.AsUser().PostAsJsonAsync(AdminGroups, new { name, locale });
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await JsonOf(res)).GetProperty("id").GetInt64();
    }

    private static object Priv(bool read = true) =>
        new { read, add = false, edit = false, delete = false, printExport = false, pricing = false, approve = false };

    private static Task<HttpResponseMessage> PutPermissions(HttpClient c, string module, object body) =>
        c.PutAsJsonAsync($"{Base}/modules/{module}/permissions", body);

    private static async Task<long> AddOption(HttpClient c, long fieldId, string label)
    {
        var res = await c.PostAsJsonAsync($"{Base}/fields/{fieldId}/options", new { label = new Dictionary<string, string> { ["zh-TW"] = label } });
        res.EnsureSuccessStatusCode();
        return (await JsonOf(res)).GetProperty("id").GetInt64();
    }

    private static int Count(TestApp app, long groupId) => Db(app, db =>
        db.Set<UserGroupMapping>().Count(x => x.GroupId == groupId) + db.Set<GroupProgramPrivilege>().Count(x => x.GroupId == groupId)
        + db.Set<FieldPermission>().Count(x => x.GroupId == groupId) + db.Set<ModuleDataScope>().Count(x => x.GroupId == groupId)
        + db.Set<FieldOptionScope>().Count(x => x.GroupId == groupId));

    [Fact]
    public async Task AdminEndpoints_NonAdmin_403()
    {
        using var app = new TestApp();
        var c = app.AsUser("sales01");

        (await c.GetAsync(AdminGroups)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await c.PostAsJsonAsync(AdminGroups, new { name = "x", locale = "zh-TW" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await c.PutAsJsonAsync($"{AdminGroups}/1", new { name = "x", locale = "zh-TW" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await c.PutAsJsonAsync($"{AdminGroups}/1/members", new { userIds = Array.Empty<string>() })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await c.GetAsync($"{AdminGroups}/1/impact")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await c.DeleteAsync($"{AdminGroups}/1")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Create_GeneratesKey_ShowsInGroups()
    {
        using var app = new TestApp();

        var id = await CreateGroup(app, "稽核組");

        var key = Db(app, db => db.Set<PermissionGroup>().Single(g => g.Id == id).Key);
        key.Should().MatchRegex("^g-[a-z0-9]{8}$");
        var list = await JsonOf(await app.AsUser().GetAsync($"{Base}/groups"));
        list.EnumerateArray().Should().Contain(g => g.GetProperty("key").GetString() == key);
        var admin = await JsonOf(await app.AsUser().GetAsync(AdminGroups));
        var row = admin.EnumerateArray().Single(g => g.GetProperty("id").GetInt64() == id);
        row.GetProperty("nameI18n").GetProperty("zh-TW").GetString().Should().Be("稽核組");
        row.GetProperty("memberUserIds").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Create_DuplicateNameInLocale_400()
    {
        using var app = new TestApp();
        await CreateGroup(app, "稽核組");

        (await app.AsUser().PostAsJsonAsync(AdminGroups, new { name = " 稽核組 ", locale = "zh-TW" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        // 不同語系可同名
        (await app.AsUser().PostAsJsonAsync(AdminGroups, new { name = "稽核組", locale = "en-US" })).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_EmptyName_400(string name)
    {
        using var app = new TestApp();

        (await app.AsUser().PostAsJsonAsync(AdminGroups, new { name, locale = "zh-TW" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Rename_KeepsKeyAndOtherLocales()
    {
        using var app = new TestApp();
        var id = GroupId(app, "sales");
        var before = Db(app, db => db.Set<PermissionGroup>().Single(g => g.Id == id));
        var zh = before.NameI18n.Values["zh-TW"];

        (await app.AsUser().PutAsJsonAsync($"{AdminGroups}/{id}", new { name = "Sales Team", locale = "en-US" })).StatusCode.Should().Be(HttpStatusCode.OK);

        var after = Db(app, db => db.Set<PermissionGroup>().Single(g => g.Id == id));
        after.Key.Should().Be("sales");
        after.NameI18n.Values["zh-TW"].Should().Be(zh);
        after.NameI18n.Values["en-US"].Should().Be("Sales Team");
    }

    [Fact]
    public async Task Rename_DuplicateInLocale_400_UnknownGroup_404()
    {
        using var app = new TestApp();
        var a = await CreateGroup(app, "甲組");
        await CreateGroup(app, "乙組");

        (await app.AsUser().PutAsJsonAsync($"{AdminGroups}/{a}", new { name = "乙組", locale = "zh-TW" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await app.AsUser().PutAsJsonAsync($"{AdminGroups}/{a}", new { name = "甲組", locale = "zh-TW" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await app.AsUser().PutAsJsonAsync($"{AdminGroups}/99999", new { name = "丙", locale = "zh-TW" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SetMembers_ReplacesMembers_SurvivesRestart()
    {
        using var app = new TestApp();
        var id = await CreateGroup(app, "稽核組");
        var c = app.AsUser();

        (await c.PutAsJsonAsync($"{AdminGroups}/{id}/members", new { userIds = new[] { "sales01", "sales02" } })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await c.PutAsJsonAsync($"{AdminGroups}/{id}/members", new { userIds = new[] { "sales02", "floor01" } })).StatusCode.Should().Be(HttpStatusCode.OK);

        using (var scope = app.Services.CreateScope())
            await SeedData.EnsureSeededAsync(scope.ServiceProvider.GetRequiredService<DynamicDbContext>());
        var row = (await JsonOf(await c.GetAsync(AdminGroups))).EnumerateArray().Single(g => g.GetProperty("id").GetInt64() == id);
        row.GetProperty("memberUserIds").EnumerateArray().Select(x => x.GetString()).Should().BeEquivalentTo(["sales02", "floor01"]);
    }

    [Fact]
    public async Task SetMembers_MissingUserIds_400_AndMembersKept()
    {
        using var app = new TestApp();
        var id = await CreateGroup(app, "稽核組");
        var c = app.AsUser();
        await c.PutAsJsonAsync($"{AdminGroups}/{id}/members", new { userIds = new[] { "sales01" } });

        (await c.PutAsJsonAsync($"{AdminGroups}/{id}/members", new { })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        Db(app, db => db.Set<UserGroupMapping>().Where(m => m.GroupId == id).Select(m => m.UserId).ToList()).Should().BeEquivalentTo(["sales01"]);
    }

    [Fact]
    public async Task SetMembers_UnknownUser_400_NothingChanged()
    {
        using var app = new TestApp();
        var id = await CreateGroup(app, "稽核組");
        var c = app.AsUser();
        await c.PutAsJsonAsync($"{AdminGroups}/{id}/members", new { userIds = new[] { "sales01" } });

        (await c.PutAsJsonAsync($"{AdminGroups}/{id}/members", new { userIds = new[] { "sales02", "ghost" } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        Db(app, db => db.Set<UserGroupMapping>().Where(m => m.GroupId == id).Select(m => m.UserId).ToList()).Should().BeEquivalentTo(["sales01"]);
        (await c.PutAsJsonAsync($"{AdminGroups}/99999/members", new { userIds = Array.Empty<string>() })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task NewGroup_GrantedModule_MemberGetsAccess()
    {
        using var app = new TestApp();
        var admin = app.AsUser();
        var module = await CreateModule(admin);
        await Activate(admin, module);
        var id = await CreateGroup(app, "稽核組");
        await admin.PutAsJsonAsync($"{AdminGroups}/{id}/members", new { userIds = new[] { "floor01" } });
        var before = await JsonOf(await app.AsUser("floor01").GetAsync($"{Base}/menu"));
        before.EnumerateArray().Select(m => m.GetProperty("key").GetString()).Should().NotContain(module);

        (await PutPermissions(admin, module, new
        {
            groups = new[] { new { groupId = id, privileges = Priv(), scope = "all" } },
            users = Array.Empty<object>(), fields = Array.Empty<object>(), options = Array.Empty<object>()
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        var menu = await JsonOf(await app.AsUser("floor01").GetAsync($"{Base}/menu"));
        menu.EnumerateArray().Select(m => m.GetProperty("key").GetString()).Should().Contain(module);
    }

    [Fact]
    public async Task UserWithNoGroups_SeesNoModules()
    {
        using var app = new TestApp();
        var admin = app.AsUser();
        var module = await CreateModule(admin);
        await Activate(admin, module);
        app.GrantBusinessGroups(module);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
            db.Set<AppUser>().Add(new AppUser { UserId = "nogroup", DisplayName = "無群組" });
            db.SaveChanges();
        }

        var menu = await JsonOf(await app.AsUser("nogroup").GetAsync($"{Base}/menu"));

        menu.GetArrayLength().Should().Be(0);
    }

    /// <summary>模組含一個一般欄位與一個選項；回傳模組 key、欄位 id、選項 id。</summary>
    private static async Task<(string Module, long FieldId, long OptionId)> ModuleWithOption(HttpClient admin)
    {
        var module = await CreateModule(admin);
        var field = await AddField(admin, module, "enum");
        var option = await AddOption(admin, field.Id, "外包");
        await Activate(admin, module);
        return (module, field.Id, option);
    }

    [Fact]
    public async Task Impact_CountsAllRelatedRows()
    {
        using var app = new TestApp();
        var admin = app.AsUser();
        var (module, fieldId, optionId) = await ModuleWithOption(admin);
        var id = await CreateGroup(app, "稽核組");
        await admin.PutAsJsonAsync($"{AdminGroups}/{id}/members", new { userIds = new[] { "sales01", "sales02", "floor01" } });
        (await PutPermissions(admin, module, new
        {
            groups = new[] { new { groupId = id, privileges = Priv(), scope = "own" } },
            users = Array.Empty<object>(),
            fields = new[] { new { fieldId, groupId = id, access = "readOnly" } },
            options = new[] { new { optionId, groupIds = new[] { id } } }
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        var impact = await JsonOf(await admin.GetAsync($"{AdminGroups}/{id}/impact"));

        impact.GetProperty("memberCount").GetInt32().Should().Be(3);
        impact.GetProperty("modules").EnumerateArray().Select(x => x.GetString()).Should().BeEquivalentTo(["測試模組"]);
        impact.GetProperty("fieldPermissionCount").GetInt32().Should().Be(1);
        impact.GetProperty("dataScopeCount").GetInt32().Should().Be(1);
        var blocking = impact.GetProperty("blockingOptions").EnumerateArray().Single();
        blocking.GetProperty("module").GetString().Should().Be("測試模組");
        blocking.GetProperty("field").GetString().Should().Be("欄位");
        blocking.GetProperty("option").GetString().Should().Be("外包");
        (await admin.GetAsync($"{AdminGroups}/99999/impact")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_WithOptionScope_409_ListsOptions_NothingDeleted()
    {
        using var app = new TestApp();
        var admin = app.AsUser();
        var (module, fieldId, optionId) = await ModuleWithOption(admin);
        var id = await CreateGroup(app, "稽核組");
        await admin.PutAsJsonAsync($"{AdminGroups}/{id}/members", new { userIds = new[] { "sales01" } });
        await PutPermissions(admin, module, new
        {
            groups = new[] { new { groupId = id, privileges = Priv(), scope = "own" } },
            users = Array.Empty<object>(),
            fields = new[] { new { fieldId, groupId = id, access = "readOnly" } },
            options = new[] { new { optionId, groupIds = new[] { id } } }
        });
        var before = Count(app, id);

        var res = await admin.DeleteAsync($"{AdminGroups}/{id}");

        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await JsonOf(res);
        var blocking = body.GetProperty("blockingOptions").EnumerateArray().Single();
        blocking.GetProperty("option").GetString().Should().Be("外包");
        blocking.GetProperty("module").GetString().Should().Be("測試模組");
        Db(app, db => db.Set<PermissionGroup>().Any(g => g.Id == id)).Should().BeTrue();
        Count(app, id).Should().Be(before);
    }

    [Fact]
    public async Task Delete_RemovesMembershipsPrivilegesFieldPermissionsDataScopes()
    {
        using var app = new TestApp();
        var admin = app.AsUser();
        var module = await CreateModule(admin);
        var field = await AddField(admin, module, "integer");
        await Activate(admin, module);
        var id = await CreateGroup(app, "稽核組");
        var other = await CreateGroup(app, "其他組");
        foreach (var g in new[] { id, other })
            await admin.PutAsJsonAsync($"{AdminGroups}/{g}/members", new { userIds = new[] { "sales01" } });
        (await PutPermissions(admin, module, new
        {
            groups = new[]
            {
                new { groupId = id, privileges = Priv(), scope = "own" },
                new { groupId = other, privileges = Priv(), scope = "all" }
            },
            users = Array.Empty<object>(),
            fields = new[]
            {
                new { fieldId = field.Id, groupId = id, access = "readOnly" },
                new { fieldId = field.Id, groupId = other, access = "hidden" }
            },
            options = Array.Empty<object>()
        })).StatusCode.Should().Be(HttpStatusCode.OK);
        Count(app, id).Should().Be(4);
        var otherBefore = Count(app, other);
        otherBefore.Should().Be(4);

        (await admin.DeleteAsync($"{AdminGroups}/{id}")).StatusCode.Should().Be(HttpStatusCode.OK);

        Db(app, db => db.Set<PermissionGroup>().Any(g => g.Id == id)).Should().BeFalse();
        Count(app, id).Should().Be(0);
        Count(app, other).Should().Be(otherBefore);
        (await admin.DeleteAsync($"{AdminGroups}/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteGroup_RemovesReportShares_AndImpactCountsThem()
    {
        using var app = new TestApp();
        var admin = app.AsUser();
        var id = await CreateGroup(app, "稽核組");
        var other = await CreateGroup(app, "其他組");
        var reportId = Db(app, db =>
        {
            var report = new Report
            {
                OwnerUserId = "sales01", Name = "月營收", DefinitionJson = "{}", RowVersion = Guid.NewGuid().ToString(),
                Shares = [new ReportShare { GroupId = id }, new ReportShare { GroupId = other }]
            };
            db.Set<Report>().Add(report);
            db.SaveChanges();
            return report.Id;
        });

        var impact = await JsonOf(await admin.GetAsync($"{AdminGroups}/{id}/impact"));

        impact.GetProperty("reports").EnumerateArray().Select(x => x.GetString()).Should().BeEquivalentTo(["月營收"]);
        (await admin.DeleteAsync($"{AdminGroups}/{id}")).StatusCode.Should().Be(HttpStatusCode.OK);
        Db(app, db => db.Set<ReportShare>().Count(x => x.GroupId == id)).Should().Be(0);
        Db(app, db => db.Set<ReportShare>().Count(x => x.GroupId == other)).Should().Be(1);
        Db(app, db => db.Set<Report>().Any(r => r.Id == reportId)).Should().BeTrue();
    }

    [Fact]
    public async Task SavePermissions_DeletedGroupId_400()
    {
        using var app = new TestApp();
        var admin = app.AsUser();
        var (module, fieldId, optionId) = await ModuleWithOption(admin);
        var gone = await CreateGroup(app, "將刪除組");
        (await admin.DeleteAsync($"{AdminGroups}/{gone}")).StatusCode.Should().Be(HttpStatusCode.OK);
        var sales = GroupId(app, "sales");
        var empty = new object[0];

        foreach (var body in new object[]
        {
            new { groups = new[] { new { groupId = gone, privileges = Priv(), scope = "all" } }, users = empty, fields = empty, options = empty },
            new { groups = empty, users = empty, fields = new[] { new { fieldId, groupId = gone, access = "readOnly" } }, options = empty },
            new { groups = empty, users = empty, fields = empty, options = new[] { new { optionId, groupIds = new[] { gone } } } },
            // 同時帶一個有效群組：有效的那份也不能寫入
            new { groups = new[] { new { groupId = sales, privileges = Priv(), scope = "all" } }, users = empty,
                  fields = new[] { new { fieldId, groupId = gone, access = "readOnly" } }, options = empty }
        })
        {
            (await PutPermissions(admin, module, body)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        Db(app, db => db.Set<GroupProgramPrivilege>().Count(x => x.GroupId == gone || (x.GroupId == sales && x.ProgramCode ==
            db.Set<Module>().Single(m => m.Key == module).ProgramCode))).Should().Be(0);
        Db(app, db => db.Set<FieldPermission>().Count(x => x.FieldId == fieldId)).Should().Be(0);
        Db(app, db => db.Set<FieldOptionScope>().Count(x => x.OptionId == optionId)).Should().Be(0);
    }

    [Fact]
    public async Task Create_Key_IsUniquePerGroup()
    {
        using var app = new TestApp();
        var a = await CreateGroup(app, "甲");
        var b = await CreateGroup(app, "乙");

        var keys = Db(app, db => db.Set<PermissionGroup>().Where(g => g.Id == a || g.Id == b).Select(g => g.Key).ToList());
        keys.Distinct().Should().HaveCount(2);
        keys.Should().OnlyContain(k => Regex.IsMatch(k, "^g-[a-z0-9]{8}$"));
    }
}
