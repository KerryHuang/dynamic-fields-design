using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests;

/// <summary>第 4 階段驗收情境：模具維修申請用公式、自動編號與規則做連動、驗證與唯讀。</summary>
public class Phase4AcceptanceTests : IClassFixture<TestApp>
{
    private const string Module = "repair-rules";

    private readonly TestApp _app;
    private readonly HttpClient _admin;

    public Phase4AcceptanceTests(TestApp app)
    {
        _app = app;
        _admin = app.AsUser("admin");
        app.Clock.Fixed = new DateTime(2026, 11, 3, 10, 0, 0);
    }

    private static Dictionary<string, string> Zh(string t) => new() { ["zh-TW"] = t };

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

    private static object Rule(string kind, string[] targets, object condition, long[]? optionIds = null, string? message = null) =>
        new { kind, condition, targets, optionIds, message = message is null ? null : Zh(message), active = true };

    private static object Cond(long field, string op, object? value = null) =>
        value is null ? new { field, op } : new { field, op, value };

    private Task<HttpResponseMessage> Create(HttpClient c, Dictionary<string, object> values) =>
        c.PostAsJsonAsync($"{Base}/modules/{Module}/records", new { values });

    private static async Task<string> ErrorOf(HttpResponseMessage res, string errorKey)
    {
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        return (await JsonOf(res)).GetProperty("errors").GetProperty(errorKey)[0].GetString()!;
    }

