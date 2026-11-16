using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests;

/// <summary>狀態機經 HTTP：設計器權限，以及繞過前端直接呼叫 API 也擋得下。</summary>
public class WorkflowApiTests : IClassFixture<TestApp>
{
    private readonly TestApp _app;
    private readonly HttpClient _admin;

    public WorkflowApiTests(TestApp app)
    {
        _app = app;
        _admin = app.AsUser("admin");
    }

    private static Dictionary<string, string> Zh(string s) => new() { ["zh-TW"] = s };

    private async Task<long> Option(long fieldId, string label) =>
        (await JsonOf(await _admin.PostAsJsonAsync($"{Base}/fields/{fieldId}/options", new { label = Zh(label), color = (string?)null })))
        .GetProperty("id").GetInt64();

    private async Task<long> Group(string key) =>
        (await JsonOf(await _admin.GetAsync($"{Base}/groups"))).EnumerateArray()
        .Single(g => g.GetProperty("key").GetString() == key).GetProperty("id").GetInt64();

    private sealed record Doc(string Key, string Title, string Status, long StatusId, long Draft, long Approved, long Manager);

    private async Task<Doc> Setup()
    {
        var key = await CreateModule(_admin);
        var status = await AddField(_admin, key, "enum");
        var draft = await Option(status.Id, "草稿");
        var approved = await Option(status.Id, "已核准");
        await Activate(_admin, key);
        var sales = await Group("sales");
        var manager = await Group("sales-manager");
        var priv = new { read = true, add = true, edit = true, delete = true, printExport = false, pricing = true, approve = false };
        (await _admin.PutAsJsonAsync($"{Base}/modules/{key}/permissions", new
        {
            groups = new[] { new { groupId = sales, privileges = priv, scope = "all" }, new { groupId = manager, privileges = priv, scope = "all" } },
            users = Array.Empty<object>(), fields = Array.Empty<object>(), options = Array.Empty<object>()
        })).EnsureSuccessStatusCode();
        (await _admin.PutAsJsonAsync($"{Base}/modules/{key}/workflow", new
        {
            statusFieldId = status.Id, initialOptionId = draft,
            states = new object[] { new { optionId = draft }, new { optionId = approved, readOnly = true } },
            transitions = new[] { new { from = draft, to = approved, groupIds = new[] { manager } } }
        })).EnsureSuccessStatusCode();
        return new Doc(key, await TitleKey(_admin, key), status.Key, status.Id, draft, approved, manager);
    }

    private static HttpClient As(TestApp app, string user) => app.AsUser(user);

    [Fact]
    public async Task Designer_Endpoints_RequireDesignerPermission()
    {
        var d = await Setup();
        var sales = As(_app, "sales01");
        (await sales.GetAsync($"{Base}/modules/{d.Key}/workflow")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await sales.DeleteAsync($"{Base}/modules/{d.Key}/workflow")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _admin.GetAsync($"{Base}/modules/{d.Key}/workflow")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DirectApiCall_CannotCreateInApprovedState()
    {
        var d = await Setup();
        var res = await As(_app, "sales01").PostAsJsonAsync($"{Base}/modules/{d.Key}/records",
            new { values = new Dictionary<string, object> { [d.Title] = "t", [d.Status] = $"opt_{d.Approved}" } });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DirectApiCall_CannotSkipTheGroupCheck_AndManagerCan()
    {
        var d = await Setup();
        var sales = As(_app, "sales01");
        var created = await JsonOf(await sales.PostAsJsonAsync($"{Base}/modules/{d.Key}/records",
            new { values = new Dictionary<string, object> { [d.Title] = "t" } }));
        var id = created.GetProperty("id").GetInt64();
        var version = created.GetProperty("rowVersion").GetString();

        var denied = await sales.PutAsJsonAsync($"{Base}/records/{id}",
            new { values = new Dictionary<string, object> { [d.Status] = $"opt_{d.Approved}" }, rowVersion = version });
        denied.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await JsonOf(await sales.GetAsync($"{Base}/records/{id}"))).GetProperty("values").GetProperty(d.Status).GetString()
            .Should().Be($"opt_{d.Draft}");

        var ok = await As(_app, "manager01").PutAsJsonAsync($"{Base}/records/{id}",
            new { values = new Dictionary<string, object> { [d.Status] = $"opt_{d.Approved}" }, rowVersion = version });
        ok.StatusCode.Should().Be(HttpStatusCode.OK);

        // 已核准是唯讀狀態：不能刪除
        var current = (await JsonOf(await sales.GetAsync($"{Base}/records/{id}"))).GetProperty("rowVersion").GetString();
        (await sales.DeleteAsync($"{Base}/records/{id}?rowVersion={current}")).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Schema_CarriesWorkflowForTheCurrentUser()
    {
        var d = await Setup();
        var forSales = await JsonOf(await As(_app, "sales01").GetAsync($"{Base}/modules/{d.Key}/schema"));
        forSales.GetProperty("workflow").GetProperty("transitions").GetArrayLength().Should().Be(0);
        var forManager = await JsonOf(await As(_app, "manager01").GetAsync($"{Base}/modules/{d.Key}/schema"));
        forManager.GetProperty("workflow").GetProperty("transitions").GetArrayLength().Should().Be(1);
        forManager.GetProperty("workflow").GetProperty("statusFieldKey").GetString().Should().Be(d.Status);
    }

    [Fact]
    public async Task DirectApiCall_CannotClearTheStatus_OnCreateOrUpdate()
    {
        var d = await Setup();
        var sales = As(_app, "sales01");

        // 新增時清空（null）：補上起始狀態，不會出現沒有狀態的記錄
        var created = await JsonOf(await sales.PostAsJsonAsync($"{Base}/modules/{d.Key}/records",
            new { values = new Dictionary<string, object?> { [d.Title] = "t", [d.Status] = null } }));
        created.GetProperty("values").GetProperty(d.Status).GetString().Should().Be($"opt_{d.Draft}");
        var id = created.GetProperty("id").GetInt64();
        var version = created.GetProperty("rowVersion").GetString();

        // 修改時清空：400，狀態不變
        foreach (var cleared in new object?[] { null, "" })
        {
            var res = await sales.PutAsJsonAsync($"{Base}/records/{id}",
                new { values = new Dictionary<string, object?> { [d.Status] = cleared }, rowVersion = version });
            res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        (await JsonOf(await sales.GetAsync($"{Base}/records/{id}"))).GetProperty("values").GetProperty(d.Status).GetString()
            .Should().Be($"opt_{d.Draft}");
    }
}
