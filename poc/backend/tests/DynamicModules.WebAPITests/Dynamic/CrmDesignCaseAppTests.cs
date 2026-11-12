using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 內建 CRM（apps/crm-sales.app.json 1.2.0）的設計案與設計圖：模組、狀態機、群組權限、任務規則，以及加上簽核流程後的端到端行為。
/// 程式碼裡沒有任何設計案專用邏輯，行為全來自套件宣告；簽核流程在 a5 之前不在套件裡，測試內以 API 建立。
/// </summary>
public class CrmDesignCaseAppTests : IDisposable
{
    private const string Apps = Base + "/apps";
    private const string Case = "crm-design-case";
    private const string Drawing = "crm-design-drawing";
    private const string Designer = "designert";
    private const string Partner = BuiltInMasterTests.Partner;

    private readonly TestApp _app = new();
    private readonly HttpClient _admin;

    public CrmDesignCaseAppTests() => _admin = _app.AsUser();

    public void Dispose() => _app.Dispose();

    private static object MapAll(params string[] groups) => new
    {
        groupMap = groups.ToDictionary(g => g, _ => (object)new { mode = "create" })
    };

    private async Task<JsonElement> OpenCrm(params string[] groups)
    {
        var opened = await _admin.PostAsJsonAsync($"{Apps}/packages/crm-sales/open",
            MapAll(groups.Length == 0 ? ["sales", "manager", "designer"] : groups));
        opened.StatusCode.Should().Be(HttpStatusCode.Created, await opened.Content.ReadAsStringAsync());
        return await JsonOf(opened);
    }

    private static async Task<Dictionary<string, string>> FieldKeys(HttpClient c, string moduleKey)
    {
        var module = await JsonOf(await c.GetAsync($"{Base}/modules/{moduleKey}"));
        return module.GetProperty("blocks").EnumerateArray().SelectMany(b => b.GetProperty("fields").EnumerateArray())
            .ToDictionary(f => f.GetProperty("label").GetProperty("zh-TW").GetString()!, f => f.GetProperty("key").GetString()!);
    }

    private static async Task<string> OptionValue(HttpClient c, string moduleKey, string fieldLabel, string optionLabel)
    {
        var module = await JsonOf(await c.GetAsync($"{Base}/modules/{moduleKey}"));
        var field = module.GetProperty("blocks").EnumerateArray().SelectMany(b => b.GetProperty("fields").EnumerateArray())
            .Single(f => f.GetProperty("label").GetProperty("zh-TW").GetString() == fieldLabel);
        return field.GetProperty("options").EnumerateArray()
            .Single(o => o.GetProperty("label").GetProperty("zh-TW").GetString() == optionLabel).GetProperty("value").GetString()!;
    }

    private static async Task<JsonElement> PostOk(HttpClient c, string url, object body, HttpStatusCode expected = HttpStatusCode.Created)
    {
        var res = await c.PostAsJsonAsync(url, body);
        res.StatusCode.Should().Be(expected, await res.Content.ReadAsStringAsync());
        return await JsonOf(res);
    }

    private async Task<long> NewCustomer()
    {
        var p = await FieldKeys(_admin, Partner);
        var created = await PostOk(_admin, $"{Base}/modules/{Partner}/records", new
        {
            values = new Dictionary<string, object> { [p["名稱"]] = "宏達科技", [p["角色"]] = await OptionValue(_admin, Partner, "角色", "客戶") }
        });
        return created.GetProperty("id").GetInt64();
    }

