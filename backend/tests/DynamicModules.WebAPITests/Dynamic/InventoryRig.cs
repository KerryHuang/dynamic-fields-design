using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 內建應用程式「進銷存」（apps/inventory.app.json）的測試工具：開啟、欄位代號對照、建主檔與單據、過帳、算餘額。
/// 欄位一律以 zh-TW 標籤找鍵，選項以 zh-TW 標籤找值；不依賴資料庫 Id。
/// </summary>
internal sealed class InventoryRig
{
    public const string Apps = Base + "/apps";

    /// <summary>示範人員分別放進三個群組：採購 sales01、倉管 floor01、主管 manager01。</summary>
    public const string Buyer = "sales01", Keeper = "floor01", Manager = "manager01";

    private readonly Dictionary<string, Dictionary<string, string>> _fields = new();
    private readonly Dictionary<string, JsonElement> _schemas = new();

    public HttpClient Admin { get; }
    public JsonElement App { get; }

    private InventoryRig(HttpClient admin, JsonElement app)
    {
        Admin = admin;
        App = app;
    }

    public static object GroupMap => new
    {
        groupMap = new Dictionary<string, object>
        {
            ["buyer"] = new { mode = "create" }, ["keeper"] = new { mode = "create" }, ["manager"] = new { mode = "create" }
        }
    };

    /// <summary>開啟內建套件（或已匯入的同代號套件），並把示範人員放進新建的三個群組。</summary>
    public static async Task<InventoryRig> OpenAsync(HttpClient admin, bool assignMembers = true, string packageId = "inventory",
        object? groupMap = null)
    {
        var opened = await admin.PostAsJsonAsync($"{Apps}/packages/{packageId}/open", groupMap ?? GroupMap);
        opened.StatusCode.Should().Be(HttpStatusCode.Created, await opened.Content.ReadAsStringAsync());
        var rig = new InventoryRig(admin, await JsonOf(opened));
        if (assignMembers)
        {
            await rig.AssignAsync("進銷存採購", Buyer);
            await rig.AssignAsync("進銷存倉管", Keeper);
            await rig.AssignAsync("進銷存主管", Manager);
        }
        return rig;
    }

    private async Task AssignAsync(string groupName, string userId)
    {
        var groups = await JsonOf(await Admin.GetAsync($"{Base}/admin/groups"));
        var group = groups.EnumerateArray().Single(g => g.GetProperty("nameI18n").GetProperty("zh-TW").GetString() == groupName);
        var put = await Admin.PutAsJsonAsync($"{Base}/admin/groups/{group.GetProperty("id").GetInt64()}/members", new { userIds = new[] { userId } });
        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
    }

    public string Key(string code) =>
        App.GetProperty("modules").EnumerateArray().Single(m => m.GetProperty("code").GetString() == code).GetProperty("key").GetString()!;

    private async Task<JsonElement> SchemaAsync(string code)
    {
        if (_schemas.TryGetValue(code, out var s)) return s;
        return _schemas[code] = await JsonOf(await Admin.GetAsync($"{Base}/modules/{Key(code)}"));
    }

    private static IEnumerable<JsonElement> FieldsOf(JsonElement module) =>
        module.GetProperty("blocks").EnumerateArray().SelectMany(b => b.GetProperty("fields").EnumerateArray());

    /// <summary>zh-TW 標籤 → 欄位鍵（f_&lt;Id&gt;）。</summary>
    public async Task<Dictionary<string, string>> F(string code)
    {
        if (_fields.TryGetValue(code, out var f)) return f;
        return _fields[code] = FieldsOf(await SchemaAsync(code))
            .ToDictionary(x => x.GetProperty("label").GetProperty("zh-TW").GetString()!, x => x.GetProperty("key").GetString()!);
    }

    /// <summary>下拉欄位某選項（zh-TW 標籤）的值。</summary>
    public async Task<string> Opt(string code, string fieldLabel, string optionLabel) =>
        FieldsOf(await SchemaAsync(code)).Single(x => x.GetProperty("label").GetProperty("zh-TW").GetString() == fieldLabel)
            .GetProperty("options").EnumerateArray()
            .Single(o => o.GetProperty("label").GetProperty("zh-TW").GetString() == optionLabel).GetProperty("value").GetString()!;

    public static async Task<JsonElement> PostOk(HttpClient c, string url, object body, HttpStatusCode expected = HttpStatusCode.Created)
    {
        var res = await c.PostAsJsonAsync(url, body);
        res.StatusCode.Should().Be(expected, await res.Content.ReadAsStringAsync());
        return await JsonOf(res);
    }

    public static long Id(JsonElement record) => record.GetProperty("id").GetInt64();

    public static string? Str(JsonElement record, string key) =>
        record.GetProperty("values").TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static decimal? Num(JsonElement record, string key) =>
        record.GetProperty("values").TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : null;

