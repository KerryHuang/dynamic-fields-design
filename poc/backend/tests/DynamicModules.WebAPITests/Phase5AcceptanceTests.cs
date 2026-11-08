using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests;

/// <summary>第 5 階段驗收情境：維修申請參照模具、維修紀錄參照申請，模具擴充模組，刪除行為與反向關聯。</summary>
public class Phase5AcceptanceTests : IClassFixture<TestApp>
{
    private const string Req = "repair-req";
    private const string Log = "repair-log";
    private const string Ext = "mold-ext";

    private readonly TestApp _app;
    private readonly HttpClient _admin;

    public Phase5AcceptanceTests(TestApp app)
    {
        _app = app;
        _admin = app.AsUser("admin");
        app.Clock.Fixed = new DateTime(2026, 11, 4, 10, 0, 0);
    }

    private static Dictionary<string, string> Zh(string t) => new() { ["zh-TW"] = t };

    private async Task<(long Id, string Key)> AddField(string module, long blockId, string type, string label, object config)
    {
        var res = await _admin.PostAsJsonAsync($"{Base}/modules/{module}/fields",
            new { blockId, type, label = Zh(label), config });
        res.StatusCode.Should().Be(HttpStatusCode.Created, label);
        var f = await JsonOf(res);
        return (f.GetProperty("id").GetInt64(), f.GetProperty("key").GetString()!);
    }

    private async Task<(long BlockId, string Title)> NewModule(string key, string name, object? extra = null)
    {
        var body = new Dictionary<string, object> { ["key"] = key, ["name"] = Zh(name) };
        if (extra is not null)
            foreach (var p in extra.GetType().GetProperties()) body[p.Name] = p.GetValue(extra)!;
        (await _admin.PostAsJsonAsync($"{Base}/modules", body)).StatusCode.Should().Be(HttpStatusCode.Created, key);
        var m = await JsonOf(await _admin.GetAsync($"{Base}/modules/{key}"));
        var title = m.TryGetProperty("titleFieldId", out var t) && t.ValueKind == JsonValueKind.Number ? $"f_{t.GetInt64()}" : "";
        return (m.GetProperty("blocks")[0].GetProperty("id").GetInt64(), title);
    }

    private Task<HttpResponseMessage> CreateRecord(HttpClient c, string module, Dictionary<string, object> values, string? hostKey = null) =>
        c.PostAsJsonAsync($"{Base}/modules/{module}/records", new { values, hostKey });

