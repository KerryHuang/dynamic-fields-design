using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Application.Dynamic.McpService.Tools;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Mcp;
using FluentAssertions;
using ModelContextProtocol.Client;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;
using static DynamicModules.WebAPITests.Dynamic.Mcp.McpTestKit;
using Record = DynamicModules.Domain.Entities.Dynamic.Record;

namespace DynamicModules.WebAPITests.Dynamic.Mcp;

/// <summary>
/// 記錄寫入的 REST／MCP 對照（設計 §12 mcp2a 驗收、mcp2a 計畫 Task 4）：同一位虛構使用者一邊用 REST（cookie 身分）、一邊用 MCP
/// （<c>business.write</c> 權杖）送同樣的值，比對記錄值與明細、歷程、事件事實、任務規則、檢查失敗、版本過期、資料範圍與權限。
/// 造模：單據模組有自動編號、公式、負責人、跨記錄檢查（同品項金額加總 ≥ 0）、建立時與修改時各一個有條件的事件效果（在異動模組新增一筆）、
/// 建立時的任務規則，以及一個明細模組。權限照 <c>McpParityTests.SetSales</c> 的寫法。
/// </summary>
[Trait("Category", "Postgres")]
public class McpWriteParityTests : IAsyncLifetime
{
    private const string User = "sales01";
    private TestApp _app = null!;
    private HttpClient _admin = null!, _rest = null!;
    private McpClient _mcp = null!;
    private string _doc = "", _child = "", _move = "";
    private string _title = "", _item = "", _amount = "", _owner = "", _number = "", _formula = "", _childTitle = "", _childParent = "", _qty = "";
    private long _numberId;

    public async Task InitializeAsync()
    {
        _app = NewApp();
        EnableMcp(_app);
        // 示範人員是 Demo 帳號、要改密碼，不能用權杖；這裡改成一般帳號（測試直接寫）
        EnsureUser(_app, User, u => (u.IsDemoAccount, u.MustChangePassword) = (false, false));
        _admin = _app.AsUser();
        _rest = _app.AsUser(User);

        _doc = await CreateModule(_admin);
        _title = await TitleKey(_admin, _doc);
        var item = await AddField(_admin, _doc, "text", new { isFilterable = true });
        var amount = await AddField(_admin, _doc, "decimal", new { scale = 2, isFilterable = true });
        var owner = await AddField(_admin, _doc, "user");
        var number = await AddField(_admin, _doc, "autonumber", new { format = "T-{0000}" });
        var formula = await AddField(_admin, _doc, "formula", new { formula = $"{{{amount.Id}}} * 2", resultType = "number", scale = 2 });
        (_item, _amount, _owner, _number, _formula, _numberId) = (item.Key, amount.Key, owner.Key, number.Key, formula.Key, number.Id);

        _child = await CreateModule(_admin);
        (_, _childParent, _) = await AddField(_admin, _child, "reference", new { refTarget = new { kind = "module", key = _doc }, relation = "detail" });
        (_, _qty, _) = await AddField(_admin, _child, "decimal", new { scale = 2 });
        _childTitle = await TitleKey(_admin, _child);

        _move = await CreateModule(_admin);
        var moveTitle = long.Parse((await TitleKey(_admin, _move))[2..]);
        foreach (var m in new[] { _doc, _child, _move }) await Activate(_admin, m);

        // 有事件事實的記錄只能變更狀態（EventFactLock），所以事件加條件：建立時品項 A、修改後品項 B 才觸發，修改的案例用其他品項建立
        foreach (var (on, when) in new[] { ("create", "A"), ("update", "B") })
        {
            var ev = await _admin.PostAsJsonAsync($"{Base}/events", new
            {
                key = $"ev-{on}-" + Guid.NewGuid().ToString("N")[..8], name = Zh(on), sourceModuleKey = _doc, active = true,
                definition = new
                {
                    trigger = new { on }, when = new { all = new[] { new { field = item.Id, op = "eq", value = when } } },
                    effects = new[] { new { code = "log", kind = "create", targetModuleKey = _move, set = new object[] { new { to = moveTitle, @const = on } } } },
                },
            });
            ev.StatusCode.Should().Be(HttpStatusCode.Created, await ev.Content.ReadAsStringAsync());
        }
        var check = await _admin.PostAsJsonAsync($"{Base}/checks", new
        {
            key = "ck-" + Guid.NewGuid().ToString("N")[..8], name = Zh("min"), moduleKey = _doc, active = true,
            definition = new
            {
                groupBy = new[] { item.Id }, function = "sum", fieldId = amount.Id, assert = new { op = "gte", limit = new { @const = 0 } },
                message = new Dictionary<string, string> { ["zh-TW"] = "金額加總不得為負", ["en-US"] = "Total must not be negative" },
            },
        });
        check.StatusCode.Should().Be(HttpStatusCode.Created, await check.Content.ReadAsStringAsync());
        var rule = await _admin.PostAsJsonAsync($"{Base}/modules/{_doc}/task-rules", new
        {
            key = "tr-" + Guid.NewGuid().ToString("N")[..8], name = Zh("follow"), active = true, repeat = "once", trigger = new { on = "onCreate" },
            when = (object?)null, tasks = new[] { new { code = "a", title = Zh("跟進單據"), assigneeFieldId = owner.Id, dueOffsetDays = 0 } },
        });
        rule.StatusCode.Should().Be(HttpStatusCode.Created, await rule.Content.ReadAsStringAsync());

        await SetSales(scope: "all", add: true);
        _mcp = await ConnectAsync(_app, IssueToken(_app, User, McpScopes.Join([McpScopes.BusinessRead, McpScopes.BusinessWrite])).Raw);
    }

