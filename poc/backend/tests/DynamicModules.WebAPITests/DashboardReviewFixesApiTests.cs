using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using static DynamicModules.WebAPITests.DashboardTestKit;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests;

/// <summary>審查修正：個人版卡片的指派來源（spec §7）與第一次存個人版的基準版本（spec §6）。</summary>
public class DashboardReviewFixesApiTests
{
    /// <summary>用 JSON 直接組卡片，才能夾帶 client 偽造的 fromAssigned。</summary>
    private static object CardWith(string id, long reportId, bool? fromAssigned, int x = 0, int y = 0) =>
        fromAssigned is null
            ? Card(id, reportId, x, y)
            : new { id, reportId, title = (string?)null, viz = "bar", x, y, w = 6, h = 3, options = new { }, fromAssigned };

    private static async Task<(long Assigned, long Shared)> Reports(TestApp app, World w)
    {
        var admin = app.AsUser();
        var assigned = await CreateReport(admin, ByStatus(w), "只在指派版");   // 沒分享給任何人
        var shared = await CreateReport(admin, ByStatus(w), "分享的");
        await ShareReport(admin, shared, GroupId(app, "sales"));
        return (assigned, shared);
    }

    private static async Task<JsonElement> DrillCard(HttpClient c, long dashboard, string card, params string[] keys)
    {
        var res = await c.PostAsJsonAsync($"{Dashboards}/{dashboard}/cards/{card}/drill", new { groupKeys = keys });
        res.EnsureSuccessStatusCode();
        return await JsonOf(res);
    }

    private static string StateOf(JsonElement result) => result.GetProperty("state").GetString()!;

    private static string[] NamesOf(JsonElement detail) =>
        detail.GetProperty("reportNames").EnumerateObject().Select(p => p.Value.GetString()!).Order().ToArray();

