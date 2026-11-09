using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>缺口 F：參照目標狀態守衛（config.refGuard）。虛構情境：工作（草稿／就緒／完成）與只能掛在「就緒」工作上的紀錄。</summary>
public class RefGuardTests : IDisposable
{
    private const string Apps = Base + "/apps";
    private readonly TestApp _app = new();
    private readonly HttpClient _admin;

    public RefGuardTests() => _admin = _app.AsUser();

    public void Dispose() => _app.Dispose();

    private static string Package(int formatVersion = 5, string guard = """{ "field": "task.status", "in": ["ready"] }""", string guardedType = "reference") => Build(formatVersion, guard, guardedType, guardedType == "reference" ? "\"refTarget\": { \"kind\": \"module\", \"key\": \"task\" }," : "");

    private static string Build(int formatVersion, string guard, string guardedType, string refTarget) => $$"""
        {
          "format": "formwright-app", "formatVersion": {{formatVersion}},
          "manifest": { "id": "gate-lite", "version": "1.0.0", "name": { "zh-TW": "守衛測試" } },
          "modules": [
            { "code": "task", "key": "gate-task", "name": { "zh-TW": "工作" }, "kind": "standalone", "titleField": "name",
              "blocks": [ { "code": "main", "label": { "zh-TW": "基本" }, "collapsed": false, "columns": 2, "fields": [
                { "code": "name", "type": "text", "label": { "zh-TW": "name" }, "config": { "required": true } },
                { "code": "status", "type": "enum", "label": { "zh-TW": "status" },
                  "options": [ { "code": "draft", "label": { "zh-TW": "草稿" }, "color": null },
                               { "code": "ready", "label": { "zh-TW": "就緒" }, "color": null },
                               { "code": "done", "label": { "zh-TW": "完成" }, "color": null } ],
                  "config": { "defaultValue": "opt_draft", "required": true, "isFilterable": true } } ] } ] },
            { "code": "note", "key": "gate-note", "name": { "zh-TW": "紀錄" }, "kind": "standalone", "titleField": "text",
              "blocks": [ { "code": "main", "label": { "zh-TW": "基本" }, "collapsed": false, "columns": 2, "fields": [
                { "code": "text", "type": "text", "label": { "zh-TW": "text" }, "config": { "required": true } },
                { "code": "task", "type": "{{guardedType}}", "label": { "zh-TW": "task" },
                  "config": { {{refTarget}} "refGuard": {{guard}} } } ] } ] }
          ]
        }
        """;

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    private async Task<HttpResponseMessage> Import(string json) => await _admin.PostAsync($"{Apps}/packages/import", Body(json));

    private async Task OpenPackage()
    {
        (await Import(Package())).StatusCode.Should().Be(HttpStatusCode.Created);
        var opened = await _admin.PostAsJsonAsync($"{Apps}/packages/gate-lite/open", new { });
        opened.StatusCode.Should().Be(HttpStatusCode.Created, await opened.Content.ReadAsStringAsync());
    }

    private async Task<(string Name, string Status, Dictionary<string, string> StatusOptions)> TaskKeys()
    {
        var module = await JsonOf(await _admin.GetAsync($"{Base}/modules/gate-task"));
        var fields = module.GetProperty("blocks").EnumerateArray().SelectMany(b => b.GetProperty("fields").EnumerateArray()).ToList();
        string Key(string label) => fields.Single(f => f.GetProperty("label").GetProperty("zh-TW").GetString() == label).GetProperty("key").GetString()!;
        var options = fields.Single(f => f.GetProperty("label").GetProperty("zh-TW").GetString() == "status").GetProperty("options").EnumerateArray()
            .ToDictionary(o => o.GetProperty("label").GetProperty("zh-TW").GetString()!, o => o.GetProperty("value").GetString()!);
        return (Key("name"), Key("status"), options);
    }

    private async Task<(string Text, string Task)> NoteKeys()
    {
        var module = await JsonOf(await _admin.GetAsync($"{Base}/modules/gate-note"));
        var fields = module.GetProperty("blocks").EnumerateArray().SelectMany(b => b.GetProperty("fields").EnumerateArray()).ToList();
        string Key(string label) => fields.Single(f => f.GetProperty("label").GetProperty("zh-TW").GetString() == label).GetProperty("key").GetString()!;
        return (Key("text"), Key("task"));
    }

    private async Task<long> CreateTask(string name, string statusLabel)
    {
        var (n, s, o) = await TaskKeys();
        var res = await _admin.PostAsJsonAsync($"{Base}/modules/gate-task/records",
            new { values = new Dictionary<string, object?> { [n] = name, [s] = o[statusLabel] } });
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return (await JsonOf(res)).GetProperty("id").GetInt64();
    }

