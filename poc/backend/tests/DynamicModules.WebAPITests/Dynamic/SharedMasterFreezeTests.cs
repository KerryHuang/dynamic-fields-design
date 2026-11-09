using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Record = DynamicModules.Domain.Entities.Dynamic.Record;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 生命週期鎖（設計 §7）：貢獻者關閉後，它貢獻的欄位凍結——給了不同的值整筆 409、值不變，系統管理員與系統寫入也一樣；
/// 值沒變時其他欄位照常寫；凍結的公式不重算；刪除記錄的清空參照也擋；畫面拿得到唯讀原因。
/// </summary>
public class SharedMasterFreezeTests : IDisposable
{
    private const string Partner = "master-bp-partner";
    private readonly SharedMasterRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private string Key(string app, string name) => _rig.FieldKey(app, name);

    /// <summary>crmx、stk、cx 都開著：bp、ct 都有別的使用者，關閉 crmx 後主檔維持 Open，只有 crmx 的貢獻凍結。</summary>
    private async Task<JsonElement> OpenAndCloseCrmAsync(Dictionary<string, object>? extra = null)
    {
        await _rig.ImportAllAsync();
        await _rig.OpenOkAsync("crmx");
        await _rig.OpenOkAsync("stk");
        await _rig.OpenOkAsync("cx");
        var values = new Dictionary<string, object>
        {
            [Key("bp", "partner.name")] = "甲公司", [Key("bp", "partner.headcount")] = 5,
            [Key("crmx", "bp/partner.score")] = 3, [Key("crmx", "bp/partner.memo")] = "老客戶"
        };
        foreach (var (k, v) in extra ?? []) values[k] = v;
        var record = await _rig.CreateRecordAsync(_rig.Admin, Partner, values);
        record.GetProperty("values").GetProperty(Key("crmx", "bp/partner.headX2")).GetDecimal().Should().Be(10);
        await _rig.CloseOkAsync("crmx");
        _rig.StatusOf("bp").Should().Be(AppStatus.Open);
        return record;
    }

    private async Task<JsonElement> GetAsync(JsonElement record) =>
        await JsonOf(await _rig.Admin.GetAsync($"{Base}/records/{record.GetProperty("id").GetInt64()}"));

    private async Task<HttpResponseMessage> PutAsync(JsonElement record, Dictionary<string, object?> values)
    {
        var fresh = await GetAsync(record);
        return await _rig.Admin.PutAsJsonAsync($"{Base}/records/{record.GetProperty("id").GetInt64()}",
            new { values, rowVersion = fresh.GetProperty("rowVersion").GetString() });
    }

    private static async Task ShouldBeFrozenAsync(HttpResponseMessage res)
    {
        res.StatusCode.Should().Be(HttpStatusCode.Conflict, await res.Content.ReadAsStringAsync());
        (await res.Content.ReadAsStringAsync()).Should().Contain("已關閉的應用程式").And.Contain("客戶關係");
    }

    [Fact]
    public async Task Update_ADifferentValue_Is409ForTheAdminToo_ValueUnchanged_SameValuePassesWithOtherFields()
    {
        var record = await OpenAndCloseCrmAsync();
        var score = Key("crmx", "bp/partner.score");

        await ShouldBeFrozenAsync(await PutAsync(record, new() { [score] = 9, [Key("bp", "partner.name")] = "甲公司二" }));
        await ShouldBeFrozenAsync(await PutAsync(record, new() { [score] = null }));
        var after = await GetAsync(record);
        after.GetProperty("values").GetProperty(score).GetInt32().Should().Be(3);
        after.GetProperty("title").GetString().Should().Be("甲公司", "整筆不寫");

        var ok = await PutAsync(record, new() { [score] = 3, [Key("bp", "partner.name")] = "甲公司二" });
        ok.StatusCode.Should().Be(HttpStatusCode.OK, await ok.Content.ReadAsStringAsync());
        var saved = await JsonOf(ok);
        saved.GetProperty("title").GetString().Should().Be("甲公司二");
        saved.GetProperty("values").GetProperty(score).GetInt32().Should().Be(3);
    }

