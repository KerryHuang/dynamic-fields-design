using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 主檔匯入走合併（設計 §9）：還沒開啟時合併定義、已開啟時依持久代號只補不刪不覆寫、撞到貢獻代號避撞並警告；
/// 匯出到空庫後兩種開啟順序都成功、結果和原庫相同；A→B→A 與主檔匯出再匯入都不產生重複；孤兒不跟著走；被刪的基本欄位讓使用者套件 unbound。
/// </summary>
public class SharedMasterMergeTests : IDisposable
{
    private const string Partner = "master-bp-partner";
    private readonly SharedMasterRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private static JsonObject Node(string json) => JsonNode.Parse(json)!.AsObject();

    private static JsonObject Field(string code, string type, string label, JsonObject? config = null, params (string Code, string Label)[] options)
    {
        var f = new JsonObject
        {
            ["code"] = code, ["type"] = type, ["label"] = new JsonObject { ["zh-TW"] = label }, ["config"] = config ?? new JsonObject()
        };
        if (options.Length > 0)
            f["options"] = new JsonArray(options.Select(o => (JsonNode)new JsonObject { ["code"] = o.Code, ["label"] = new JsonObject { ["zh-TW"] = o.Label } }).ToArray());
        return f;
    }

    private static JsonObject Module(JsonObject package) => package["modules"]!.AsArray()[0]!.AsObject();
    private static JsonArray MainFields(JsonObject package) => Module(package)["blocks"]!.AsArray()[0]!["fields"]!.AsArray();
    private static JsonObject FieldNode(JsonObject package, string code) =>
        MainFields(package).Single(f => f!["code"]!.GetValue<string>() == code)!.AsObject();

    private static void AddOption(JsonObject field, string code, string label) =>
        field["options"]!.AsArray().Add(new JsonObject { ["code"] = code, ["label"] = new JsonObject { ["zh-TW"] = label } });

    private static List<string> Names(SharedMasterRig rig, string appKey, Func<DynamicModules.Domain.Data.DynamicDbContext, long, IEnumerable<string>> names) =>
        rig.Db(db => names(db, db.Set<App>().Single(a => a.Key == appKey).Id).Order(StringComparer.Ordinal).ToList());

    private static List<string> FieldNames(SharedMasterRig rig, string appKey) =>
        Names(rig, appKey, (db, id) => db.Set<AppField>().Where(f => f.AppId == id).Select(f => f.Name).ToList());

    private static List<string> OptionNames(SharedMasterRig rig, string appKey) =>
        Names(rig, appKey, (db, id) => db.Set<AppOption>().Where(f => f.AppId == id).Select(f => f.Name).ToList());

    private static (int Fields, int Options, int Blocks) Counts(SharedMasterRig rig) => rig.Db(db =>
    {
        var module = db.Set<Module>().Single(m => m.Key == Partner);
        var blocks = db.Set<Block>().Where(b => b.ModuleId == module.Id).Select(b => b.Id).ToList();
        var fields = db.Set<Field>().Where(f => blocks.Contains(f.BlockId)).Select(f => f.Id).ToList();
        return (fields.Count, db.Set<FieldOption>().Count(o => fields.Contains(o.FieldId)), blocks.Count);
    });

    private static Field DbField(SharedMasterRig rig, string appKey, string name)
    {
        var id = rig.FieldId(appKey, name);
        return rig.Db(db => db.Set<Field>().Single(f => f.Id == id));
    }

    [Fact]
    public async Task ReimportingAMasterNobodyOpened_MergesTheDefinition_AndItsFirstUserOpensTheMergedOne()
    {
        await _rig.ImportAllAsync();
        var bp = Node(SharedMasterPackages.Bp);
        MainFields(bp).Add(Field("grade", "enum", "等級", null, ("gold", "金牌")));
        AddOption(FieldNode(bp, "role"), "agent", "代理商");
        FieldNode(bp, "phone")["type"] = "text";
        MainFields(bp).Remove(FieldNode(bp, "headcount"));
        Module(bp)["blocks"]!.AsArray().Add(new JsonObject
        {
            ["code"] = "extra", ["label"] = new JsonObject { ["zh-TW"] = "其他" }, ["fields"] = new JsonArray(Field("site", "text", "網站"))
        });

        var warnings = await _rig.ImportMergeOkAsync(bp.ToJsonString());
        warnings.Should().Contain(w => w.Contains("電話") && w.Contains("型別"), string.Join("\n", warnings));
        _rig.Db(db => db.Set<AppPackage>().Count(p => p.Key == "bp")).Should().Be(1, "合併後仍只有一份 bp");

        await _rig.OpenOkAsync("crmx");
        FieldNames(_rig, "bp").Should().Contain(["partner.grade", "partner.site", "partner.headcount", "partner.phone"]);
        OptionNames(_rig, "bp").Should().Contain(["partner.gold", "partner.agent", "partner.customer"]);
        DbField(_rig, "bp", "partner.phone").Type.Should().Be(FieldType.Phone, "代號相同但型別不同的保留既有的");
        _rig.Blocks(Partner).Select(b => b.Block).Should().Equal("基本資料", "其他", "CRM");
        await _rig.AssertIntegrityAsync();
    }

