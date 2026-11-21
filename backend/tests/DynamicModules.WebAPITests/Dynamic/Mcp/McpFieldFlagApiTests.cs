using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.WebAPITests.Dynamic.PermissionAxes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;
using static DynamicModules.WebAPITests.Dynamic.PermissionAxes.PaxWorld;

namespace DynamicModules.WebAPITests.Dynamic.Mcp;

/// <summary>「不提供給 AI」旗標的 HTTP 層（mcp1a 計畫判斷 4，Task 5）：新增、修改帶旗標要權限設定，回應帶 excludeFromAi。</summary>
[Trait("Category", "Postgres")]
public sealed class McpFieldFlagApiTests : IAsyncLifetime
{
    private readonly TestApp _app = new();
    private PaxWorld _w = null!;

    public async Task InitializeAsync() => _w = await CreateAsync(_app);

    public Task DisposeAsync()
    {
        _app.Dispose();
        return Task.CompletedTask;
    }

    private async Task<JsonElement> FieldJson(long id)
    {
        var m = await JsonOf(await _w.As(Admin).GetAsync($"{Base}/modules/{A}"));
        return m.GetProperty("blocks").EnumerateArray().SelectMany(b => b.GetProperty("fields").EnumerateArray())
            .Single(f => f.GetProperty("id").GetInt64() == id);
    }

    private bool Flag(long id) => _w.Db(db => db.Set<Field>().AsNoTracking().Single(f => f.Id == id).ExcludeFromAi);

    private async Task<HttpResponseMessage> Add(string user, bool? exclude)
    {
        var m = await JsonOf(await _w.As(Admin).GetAsync($"{Base}/modules/{A}"));
        var body = new Dictionary<string, object>
        {
            ["blockId"] = m.GetProperty("blocks")[0].GetProperty("id").GetInt64(), ["type"] = "decimal",
            ["label"] = new Dictionary<string, string> { ["zh-TW"] = "機密" }, ["config"] = new { },
        };
        if (exclude is { } v) body["excludeFromAi"] = v;
        return await _w.As(user).PostAsJsonAsync($"{Base}/modules/{A}/fields", body);
    }

    private async Task<HttpResponseMessage> Update(string user, long id, bool? exclude)
    {
        var f = await FieldJson(id);
        var body = new Dictionary<string, object>
        {
            ["type"] = f.GetProperty("type").GetString()!, ["label"] = new Dictionary<string, string> { ["zh-TW"] = "改名" },
            ["config"] = f.GetProperty("config"), ["blockId"] = f.GetProperty("blockId").GetInt64(),
        };
        if (exclude is { } v) body["excludeFromAi"] = v;
        return await _w.As(user).PutAsJsonAsync($"{Base}/fields/{id}", body);
    }

    [Fact]
    public async Task AddField_Flag_NeedsPermissionAdmin()
    {
        await PaxAssert.Forbidden(await Add(Wang, true), "permissionAdminRequired");

        var created = await Add(Admin, true);
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var body = await JsonOf(created);
        var id = body.GetProperty("id").GetInt64();
        body.GetProperty("excludeFromAi").GetBoolean().Should().BeTrue();
        (await FieldJson(id)).GetProperty("excludeFromAi").GetBoolean().Should().BeTrue();

        var plain = await Add(Wang, null);
        plain.StatusCode.Should().Be(HttpStatusCode.Created, await plain.Content.ReadAsStringAsync());
        (await FieldJson((await JsonOf(plain)).GetProperty("id").GetInt64())).GetProperty("excludeFromAi").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task UpdateField_OmittedKeeps_ChangeNeedsPermissionAdmin()
    {
        await PaxAssert.Status(await Update(Admin, _w.AF1, true), HttpStatusCode.OK);
        Flag(_w.AF1).Should().BeTrue();

        await PaxAssert.Status(await Update(Wang, _w.AF1, null), HttpStatusCode.OK);
        Flag(_w.AF1).Should().BeTrue("沒帶＝不改");

        await PaxAssert.Forbidden(await Update(Wang, _w.AF1, false), "permissionAdminRequired");
        Flag(_w.AF1).Should().BeTrue();
        await PaxAssert.Status(await Update(Chen, _w.AF1, false), HttpStatusCode.OK);
        Flag(_w.AF1).Should().BeFalse();
    }
}
