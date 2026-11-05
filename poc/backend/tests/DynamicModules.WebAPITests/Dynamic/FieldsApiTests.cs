using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

public class FieldsApiTests : IClassFixture<TestApp>
{
    private readonly HttpClient _admin;

    public FieldsApiTests(TestApp app) => _admin = app.AsUser("admin");

    private static Dictionary<string, string> Zh(string t) => new() { ["zh-TW"] = t };

    [Fact]
    public async Task PutField_ChangeType_Returns400()
    {
        var key = await CreateModule(_admin);
        var f = await AddField(_admin, key, "integer");

        var res = await _admin.PutAsJsonAsync($"{Base}/fields/{f.Id}",
            new { type = "text", label = Zh("欄位"), config = new { }, blockId = f.BlockId });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await JsonOf(res);
        body.GetProperty("errors").GetProperty("Type").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Block_Field_Option_Lifecycle()
    {
        var key = await CreateModule(_admin);

        var blockRes = await _admin.PostAsJsonAsync($"{Base}/modules/{key}/blocks", new { label = Zh("第二區") });
        blockRes.StatusCode.Should().Be(HttpStatusCode.Created);
        var blockId = (await JsonOf(blockRes)).GetProperty("id").GetInt64();
        (await _admin.PutAsJsonAsync($"{Base}/blocks/{blockId}", new { label = Zh("改名區"), collapsed = true }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var titleId = long.Parse((await TitleKey(_admin, key))[2..]);
        var f1 = await AddField(_admin, key, "enum");
        var f2 = await AddField(_admin, key, "text");

        var optRes = await _admin.PostAsJsonAsync($"{Base}/fields/{f1.Id}/options", new { label = Zh("甲"), color = "#fff" });
        optRes.StatusCode.Should().Be(HttpStatusCode.Created);
        var optId = (await JsonOf(optRes)).GetProperty("id").GetInt64();
        (await _admin.PutAsJsonAsync($"{Base}/options/{optId}", new { label = Zh("乙"), color = "#000", active = false }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await _admin.PutAsJsonAsync($"{Base}/fields/{f2.Id}/status", new { status = "inactive" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await _admin.PutAsJsonAsync($"{Base}/blocks/{f1.BlockId}/field-order", new { fieldIds = new[] { f2.Id, f1.Id, titleId } }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task FieldType_SerializesLowercase_InModuleAndSchemaResponses()
    {
        var key = await CreateModule(_admin);
        await AddField(_admin, key, "multienum");
        await AddField(_admin, key, "datetime");
        await Activate(_admin, key);

        foreach (var url in new[] { $"{Base}/modules/{key}", $"{Base}/modules/{key}/schema" })
        {
            var raw = await (await _admin.GetAsync(url)).Content.ReadAsStringAsync();
            raw.Should().Contain("\"type\":\"multienum\"").And.Contain("\"type\":\"datetime\"", url);
            raw.Should().NotContain("multiEnum").And.NotContain("dateTime");
        }
    }

    private async Task<(long Id, string Key, long BlockId)> AddRef(string module, string target, string? relation = null) =>
        await AddField(_admin, module, "reference", new
        {
            refTarget = new { kind = "module", key = target },
            relation
        });

    [Fact]
    public async Task RollupSources_ListsIncomingReferenceFields()
    {
        var parent = await CreateModule(_admin);
        var child = await CreateModule(_admin);
        var lookupModule = await CreateModule(_admin); // 不啟用：停用模組的欄位也要列出
        var detail = await AddRef(child, parent, "detail");
        var lookup = await AddRef(lookupModule, parent);
        var self = await AddRef(parent, parent);
        await AddRef(lookupModule, await CreateModule(_admin)); // 指向別的模組，不列出
        var inactive = await AddRef(lookupModule, parent);
        (await _admin.PutAsJsonAsync($"{Base}/fields/{inactive.Id}/status", new { status = "inactive" })).EnsureSuccessStatusCode();

        var res = await _admin.GetAsync($"{Base}/modules/{parent}/rollup-sources");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var items = (await JsonOf(res)).EnumerateArray().ToList();

        items.Select(i => i.GetProperty("fieldId").GetInt64()).Should().BeEquivalentTo(new[] { detail.Id, lookup.Id, self.Id });
        var d = items.Single(i => i.GetProperty("fieldId").GetInt64() == detail.Id);
        d.GetProperty("moduleKey").GetString().Should().Be(child);
        d.GetProperty("relation").GetString().Should().Be("detail");
        d.GetProperty("fieldLabel").GetProperty("zh-TW").GetString().Should().Be("欄位");
        d.GetProperty("moduleName").GetProperty("zh-TW").GetString().Should().Be("測試模組");
        items.Single(i => i.GetProperty("fieldId").GetInt64() == lookup.Id).GetProperty("relation").GetString().Should().Be("lookup");
        items.Single(i => i.GetProperty("fieldId").GetInt64() == self.Id).GetProperty("moduleKey").GetString().Should().Be(parent);

        (await _admin.GetAsync($"{Base}/modules/no-such-module/rollup-sources")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ModuleDetail_DetailOf_Set()
    {
        var parent = await CreateModule(_admin);
        var child = await CreateModule(_admin);
        await AddRef(child, parent, "detail");

        var c = await JsonOf(await _admin.GetAsync($"{Base}/modules/{child}"));
        c.GetProperty("detailOf").GetString().Should().Be(parent);
        var p = await JsonOf(await _admin.GetAsync($"{Base}/modules/{parent}"));
        (!p.TryGetProperty("detailOf", out var v) || v.ValueKind == System.Text.Json.JsonValueKind.Null).Should().BeTrue();

        var list = (await JsonOf(await _admin.GetAsync($"{Base}/modules"))).EnumerateArray().ToList();
        list.Single(m => m.GetProperty("key").GetString() == child).GetProperty("detailOf").GetString().Should().Be(parent);
    }
}