    /// <summary>
    /// 審查必修 1：倉管看不到 CRM 的分數。CRM 關閉後倉管猜分數送進來，猜對猜錯都一樣 200、值不變、回應不提欄位名稱，
    /// 不能靠成功／409 分辨隱藏的舊值。
    /// </summary>
    [Fact]
    public async Task AHiddenFrozenField_ItsInputIsIgnored_RightAndWrongGuessesLookTheSame()
    {
        await _rig.ImportAllAsync();
        await _rig.OpenOkAsync("crmx");
        await _rig.OpenOkAsync("stk");
        await _rig.OpenOkAsync("cx");
        var keeper = _rig.App.AsUser("keeper01");
        var score = Key("crmx", "bp/partner.score");
        var name = Key("bp", "partner.name");
        var record = await _rig.CreateRecordAsync(keeper, Partner, new Dictionary<string, object> { [name] = "甲公司" });
        (await PutAsync(record, new() { [score] = 3 })).StatusCode.Should().Be(HttpStatusCode.OK);
        await _rig.CloseOkAsync("crmx");

        var id = record.GetProperty("id").GetInt64();
        foreach (var (guess, title) in new[] { (3, "甲公司二"), (4, "甲公司三") })
        {
            var fresh = await JsonOf(await keeper.GetAsync($"{Base}/records/{id}"));
            var res = await keeper.PutAsJsonAsync($"{Base}/records/{id}", new
            {
                values = new Dictionary<string, object> { [score] = guess, [name] = title },
                rowVersion = fresh.GetProperty("rowVersion").GetString()
            });
            var body = await res.Content.ReadAsStringAsync();
            res.StatusCode.Should().Be(HttpStatusCode.OK, $"猜 {guess}：{body}");
            body.Should().NotContain("分數").And.NotContain(score);
        }
        (await GetAsync(record)).GetProperty("values").GetProperty(score).GetInt32().Should().Be(3, "看不到的欄位不吃輸入");
        (await GetAsync(record)).GetProperty("title").GetString().Should().Be("甲公司三", "其他欄位照常寫");
    }

    [Fact]
    public async Task Create_WithAValueForAFrozenField_Is409_WithoutItSucceedsAndTheFieldStaysEmpty()
    {
        await OpenAndCloseCrmAsync();
        var res = await _rig.Admin.PostAsJsonAsync($"{Base}/modules/{Partner}/records", new
        {
            values = new Dictionary<string, object> { [Key("bp", "partner.name")] = "乙公司", [Key("crmx", "bp/partner.memo")] = "新備忘" }
        });
        await ShouldBeFrozenAsync(res);
        _rig.Db(db => db.Set<Record>().Count(r => r.Title == "乙公司")).Should().Be(0);

        var created = await _rig.CreateRecordAsync(_rig.Admin, Partner, new Dictionary<string, object>
            { [Key("bp", "partner.name")] = "乙公司", [Key("bp", "partner.headcount")] = 2 });
        created.GetProperty("values").TryGetProperty(Key("crmx", "bp/partner.headX2"), out _).Should().BeFalse("凍結的公式不算");
    }

