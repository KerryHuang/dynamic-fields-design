using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 請求沒指定語系時用預設語系政策（p3 計畫 Task 10）。D4（使用者 2026-11-20 裁決）：沒指定 → 請求語系（X-Locale，沒有時是預設語系）；
/// 明確指定但已停用或不認得 → 系統預設語系，不看標頭。
/// </summary>
[Trait("Category", "Postgres")]
public sealed class LocaleDefaultApiTests : IDisposable
{
    private readonly TestApp _app = new();
    private readonly HttpClient _admin;

    public LocaleDefaultApiTests() => _admin = _app.AsUser();

    public void Dispose() => _app.Dispose();

    internal static Dictionary<string, string> Two(string zh, string en) => new() { ["zh-TW"] = zh, ["en-US"] = en };

    /// <summary>預設 en-US；只有 zh-TW、en-US 啟用（th 停用）。</summary>
    private async Task EnglishDefault()
    {
        (await _admin.PutAsJsonAsync($"{Base}/system/policies/locale.default", new { value = "en-US" })).EnsureSuccessStatusCode();
        (await _admin.PutAsJsonAsync($"{Base}/system/policies/locale.enabled", new { value = new[] { "zh-TW", "en-US" } })).EnsureSuccessStatusCode();
    }

    internal sealed record World(string Module, long RecordId, long Status, string High, long ReportId);

    /// <summary>模組含選項欄位「優先」（Priority），選項「高／低」（High／Low）；一筆記錄、一張依優先分組的報表。</summary>
    private Task<World> MakeWorld() => MakeWorld(_app, _admin);

