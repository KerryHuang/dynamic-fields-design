using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests;

/// <summary>第 2 階段驗收情境：模具維修申請列表的篩選、排序與清單檢視。</summary>
public class Phase2AcceptanceTests : IClassFixture<TestApp>
{
    private const string Module = "repair-request";

    private readonly TestApp _app;
    private readonly HttpClient _admin;
    private readonly HttpClient _sales01;
    private readonly HttpClient _sales02;

    public Phase2AcceptanceTests(TestApp app)
    {
        _app = app;
        _admin = app.AsUser("admin");
        _sales01 = app.AsUser("sales01");
        _sales02 = app.AsUser("sales02");
    }

    private static Dictionary<string, string> Zh(string t) => new() { ["zh-TW"] = t };

    private static string Enc(object o) => Uri.EscapeDataString(JsonSerializer.Serialize(o));

    private async Task<(long Id, string Key)> AddField(long blockId, string type, string label, object config)
    {
        var res = await _admin.PostAsJsonAsync($"{Base}/modules/{Module}/fields",
            new { blockId, type, label = Zh(label), config });
        res.StatusCode.Should().Be(HttpStatusCode.Created, label);
        var f = await JsonOf(res);
        return (f.GetProperty("id").GetInt64(), f.GetProperty("key").GetString()!);
    }

    private async Task<long> AddOption(long fieldId, string label)
    {
        var res = await _admin.PostAsJsonAsync($"{Base}/fields/{fieldId}/options", new { label = Zh(label) });
        res.StatusCode.Should().Be(HttpStatusCode.Created, label);
        return (await JsonOf(res)).GetProperty("id").GetInt64();
    }

    private async Task<JsonElement> Paged(HttpClient c, string query)
    {
        var res = await c.GetAsync($"{Base}/modules/{Module}/records/paged?currentPage=1&pagesSize=50{query}");
        res.StatusCode.Should().Be(HttpStatusCode.OK, query);
        return await JsonOf(res);
    }

