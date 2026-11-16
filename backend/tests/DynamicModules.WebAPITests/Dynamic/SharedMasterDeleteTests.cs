using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;
using Record = DynamicModules.Domain.Entities.Dynamic.Record;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 刪除貢獻者（設計 §7 刪除）：參照自己模組的貢獻欄位連同值清除、其他有值的停用並記成孤兒、沒值的刪掉、空區塊刪掉；
/// 自己的轉換跟著刪；外部引用 409；確認數含清除的值；最後一個使用者刪除時保留主檔並警告；重開時接回孤兒；主檔只有沒有使用者時能刪。
/// </summary>
public class SharedMasterDeleteTests : IDisposable
{
    private const string Partner = "master-bp-partner";
    private readonly SharedMasterRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private string Key(string app, string name) => _rig.FieldKey(app, name);

    private async Task<JsonElement> CreateLeadAsync(string company) =>
        await _rig.CreateRecordAsync(_rig.Admin, "crmx-lead", new Dictionary<string, object> { [Key("crmx", "lead.company")] = company });

    /// <summary>潛在客戶轉往來對象：往來對象的 fromLead 指回潛在客戶。</summary>
    private async Task<JsonElement> ConvertAsync(JsonElement lead)
    {
        var key = _rig.Db(db => db.Set<Conversion>().Single(c => c.Key.Contains("lead-to-partner")).Key);
        var res = await _rig.Admin.PostAsJsonAsync($"{Base}/records/{lead.GetProperty("id").GetInt64()}/conversions/{key}/convert", new { });
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return await JsonOf(res);
    }

    private async Task SoftDeleteAsync(JsonElement record)
    {
        var id = record.GetProperty("id").GetInt64();
        var fresh = await JsonOf(await _rig.Admin.GetAsync($"{Base}/records/{id}"));
        var res = await _rig.Admin.DeleteAsync($"{Base}/records/{id}?rowVersion={fresh.GetProperty("rowVersion").GetString()}");
        res.IsSuccessStatusCode.Should().BeTrue(await res.Content.ReadAsStringAsync());
    }

    private async Task UpdateAsync(JsonElement record, Dictionary<string, object?> values)
    {
        var id = record.GetProperty("id").GetInt64();
        var fresh = await JsonOf(await _rig.Admin.GetAsync($"{Base}/records/{id}"));
        var res = await _rig.Admin.PutAsJsonAsync($"{Base}/records/{id}", new { values, rowVersion = fresh.GetProperty("rowVersion").GetString() });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
    }

    /// <summary>送錯的確認數拿到預覽（409 帶 check），再用 purgedTotal 刪除。</summary>
    private async Task<JsonElement> PreviewAsync(string key)
    {
        var res = await _rig.Admin.DeleteAsync($"{SharedMasterRig.Apps}/{key}?purged=-1");
        res.StatusCode.Should().Be(HttpStatusCode.Conflict, await res.Content.ReadAsStringAsync());
        return await JsonOf(res);
    }

    private async Task<JsonElement> DeleteOkAsync(string key)
    {
        var preview = await PreviewAsync(key);
        preview.GetProperty("canDelete").GetBoolean().Should().BeTrue(preview.ToString());
        var res = await _rig.Admin.DeleteAsync($"{SharedMasterRig.Apps}/{key}?purged={preview.GetProperty("purgedTotal").GetInt32()}");
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return await JsonOf(res);
    }

    private Field FieldById(long id) => _rig.Db(db => db.Set<Field>().Single(f => f.Id == id));
    private bool FieldExists(long id) => _rig.Db(db => db.Set<Field>().Any(f => f.Id == id));
    private string? Raw(long recordId, string key) => _rig.Db(db =>
        JsonDocument.Parse(db.Set<Record>().Single(r => r.Id == recordId).ValuesJson).RootElement.TryGetProperty(key, out var v) ? v.ToString() : null);

    /// <summary>crmx（與 stk）開著：一筆轉出來的往來對象，帶產業；之後軟刪潛在客戶（fromLead 被 setNull 清空）並關閉 crmx。</summary>
    private async Task<(JsonElement Partner, long Industry, long FromLead, long Score)> ConvertedThenClosedAsync(bool withStock)
    {
        await _rig.ImportAllAsync();
        await _rig.OpenOkAsync("crmx");
        if (withStock) await _rig.OpenOkAsync("stk");
        var lead = await CreateLeadAsync("甲公司");
        var partner = await ConvertAsync(lead);
        var industry = _rig.FieldId("crmx", "bp/partner.industry");
        var option = _rig.Db(db => db.Set<FieldOption>().First(o => o.FieldId == industry).Id);
        await UpdateAsync(partner, new() { [Key("crmx", "bp/partner.industry")] = $"opt_{option}" });
        var ids = (industry, _rig.FieldId("crmx", "bp/partner.fromLead"), _rig.FieldId("crmx", "bp/partner.score"));
        Raw(partner.GetProperty("id").GetInt64(), Key("crmx", "bp/partner.fromLead")).Should().NotBeNull();
        await SoftDeleteAsync(lead);
        Raw(partner.GetProperty("id").GetInt64(), Key("crmx", "bp/partner.fromLead")).Should().BeNull("setNull 已清空");
        await _rig.CloseOkAsync("crmx");
        return (partner, ids.industry, ids.Item2, ids.Item3);
    }