    [Fact]
    public async Task ReimportingAMasterAlreadyOpen_OnlyAddsByPersistentCode_AvoidsContributorCodes_AndNeverDeletesOrOverwrites()
    {
        await _rig.ImportAllAsync();
        await _rig.OpenOkAsync("crmx");
        await _rig.OpenOkAsync("stk");
        var (exported, _) = await _rig.ExportAsync("bp");
        var bp = Node(exported.GetRawText());
        MainFields(bp).Add(Field("grade", "enum", "等級", null, ("gold", "金牌")));
        MainFields(bp).Add(Field("industry", "text", "行業別"));
        AddOption(FieldNode(bp, "role"), "agent", "代理商");
        AddOption(FieldNode(bp, "role"), "retail", "零售通路");
        FieldNode(bp, "phone")["type"] = "text";
        FieldNode(bp, "phone")["label"] = new JsonObject { ["zh-TW"] = "市話" };
        FieldNode(bp, "name")["active"] = false;
        MainFields(bp).Remove(FieldNode(bp, "headcount"));
        Module(bp)["blocks"]!.AsArray().Add(new JsonObject
        {
            ["code"] = "extra", ["label"] = new JsonObject { ["zh-TW"] = "其他" },
            ["fields"] = new JsonArray(Field("site", "text", "網站", new JsonObject { ["required"] = true }))
        });

        var warnings = await _rig.ImportMergeOkAsync(bp.ToJsonString());
        string All() => string.Join("\n", warnings);
        warnings.Should().Contain(w => w.Contains("電話") && w.Contains("型別"), All());
        warnings.Should().Contain(w => w.Contains("industry_2"), All());
        warnings.Should().Contain(w => w.Contains("retail_2"), All());
        warnings.Should().Contain(w => w.Contains("網站") && w.Contains("必填"), All());

        FieldNames(_rig, "bp").Should().Contain(["partner.grade", "partner.industry_2", "partner.site", "partner.headcount"]);
        OptionNames(_rig, "bp").Should().Contain(["partner.gold", "partner.agent", "partner.retail_2"]);
        FieldNames(_rig, "crmx").Should().Contain("bp/partner.industry", "貢獻者的代號不受影響");
        var phone = DbField(_rig, "bp", "partner.phone");
        (phone.Type, phone.LabelI18n.Resolve("zh-TW")).Should().Be((FieldType.Phone, "電話"), "既有的不覆寫");
        DbField(_rig, "bp", "partner.name").Status.Should().Be(FieldStatus.Active, "不改既有欄位的啟用狀態");
        DbField(_rig, "bp", "partner.headcount").Status.Should().Be(FieldStatus.Active, "匯入檔少了的不刪");
        DbField(_rig, "bp", "partner.site").Config.Required.Should().BeFalse("已有資料的模組不能新增必填欄位");
        _rig.Blocks(Partner).Select(b => b.Block).Should().Equal(["基本資料", "其他", "CRM", "進銷存"], "補上的區塊是基本區塊，排在貢獻區塊前面");
        await _rig.AssertIntegrityAsync();

        // 主檔匯出再匯入同一個庫：不產生重複
        var before = Counts(_rig);
        var (again, _) = await _rig.ExportAsync("bp");
        await _rig.ImportMergeOkAsync(again.GetRawText());
        Counts(_rig).Should().Be(before);

        // 匯入的那份也更新成合併結果
        var summary = await JsonOf(await _rig.Admin.GetAsync($"{SharedMasterRig.Apps}/packages/bp"));
        summary.ToString().Should().Contain("grade").And.Contain("industry_2");
    }

