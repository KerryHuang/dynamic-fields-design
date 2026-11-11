using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 套件格式 7 的 taskRules 隨應用程式匯出、開啟、關閉與刪除（任務管理設計 §5.8）。
/// 關閉與重開應用程式時規則是否觸發，含端到端斷言（建記錄產生任務、關閉後不再產生）。
/// </summary>
public class AppFormat7Tests : IDisposable
{
    private const string LeadKey = "crmx-lead", PartnerKey = "master-bp-partner";
    private const string LeadRule = "crmx-lead-follow", PartnerRule = "crmx-partner-visit";

    private readonly SharedMasterRig _rig = new();

    public void Dispose() => _rig.Dispose();

    /// <summary>CRM 測試套件改成格式 7：潛在客戶加上負責業務、預計成交、階段，貢獻加上客戶經理，再加兩條任務規則。</summary>
    private static string Crm7()
    {
        var root = JsonNode.Parse(SharedMasterPackages.Crm)!.AsObject();
        root["formatVersion"] = 7;
        var lead = root["modules"]!.AsArray().Single(m => m!["code"]!.GetValue<string>() == "lead")!;
        var leadFields = lead["blocks"]![0]!["fields"]!.AsArray();
        foreach (var f in Nodes("""
            [ { "code": "owner", "type": "user", "label": { "zh-TW": "負責業務" }, "config": {} },
              { "code": "due", "type": "date", "label": { "zh-TW": "預計成交" }, "config": {} },
              { "code": "stage", "type": "enum", "label": { "zh-TW": "階段" }, "config": {},
                "options": [ { "code": "hot", "label": { "zh-TW": "熱" } }, { "code": "cold", "label": { "zh-TW": "冷" } } ] } ]
            """)) leadFields.Add(f);
        root["contributions"]![0]!["fields"]!.AsArray()
            .Add(JsonNode.Parse("""{ "code": "accountMgr", "type": "user", "label": { "zh-TW": "客戶經理" }, "config": {} }"""));
        root["taskRules"] = JsonNode.Parse("""
            [
              { "code": "crmx-lead-follow", "name": { "zh-TW": "熱名單跟進", "en-US": "Hot lead follow-up" }, "active": true, "module": "lead",
                "repeat": "everyTime",
                "trigger": { "on": "onFieldBecomes", "field": "lead.stage", "value": "opt_hot" },
                "when": { "all": [ { "field": "lead.stage", "op": "ne", "value": "opt_cold" } ] },
                "tasks": [ { "code": "call", "title": { "zh-TW": "打電話", "en-US": "Call" }, "description": { "zh-TW": "先確認需求" },
                             "assignee": "lead.owner", "due": { "field": "lead.due", "offsetDays": -1 } } ] },
              { "code": "crmx-partner-visit", "name": { "zh-TW": "拜訪客戶" }, "active": true, "module": "bp/partner",
                "trigger": { "on": "manual" },
                "tasks": [ { "code": "visit", "title": { "zh-TW": "拜訪" }, "assignee": "bp/partner.accountMgr" } ] }
            ]
            """);
        return root.ToJsonString();

        static List<JsonNode> Nodes(string json) => JsonNode.Parse(json)!.AsArray().Select(n => n!.DeepClone()).ToList();
    }

    private async Task OpenCrm7Async()
    {
        foreach (var json in SharedMasterPackages.Masters) await _rig.ImportAsync(json);
        await _rig.ImportAsync(Crm7());
        await _rig.OpenOkAsync("crmx");
    }

    private async Task<List<JsonElement>> RulesAsync(string moduleKey)
    {
        var res = await _rig.Admin.GetAsync($"{Base}/modules/{moduleKey}/task-rules");
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return (await JsonOf(res)).EnumerateArray().ToList();
    }

    private List<string> ActiveRuleKeys(string moduleKey)
    {
        using var scope = _rig.App.Services.CreateScope();
        var moduleId = _rig.Db(db => db.Set<Module>().Single(m => m.Key == moduleKey).Id);
        return scope.ServiceProvider.GetRequiredService<ITaskRuleRepository>().ListActiveByModuleAsync(moduleId, CancellationToken.None)
            .GetAwaiter().GetResult().Select(r => r.Key).ToList();
    }

    /// <summary>建一筆階段為「熱」的潛在客戶（觸發熱名單跟進規則）；回傳 HTTP 回應。</summary>
    private Task<HttpResponseMessage> PostHotLeadAsync(string company)
    {
        var hot = _rig.Db(db => db.Set<AppOption>().Single(o => o.Name == "lead.hot").OptionId);
        return _rig.Admin.PostAsJsonAsync($"{Base}/modules/{LeadKey}/records", new
        {
            values = new Dictionary<string, object>
            {
                [_rig.FieldKey("crmx", "lead.company")] = company,
                [_rig.FieldKey("crmx", "lead.owner")] = "sales02",
                [_rig.FieldKey("crmx", "lead.stage")] = $"opt_{hot}"
            }
        });
    }