    [Fact]
    public async Task FrozenFormula_KeepsItsLastValue_EvenInBatchRecalc_AndUpdatesAfterReopen()
    {
        var record = await OpenAndCloseCrmAsync();
        var headX2 = Key("crmx", "bp/partner.headX2");
        (await PutAsync(record, new() { [Key("bp", "partner.headcount")] = 7 })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetAsync(record)).GetProperty("values").GetProperty(headX2).GetDecimal().Should().Be(10, "基本欄位改了，凍結的公式不重算");

        // 在基本區塊加一個公式欄位會觸發整個模組的 FormulaRecalc
        await AddField(_rig.Admin, Partner, "formula", new { formula = $"{{{_rig.FieldId("bp", "partner.headcount")}}} + 1", resultType = "number" });
        (await GetAsync(record)).GetProperty("values").GetProperty(headX2).GetDecimal().Should().Be(10, "批次重算也不動凍結欄位");

        await _rig.ReopenOkAsync("crmx");
        (await PutAsync(record, new() { [Key("bp", "partner.name")] = "甲公司二" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetAsync(record)).GetProperty("values").GetProperty(headX2).GetDecimal().Should().Be(14, "重開後下一次存檔才更新");
    }

    [Fact]
    public async Task Import_AColumnOfAFrozenField_IsRefusedWhole()
    {
        await OpenAndCloseCrmAsync();
        var csv = "名稱,備忘\n丙公司,新備忘\n";
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        var res = await _rig.Admin.PostAsync($"{Base}/modules/{Partner}/import?commit=true",
            new MultipartFormDataContent { { file, "file", "import.csv" } });
        await ShouldBeFrozenAsync(res);
        _rig.Db(db => db.Set<Record>().Count(r => r.Title == "丙公司")).Should().Be(0);
    }

    [Fact]
    public async Task Conversion_WritingAFrozenField_FailsWhole()
    {
        await OpenAndCloseCrmAsync();
        var shipNo = _rig.FieldId("stk", "ship.no");
        var key = "cv-" + Guid.NewGuid().ToString("N")[..8];
        var saved = await _rig.Admin.PostAsJsonAsync($"{Base}/conversions", new
        {
            key, name = new Dictionary<string, string> { ["zh-TW"] = "出貨轉往來對象" },
            sourceModuleKey = "stk-ship", targetModuleKey = Partner, active = true,
            definition = new
            {
                sourceStates = Array.Empty<long>(),
                header = new
                {
                    sourceFieldId = _rig.FieldId("stk", "bp/partner.fromShip"),
                    copy = new[] { new { from = shipNo, to = _rig.FieldId("bp", "partner.name") }, new { from = shipNo, to = _rig.FieldId("crmx", "bp/partner.memo") } }
                }
            }
        });
        saved.StatusCode.Should().Be(HttpStatusCode.Created, await saved.Content.ReadAsStringAsync());
        var ship = await _rig.CreateRecordAsync(_rig.Admin, "stk-ship", new Dictionary<string, object> { [Key("stk", "ship.no")] = "SH-1" });

        var converted = await _rig.Admin.PostAsJsonAsync($"{Base}/records/{ship.GetProperty("id").GetInt64()}/conversions/{key}/convert", new { });
        await ShouldBeFrozenAsync(converted);
        var partnerId = _rig.Db(db => db.Set<Module>().Single(m => m.Key == Partner).Id);
        _rig.Db(db => db.Set<Record>().Count(r => r.ModuleId == partnerId && r.Title == "SH-1")).Should().Be(0, "目標記錄整筆不建立");
    }

    [Fact]
    public async Task Event_WritingAFrozenField_FailsTheWholeAction()
    {
        await OpenAndCloseCrmAsync();
        var ev = await _rig.Admin.PostAsJsonAsync($"{Base}/events", new
        {
            key = "ev-" + Guid.NewGuid().ToString("N")[..8], name = new Dictionary<string, string> { ["zh-TW"] = "出貨建往來對象" },
            sourceModuleKey = "stk-ship", active = true,
            definition = new
            {
                trigger = new { on = "create" },
                effects = new[]
                {
                    new
                    {
                        code = "mk", kind = "create", targetModuleKey = Partner,
                        set = new object[]
                        {
                            new { to = _rig.FieldId("bp", "partner.name"), @const = "事件公司" },
                            new { to = _rig.FieldId("crmx", "bp/partner.memo"), @const = "事件備忘" }
                        }
                    }
                }
            }
        });
        ev.StatusCode.Should().Be(HttpStatusCode.Created, await ev.Content.ReadAsStringAsync());

        var res = await _rig.Admin.PostAsJsonAsync($"{Base}/modules/stk-ship/records",
            new { values = new Dictionary<string, object> { [Key("stk", "ship.no")] = "SH-2" } });
        await ShouldBeFrozenAsync(res);
        _rig.Db(db => db.Set<Record>().Count(r => r.Title == "SH-2" || r.Title == "事件公司")).Should().Be(0, "觸發的記錄也不建立");
    }

    [Fact]
    public async Task DeletingARecord_ThatWouldClearAFrozenReference_Is409_NothingChanges()
    {
        await _rig.ImportAllAsync();
        await _rig.OpenOkAsync("crmx");
        await _rig.OpenOkAsync("stk");
        await _rig.OpenOkAsync("cx");
        var contact = await _rig.CreateRecordAsync(_rig.Admin, "master-ct-contact", new Dictionary<string, object> { [Key("ct", "contact.name")] = "王小明" });
        var contactId = contact.GetProperty("id").GetInt64();
        var partner = await _rig.CreateRecordAsync(_rig.Admin, Partner, new Dictionary<string, object>
            { [Key("bp", "partner.name")] = "甲公司", [Key("crmx", "bp/partner.mainContact")] = contactId });
        await _rig.CloseOkAsync("crmx");
        _rig.StatusOf("ct").Should().Be(AppStatus.Open, "cx 還在用聯絡人");

        var res = await _rig.Admin.DeleteAsync($"{Base}/records/{contactId}?rowVersion={contact.GetProperty("rowVersion").GetString()}");
        await ShouldBeFrozenAsync(res);
        _rig.Db(db => db.Set<Record>().Single(r => r.Id == contactId).DeleteFlag).Should().BeFalse();
        (await GetAsync(partner)).GetProperty("values").GetProperty(Key("crmx", "bp/partner.mainContact")).GetInt64().Should().Be(contactId);
    }

    /// <summary>
    /// 審查必修 5：CRM 的參照欄位以 lock 模式帶入備忘。CRM 關閉後明確送不同的備忘與新名稱：帶值會先移除 lock 欄位的輸入，
    /// 凍結檢查要看原始請求，整筆 409、名稱也不寫。
    /// </summary>
    [Fact]
    public async Task Update_AFrozenLockFillTarget_WithADifferentValue_Is409Whole_EvenThoughTheFillDropsTheInput()
    {
        await _rig.ImportAllAsync();
        await _rig.OpenOkAsync("crmx");
        await _rig.OpenOkAsync("stk");
        await _rig.OpenOkAsync("cx");
        var mainContact = _rig.FieldId("crmx", "bp/partner.mainContact");
        var memo = _rig.FieldId("crmx", "bp/partner.memo");
        var contactName = _rig.FieldId("ct", "contact.name");
        _rig.Db(db =>
        {
            var field = db.Set<Field>().Single(f => f.Id == mainContact);
            field.Config = field.Config with { Fill = new FillConfig(FillConfig.Lock, [new FillMapping(contactName, memo)]) };
        });
        var contact = await _rig.CreateRecordAsync(_rig.Admin, "master-ct-contact", new Dictionary<string, object> { [Key("ct", "contact.name")] = "王小明" });
        var record = await _rig.CreateRecordAsync(_rig.Admin, Partner, new Dictionary<string, object>
            { [Key("bp", "partner.name")] = "甲公司", [Key("crmx", "bp/partner.mainContact")] = contact.GetProperty("id").GetInt64() });
        record.GetProperty("values").GetProperty(Key("crmx", "bp/partner.memo")).GetString().Should().Be("王小明", "lock 帶值");
        await _rig.CloseOkAsync("crmx");

        await ShouldBeFrozenAsync(await PutAsync(record, new() { [Key("crmx", "bp/partner.memo")] = "不同的備忘", [Key("bp", "partner.name")] = "甲公司二" }));
        var after = await GetAsync(record);
        after.GetProperty("title").GetString().Should().Be("甲公司", "整筆回滾");
        after.GetProperty("values").GetProperty(Key("crmx", "bp/partner.memo")).GetString().Should().Be("王小明");

        var same = await PutAsync(record, new() { [Key("crmx", "bp/partner.memo")] = "王小明", [Key("bp", "partner.name")] = "甲公司二" });
        same.StatusCode.Should().Be(HttpStatusCode.OK, await same.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SchemaAndRecord_ShowFrozenFieldsAsReadOnlyWithTheReason_TheBlockCollapsed()
    {
        var record = await OpenAndCloseCrmAsync();
        var schema = await JsonOf(await _rig.Admin.GetAsync($"{Base}/modules/{Partner}/schema"));
        var blocks = schema.GetProperty("blocks").EnumerateArray().ToList();
        var crm = blocks.Single(b => b.GetProperty("label").GetProperty("zh-TW").GetString() == "CRM");
        crm.GetProperty("collapsed").GetBoolean().Should().BeTrue();
        var score = crm.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("key").GetString() == Key("crmx", "bp/partner.score"));
        score.GetProperty("config").GetProperty("readOnly").GetBoolean().Should().BeTrue();
        score.GetProperty("lockedBy").GetProperty("appKey").GetString().Should().Be("crmx");
        score.GetProperty("lockedBy").GetProperty("appName").GetProperty("zh-TW").GetString().Should().Be("客戶關係");
        var stock = blocks.Single(b => b.GetProperty("label").GetProperty("zh-TW").GetString() == "進銷存");
        (stock.GetProperty("fields")[0].TryGetProperty("lockedBy", out var none) && none.ValueKind != JsonValueKind.Null).Should().BeFalse("進銷存開著");

        var read = await GetAsync(record);
        read.GetProperty("lockedBy").GetProperty(Key("crmx", "bp/partner.score")).GetProperty("appKey").GetString().Should().Be("crmx");
        read.GetProperty("lockedBy").TryGetProperty(Key("stk", "bp/partner.shipAddress"), out _).Should().BeFalse();
    }

    [Fact]
    public async Task Reopen_MakesTheFieldsWritableAgain()
    {
        var record = await OpenAndCloseCrmAsync();
        await _rig.ReopenOkAsync("crmx");
        var res = await PutAsync(record, new() { [Key("crmx", "bp/partner.score")] = 9 });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        (await JsonOf(res)).GetProperty("values").GetProperty(Key("crmx", "bp/partner.score")).GetInt32().Should().Be(9);
    }
}