    private static async Task<long> IdOf(HttpResponseMessage res)
    {
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await JsonOf(res)).GetProperty("id").GetInt64();
    }

    private async Task<HttpResponseMessage> Delete(HttpClient c, long id)
    {
        var rec = await JsonOf(await c.GetAsync($"{Base}/records/{id}"));
        return await c.DeleteAsync($"{Base}/records/{id}?rowVersion={rec.GetProperty("rowVersion").GetString()}");
    }

    private static object Priv(bool read = false, bool add = false) =>
        new { read, add, edit = false, delete = false, printExport = false, pricing = false, approve = false };

    [Fact]
    public async Task Phase5_Scenario()
    {
        // 1. repair-req：申請人（標題）、模具（參照既有實體，必填）、故障說明
        var (reqBlock, applicantKey) = await NewModule(Req, "模具維修申請");
        var mold = await AddField(Req, reqBlock, "reference", "模具",
            new { required = true, refTarget = new { kind = "entity", key = "test.entity" } });
        var memo = await AddField(Req, reqBlock, "textarea", "故障說明", new { });
        await Activate(_admin, Req);

        // 2. repair-log：處理內容（標題）、申請單（參照 repair-req，restrict）、工時
        var (logBlock, logTitleKey) = await NewModule(Log, "維修紀錄");
        var reqRef = await AddField(Log, logBlock, "reference", "申請單",
            new { refTarget = new { kind = "module", key = Req }, onDelete = "restrict" });
        await AddField(Log, logBlock, "decimal", "工時", new { scale = 1 });
        await Activate(_admin, Log);

        // 3. 擴充模組 mold-ext
        var (extBlock, _) = await NewModule(Ext, "模具擴充", new { kind = "extension", hostEntityKey = "test.entity" });
        await AddField(Ext, extBlock, "integer", "保養週期", new { });
        var lastDate = await AddField(Ext, extBlock, "date", "上次保養日", new { });
        var cycle = (await JsonOf(await _admin.GetAsync($"{Base}/modules/{Ext}"))).GetProperty("blocks")[0]
            .GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("type").GetString() == "integer")
            .GetProperty("key").GetString()!;
        await Activate(_admin, Ext);

        // 4. 申請 A 參照 E-0001；E-9999 被擋
        var a = await IdOf(await CreateRecord(_admin, Req,
            new() { [applicantKey] = "王小明", [mold.Key] = "E-0001", [memo.Key] = "頂針斷裂" }));
        var aRec = await JsonOf(await _admin.GetAsync($"{Base}/records/{a}"));
        var info = aRec.GetProperty("references").GetProperty(mold.Key);
        info.GetProperty("key").GetString().Should().Be("E-0001");
        info.GetProperty("title").GetString().Should().Be("E-0001 甲號");
        info.GetProperty("state").GetString().Should().Be("ok");
        var bad = await CreateRecord(_admin, Req, new() { [applicantKey] = "x", [mold.Key] = "E-9999" });
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await JsonOf(bad)).GetProperty("errors").GetProperty(mold.Key)[0].GetString().Should().Be("參照的資料不存在或無權選取");

        // 5. 兩筆維修紀錄參照 A，反向分頁 2 筆
        var log1 = await IdOf(await CreateRecord(_admin, Log, new() { [logTitleKey] = "換頂針", [reqRef.Key] = a }));
        var log2 = await IdOf(await CreateRecord(_admin, Log, new() { [logTitleKey] = "試模", [reqRef.Key] = a }));
        var tabs = await JsonOf(await _admin.GetAsync($"{Base}/records/{a}/related"));
        tabs.GetArrayLength().Should().Be(1);
        tabs[0].GetProperty("moduleKey").GetString().Should().Be(Log);
        var tabField = tabs[0].GetProperty("fieldId").GetInt64();
        (await JsonOf(await _admin.GetAsync($"{Base}/records/{a}/related/{tabField}/paged?page=1&size=20")))
            .GetProperty("totalCount").GetInt32().Should().Be(2);

        // 6. 刪除 A 被擋，清掉維修紀錄後可刪
        var blocked = await Delete(_admin, a);
        blocked.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var blockedBody = await JsonOf(blocked);
        blockedBody.GetProperty("message").GetString().Should().Be("還有其他資料參照這筆記錄，不能刪除");
        blockedBody.GetProperty("total").GetInt32().Should().Be(2);
        (await Delete(_admin, log1)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Delete(_admin, log2)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Delete(_admin, a)).StatusCode.Should().Be(HttpStatusCode.OK);

        // 7. 擴充資料：標題取自模具，同一筆模具只能有一筆
        var ext = await CreateRecord(_admin, Ext, new() { [cycle] = 90 }, "E-0001");
        ext.StatusCode.Should().Be(HttpStatusCode.Created);
        (await JsonOf(ext)).GetProperty("title").GetString().Should().Be("E-0001 甲號");
        var dup = await CreateRecord(_admin, Ext, new() { [cycle] = 30 }, "E-0001");
        dup.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await JsonOf(dup)).GetProperty("message").GetString().Should().Be("這筆既有資料已經有擴充資料");
        var byHost = await _admin.GetAsync($"{Base}/modules/{Ext}/records/by-host/E-0001");
        byHost.StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonOf(byHost)).GetProperty("values").GetProperty(cycle).GetInt32().Should().Be(90);

        // 8. 權限：floor 對 repair-log 只看自己的；對 repair-req 需要讀取才能挑選與檢視
        var groups = await JsonOf(await _admin.GetAsync($"{Base}/groups"));
        var floorId = groups.EnumerateArray().Single(g => g.GetProperty("key").GetString() == "floor").GetProperty("id").GetInt64();
        async Task Grant(string module, object privileges, string scope) =>
            (await _admin.PutAsJsonAsync($"{Base}/modules/{module}/permissions", new
            {
                groups = new[] { new { groupId = floorId, privileges, scope } },
                users = Array.Empty<object>(), fields = Array.Empty<object>(), options = Array.Empty<object>()
            })).StatusCode.Should().Be(HttpStatusCode.OK);
        await Grant(Req, Priv(read: true), "all");
        await Grant(Log, Priv(read: true, add: true), "own");

        var b = await IdOf(await CreateRecord(_admin, Req, new() { [applicantKey] = "李小華", [mold.Key] = "E-0003" }));
        var floor = _app.AsUser("floor01");
        await IdOf(await CreateRecord(floor, Log, new() { [logTitleKey] = "現場處理", [reqRef.Key] = b }));
        await IdOf(await CreateRecord(_admin, Log, new() { [logTitleKey] = "主管處理", [reqRef.Key] = b }));
        var floorTabs = await JsonOf(await floor.GetAsync($"{Base}/records/{b}/related"));
        floorTabs.GetArrayLength().Should().Be(1);
        var floorPaged = await JsonOf(await floor.GetAsync(
            $"{Base}/records/{b}/related/{floorTabs[0].GetProperty("fieldId").GetInt64()}/paged?page=1&size=20"));
        floorPaged.GetProperty("totalCount").GetInt32().Should().Be(1);

        // 9. E-0002 被標為已刪除：參照它的申請顯示 missing，只改故障說明照樣存得進去
        var c = await IdOf(await CreateRecord(_admin, Req, new() { [applicantKey] = "陳大文", [mold.Key] = "E-0002" }));
        _app.Services.GetRequiredService<TestEntityProvider>().Delete("E-0002");
        var cRec = await JsonOf(await _admin.GetAsync($"{Base}/records/{c}"));
        var gone = cRec.GetProperty("references").GetProperty(mold.Key);
        gone.GetProperty("state").GetString().Should().Be("missing");
        gone.TryGetProperty("title", out var goneTitle).Should().BeFalse("null 屬性不輸出");
        goneTitle.ValueKind.Should().Be(JsonValueKind.Undefined);
        var edit = await _admin.PutAsJsonAsync($"{Base}/records/{c}", new
        {
            values = new Dictionary<string, object> { [applicantKey] = "陳大文", [mold.Key] = "E-0002", [memo.Key] = "只改說明" },
            rowVersion = cRec.GetProperty("rowVersion").GetString()
        });
        edit.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