    [Fact]
    public async Task Phase4_Scenario()
    {
        // 1. admin 建模組與欄位：標題欄位換成自動編號，原本的文字標題欄位改名成「申請人」
        (await _admin.PostAsJsonAsync($"{Base}/modules", new { key = Module, name = Zh("模具維修申請") }))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        var module = await JsonOf(await _admin.GetAsync($"{Base}/modules/{Module}"));
        var blockId = module.GetProperty("blocks")[0].GetProperty("id").GetInt64();
        var applicantId = module.GetProperty("titleFieldId").GetInt64();
        var applicant = $"f_{applicantId}";
        (await _admin.PutAsJsonAsync($"{Base}/fields/{applicantId}", new
        {
            type = "text", label = Zh("申請人"), config = new { required = true }, blockId
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        var number = await AddField(blockId, "autonumber", "申請單號", new { format = "REP-{yyyyMM}-{0000}" });
        (await _admin.PutAsJsonAsync($"{Base}/modules/{Module}", new { name = Zh("模具維修申請"), titleFieldId = number.Id }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var applied = await AddField(blockId, "date", "申請日", new { });
        var needed = await AddField(blockId, "date", "需求日", new { });
        var category = await AddField(blockId, "enum", "故障類別", new { });
        var catMold = await AddOption(category.Id, "模具損壞");
        var catMachine = await AddOption(category.Id, "機台異常");
        var catOutsource = await AddOption(category.Id, "外包");
        var detail = await AddField(blockId, "enum", "故障細項", new { });
        var moldA = await AddOption(detail.Id, "模仁崩裂");
        var moldB = await AddOption(detail.Id, "頂針斷裂");
        var machineA = await AddOption(detail.Id, "油壓異常");
        var machineB = await AddOption(detail.Id, "加熱失效");
        var outsourceA = await AddOption(detail.Id, "委外修復");
        var vendor = await AddField(blockId, "text", "外包廠商", new { required = true });
        var cost = await AddField(blockId, "currency", "預估費用", new { scale = 0 });
        var days = await AddField(blockId, "formula", "距需求日天數",
            new { formula = $"DATEDIFF({{{needed.Id}}}, {{{applied.Id}}})", resultType = "number", scale = 0 });
        var status = await AddField(blockId, "enum", "狀態", new { });
        await AddOption(status.Id, "草稿");
        var closed = await AddOption(status.Id, "已結案");

        // 2. PUT 規則：Visibility、Validation、3 條 OptionFilter、ReadOnly
        const string dateMessage = "需求日不得早於申請日";
        var baseRules = new List<object>
        {
            Rule("visibility", [$"field:{vendor.Id}"], Cond(category.Id, "eq", $"opt_{catOutsource}")),
            Rule("validation", [$"field:{needed.Id}"], new
            {
                any = new object[]
                {
                    new { field = needed.Id, op = "isEmpty" },
                    new { field = needed.Id, op = "gte", value = new { fieldRef = applied.Id } }
                }
            }, message: dateMessage),
            Rule("optionFilter", [$"field:{detail.Id}"], Cond(category.Id, "eq", $"opt_{catMold}"), [moldA, moldB]),
            Rule("optionFilter", [$"field:{detail.Id}"], Cond(category.Id, "eq", $"opt_{catMachine}"), [machineA, machineB]),
            Rule("optionFilter", [$"field:{detail.Id}"], Cond(category.Id, "eq", $"opt_{catOutsource}"), [outsourceA]),
            Rule("readOnly", ["*"], Cond(status.Id, "eq", $"opt_{closed}")),
        };
        (await _admin.PutAsJsonAsync($"{Base}/modules/{Module}/rules", new { rules = baseRules }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await _admin.PostAsync($"{Base}/modules/{Module}/activate", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        // 3. 故障類別 = 機台異常、不填外包廠商 → 201，單號 REP-202611-0001，title 相同，天數 = 9
        Dictionary<string, object> Values(long cat, long det) => new()
        {
            [applicant] = "王小明", [applied.Key] = "2026-11-01", [needed.Key] = "2026-11-10",
            [category.Key] = $"opt_{cat}", [detail.Key] = $"opt_{det}"
        };
        var okRes = await Create(_admin, Values(catMachine, machineA));
        okRes.StatusCode.Should().Be(HttpStatusCode.Created);
        var record = await JsonOf(okRes);
        record.GetProperty("values").GetProperty(number.Key).GetString().Should().Be("REP-202611-0001");
        record.GetProperty("title").GetString().Should().Be("REP-202611-0001");
        record.GetProperty("values").GetProperty(days.Key).GetDecimal().Should().Be(9m);
        var recordId = record.GetProperty("id").GetInt64();

        // 4. 故障類別 = 外包、不填外包廠商 → 400，鍵是外包廠商
        var noVendor = await Create(_admin, Values(catOutsource, outsourceA));
        noVendor.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await JsonOf(noVendor)).GetProperty("errors").TryGetProperty(vendor.Key, out _).Should().BeTrue();

        // 5. 需求日早於申請日 → 400，訊息逐字
        var early = Values(catMachine, machineA);
        early[needed.Key] = "2026-10-20";
        (await ErrorOf(await Create(_admin, early), needed.Key)).Should().Be(dateMessage);

        // 6. 機台異常配外包的細項 → 400 選項不在可選範圍
        (await ErrorOf(await Create(_admin, Values(catMachine, outsourceA)), detail.Key)).Should().Be("選項不在可選範圍");

        // 7. 狀態改已結案 → 200；再改 → 400 此記錄目前不可修改
        var toClosed = Values(catMachine, machineA);
        toClosed[status.Key] = $"opt_{closed}";
        var close = await _admin.PutAsJsonAsync($"{Base}/records/{recordId}",
            new { values = toClosed, rowVersion = record.GetProperty("rowVersion").GetString() });
        close.StatusCode.Should().Be(HttpStatusCode.OK);
        var closedRecord = await JsonOf(close);
        var again = await _admin.PutAsJsonAsync($"{Base}/records/{recordId}",
            new { values = toClosed, rowVersion = closedRecord.GetProperty("rowVersion").GetString() });
        (await ErrorOf(again, "_record")).Should().Be("此記錄目前不可修改");

        // 8. floor 群組 Read/Add/Edit、沒有 Pricing；再加 Required 規則（預估費用 > 1000 → 外包廠商）
        var groups = await JsonOf(await _admin.GetAsync($"{Base}/groups"));
        var floorId = groups.EnumerateArray().Single(g => g.GetProperty("key").GetString() == "floor").GetProperty("id").GetInt64();
        (await _admin.PutAsJsonAsync($"{Base}/modules/{Module}/permissions", new
        {
            groups = new[]
            {
                new
                {
                    groupId = floorId,
                    privileges = new { read = true, add = true, edit = true, delete = false, printExport = false, pricing = false, approve = false },
                    scope = "all"
                }
            },
            users = Array.Empty<object>(),
            fields = Array.Empty<object>(),
            options = Array.Empty<object>()
        })).StatusCode.Should().Be(HttpStatusCode.OK);
        baseRules.Add(Rule("required", [$"field:{vendor.Id}"], Cond(cost.Id, "gt", 1000)));
        (await _admin.PutAsJsonAsync($"{Base}/modules/{Module}/rules", new { rules = baseRules }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var schema = await JsonOf(await _app.AsUser("floor01").GetAsync($"{Base}/modules/{Module}/schema"));
        schema.GetProperty("blocks").EnumerateArray().SelectMany(b => b.GetProperty("fields").EnumerateArray())
            .Select(f => f.GetProperty("id").GetInt64()).Should().NotContain(cost.Id);
        var rules = schema.GetProperty("rules").EnumerateArray().ToList();
        rules.Should().NotContain(r => r.GetRawText().Contains($"\"field\":{cost.Id}"), "引用預估費用的規則不送給看不到它的人");
        rules.Should().Contain(r => r.GetProperty("kind").GetString() == "visibility"
            && r.GetProperty("targets")[0].GetString() == $"field:{vendor.Id}");
    }
}