    private async Task<HttpResponseMessage> CreateNote(string text, long task)
    {
        var (t, k) = await NoteKeys();
        return await _admin.PostAsJsonAsync($"{Base}/modules/gate-note/records", new { values = new Dictionary<string, object?> { [t] = text, [k] = task } });
    }

    private async Task<HttpResponseMessage> SetTaskStatus(long task, string statusLabel)
    {
        var (_, s, o) = await TaskKeys();
        var current = await JsonOf(await _admin.GetAsync($"{Base}/records/{task}"));
        return await _admin.PutAsJsonAsync($"{Base}/records/{task}",
            new { values = new Dictionary<string, object?> { [s] = o[statusLabel] }, rowVersion = current.GetProperty("rowVersion").GetString() });
    }

    // ---- 套件驗證 ----

    [Fact]
    public async Task Package_RefGuard_NeedsFormatVersion5()
    {
        var res = await Import(Package(formatVersion: 4));
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("refGuard");
    }

    [Theory]
    [InlineData("""{ "field": "task.status", "in": ["nope"] }""", "不是 task 的選項代號")]
    [InlineData("""{ "field": "task.name", "in": ["ready"] }""", "必須是 task 的下拉欄位")]
    [InlineData("""{ "field": "note.text", "in": ["ready"] }""", "必須是 task 的下拉欄位")]
    [InlineData("""{ "field": "task.status", "in": [] }""", "至少要列一個")]
    [InlineData("""{ "field": "task.status", "in": ["ready"], "extra": 1 }""", "不認得")]
    public async Task Package_BadRefGuard_IsRejected(string guard, string message)
    {
        var res = await Import(Package(guard: guard));
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain(message);
    }

    [Fact]
    public async Task Package_RefGuardOnANonReferenceField_IsRejected()
    {
        var res = await Import(Package(guardedType: "text"));
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("參照欄位");
    }

    // ---- 執行 ----

