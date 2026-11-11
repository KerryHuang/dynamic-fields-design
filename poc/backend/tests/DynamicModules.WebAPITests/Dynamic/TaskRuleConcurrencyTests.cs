using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 任務規則經過 HTTP：檔案型 SQLite（與正式站相同的鎖行為）下同時送出的寫入只觸發一次；X-Locale 決定規則任務的標題語系。
/// </summary>
public class TaskRuleConcurrencyTests : IDisposable
{
    private readonly TestApp _app;
    private readonly HttpClient _admin;

    public TaskRuleConcurrencyTests()
    {
        _app = new TestApp(fileDatabase: true);
        // EF 每條 SQL 都寫 console，console 的鎖會把請求排成一列，蓋掉真正的併發
        _app.Settings["Logging:LogLevel:Default"] = "Warning";
        _admin = _app.AsUser();
    }

    public void Dispose() => _app.Dispose();

    private sealed record Rig(string Module, string Title, (long Id, string Key) Owner, (long Id, string Key) Flag);

    private async Task<Rig> BuildRig()
    {
        var module = await CreateModule(_admin);
        var owner = await AddField(_admin, module, "user");
        var flag = await AddField(_admin, module, "boolean");
        await Activate(_admin, module);
        _app.GrantBusinessGroups(module);
        return new Rig(module, await TitleKey(_admin, module), (owner.Id, owner.Key), (flag.Id, flag.Key));
    }

    private async Task CreateRule(Rig r, string key, object trigger, object? when, string repeat, params object[] tasks)
    {
        var res = await _admin.PostAsJsonAsync($"{Base}/modules/{r.Module}/task-rules", new
        {
            key, name = new Dictionary<string, string> { ["zh-TW"] = key }, active = true, repeat, trigger, when, tasks
        });
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
    }

    private static object Step(Rig r, string code, Dictionary<string, string> title) =>
        new { code, title, assigneeFieldId = r.Owner.Id, dueOffsetDays = 0 };

    private static object FlagIsTrue(Rig r) =>
        new { all = new[] { new { field = r.Flag.Id, op = "eq", value = true } } };

    private async Task<JsonElement> NewRecord(Rig r, HttpClient c, bool flag, string? locale = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{Base}/modules/{r.Module}/records")
        {
            Content = JsonContent.Create(new { values = new Dictionary<string, object> { [r.Title] = "案", [r.Owner.Key] = "sales02", [r.Flag.Key] = flag } })
        };
        if (locale is not null) req.Headers.Add("X-Locale", locale);
        var res = await c.SendAsync(req);
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return await JsonOf(res);
    }

    private async Task<JsonElement> RecordTasks(long recordId) =>
        (await JsonOf(await _admin.GetAsync($"{Base}/records/{recordId}/tasks"))).GetProperty("items");

    [Fact]
    public async Task ConcurrentUpdates_SameRecord_RuleFiresOnce()
    {
        var r = await BuildRig();
        await CreateRule(r, "conc-match", new { on = "onUpdateMatch" }, FlagIsTrue(r), "once",
            Step(r, "a", new() { ["zh-TW"] = "甲" }), Step(r, "b", new() { ["zh-TW"] = "乙" }));
        var record = await NewRecord(r, _admin, flag: false);
        var id = record.GetProperty("id").GetInt64();
        var rowVersion = record.GetProperty("rowVersion").GetString();

        var clients = new[] { _app.AsUser("sales01"), _app.AsUser("sales02") };
        var results = await Task.WhenAll(clients.Select(c => c.PutAsJsonAsync($"{Base}/records/{id}",
            new { values = new Dictionary<string, object> { [r.Flag.Key] = true }, rowVersion })));

        results.Select(x => x.StatusCode).Should().BeEquivalentTo([HttpStatusCode.OK, HttpStatusCode.Conflict]);
        (await RecordTasks(id)).GetArrayLength().Should().Be(2, "任務數等於範本數");
    }

    private async Task<List<HttpStatusCode>> ApplyTwiceAtOnce(long recordId, string ruleKey)
    {
        var clients = new[] { _app.AsUser("sales01"), _app.AsUser("sales02") };
        var results = await Task.WhenAll(clients.Select(c => c.PostAsync($"{Base}/records/{recordId}/task-rules/{ruleKey}/apply", null)));
        return results.Select(x => x.StatusCode).ToList();
    }

    [Fact]
    public async Task ConcurrentManualApply_GeneratesOnce()
    {
        var r = await BuildRig();
        await CreateRule(r, "conc-manual", new { on = "manual" }, null, "once",
            Step(r, "a", new() { ["zh-TW"] = "甲" }), Step(r, "b", new() { ["zh-TW"] = "乙" }));
        var id = (await NewRecord(r, _admin, flag: false)).GetProperty("id").GetInt64();

        (await ApplyTwiceAtOnce(id, "conc-manual")).Should().BeEquivalentTo([HttpStatusCode.OK, HttpStatusCode.Conflict]);
        (await RecordTasks(id)).GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task ConcurrentManualApply_EveryTime_OneBatch()
    {
        var r = await BuildRig();
        await CreateRule(r, "conc-every", new { on = "manual" }, null, "everyTime", Step(r, "a", new() { ["zh-TW"] = "甲" }));
        var id = (await NewRecord(r, _admin, flag: false)).GetProperty("id").GetInt64();

        (await ApplyTwiceAtOnce(id, "conc-every")).Should().BeEquivalentTo([HttpStatusCode.OK, HttpStatusCode.Conflict]);
        (await RecordTasks(id)).GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task RuleTask_XLocaleHeader_Thai()
    {
        var r = await BuildRig();
        await CreateRule(r, "thai-title", new { on = "onCreate" }, null, "once",
            Step(r, "a", new() { ["zh-TW"] = "準備資料", ["th"] = "เตรียมข้อมูล" }));

        var record = await NewRecord(r, _app.AsUser("sales01"), flag: false, locale: "th");

        var items = await RecordTasks(record.GetProperty("id").GetInt64());
        items.GetArrayLength().Should().Be(1);
        items[0].GetProperty("title").GetString().Should().Be("เตรียมข้อมูล");
    }
}
