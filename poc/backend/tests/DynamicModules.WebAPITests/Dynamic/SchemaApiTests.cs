using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

public class SchemaApiTests : IClassFixture<TestApp>
{
    private readonly TestApp _app;
    private readonly HttpClient _admin;

    public SchemaApiTests(TestApp app)
    {
        _app = app;
        _admin = app.AsUser("admin");
    }

    private async Task<string> ActiveModule()
    {
        var key = await CreateModule(_admin);
        await Activate(_admin, key);
        return key;
    }

    [Fact]
    public async Task GetSchema_ReturnsETag_AndMatchingIfNoneMatch_Returns304()
    {
        var key = await ActiveModule();
        var first = await _admin.GetAsync($"{Base}/modules/{key}/schema");
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var etag = first.Headers.ETag!.ToString();

        var req = new HttpRequestMessage(HttpMethod.Get, $"{Base}/modules/{key}/schema");
        req.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var second = await _admin.SendAsync(req);

        second.StatusCode.Should().Be(HttpStatusCode.NotModified);
        (await second.Content.ReadAsStringAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task GetSchema_WeakIfNoneMatch_Returns304()
    {
        var key = await ActiveModule();
        var etag = (await _admin.GetAsync($"{Base}/modules/{key}/schema")).Headers.ETag!.Tag;

        var req = new HttpRequestMessage(HttpMethod.Get, $"{Base}/modules/{key}/schema");
        req.Headers.TryAddWithoutValidation("If-None-Match", "W/" + etag);
        (await _admin.SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.NotModified);
    }

    [Fact]
    public async Task GetSchema_AfterAddField_ETagChanges()
    {
        var key = await ActiveModule();
        var before = (await _admin.GetAsync($"{Base}/modules/{key}/schema")).Headers.ETag!.ToString();

        await AddField(_admin, key, "text");

        var req = new HttpRequestMessage(HttpMethod.Get, $"{Base}/modules/{key}/schema");
        req.Headers.TryAddWithoutValidation("If-None-Match", before);
        var res = await _admin.SendAsync(req);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Headers.ETag!.ToString().Should().NotBe(before);
    }

    [Fact]
    public async Task GetSchema_DraftModule_Returns404()
    {
        var key = await CreateModule(_admin);
        (await _admin.GetAsync($"{Base}/modules/{key}/schema")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetMenu_ReturnsOnlyActive()
    {
        var active = await ActiveModule();
        var draft = await CreateModule(_admin);

        var menu = await JsonOf(await _admin.GetAsync($"{Base}/menu"));
        var keys = menu.EnumerateArray().Select(m => m.GetProperty("key").GetString()).ToList();

        keys.Should().Contain(active);
        keys.Should().NotContain(draft);
    }

    private static async Task<HttpResponseMessage> GetSchemaAs(HttpClient c, string key, string? ifNoneMatch = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"{Base}/modules/{key}/schema");
        if (ifNoneMatch is not null) req.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        return await c.SendAsync(req);
    }

    [Fact]
    public async Task SchemaEndpoint_ETagDiffersByUser()
    {
        var key = await ActiveModule();
        _app.GrantBusinessGroups(key);
        var sales = _app.AsUser("sales01");

        var adminRes = await GetSchemaAs(_admin, key);
        var salesRes = await GetSchemaAs(sales, key);
        var adminTag = adminRes.Headers.ETag!.ToString();
        var salesTag = salesRes.Headers.ETag!.ToString();

        salesTag.Should().NotBe(adminTag);
        (await GetSchemaAs(sales, key, salesTag)).StatusCode.Should().Be(HttpStatusCode.NotModified);
        (await GetSchemaAs(_admin, key, adminTag)).StatusCode.Should().Be(HttpStatusCode.NotModified);
        (await GetSchemaAs(sales, key, adminTag)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SchemaEndpoint_NoRead_Returns403_AndMenuOmitsModule()
    {
        var key = await ActiveModule();
        var sales = _app.AsUser("sales01");

        (await GetSchemaAs(sales, key)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var menu = await JsonOf(await sales.GetAsync($"{Base}/menu"));
        menu.EnumerateArray().Select(m => m.GetProperty("key").GetString()).Should().NotContain(key);
    }

    /// <summary>表頭與明細模組，都已啟用；明細有一個數量欄位。</summary>
    private async Task<(string Parent, string Child, string DetailFieldKey, long QtyId)> DetailPair()
    {
        var parent = await CreateModule(_admin);
        var child = await CreateModule(_admin);
        var link = await AddField(_admin, child, "reference",
            new { refTarget = new { kind = "module", key = parent }, relation = "detail" });
        var qty = await AddField(_admin, child, "integer");
        await Activate(_admin, parent);
        await Activate(_admin, child);
        return (parent, child, link.Key, qty.Id);
    }

    [Fact]
    public async Task DetailModule_NotInMenu_And_SchemaIs404()
    {
        var (parent, child, _, _) = await DetailPair();

        var keys = (await JsonOf(await _admin.GetAsync($"{Base}/menu"))).EnumerateArray()
            .Select(m => m.GetProperty("key").GetString()).ToList();
        keys.Should().Contain(parent).And.NotContain(child);
        var res = await GetSchemaAs(_admin, child);
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await JsonOf(res)).GetProperty("message").GetString().Should().Be("模組不存在或未啟用");
    }

    [Fact]
    public async Task Schema_Parent_IncludesDetails_AndETagFollowsDetailModule()
    {
        var (parent, child, linkKey, _) = await DetailPair();
        var first = await GetSchemaAs(_admin, parent);
        var detail = (await JsonOf(first)).GetProperty("details").EnumerateArray().Single();
        detail.GetProperty("moduleKey").GetString().Should().Be(child);
        detail.GetProperty("parentFieldKey").GetString().Should().Be(linkKey);
        detail.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("key").GetString())
            .Should().NotContain(linkKey);

        await AddField(_admin, child, "text");
        (await GetSchemaAs(_admin, parent, first.Headers.ETag!.ToString())).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SavePermissions_DetailModule_GroupsRejected_FieldsOk()
    {
        var (_, child, _, qtyId) = await DetailPair();
        var groups = await _admin.PutAsJsonAsync($"{Base}/modules/{child}/permissions", new
        {
            groups = new[] { new { groupId = 1, privileges = new { read = true }, scope = (string?)null } },
            users = Array.Empty<object>(), fields = Array.Empty<object>(), options = Array.Empty<object>()
        });
        groups.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await JsonOf(groups)).GetProperty("errors").GetProperty("groups")[0].GetString().Should().Be("明細模組的權限沿用表頭模組");

        var fields = await _admin.PutAsJsonAsync($"{Base}/modules/{child}/permissions", new
        {
            groups = Array.Empty<object>(), users = Array.Empty<object>(),
            fields = new[] { new { fieldId = qtyId, groupId = 1, access = "hidden" } }, options = Array.Empty<object>()
        });
        fields.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