    public static long? Ref(JsonElement record, string key) =>
        record.GetProperty("values").TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;

    public async Task<JsonElement> GetAsync(long id, HttpClient? as_ = null)
    {
        var res = await (as_ ?? Admin).GetAsync($"{Base}/records/{id}");
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return await JsonOf(res);
    }

    // ---- 主檔 ----

    public async Task<long> ItemAsync(string name, decimal price = 100, string unit = "pcs")
    {
        var f = await F("item");
        return Id(await PostOk(Admin, $"{Base}/modules/{Key("item")}/records", new
        {
            values = new Dictionary<string, object> { [f["名稱"]] = name, [f["單位"]] = unit, [f["參考單價"]] = price }
        }));
    }

    public async Task<long> WarehouseAsync(string code, string name)
    {
        var f = await F("warehouse");
        return Id(await PostOk(Admin, $"{Base}/modules/{Key("warehouse")}/records", new
        {
            values = new Dictionary<string, object> { [f["倉庫代號"]] = code, [f["名稱"]] = name }
        }));
    }

    /// <summary>供應商與客戶是共用主檔往來對象（s2d-1f）：建一筆角色是供應商的往來對象。</summary>
    public Task<long> SupplierAsync(string name) => PartnerAsync(Admin, name, "供應商");

    /// <summary>建一筆角色是客戶的往來對象。</summary>
    public Task<long> CustomerAsync(string name) => PartnerAsync(Admin, name, "客戶");

    // ---- 單據 ----

    /// <summary>送出狀態轉移（只改狀態欄位，可附帶其他欄位）；回傳原始回應，呼叫端自行斷言。</summary>
    public async Task<HttpResponseMessage> TransitionAsync(HttpClient c, JsonElement record, string code, string optionLabel,
        Dictionary<string, object>? extra = null, string? idempotencyKey = null)
    {
        var f = await F(code);
        var values = new Dictionary<string, object>(extra ?? new()) { [f["狀態"]] = await Opt(code, "狀態", optionLabel) };
        var req = new HttpRequestMessage(HttpMethod.Put, $"{Base}/records/{Id(record)}")
        {
            Content = JsonContent.Create(new { values, rowVersion = record.GetProperty("rowVersion").GetString() })
        };
        if (idempotencyKey is not null) req.Headers.Add("Idempotency-Key", idempotencyKey);
        return await c.SendAsync(req);
    }

    public async Task<JsonElement> TransitionOk(HttpClient c, JsonElement record, string code, string optionLabel)
    {
        var res = await TransitionAsync(c, record, code, optionLabel);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return await JsonOf(res);
    }

    /// <summary>採購單草稿：一列，單價取品項的參考單價（含稅總計 = 數量 × 單價 × 1.05）。</summary>
    public async Task<JsonElement> PoDraftAsync(long supplier, long item, decimal qty, HttpClient? as_ = null)
    {
        var h = await F("po");
        var l = await F("po-line");
        return await PostOk(as_ ?? Admin, $"{Base}/modules/{Key("po")}/records", new
        {
            values = new Dictionary<string, object> { [h["供應商"]] = supplier },
            details = new Dictionary<string, object[]>
            {
                [Key("po-line")] = [new { values = new Dictionary<string, object> { [l["品項"]] = item, [l["數量"]] = qty } }]
            }
        });
    }

    /// <summary>送出採購單簽核（目標「已確認」）：直接確認回 200，建立簽核實例回 201。</summary>
    public async Task<HttpResponseMessage> SubmitPoAsync(HttpClient c, JsonElement po) =>
        await c.PostAsJsonAsync($"{Base}/records/{Id(po)}/approval/submit", new
        {
            to = long.Parse((await Opt("po", "狀態", "已確認"))[4..]),
            recordRowVersion = po.GetProperty("rowVersion").GetString()
        });

    /// <summary>目前進行中的簽核實例（沒有則為 null）。</summary>
    public async Task<JsonElement?> CurrentApprovalAsync(long recordId, HttpClient? as_ = null)
    {
        var res = await (as_ ?? Admin).GetAsync($"{Base}/records/{recordId}/approval");
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return (await JsonOf(res)).TryGetProperty("current", out var current) && current.ValueKind == JsonValueKind.Object ? current : null;
    }

    /// <summary>以 approver 同意目前實例中指派給他的未處理任務。</summary>
    public async Task<HttpResponseMessage> ApprovePoAsync(HttpClient approver, string userId, long recordId)
    {
        var current = (await CurrentApprovalAsync(recordId))!.Value;
        var task = current.GetProperty("stages").EnumerateArray().SelectMany(s => s.GetProperty("tasks").EnumerateArray())
            .First(t => t.GetProperty("status").GetString() == "open" && t.GetProperty("assigneeUserId").GetString() == userId);
        return await approver.PostAsJsonAsync($"{Base}/approval-tasks/{task.GetProperty("id").GetInt64()}/approve",
            new { rowVersion = current.GetProperty("rowVersion").GetString() });
    }