    [Fact]
    public async Task DeletingANonLastUser_PurgesOwnReferences_OrphansValuedFields_DropsEmptyOnes_DeletesOwnConversion()
    {
        var (partner, industry, fromLead, score) = await ConvertedThenClosedAsync(withStock: true);
        var preview = await PreviewAsync("crmx");
        preview.GetProperty("blockers").GetArrayLength().Should().Be(0, preview.ToString());
        preview.GetProperty("purgedTotal").GetInt32().Should().Be(1, "一筆軟刪的潛在客戶，fromLead 已沒有值");
        preview.GetProperty("orphaned").EnumerateArray().Select(o => o.GetProperty("field").GetString()).Should().Contain("產業");

        var deleted = await DeleteOkAsync("crmx");
        deleted.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()).Should()
            .Equal(["共用主檔「聯絡人」已沒有應用程式使用，仍有 0 筆資料，已保留"], "往來對象還有進銷存在用，聯絡人只有 crmx 用");

        FieldExists(fromLead).Should().BeFalse("參照自己模組的貢獻欄位一起清除");
        FieldExists(score).Should().BeFalse("沒有值的貢獻欄位直接刪");
        FieldById(industry).Status.Should().Be(FieldStatus.Inactive);
        Raw(partner.GetProperty("id").GetInt64(), $"f_{industry}").Should().NotBeNull("值保留");
        _rig.Db(db => db.Set<AppOrphan>().Where(o => o.FormerAppKey == "crmx").Select(o => $"{o.Kind}:{o.Name}").ToList())
            .Should().BeEquivalentTo("Field:bp/partner.industry", "Option:bp/partner.manufacturing", "Option:bp/partner.retail", "Block:bp/partner.crm");
        _rig.Blocks(Partner).Select(b => b.Block).Should().Equal("基本資料", "CRM", "進銷存");
        _rig.Db(db => db.Set<Conversion>().Any(c => c.Key.Contains("lead-to-partner"))).Should().BeFalse("自己的轉換隨應用程式刪除");
        _rig.Db(db => db.Set<Record>().Single(r => r.Id == partner.GetProperty("id").GetInt64()).DeleteFlag).Should().BeFalse();
        _rig.Db(db => db.Set<AppGrant>().Count(g => g.AppId != null && !db.Set<App>().Any(a => a.Id == g.AppId))).Should().Be(0);
        _rig.StatusOf("bp").Should().Be(AppStatus.Open);
        _rig.StatusOf("ct").Should().Be(AppStatus.Closed, "聯絡人只剩已刪的 crmx 在用");
        await _rig.AssertIntegrityAsync();
    }

    [Fact]
    public async Task DeletingTheLastUser_KeepsTheMasterClosed_WithAWarning_EvenThoughThePurgeTouchesAClosedModule()
    {
        await ConvertedThenClosedAsync(withStock: false);
        _rig.StatusOf("bp").Should().Be(AppStatus.Closed);
        var deleted = await DeleteOkAsync("crmx");
        deleted.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()).Should()
            .Contain(w => w!.Contains("往來對象") && w.Contains("1 筆資料"), "聯絡人仍用往來對象，但已沒有應用程式經它使用");
        _rig.StatusOf("bp").Should().Be(AppStatus.Closed);
        FieldExists(_rig.Db(db => db.Set<AppOrphan>().Single(o => o.Kind == OrphanKind.Field && o.Name == "bp/partner.industry").FieldId!.Value)).Should().BeTrue();
        await _rig.AssertIntegrityAsync();

        // 之後別的應用程式開啟：重新採用保留的主檔，資料還在
        await _rig.OpenOkAsync("stk");
        _rig.StatusOf("bp").Should().Be(AppStatus.Open);
        _rig.Db(db => db.Set<Record>().Count(r => r.ModuleId == db.Set<Module>().Single(m => m.Key == Partner).Id && !r.DeleteFlag)).Should().Be(1);
        await _rig.AssertIntegrityAsync();
    }

    [Fact]
    public async Task ResidualOwnReferenceValues_AreCountedInPurged_AndAStaleConfirmationIs409()
    {
        await _rig.ImportAllAsync();
        await _rig.OpenOkAsync("crmx");
        var lead = await CreateLeadAsync("甲公司");
        var partner = await ConvertAsync(lead);
        // 先軟刪往來對象、再軟刪潛在客戶：軟刪的往來對象上 fromLead 留有值（setNull 清不到它）
        await SoftDeleteAsync(partner);
        await SoftDeleteAsync(lead);
        Raw(partner.GetProperty("id").GetInt64(), Key("crmx", "bp/partner.fromLead")).Should().NotBeNull();
        var other0 = (await _rig.CreateRecordAsync(_rig.Admin, Partner, new Dictionary<string, object> { [Key("bp", "partner.name")] = "乙公司" }))
            .GetProperty("id").GetInt64();
        await _rig.CloseOkAsync("crmx");

        var preview = await PreviewAsync("crmx");
        preview.GetProperty("clearedValues").GetInt32().Should().Be(1);
        preview.GetProperty("cleared").EnumerateArray().Single().GetProperty("field").GetString().Should().Be("來源潛在客戶");
        preview.GetProperty("purgedTotal").GetInt32().Should().Be(2, "一筆軟刪的潛在客戶加一筆清除的值");
        (await _rig.Admin.DeleteAsync($"{SharedMasterRig.Apps}/crmx?purged=1")).StatusCode.Should().Be(HttpStatusCode.Conflict, "只算記錄的舊確認數");

        // 預覽之後又多一筆殘留值：舊的確認數 409，不會沒經確認就清掉
        var fromLeadKey = Key("crmx", "bp/partner.fromLead");
        _rig.Db(db =>
        {
            var other = db.Set<Record>().Single(r => r.Id == other0);
            var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(other.ValuesJson)!;
            values[fromLeadKey] = JsonSerializer.SerializeToElement(lead.GetProperty("id").GetInt64());
            other.ValuesJson = JsonSerializer.Serialize(values);
        });
        (await _rig.Admin.DeleteAsync($"{SharedMasterRig.Apps}/crmx?purged=2")).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await PreviewAsync("crmx")).GetProperty("purgedTotal").GetInt32().Should().Be(3);

        await DeleteOkAsync("crmx");
        Raw(other0, fromLeadKey).Should().BeNull();
        Raw(partner.GetProperty("id").GetInt64(), fromLeadKey).Should().BeNull("殘留值被清除");
        await _rig.AssertIntegrityAsync();
    }

    [Fact]
    public async Task AnOutsideReferenceToAContributedField_Is409()
    {
        await _rig.ImportAllAsync();
        await _rig.OpenOkAsync("crmx");
        await _rig.OpenOkAsync("stk");
        // 客戶在基本區塊自己加了一個引用 CRM 分數的公式
        await AddField(_rig.Admin, Partner, "formula", new { formula = $"{{{_rig.FieldId("crmx", "bp/partner.score")}}} + 1", resultType = "number" });
        await _rig.CloseOkAsync("crmx");

        var preview = await PreviewAsync("crmx");
        preview.GetProperty("canDelete").GetBoolean().Should().BeFalse();
        preview.GetProperty("blockers").EnumerateArray().Select(b => b.GetProperty("kind").GetString()).Should().Contain("formula");
        _rig.StatusOf("crmx").Should().Be(AppStatus.Closed, "什麼都沒刪");
    }

    /// <summary>在 module 建一個檢查：同一個客戶的筆數不得超過該客戶的 CRM 分數（經參照取主檔上的貢獻欄位）。</summary>
    private async Task AddScoreCheckAsync(string module, string customerField)
    {
        var customer = _rig.FieldId(module.Split('-')[0], customerField);
        var res = await _rig.Admin.PostAsJsonAsync($"{Base}/checks", new
        {
            key = "ck-" + Guid.NewGuid().ToString("N")[..8], name = new Dictionary<string, string> { ["zh-TW"] = "額度" }, moduleKey = module, active = true,
            definition = new
            {
                groupBy = new[] { customer }, function = "count", fieldId = (long?)null,
                assert = new { op = "lte", limit = new { via = customer, field = _rig.FieldId("crmx", "bp/partner.score") } },
                message = new Dictionary<string, string> { ["zh-TW"] = "超過額度" }
            }
        });
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
    }

    /// <summary>審查必修 9：CRM 訂單自己的檢查引用 CRM 貢獻的分數，隨訂單模組一起刪，不算外部引用。</summary>
    [Fact]
    public async Task ItsOwnCheckOnAContributedField_DoesNotBlockTheDelete()
    {
        await _rig.ImportAllAsync();
        await _rig.OpenOkAsync("crmx");
        await _rig.OpenOkAsync("stk");
        await AddScoreCheckAsync("crmx-order", "order.customer");
        await _rig.CloseOkAsync("crmx");

        var preview = await PreviewAsync("crmx");
        preview.GetProperty("blockers").GetArrayLength().Should().Be(0, preview.ToString());
        await DeleteOkAsync("crmx");
        _rig.Db(db => db.Set<ModuleCheck>().Count()).Should().Be(0, "檢查隨訂單模組移除");
        await _rig.AssertIntegrityAsync();
    }

    [Fact]
    public async Task AnOutsideCheckOnAContributedField_Is409()
    {
        await _rig.ImportAllAsync();
        await _rig.OpenOkAsync("crmx");
        await _rig.OpenOkAsync("stk");
        await AddScoreCheckAsync("stk-ship", "ship.customer");
        await _rig.CloseOkAsync("crmx");

        var preview = await PreviewAsync("crmx");
        preview.GetProperty("canDelete").GetBoolean().Should().BeFalse();
        preview.GetProperty("blockers").EnumerateArray().Select(b => b.GetProperty("kind").GetString()).Should().Contain("check");
    }

    /// <summary>直接寫一筆分錄設定（驗證器只收本模組欄位，貢獻欄位走不到 API；這裡只測刪除規劃看到的引用）。</summary>
    private void InsertLedgerOnContributedField(string module, string contributedFieldName)
    {
        var score = _rig.FieldId("crmx", contributedFieldName);
        _rig.Db(db =>
        {
            var moduleId = db.Set<Module>().Single(m => m.Key == module).Id;
            db.Set<ModuleLedger>().Add(new ModuleLedger { ModuleId = moduleId, Definition = new LedgerDefinition(score, [score], []) });
        });
    }

    /// <summary>審查待決 4：帳簿的擁有者模組也在這次刪除內，隨模組一起移除，不算外部引用。</summary>
    [Fact]
    public async Task ItsOwnLedgerOnAContributedField_DoesNotBlockTheDelete()
    {
        await _rig.ImportAllAsync();
        await _rig.OpenOkAsync("crmx");
        await _rig.OpenOkAsync("stk");
        InsertLedgerOnContributedField("crmx-order", "bp/partner.score");
        await _rig.CloseOkAsync("crmx");

        var preview = await PreviewAsync("crmx");
        preview.GetProperty("blockers").GetArrayLength().Should().Be(0, preview.ToString());
        await DeleteOkAsync("crmx");
        _rig.Db(db => db.Set<ModuleLedger>().Count()).Should().Be(0, "帳簿隨訂單模組移除");
    }

    [Fact]
    public async Task AnOutsideLedgerOnAContributedField_Is409()
    {
        await _rig.ImportAllAsync();
        await _rig.OpenOkAsync("crmx");
        await _rig.OpenOkAsync("stk");
        InsertLedgerOnContributedField("stk-ship", "bp/partner.score");
        await _rig.CloseOkAsync("crmx");

        var preview = await PreviewAsync("crmx");
        preview.GetProperty("canDelete").GetBoolean().Should().BeFalse();
        preview.GetProperty("blockers").EnumerateArray().Select(b => b.GetProperty("kind").GetString()).Should().Contain("ledger");
    }

    [Fact]
    public async Task AFromLeadWithFill_DoesNotBlockTheDelete()
    {
        const string plain = """ "key": "lead" } } },""";
        var crm = SharedMasterPackages.Crm.Replace(plain,
            """ "key": "lead" }, "fill": { "mode": "default", "map": [ { "from": "lead.company", "to": "memo" } ] } } },""");
        crm.Should().Contain("\"lead.company\", \"to\"");
        foreach (var json in new[] { SharedMasterPackages.Bp, SharedMasterPackages.Ct, crm }) await _rig.ImportAsync(json);
        await _rig.OpenOkAsync("crmx");
        var fromLead = _rig.FieldId("crmx", "bp/partner.fromLead");
        _rig.Db(db => db.Set<Field>().Single(f => f.Id == fromLead).Config.Fill).Should().NotBeNull();
        var lead = await CreateLeadAsync("甲公司");
        await ConvertAsync(lead);
        await SoftDeleteAsync(lead);
        await _rig.CloseOkAsync("crmx");

        await DeleteOkAsync("crmx");
        FieldExists(fromLead).Should().BeFalse();
        await _rig.AssertIntegrityAsync();
    }

    [Fact]
    public async Task AContributorWithoutValues_LeavesNothing_ItsBlockIsDeleted()
    {
        await _rig.ImportAllAsync();
        await _rig.OpenOkAsync("stk");
        await _rig.OpenOkAsync("mx");
        var website = _rig.FieldId("mx", "bp/partner.website");
        await _rig.CloseOkAsync("mx");
        await DeleteOkAsync("mx");
        FieldExists(website).Should().BeFalse();
        _rig.Blocks(Partner).Select(b => b.Block).Should().Equal("基本資料", "進銷存");
        _rig.Db(db => db.Set<AppOrphan>().Count()).Should().Be(0);
        await _rig.AssertIntegrityAsync();
    }

    [Fact]
    public async Task ReopeningTheSameApp_ReattachesItsOrphans_OldValuesCome_Back_NoSecondField()
    {
        var (partner, industry, _, _) = await ConvertedThenClosedAsync(withStock: true);
        await DeleteOkAsync("crmx");
        var grantsBefore = _rig.Db(db => db.Set<FieldGrant>().Count(g => g.FieldId == industry));
        grantsBefore.Should().Be(0, "貢獻者的可見列隨它刪除");

        await _rig.OpenOkAsync("crmx");
        _rig.FieldId("crmx", "bp/partner.industry").Should().Be(industry, "接回同一個欄位");
        FieldById(industry).Status.Should().Be(FieldStatus.Active);
        _rig.Blocks(Partner).Where(b => b.Block == "CRM").Should().ContainSingle("區塊也接回，沒有第二個 CRM");
        _rig.Blocks(Partner).Single(b => b.Block == "CRM").Fields.Count(f => f.StartsWith("產業:")).Should().Be(1);
        var read = await JsonOf(await _rig.App.AsUser("sales01").GetAsync($"{Base}/records/{partner.GetProperty("id").GetInt64()}"));
        read.GetProperty("values").TryGetProperty(Key("crmx", "bp/partner.industry"), out _).Should().BeTrue("舊值回來，業務重新看得到");
        _rig.Db(db => db.Set<AppOrphan>().Count(o => o.FormerAppKey == "crmx")).Should().Be(0);
        await _rig.AssertIntegrityAsync();
    }

    [Fact]
    public async Task ReopeningWithADifferentType_CreatesANewFieldWithTheOriginalCode_AndCanBeDeletedAgain()
    {
        var (_, industry, _, _) = await ConvertedThenClosedAsync(withStock: true);
        await DeleteOkAsync("crmx");
        // 新版套件把產業改成文字，並多一個欄位 vip（選項也叫 vip）
        _rig.Db(db =>
        {
            var row = db.Set<AppPackage>().Single(p => p.Key == "crmx");
            row.Json = System.Text.RegularExpressions.Regex.Replace(row.Json, """\{ "code": "industry".*?\] \},""",
                """{ "code": "industry", "type": "text", "label": { "zh-TW": "產業" }, "config": {} }, """
                + """{ "code": "vip", "type": "enum", "label": { "zh-TW": "等級" }, "config": {}, "options": [ { "code": "vip", "label": { "zh-TW": "貴賓" } } ] },""",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            row.Json.Should().Contain("\"vip\"");
        });
        var preview = await JsonOf(await _rig.Admin.PostAsJsonAsync($"{SharedMasterRig.Apps}/packages/crmx/preview", _rig.BodyFor("crmx")));
        preview.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()).Should().Contain(w => w!.Contains("型別") && w.Contains("industry"));

        await _rig.OpenOkAsync("crmx");
        var fresh = _rig.FieldId("crmx", "bp/partner.industry");
        fresh.Should().NotBe(industry, "型別不同不接回");
        FieldById(fresh).Type.Should().Be(FieldType.Text);
        FieldById(industry).Status.Should().Be(FieldStatus.Inactive, "舊欄位留成停用");
        _rig.Db(db => db.Set<AppOrphan>().Count(o => o.FormerAppKey == "crmx" && o.Kind == OrphanKind.Field)).Should().Be(0, "舊孤兒列刪除");

        // vip 欄位與 vip 選項都有值，再刪一次：兩列孤兒不撞鍵
        var vip = _rig.FieldId("crmx", "bp/partner.vip");
        var vipOption = _rig.Db(db => db.Set<FieldOption>().Single(o => o.FieldId == vip).Id);
        var partner = await _rig.CreateRecordAsync(_rig.Admin, Partner, new Dictionary<string, object>
            { [Key("bp", "partner.name")] = "乙公司", [Key("crmx", "bp/partner.vip")] = $"opt_{vipOption}", [Key("crmx", "bp/partner.industry")] = "製造" });
        partner.GetProperty("id").GetInt64().Should().BePositive();
        await _rig.CloseOkAsync("crmx");
        await DeleteOkAsync("crmx");
        _rig.Db(db => db.Set<AppOrphan>().Where(o => o.FormerAppKey == "crmx").Select(o => $"{o.Kind}:{o.Name}").ToList())
            .Should().Contain(["Field:bp/partner.vip", "Option:bp/partner.vip", "Field:bp/partner.industry"]);
        await _rig.AssertIntegrityAsync();
    }

    /// <summary>
    /// 審查必修 6：貢獻有整數分數與公式「分數×2」，兩者都有值。刪除後新版把分數改成小數：分數另建新欄位，公式接回舊欄位，
    /// 接回時公式要重綁到新的分數，不能還引用停用的舊分數（否則下次存檔公式值被移除）。
    /// </summary>
    [Fact]
    public async Task Reattaching_RebindsTheFormulaToTheNewFieldWhenADependencyChangedType()
    {
        const string memo = """{ "code": "memo", "type": "text", "label": { "zh-TW": "備忘" }, "config": {} },""";
        var crm = SharedMasterPackages.Crm.Replace(memo, memo
            + """ { "code": "scoreX2", "type": "formula", "label": { "zh-TW": "分數兩倍" }, "config": { "formula": "{score} * 2", "resultType": "number" } },""");
        crm.Should().Contain("scoreX2");
        foreach (var json in new[] { SharedMasterPackages.Bp, SharedMasterPackages.Ct, crm, SharedMasterPackages.Stock }) await _rig.ImportAsync(json);
        await _rig.OpenOkAsync("crmx");
        await _rig.OpenOkAsync("stk");
        var partner = await _rig.CreateRecordAsync(_rig.Admin, Partner, new Dictionary<string, object>
            { [Key("bp", "partner.name")] = "甲公司", [Key("crmx", "bp/partner.score")] = 3 });
        partner.GetProperty("values").GetProperty(Key("crmx", "bp/partner.scoreX2")).GetDecimal().Should().Be(6);
        var oldScore = _rig.FieldId("crmx", "bp/partner.score");
        var scoreX2 = _rig.FieldId("crmx", "bp/partner.scoreX2");
        await _rig.CloseOkAsync("crmx");
        await DeleteOkAsync("crmx");

        _rig.Db(db =>
        {
            var row = db.Set<AppPackage>().Single(p => p.Key == "crmx");
            row.Json = row.Json.Replace("""{ "code": "score", "type": "integer",""", """{ "code": "score", "type": "decimal",""");
            row.Json.Should().Contain("\"score\", \"type\": \"decimal\"");
        });
        await _rig.OpenOkAsync("crmx");

        var newScore = _rig.FieldId("crmx", "bp/partner.score");
        newScore.Should().NotBe(oldScore, "型別不同另建新欄位");
        _rig.FieldId("crmx", "bp/partner.scoreX2").Should().Be(scoreX2, "公式接回");
        FieldById(scoreX2).Config.Formula.Should().Be($"{{{newScore}}} * 2", "接回時重綁到新的分數");
        await UpdateAsync(partner, new() { [Key("crmx", "bp/partner.score")] = 4.5m });
        (await JsonOf(await _rig.Admin.GetAsync($"{Base}/records/{partner.GetProperty("id").GetInt64()}")))
            .GetProperty("values").GetProperty($"f_{scoreX2}").GetDecimal().Should().Be(9);
        await _rig.AssertIntegrityAsync();
    }

    /// <summary>
    /// 審查待決 10：必修 6 的拒絕路徑。公式「分數×2」接回時，新版把分數改成文字，重綁後的公式通不過一般的欄位驗證，
    /// 整個開啟被拒絕，交易回滾：應用程式維持關閉、孤兒與舊欄位原封不動、沒有新欄位或半套設定。
    /// </summary>
    [Fact]
    public async Task Reattaching_WhenTheRebindCannotBeValidated_RejectsTheWholeOpen_AndChangesNothing()
    {
        const string memo = """{ "code": "memo", "type": "text", "label": { "zh-TW": "備忘" }, "config": {} },""";
        var crm = SharedMasterPackages.Crm.Replace(memo, memo
            + """ { "code": "scoreX2", "type": "formula", "label": { "zh-TW": "分數兩倍" }, "config": { "formula": "{score} * 2", "resultType": "number" } },""");
        foreach (var json in new[] { SharedMasterPackages.Bp, SharedMasterPackages.Ct, crm, SharedMasterPackages.Stock }) await _rig.ImportAsync(json);
        await _rig.OpenOkAsync("crmx");
        await _rig.OpenOkAsync("stk");
        await _rig.CreateRecordAsync(_rig.Admin, Partner, new Dictionary<string, object>
            { [Key("bp", "partner.name")] = "甲公司", [Key("crmx", "bp/partner.score")] = 3 });
        var oldScore = _rig.FieldId("crmx", "bp/partner.score");
        var scoreX2 = _rig.FieldId("crmx", "bp/partner.scoreX2");
        await _rig.CloseOkAsync("crmx");
        await DeleteOkAsync("crmx");

        (List<string> Orphans, int Fields, string Formula) Snapshot() => _rig.Db(db => (
            db.Set<AppOrphan>().Where(o => o.FormerAppKey == "crmx").AsEnumerable().Select(o => $"{o.Kind}:{o.Name}:{o.FieldId}:{o.OptionId}:{o.BlockId}").OrderBy(x => x).ToList(),
            db.Set<Field>().Count(),
            db.Set<Field>().Single(f => f.Id == scoreX2).Config.Formula ?? ""));
        var before = Snapshot();
        before.Formula.Should().Be($"{{{oldScore}}} * 2");

        _rig.Db(db =>
        {
            var row = db.Set<AppPackage>().Single(p => p.Key == "crmx");
            row.Json = row.Json.Replace("""{ "code": "score", "type": "integer",""", """{ "code": "score", "type": "text",""");
            row.Json.Should().Contain("\"score\", \"type\": \"text\"");
        });
        var res = await _rig.OpenAsync("crmx");
        res.IsSuccessStatusCode.Should().BeFalse(await res.Content.ReadAsStringAsync());
        res.StatusCode.Should().NotBe(HttpStatusCode.InternalServerError, "是乾淨的驗證拒絕，不是未處理的例外");

        _rig.StatusOf("crmx").Should().BeNull("開啟整個回滾，沒有留下應用程式列（刪除之後本來就沒有）");
        var after = Snapshot();
        after.Orphans.Should().Equal(before.Orphans, "孤兒沒被消耗");
        after.Fields.Should().Be(before.Fields, "沒有新建欄位");
        after.Formula.Should().Be(before.Formula, "公式設定沒被半套改寫");
        FieldById(oldScore).Status.Should().Be(FieldStatus.Inactive);
        await _rig.AssertIntegrityAsync();
    }

    /// <summary>
    /// 審查必修 7：第一版產業是下拉、留下 manufacturing 選項孤兒；第二版產業改成文字，另一個新下拉欄位合法重用 manufacturing。
    /// 型別不符時舊欄位的選項孤兒一併失去接回資格，填值後再刪一次不撞 (FormerAppKey, Kind, Name)。
    /// </summary>
    [Fact]
    public async Task ADifferentType_AlsoRetiresItsOptionOrphans_SoAReusedOptionCodeCanBeOrphanedAgain()
    {
        var (_, industry, _, _) = await ConvertedThenClosedAsync(withStock: true);
        var oldOptions = _rig.Db(db => db.Set<FieldOption>().Where(o => o.FieldId == industry).Select(o => o.Id).ToList());
        await DeleteOkAsync("crmx");
        _rig.Db(db =>
        {
            var row = db.Set<AppPackage>().Single(p => p.Key == "crmx");
            row.Json = System.Text.RegularExpressions.Regex.Replace(row.Json, """\{ "code": "industry".*?\] \},""",
                """{ "code": "industry", "type": "text", "label": { "zh-TW": "產業" }, "config": {} }, """
                + """{ "code": "sector", "type": "enum", "label": { "zh-TW": "行業" }, "config": {}, "options": [ { "code": "manufacturing", "label": { "zh-TW": "製造" } } ] },""",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            row.Json.Should().Contain("\"sector\"");
        });

        await _rig.OpenOkAsync("crmx");
        _rig.Db(db => db.Set<AppOrphan>().Count(o => o.FormerAppKey == "crmx")).Should().Be(0, "舊產業的欄位與選項孤兒都失去接回資格");
        oldOptions.Should().OnlyContain(id => _rig.Db(db => db.Set<FieldOption>().Any(o => o.Id == id)), "舊選項本身保留，資料不動");
        var sector = _rig.FieldId("crmx", "bp/partner.sector");
        var option = _rig.Db(db => db.Set<FieldOption>().Single(o => o.FieldId == sector).Id);
        await _rig.CreateRecordAsync(_rig.Admin, Partner, new Dictionary<string, object>
            { [Key("bp", "partner.name")] = "乙公司", [Key("crmx", "bp/partner.sector")] = $"opt_{option}" });
        await _rig.CloseOkAsync("crmx");

        await DeleteOkAsync("crmx");
        _rig.Db(db => db.Set<AppOrphan>().Where(o => o.FormerAppKey == "crmx" && o.Kind == OrphanKind.Option).Select(o => $"{o.Name}:{o.OptionId}").ToList())
            .Should().Equal($"bp/partner.manufacturing:{option}");
        await _rig.AssertIntegrityAsync();
    }

    /// <summary>
    /// 審查待決 6：第二版整個拿掉產業欄位，新欄位沿用同一個選項代碼。第一版留下的 manufacturing 選項孤兒沒有人接回，
    /// 與新選項同名；不解除資格的話，填值後再刪除會撞 (FormerAppKey, Kind, Name)。
    /// </summary>
    [Fact]
    public async Task AFieldDroppedFromThePackage_RetiresItsOptionOrphans_WhenANewFieldReusesTheOptionCode()
    {
        var (_, industry, _, _) = await ConvertedThenClosedAsync(withStock: true);
        var oldOptions = _rig.Db(db => db.Set<FieldOption>().Where(o => o.FieldId == industry).Select(o => o.Id).ToList());
        await DeleteOkAsync("crmx");
        _rig.Db(db =>
        {
            var row = db.Set<AppPackage>().Single(p => p.Key == "crmx");
            row.Json = System.Text.RegularExpressions.Regex.Replace(row.Json, """\{ "code": "industry".*?\] \},""",
                """{ "code": "sector", "type": "enum", "label": { "zh-TW": "行業" }, "config": {}, "options": [ { "code": "manufacturing", "label": { "zh-TW": "製造" } } ] },""",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            row.Json.Should().Contain("\"sector\"").And.NotContain("\"industry\"");
        });

        await _rig.OpenOkAsync("crmx");
        oldOptions.Should().OnlyContain(id => _rig.Db(db => db.Set<FieldOption>().Any(o => o.Id == id)), "舊選項本身保留，資料不動");
        var sector = _rig.FieldId("crmx", "bp/partner.sector");
        var option = _rig.Db(db => db.Set<FieldOption>().Single(o => o.FieldId == sector).Id);
        await _rig.CreateRecordAsync(_rig.Admin, Partner, new Dictionary<string, object>
            { [Key("bp", "partner.name")] = "乙公司", [Key("crmx", "bp/partner.sector")] = $"opt_{option}" });
        await _rig.CloseOkAsync("crmx");

        await DeleteOkAsync("crmx");
        _rig.Db(db => db.Set<AppOrphan>().Where(o => o.FormerAppKey == "crmx" && o.Kind == OrphanKind.Option).Select(o => $"{o.Name}:{o.OptionId}").ToList())
            .Should().Contain($"bp/partner.manufacturing:{option}").And.OnlyContain(x => !oldOptions.Any(id => x.EndsWith($":{id}") && x.Contains("manufacturing")),
                "同名的舊選項孤兒已失去資格，不重複；其他沒撞名的舊選項孤兒照舊可接回");
        await _rig.AssertIntegrityAsync();
    }

    /// <summary>
    /// 第三輪 6：想重現的情境是「孤兒列還在、欄位實體已被刪」。實測做不出來：孤兒表對欄位、選項、區塊都有外鍵連帶刪除，
    /// 設計器把停用的孤兒欄位清掉時，孤兒列（與選項孤兒列）一起消失，所以不存在懸空的孤兒列，也不會撞唯一索引。
    /// 這個測試把這件事釘住：清掉孤兒欄位後重開、填值、再刪除都順利，不留舊孤兒。
    /// </summary>
    [Fact]
    public async Task DeletingAnOrphanedFieldInTheDesigner_TakesItsOrphanRowsWithIt_SoTheSameCodeCanBeOrphanedAgain()
    {
        var (partnerRecord, industry, _, _) = await ConvertedThenClosedAsync(withStock: true);
        await DeleteOkAsync("crmx");
        _rig.Db(db => db.Set<AppOrphan>().Count(o => o.FormerAppKey == "crmx" && o.Kind == OrphanKind.Field && o.FieldId == industry)).Should().Be(1);
        await SoftDeleteAsync(partnerRecord); // 欄位有資料時設計器不讓刪；記錄刪除後才清得掉

        var check = await JsonOf(await _rig.Admin.GetAsync($"{Base}/fields/{industry}/delete-check"));
        var del = await _rig.Admin.DeleteAsync($"{Base}/fields/{industry}?purged={check.GetProperty("purge").GetProperty("records").GetInt32()}");
        del.StatusCode.Should().Be(HttpStatusCode.OK, await del.Content.ReadAsStringAsync());
        FieldExists(industry).Should().BeFalse();
        _rig.Db(db => db.Set<AppOrphan>().Count(o => o.FormerAppKey == "crmx" && o.Kind != OrphanKind.Block))
            .Should().Be(0, "欄位與它的選項孤兒列隨外鍵連帶刪除，沒有懸空的孤兒列");

        await _rig.OpenOkAsync("crmx");
        var newIndustry = _rig.FieldId("crmx", "bp/partner.industry");
        newIndustry.Should().NotBe(industry);
        var option = _rig.Db(db => db.Set<FieldOption>().First(o => o.FieldId == newIndustry).Id);
        await _rig.CreateRecordAsync(_rig.Admin, Partner, new Dictionary<string, object>
            { [Key("bp", "partner.name")] = "乙公司", [Key("crmx", "bp/partner.industry")] = $"opt_{option}" });
        await _rig.CloseOkAsync("crmx");

        await DeleteOkAsync("crmx");
        _rig.Db(db => db.Set<AppOrphan>().Where(o => o.FormerAppKey == "crmx" && o.Kind == OrphanKind.Field).Select(o => o.FieldId).ToList())
            .Should().Contain(newIndustry).And.NotContain(industry);
        await _rig.AssertIntegrityAsync();
    }

    [Fact]
    public async Task AnOrphanReEnabledInTheDesigner_StaysRestricted_UntilTheAdminGrantsIt()
    {
        await _rig.ImportAllAsync();
        await _rig.OpenOkAsync("crmx");
        await _rig.OpenOkAsync("stk");
        var keeper = _rig.App.AsUser("keeper01");
        var record = await _rig.CreateRecordAsync(keeper, Partner, new Dictionary<string, object> { [Key("bp", "partner.name")] = "丙公司" });
        var industry = _rig.FieldId("crmx", "bp/partner.industry");
        var option = _rig.Db(db => db.Set<FieldOption>().First(o => o.FieldId == industry).Id);
        await UpdateAsync(record, new() { [$"f_{industry}"] = $"opt_{option}" });
        await _rig.CloseOkAsync("crmx");
        await DeleteOkAsync("crmx");

        (await _rig.Admin.PutAsJsonAsync($"{Base}/fields/{industry}/status", new { status = "Active" })).StatusCode.Should().Be(HttpStatusCode.OK);
        FieldById(industry).VisibilityRestricted.Should().BeTrue();
        async Task<bool> KeeperSeesIt() => (await JsonOf(await keeper.GetAsync($"{Base}/records/{record.GetProperty("id").GetInt64()}")))
            .GetProperty("values").TryGetProperty($"f_{industry}", out _);
        (await KeeperSeesIt()).Should().BeFalse("只剩系統管理員看得到");

        var perms = JsonNode.Parse(await (await _rig.Admin.GetAsync($"{Base}/modules/{Partner}/permissions")).Content.ReadAsStringAsync())!;
        perms["fieldGrants"] = new JsonArray(new JsonObject { ["fieldId"] = industry, ["groupIds"] = new JsonArray(_rig.GroupId("warehouse")) });
        var saved = await _rig.Admin.PutAsync($"{Base}/modules/{Partner}/permissions",
            new StringContent(perms.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));
        saved.StatusCode.Should().Be(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync());
        (await KeeperSeesIt()).Should().BeTrue("管理員另外授予後看得到");
        await _rig.AssertIntegrityAsync();
    }

    [Fact]
    public async Task AMasterCanBeDeletedOnlyWhenNothingUsesIt()
    {
        await _rig.ImportAllAsync();
        await _rig.OpenOkAsync("mx");
        await _rig.CloseOkAsync("mx");
        var inUse = await PreviewAsync("bp");
        inUse.GetProperty("blockers").EnumerateArray().Select(b => b.GetProperty("kind").GetString()).Should().Contain("masterInUse");

        await DeleteOkAsync("mx");
        _rig.StatusOf("bp").Should().Be(AppStatus.Closed);
        await DeleteOkAsync("bp");
        _rig.StatusOf("bp").Should().BeNull();
        _rig.Db(db => db.Set<Module>().Any(m => m.Key == Partner)).Should().BeFalse();
        await _rig.AssertIntegrityAsync();
    }

    /// <summary>轉換的固定值（header.set）也是對目標欄位的引用：沒有資料的固定值欄位，安全刪除要被轉換擋下。</summary>
    [Fact]
    public async Task AFieldUsedByAConversionFixedValue_IsBlockedFromSafeDelete()
    {
        await _rig.ImportAsync(AppTestPackagesV6.LocalSet);
        await _rig.OpenOkAsync("set-lite", new { groupMap = new Dictionary<string, object>() });
        var role = _rig.FieldId("set-lite", "customer.role");

        var check = await JsonOf(await _rig.Admin.GetAsync($"{Base}/fields/{role}/delete-check"));
        check.GetProperty("blockers").EnumerateArray().Select(b => b.GetProperty("kind").GetString()).Should().Contain("conversion");
    }
}
