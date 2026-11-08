using System.Net;
using DynamicModules.TestSupport;
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
        // design.mold 已移除（s2d-0）；test.entity 是測試主機註冊的
        items.Select(i => i.GetProperty("key").GetString()).Should().BeEquivalentTo("sales.customer", TestEntityProvider.Key);
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
        var res = await _app.AsUser("floor01").GetAsync($"{Base}/entities/test.entity/light?keyword=甲");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await JsonOf(res);
        body.GetProperty("total").GetInt32().Should().Be(1);
        body.GetProperty("items")[0].GetProperty("key").GetString().Should().Be("E-0001");
        body.GetProperty("items")[0].GetProperty("title").GetString().Should().Be("E-0001 甲號");
    }
}
