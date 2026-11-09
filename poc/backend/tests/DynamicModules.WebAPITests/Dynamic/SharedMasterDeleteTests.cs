using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
}