    /// <summary>
    /// 把採購單確認：送出簽核；有簽核實例就由 approver 逐關同意（測試沒建組織時各關都由備援的進銷存主管群組接手）。
    /// 採購單不能再用 PUT 直接改成已確認。
    /// </summary>
    public async Task<JsonElement> ConfirmPoAsync(HttpClient submitter, JsonElement draft, HttpClient approver, string approverId)
    {
        var submit = await SubmitPoAsync(submitter, draft);
        submit.IsSuccessStatusCode.Should().BeTrue(await submit.Content.ReadAsStringAsync());
        for (var guard = 0; guard < 5 && await CurrentApprovalAsync(Id(draft)) is not null; guard++)
        {
            var ok = await ApprovePoAsync(approver, approverId, Id(draft));
            ok.StatusCode.Should().Be(HttpStatusCode.OK, await ok.Content.ReadAsStringAsync());
        }
        return await GetAsync(Id(draft));
    }

    /// <summary>盤點調整單（草稿）：一列，數量可正可負。</summary>
    public async Task<JsonElement> AdjustDraftAsync(long warehouse, long item, decimal qty, string reason = "盤點", HttpClient? as_ = null)
    {
        var h = await F("adjust");
        var l = await F("adjust-line");
        return await PostOk(as_ ?? Admin, $"{Base}/modules/{Key("adjust")}/records", new
        {
            values = new Dictionary<string, object> { [h["倉庫"]] = warehouse, [h["原因"]] = await Opt("adjust", "原因", reason) },
            details = new Dictionary<string, object[]>
            {
                [Key("adjust-line")] = [new { values = new Dictionary<string, object> { [l["品項"]] = item, [l["調整數量"]] = qty } }]
            }
        });
    }

    /// <summary>以期初調整單入帳（已過帳）。</summary>
    public async Task OpeningAsync(long warehouse, long item, decimal qty) =>
        await TransitionOk(Admin, await AdjustDraftAsync(warehouse, item, qty, "期初"), "adjust", "已過帳");

    public async Task<HttpRequestMessage> ShipmentRequestAsync(long customer, long warehouse, IEnumerable<(long Item, decimal Qty)> lines)
    {
        var h = await F("shipment");
        var l = await F("shipment-line");
        return new HttpRequestMessage(HttpMethod.Post, $"{Base}/modules/{Key("shipment")}/records")
        {
            Content = JsonContent.Create(new
            {
                values = new Dictionary<string, object> { [h["客戶"]] = customer, [h["出庫倉"]] = warehouse },
                details = new Dictionary<string, object[]>
                {
                    [Key("shipment-line")] = lines.Select(x => (object)new
                    {
                        values = new Dictionary<string, object> { [l["品項"]] = x.Item, [l["數量"]] = x.Qty }
                    }).ToArray()
                }
            })
        };
    }

    public async Task<JsonElement> ShipmentDraftAsync(long customer, long warehouse, long item, decimal qty, HttpClient? as_ = null)
    {
        var res = await (as_ ?? Admin).SendAsync(await ShipmentRequestAsync(customer, warehouse, [(item, qty)]));
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return await JsonOf(res);
    }

    public async Task<JsonElement> ReceiptDraftAsync(long supplier, long warehouse, long item, decimal qty, HttpClient? as_ = null)
    {
        var h = await F("receipt");
        var l = await F("receipt-line");
        return await PostOk(as_ ?? Admin, $"{Base}/modules/{Key("receipt")}/records", new
        {
            values = new Dictionary<string, object> { [h["供應商"]] = supplier, [h["入庫倉"]] = warehouse },
            details = new Dictionary<string, object[]>
            {
                [Key("receipt-line")] = [new { values = new Dictionary<string, object> { [l["品項"]] = item, [l["數量"]] = qty } }]
            }
        });
    }

    // ---- 庫存異動 ----

    /// <summary>全部庫存異動（依 Id 由小到大，也就是提交順序）。</summary>
    public async Task<List<JsonElement>> MovesAsync()
    {
        var all = new List<JsonElement>();
        for (var page = 1; ; page++)
        {
            var res = await Admin.GetAsync($"{Base}/modules/{Key("move")}/records/paged?currentPage={page}&pagesSize=200");
            res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
            var items = (await JsonOf(res)).GetProperty("items").EnumerateArray().ToList();
            all.AddRange(items);
            if (items.Count < 200) break;
        }
        return all.OrderBy(Id).ToList();
    }

    public async Task<decimal> BalanceAsync(long item, long warehouse)
    {
        var m = await F("move");
        return (await MovesAsync())
            .Where(x => Ref(x, m["品項"]) == item && Ref(x, m["倉庫"]) == warehouse)
            .Sum(x => Num(x, m["數量（入正出負）"]) ?? 0);
    }
}
