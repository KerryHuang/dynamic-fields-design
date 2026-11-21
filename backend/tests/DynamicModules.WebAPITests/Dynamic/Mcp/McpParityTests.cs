using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using ModelContextProtocol.Client;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;
using static DynamicModules.WebAPITests.Dynamic.Mcp.McpTestKit;

namespace DynamicModules.WebAPITests.Dynamic.Mcp;

/// <summary>
/// REST 與 MCP 的對照（設計 §13.8、mcp1a 計畫 Task 9）：同一位虛構使用者經 REST 列表與 <c>query_records</c> 查同一模組，
/// 記錄 id 集合與每筆的欄位 key 集合相同。情況：資料範圍全部、本人、同群組，加上沒有 Pricing（金額欄在兩邊都不出現）。
/// 權限設定照 <c>PrintApiTests.SetSalesPermissions</c> 的寫法。
/// </summary>
[Trait("Category", "Postgres")]
public class McpParityTests : IAsyncLifetime
{
    private TestApp _app = null!;
    private HttpClient _admin = null!, _sales = null!;
    private string _module = "", _titleKey = "", _amountKey = "";
    private long _adminRecord, _ownRecord, _peerRecord;

    public async Task InitializeAsync()
    {
        _app = NewApp();
        EnableMcp(_app);
        // 示範人員是 Demo 帳號、要改密碼，不能用權杖；這裡改成一般帳號（測試直接寫）
        EnsureUser(_app, "sales01", u => (u.IsDemoAccount, u.MustChangePassword) = (false, false));
        _admin = _app.AsUser();
        _sales = _app.AsUser("sales01");
        _module = await CreateModule(_admin);
        _titleKey = await TitleKey(_admin, _module);
        (_, _amountKey, _) = await AddField(_admin, _module, "currency");
        await Activate(_admin, _module);
        await SetSales(scope: "all", pricing: true);
        _adminRecord = await Create(_admin, "總部單");
        _ownRecord = await Create(_sales, "業務一的單");
        _peerRecord = await Create(_app.AsUser("sales02"), "業務二的單");
    }

    public Task DisposeAsync()
    {
        _app.Dispose();
        return Task.CompletedTask;
    }

    private async Task<long> Create(HttpClient c, string title)
    {
        var res = await c.PostAsJsonAsync($"/api/v3/dynamic/modules/{_module}/records",
            new { values = new Dictionary<string, object> { [_titleKey] = title, [_amountKey] = 1234.5 } });
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return (await JsonOf(res)).GetProperty("id").GetInt64();
    }

    private async Task SetSales(string scope, bool pricing)
    {
        var groups = await JsonOf(await _admin.GetAsync("/api/v3/dynamic/groups"));
        var salesId = groups.EnumerateArray().Single(g => g.GetProperty("key").GetString() == "sales").GetProperty("id").GetInt64();
        var put = await _admin.PutAsJsonAsync($"/api/v3/dynamic/modules/{_module}/permissions", new
        {
            groups = new[]
            {
                new
                {
                    groupId = salesId, scope,
                    privileges = new { read = true, add = true, edit = true, delete = false, printExport = false, pricing, approve = false },
                },
            },
            users = Array.Empty<object>(), fields = Array.Empty<object>(), options = Array.Empty<object>(),
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
    }

    private static Dictionary<long, string[]> Shape(JsonElement items) => items.EnumerateArray().ToDictionary(
        i => i.GetProperty("id").GetInt64(), i => i.GetProperty("values").EnumerateObject().Select(p => p.Name).Order().ToArray());

    /// <summary>回 REST 與 MCP 各自的形狀；斷言兩邊相同。</summary>
    private async Task<Dictionary<long, string[]>> BothSides()
    {
        var rest = await JsonOf(await _sales.GetAsync($"/api/v3/dynamic/modules/{_module}/records/paged?currentPage=1&pagesSize=50"));
        await using var mcp = await ConnectAsync(_app, IssueToken(_app, "sales01").Raw);
        var r = (await mcp.CallToolAsync("query_records", new Dictionary<string, object?> { ["moduleKey"] = _module })).StructuredContent!.Value;
        r.TryGetProperty("error", out var error).Should().BeFalse(error.ValueKind == JsonValueKind.Undefined ? "" : error.GetRawText());

        var restShape = Shape(rest.GetProperty("items"));
        Shape(r.GetProperty("data").GetProperty("items")).Should().BeEquivalentTo(restShape);
        return restShape;
    }

    [Fact]
    public async Task ScopeAll_SameRecordsAndKeys()
    {
        (await BothSides()).Keys.Should().BeEquivalentTo([_adminRecord, _ownRecord, _peerRecord]);
    }

    [Fact]
    public async Task ScopeOwn_SameRecordsAndKeys()
    {
        await SetSales(scope: "own", pricing: true);
        (await BothSides()).Keys.Should().BeEquivalentTo([_ownRecord]);
    }

    [Fact]
    public async Task ScopeGroup_SameRecordsAndKeys()
    {
        await SetSales(scope: "group", pricing: true);
        (await BothSides()).Keys.Should().Contain(_ownRecord).And.NotContain(_adminRecord);
    }

    [Fact]
    public async Task NoPricing_AmountAbsentOnBothSides()
    {
        await SetSales(scope: "all", pricing: false);
        var shape = await BothSides();
        shape.Values.Should().OnlyContain(keys => !keys.Contains(_amountKey));
        shape.Should().NotBeEmpty();
    }
}
