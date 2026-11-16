using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>每個測試一個新的資料庫：內建範本的模組代碼固定，共用會互相撞。</summary>
public sealed class TemplateImportTests : IDisposable
{
    private readonly TestApp _app = new();
    private readonly HttpClient _admin;

    public TemplateImportTests() => _admin = _app.AsUser("admin");

    public void Dispose() => _app.Dispose();

    private Task<HttpResponseMessage> Import(string json, HttpClient? c = null) =>
        (c ?? _admin).PostAsync($"{Base}/templates/import", new StringContent(json, Encoding.UTF8, "application/json"));

    /// <summary>套用內建 order，匯出，換掉範本代號。</summary>
    private async Task<JsonObject> ExportedOrder(string key)
    {
        var inst = await _admin.PostAsJsonAsync($"{Base}/templates/order/instantiate", new { });
        inst.StatusCode.Should().Be(HttpStatusCode.Created, await inst.Content.ReadAsStringAsync());
        var res = await _admin.GetAsync($"{Base}/modules/order/export-template");
        var node = JsonNode.Parse(await res.Content.ReadAsStringAsync())!.AsObject();
        node["key"] = key;
        return node;
    }

    private static async Task<string> Text(HttpResponseMessage r) => await r.Content.ReadAsStringAsync();

    [Fact]
    public async Task Import_ExportedPackage_ThenListedAndInstantiable()
    {
        var pkg = await ExportedOrder("order-copy");
        var res = await Import(pkg.ToJsonString());
        res.StatusCode.Should().Be(HttpStatusCode.Created, await Text(res));
        var summary = JsonDocument.Parse(await Text(res)).RootElement;
        summary.GetProperty("key").GetString().Should().Be("order-copy");
        summary.GetProperty("source").GetString().Should().Be("imported");

        var list = JsonDocument.Parse(await Text(await _admin.GetAsync($"{Base}/templates"))).RootElement
            .EnumerateArray().Select(t => (Key: t.GetProperty("key").GetString(), Source: t.GetProperty("source").GetString())).ToList();
        list.Last().Should().Be(("order-copy", "imported"));
        list.Where(t => t.Source == "builtIn").Should().NotBeEmpty();
        list.FindLastIndex(t => t.Source == "builtIn").Should().BeLessThan(list.FindIndex(t => t.Source == "imported"));

        var modules = pkg["modules"]!.AsArray()
            .Select(m => new { code = m!["code"]!.GetValue<string>(), key = m["key"]!.GetValue<string>() + "-2" }).ToList();
        var inst = await _admin.PostAsJsonAsync($"{Base}/templates/order-copy/instantiate", new { modules });
        inst.StatusCode.Should().Be(HttpStatusCode.Created, await Text(inst));
    }

    [Fact]
    public async Task Import_InvalidJson_400()
    {
        var res = await Import("{ not json");
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        JsonDocument.Parse(await Text(res)).RootElement.GetProperty("errors").GetProperty("template")[0].GetString()
            .Should().Be("範本不是有效的 JSON");
    }

    [Fact]
    public async Task Import_StructuralErrors_400()
    {
        var pkg = await ExportedOrder("order-copy");
        pkg["key"] = "Bad Key";
        var res = await Import(pkg.ToJsonString());
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var msgs = JsonDocument.Parse(await Text(res)).RootElement.GetProperty("errors").GetProperty("template")
            .EnumerateArray().Select(e => e.GetString()).ToList();
        msgs.Should().Contain(m => m!.Contains("Bad Key") && m.Contains("格式錯誤"));
    }

    [Fact]
    public async Task Import_KeyOfBuiltIn_409()
    {
        var pkg = await ExportedOrder("order");
        var res = await Import(pkg.ToJsonString());
        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Text(res)).Should().Contain("範本代碼已存在");
    }

    [Fact]
    public async Task Import_SameKeyTwice_409()
    {
        var json = (await ExportedOrder("order-copy")).ToJsonString();
        (await Import(json)).StatusCode.Should().Be(HttpStatusCode.Created);
        var res = await Import(json);
        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Text(res)).Should().Contain("範本代碼已存在");
    }

    [Fact]
    public async Task Import_NonDesigner_403()
    {
        var json = (await ExportedOrder("order-copy")).ToJsonString();
        (await Import(json, _app.AsUser("sales01"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