    private int LeadRuleTaskCount() => _rig.Db(db =>
    {
        var ruleId = db.Set<TaskRule>().Single(r => r.Key == LeadRule).Id;
        return db.Set<TaskItem>().Count(t => t.RuleId == ruleId);
    });

    private async Task CreateManualRuleAsync(string key, long assigneeFieldId, string moduleKey = PartnerKey)
    {
        var res = await _rig.Admin.PostAsJsonAsync($"{Base}/modules/{moduleKey}/task-rules", new
        {
            key, name = new Dictionary<string, string> { ["zh-TW"] = "手動規則" }, active = true, repeat = "once",
            trigger = new { on = "manual" },
            tasks = new[] { new { code = "check", title = new Dictionary<string, string> { ["zh-TW"] = "確認" }, assigneeFieldId, dueOffsetDays = 0 } }
        });
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync() + _rig.LastError());
    }

    [Fact]
    public async Task OpenApp_RuleHasAppId()
    {
        await OpenCrm7Async();
        var appId = _rig.Db(db => db.Set<App>().Single(a => a.Key == "crmx").Id);
        _rig.Db(db => db.Set<TaskRule>().Where(r => r.Key == LeadRule || r.Key == PartnerRule).Select(r => r.AppId).ToList())
            .Should().Equal(appId, appId);

        var lead = (await RulesAsync(LeadKey)).Single();
        lead.GetProperty("key").GetString().Should().Be(LeadRule);
        lead.GetProperty("appKey").GetString().Should().Be("crmx");
        lead.GetProperty("repeat").GetString().Should().Be("everyTime");
        var hot = _rig.Db(db => db.Set<AppOption>().Single(o => o.Name == "lead.hot").OptionId);
        lead.GetProperty("trigger").GetProperty("value").GetString().Should().Be($"opt_{hot}");
        var task = lead.GetProperty("tasks").EnumerateArray().Single();
        task.GetProperty("assigneeFieldId").GetInt64().Should().Be(_rig.FieldId("crmx", "lead.owner"));
        task.GetProperty("dueFieldId").GetInt64().Should().Be(_rig.FieldId("crmx", "lead.due"));
        task.GetProperty("dueOffsetDays").GetInt32().Should().Be(-1);
        task.GetProperty("title").GetProperty("en-US").GetString().Should().Be("Call");

        var partner = (await RulesAsync(PartnerKey)).Single();
        partner.GetProperty("appKey").GetString().Should().Be("crmx", "掛在共用主檔上的規則也記它屬於哪個應用程式");
        partner.GetProperty("tasks")[0].GetProperty("assigneeFieldId").GetInt64().Should().Be(_rig.FieldId("crmx", "bp/partner.accountMgr"));
    }

    [Fact]
    public async Task OpenApp_RuleFails_RollsBackEverything()
    {
        foreach (var json in SharedMasterPackages.Masters) await _rig.ImportAsync(json);
        await _rig.ImportAsync(Crm7());
        // 先在主檔上手動建一條同代號的規則，讓開啟時第二條規則撞到 409
        var other = await CreateModule(_rig.Admin);
        var owner = await AddField(_rig.Admin, other, "user");
        await CreateManualRuleAsync(PartnerRule, owner.Id, other);

        var res = await _rig.OpenAsync("crmx");
        ((int)res.StatusCode).Should().BeInRange(400, 499, await res.Content.ReadAsStringAsync());
        _rig.Db(db => db.Set<App>().Any(a => a.Key == "crmx")).Should().BeFalse("App 列回滾");
        _rig.Db(db => db.Set<Module>().Any(m => m.Key == LeadKey)).Should().BeFalse("本套件的模組回滾");
        _rig.Db(db => db.Set<TaskRule>().Any(r => r.Key == LeadRule)).Should().BeFalse("先建好的規則回滾");
        _rig.Db(db => db.Set<TaskRule>().Count(r => r.Key == PartnerRule)).Should().Be(1, "只剩原本手動建的那一條");
    }

    [Fact]
    public async Task OpenApp_ExcludedField_SkipsRuleWithWarning()
    {
        foreach (var json in SharedMasterPackages.Masters) await _rig.ImportAsync(json);
        await _rig.ImportAsync(Crm7());
        var body = JsonSerializer.SerializeToNode(_rig.BodyFor("crmx"))!.AsObject();
        body["excludeFields"] = new JsonArray("lead.due");
        var opened = await _rig.OpenOkAsync("crmx", body);
        opened.GetProperty("warnings").EnumerateArray().Select(w => w.GetString())
            .Should().Contain("任務規則「crmx-lead-follow」引用了被排除的欄位，已略過");
        (await RulesAsync(LeadKey)).Should().BeEmpty();
        (await RulesAsync(PartnerKey)).Should().ContainSingle();
    }

    [Fact]
    public async Task TaskRules_ExportThenImport_RoundTrip()
    {
        await OpenCrm7Async();
        var (package, warnings) = await _rig.ExportAsync("crmx");
        warnings.Should().NotContain(w => w.Contains("任務規則"));
        package.GetProperty("formatVersion").GetInt32().Should().Be(7);
        var exported = package.GetProperty("taskRules").EnumerateArray().ToList();
        exported.Select(r => r.GetProperty("code").GetString()).Should().Equal(LeadRule, PartnerRule);
        var lead = exported[0];
        lead.GetProperty("module").GetString().Should().Be("lead");
        lead.GetProperty("repeat").GetString().Should().Be("everyTime");
        lead.GetProperty("trigger").GetProperty("value").GetString().Should().Be("opt_hot");
        lead.GetProperty("when").GetRawText().Should().Contain("\"lead.stage\"").And.Contain("opt_cold");
        lead.GetProperty("tasks")[0].GetProperty("due").GetProperty("field").GetString().Should().Be("lead.due");
        exported[1].TryGetProperty("repeat", out _).Should().BeFalse("once 是預設值，匯出省略");

        // 換一個資料庫：匯出的檔案讀得回來，規則內容不變（Id 換成新庫的）
        using var other = new SharedMasterRig();
        foreach (var json in SharedMasterPackages.Masters) await other.ImportAsync(json);
        await other.ImportAsync(package.GetRawText());
        await other.OpenOkAsync("crmx");
        var res = await other.Admin.GetAsync($"{Base}/modules/{LeadKey}/task-rules");
        var reopened = (await JsonOf(res)).EnumerateArray().Single();
        var original = (await RulesAsync(LeadKey)).Single();
        foreach (var name in new[] { "key", "name", "repeat", "active", "appKey" })
            reopened.GetProperty(name).GetRawText().Should().Be(original.GetProperty(name).GetRawText(), name);
        var hot = other.Db(db => db.Set<AppOption>().Single(o => o.Name == "lead.hot").OptionId);
        reopened.GetProperty("trigger").GetProperty("value").GetString().Should().Be($"opt_{hot}");
        var task = reopened.GetProperty("tasks")[0];
        task.GetProperty("assigneeFieldId").GetInt64().Should().Be(other.FieldId("crmx", "lead.owner"));
        task.GetProperty("dueOffsetDays").GetInt32().Should().Be(-1);
        task.GetProperty("description").GetRawText().Should().Be(original.GetProperty("tasks")[0].GetProperty("description").GetRawText());
    }

    [Fact]
    public async Task DescriptionOnlyEnUs_RoundTrips()
    {
        await OpenCrm7Async();
        // 設計器允許說明只填 en-US（zh-TW 可以不填）；匯出的檔案必須能讀回來
        var ruleId = _rig.Db(db => db.Set<TaskRule>().Single(r => r.Key == LeadRule).Id);
        _rig.Db(db =>
        {
            var task = db.Set<TaskRuleTask>().Single(t => t.RuleId == ruleId);
            task.DescriptionI18n = new([KeyValuePair.Create("en-US", "Check stock")]);
        });
        var (package, _) = await _rig.ExportAsync("crmx");
        package.GetProperty("taskRules")[0].GetProperty("tasks")[0].GetProperty("description").GetRawText().Should().Contain("Check stock");

        using var other = new SharedMasterRig();
        foreach (var json in SharedMasterPackages.Masters) await other.ImportAsync(json);
        await other.ImportAsync(package.GetRawText());
        await other.OpenOkAsync("crmx");
    }

    [Fact]
    public async Task TaskRules_OnMasterModule_ExportedWithSymbol()
    {
        await OpenCrm7Async();
        // 主檔模組上手動建的規則（不屬於任何應用程式）不跟著 crmx 匯出
        await CreateManualRuleAsync("manual-partner", _rig.FieldId("crmx", "bp/partner.accountMgr"));
        var (package, _) = await _rig.ExportAsync("crmx");
        var rules = package.GetProperty("taskRules").EnumerateArray().ToList();
        rules.Select(r => r.GetProperty("code").GetString()).Should().Equal(LeadRule, PartnerRule);
        var partner = rules[1];
        partner.GetProperty("module").GetString().Should().Be("bp/partner");
        partner.GetProperty("tasks")[0].GetProperty("assignee").GetString().Should().Be("bp/partner.accountMgr");

        // 主檔套件不帶任務規則：手動建在主檔上的不寫進檔案並警告；crmx 裝的屬於 crmx，不算主檔的
        var (master, masterWarnings) = await _rig.ExportAsync("bp");
        master.GetProperty("taskRules").GetArrayLength().Should().Be(0);
        masterWarnings.Should().Contain("共用主檔不帶任務規則，這些設定未匯出");
    }

    [Fact]
    public async Task CloseApp_RuleStops_TasksKept()
    {
        await OpenCrm7Async();
        await _rig.ImportAsync(SharedMasterPackages.Stock);
        await _rig.OpenOkAsync("stk");   // 讓往來對象主檔在 crmx 關閉後仍然開啟
        var created = await PostHotLeadAsync("開啟中");
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync() + _rig.LastError());
        var recordId = (await JsonOf(created)).GetProperty("id").GetInt64();
        LeadRuleTaskCount().Should().Be(1, "開啟中建立記錄，規則產生任務");
        var ruleId = _rig.Db(db => db.Set<TaskRule>().Single(r => r.Key == PartnerRule).Id);
        _rig.Db(db => { db.Set<TaskItem>().Add(new TaskItem { Title = "已產生的任務", RuleId = ruleId, RuleTaskCode = "visit", CreatedTime = DateTime.UtcNow }); });
        ActiveRuleKeys(PartnerKey).Should().Equal(PartnerRule);
        ActiveRuleKeys(LeadKey).Should().Equal(LeadRule);

        await _rig.CloseOkAsync("crmx");
        _rig.StatusOf("bp").Should().Be(AppStatus.Open);
        ActiveRuleKeys(PartnerKey).Should().BeEmpty("應用程式關閉時它的規則不再觸發，包括掛在共用主檔上的");
        ActiveRuleKeys(LeadKey).Should().BeEmpty();
        _rig.Db(db => db.Set<TaskRule>().Count(r => r.Key == PartnerRule || r.Key == LeadRule)).Should().Be(2, "規則本身保留");
        _rig.Db(db => db.Set<TaskRule>().Single(r => r.Key == PartnerRule).Active).Should().BeTrue("關閉不改規則的啟用設定，重新開啟才能原樣恢復");
        _rig.Db(db => db.Set<TaskItem>().Count(t => t.RuleId == ruleId)).Should().Be(1, "已產生的任務保留");
    }

    [Fact]
    public async Task ReopenApp_RuleResumes()
    {
        await OpenCrm7Async();
        await _rig.ImportAsync(SharedMasterPackages.Stock);
        await _rig.OpenOkAsync("stk");
        await _rig.CloseOkAsync("crmx");
        ActiveRuleKeys(PartnerKey).Should().BeEmpty();

        await _rig.ReopenOkAsync("crmx");
        ActiveRuleKeys(PartnerKey).Should().Equal(PartnerRule);
        ActiveRuleKeys(LeadKey).Should().Equal(LeadRule);

        var res = await PostHotLeadAsync("重新開啟後");
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync() + _rig.LastError());
        var recordId = (await JsonOf(res)).GetProperty("id").GetInt64();
        var tasks = await JsonOf(await _rig.Admin.GetAsync($"{Base}/records/{recordId}/tasks"));
        tasks.GetProperty("items").EnumerateArray().Select(t => t.GetProperty("title").GetString()).Should().Contain("打電話", "重新開啟後規則恢復觸發");
    }

    [Fact]
    public async Task DeleteApp_RemovesRules_IncludingOnMasterModule()
    {
        await OpenCrm7Async();
        await _rig.CloseOkAsync("crmx");
        await _rig.DeleteAppOkAsync("crmx");
        _rig.Db(db => db.Set<TaskRule>().Count(r => r.Key == PartnerRule || r.Key == LeadRule)).Should().Be(0);
        _rig.Db(db => db.Set<Module>().Any(m => m.Key == PartnerKey)).Should().BeTrue("共用主檔保留");
    }

    [Fact]
    public async Task DeleteApp_PurgedContributionField_DeactivatesManualRule()
    {
        await OpenCrm7Async();
        await CreateManualRuleAsync("manual-partner", _rig.FieldId("crmx", "bp/partner.accountMgr"));
        await _rig.CloseOkAsync("crmx");
        await _rig.DeleteAppOkAsync("crmx");

        var rule = (await RulesAsync(PartnerKey)).Single();
        rule.GetProperty("key").GetString().Should().Be("manual-partner");
        rule.GetProperty("active").GetBoolean().Should().BeFalse("引用的貢獻欄位被清除，規則自動停用");
        rule.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()).Should().Contain("引用的欄位已刪除");
        // 引擎（t4a2）合併後由主控補端到端斷言：記錄寫入時不再產生任務
    }
}
