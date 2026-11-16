using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.DashboardTestKit;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests;

public class DashboardCardsApiTests
{
    private static readonly DateOnly Today = new(2026, 11, 15);

    /// <summary>三組：新單 3 筆（金額 100）、處理中 2 筆（50）、結案 1 筆（10），日期都是今天。</summary>
    private static async Task SeedCounts(TestApp app, World w)
    {
        var admin = app.AsUser();
        for (var i = 0; i < 3; i++) await AddRecord(admin, w, 100, 0, Today);
        for (var i = 0; i < 2; i++) await AddRecord(admin, w, 50, 1, Today);
        await AddRecord(admin, w, 10, 2, Today);
    }

    private static async Task<(long Dashboard, long Report)> Setup(TestApp app, World w, object options, string viz = "bar", Dictionary<string, object?>? definition = null)
    {
        var admin = app.AsUser();
        var report = await CreateReport(admin, definition ?? ByStatus(w));
        var res = await admin.PostAsJsonAsync(Dashboards, new { name = "我的", layout = Layout(Card("c", report, viz: viz, options: options)) });
        res.EnsureSuccessStatusCode();
        return ((await JsonOf(res)).GetProperty("id").GetInt64(), report);
    }

    private static string[] Labels(JsonElement result) =>
        result.GetProperty("groups").EnumerateArray().Select(g => g.GetProperty("groupLabels")[0].GetString()!).ToArray();

    [Fact]
    public async Task TopN_TotalStillAll_NoOtherGroup()
    {
        using var app = new TestApp();
        app.Clock.Fixed = Today.ToDateTime(new TimeOnly(10, 0));
        var w = await MakeWorld(app);
        await SeedCounts(app, w);
        var (id, _) = await Setup(app, w, new { topN = 2 }, "table");

        var result = await RunCard(app.AsUser(), id, "c");

        Labels(result).Should().Equal("新單", "處理中");
        TotalOf(result, "n").Should().Be(6);
        TotalOf(result, "s").Should().Be(410);
    }

    [Fact]
    public async Task TopN_WithSortKey_UsesGivenMeasureAndDirection()
    {
        using var app = new TestApp();
        app.Clock.Fixed = Today.ToDateTime(new TimeOnly(10, 0));
        var w = await MakeWorld(app);
        await SeedCounts(app, w);
        var (id, _) = await Setup(app, w, new { topN = 1, sortKey = "s", sortDesc = false }, "table");

        var result = await RunCard(app.AsUser(), id, "c");

        Labels(result).Should().Equal("結案");
    }

    [Fact]
    public async Task Cumulative_DenominatorIsAllGroups()
    {
        using var app = new TestApp();
        app.Clock.Fixed = Today.ToDateTime(new TimeOnly(10, 0));
        var w = await MakeWorld(app);
        await SeedCounts(app, w);
        var (id, _) = await Setup(app, w, new { cumulative = true, topN = 2 });

        var result = await RunCard(app.AsUser(), id, "c");

        // 前兩名 3、2 筆，分母是全部 6 筆
        var cumulative = result.GetProperty("cumulative").EnumerateArray().Select(v => Math.Round(v.GetDecimal(), 1)).ToArray();
        cumulative.Should().Equal(50.0m, 83.3m);
    }

    [Fact]
    public async Task Compare_ThisMonth_AveragesPreviousNMonths()
    {
        using var app = new TestApp();
        app.Clock.Fixed = Today.ToDateTime(new TimeOnly(10, 0));
        var w = await MakeWorld(app);
        var admin = app.AsUser();
        for (var i = 0; i < 6; i++) await AddRecord(admin, w, 1, 0, new DateOnly(2026, 11, 3));
        for (var i = 0; i < 4; i++) await AddRecord(admin, w, 1, 0, new DateOnly(2026, 10, 20));
        for (var i = 0; i < 2; i++) await AddRecord(admin, w, 1, 0, new DateOnly(2026, 9, 5));
        await AddRecord(admin, w, 1, 0, new DateOnly(2026, 8, 5)); // 第三期以外，不計入
        var (id, _) = await Setup(app, w, new { comparePeriods = 2, range = new { kind = "thisMonth" } }, "indicator");

        var result = await RunCard(admin, id, "c");

        TotalOf(result, "n").Should().Be(6);
        var compare = result.GetProperty("compare");
        compare.GetProperty("previousAverage").GetDecimal().Should().Be(3);
        compare.GetProperty("deltaPercent").GetDecimal().Should().Be(100);
    }

    [Fact]
    public async Task Compare_LastDays30_UsesPrevious30DayWindows()
    {
        using var app = new TestApp();
        app.Clock.Fixed = Today.ToDateTime(new TimeOnly(10, 0));
        var w = await MakeWorld(app);
        var admin = app.AsUser();
        await AddRecord(admin, w, 1, 0, Today);                      // 本期
        await AddRecord(admin, w, 1, 0, Today.AddDays(-30));         // 前一期（16 日起往前 30 天）
        await AddRecord(admin, w, 1, 0, Today.AddDays(-59));
        await AddRecord(admin, w, 1, 0, Today.AddDays(-60));         // 前兩期
        await AddRecord(admin, w, 1, 0, Today.AddDays(-90));         // 第三期，不計入
        var (id, _) = await Setup(app, w, new { comparePeriods = 2, range = new { kind = "lastDays", n = 30 } }, "indicator");

        var result = await RunCard(admin, id, "c");

        TotalOf(result, "n").Should().Be(1);
        result.GetProperty("compare").GetProperty("previousAverage").GetDecimal().Should().Be(1.5m);
    }

