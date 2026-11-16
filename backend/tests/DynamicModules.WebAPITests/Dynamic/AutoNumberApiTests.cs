using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

public class AutoNumberApiTests : IClassFixture<TestApp>
{
    private readonly HttpClient _sales;
    private readonly HttpClient _admin;
    private readonly TestApp _app;

    public AutoNumberApiTests(TestApp app)
    {
        _app = app;
        _sales = app.AsUser("sales01");
        _admin = app.AsUser();
        app.Clock.Fixed = new DateTime(2026, 11, 3, 10, 0, 0);
    }

    private async Task<(string Module, string Title, string Number, string Other)> Setup(bool required = false)
    {
        var key = await CreateModule(_admin);
        var number = await AddField(_admin, key, "autonumber", new { format = "REP-{yyyyMM}-{0000}" });
        var other = await AddField(_admin, key, "integer", new { required });
        await Activate(_admin, key);
        _app.GrantBusinessGroups(key);
        return (key, await TitleKey(_admin, key), number.Key, other.Key);
    }

    private Task<HttpResponseMessage> Create(string module, Dictionary<string, object> values) =>
        _sales.PostAsJsonAsync($"{Base}/modules/{module}/records", new { values });

    private static async Task<string> NumberOf(HttpResponseMessage res, string key) =>
        (await JsonOf(res)).GetProperty("values").GetProperty(key).GetString()!;

    [Fact]
    public async Task Create_AutoNumber_IncrementsAndFormats()
    {
        var m = await Setup();
        var got = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var res = await Create(m.Module, new() { [m.Title] = "t" });
            res.StatusCode.Should().Be(HttpStatusCode.Created);
            got.Add(await NumberOf(res, m.Number));
        }
        got.Should().Equal("REP-202611-0001", "REP-202611-0002", "REP-202611-0003");
    }

    [Fact]
    public async Task Create_AutoNumber_ResetsNextMonth()
    {
        var m = await Setup();
        (await NumberOf(await Create(m.Module, new() { [m.Title] = "t" }), m.Number)).Should().Be("REP-202611-0001");
        _app.Clock.Fixed = new DateTime(2026, 12, 1, 9, 0, 0);
        (await NumberOf(await Create(m.Module, new() { [m.Title] = "t" }), m.Number)).Should().Be("REP-202612-0001");
    }

    [Fact]
    public async Task Create_AutoNumber_InputIgnored()
    {
        var m = await Setup();
        var res = await Create(m.Module, new() { [m.Title] = "t", [m.Number] = "HACK-1" });
        (await NumberOf(res, m.Number)).Should().Be("REP-202611-0001");
    }

    [Fact]
    public async Task Update_AutoNumber_Unchanged()
    {
        var m = await Setup();
        var created = await JsonOf(await Create(m.Module, new() { [m.Title] = "t", [m.Other] = 1 }));
        var put = await _sales.PutAsJsonAsync($"{Base}/records/{created.GetProperty("id").GetInt64()}", new
        {
            values = new Dictionary<string, object> { [m.Title] = "t", [m.Other] = 2, [m.Number] = "HACK" },
            rowVersion = created.GetProperty("rowVersion").GetString()
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        (await NumberOf(put, m.Number)).Should().Be("REP-202611-0001");
    }

    [Fact]
    public async Task Create_ValidationFails_NumberNotConsumed()
    {
        var m = await Setup(required: true);
        var bad = await Create(m.Module, new() { [m.Title] = "t" });
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var ok = await Create(m.Module, new() { [m.Title] = "t", [m.Other] = 1 });
        (await NumberOf(ok, m.Number)).Should().Be("REP-202611-0001");
    }

    [Fact]
    public async Task Create_AutoNumberIsTitle_TitleSet()
    {
        var key = await CreateModule(_admin);
        var number = await AddField(_admin, key, "autonumber", new { format = "REP-{yyyyMM}-{0000}" });
        var oldTitle = await TitleKey(_admin, key); // 原本的必填文字欄位仍在，建立時要給值
        (await _admin.PutAsJsonAsync($"{Base}/modules/{key}", new
        {
            name = new Dictionary<string, string> { ["zh-TW"] = "測試模組" },
            titleFieldId = number.Id
        })).EnsureSuccessStatusCode();
        await Activate(_admin, key);
        _app.GrantBusinessGroups(key);

        var res = await Create(key, new() { [oldTitle] = "x" });
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        (await JsonOf(res)).GetProperty("title").GetString().Should().Be("REP-202611-0001");
    }
}
