using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

public class HistoryApiTests(TestApp app) : IClassFixture<TestApp>
{
    private readonly HttpClient _sales = app.AsUser("sales01");
    private readonly HttpClient _admin = app.AsUser();

    [Fact]
    public async Task GetHistory_ReturnsLowercaseOperationAndDisplayName()
    {
        var key = await CreateModule(_admin);
        await Activate(_admin, key);
        app.GrantBusinessGroups(key);
        var title = await TitleKey(_admin, key);
        var create = await _sales.PostAsJsonAsync($"{Base}/modules/{key}/records",
            new { values = new Dictionary<string, object> { [title] = "t" } });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await JsonOf(create)).GetProperty("id").GetInt64();

        var res = await _sales.GetAsync($"{Base}/records/{id}/history?locale=en-US");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await JsonOf(res);
        body.GetProperty("totalCount").GetInt32().Should().Be(1);
        var entry = body.GetProperty("items")[0];
        entry.GetProperty("operation").GetString().Should().Be("create");
        entry.GetProperty("changedUserId").GetString().Should().Be("sales01");
        entry.GetProperty("changedUserName").GetString().Should().Be("業務一");
        entry.TryGetProperty("detail", out _).Should().BeFalse();
    }
}