    [Fact]
    public async Task Compare_NoPrimaryDate_Null()
    {
        using var app = new TestApp();
        app.Clock.Fixed = Today.ToDateTime(new TimeOnly(10, 0));
        var w = await MakeWorld(app);
        await SeedCounts(app, w);
        var definition = ByStatus(w);
        definition["primaryDateField"] = null;
        var (id, _) = await Setup(app, w, new { comparePeriods = 2, range = new { kind = "thisMonth" } }, "indicator", definition);

        var result = await RunCard(app.AsUser(), id, "c");

        result.TryGetProperty("compare", out _).Should().BeFalse(); // null 不輸出
        TotalOf(result, "n").Should().Be(6);
    }

    [Fact]
    public async Task Range_Precedence_Card_Over_Filter_Over_Report()
    {
        using var app = new TestApp();
        app.Clock.Fixed = Today.ToDateTime(new TimeOnly(10, 0));
        var w = await MakeWorld(app);
        var admin = app.AsUser();
        await AddRecord(admin, w, 1, 0, Today);
        await AddRecord(admin, w, 1, 0, Today.AddDays(-3));
        await AddRecord(admin, w, 1, 0, Today.AddDays(-20));
        var definition = ByStatus(w);
        definition["dateRange"] = new { kind = "lastDays", n = 5 };
        var report = await CreateReport(admin, definition);
        var layout = Layout(
            Card("plain", report, viz: "table"),
            Card("own", report, viz: "table", options: new { range = new { kind = "today" } }));
        var id = (await JsonOf(await admin.PostAsJsonAsync(Dashboards, new { name = "我的", layout }))).GetProperty("id").GetInt64();
        var filter = new { kind = "lastDays", n = 30 };

        TotalOf(await RunCard(admin, id, "plain"), "n").Should().Be(2);                 // 報表自己的近 5 天
        TotalOf(await RunCard(admin, id, "plain", filter), "n").Should().Be(3);         // 篩選蓋過報表
        var own = await RunCard(admin, id, "own", filter);
        TotalOf(own, "n").Should().Be(1);                                               // 卡片自己的區間最優先
        own.GetProperty("appliedRange").GetProperty("kind").GetString().Should().Be("today");
        own.GetProperty("ignoresDateFilter").GetBoolean().Should().BeTrue();
        (await RunCard(admin, id, "plain", filter)).GetProperty("ignoresDateFilter").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task NoPrimaryDate_IgnoresFilter_FlagSet()
    {
        using var app = new TestApp();
        app.Clock.Fixed = Today.ToDateTime(new TimeOnly(10, 0));
        var w = await MakeWorld(app);
        await SeedCounts(app, w);
        var definition = ByStatus(w);
        definition["primaryDateField"] = null;
        var (id, _) = await Setup(app, w, new { }, "table", definition);

        var filtered = await RunCard(app.AsUser(), id, "c", new { kind = "today" });
        var unfiltered = await RunCard(app.AsUser(), id, "c");

        TotalOf(filtered, "n").Should().Be(6);
        filtered.GetProperty("ignoresDateFilter").GetBoolean().Should().BeTrue();
        unfiltered.GetProperty("ignoresDateFilter").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task RunCard_DashboardNotVisible_404()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app);
        var (id, _) = await Setup(app, w, new { });

        var res = await app.AsUser("sales01").PostAsJsonAsync($"{Dashboards}/{id}/cards/c/run", new { });

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await app.AsUser("sales01").PostAsJsonAsync($"{Dashboards}/{id}/cards/c/drill", new { groupKeys = new[] { "x" } })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RunCard_CardNotInEffectiveLayout_404()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app);
        var admin = app.AsUser();
        var r1 = await CreateReport(admin, ByStatus(w));
        var r2 = await CreateReport(admin, ByStatus(w), "另一張");
        await ShareReport(admin, r1, GroupId(app, "sales"));
        await ShareReport(admin, r2, GroupId(app, "sales"));
        var id = await CreateAssigned(admin, "業務", Layout(Card("a", r1), Card("b", r2)), GroupId(app, "sales"));
        var sales01 = app.AsUser("sales01");
        await sales01.PutAsJsonAsync($"{Dashboards}/{id}/layout", new { layout = Layout(Card("a", r1)) }); // 個人版拿掉 b