    /// <summary>庫 A：兩個使用者、客戶改過主檔（加基本欄位與 role 選項、停用一個基本欄位、在 CRM 區塊加欄位）。</summary>
    private static async Task<SharedMasterRig> CustomizedAsync()
    {
        var a = new SharedMasterRig();
        await a.ImportAllAsync();
        await a.OpenOkAsync("crmx");
        await a.OpenOkAsync("stk");
        var grade = await a.AddDesignerFieldAsync(Partner, a.BlockId("bp", "partner.main"), "enum", "等級");
        await a.AddDesignerOptionAsync(grade, "金牌");
        await a.AddDesignerOptionAsync(a.FieldId("bp", "partner.role"), "代理商");
        await a.SetFieldStatusOkAsync(a.FieldId("bp", "partner.no"), "Inactive");
        await a.AddDesignerFieldAsync(Partner, a.BlockId("crmx", "bp/partner.crm"), "text", "拜訪紀錄");
        return a;
    }

    /// <summary>
    /// 匯出檔的群組代號取自群組 Key（主檔的 steward 開啟時新建，Key 是自動產生的），所以在庫 B 開啟時照匯出檔的代號對應：
    /// 使用者套件的代號是既有群組的 Key 就對應過去、否則新建；主檔的群組一律新建。
    /// </summary>
    private static object BodyFrom(SharedMasterRig rig, Dictionary<string, JsonElement> exports, string key, string userJson)
    {
        var groupMap = new Dictionary<string, object>();
        foreach (var g in JsonDocument.Parse(userJson).RootElement.GetProperty("groups").EnumerateArray())
        {
            var code = g.GetProperty("code").GetString()!;
            var id = rig.Db(db => db.Set<PermissionGroup>().Where(x => x.Key == code).Select(x => (long?)x.Id).FirstOrDefault());
            groupMap[code] = id is { } existing ? new { mode = "map", groupId = existing } : new { mode = "create" };
        }
        var masters = new Dictionary<string, object>();
        foreach (var (id, package) in exports.Where(e => e.Value.GetProperty("manifest").TryGetProperty("kind", out var k) && k.GetString() == "master"))
            masters[id] = package.GetProperty("groups").EnumerateArray()
                .ToDictionary(g => g.GetProperty("code").GetString()!, _ => (object)new { mode = "create" });
        return new { groupMap, masters };
    }

    private static string Section(JsonElement package, params string[] names) =>
        string.Join("\n", names.Select(n => package.TryGetProperty(n, out var v) ? v.GetRawText() : ""));

    [Theory]
    [InlineData("crmx", "stk")]
    [InlineData("stk", "crmx")]
    public async Task RoundTrip_ToAnEmptyDatabase_OpensInEitherOrder_WithTheSameMasterAndUsers(string first, string second)
    {
        using var a = await CustomizedAsync();
        var exports = new Dictionary<string, JsonElement>();
        foreach (var key in new[] { "bp", "ct", "crmx", "stk" }) exports[key] = (await a.ExportAsync(key)).Package;

        using var b = new SharedMasterRig();
        foreach (var key in new[] { "bp", "ct", "crmx", "stk" }) await b.ImportAsync(exports[key].GetRawText());
        await b.OpenOkAsync(first, BodyFrom(b, exports, first, exports[first].GetRawText()));
        await b.OpenOkAsync(second, BodyFrom(b, exports, second, exports[second].GetRawText()));

        b.Blocks(Partner).Should().BeEquivalentTo(a.Blocks(Partner), o => o.WithStrictOrdering(), "主檔的欄位、選項、停用狀態與區塊順序都相同");
        OptionNames(b, "bp").Should().Equal(OptionNames(a, "bp"));
        FieldNames(b, "bp").Should().Equal(FieldNames(a, "bp"));
        foreach (var key in new[] { "crmx", "stk" })
        {
            FieldNames(b, key).Should().Equal(FieldNames(a, key));
            var (again, _) = await b.ExportAsync(key);
            Section(again, "manifest", "groups", "modules", "conversions", "contributions")
                .Should().Be(Section(exports[key], "manifest", "groups", "modules", "conversions", "contributions"), $"{key} 的引用、轉換、權限與貢獻都相同");
        }
        var (bpAgain, _) = await b.ExportAsync("bp");
        Module(Node(bpAgain.GetRawText()))["blocks"]!.ToJsonString()
            .Should().Be(Module(Node(exports["bp"].GetRawText()))["blocks"]!.ToJsonString());
        await b.AssertIntegrityAsync();

        // A→B→A：B 的主檔匯回 A，什麼都不多
        var before = Counts(a);
        await a.ImportMergeOkAsync(bpAgain.GetRawText());
        Counts(a).Should().Be(before);
        await a.AssertIntegrityAsync();
    }

