using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests;

/// <summary>第 3 階段驗收情境：模具維修申請依角色看到不同的記錄、欄位與選項。</summary>
public class Phase3AcceptanceTests : IClassFixture<TestApp>
{
    private const string Module = "repair-request";

    private readonly TestApp _app;
    private readonly HttpClient _admin;

    public Phase3AcceptanceTests(TestApp app)
    {
        _app = app;
        _admin = app.AsUser("admin");
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

    private static object Priv(bool read = false, bool add = false, bool edit = false, bool delete = false, bool pricing = false) =>
        new { read, add, edit, delete, printExport = false, pricing, approve = false };

    private Task<HttpResponseMessage> Create(HttpClient c, Dictionary<string, object> values) =>
        c.PostAsJsonAsync($"{Base}/modules/{Module}/records", new { values });

    private static async Task<JsonElement> Paged(HttpClient c, string query = "")
    {
        var res = await c.GetAsync($"{Base}/modules/{Module}/records/paged?currentPage=1&pagesSize=50{query}");
        res.StatusCode.Should().Be(HttpStatusCode.OK, query);
        return await JsonOf(res);
    }

    [Fact]
    public async Task Phase3_Scenario()
    {
        var sales01 = _app.AsUser("sales01");
        var sales02 = _app.AsUser("sales02");
        var manager = _app.AsUser("manager01");
        var floor = _app.AsUser("floor01");

        // 1. admin 建模組與欄位並啟用
        (await _admin.PostAsJsonAsync($"{Base}/modules", new { key = Module, name = Zh("模具維修申請") }))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        var module = await JsonOf(await _admin.GetAsync($"{Base}/modules/{Module}"));
        var blockId = module.GetProperty("blocks")[0].GetProperty("id").GetInt64();
        var titleId = module.GetProperty("titleFieldId").GetInt64();
        var title = $"f_{titleId}";
        (await _admin.PutAsJsonAsync($"{Base}/fields/{titleId}", new
        {
            type = "text", label = Zh("申請人"), config = new { required = true }, blockId
        })).StatusCode.Should().Be(HttpStatusCode.OK);
        var category = await AddField(blockId, "enum", "故障類別", new { });
        var optDamage = await AddOption(category.Id, "模具損壞");
        await AddOption(category.Id, "機台異常");
        var optOutsource = await AddOption(category.Id, "外包");
        var cost = await AddField(blockId, "currency", "預估費用", new { scale = 0, required = true, isSortable = true });
        var note = await AddField(blockId, "textarea", "說明", new { });
        (await _admin.PostAsync($"{Base}/modules/{Module}/activate", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        // 2. 用 PUT /permissions 設定群組權限、資料範圍、欄位唯讀、選項範圍
        var groups = await JsonOf(await _admin.GetAsync($"{Base}/groups"));
        long G(string key) => groups.EnumerateArray().Single(g => g.GetProperty("key").GetString() == key).GetProperty("id").GetInt64();
        var (sales, managers, floors) = (G("sales"), G("sales-manager"), G("floor"));
        var put = await _admin.PutAsJsonAsync($"{Base}/modules/{Module}/permissions", new
        {
            groups = new object[]
            {
                new { groupId = sales, privileges = Priv(read: true, add: true, edit: true), scope = "own" },
                new { groupId = managers, privileges = Priv(read: true, add: true, edit: true, delete: true, pricing: true), scope = "all" },
                new { groupId = floors, privileges = Priv(read: true, add: true, edit: true), scope = "own" },
            },
            users = Array.Empty<object>(),
            fields = new[] { new { fieldId = note.Id, groupId = floors, access = "readOnly" } },
            options = new[] { new { optionId = optOutsource, groupIds = new[] { managers } } }
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        // 3. sales01 建 2 筆、sales02 建 1 筆，都沒送預估費用（沒有 Pricing，必填檢查跳過）
        long sales01First;
        foreach (var (client, name) in new[] { (sales01, "A1"), (sales01, "A2"), (sales02, "B1") })
        {
            var res = await Create(client, new() { [title] = name, [category.Key] = $"opt_{optDamage}" });
            res.StatusCode.Should().Be(HttpStatusCode.Created, name);
        }
        var mine = await Paged(sales01);
        sales01First = mine.GetProperty("items")[0].GetProperty("id").GetInt64();

        // 4. sales01 列表只有自己 2 筆，values 沒有預估費用；schema 沒有預估費用，permissions.delete 為 false
        mine.GetProperty("totalCount").GetInt32().Should().Be(2);
        mine.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("title").GetString())
            .Should().BeEquivalentTo(["A1", "A2"]);
        mine.GetProperty("items").EnumerateArray()
            .All(r => !r.GetProperty("values").TryGetProperty(cost.Key, out _)).Should().BeTrue();
        var schema01 = await JsonOf(await sales01.GetAsync($"{Base}/modules/{Module}/schema"));
        schema01.GetProperty("blocks").EnumerateArray().SelectMany(b => b.GetProperty("fields").EnumerateArray())
            .Select(f => f.GetProperty("id").GetInt64()).Should().NotContain(cost.Id);
        schema01.GetProperty("permissions").GetProperty("delete").GetBoolean().Should().BeFalse();

        // 5. sales01 讀 sales02 的記錄 404；刪除自己的記錄 403
        var other = (await Paged(sales02)).GetProperty("items")[0].GetProperty("id").GetInt64();
        (await sales01.GetAsync($"{Base}/records/{other}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var own = await JsonOf(await sales01.GetAsync($"{Base}/records/{sales01First}"));
        (await sales01.DeleteAsync($"{Base}/records/{sales01First}?rowVersion={own.GetProperty("rowVersion").GetString()}"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // 6. sales01 選「外包」400；manager01 選「外包」（送預估費用）201
        var denied = await Create(sales01, new() { [title] = "A3", [category.Key] = $"opt_{optOutsource}" });
        denied.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await JsonOf(denied)).GetProperty("errors").TryGetProperty(category.Key, out _).Should().BeTrue();
        (await Create(manager, new() { [title] = "M1", [category.Key] = $"opt_{optOutsource}", [cost.Key] = 8000 }))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        // 7. manager01 列表有全部 4 筆，看得到預估費用，可依預估費用排序（降冪，有填的排最前）
        var all = await Paged(manager, $"&sortBy={cost.Key}&sortOrder=desc");
        all.GetProperty("totalCount").GetInt32().Should().Be(4);
        var first = all.GetProperty("items")[0];
        first.GetProperty("title").GetString().Should().Be("M1");
        first.GetProperty("values").GetProperty(cost.Key).GetDecimal().Should().Be(8000m);

        // 8. floor01 建一筆（說明被忽略）；admin 設說明原值；floor01 改說明 → 200，存的仍是原值
        var created = await Create(floor, new() { [title] = "F1", [note.Key] = "floor 寫的" });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var floorId = (await JsonOf(created)).GetProperty("id").GetInt64();
        var adminView = await JsonOf(await _admin.GetAsync($"{Base}/records/{floorId}"));
        adminView.GetProperty("values").TryGetProperty(note.Key, out _).Should().BeFalse("floor 對說明唯讀，新增時送的值被忽略");
        (await _admin.PutAsJsonAsync($"{Base}/records/{floorId}", new
        {
            values = new Dictionary<string, object> { [title] = "F1", [cost.Key] = 100, [note.Key] = "原始說明" },
            rowVersion = adminView.GetProperty("rowVersion").GetString()
        })).StatusCode.Should().Be(HttpStatusCode.OK);
        var floorView = await JsonOf(await floor.GetAsync($"{Base}/records/{floorId}"));
        var update = await floor.PutAsJsonAsync($"{Base}/records/{floorId}", new
        {
            values = new Dictionary<string, object> { [title] = "F1b", [note.Key] = "floor 想改的" },
            rowVersion = floorView.GetProperty("rowVersion").GetString()
        });
        update.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = await JsonOf(await _admin.GetAsync($"{Base}/records/{floorId}"));
        stored.GetProperty("values").GetProperty(note.Key).GetString().Should().Be("原始說明");
        stored.GetProperty("title").GetString().Should().Be("F1b");

        // 9. 沒帶 X-User 呼叫 GET /menu 回 401；sales01 呼叫 POST /modules 回 403
        var anon = await _app.AsUser(null).GetAsync($"{Base}/menu");
        anon.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await JsonOf(anon)).GetProperty("message").GetString().Should().Be("請先選擇使用者");
        var forbidden = await sales01.PostAsJsonAsync($"{Base}/modules", new { key = "nope", name = Zh("不該建") });
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await JsonOf(forbidden)).GetProperty("message").GetString().Should().Be("沒有權限執行此操作");
    }
}
