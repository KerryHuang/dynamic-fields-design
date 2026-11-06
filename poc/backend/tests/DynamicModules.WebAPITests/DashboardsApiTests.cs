using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using static DynamicModules.WebAPITests.DashboardTestKit;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests;

public class DashboardsApiTests
{
    private static async Task<(long R1, long R2)> TwoSharedReports(TestApp app, World w)
    {
        var admin = app.AsUser();
        var r1 = await CreateReport(admin, ByStatus(w), "報表一");
        var r2 = await CreateReport(admin, ByStatus(w), "報表二");
        await ShareReport(admin, r1, GroupId(app, "sales"), GroupId(app, "sales-manager"));
        await ShareReport(admin, r2, GroupId(app, "sales"), GroupId(app, "sales-manager"));
        return (r1, r2);
    }

    [Fact]
    public async Task AssignedVisibleToGroupMembers_NotOthers()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app);
        var (r1, _) = await TwoSharedReports(app, w);
        var id = await CreateAssigned(app.AsUser(), "業務總覽", Layout(Card("a", r1)), GroupId(app, "sales"));

        var list = await JsonOf(await app.AsUser("sales01").GetAsync(Dashboards));
        list.GetProperty("assigned").EnumerateArray().Select(d => d.GetProperty("id").GetInt64()).Should().Equal(id);
        (await app.AsUser("sales01").GetAsync($"{Dashboards}/{id}")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await app.AsUser("floor01").GetAsync($"{Dashboards}/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await JsonOf(await app.AsUser("floor01").GetAsync(Dashboards))).GetProperty("assigned").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task MultipleGroups_SeesAll_InSequence()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app);
        var (r1, _) = await TwoSharedReports(app, w);
        var admin = app.AsUser();
        var first = await CreateAssigned(admin, "甲", Layout(Card("a", r1)), GroupId(app, "sales"));
        var second = await CreateAssigned(admin, "乙", Layout(Card("a", r1)), GroupId(app, "sales-manager"));
        var third = await CreateAssigned(admin, "丙", Layout(Card("a", r1)), GroupId(app, "sales"), GroupId(app, "sales-manager"));
        // manager01 只屬於業務主管群組，看到乙、丙
        static long[] Ids(JsonElement list) => list.GetProperty("assigned").EnumerateArray().Select(d => d.GetProperty("id").GetInt64()).ToArray();

        Ids(await JsonOf(await app.AsUser("manager01").GetAsync(Dashboards))).Should().Equal(second, third);
        (await admin.PutAsJsonAsync($"{AdminDashboards}/order", new { ids = new[] { third, second, first } })).StatusCode.Should().Be(HttpStatusCode.OK);

        Ids(await JsonOf(await app.AsUser("manager01").GetAsync(Dashboards))).Should().Equal(third, second);
        Ids(await JsonOf(await app.AsUser("sales01").GetAsync(Dashboards))).Should().Equal(third, first);
    }

    [Fact]
    public async Task EditAssigned_NonAdmin_CreatesPersonal_OthersUnaffected()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app);
        var (r1, r2) = await TwoSharedReports(app, w);
        var id = await CreateAssigned(app.AsUser(), "業務總覽", Layout(Card("a", r1)), GroupId(app, "sales"));
        var sales01 = app.AsUser("sales01");

        var res = await sales01.PutAsJsonAsync($"{Dashboards}/{id}/layout", new { layout = Layout(Card("a", r1, x: 6), Card("b", r2, y: 3)) });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var mine = await GetDashboard(sales01, id);
        CardReportIds(mine).Should().Equal(r1, r2);
        mine.GetProperty("adjusted").GetBoolean().Should().BeTrue();
        mine.GetProperty("hasUpdate").GetBoolean().Should().BeFalse();
        var other = await GetDashboard(app.AsUser("sales02"), id);
        CardReportIds(other).Should().Equal(r1);
        other.GetProperty("adjusted").GetBoolean().Should().BeFalse();
        var list = await JsonOf(await sales01.GetAsync(Dashboards));
        list.GetProperty("assigned")[0].GetProperty("adjusted").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task AdminUpdate_BumpsVersion_PersonalKept_HasUpdateTrue()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app);
        var (r1, r2) = await TwoSharedReports(app, w);
        var admin = app.AsUser();
        var id = await CreateAssigned(admin, "業務總覽", Layout(Card("a", r1)), GroupId(app, "sales"));
        var sales01 = app.AsUser("sales01");
        await sales01.PutAsJsonAsync($"{Dashboards}/{id}/layout", new { layout = Layout(Card("a", r1, x: 6)) });

        var res = await admin.PutAsJsonAsync($"{AdminDashboards}/{id}", new { name = "業務總覽 v2", layout = Layout(Card("a", r1), Card("b", r2)), version = 1 });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonOf(res)).GetProperty("version").GetInt32().Should().Be(2);
        var mine = await GetDashboard(sales01, id);
        CardReportIds(mine).Should().Equal(r1);
        mine.GetProperty("hasUpdate").GetBoolean().Should().BeTrue();
        mine.GetProperty("adjusted").GetBoolean().Should().BeTrue();
        var untouched = await GetDashboard(app.AsUser("sales02"), id);
        CardReportIds(untouched).Should().Equal(r1, r2);
        untouched.GetProperty("name").GetString().Should().Be("業務總覽 v2");
    }

    [Fact]
    public async Task Restore_DeletesPersonal_ReturnsLatest()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app);
        var (r1, r2) = await TwoSharedReports(app, w);
        var admin = app.AsUser();
        var id = await CreateAssigned(admin, "業務總覽", Layout(Card("a", r1)), GroupId(app, "sales"));
        var sales01 = app.AsUser("sales01");
        await sales01.PutAsJsonAsync($"{Dashboards}/{id}/layout", new { layout = Layout(Card("a", r1, x: 6)) });
        await admin.PutAsJsonAsync($"{AdminDashboards}/{id}", new { name = "業務總覽", layout = Layout(Card("a", r1), Card("b", r2)), version = 1 });

        var res = await sales01.PostAsync($"{Dashboards}/{id}/restore", null);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var restored = await JsonOf(res);
        CardReportIds(restored).Should().Equal(r1, r2);
        restored.GetProperty("adjusted").GetBoolean().Should().BeFalse();
        restored.GetProperty("hasUpdate").GetBoolean().Should().BeFalse();
        (await sales01.PostAsync($"{Dashboards}/{id}/restore", null)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Unassign_HidesDashboard_PersonalKept_ReassignRestores()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app);
        var (r1, r2) = await TwoSharedReports(app, w);
        var admin = app.AsUser();
        var sales = GroupId(app, "sales");
        var id = await CreateAssigned(admin, "業務總覽", Layout(Card("a", r1)), sales);
        var sales01 = app.AsUser("sales01");
        await sales01.PutAsJsonAsync($"{Dashboards}/{id}/layout", new { layout = Layout(Card("a", r1), Card("b", r2)) });

        await admin.PutAsJsonAsync($"{AdminDashboards}/{id}/assignments", new { groupIds = Array.Empty<long>() });

        (await sales01.GetAsync($"{Dashboards}/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await JsonOf(await sales01.GetAsync(Dashboards))).GetProperty("assigned").GetArrayLength().Should().Be(0);
        await admin.PutAsJsonAsync($"{AdminDashboards}/{id}/assignments", new { groupIds = new[] { sales } });
        var back = await GetDashboard(sales01, id);
        CardReportIds(back).Should().Equal(r1, r2);
        back.GetProperty("adjusted").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task AdminStaleVersion_409()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app);
        var (r1, _) = await TwoSharedReports(app, w);
        var admin = app.AsUser();
        var id = await CreateAssigned(admin, "業務總覽", Layout(Card("a", r1)), GroupId(app, "sales"));
        (await admin.PutAsJsonAsync($"{AdminDashboards}/{id}", new { name = "改一", layout = Layout(Card("a", r1)), version = 1 })).StatusCode.Should().Be(HttpStatusCode.OK);

        var stale = await admin.PutAsJsonAsync($"{AdminDashboards}/{id}", new { name = "改二", layout = Layout(), version = 1 });

        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await GetDashboard(app.AsUser("sales01"), id)).GetProperty("name").GetString().Should().Be("改一");
    }

    [Fact]
    public async Task NonAdmin_AdminRoutes_403()
    {
        using var app = new TestApp();
        var c = app.AsUser("sales01");

        (await c.GetAsync(AdminDashboards)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await c.GetAsync($"{AdminDashboards}/1")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await c.PostAsJsonAsync(AdminDashboards, new { name = "x", layout = Layout() })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await c.PutAsJsonAsync($"{AdminDashboards}/1", new { name = "x", layout = Layout(), version = 1 })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await c.DeleteAsync($"{AdminDashboards}/1")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await c.PutAsJsonAsync($"{AdminDashboards}/1/assignments", new { groupIds = Array.Empty<long>() })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await c.PutAsJsonAsync($"{AdminDashboards}/order", new { ids = Array.Empty<long>() })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task CannotAddCardOfInvisibleReport_400()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app);
        var admin = app.AsUser();
        var hidden = await CreateReport(admin, ByStatus(w), "只有管理員");
        var sales01 = app.AsUser("sales01");
        var own = (await JsonOf(await sales01.PostAsJsonAsync(Dashboards, new { name = "我的", layout = Layout() }))).GetProperty("id").GetInt64();
        var assigned = await CreateAssigned(admin, "業務總覽", Layout(), GroupId(app, "sales"));

        (await sales01.PutAsJsonAsync($"{Dashboards}/{own}/layout", new { layout = Layout(Card("a", hidden)) })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await sales01.PutAsJsonAsync($"{Dashboards}/{assigned}/layout", new { layout = Layout(Card("a", hidden)) })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await sales01.PostAsJsonAsync(Dashboards, new { name = "新的", layout = Layout(Card("a", hidden)) })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await sales01.PutAsJsonAsync($"{Dashboards}/{own}/layout", new { layout = Layout(Card("a", 99999)) })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        CardReportIds(await GetDashboard(sales01, own)).Should().BeEmpty();
    }

    [Fact]
    public async Task OwnDashboard_NotVisibleToOthers()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app);
        var (r1, _) = await TwoSharedReports(app, w);
        var sales01 = app.AsUser("sales01");
        var res = await sales01.PostAsJsonAsync(Dashboards, new { name = "我的看板", layout = Layout(Card("a", r1)) });
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await JsonOf(res)).GetProperty("id").GetInt64();

        (await app.AsUser("sales02").GetAsync($"{Dashboards}/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await app.AsUser("sales02").PutAsJsonAsync($"{Dashboards}/{id}/layout", new { layout = Layout() })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await app.AsUser("sales02").DeleteAsync($"{Dashboards}/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await JsonOf(await app.AsUser("sales02").GetAsync(Dashboards))).GetProperty("mine").GetArrayLength().Should().Be(0);
        // 管理員也看不到別人自己的
        (await app.AsUser().GetAsync($"{Dashboards}/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await JsonOf(await sales01.GetAsync(Dashboards))).GetProperty("mine")[0].GetProperty("name").GetString().Should().Be("我的看板");
    }

    [Fact]
    public async Task OwnDashboard_SaveRenameDelete()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app);
        var (r1, r2) = await TwoSharedReports(app, w);
        var sales01 = app.AsUser("sales01");
        var id = (await JsonOf(await sales01.PostAsJsonAsync(Dashboards, new { name = "我的看板", layout = Layout(Card("a", r1)) }))).GetProperty("id").GetInt64();

        var saved = await sales01.PutAsJsonAsync($"{Dashboards}/{id}/layout", new { name = "改名", layout = Layout(Card("a", r1), Card("b", r2)) });

        saved.StatusCode.Should().Be(HttpStatusCode.OK);
        var detail = await GetDashboard(sales01, id);
        detail.GetProperty("name").GetString().Should().Be("改名");
        detail.GetProperty("kind").GetString().Should().Be("mine");
        CardReportIds(detail).Should().Equal(r1, r2);
        (await sales01.DeleteAsync($"{Dashboards}/{id}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await sales01.GetAsync($"{Dashboards}/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SharedReports_ListedForViewer_NotOwn()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app);
        var (r1, _) = await TwoSharedReports(app, w);
        var mineReport = await CreateReport(app.AsUser("sales01"), ByStatus(w), "我自己的");

        var list = await JsonOf(await app.AsUser("sales01").GetAsync(Dashboards));

        var shared = list.GetProperty("sharedReports").EnumerateArray().ToList();
        shared.Select(r => r.GetProperty("id").GetInt64()).Should().Contain(r1).And.NotContain(mineReport);
        shared.First().GetProperty("ownerName").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task SaveLayout_InvalidShape_400()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app);
        var (r1, _) = await TwoSharedReports(app, w);
        var sales01 = app.AsUser("sales01");
        var id = (await JsonOf(await sales01.PostAsJsonAsync(Dashboards, new { name = "我的", layout = Layout() }))).GetProperty("id").GetInt64();
        async Task<HttpStatusCode> Save(object layout) => (await sales01.PutAsJsonAsync($"{Dashboards}/{id}/layout", new { layout })).StatusCode;

        (await Save(Layout(Card("a", r1), Card("a", r1)))).Should().Be(HttpStatusCode.BadRequest);           // id 重複
        (await Save(Layout(Card("a", r1, viz: "pie")))).Should().Be(HttpStatusCode.BadRequest);                // 圖表種類
        (await Save(Layout(Card("a", r1, options: new { topN = 0 })))).Should().Be(HttpStatusCode.BadRequest);
        (await Save(Layout(Card("a", r1, viz: "table", options: new { cumulative = true })))).Should().Be(HttpStatusCode.BadRequest);
        (await Save(Layout(Card("a", r1, viz: "bar", options: new { comparePeriods = 3 })))).Should().Be(HttpStatusCode.BadRequest);
        (await Save(Layout(Card("a", r1, options: new { range = new { kind = "lastDays" } })))).Should().Be(HttpStatusCode.BadRequest);
        (await Save(Layout(Card("a", r1, viz: "indicator", options: new { comparePeriods = 3, range = new { kind = "thisMonth" } })))).Should().Be(HttpStatusCode.OK);
        (await sales01.PutAsJsonAsync($"{Dashboards}/{id}/layout", new { name = " ", layout = Layout() })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