    [Fact]
    public async Task AdminRemovesReport_PersonalKeepsCard_StillRunsDrillsAndHasName()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app);
        var (onlyAssigned, shared) = await Reports(app, w);
        var admin = app.AsUser();
        var id = await CreateAssigned(admin, "業務", Layout(Card("a", shared), Card("b", onlyAssigned)), GroupId(app, "sales"));
        var sales01 = app.AsUser("sales01");
        (await sales01.PutAsJsonAsync($"{Dashboards}/{id}/layout", new { layout = Layout(Card("a", shared, x: 6), Card("b", onlyAssigned, y: 3)) }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // 管理員把「只在指派版」的報表移出指派版
        (await admin.PutAsJsonAsync($"{AdminDashboards}/{id}", new { name = "業務", layout = Layout(Card("a", shared)), version = 1 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var detail = await GetDashboard(sales01, id);
        CardReportIds(detail).Should().Contain(onlyAssigned);
        NamesOf(detail).Should().Contain("只在指派版");
        StateOf(await RunCard(sales01, id, "b")).Should().NotBe("unshared");
        StateOf(await DrillCard(sales01, id, "b", "x")).Should().NotBe("unshared");
        // 報表本身仍然看不到，這只是卡片的來源
        (await sales01.GetAsync($"{Base}/reports/{onlyAssigned}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SelfSharedCardInPersonal_AfterShareRevoked_IsUnsharedAndNameHidden()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app);
        var (onlyAssigned, shared) = await Reports(app, w);
        var admin = app.AsUser();
        var id = await CreateAssigned(admin, "業務", Layout(Card("a", onlyAssigned)), GroupId(app, "sales"));
        var sales01 = app.AsUser("sales01");
        // 自己靠分享把 shared 加進個人版
        (await sales01.PutAsJsonAsync($"{Dashboards}/{id}/layout", new { layout = Layout(Card("a", onlyAssigned), Card("s", shared, x: 6)) }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        StateOf(await RunCard(sales01, id, "s")).Should().NotBe("unshared");

        await ShareReport(admin, shared); // 取消分享

        StateOf(await RunCard(sales01, id, "s")).Should().Be("unshared");
        StateOf(await DrillCard(sales01, id, "s", "x")).Should().Be("unshared");
        NamesOf(await GetDashboard(sales01, id)).Should().NotContain("分享的");
        StateOf(await RunCard(sales01, id, "a")).Should().NotBe("unshared"); // 指派版自己的卡片不受影響
    }

    [Fact]
    public async Task ForgedFlag_OnReportTheViewerCannotSee_IsRejected()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app);
        var (onlyAssigned, shared) = await Reports(app, w);
        var secret = await CreateReport(app.AsUser(), ByStatus(w), "別人的");
        var id = await CreateAssigned(app.AsUser(), "業務", Layout(Card("a", shared)), GroupId(app, "sales"));
        var sales01 = app.AsUser("sales01");

        var res = await sales01.PutAsJsonAsync($"{Dashboards}/{id}/layout",
            new { layout = Layout(CardWith("a", shared, null), CardWith("x", secret, true, x: 6)) });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var detail = await GetDashboard(sales01, id);
        CardReportIds(detail).Should().Equal(shared);
        (await sales01.PostAsJsonAsync($"{Dashboards}/{id}/cards/x/run", new { })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        NamesOf(detail).Should().NotContain("別人的");
        _ = onlyAssigned;
    }

    [Fact]
    public async Task ForgedFlag_OnRevokedSelfSharedCard_StaysUnshared()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app);
        var (onlyAssigned, shared) = await Reports(app, w);
        var admin = app.AsUser();
        var id = await CreateAssigned(admin, "業務", Layout(Card("a", onlyAssigned)), GroupId(app, "sales"));
        var sales01 = app.AsUser("sales01");
        await sales01.PutAsJsonAsync($"{Dashboards}/{id}/layout", new { layout = Layout(Card("a", onlyAssigned), Card("s", shared, x: 6)) });
        await ShareReport(admin, shared);

        // 卡片還在版面裡所以存檔會過，但 client 夾帶的旗標不能讓它變回看得到
        var res = await sales01.PutAsJsonAsync($"{Dashboards}/{id}/layout",
            new { layout = Layout(CardWith("a", onlyAssigned, null), CardWith("s", shared, true, x: 6)) });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var run = await RunCard(sales01, id, "s");
        StateOf(run).Should().Be("unshared");
        run.TryGetProperty("groups", out _).Should().BeFalse();
        NamesOf(await GetDashboard(sales01, id)).Should().NotContain("分享的");
    }

    [Fact]
    public async Task ForgedFlag_InMyOwnDashboard_NeverBypassesSharing()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app);
        var (onlyAssigned, _) = await Reports(app, w);
        var sales01 = app.AsUser("sales01");

        var created = await sales01.PostAsJsonAsync(Dashboards, new { name = "我的", layout = Layout(CardWith("c", onlyAssigned, true)) });

        created.StatusCode.Should().Be(HttpStatusCode.BadRequest); // 看不到的報表放不上去
    }

    [Fact]
    public async Task ForgedFlag_AdminRoutesDoNotStoreClientValue()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app);
        var (onlyAssigned, shared) = await Reports(app, w);
        var admin = app.AsUser();
        var id = await CreateAssigned(admin, "業務", Layout(CardWith("a", shared, true)), GroupId(app, "sales"));
        // 管理員把 shared 移出，再讓 sales01 第一次存檔：旗標是伺服器依指派版算的，不是 client 送的 true
        (await admin.PutAsJsonAsync($"{AdminDashboards}/{id}", new { name = "業務", layout = Layout(Card("a", onlyAssigned)), version = 1 })).EnsureSuccessStatusCode();
        var detail = await JsonOf(await admin.GetAsync($"{AdminDashboards}/{id}"));

        detail.GetProperty("layout").GetProperty("cards")[0].GetProperty("fromAssigned").GetBoolean().Should().BeFalse();
    }

    // ---- 第一次存個人版的基準版本 ----

    private static async Task<(TestApp App, long Id, HttpClient Sales)> AssignedAtV2()
    {
        var app = new TestApp();
        var w = await MakeWorld(app);
        var (_, shared) = await Reports(app, w);
        var admin = app.AsUser();
        var id = await CreateAssigned(admin, "業務", Layout(Card("a", shared)), GroupId(app, "sales"));
        (await admin.PutAsJsonAsync($"{AdminDashboards}/{id}", new { name = "業務 v2", layout = Layout(Card("a", shared), Card("b", shared, y: 3)), version = 1 }))
            .EnsureSuccessStatusCode();
        return (app, id, app.AsUser("sales01"));
    }

    [Fact]
    public async Task FirstSave_WithLoadedVersion_KeepsUpdateNotice()
    {
        var (app, id, sales) = await AssignedAtV2();
        using var keep = app;
        var shared = CardReportIds(await GetDashboard(sales, id))[0];

        // 使用者載入的是 v1（管理員之後才改成 v2），第一次存檔帶 version=1
        var res = await sales.PutAsJsonAsync($"{Dashboards}/{id}/layout", new { layout = Layout(Card("a", shared, x: 6)), version = 1 });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var mine = await GetDashboard(sales, id);
        mine.GetProperty("hasUpdate").GetBoolean().Should().BeTrue();
        (await JsonOf(res)).GetProperty("hasUpdate").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task FirstSave_WithoutVersion_UsesCurrent()
    {
        var (app, id, sales) = await AssignedAtV2();
        using var keep = app;
        var shared = CardReportIds(await GetDashboard(sales, id))[0];

        (await sales.PutAsJsonAsync($"{Dashboards}/{id}/layout", new { layout = Layout(Card("a", shared, x: 6)) })).EnsureSuccessStatusCode();

        (await GetDashboard(sales, id)).GetProperty("hasUpdate").GetBoolean().Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3)]
    public async Task FirstSave_VersionOutOfRange_Invalid_NothingSaved(int version)
    {
        var (app, id, sales) = await AssignedAtV2();
        using var keep = app;
        var shared = CardReportIds(await GetDashboard(sales, id))[0];

        var res = await sales.PutAsJsonAsync($"{Dashboards}/{id}/layout", new { layout = Layout(Card("a", shared, x: 6)), version });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await JsonOf(res)).GetProperty("errors").TryGetProperty("version", out _).Should().BeTrue();
        (await GetDashboard(sales, id)).GetProperty("adjusted").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task SecondSave_DoesNotMoveBaseVersion()
    {
        var (app, id, sales) = await AssignedAtV2();
        using var keep = app;
        var shared = CardReportIds(await GetDashboard(sales, id))[0];
        await sales.PutAsJsonAsync($"{Dashboards}/{id}/layout", new { layout = Layout(Card("a", shared, x: 6)), version = 1 });

        // 已有個人版：之後存檔帶什麼版本都不會讓提示消失
        (await sales.PutAsJsonAsync($"{Dashboards}/{id}/layout", new { layout = Layout(Card("a", shared, x: 3)), version = 2 })).EnsureSuccessStatusCode();

        (await GetDashboard(sales, id)).GetProperty("hasUpdate").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task OwnDashboard_VersionIsIgnored()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app);
        var (_, shared) = await Reports(app, w);
        var sales01 = app.AsUser("sales01");
        var id = (await JsonOf(await sales01.PostAsJsonAsync(Dashboards, new { name = "我的", layout = Layout(Card("c", shared)) }))).GetProperty("id").GetInt64();

        var res = await sales01.PutAsJsonAsync($"{Dashboards}/{id}/layout", new { layout = Layout(Card("c", shared, x: 6)), version = 99 });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