    private static List<string> Titles(JsonElement paged) =>
        paged.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("title").GetString()!).ToList();

    [Fact]
    public async Task Phase2_Scenario()
    {
        // 1. 建立模組並加欄位
        (await _admin.PostAsJsonAsync($"{Base}/modules", new { key = Module, name = Zh("模具維修申請") }))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        var module = await JsonOf(await _admin.GetAsync($"{Base}/modules/{Module}"));
        var blockId = module.GetProperty("blocks")[0].GetProperty("id").GetInt64();
        var title = await TitleKey(_admin, Module);
        var titleId = module.GetProperty("titleFieldId").GetInt64();

        // 標題欄預設不可排序；為了驗證排序先把它開成可排序
        var titleField = module.GetProperty("blocks")[0].GetProperty("fields").EnumerateArray()
            .Single(f => f.GetProperty("id").GetInt64() == titleId);
        (await _admin.PutAsJsonAsync($"{Base}/fields/{titleId}", new
        {
            type = "text", label = JsonSerializer.Deserialize<JsonElement>(titleField.GetProperty("label").GetRawText()),
            config = new { required = true, isSortable = true }, blockId
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        var applyDate = await AddField(blockId, "date", "申請日", new { isFilterable = true, isSortable = true });
        var category = await AddField(blockId, "enum", "故障類別", new { isFilterable = true, isSortable = true });
        var options = new[]
        {
            await AddOption(category.Id, "模具損壞"),
            await AddOption(category.Id, "機台異常"),
            await AddOption(category.Id, "外包"),
        };
        var inspect = await AddField(blockId, "multienum", "檢查項目", new { isFilterable = true });
        var cost = await AddField(blockId, "currency", "預估費用", new { scale = 0, isFilterable = true, isSortable = true });
        var handler = await AddField(blockId, "user", "承辦人", new { isFilterable = true });
        var note = await AddField(blockId, "textarea", "說明", new { });

        // 2. 啟用後建 15 筆；第 0–2 筆不填故障類別，其餘依序輪流三個選項（各 4 筆）；費用 500 * i；偶數筆承辦人 sales01
        (await _admin.PostAsync($"{Base}/modules/{Module}/activate", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        _app.GrantBusinessGroups(Module); // 新模組沒有授權，業務才能讀取
        for (var i = 0; i < 15; i++)
        {
            var values = new Dictionary<string, object>
            {
                [title] = $"T{i:00}",
                [applyDate.Key] = $"2026-11-{i + 1:00}",
                [cost.Key] = 500 * i,
                [handler.Key] = i % 2 == 0 ? "sales01" : "sales02",
                [note.Key] = $"說明 {i}",
            };
            if (i >= 3) values[category.Key] = $"opt_{options[(i - 3) % 3]}";
            (await _admin.PostAsJsonAsync($"{Base}/modules/{Module}/records", new { values }))
                .StatusCode.Should().Be(HttpStatusCode.Created, $"第 {i} 筆");
        }
        (await Paged(_admin, "")).GetProperty("totalCount").GetInt32().Should().Be(15);

        // 3. 故障類別不等於外包：非外包 8 筆 + 未填 3 筆
        var notOutsourced = await Paged(_admin,
            $"&filter={Enc(new { all = new[] { new { field = category.Id, op = "ne", value = $"opt_{options[2]}" } } })}");
        notOutsourced.GetProperty("totalCount").GetInt32().Should().Be(8 + 3);
        Titles(notOutsourced).Should().NotContain(["T05", "T08", "T11", "T14"]).And.Contain(["T00", "T01", "T02"]);

        // 4. 預估費用介於 1000–5000 且承辦人是 sales01（all）：T02、T04、T06、T08、T10
        var costAndHandler = new
        {
            all = new object[]
            {
                new { field = cost.Id, op = "between", value = new[] { 1000, 5000 } },
                new { field = handler.Id, op = "eq", value = "sales01" },
            }
        };
        var between = await Paged(_admin, $"&filter={Enc(costAndHandler)}");
        between.GetProperty("totalCount").GetInt32().Should().Be(5);
        Titles(between).Should().BeEquivalentTo(["T02", "T04", "T06", "T08", "T10"]);

        // 5. 故障類別升冪（依選項順序）、預估費用降冪；未填者排最後
        var sorted = await Paged(_admin,
            $"&sortBy={category.Key}&sortOrder=asc&sortBy={cost.Key}&sortOrder=desc");
        Titles(sorted).Take(5).Should().Equal("T12", "T09", "T06", "T03", "T13");
        Titles(sorted).TakeLast(3).Should().BeEquivalentTo(["T00", "T01", "T02"]);

        // 6. 共用預設檢視（admin 建）與 sales01 個人預設檢視；sales02 只看到共用的
        var filterOnCost = new { all = new[] { new { field = cost.Id, op = "gte", value = 1000 } } };
        var shared = await _admin.PostAsJsonAsync($"{Base}/modules/{Module}/views", new
        {
            name = Zh("全部"), shared = true, columns = new[] { titleId, cost.Id },
            sort = new[] { new { field = cost.Id, order = "desc" } }, isDefault = true
        });
        shared.StatusCode.Should().Be(HttpStatusCode.Created);
        var sharedId = (await JsonOf(shared)).GetProperty("id").GetInt64();
        var personal = await _sales01.PostAsJsonAsync($"{Base}/modules/{Module}/views", new
        {
            name = Zh("我的高額件"), shared = false, columns = new[] { titleId, cost.Id },
            filter = filterOnCost, sort = new[] { new { field = cost.Id, order = "desc" } }, isDefault = true
        });
        personal.StatusCode.Should().Be(HttpStatusCode.Created);
        var personalId = (await JsonOf(personal)).GetProperty("id").GetInt64();

        var seen02 = await JsonOf(await _sales02.GetAsync($"{Base}/modules/{Module}/views"));
        seen02.EnumerateArray().Select(v => v.GetProperty("id").GetInt64()).Should().Equal(sharedId);
        var seen01 = await JsonOf(await _sales01.GetAsync($"{Base}/modules/{Module}/views"));
        seen01.EnumerateArray().Select(v => v.GetProperty("id").GetInt64()).Should().BeEquivalentTo([sharedId, personalId]);
        seen01.EnumerateArray().All(v => v.GetProperty("staleFieldIds").GetArrayLength() == 0).Should().BeTrue();

        // 7. 關掉預估費用的可篩選：檢視的 staleFieldIds 包含它，篩選條件被剔除
        (await _admin.PutAsJsonAsync($"{Base}/fields/{cost.Id}", new
        {
            type = "currency", label = Zh("預估費用"), config = new { scale = 0, isFilterable = false, isSortable = true }, blockId
        })).StatusCode.Should().Be(HttpStatusCode.OK);
        var stale = (await JsonOf(await _sales01.GetAsync($"{Base}/modules/{Module}/views"))).EnumerateArray()
            .Single(v => v.GetProperty("id").GetInt64() == personalId);
        stale.GetProperty("staleFieldIds").EnumerateArray().Select(x => x.GetInt64()).Should().Contain(cost.Id);
        (!stale.TryGetProperty("filter", out var f) || f.ValueKind == JsonValueKind.Null).Should().BeTrue();

        // 8. 帶預估費用條件查列表：400，錯誤鍵 filter
        var rejected = await _admin.GetAsync(
            $"{Base}/modules/{Module}/records/paged?currentPage=1&pagesSize=50&filter={Enc(costAndHandler)}");
        rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await JsonOf(rejected)).GetProperty("errors").TryGetProperty("filter", out _).Should().BeTrue();
    }
}
