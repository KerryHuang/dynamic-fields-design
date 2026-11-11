using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DynamicModules.Application.Dynamic.TemplateService;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 套件模組的 tasksEnabled（格式 7，任務設計 §4.4、§5.8）：開啟、匯出、往返、格式閘門、內建主檔預設關，
/// 以及規則掛在任務關閉的模組上時開啟會自動啟用（預覽先列出）。
/// </summary>
public class AppTaskToggleTests : IDisposable
{
    private const string PartnerKey = "master-bp-partner";
    private const string PartnerRuleCode = "tt-partner-visit", OffRuleCode = "tt-off-check";

    private const string PartnerRule = """
        { "code": "tt-partner-visit", "name": { "zh-TW": "拜訪" }, "active": true, "module": "bp/partner",
          "trigger": { "on": "manual" },
          "tasks": [ { "code": "visit", "title": { "zh-TW": "拜訪客戶" }, "assignee": "bp/partner.accountMgr" } ] }
        """;

    private const string OffRule = """
        { "code": "tt-off-check", "name": { "zh-TW": "檢查" }, "active": true, "module": "off",
          "trigger": { "on": "manual" },
          "tasks": [ { "code": "check", "title": { "zh-TW": "確認" }, "assignee": "off.owner" } ] }
        """;

    private readonly SharedMasterRig _rig = new();

    public void Dispose() => _rig.Dispose();

    /// <summary>主檔 bp 改成格式 7、partner 關閉任務。</summary>
    private static string BpOff(int formatVersion = 7)
    {
        var root = JsonNode.Parse(SharedMasterPackages.Bp)!.AsObject();
        root["formatVersion"] = formatVersion;
        root["modules"]![0]!["tasksEnabled"] = false;
        return root.ToJsonString();
    }

    /// <summary>使用者套件 tt：模組 on（沒寫 tasksEnabled）與 off（false），貢獻 bp/partner 一個使用者欄位；rules 是 taskRules 的內容。</summary>
    private static string Tt(params string[] rules) => $$"""
        {
          "format": "formwright-app", "formatVersion": 7,
          "manifest": { "id": "tt", "version": "1.0.0", "name": { "zh-TW": "任務開關測試" },
            "requires": { "masters": [ { "id": "bp", "modules": ["partner"] } ] } },
          "groups": [],
          "modules": [
            { "code": "on", "key": "tt-on", "name": { "zh-TW": "開著" }, "kind": "standalone", "titleField": "name",
              "blocks": [ { "code": "main", "label": { "zh-TW": "基本資料" }, "fields": [
                { "code": "name", "type": "text", "label": { "zh-TW": "名稱" }, "config": { "required": true } } ] } ],
              "permissions": [] },
            { "code": "off", "key": "tt-off", "name": { "zh-TW": "關著" }, "kind": "standalone", "titleField": "name", "tasksEnabled": false,
              "blocks": [ { "code": "main", "label": { "zh-TW": "基本資料" }, "fields": [
                { "code": "name", "type": "text", "label": { "zh-TW": "名稱" }, "config": { "required": true } },
                { "code": "owner", "type": "user", "label": { "zh-TW": "負責人" }, "config": {} } ] } ],
              "permissions": [] }
          ],
          "contributions": [ {
            "master": "bp", "module": "partner",
            "block": { "code": "tt", "name": { "zh-TW": "任務" } },
            "fields": [ { "code": "accountMgr", "type": "user", "label": { "zh-TW": "客戶經理" }, "config": {} } ],
            "permissions": []
          } ],
          "taskRules": [ {{string.Join(",", rules)}} ]
        }
        """;

    private bool TasksEnabled(string moduleKey) => TasksEnabled(_rig, moduleKey);

    private static bool TasksEnabled(SharedMasterRig rig, string moduleKey) =>
        rig.Db(db => db.Set<Module>().Single(m => m.Key == moduleKey).TasksEnabled);