    [Fact]
    public async Task AnOrphanedField_IsNotExported_AndTheImportedMasterHasNoSuchField()
    {
        await _rig.ImportAllAsync();
        await _rig.OpenOkAsync("crmx");
        var industry = _rig.FieldId("crmx", "bp/partner.industry");
        var option = _rig.Db(db => db.Set<FieldOption>().First(o => o.FieldId == industry).Id);
        await _rig.CreateRecordAsync(_rig.Admin, Partner, new Dictionary<string, object>
        {
            [_rig.FieldKey("bp", "partner.name")] = "甲公司", [$"f_{industry}"] = $"opt_{option}"
        });
        await _rig.CloseOkAsync("crmx");
        await _rig.DeleteAppOkAsync("crmx");
        var (bp, warnings) = await _rig.ExportAsync("bp");
        warnings.Should().Contain(w => w.Contains("產業"));

        using var b = new SharedMasterRig();
        await b.ImportAsync(bp.GetRawText());
        await b.ImportAsync(SharedMasterPackages.Stock);
        await b.OpenOkAsync("stk", BodyFrom(b, new() { ["bp"] = bp }, "stk", SharedMasterPackages.Stock));
        b.Blocks(Partner).SelectMany(x => x.Fields).Should().NotContain(f => f.StartsWith("產業"));
    }

    [Fact]
    public async Task ADeletedBaseField_IsMissingFromTheExport_AndAUserThatNeedsItIsUnbound()
    {
        await _rig.ImportAllAsync();
        await _rig.OpenOkAsync("mx");
        var phone = _rig.FieldId("bp", "partner.phone");
        (await _rig.Admin.PutAsJsonAsync($"{Base}/fields/{phone}/status", new { status = "Inactive" })).EnsureSuccessStatusCode();
        var check = await JsonOf(await _rig.Admin.GetAsync($"{Base}/fields/{phone}/delete-check"));
        var del = await _rig.Admin.DeleteAsync($"{Base}/fields/{phone}?purged={check.GetProperty("purge").GetProperty("records").GetInt32()}");
        del.IsSuccessStatusCode.Should().BeTrue(await del.Content.ReadAsStringAsync());
        var (bp, _) = await _rig.ExportAsync("bp");
        bp.GetRawText().Should().NotContain("\"phone\"");

        // 主檔先匯入的話，匯入 stk 時就會因引用不存在的基本欄位被擋（§4.6）；先匯入 stk、之後才來主檔，開啟時才 unbound
        using var b = new SharedMasterRig();
        await b.ImportAsync(SharedMasterPackages.Stock);
        await b.ImportAsync(bp.GetRawText());
        var res = await b.OpenAsync("stk", BodyFrom(b, new() { ["bp"] = bp }, "stk", SharedMasterPackages.Stock));
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.Conflict, body);
        body.Should().Contain("unbound").And.Contain("bp/partner.phone");
    }

    [Fact]
    public async Task ImportingAnAppOverAMasterId_OrAMasterOverAnAppId_IsStill409()
    {
        await _rig.ImportAllAsync();
        var crm = Node("""
            { "format": "formwright-app", "formatVersion": 6, "manifest": { "id": "bp", "version": "1.0.0", "name": { "zh-TW": "假的" } },
              "modules": [ { "code": "note", "key": "fake-bp-note", "name": { "zh-TW": "便條" }, "kind": "standalone", "titleField": "title",
                "blocks": [ { "code": "main", "label": { "zh-TW": "基本" }, "fields": [
                  { "code": "title", "type": "text", "label": { "zh-TW": "標題" }, "config": {} } ] } ] } ] }
            """);
        var res = await _rig.Admin.PostAsync($"{SharedMasterRig.Apps}/packages/import",
            new StringContent(crm.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));
        res.StatusCode.Should().Be(HttpStatusCode.Conflict, await res.Content.ReadAsStringAsync());
        var bp = Node(SharedMasterPackages.Bp);
        bp["manifest"]!["id"] = "stk";
        res = await _rig.Admin.PostAsync($"{SharedMasterRig.Apps}/packages/import",
            new StringContent(bp.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));
        res.StatusCode.Should().Be(HttpStatusCode.Conflict, await res.Content.ReadAsStringAsync());
    }
}
