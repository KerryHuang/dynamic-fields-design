using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

public class RelationsApiTests(TestApp app) : IClassFixture<TestApp>
{
    [Fact]
    public async Task Related_Endpoints_Roundtrip()
    {
        var admin = app.AsUser();
        var target = await CreateModule(admin);
        await Activate(admin, target);
        var targetTitle = await TitleKey(admin, target);
        var source = await CreateModule(admin);
        var refField = await AddField(admin, source, "reference", new { refTarget = new { kind = "module", key = target } });
        await Activate(admin, source);
        var sourceTitle = await TitleKey(admin, source);

        var rec = await admin.PostAsJsonAsync($"{Base}/modules/{target}/records",
            new { values = new Dictionary<string, object> { [targetTitle] = "申請" } });
        var id = (await JsonOf(rec)).GetProperty("id").GetInt64();
        (await admin.PostAsJsonAsync($"{Base}/modules/{source}/records",
            new { values = new Dictionary<string, object> { [sourceTitle] = "維修", [refField.Key] = id } }))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var tabs = await admin.GetAsync($"{Base}/records/{id}/related");
        tabs.StatusCode.Should().Be(HttpStatusCode.OK);
        var tab = (await JsonOf(tabs))[0];
        tab.GetProperty("fieldId").GetInt64().Should().Be(refField.Id);
        tab.GetProperty("canAdd").GetBoolean().Should().BeTrue();

        var paged = await admin.GetAsync($"{Base}/records/{id}/related/{refField.Id}/paged?page=1&size=20");
        paged.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await JsonOf(paged);
        body.GetProperty("totalCount").GetInt32().Should().Be(1);
        body.GetProperty("items")[0].GetProperty("references").GetProperty(refField.Key)
            .GetProperty("state").GetString().Should().Be("ok");

        (await admin.GetAsync($"{Base}/records/{id}/related/999999/paged")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
