using System.Net;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

public class EntitiesApiTests : IClassFixture<TestApp>
{
    private readonly TestApp _app;

    public EntitiesApiTests(TestApp app) => _app = app;

    [Fact]
    public async Task List_Admin_ReturnsTwoEntities()
    {
        var res = await _app.AsUser("admin").GetAsync($"{Base}/entities");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var items = (await JsonOf(res)).EnumerateArray().ToList();
        items.Select(i => i.GetProperty("key").GetString()).Should().BeEquivalentTo("design.mold", "sales.customer");
        items.Single(i => i.GetProperty("key").GetString() == "design.mold").GetProperty("name").GetProperty("zh-TW")
            .GetString().Should().Be("模具");
        items.Single(i => i.GetProperty("key").GetString() == "sales.customer").GetProperty("name").GetProperty("zh-TW")
            .GetString().Should().Be("客戶");
    }

    [Fact]
    public async Task List_NonAdmin_Forbidden() =>
        (await _app.AsUser("sales01").GetAsync($"{Base}/entities")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

    [Fact]
    public async Task Light_UnknownEntity_NotFound()
    {
        var res = await _app.AsUser("admin").GetAsync($"{Base}/entities/nope/light");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await JsonOf(res)).GetProperty("message").GetString().Should().Be("既有實體不存在");
    }

    [Fact]
    public async Task Light_AnyUser_Ok()
    {
        var res = await _app.AsUser("floor01").GetAsync($"{Base}/entities/design.mold/light?keyword=上蓋");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await JsonOf(res);
        body.GetProperty("total").GetInt32().Should().Be(1);
        body.GetProperty("items")[0].GetProperty("key").GetString().Should().Be("M-0001");
        body.GetProperty("items")[0].GetProperty("title").GetString().Should().Be("M-0001 外殼上蓋模");
    }
}
