using System.Net;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;
using static DynamicModules.WebAPITests.Dynamic.LocaleDefaultApiTests;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 輸出標籤走退回鏈（p3 計畫 Task 11a、11b）：預設 en-US、啟用 zh-TW／en-US／th；文字都沒有 th，帶 X-Locale: th 時回英文（退回鏈第一步＝預設語系）。
/// </summary>
[Trait("Category", "Postgres")]
public sealed class LocaleChainApiTests : IDisposable
{
    private readonly TestApp _app = new();
    private readonly HttpClient _admin;
    private readonly HttpClient _thai;

    public LocaleChainApiTests()
    {
        _admin = _app.AsUser();
        _thai = _app.AsUser();
        _thai.DefaultRequestHeaders.Add("X-Locale", "th");
    }

    public void Dispose() => _app.Dispose();

    private async Task Policies(string defaultLocale)
    {
        (await _admin.PutAsJsonAsync($"{Base}/system/policies/locale.default", new { value = defaultLocale })).EnsureSuccessStatusCode();
        (await _admin.PutAsJsonAsync($"{Base}/system/policies/locale.enabled", new { value = new[] { "zh-TW", "en-US", "th" } })).EnsureSuccessStatusCode();
    }

    private async Task<string> Text(HttpResponseMessage res)
    {
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return Encoding.UTF8.GetString(await res.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("th")]
    public async Task Export_HeaderAndOption_English(string? query)
    {
        await Policies("en-US");
        var w = await MakeWorld(_app, _admin);
        var csv = await Text(await _thai.GetAsync($"{Base}/modules/{w.Module}/export?format=csv{(query is null ? "" : "&locale=" + query)}"));
        csv.Should().Contain("Priority").And.Contain("High").And.NotContain("優先");
    }

    [Fact]
    public async Task Print_Title_English()
    {
        await Policies("en-US");
        var w = await MakeWorld(_app, _admin);
        (await _admin.PostAsJsonAsync($"{Base}/modules/{w.Module}/print-templates",
            new { key = "sheet", name = Two("出貨單", "Packing slip"), body = "<p>x</p>", active = true })).EnsureSuccessStatusCode();
        (await Text(await _thai.GetAsync($"{Base}/records/{w.RecordId}/print/sheet"))).Should().Contain("<title>Packing slip</title>");
    }

    [Fact]
    public async Task ReportCatalog_English()
    {
        await Policies("en-US");
        var w = await MakeWorld(_app, _admin);
        (await Text(await _thai.GetAsync($"{Base}/reports/catalog"))).Should().Contain("\"Order\"");
        var catalog = await Text(await _thai.GetAsync($"{Base}/reports/catalog/{w.Module}"));
        catalog.Should().Contain("\"Priority\"").And.Contain("\"High\"");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("th")]
    public async Task ReportRun_ColumnsAndRowMeaning_English(string? query)
    {
        await Policies("en-US");
        var w = await MakeWorld(_app, _admin);
        var json = await Text(await _thai.PostAsJsonAsync($"{Base}/reports/{w.ReportId}/run{(query is null ? "" : "?locale=" + query)}", new { }));
        json.Should().Contain("\"Priority\"").And.Contain("Order").And.NotContain("優先");
    }

    [Fact]
    public async Task Factory_ZhTwDefault_Unchanged()
    {
        await Policies("zh-TW");
        var w = await MakeWorld(_app, _admin);
        (await Text(await _thai.GetAsync($"{Base}/modules/{w.Module}/export?format=csv"))).Should().Contain("優先").And.Contain("高");
        (await Text(await _thai.PostAsJsonAsync($"{Base}/reports/{w.ReportId}/run", new { }))).Should().Contain("\"優先\"");
    }

    [Fact]
    public async Task Menu_SortedByRequestLocale()
    {
        await Policies("en-US");
        var zebra = NewKey(); var apple = NewKey();
        foreach (var (key, zh, en) in new[] { (zebra, "乙", "Zebra"), (apple, "甲", "Apple") })
        {
            (await _admin.PostAsJsonAsync($"{Base}/modules", new { key, name = Two(zh, en) })).EnsureSuccessStatusCode();
            await Activate(_admin, key);
        }
        List<string> Order(System.Text.Json.JsonElement menu) =>
            menu.EnumerateArray().Select(m => m.GetProperty("key").GetString()!).Where(k => k == zebra || k == apple).ToList();
        var english = _app.AsUser();
        english.DefaultRequestHeaders.Add("X-Locale", "en-US");
        Order(await JsonOf(await english.GetAsync($"{Base}/menu"))).Should().Equal(apple, zebra);
        var chinese = _app.AsUser();
        chinese.DefaultRequestHeaders.Add("X-Locale", "zh-TW");
        Order(await JsonOf(await chinese.GetAsync($"{Base}/menu"))).Should().Equal(zebra, apple);
    }

    [Fact]
    public async Task GroupImpact_ModuleNamesInRequestLocale()
    {
        await Policies("en-US");
        var w = await MakeWorld(_app, _admin);
        var groups = await JsonOf(await _admin.GetAsync($"{Base}/admin/groups"));
        var names = new List<string>();
        foreach (var g in groups.EnumerateArray())
        {
            var impact = await JsonOf(await _thai.GetAsync($"{Base}/admin/groups/{g.GetProperty("id").GetInt64()}/impact"));
            names.AddRange(impact.GetProperty("modules").EnumerateArray().Select(m => m.GetString()!));
        }
        names.Should().Contain("Order").And.NotContain("訂單");
    }
}