    [Fact]
    public async Task Create_OnlyWhenTheTargetIsInAnAllowedState()
    {
        await OpenPackage();
        var draft = await CreateTask("草稿工作", "草稿");
        var ready = await CreateTask("就緒工作", "就緒");

        (await CreateNote("掛草稿", draft)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await CreateNote("掛就緒", ready)).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Create_TargetInADisallowedState_SaysTheStateDoesNotMatch()
    {
        await OpenPackage();
        var draft = await CreateTask("草稿工作", "草稿");
        var res = await CreateNote("掛草稿", draft);
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain("目標記錄的狀態不符，不能選").And.NotContain("不存在或無權選取");
    }

    [Fact]
    public async Task Repointing_IsAlsoGuarded_ButUnchangedValuesAreNot()
    {
        await OpenPackage();
        var ready = await CreateTask("就緒工作", "就緒");
        var other = await CreateTask("另一個草稿", "草稿");
        var note = await JsonOf(await CreateNote("原本掛就緒", ready));
        var (text, taskKey) = await NoteKeys();
        var noteId = note.GetProperty("id").GetInt64();

        // 目標之後離開允許狀態：已存在的參照不受影響，其他欄位照常可改
        (await SetTaskStatus(ready, "完成")).StatusCode.Should().Be(HttpStatusCode.OK);
        var current = await JsonOf(await _admin.GetAsync($"{Base}/records/{noteId}"));
        var edit = await _admin.PutAsJsonAsync($"{Base}/records/{noteId}",
            new { values = new Dictionary<string, object?> { [text] = "改文字", [taskKey] = ready }, rowVersion = current.GetProperty("rowVersion").GetString() });
        edit.StatusCode.Should().Be(HttpStatusCode.OK, await edit.Content.ReadAsStringAsync());

        // 改選到不在允許狀態的目標被擋
        current = await JsonOf(await _admin.GetAsync($"{Base}/records/{noteId}"));
        var repoint = await _admin.PutAsJsonAsync($"{Base}/records/{noteId}",
            new { values = new Dictionary<string, object?> { [taskKey] = other }, rowVersion = current.GetProperty("rowVersion").GetString() });
        repoint.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Designer_RejectsAGuardWithABadFieldOrOption()
    {
        await OpenPackage();
        var note = await JsonOf(await _admin.GetAsync($"{Base}/modules/gate-note"));
        var block = note.GetProperty("blocks")[0].GetProperty("id").GetInt64();
        var task = await JsonOf(await _admin.GetAsync($"{Base}/modules/gate-task"));
        var nameFieldId = task.GetProperty("blocks").EnumerateArray().SelectMany(b => b.GetProperty("fields").EnumerateArray())
            .Single(f => f.GetProperty("label").GetProperty("zh-TW").GetString() == "name").GetProperty("id").GetInt64();

        var res = await _admin.PostAsJsonAsync($"{Base}/modules/gate-note/fields", new
        {
            blockId = block, type = "reference", label = new Dictionary<string, string> { ["zh-TW"] = "另一個任務" },
            config = new { refTarget = new { kind = "module", key = "gate-task" }, refGuard = new { fieldId = nameFieldId, optionIds = new[] { 1L } } }
        });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("下拉欄位");
    }

    [Fact]
    public async Task Designer_SavingWithoutRefGuard_KeepsTheGuard()
    {
        await OpenPackage();
        var note = await JsonOf(await _admin.GetAsync($"{Base}/modules/gate-note"));
        var field = note.GetProperty("blocks").EnumerateArray().SelectMany(b => b.GetProperty("fields").EnumerateArray())
            .Single(f => f.GetProperty("type").GetString() == "reference");
        var fieldId = field.GetProperty("id").GetInt64();
        var blockId = note.GetProperty("blocks")[0].GetProperty("id").GetInt64();

        // 設計器存檔：只改標籤，config 不帶 refGuard（設計器編不了守衛）
        var save = await _admin.PutAsJsonAsync($"{Base}/fields/{fieldId}", new
        {
            type = "reference", label = new Dictionary<string, string> { ["zh-TW"] = "所屬工作" }, blockId,
            config = new { refTarget = new { kind = "module", key = "gate-task" } }
        });
        save.StatusCode.Should().Be(HttpStatusCode.OK, await save.Content.ReadAsStringAsync());

        var after = (await JsonOf(await _admin.GetAsync($"{Base}/modules/gate-note"))).GetProperty("blocks").EnumerateArray()
            .SelectMany(b => b.GetProperty("fields").EnumerateArray()).Single(f => f.GetProperty("id").GetInt64() == fieldId);
        after.GetProperty("config").TryGetProperty("refGuard", out var guard).Should().BeTrue();
        guard.ValueKind.Should().Be(JsonValueKind.Object);

        var draft = await CreateTask("草稿工作", "草稿");
        var text = note.GetProperty("blocks").EnumerateArray().SelectMany(b => b.GetProperty("fields").EnumerateArray())
            .Single(f => f.GetProperty("type").GetString() == "text").GetProperty("key").GetString()!;
        var blocked = await _admin.PostAsJsonAsync($"{Base}/modules/gate-note/records", new
        {
            values = new Dictionary<string, object?> { [text] = "掛草稿", [field.GetProperty("key").GetString()!] = draft }
        });
        blocked.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Export_GuardOnADeactivatedOption_IsDropped_WithAWarning_AndTheFileReimports()
    {
        await OpenPackage();
        var (_, _, options) = await TaskKeys();
        var ready = options["就緒"]["opt_".Length..];
        (await _admin.PutAsJsonAsync($"{Base}/options/{ready}", new { label = new Dictionary<string, string> { ["zh-TW"] = "就緒" }, active = false }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var res = await _admin.GetAsync($"{Apps}/gate-lite/export");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var warnings = Uri.UnescapeDataString(string.Join('|', res.Headers.GetValues("X-Export-Warnings")));
        warnings.Should().Contain("目標狀態守衛").And.Contain("匯入後不再限制目標狀態");
        var package = await JsonOf(res);
        package.GetProperty("modules").EnumerateArray().Single(m => m.GetProperty("code").GetString() == "note")
            .GetProperty("blocks").EnumerateArray().SelectMany(b => b.GetProperty("fields").EnumerateArray())
            .Single(f => f.GetProperty("type").GetString() == "reference").GetProperty("config")
            .TryGetProperty("refGuard", out _).Should().BeFalse();

        using var fresh = new TestApp();
        var admin = fresh.AsUser();
        (await admin.PostAsync($"{Apps}/packages/import", Body(package.GetRawText()))).StatusCode.Should().Be(HttpStatusCode.Created);
        var opened = await admin.PostAsJsonAsync($"{Apps}/packages/gate-lite/open", new { });
        opened.StatusCode.Should().Be(HttpStatusCode.Created, await opened.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Export_KeepsTheGuard_AndTheRoundTripStillGuards()
    {
        await OpenPackage();
        var exported = await _admin.GetAsync($"{Apps}/gate-lite/export");
        exported.StatusCode.Should().Be(HttpStatusCode.OK);
        var package = await JsonOf(exported);
        package.GetProperty("formatVersion").GetInt32().Should().Be(6);
        var guarded = package.GetProperty("modules").EnumerateArray().Single(m => m.GetProperty("code").GetString() == "note")
            .GetProperty("blocks").EnumerateArray().SelectMany(b => b.GetProperty("fields").EnumerateArray())
            .Single(f => f.GetProperty("type").GetString() == "reference").GetProperty("config").GetProperty("refGuard");
        guarded.GetProperty("in").EnumerateArray().Should().HaveCount(1);
        guarded.GetProperty("field").GetString().Should().Contain(".");
    }
}
