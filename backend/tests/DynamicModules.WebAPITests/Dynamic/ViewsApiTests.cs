using System.Net;
using System.Net.Http.Json;
using DynamicModules.WebAPITests;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

public class ViewsApiTests(TestApp app) : IClassFixture<TestApp>
{
    private static readonly Dictionary<string, string> Name = new() { ["zh-TW"] = "我的檢視" };

    [Fact]
    public async Task ViewsApi_CrudRoundTrip()
    {
        var sales01 = app.AsUser("sales01");
        var sales02 = app.AsUser("sales02");
        var admin = app.AsUser();
        var key = await CreateModule(admin);
        var field = await AddField(admin, key, "text", new { isFilterable = true, isSortable = true });
        await Activate(admin, key);
        app.GrantBusinessGroups(key);
        var titleId = (await JsonOf(await admin.GetAsync($"{Base}/modules/{key}"))).GetProperty("titleFieldId").GetInt64();

        var create = await sales01.PostAsJsonAsync($"{Base}/modules/{key}/views", new
        {
            name = Name, shared = false, columns = new[] { titleId, field.Id },
            filter = new { all = new[] { new { field = field.Id, op = "eq", value = "x" } } },
            sort = new[] { new { field = field.Id, order = "desc" } }, isDefault = true
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await JsonOf(create);
        var id = created.GetProperty("id").GetInt64();
        created.GetProperty("isOwner").GetBoolean().Should().BeTrue();
        created.GetProperty("sort")[0].GetProperty("order").GetString().Should().Be("desc");
        created.GetProperty("filter").GetProperty("all")[0].GetProperty("op").GetString().Should().Be("eq");
        created.GetProperty("staleFieldIds").GetArrayLength().Should().Be(0);

        var list = await JsonOf(await sales01.GetAsync($"{Base}/modules/{key}/views"));
        list.GetArrayLength().Should().Be(1);
        (await JsonOf(await sales02.GetAsync($"{Base}/modules/{key}/views"))).GetArrayLength().Should().Be(0);

        (await sales02.PutAsJsonAsync($"{Base}/views/{id}", new
        {
            name = Name, columns = new[] { titleId }, sort = Array.Empty<object>(), isDefault = false
        })).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var put = await sales01.PutAsJsonAsync($"{Base}/views/{id}", new
        {
            name = new Dictionary<string, string> { ["zh-TW"] = "改名" }, columns = new[] { titleId },
            sort = Array.Empty<object>(), isDefault = false
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await JsonOf(put);
        updated.GetProperty("name").GetProperty("zh-TW").GetString().Should().Be("改名");
        (!updated.TryGetProperty("filter", out var filter) || filter.ValueKind == System.Text.Json.JsonValueKind.Null)
            .Should().BeTrue("更新時清掉了篩選");

        var bad = await admin.PostAsJsonAsync($"{Base}/modules/{key}/views", new
        {
            name = Name, shared = true, columns = new[] { 999999L }, sort = Array.Empty<object>(), isDefault = false
        });
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await JsonOf(bad)).GetProperty("errors").TryGetProperty("Columns", out _).Should().BeTrue();

        (await sales02.DeleteAsync($"{Base}/views/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await sales01.DeleteAsync($"{Base}/views/{id}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await sales01.DeleteAsync($"{Base}/views/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await sales01.GetAsync($"{Base}/modules/nope-nope/views")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task BeforeToday_ViewFilter_ReturnsOnlyRecordsStrictlyBeforeBusinessToday()
    {
        app.Clock.Fixed = new DateTime(2026, 11, 8, 10, 0, 0);
        var admin = app.AsUser();
        var key = await CreateModule(admin);
        var due = await AddField(admin, key, "date", new { isFilterable = true });
        await Activate(admin, key);
        var title = await TitleKey(admin, key);
        foreach (var (name, date) in new (string, string?)[] { ("昨天", "2026-11-07"), ("今天", "2026-11-08"), ("明天", "2026-11-09"), ("很久以前", "2025-01-01"), ("未填", null) })
        {
            var values = new Dictionary<string, object?> { [title] = name, [due.Key] = date };
            (await admin.PostAsJsonAsync($"{Base}/modules/{key}/records", new { values })).StatusCode.Should().Be(HttpStatusCode.Created, name);
        }
        var titleId = (await JsonOf(await admin.GetAsync($"{Base}/modules/{key}"))).GetProperty("titleFieldId").GetInt64();

        var withValue = await admin.PostAsJsonAsync($"{Base}/modules/{key}/views", new
        {
            name = Name, shared = false, columns = new[] { titleId },
            filter = new { all = new[] { new { field = due.Id, op = "beforeToday", value = (object)"2026-11-08" } } },
            sort = Array.Empty<object>(), isDefault = false
        });
        withValue.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var create = await admin.PostAsJsonAsync($"{Base}/modules/{key}/views", new
        {
            name = Name, shared = false, columns = new[] { titleId },
            filter = new { all = new[] { new { field = due.Id, op = "beforeToday" } } },
            sort = Array.Empty<object>(), isDefault = false
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var filter = (await JsonOf(create)).GetProperty("filter").GetRawText();

        var paged = await JsonOf(await admin.GetAsync(
            $"{Base}/modules/{key}/records/paged?currentPage=1&pagesSize=50&filter={Uri.EscapeDataString(filter)}"));
        paged.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("title").GetString())
            .Should().BeEquivalentTo(["昨天", "很久以前"]);
    }
}