    /// <summary>LocaleChainApiTests 共用。</summary>
    internal static async Task<World> MakeWorld(TestApp app, HttpClient admin)
    {
        var key = NewKey();
        (await admin.PostAsJsonAsync($"{Base}/modules", new { key, name = Two("訂單", "Order") })).EnsureSuccessStatusCode();
        var module = await JsonOf(await admin.GetAsync($"{Base}/modules/{key}"));
        var blockId = module.GetProperty("blocks")[0].GetProperty("id").GetInt64();
        var field = await JsonOf(await admin.PostAsJsonAsync($"{Base}/modules/{key}/fields",
            new { blockId, type = "enum", label = Two("優先", "Priority"), config = new { } }));
        var status = field.GetProperty("id").GetInt64();
        var options = new List<string>();
        foreach (var (zh, en) in new[] { ("高", "High"), ("低", "Low") })
        {
            var res = await admin.PostAsJsonAsync($"{Base}/fields/{status}/options", new { label = Two(zh, en) });
            res.EnsureSuccessStatusCode();
            options.Add($"opt_{(await JsonOf(res)).GetProperty("id").GetInt64()}");
        }
        await Activate(admin, key);
        app.GrantBusinessGroups(key);
        var title = await TitleKey(admin, key);
        var rec = await admin.PostAsJsonAsync($"{Base}/modules/{key}/records",
            new { values = new Dictionary<string, object> { [title] = "t1", [$"f_{status}"] = options[1] } });
        rec.EnsureSuccessStatusCode();
        var created = await JsonOf(rec);
        var recordId = created.GetProperty("id").GetInt64();
        var put = await admin.PutAsJsonAsync($"{Base}/records/{recordId}", new
        {
            values = new Dictionary<string, object> { [title] = "t1", [$"f_{status}"] = options[0] },
            rowVersion = created.GetProperty("rowVersion").GetString()
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
        var definition = new Dictionary<string, object?>
        {
            ["version"] = 1, ["mainModuleKey"] = key, ["child"] = null, ["columns"] = Array.Empty<object>(),
            ["groups"] = new object[] { new { field = new { source = "main", fieldId = status, viaFieldId = (long?)null }, bucket = (string?)null } },
            ["measures"] = new object[] { new { id = "n", kind = "countMain", field = (object?)null, condition = (object?)null } },
            ["filters"] = Array.Empty<object>(), ["primaryDateField"] = null, ["dateRange"] = null,
            ["sort"] = Array.Empty<object>(), ["viz"] = "bar"
        };
        var report = await JsonOf(await admin.PostAsJsonAsync($"{Base}/reports", new { name = "r", definition }));
        return new World(key, recordId, status, options[0], report.GetProperty("id").GetInt64());
    }

    private static HttpRequestMessage Req(HttpMethod method, string url, string? header, object? body = null)
    {
        var req = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        if (header is not null) req.Headers.Add("X-Locale", header);
        return req;
    }

    private async Task<string> ExportCsv(World w, string? query, string? header = null)
    {
        var res = await _admin.SendAsync(Req(HttpMethod.Get, $"{Base}/modules/{w.Module}/export?format=csv{(query is null ? "" : "&locale=" + query)}", header));
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return Encoding.UTF8.GetString(await res.Content.ReadAsByteArrayAsync());
    }

    /// <summary>報表分組欄的欄名（＝欄位標籤）。</summary>
    private async Task<string> ReportGroupLabel(World w, string? query, string? header = null)
    {
        var res = await _admin.SendAsync(Req(HttpMethod.Post, $"{Base}/reports/{w.ReportId}/run{(query is null ? "" : "?locale=" + query)}", header, new { }));
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var json = await JsonOf(res);
        return json.GetProperty("columns").EnumerateArray().First(c => c.GetProperty("role").GetString() == "group").GetProperty("label").GetString()!;
    }

    [Fact]
    public async Task Export_UsesDefaultLocale()
    {
        await EnglishDefault();
        var w = await MakeWorld();
        var csv = await ExportCsv(w, null);
        csv.Should().Contain("Priority").And.Contain("High").And.NotContain("優先");
    }

    [Fact]
    public async Task Print_UsesDefaultLocale()
    {
        await EnglishDefault();
        var w = await MakeWorld();
        (await _admin.PostAsJsonAsync($"{Base}/modules/{w.Module}/print-templates",
            new { key = "sheet", name = Two("出貨單", "Packing slip"), body = $"<p>{{{{f_{w.Status}}}}}</p>", active = true })).EnsureSuccessStatusCode();
        var html = await (await _admin.GetAsync($"{Base}/records/{w.RecordId}/print/sheet")).Content.ReadAsStringAsync();
        html.Should().Contain("Packing slip").And.Contain("High");
    }

    [Fact]
    public async Task History_UsesDefaultLocale()
    {
        await EnglishDefault();
        var w = await MakeWorld();
        var json = await (await _admin.GetAsync($"{Base}/records/{w.RecordId}/history")).Content.ReadAsStringAsync();
        json.Should().Contain("\"High\"").And.Contain("\"Low\"");
    }

    [Fact]
    public async Task ReportRun_UsesDefaultLocale()
    {
        await EnglishDefault();
        var w = await MakeWorld();
        (await ReportGroupLabel(w, null)).Should().Be("Priority");
    }

    [Fact]
    public async Task DashboardCard_UsesDefaultLocale()
    {
        await EnglishDefault();
        var w = await MakeWorld();
        var created = await _admin.PostAsJsonAsync(DashboardTestKit.Dashboards,
            new { name = "我的", layout = DashboardTestKit.Layout(DashboardTestKit.Card("c", w.ReportId)) });
        created.EnsureSuccessStatusCode();
        var id = (await JsonOf(created)).GetProperty("id").GetInt64();
        var card = await DashboardTestKit.RunCard(_admin, id, "c");
        card.GetProperty("columns").EnumerateArray().First(c => c.GetProperty("role").GetString() == "group")
            .GetProperty("label").GetString().Should().Be("Priority");
    }

    [Fact]
    public async Task CreateGroup_NoLocale_WritesDefaultLocale()
    {
        await EnglishDefault();
        var res = await _admin.PostAsJsonAsync($"{Base}/admin/groups", new { name = "Warehouse" });
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        var id = (await JsonOf(res)).GetProperty("id").GetInt64();
        using var scope = _app.Services.CreateScope();
        var group = await scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<PermissionGroup>().AsNoTracking().SingleAsync(g => g.Id == id);
        group.NameI18n.Values.Should().Contain("en-US", "Warehouse");
    }

    [Fact]
    public async Task ExplicitLocale_StillWins()
    {
        await EnglishDefault();
        var w = await MakeWorld();
        (await ExportCsv(w, "zh-TW")).Should().Contain("優先").And.Contain("高");
        (await ReportGroupLabel(w, "zh-TW")).Should().Be("優先");
    }

    [Fact]
    public async Task DisabledQueryLocale_UsesDefault()
    {
        await EnglishDefault();
        var w = await MakeWorld();
        foreach (var q in new[] { "th", "xx" })
        {
            (await ExportCsv(w, q)).Should().Contain("Priority", q);
            (await ReportGroupLabel(w, q)).Should().Be("Priority", q);
        }
    }

    [Fact]
    public async Task DisabledQueryLocale_IgnoresHeader_UsesDefault()
    {
        await EnglishDefault();
        var w = await MakeWorld();
        foreach (var q in new[] { "th", "xx" })
        {
            (await ExportCsv(w, q, "zh-TW")).Should().Contain("Priority", q);
            (await ReportGroupLabel(w, q, "zh-TW")).Should().Be("Priority", q);
        }
    }

    [Fact]
    public async Task NoQueryLocale_UsesHeader()
    {
        await EnglishDefault();
        var w = await MakeWorld();
        (await ExportCsv(w, null, "zh-TW")).Should().Contain("優先");
        (await ReportGroupLabel(w, null, "zh-TW")).Should().Be("優先");
    }

    [Fact]
    public async Task OpenApp_NoLocale_UsesDefault()
    {
        await EnglishDefault();
        var open = await _admin.PostAsJsonAsync($"{Base}/apps/packages/crm-sales/open",
            new { groupMap = new Dictionary<string, object> { ["sales"] = new { mode = "create" } } });
        open.StatusCode.Should().Be(HttpStatusCode.Created, await open.Content.ReadAsStringAsync());
        using var scope = _app.Services.CreateScope();
        var groups = await scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<PermissionGroup>().AsNoTracking().ToListAsync();
        // 套件群組名稱只有繁中：以預設語系 en-US 解析（退回繁中）後寫進 en-US
        groups.Should().Contain(g => g.NameI18n.Values.ContainsKey("en-US"));
    }
}
