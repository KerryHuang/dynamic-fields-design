using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

public class ModulesApiTests : IClassFixture<TestApp>
{
    private readonly HttpClient _admin;

    public ModulesApiTests(TestApp app) => _admin = app.AsUser("admin");

    [Fact]
    public async Task Create_Update_List_Get_Roundtrip()
    {
        var key = await CreateModule(_admin);

        var put = await _admin.PutAsJsonAsync($"{Base}/modules/{key}", new
        {
            name = new Dictionary<string, string> { ["zh-TW"] = "改名" },
            icon = "star",
            section = "sales"
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var got = await JsonOf(await _admin.GetAsync($"{Base}/modules/{key}"));
        got.GetProperty("name").GetProperty("zh-TW").GetString().Should().Be("改名");
        got.GetProperty("status").GetString().Should().Be("Draft");

        var list = await JsonOf(await _admin.GetAsync($"{Base}/modules"));
        list.EnumerateArray().Select(m => m.GetProperty("key").GetString()).Should().Contain(key);
    }

    [Fact]
    public async Task GetUnknownModule_Returns404()
        => (await _admin.GetAsync($"{Base}/modules/nope-nope")).StatusCode.Should().Be(HttpStatusCode.NotFound);

    [Fact]
    public async Task CreateDuplicateKey_Returns409()
    {
        var key = await CreateModule(_admin);
        var res = await _admin.PostAsJsonAsync($"{Base}/modules",
            new { key, name = new Dictionary<string, string> { ["zh-TW"] = "x" } });
        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task ActivateThenDeactivate_Succeeds()
    {
        var key = await CreateModule(_admin);
        (await _admin.PostAsync($"{Base}/modules/{key}/activate", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _admin.PostAsync($"{Base}/modules/{key}/deactivate", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonOf(await _admin.GetAsync($"{Base}/modules/{key}"))).GetProperty("status").GetString().Should().Be("Inactive");
    }

    [Fact]
    public async Task ExtensionModule_ApiRoundtrip_ByHostAndSchema()
    {
        var key = NewKey();
        var res = await _admin.PostAsJsonAsync($"{Base}/modules", new
        {
            key, name = new Dictionary<string, string> { ["zh-TW"] = "模具擴充" }, kind = "extension", hostEntityKey = "test.entity"
        });
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await JsonOf(res);
        created.GetProperty("kind").GetString().Should().Be("extension");
        created.GetProperty("hostEntityKey").GetString().Should().Be("test.entity");

        var dup = await _admin.PostAsJsonAsync($"{Base}/modules", new
        {
            key = NewKey(), name = new Dictionary<string, string> { ["zh-TW"] = "x" }, kind = "extension", hostEntityKey = "test.entity"
        });
        dup.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var (_, noteKey, _) = await AddField(_admin, key, "text");
        await Activate(_admin, key);
        var schema = await JsonOf(await _admin.GetAsync($"{Base}/modules/{key}/schema"));
        schema.GetProperty("kind").GetString().Should().Be("extension");
        schema.GetProperty("hostEntityKey").GetString().Should().Be("test.entity");

        (await _admin.GetAsync($"{Base}/modules/{key}/records/by-host/E-0005")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var rec = await _admin.PostAsJsonAsync($"{Base}/modules/{key}/records",
            new { values = new Dictionary<string, string> { [noteKey] = "x" }, hostKey = "E-0005" });
        rec.StatusCode.Should().Be(HttpStatusCode.Created);
        var got = await JsonOf(await _admin.GetAsync($"{Base}/modules/{key}/records/by-host/E-0005"));
        got.GetProperty("hostKey").GetString().Should().Be("E-0005");
        got.GetProperty("title").GetString().Should().Be("E-0005 戊號");
    }
}
