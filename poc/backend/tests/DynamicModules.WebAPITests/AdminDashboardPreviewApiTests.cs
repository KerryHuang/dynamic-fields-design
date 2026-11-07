using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using static DynamicModules.WebAPITests.DashboardTestKit;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests;

/// <summary>指派版編輯時的卡片預覽：用管理員自己的權限算真實數字，不繞過任何權限、不要求卡片已存檔。</summary>
public class AdminDashboardPreviewApiTests
{
    private static readonly DateOnly Today = new(2026, 11, 15);
    private const string Preview = AdminDashboards + "/preview-card";

    private static async Task Seed(TestApp app, World w)
    {
        app.Clock.Fixed = Today.ToDateTime(new TimeOnly(10, 0));
        var admin = app.AsUser();
        for (var i = 0; i < 3; i++) await AddRecord(admin, w, 100, 0, Today);
        for (var i = 0; i < 2; i++) await AddRecord(admin, w, 50, 1, Today);
    }

    private static Task<HttpResponseMessage> Send(HttpClient c, object card, object? filterRange = null) =>
        c.PostAsJsonAsync(Preview, new { card, filterRange });

    private static object CardBody(long report, string viz = "bar", object? options = null) => Card("preview", report, viz: viz, options: options); // 與編輯中的版面同一格式

    private static async Task<JsonElement> Ok(HttpResponseMessage res)
    {
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return await JsonOf(res);
    }

    [Fact]
    public async Task Admin_PreviewsUnsavedCard_WithRealNumbers()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app);
        await Seed(app, w);
        var admin = app.AsUser();
        var report = await CreateReport(admin, ByStatus(w));

        var result = await Ok(await Send(admin, CardBody(report)));

        result.GetProperty("state").GetString().Should().Be("ok");
        TotalOf(result, "n").Should().Be(5);
        TotalOf(result, "s").Should().Be(400);
    }

    [Fact]
    public async Task Preview_AppliesCardOptions_LikeTheRealCard()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app);
        await Seed(app, w);
        var admin = app.AsUser();
        var report = await CreateReport(admin, ByStatus(w));

        var result = await Ok(await Send(admin, CardBody(report, "table", new { topN = 1 })));

        result.GetProperty("groups").GetArrayLength().Should().Be(1);
        TotalOf(result, "n").Should().Be(5);
    }

    [Fact]
    public async Task Preview_SeesWhatAdminSees_NotWhatAssignedGroupSees()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app);
        var admin = app.AsUser();
        await AddRecord(admin, w, 100, 0, Today);                       // 管理員建立
        await AddRecord(app.AsUser("sales01"), w, 50, 0, Today);        // 業務建立
        await Grant(app, w.Main, ("sales", "own"));                     // 業務只看得到自己的
        var report = await CreateReport(admin, ByStatus(w));
        (await ShareReport(admin, report, GroupId(app, "sales"))).EnsureSuccessStatusCode();

        // 系統管理員的資料範圍本來就是全部：預覽 2 筆；業務自己跑同一份報表只有 1 筆。畫面要明講預覽用的是管理員的權限
        TotalOf(await Ok(await Send(admin, CardBody(report))), "n").Should().Be(2);
        var asSales = await app.AsUser("sales01").PostAsJsonAsync($"{Base}/reports/{report}/run", new { });
        TotalOf(await JsonOf(asSales), "n").Should().Be(1);
    }

    [Fact]
    public async Task Preview_ReportAdminCannotSee_IsUnshared_NoNumbers()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app);
        await Seed(app, w);
        var report = await CreateReport(app.AsUser("sales01"), ByStatus(w)); // 業務自己的、沒分享

        var result = await Ok(await Send(app.AsUser(), CardBody(report)));

        result.GetProperty("state").GetString().Should().Be("unshared");
        result.TryGetProperty("total", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Preview_MissingReport_IsReportGone()
    {
        using var app = new TestApp();
        await MakeWorld(app);

        var result = await Ok(await Send(app.AsUser(), CardBody(999_999)));

        result.GetProperty("state").GetString().Should().Be("reportGone");
    }

    [Fact]
    public async Task Preview_NonAdmin_Forbidden()
    {
        using var app = new TestApp();
        var w = await MakeWorld(app);
        var report = await CreateReport(app.AsUser(), ByStatus(w));

        (await Send(app.AsUser("sales01"), CardBody(report))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Preview_BadBody_Is400()
    {
        using var app = new TestApp();
        var res = await app.AsUser().PostAsJsonAsync(Preview, new { card = (object?)null });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