    /// <summary>新增測試用設計人員（DemoPeople 的 designer01 在 a6b 才進來），並依帳號清單設定三個 CRM 群組的成員。</summary>
    private async Task SetMembers(string[] sales, string[] managers, string[] designers)
    {
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
            if (!db.Set<AppUser>().Any(u => u.UserId == Designer))
            {
                db.Set<AppUser>().Add(new AppUser { UserId = Designer, DisplayName = "設計人員T" });
                db.SaveChanges();
            }
        }
        var groups = await JsonOf(await _admin.GetAsync($"{Base}/admin/groups"));
        async Task Put(string name, string[] users)
        {
            var g = groups.EnumerateArray().Single(x => x.GetProperty("nameI18n").GetProperty("zh-TW").GetString() == name);
            var res = await _admin.PutAsJsonAsync($"{Base}/admin/groups/{g.GetProperty("id").GetInt64()}/members", new { userIds = users });
            res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        }
        await Put("CRM 業務", sales);
        await Put("CRM 業務主管", managers);
        await Put("CRM 產品設計", designers);
    }

    private async Task<JsonElement> NewCase(HttpClient by, string owner = "sales01", string designOwner = Designer)
    {
        var c = await FieldKeys(_admin, Case);
        return await PostOk(by, $"{Base}/modules/{Case}/records", new
        {
            values = new Dictionary<string, object>
            {
                [c["案名"]] = "感測器外殼改版", [c["客戶"]] = await NewCustomer(), [c["負責業務"]] = owner,
                [c["設計負責人"]] = designOwner, [c["需求說明"]] = "外殼加散熱槽，尺寸維持不變"
            }
        });
    }

    private async Task<HttpResponseMessage> Put(HttpClient by, JsonElement record, Dictionary<string, object> values) =>
        await by.PutAsJsonAsync($"{Base}/records/{record.GetProperty("id").GetInt64()}",
            new { values, rowVersion = record.GetProperty("rowVersion").GetString() });

    private async Task<JsonElement> Move(HttpClient by, JsonElement record, string toLabel, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var status = (await FieldKeys(_admin, Case))["狀態"];
        var res = await Put(by, record, new() { [status] = await OptionValue(_admin, Case, "狀態", toLabel) });
        res.StatusCode.Should().Be(expected, await res.Content.ReadAsStringAsync());
        return expected == HttpStatusCode.OK ? await JsonOf(res) : record;
    }

    private async Task<JsonElement> Reload(long id) => await JsonOf(await _admin.GetAsync($"{Base}/records/{id}"));

    // ---- Task 1：模組、狀態機、群組、權限 ----

    [Fact]
    public async Task Open_CreatesDesignCaseAndDrawing()
    {
        var app = await OpenCrm();
        var keys = app.GetProperty("modules").EnumerateArray().Select(m => m.GetProperty("key").GetString()).ToList();
        keys.Should().Contain([Case, Drawing]);

        var drawing = await JsonOf(await _admin.GetAsync($"{Base}/modules/{Drawing}"));
        drawing.GetProperty("blocks").EnumerateArray().SelectMany(b => b.GetProperty("fields").EnumerateArray())
            .Any(f => f.GetProperty("type").GetString() == "reference" && f.GetProperty("config").TryGetProperty("relation", out var r) && r.GetString() == "detail")
            .Should().BeTrue("設計圖是設計案的明細");

        var module = await JsonOf(await _admin.GetAsync($"{Base}/modules/{Case}"));
        var fields = module.GetProperty("blocks").EnumerateArray().SelectMany(b => b.GetProperty("fields").EnumerateArray()).ToList();
        fields.Single(f => f.GetProperty("label").GetProperty("zh-TW").GetString() == "設計圖檔").GetProperty("type").GetString().Should().Be("attachment");
        var status = fields.Single(f => f.GetProperty("label").GetProperty("zh-TW").GetString() == "狀態");
        status.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("label").GetProperty("zh-TW").GetString())
            .Should().Equal("需求確認", "設計中", "客戶確認中", "待主管確認", "結案", "取消");

        var wf = await JsonOf(await _admin.GetAsync($"{Base}/modules/{Case}/workflow"));
        var readOnly = wf.GetProperty("states").EnumerateArray().Where(s => s.GetProperty("readOnly").GetBoolean())
            .Select(s => s.GetProperty("optionId").GetInt64()).ToList();
        var byLabel = status.GetProperty("options").EnumerateArray()
            .ToDictionary(o => o.GetProperty("label").GetProperty("zh-TW").GetString()!, o => o.GetProperty("id").GetInt64());
        readOnly.Should().BeEquivalentTo([byLabel["結案"], byLabel["取消"]]);
    }

    [Fact]
    public async Task Open_WithoutDesignerMapping_StillOpens()
    {
        var res = await _admin.PostAsJsonAsync($"{Apps}/packages/crm-sales/open", MapAll("sales", "manager"));
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        (await res.Content.ReadAsStringAsync()).Should().Contain("群組「designer」沒有對應");
    }

    [Theory]
    [InlineData("需求確認", "設計中", "sales01", true)]
    [InlineData("需求確認", "設計中", "manager01", true)]
    [InlineData("需求確認", "設計中", Designer, false)]
    [InlineData("設計中", "客戶確認中", Designer, true)]
    [InlineData("設計中", "客戶確認中", "sales01", true)]
    [InlineData("設計中", "客戶確認中", "manager01", false)]
    [InlineData("客戶確認中", "設計中", "sales01", true)]
    [InlineData("客戶確認中", "設計中", Designer, false)]
    [InlineData("客戶確認中", "待主管確認", "sales01", true)]
    [InlineData("客戶確認中", "待主管確認", Designer, false)]
    [InlineData("待主管確認", "結案", "manager01", true)]
    [InlineData("待主管確認", "結案", "sales01", false)]
    [InlineData("需求確認", "取消", "manager01", true)]
    [InlineData("設計中", "取消", "manager01", true)]
    [InlineData("客戶確認中", "取消", "manager01", true)]
    [InlineData("需求確認", "取消", "sales01", false)]
    [InlineData("待主管確認", "取消", "manager01", false)]
    public async Task Transitions_GroupsPerTable(string from, string to, string user, bool allowed)
    {
        await OpenCrm();
        await SetMembers(["sales01"], ["manager01"], [Designer]);
        var record = await NewCase(_app.AsUser("manager01"));
        // 用 admin 沿主線走到起點（admin 屬 MIS，不受轉移群組限制）
        foreach (var step in new[] { "設計中", "客戶確認中", "待主管確認" })
        {
            if (from == "需求確認") break;
            record = await Move(_admin, record, step);
            if (step == from) break;
        }
        var status = (await FieldKeys(_admin, Case))["狀態"];
        var res = await Put(_app.AsUser(user), record, new() { [status] = await OptionValue(_admin, Case, "狀態", to) });
        if (allowed) res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        else res.StatusCode.Should().Be(HttpStatusCode.BadRequest, await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Designer_CannotCreate_CanEditStatusAndDrawingRows()
    {
        await OpenCrm();
        await SetMembers(["sales01"], ["manager01"], [Designer]);
        var designer = _app.AsUser(Designer);
        var c = await FieldKeys(_admin, Case);
        var denied = await designer.PostAsJsonAsync($"{Base}/modules/{Case}/records", new
        {
            values = new Dictionary<string, object> { [c["案名"]] = "不該建成" }
        });
        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var record = await NewCase(_app.AsUser("manager01"));
        record = await Move(_admin, record, "設計中");
        var d = await FieldKeys(_admin, Drawing);
        var withRow = await designer.PutAsJsonAsync($"{Base}/records/{record.GetProperty("id").GetInt64()}", new
        {
            values = new Dictionary<string, object> { [c["狀態"]] = await OptionValue(_admin, Case, "狀態", "客戶確認中") },
            rowVersion = record.GetProperty("rowVersion").GetString(),
            details = new Dictionary<string, object[]>
            {
                [Drawing] = [new { values = new Dictionary<string, object> { [d["版本"]] = 1, [d["圖檔名稱"]] = "外殼-v1.png" } }]
            }
        });
        withRow.StatusCode.Should().Be(HttpStatusCode.OK, await withRow.Content.ReadAsStringAsync());
        (await Reload(record.GetProperty("id").GetInt64())).GetProperty("details").GetProperty(Drawing).GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Designer_FieldAccessReadOnly()
    {
        await OpenCrm();
        await SetMembers(["sales01"], ["manager01"], [Designer]);
        var record = await NewCase(_app.AsUser("manager01"));
        record = await Move(_admin, record, "設計中");
        var c = await FieldKeys(_admin, Case);
        var res = await Put(_app.AsUser(Designer), record, new() { [c["負責業務"]] = "sales02" });
        res.StatusCode.Should().Be(HttpStatusCode.OK, "唯讀欄位的值被忽略，不是整筆拒絕（既有 fieldAccess 行為）");
        (await Reload(record.GetProperty("id").GetInt64())).GetProperty("values").GetProperty(c["負責業務"]).GetString().Should().Be("sales01");
    }

    [Fact]
    public async Task Designer_SeesCustomerName()
    {
        await OpenCrm();
        await SetMembers(["sales01"], ["manager01"], [Designer]);
        var record = await NewCase(_app.AsUser("manager01"));
        var c = await FieldKeys(_admin, Case);
        var read = await JsonOf(await _app.AsUser(Designer).GetAsync($"{Base}/records/{record.GetProperty("id").GetInt64()}"));
        var customer = read.GetProperty("references").GetProperty(c["客戶"]);
        customer.GetProperty("title").GetString().Should().Be("宏達科技");
        customer.GetProperty("state").GetString().Should().Be("ok");
    }

    // ---- Task 2：任務規則 ----

    private const string IntakeTitle = "確認需求並交設計", DrawTitle = "依需求繪製／修改設計圖", ConfirmTitle = "將設計圖交客戶確認";

    private async Task<List<JsonElement>> TasksOf(long recordId, string title) =>
        (await JsonOf(await _admin.GetAsync($"{Base}/records/{recordId}/tasks"))).GetProperty("items").EnumerateArray()
            .Where(t => t.GetProperty("title").GetString() == title).ToList();

    private async Task Complete(JsonElement task)
    {
        var res = await _app.AsUser(task.GetProperty("assigneeUserId").GetString()).PatchAsync($"{Base}/tasks/{task.GetProperty("id").GetInt64()}",
            JsonContent.Create(new { status = "done" }));
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
    }

    private async Task<string[]> NotificationKinds(string user) =>
        (await JsonOf(await _app.AsUser(user).GetAsync($"{Base}/notifications?take=50"))).GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("kind").GetString()!).ToArray();

    private async Task OpenWithMembers()
    {
        await OpenCrm();
        await SetMembers(["sales01"], ["manager01"], [Designer]);
    }

    /// <summary>每次轉移前把上一批任務標完成；回傳最後的記錄。</summary>
    private async Task<JsonElement> GoRound(JsonElement record, string toLabel, string? completeTitle)
    {
        if (completeTitle is not null)
            foreach (var t in await TasksOf(record.GetProperty("id").GetInt64(), completeTitle)) if (t.GetProperty("status").GetString() != "done") await Complete(t);
        return await Move(_admin, record, toLabel);
    }

    [Fact]
    public async Task Create_AssignsIntakeToOwner_Notified()
    {
        await OpenWithMembers();
        var record = await NewCase(_app.AsUser("manager01"));
        var tasks = await TasksOf(record.GetProperty("id").GetInt64(), IntakeTitle);
        tasks.Should().ContainSingle().Which.GetProperty("assigneeUserId").GetString().Should().Be("sales01");
        (await NotificationKinds("sales01")).Should().Contain("taskAssigned");
    }

    [Fact]
    public async Task TwoRounds_TwoBatchesEach()
    {
        await OpenWithMembers();
        var record = await NewCase(_app.AsUser("manager01"));
        var id = record.GetProperty("id").GetInt64();
        record = await Move(_admin, record, "設計中");
        record = await GoRound(record, "客戶確認中", DrawTitle);
        record = await GoRound(record, "設計中", ConfirmTitle);
        record = await GoRound(record, "客戶確認中", DrawTitle);

        var draws = await TasksOf(id, DrawTitle);
        draws.Should().HaveCount(2).And.OnlyContain(t => t.GetProperty("assigneeUserId").GetString() == Designer);
        var confirms = await TasksOf(id, ConfirmTitle);
        confirms.Should().HaveCount(2).And.OnlyContain(t => t.GetProperty("assigneeUserId").GetString() == "sales01");
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
        db.Set<TaskItem>().Where(t => t.RecordId == id && t.RuleOccurrence != null && t.Title == DrawTitle).Select(t => t.RuleOccurrence!.Value)
            .OrderBy(x => x).ToList().Should().Equal(1, 2);
    }

    [Fact]
    public async Task Redesign_WhileDrawOpen_NoNewBatch()
    {
        await OpenWithMembers();
        var record = await NewCase(_app.AsUser("manager01"));
        var id = record.GetProperty("id").GetInt64();
        record = await Move(_admin, record, "設計中");
        record = await Move(_admin, record, "客戶確認中");           // 第一批 draw 沒完成
        record = await Move(_admin, record, "設計中");

        var draws = await TasksOf(id, DrawTitle);
        draws.Should().ContainSingle("上一輪尚未完成就不再派");
        var comments = await JsonOf(await _admin.GetAsync($"{Base}/tasks/{draws[0].GetProperty("id").GetInt64()}/comments"));
        comments.EnumerateArray().Any(c => c.GetProperty("kind").GetString() != "comment" && c.GetProperty("systemEvent").ValueKind == JsonValueKind.String)
            .Should().BeTrue("留言串有系統記錄：條件再次符合，但上一輪任務尚未完成");
    }

    [Fact]
    public async Task Comment_AttachmentAndMention()
    {
        await OpenWithMembers();
        var record = await NewCase(_app.AsUser("manager01"));
        var id = record.GetProperty("id").GetInt64();
        record = await Move(_admin, record, "設計中");
        record = await Move(_admin, record, "客戶確認中");
        var confirm = (await TasksOf(id, ConfirmTitle)).Single();
        var taskId = confirm.GetProperty("id").GetInt64();

        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
        var txt = System.Text.Encoding.UTF8.GetBytes("面板顏色再確認");
        var form = new MultipartFormDataContent { { new StringContent($"請看 @{{{Designer}}}"), "body" } };
        form.Add(new ByteArrayContent(png), "files", "annotated.png");
        form.Add(new ByteArrayContent(txt), "files", "notes.txt");
        var posted = await _app.AsUser("sales01").PostAsync($"{Base}/tasks/{taskId}/comments", form);
        posted.StatusCode.Should().Be(HttpStatusCode.Created, await posted.Content.ReadAsStringAsync());
        var comment = await JsonOf(posted);

        (await NotificationKinds(Designer)).Should().Contain("taskMentioned");
        var designer = _app.AsUser(Designer);
        var commentId = comment.GetProperty("id").GetInt64();
        var expected = new Dictionary<string, byte[]> { ["annotated.png"] = png, ["notes.txt"] = txt };
        foreach (var a in comment.GetProperty("attachments").EnumerateArray())
        {
            var down = await designer.GetAsync($"{Base}/tasks/{taskId}/comments/{commentId}/attachments/{a.GetProperty("id").GetInt64()}");
            down.StatusCode.Should().Be(HttpStatusCode.OK);
            (await down.Content.ReadAsByteArrayAsync()).Should().Equal(expected[a.GetProperty("fileName").GetString()!]);
        }
        comment.GetProperty("attachments").GetArrayLength().Should().Be(2);
    }
}