    public async Task DisposeAsync()
    {
        await _mcp.DisposeAsync();
        _app.Dispose();
    }

    private static Dictionary<string, string> Zh(string text) => new() { ["zh-TW"] = text };

    private async Task SetSales(string scope, bool add)
    {
        var groups = await JsonOf(await _admin.GetAsync($"{Base}/groups"));
        var salesId = groups.EnumerateArray().Single(g => g.GetProperty("key").GetString() == "sales").GetProperty("id").GetInt64();
        var put = await _admin.PutAsJsonAsync($"{Base}/modules/{_doc}/permissions", new
        {
            groups = new[]
            {
                new
                {
                    groupId = salesId, scope,
                    privileges = new { read = true, add, edit = true, delete = false, printExport = false, pricing = true, approve = false },
                },
            },
            users = Array.Empty<object>(), fields = Array.Empty<object>(), options = Array.Empty<object>(),
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
    }

    private Dictionary<string, object?> Values(string title, decimal amount, string item = "A") =>
        new() { [_title] = title, [_item] = item, [_amount] = amount, [_owner] = "sales02" };

    private object Rows() => new Dictionary<string, object[]>
    {
        [_child] =
        [
            new { values = new Dictionary<string, object> { [_childTitle] = "第一行", [_qty] = 1 } },
            new { values = new Dictionary<string, object> { [_childTitle] = "第二行", [_qty] = 2.5 } },
        ],
    };

    private static string NewIdem() => "k-" + Guid.NewGuid().ToString("N");

    private async Task<HttpResponseMessage> RestCreate(Dictionary<string, object?> values, object? details = null) =>
        await _rest.PostAsJsonAsync($"{Base}/modules/{_doc}/records", new { values, details });

    private async Task<JsonElement> RestCreated(Dictionary<string, object?> values, object? details = null)
    {
        var res = await RestCreate(values, details);
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return await JsonOf(res);
    }

    private async Task<JsonElement> Mcp(string tool, object args)
    {
        var dict = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(args));
        return (await _mcp.CallToolAsync(tool, dict)).StructuredContent!.Value;
    }

    private static string? Code(JsonElement r) => r.TryGetProperty("error", out var e) ? e.GetProperty("code").GetString() : null;

    private static JsonElement Data(JsonElement r)
    {
        Code(r).Should().BeNull(r.GetRawText());
        return r.GetProperty("data");
    }

    private static string Canon(JsonElement e) => JsonSerializer.Serialize(e, new JsonSerializerOptions
        { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    /// <summary>values 的對照形式：排除會不同的鍵（自動編號流水號、明細列指向表頭的參照）。</summary>
    private static Dictionary<string, string> Shape(JsonElement values, params string[] except) =>
        values.EnumerateObject().Where(p => !except.Contains(p.Name)).ToDictionary(p => p.Name, p => Canon(p.Value));

    private List<Dictionary<string, string>> DetailShape(JsonElement record) =>
        record.GetProperty("details").GetProperty(_child).EnumerateArray()
            .Select(row => Shape(row.GetProperty("values"), _childParent)).OrderBy(s => s[_childTitle]).ToList();

    private List<(RecordHistoryOperation Op, long? Field, string? Old, string? New, string User)> History(long recordId) =>
        Query(_app, db => db.Set<RecordHistory>().Where(h => h.RecordId == recordId && h.FieldId != _numberId).AsEnumerable()
            .Select(h => (h.Operation, h.FieldId, h.OldValue, h.NewValue, h.ChangedUserId))
            .OrderBy(h => h.Operation).ThenBy(h => h.FieldId ?? 0).ToList());

    private List<EventFact> Facts(long recordId) =>
        Query(_app, db => db.Set<EventFact>().Where(f => f.SourceRecordId == recordId).AsEnumerable().OrderBy(f => f.EventKey, StringComparer.Ordinal).ToList());

    private static List<string> FactShape(List<EventFact> facts) => facts.Select(f => $"{f.EventKey}/{f.EffectCode}").ToList();

    private int Count(string module) => Query(_app, db =>
    {
        var id = db.Set<Module>().Single(m => m.Key == module).Id;
        return db.Set<Record>().Count(r => r.ModuleId == id);
    });

    [Fact]
    public async Task Create_SameRecord()
    {
        var rest = await RestCreated(Values("對照單", 10), Rows());
        var mcp = Data(await Mcp("create_record", new { moduleKey = _doc, values = Values("對照單", 10), details = Rows(), idempotencyKey = NewIdem() }));

        Shape(mcp.GetProperty("values"), _number).Should().BeEquivalentTo(Shape(rest.GetProperty("values"), _number));
        mcp.GetProperty("values").GetProperty(_formula).GetDecimal().Should().Be(20m, "公式照算");
        mcp.GetProperty("values").GetProperty(_number).GetString().Should().StartWith("T-", "自動編號照取");
        DetailShape(mcp).Should().BeEquivalentTo(DetailShape(rest), o => o.WithStrictOrdering());
        mcp.GetProperty("ownerUserId").GetString().Should().Be(User);
    }

    [Fact]
    public async Task Create_SameHistory()
    {
        var rest = await RestCreated(Values("歷程單", 5));
        var mcp = Data(await Mcp("create_record", new { moduleKey = _doc, values = Values("歷程單", 5), idempotencyKey = NewIdem() }));

        var restHistory = History(rest.GetProperty("id").GetInt64());
        var mcpHistory = History(mcp.GetProperty("id").GetInt64());
        restHistory.Should().NotBeEmpty();
        mcpHistory.Should().Equal(restHistory);
        mcpHistory.Should().OnlyContain(h => h.User == User, "歷程記在權杖擁有者名下（D7）");
    }

    [Fact]
    public async Task Create_SameEventFacts()
    {
        var moves = Count(_move);
        var rest = await RestCreated(Values("事件單", 1));
        var afterRest = Count(_move);
        var key = NewIdem();
        var mcp = Data(await Mcp("create_record", new { moduleKey = _doc, values = Values("事件單", 1), idempotencyKey = key }));

        var restFacts = Facts(rest.GetProperty("id").GetInt64());
        var mcpFacts = Facts(mcp.GetProperty("id").GetInt64());
        restFacts.Should().ContainSingle();
        FactShape(mcpFacts).Should().Equal(FactShape(restFacts));
        mcpFacts.Should().OnlyContain(f => f.OperationId == key, "MCP 的 operation id 是工具參數的冪等鍵");
        (Count(_move) - afterRest).Should().Be(afterRest - moves, "效果寫到目標模組的筆數相同");
    }

    [Fact]
    public async Task Create_SameTaskRuleTasks()
    {
        var rest = await RestCreated(Values("任務單", 1));
        var mcp = Data(await Mcp("create_record", new { moduleKey = _doc, values = Values("任務單", 1), idempotencyKey = NewIdem() }));

        List<(string Title, string? Assignee)> Tasks(long recordId) => Query(_app, db => db.Set<TaskItem>().Where(t => t.RecordId == recordId)
            .AsEnumerable().Select(t => (t.Title, t.AssigneeUserId)).OrderBy(t => t.Title, StringComparer.Ordinal).ToList());
        var restTasks = Tasks(rest.GetProperty("id").GetInt64());
        restTasks.Should().ContainSingle();
        Tasks(mcp.GetProperty("id").GetInt64()).Should().Equal(restTasks);
    }

    [Fact]
    public async Task Create_CheckFails_SameMessage_NoWrite()
    {
        var (docs, facts) = (Count(_doc), Query(_app, db => db.Set<EventFact>().Count()));
        var rest = await RestCreate(Values("負數單", -5, item: "Z"));
        // 跨記錄檢查失敗是記錄層的驗證錯誤（REST 400、錯誤鍵 _record），MCP 對成 invalid、同一個欄位鍵與訊息
        rest.StatusCode.Should().Be(HttpStatusCode.BadRequest, await rest.Content.ReadAsStringAsync());
        var restMessage = (await JsonOf(rest)).GetProperty("errors").GetProperty("_record")[0].GetString();

        var mcp = await Mcp("create_record", new { moduleKey = _doc, values = Values("負數單", -5, item: "Z"), idempotencyKey = NewIdem() });

        Code(mcp).Should().Be(McpErrorCodes.Invalid);
        var error = mcp.GetProperty("error");
        error.GetProperty("fields").GetProperty("_record").GetString().Should().Be(restMessage);
        error.GetProperty("message").GetString().Should().Be(restMessage);
        Count(_doc).Should().Be(docs, "兩邊都沒寫入");
        Query(_app, db => db.Set<EventFact>().Count()).Should().Be(facts, "兩邊都沒有事件事實");
    }

    [Fact]
    public async Task Update_Same()
    {
        var a = await RestCreated(Values("修改單", 10, item: "U"));
        var b = await RestCreated(Values("修改單", 10, item: "U"));
        var change = new Dictionary<string, object?> { [_amount] = 30, [_item] = "B" };

        var put = await _rest.PutAsJsonAsync($"{Base}/records/{a.GetProperty("id").GetInt64()}",
            new { values = change, rowVersion = a.GetProperty("rowVersion").GetString() });
        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
        var mcp = Data(await Mcp("update_record", new
        {
            recordId = b.GetProperty("id").GetInt64(), rowVersion = b.GetProperty("rowVersion").GetString(), values = change, idempotencyKey = NewIdem(),
        }));

        Shape(mcp.GetProperty("values"), _number).Should().BeEquivalentTo(Shape((await JsonOf(put)).GetProperty("values"), _number));
        List<(long? Field, string? Old, string? New, string User)> Updates(long id) =>
            History(id).Where(h => h.Op == RecordHistoryOperation.Update).Select(h => (h.Field, h.Old, h.New, h.User)).ToList();
        var restUpdates = Updates(a.GetProperty("id").GetInt64());
        restUpdates.Should().NotBeEmpty();
        Updates(b.GetProperty("id").GetInt64()).Should().Equal(restUpdates);
        FactShape(Facts(b.GetProperty("id").GetInt64())).Should().Equal(FactShape(Facts(a.GetProperty("id").GetInt64())));
        Facts(b.GetProperty("id").GetInt64()).Should().ContainSingle("修改後品項 B 觸發修改事件");
    }

    [Fact]
    public async Task Update_Stale()
    {
        var created = await RestCreated(Values("過期單", 1, item: "S"));
        var id = created.GetProperty("id").GetInt64();
        var old = created.GetProperty("rowVersion").GetString();
        (await _rest.PutAsJsonAsync($"{Base}/records/{id}", new { values = new Dictionary<string, object?> { [_amount] = 2 }, rowVersion = old }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var rest = await _rest.PutAsJsonAsync($"{Base}/records/{id}", new { values = new Dictionary<string, object?> { [_amount] = 3 }, rowVersion = old });
        var mcp = await Mcp("update_record", new { recordId = id, rowVersion = old, values = new Dictionary<string, object?> { [_amount] = 3 }, idempotencyKey = NewIdem() });

        rest.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await JsonOf(rest)).GetProperty("code").GetString().Should().Be("stale");
        Code(mcp).Should().Be(McpErrorCodes.Stale);
    }

    [Fact]
    public async Task Update_DataScope_NotFound()
    {
        var res = await _admin.PostAsJsonAsync($"{Base}/modules/{_doc}/records", new { values = Values("總部單", 1, item: "S") });
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        var other = await JsonOf(res);
        await SetSales(scope: "own", add: true);
        var id = other.GetProperty("id").GetInt64();
        var rowVersion = other.GetProperty("rowVersion").GetString();

        var rest = await _rest.PutAsJsonAsync($"{Base}/records/{id}", new { values = new Dictionary<string, object?> { [_amount] = 9 }, rowVersion });
        var mcp = await Mcp("update_record", new { recordId = id, rowVersion, values = new Dictionary<string, object?> { [_amount] = 9 }, idempotencyKey = NewIdem() });

        rest.StatusCode.Should().Be(HttpStatusCode.NotFound);
        Code(mcp).Should().Be(McpErrorCodes.NotFound);
    }

    [Fact]
    public async Task Create_NoAddPermission_Forbidden()
    {
        await SetSales(scope: "all", add: false);
        var docs = Count(_doc);

        var rest = await RestCreate(Values("無權單", 1));
        var mcp = await Mcp("create_record", new { moduleKey = _doc, values = Values("無權單", 1), idempotencyKey = NewIdem() });

        rest.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        Code(mcp).Should().Be(McpErrorCodes.Forbidden);
        Count(_doc).Should().Be(docs);
    }
}
