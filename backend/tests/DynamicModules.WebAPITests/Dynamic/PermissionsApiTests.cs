using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

public class PermissionsApiTests : IClassFixture<TestApp>
{
    private readonly TestApp _app;

    public PermissionsApiTests(TestApp app) => _app = app;

    [Fact]
    public async Task PutThenGet_UsesCamelCaseEnumStrings()
    {
        var admin = _app.AsUser("admin");
        var key = await CreateModule(admin);
        var memo = await AddField(admin, key, "text");
        var note = await AddField(admin, key, "text");
        var titleId = (await JsonOf(await admin.GetAsync($"{Base}/modules/{key}"))).GetProperty("titleFieldId").GetInt64();
        var groups = await JsonOf(await admin.GetAsync($"{Base}/groups"));
        var salesId = groups.EnumerateArray().Single(g => g.GetProperty("key").GetString() == "sales").GetProperty("id").GetInt64();
        var priv = new { read = true, add = false, edit = false, delete = false, printExport = false, pricing = false, approve = false };

        var put = await admin.PutAsJsonAsync($"{Base}/modules/{key}/permissions", new
        {
            groups = new[] { new { groupId = salesId, privileges = priv, scope = "own" } },
            users = Array.Empty<object>(),
            fields = new[]
            {
                new { fieldId = memo.Id, groupId = salesId, access = "hidden" },
                new { fieldId = note.Id, groupId = salesId, access = "readOnly" }
            },
            options = Array.Empty<object>()
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var got = await JsonOf(await admin.GetAsync($"{Base}/modules/{key}/permissions"));
        var sales = got.GetProperty("groups").EnumerateArray().Single(g => g.GetProperty("groupId").GetInt64() == salesId);
        sales.GetProperty("scope").GetString().Should().Be("own");
        sales.GetProperty("privileges").GetProperty("printExport").GetBoolean().Should().BeFalse();
        var access = got.GetProperty("fields").EnumerateArray()
            .ToDictionary(f => f.GetProperty("fieldId").GetInt64(), f => f.GetProperty("access").GetString());
        access[memo.Id].Should().Be("hidden");
        access[note.Id].Should().Be("readOnly");

        var bad = await admin.PutAsJsonAsync($"{Base}/modules/{key}/permissions", new
        {
            fields = new[] { new { fieldId = titleId, groupId = salesId, access = "hidden" } }
        });
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await JsonOf(bad)).GetProperty("errors").GetProperty("fields[0].access")[0].GetString()
            .Should().Be("標題欄位不能設為隱藏");

        var denied = await _app.AsUser("sales01").PutAsJsonAsync($"{Base}/modules/{key}/permissions", new { });
        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