        (await sales01.PostAsJsonAsync($"{Dashboards}/{id}/cards/a/run", new { })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await sales01.PostAsJsonAsync($"{Dashboards}/{id}/cards/b/run", new { })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await app.AsUser("sales02").PostAsJsonAsync($"{Dashboards}/{id}/cards/b/run", new { })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await sales01.PostAsJsonAsync($"{Dashboards}/{id}/cards/zzz/run", new { })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AssignedCard_ReportNotShared_StillRuns()
    {
        using var app = new TestApp();
        app.Clock.Fixed = Today.ToDateTime(new TimeOnly(10, 0));
        var w = await MakeWorld(app);
        await SeedCounts(app, w);
        var admin = app.AsUser();
        var report = await CreateReport(admin, ByStatus(w)); // 沒有分享給任何人
        var id = await CreateAssigned(admin, "業務", Layout(Card("a", report, viz: "table")), GroupId(app, "sales"));

        var result = await RunCard(app.AsUser("sales01"), id, "a");

        result.GetProperty("state").GetString().Should().Be("ok");
        TotalOf(result, "n").Should().Be(6);
        // 報表本身仍然看不到
        (await app.AsUser("sales01").GetAsync($"{Base}/reports/{report}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Unshared_MyDashboardCard_ReturnsNoLongerShared()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app);
        var admin = app.AsUser();
        var report = await CreateReport(admin, ByStatus(w));
        await ShareReport(admin, report, GroupId(app, "sales"));
        var sales01 = app.AsUser("sales01");
        var id = (await JsonOf(await sales01.PostAsJsonAsync(Dashboards, new { name = "我的", layout = Layout(Card("c", report)) }))).GetProperty("id").GetInt64();
        (await RunCard(sales01, id, "c")).GetProperty("state").GetString().Should().NotBe("unshared");

        await ShareReport(admin, report); // 取消分享

        var result = await RunCard(sales01, id, "c");
        result.GetProperty("state").GetString().Should().Be("unshared");
        result.GetProperty("message").GetString().Should().Contain("不再分享");
        result.TryGetProperty("groups", out _).Should().BeFalse();
    }

    [Fact]
    public async Task AssignedCard_UsesViewerPermissions_DifferentNumbers()
    {
        using var app = new TestApp();
        app.Clock.Fixed = Today.ToDateTime(new TimeOnly(10, 0));
        var w = await MakeWorld(app);
        await Grant(app, w.Main, ("mis", "all"), ("sales", "own"), ("sales-manager", "all"));
        var admin = app.AsUser();
        await AddRecord(admin, w, 100, 0, Today);
        await AddRecord(admin, w, 100, 0, Today);
        await AddRecord(app.AsUser("sales01"), w, 5, 0, Today);
        var report = await CreateReport(admin, ByStatus(w));
        var id = await CreateAssigned(admin, "業務", Layout(Card("a", report, viz: "table")), GroupId(app, "sales"), GroupId(app, "sales-manager"));

        var manager = await RunCard(app.AsUser("manager01"), id, "a");
        var sales = await RunCard(app.AsUser("sales01"), id, "a");

        TotalOf(manager, "n").Should().Be(3);
        TotalOf(sales, "n").Should().Be(1);
        TotalOf(sales, "s").Should().Be(5);
    }

    [Fact]
    public async Task DrillCard_ReturnsDetailRowsOfGroup_ForViewer()
    {
        using var app = new TestApp();
        app.Clock.Fixed = Today.ToDateTime(new TimeOnly(10, 0));
        var w = await MakeWorld(app);
        await SeedCounts(app, w);
        var admin = app.AsUser();
        var definition = ByStatus(w);
        definition["columns"] = new object[] { Ref(w.Amount) };
        var (id, _) = await Setup(app, w, new { }, "table", definition);

        var res = await admin.PostAsJsonAsync($"{Dashboards}/{id}/cards/c/drill", new { groupKeys = new[] { w.Options[0] } });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await JsonOf(res);
        body.GetProperty("detailRows").GetArrayLength().Should().Be(3);
        body.GetProperty("truncated").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task CardSortKey_HiddenField_RestrictedNotPartial()
    {
        using var app = new TestApp();
        app.Clock.Fixed = Today.ToDateTime(new TimeOnly(10, 0));
        var w = await MakeWorld(app);
        await SeedCounts(app, w);
        var sales = GroupId(app, "sales");
        (await app.AsUser().PutAsJsonAsync($"{Base}/modules/{w.Main}/permissions", new
        {
            groups = new[] { new { groupId = sales, privileges = new { read = true, add = true, edit = true, delete = true, printExport = true, pricing = true, approve = false }, scope = "all" } },
            users = Array.Empty<object>(),
            fields = new[] { new { fieldId = w.Amount, groupId = sales, access = "hidden" } },
            options = Array.Empty<object>()
        })).EnsureSuccessStatusCode();
        var admin = app.AsUser();
        var report = await CreateReport(admin, ByStatus(w));
        var sortKey = $"main:{w.Amount}:";
        var id = await CreateAssigned(admin, "業務", Layout(Card("a", report, viz: "table", options: new { sortKey, sortDesc = true })), GroupId(app, "sales"));

        var result = await RunCard(app.AsUser("sales01"), id, "a");

        result.GetProperty("state").GetString().Should().Be("restricted");
        result.TryGetProperty("groups", out _).Should().BeFalse();
    }
}