    private static List<string> Warnings(JsonElement body) =>
        body.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()!).ToList();

    private static JsonElement ModuleOf(JsonElement package, string code) =>
        package.GetProperty("modules").EnumerateArray().Single(m => m.GetProperty("code").GetString() == code);

    [Fact]
    public async Task Open_TasksEnabledFalse_ModuleOff_OmittedIsOn()
    {
        await _rig.ImportAsync(SharedMasterPackages.Bp);
        await _rig.ImportAsync(Tt());
        await _rig.OpenOkAsync("tt");

        TasksEnabled("tt-off").Should().BeFalse();
        TasksEnabled("tt-on").Should().BeTrue("省略＝開");
        TasksEnabled(PartnerKey).Should().BeTrue("格式 6 的主檔沒有這個欄位，維持開");
        var schema = await JsonOf(await _rig.Admin.GetAsync($"{Base}/modules/tt-off/schema"));
        schema.GetProperty("tasksEnabled").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Export_OffWritesFalse_OnOmits()
    {
        await _rig.ImportAsync(SharedMasterPackages.Bp);
        await _rig.ImportAsync(Tt());
        await _rig.OpenOkAsync("tt");

        var (package, _) = await _rig.ExportAsync("tt");
        package.GetProperty("formatVersion").GetInt32().Should().Be(7);
        ModuleOf(package, "off").GetProperty("tasksEnabled").GetBoolean().Should().BeFalse();
        ModuleOf(package, "on").TryGetProperty("tasksEnabled", out _).Should().BeFalse("開著的模組不寫，既有匯出不變");
    }

    [Fact]
    public async Task ExportThenImport_RoundTrip_KeepsOff()
    {
        await _rig.ImportAsync(SharedMasterPackages.Bp);
        await _rig.ImportAsync(Tt());
        await _rig.OpenOkAsync("tt");
        var (package, _) = await _rig.ExportAsync("tt");

        using var other = new SharedMasterRig();
        await other.ImportAsync(SharedMasterPackages.Bp);
        await other.ImportAsync(package.GetRawText());
        await other.OpenOkAsync("tt");
        TasksEnabled(other, "tt-off").Should().BeFalse();
        TasksEnabled(other, "tt-on").Should().BeTrue();
    }

    [Fact]
    public async Task Format6_WithModuleTasksEnabled_Rejected()
    {
        // 沒有 taskRules 的格式 6 檔案也要擋：舊讀取器會靜默略過這個欄位
        var res = await _rig.Admin.PostAsync($"{SharedMasterRig.Apps}/packages/import",
            new StringContent(BpOff(formatVersion: 6), Encoding.UTF8, "application/json"));
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("partner.tasksEnabled: formatVersion 7 才支援 tasksEnabled");
    }

    [Fact]
    public async Task BuiltInMasters_OpenWithTasksOff_AppModulesOn()
    {
        await _rig.OpenOkAsync("crm-sales", new { groupMap = new Dictionary<string, object>() });

        TasksEnabled("master-partner").Should().BeFalse("內建主檔預設不用任務");
        TasksEnabled("master-contact").Should().BeFalse();
        TasksEnabled("crm-lead").Should().BeTrue("一般應用程式的模組維持開");
    }

    [Fact]
    public async Task RuleOnTasksOffModule_OpenEnablesModule_Warns()
    {
        await _rig.ImportAsync(BpOff());
        await _rig.ImportAsync(Tt(PartnerRule, OffRule));

        var opened = await _rig.OpenOkAsync("tt");

        TasksEnabled(PartnerKey).Should().BeTrue("規則掛在任務關閉的主檔模組上，開啟時自動打開");
        TasksEnabled("tt-off").Should().BeTrue("本套件的模組也一樣");
        Warnings(opened).Should().Contain([
            $"任務規則「{PartnerRuleCode}」所在的模組「往來對象」原本未啟用任務，已自動啟用",
            $"任務規則「{OffRuleCode}」所在的模組「關著」原本未啟用任務，已自動啟用"]);
        _rig.Db(db => db.Set<TaskRule>().Count(r => r.Key == PartnerRuleCode || r.Key == OffRuleCode)).Should().Be(2, "規則照建");
    }

    [Fact]
    public async Task RuleOnTasksOffModule_PreviewListsAutoEnable()
    {
        await _rig.ImportAsync(BpOff());
        await _rig.ImportAsync(Tt(PartnerRule, OffRule));

        var res = await _rig.Admin.PostAsJsonAsync($"{SharedMasterRig.Apps}/packages/tt/preview", _rig.BodyFor("tt"));
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var preview = await JsonOf(res);

        Warnings(preview).Should().Contain([
            $"任務規則「{PartnerRuleCode}」所在的模組「往來對象」未啟用任務，開啟時會自動啟用",
            $"任務規則「{OffRuleCode}」所在的模組「關著」未啟用任務，開啟時會自動啟用"]);
        _rig.StatusOf("bp").Should().BeNull("預覽不寫入");
    }

    [Fact]
    public async Task RuleOnTasksOffModule_PreviewListsAutoEnable_MasterAlreadyOpen()
    {
        // 主檔已開啟（由另一個應用程式帶開）：看資料庫裡模組現在的開關
        await _rig.ImportAsync(BpOff());
        await _rig.ImportAsync(Tt());
        await _rig.OpenOkAsync("tt");
        TasksEnabled(PartnerKey).Should().BeFalse();
        var withRule = JsonNode.Parse(Tt(PartnerRule))!.AsObject();
        withRule["manifest"]!["id"] = "tt2";
        foreach (var m in withRule["modules"]!.AsArray()) m!["key"] = "tt2-" + m["code"]!.GetValue<string>();
        withRule["contributions"]![0]!["block"]!["code"] = "tt2";
        await _rig.ImportAsync(withRule.ToJsonString());

        var preview = await JsonOf(await _rig.Admin.PostAsJsonAsync($"{SharedMasterRig.Apps}/packages/tt2/preview", _rig.BodyFor("tt2")));
        Warnings(preview).Should().Contain($"任務規則「{PartnerRuleCode}」所在的模組「往來對象」未啟用任務，開啟時會自動啟用");
    }

    [Fact]
    public async Task MasterReimport_KeepsCustomerToggle()
    {
        await _rig.ImportAsync(BpOff());
        await _rig.ImportAsync(Tt());
        await _rig.OpenOkAsync("tt");
        TasksEnabled(PartnerKey).Should().BeFalse();

        // 管理員把主檔的任務打開，之後再匯入（合併）同一份主檔：開啟後模組設定屬於客戶，不被套件翻回去
        var module = await JsonOf(await _rig.Admin.GetAsync($"{Base}/modules/{PartnerKey}"));
        var put = await _rig.Admin.PutAsJsonAsync($"{Base}/modules/{PartnerKey}", new
        {
            name = module.GetProperty("name"), icon = module.GetProperty("icon").GetString(),
            titleFieldId = module.GetProperty("titleFieldId").GetInt64(), tasksEnabled = true
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
        TasksEnabled(PartnerKey).Should().BeTrue();

        await _rig.ImportMergeOkAsync(BpOff());
        TasksEnabled(PartnerKey).Should().BeTrue();
        var (exported, _) = await _rig.ExportAsync("bp");
        await _rig.ImportMergeOkAsync(exported.GetRawText());
        TasksEnabled(PartnerKey).Should().BeTrue();
    }

    [Fact]
    public async Task ModuleTemplate_ExportThenInstantiate_KeepsOff()
    {
        using var app = new TemplateExportTestApp();
        var admin = app.AsUser("admin");
        var key = await CreateModule(admin);
        var module = await JsonOf(await admin.GetAsync($"{Base}/modules/{key}"));
        var put = await admin.PutAsJsonAsync($"{Base}/modules/{key}", new
        {
            name = module.GetProperty("name"), titleFieldId = module.GetProperty("titleFieldId").GetInt64(), tasksEnabled = false
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());

        var exported = await admin.GetAsync($"{Base}/modules/{key}/export-template");
        var json = await exported.Content.ReadAsStringAsync();
        exported.StatusCode.Should().Be(HttpStatusCode.OK, json);
        json.Should().Contain("\"tasksEnabled\": false");
        var package = TemplateJson.Parse(json).Package!;
        package.Modules.Single().TasksEnabled.Should().BeFalse();

        using var fresh = new TemplateExportTestApp();
        fresh.Extra.Add(package with { Key = "tg-rt" });
        var admin2 = fresh.AsUser("admin");
        var res = await admin2.PostAsJsonAsync($"{Base}/templates/tg-rt/instantiate",
            new { modules = Array.Empty<object>(), excludeFields = Array.Empty<string>() });
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        var detail = await JsonOf(await admin2.GetAsync($"{Base}/modules/{key}"));
        detail.GetProperty("tasksEnabled").GetBoolean().Should().BeFalse();
    }
}
