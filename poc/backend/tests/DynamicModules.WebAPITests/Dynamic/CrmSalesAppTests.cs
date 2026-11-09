using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 出貨的內建應用程式「CRM 與銷售」（apps/crm-sales.app.json）：從空系統開啟，走完 潛在客戶 → 客戶＋商機 → 報價 → 訂單，
/// 以及列印、併發轉換、關閉／重開。程式碼裡沒有任何 CRM 專用邏輯，這些行為全來自套件宣告。
/// </summary>
public class CrmSalesAppTests : IDisposable
{
    private const string Apps = Base + "/apps";

    private readonly TestApp _app = new();
    private readonly HttpClient _admin;

    public CrmSalesAppTests() => _admin = _app.AsUser();

    public void Dispose() => _app.Dispose();

    private static readonly object CreateGroups = new
    {
        groupMap = new Dictionary<string, object> { ["sales"] = new { mode = "create" }, ["manager"] = new { mode = "create" } }
    };

    private async Task<JsonElement> OpenCrm()
    {
        var opened = await _admin.PostAsJsonAsync($"{Apps}/packages/crm-sales/open", CreateGroups);
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

    private static string? Str(JsonElement record, string key) =>
        record.GetProperty("values").TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static async Task<JsonElement> Transition(HttpClient c, JsonElement record, string statusKey, string optionValue)
    {
        var res = await c.PutAsJsonAsync($"{Base}/records/{record.GetProperty("id").GetInt64()}", new
        {
            values = new Dictionary<string, object> { [statusKey] = optionValue },
            rowVersion = record.GetProperty("rowVersion").GetString()
        });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return await JsonOf(res);
    }

    private static string ConvertUrl(JsonElement source, string conversion) =>
        $"{Base}/records/{source.GetProperty("id").GetInt64()}/conversions/{conversion}/convert";

    /// <summary>客戶改用共用主檔往來對象（共用主檔設計 §11.1），模組鍵固定。</summary>
    private const string Partner = BuiltInMasterTests.Partner;

    /// <summary>建一個角色是客戶的往來對象（CRM 的客戶欄位只收客戶或兩者）。</summary>
    private async Task<JsonElement> NewPartner(string name, string? phone = null, string role = "客戶")
    {
        var cu = await FieldKeys(_admin, Partner);
        var values = new Dictionary<string, object> { [cu["名稱"]] = name, [cu["角色"]] = await OptionValue(_admin, Partner, "角色", role) };
        if (phone is not null) values[cu["電話"]] = phone;
        return await PostOk(_admin, $"{Base}/modules/{Partner}/records", new { values });
    }

    private static string Key(JsonElement app, string code) =>
        app.GetProperty("modules").EnumerateArray().Single(m => m.GetProperty("code").GetString() == code).GetProperty("key").GetString()!;

    /// <summary>一張已核准的報價單：兩個品項，數量各 10。回傳（報價單、明細列 id、欄位對照）。</summary>
    private async Task<(JsonElement Quote, long[] LineIds, string QuoteKey, string LineKey)> ApprovedQuote(JsonElement app, string customerName = "宏達科技")
    {
        string product = Key(app, "product"), quote = Key(app, "quote"), quoteLine = Key(app, "quote-line");
        var prod = await FieldKeys(_admin, product);
        var q = await FieldKeys(_admin, quote);
        var ql = await FieldKeys(_admin, quoteLine);

        var c = await NewPartner(customerName, "02-1234-5678");
        var p1 = await PostOk(_admin, $"{Base}/modules/{product}/records", new
        {
            values = new Dictionary<string, object> { [prod["料號"]] = "P-001", [prod["名稱"]] = "感測器", [prod["單位"]] = "個", [prod["標準單價"]] = 1200 }
        });
        var p2 = await PostOk(_admin, $"{Base}/modules/{product}/records", new
        {
            values = new Dictionary<string, object> { [prod["料號"]] = "P-002", [prod["名稱"]] = "控制器", [prod["單位"]] = "台", [prod["標準單價"]] = 5000 }
        });

        var created = await PostOk(_admin, $"{Base}/modules/{quote}/records", new
        {
            values = new Dictionary<string, object> { [q["客戶"]] = c.GetProperty("id").GetInt64() },
            details = new Dictionary<string, object[]>
            {
                [quoteLine] =
                [
                    new { values = new Dictionary<string, object> { [ql["產品"]] = p1.GetProperty("id").GetInt64(), [ql["數量"]] = 10, [ql["單價"]] = 1200 } },
                    new { values = new Dictionary<string, object> { [ql["產品"]] = p2.GetProperty("id").GetInt64(), [ql["數量"]] = 10, [ql["單價"]] = 5000 } },
                ]
            }
        });
        var status = q["狀態"];
        var pending = await Transition(_admin, created, status, await OptionValue(_admin, quote, "狀態", "待核准"));
        var approved = await Transition(_admin, pending, status, await OptionValue(_admin, quote, "狀態", "已核准"));
        var lines = created.GetProperty("details").GetProperty(quoteLine).EnumerateArray().Select(l => l.GetProperty("id").GetInt64()).ToArray();
        return (approved, lines, quote, quoteLine);
    }

    [Fact]
    public async Task NotOpened_NothingAppears_AndOpeningFromAnEmptySystemWorks()
    {
        (await JsonOf(await _admin.GetAsync(Apps))).GetArrayLength().Should().Be(0);
        (await JsonOf(await _admin.GetAsync($"{Base}/menu"))).GetArrayLength().Should().Be(0);
        (await _admin.GetAsync($"{Base}/modules/crm-quote")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var app = await OpenCrm();
        app.GetProperty("modules").GetArrayLength().Should().Be(8, "客戶與聯絡人改用共用主檔，不再是 CRM 自己的模組");
        app.GetProperty("modules").EnumerateArray().Select(m => m.GetProperty("key").GetString()).Should().OnlyContain(k => k!.StartsWith("crm-"));
        (await JsonOf(await _admin.GetAsync(Apps))).EnumerateArray().Select(a => a.GetProperty("key").GetString())
            .Should().BeEquivalentTo(["crm-sales", "partner", "contact"], "開 CRM 時自動開啟它用到的共用主檔");
        (await JsonOf(await _admin.GetAsync($"{Base}/menu"))).GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task LeadToCustomerAndOpportunity_ToQuote_ToPartialOrders_WholeFlow()
    {
        var app = await OpenCrm();
        string lead = Key(app, "lead"), customer = Partner, opportunity = Key(app, "opportunity"), order = Key(app, "order"),
            orderLine = Key(app, "order-line"), quote = Key(app, "quote");
        var l = await FieldKeys(_admin, lead);
        var cu = await FieldKeys(_admin, customer);
        var op = await FieldKeys(_admin, opportunity);
        var o = await FieldKeys(_admin, order);
        var ol = await FieldKeys(_admin, orderLine);

        // 潛在客戶：狀態機擋下還沒「已確認」就轉換
        var newLead = await PostOk(_admin, $"{Base}/modules/{lead}/records", new
        {
            values = new Dictionary<string, object> { [l["姓名"]] = "王小明", [l["公司"]] = "宏達科技", [l["電話"]] = "02-1111-2222" }
        });
        (await _admin.PostAsJsonAsync(ConvertUrl(newLead, "crm-lead-to-customer"), new { })).StatusCode.Should().NotBe(HttpStatusCode.Created);
        var contacted = await Transition(_admin, newLead, l["狀態"], await OptionValue(_admin, lead, "狀態", "已聯繫"));
        var qualified = await Transition(_admin, contacted, l["狀態"], await OptionValue(_admin, lead, "狀態", "已確認"));

        // 轉客戶＋商機；同一筆潛在客戶每種轉換只能一次
        var newCustomer = await PostOk(_admin, ConvertUrl(qualified, "crm-lead-to-customer"), new { });
        Str(newCustomer, cu["名稱"]).Should().Be("宏達科技");
        Str(newCustomer, cu["電話"]).Should().Be("02-1111-2222");
        Str(newCustomer, cu["角色"]).Should().Be(await OptionValue(_admin, customer, "角色", "客戶"), "轉換固定把角色設為客戶");
        newCustomer.GetProperty("values").GetProperty(cu["來源潛在客戶"]).GetInt64().Should().Be(qualified.GetProperty("id").GetInt64());
        var opp = await PostOk(_admin, ConvertUrl(qualified, "crm-lead-to-opportunity"), new { });
        Str(opp, op["名稱"]).Should().Be("王小明");
        (await _admin.PostAsJsonAsync(ConvertUrl(qualified, "crm-lead-to-customer"), new { })).StatusCode
            .Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.Conflict);

        // 商機到提案階段後，指定客戶，轉報價單
        var withCustomer = await _admin.PutAsJsonAsync($"{Base}/records/{opp.GetProperty("id").GetInt64()}", new
        {
            values = new Dictionary<string, object> { [op["客戶"]] = newCustomer.GetProperty("id").GetInt64() },
            rowVersion = opp.GetProperty("rowVersion").GetString()
        });
        withCustomer.StatusCode.Should().Be(HttpStatusCode.OK, await withCustomer.Content.ReadAsStringAsync());
        var oppNow = await JsonOf(withCustomer);
        (await _admin.PostAsJsonAsync(ConvertUrl(oppNow, "crm-opportunity-to-quote"), new { })).StatusCode
            .Should().NotBe(HttpStatusCode.Created, "階段還在初步接觸，不能轉報價");
        var qual = await Transition(_admin, oppNow, op["階段"], await OptionValue(_admin, opportunity, "階段", "需求確認"));
        var prop = await Transition(_admin, qual, op["階段"], await OptionValue(_admin, opportunity, "階段", "提案報價"));
        var q = await PostOk(_admin, ConvertUrl(prop, "crm-opportunity-to-quote"), new { });
        var qk = await FieldKeys(_admin, quote);
        Str(q, qk["客戶名稱"]).Should().Be("宏達科技", "選客戶後客戶資料由系統帶入");

        // 報價單：加明細後走核准；核准後唯讀
        string productKey = Key(app, "product"), quoteLine = Key(app, "quote-line");
        var pk = await FieldKeys(_admin, productKey);
        var qlk = await FieldKeys(_admin, quoteLine);
        var prod = await PostOk(_admin, $"{Base}/modules/{productKey}/records", new
        {
            values = new Dictionary<string, object> { [pk["料號"]] = "P-9", [pk["名稱"]] = "馬達", [pk["單位"]] = "具", [pk["標準單價"]] = 3000 }
        });
        var put = await _admin.PutAsJsonAsync($"{Base}/records/{q.GetProperty("id").GetInt64()}", new
        {
            values = new Dictionary<string, object>(),
            rowVersion = q.GetProperty("rowVersion").GetString(),
            details = new Dictionary<string, object[]>
            {
                [quoteLine] = [new { values = new Dictionary<string, object> { [qlk["產品"]] = prod.GetProperty("id").GetInt64(), [qlk["數量"]] = 10 } }]
            }
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
        q = await JsonOf(put);
        var fresh = await JsonOf(await _admin.GetAsync($"{Base}/records/{q.GetProperty("id").GetInt64()}"));
        var lineRow = fresh.GetProperty("details").GetProperty(quoteLine)[0];
        lineRow.GetProperty("values").GetProperty(qlk["品名"]).GetString().Should().Be("馬達", "選產品後品名由系統帶入");
        lineRow.GetProperty("values").GetProperty(qlk["單價"]).GetDecimal().Should().Be(3000m, "選產品後單價由系統帶入");
        lineRow.GetProperty("values").GetProperty(qlk["金額"]).GetDecimal().Should().Be(30000m);
        var pending = await Transition(_admin, fresh, qk["狀態"], await OptionValue(_admin, quote, "狀態", "待核准"));
        var approved = await Transition(_admin, pending, qk["狀態"], await OptionValue(_admin, quote, "狀態", "已核准"));
        approved.GetProperty("values").GetProperty(qk["含稅總計"]).GetDecimal().Should().Be(31500m);
        var edit = await _admin.PutAsJsonAsync($"{Base}/records/{approved.GetProperty("id").GetInt64()}", new
        {
            values = new Dictionary<string, object> { [qk["備註"]] = "核准後想改" }, rowVersion = approved.GetProperty("rowVersion").GetString()
        });
        if (edit.IsSuccessStatusCode) Str(await JsonOf(edit), qk["備註"]).Should().BeNull("核准後的報價單其他欄位不能改");
        (await _admin.DeleteAsync($"{Base}/records/{approved.GetProperty("id").GetInt64()}?rowVersion={Uri.EscapeDataString(approved.GetProperty("rowVersion").GetString()!)}"))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);

        // 報價單 → 訂單：分兩次轉（4 + 6），第三次超過剩餘量被擋
        var lineId = fresh.GetProperty("details").GetProperty(quoteLine)[0].GetProperty("id").GetInt64();
        var first = await PostOk(_admin, ConvertUrl(approved, "crm-quote-to-order"), new { lines = new[] { new { sourceLineId = lineId, quantity = 4 } } });
        Str(first, o["客戶名稱"]).Should().Be("宏達科技");
        first.GetProperty("values").GetProperty(o["來源報價單"]).GetInt64().Should().Be(approved.GetProperty("id").GetInt64());
        await PostOk(_admin, ConvertUrl(approved, "crm-quote-to-order"), new { lines = new[] { new { sourceLineId = lineId, quantity = 6 } } });
        (await _admin.PostAsJsonAsync(ConvertUrl(approved, "crm-quote-to-order"), new { lines = new[] { new { sourceLineId = lineId, quantity = 1 } } }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var firstOrder = await JsonOf(await _admin.GetAsync($"{Base}/records/{first.GetProperty("id").GetInt64()}"));
        var orderRow = firstOrder.GetProperty("details").GetProperty(orderLine)[0].GetProperty("values");
        orderRow.GetProperty(ol["數量"]).GetDecimal().Should().Be(4m);
        orderRow.GetProperty(ol["單價"]).GetDecimal().Should().Be(3000m, "訂單單價沿用報價時的快照");
        orderRow.GetProperty(ol["金額"]).GetDecimal().Should().Be(12000m);
        firstOrder.GetProperty("values").GetProperty(o["含稅總計"]).GetDecimal().Should().Be(12600m);
    }

    [Fact]
    public async Task ConvertedPartner_CanBePickedInTheOrderCustomerField_ASupplierCannot()
    {
        var app = await OpenCrm();
        string lead = Key(app, "lead"), order = Key(app, "order");
        var l = await FieldKeys(_admin, lead);
        var o = await FieldKeys(_admin, order);

        var newLead = await PostOk(_admin, $"{Base}/modules/{lead}/records", new
        {
            values = new Dictionary<string, object> { [l["姓名"]] = "陳雅婷", [l["公司"]] = "晨曦機械" }
        });
        var contacted = await Transition(_admin, newLead, l["狀態"], await OptionValue(_admin, lead, "狀態", "已聯繫"));
        var qualified = await Transition(_admin, contacted, l["狀態"], await OptionValue(_admin, lead, "狀態", "已確認"));
        var partner = await PostOk(_admin, ConvertUrl(qualified, "crm-lead-to-customer"), new { });

        async Task<HttpResponseMessage> OrderFor(long customer) => await _admin.PostAsJsonAsync($"{Base}/modules/{order}/records", new
        {
            values = new Dictionary<string, object>
            {
                [o["客戶"]] = customer, [o["訂單日期"]] = "2026-11-10", [o["狀態"]] = await OptionValue(_admin, order, "狀態", "已確認")
            }
        });
        var picked = await OrderFor(partner.GetProperty("id").GetInt64());
        picked.StatusCode.Should().Be(HttpStatusCode.Created, await picked.Content.ReadAsStringAsync());

        var supplier = await NewPartner("昌盛鋼鐵", role: "供應商");
        (await OrderFor(supplier.GetProperty("id").GetInt64())).StatusCode.Should().Be(HttpStatusCode.BadRequest, "供應商不能當訂單客戶");
    }

    [Fact]
    public async Task OnlyRequiredFields_LeadToCustomerAndOpportunity_ToQuote_ToOrder_Completes()
    {
        var app = await OpenCrm();
        string lead = Key(app, "lead"), customer = Partner, opportunity = Key(app, "opportunity"),
            quote = Key(app, "quote"), quoteLine = Key(app, "quote-line"), product = Key(app, "product"), order = Key(app, "order");
        var l = await FieldKeys(_admin, lead);
        var op = await FieldKeys(_admin, opportunity);
        var pk = await FieldKeys(_admin, product);
        var qlk = await FieldKeys(_admin, quoteLine);
        var qk = await FieldKeys(_admin, quote);

        // 潛在客戶：只填必填欄位。公司會被帶進客戶名稱（客戶名稱必填），所以公司也必須是必填
        var requiredOfLead = (await JsonOf(await _admin.GetAsync($"{Base}/modules/{lead}"))).GetProperty("blocks").EnumerateArray()
            .SelectMany(b => b.GetProperty("fields").EnumerateArray())
            .Where(f => f.GetProperty("config").TryGetProperty("required", out var r) && r.GetBoolean())
            .Select(f => f.GetProperty("label").GetProperty("zh-TW").GetString()).ToList();
        requiredOfLead.Should().Contain("公司");

        var missingCompany = await _admin.PostAsJsonAsync($"{Base}/modules/{lead}/records",
            new { values = new Dictionary<string, object> { [l["姓名"]] = "只有姓名" } });
        missingCompany.StatusCode.Should().Be(HttpStatusCode.BadRequest, "沒有公司的潛在客戶根本建不起來，也就不會卡在轉客戶");

        var newLead = await PostOk(_admin, $"{Base}/modules/{lead}/records",
            new { values = new Dictionary<string, object> { [l["姓名"]] = "李小華", [l["公司"]] = "最小公司" } });
        var contacted = await Transition(_admin, newLead, l["狀態"], await OptionValue(_admin, lead, "狀態", "已聯繫"));
        var qualified = await Transition(_admin, contacted, l["狀態"], await OptionValue(_admin, lead, "狀態", "已確認"));

        var newCustomer = await PostOk(_admin, ConvertUrl(qualified, "crm-lead-to-customer"), new { });
        Str(newCustomer, (await FieldKeys(_admin, customer))["名稱"]).Should().Be("最小公司");
        var opp = await PostOk(_admin, ConvertUrl(qualified, "crm-lead-to-opportunity"), new { });

        // 商機：只有名稱與階段是必填；轉報價單需要客戶，所以先補上客戶（不是欄位必填，是轉換的前提）
        var withCustomer = await JsonOf(await _admin.PutAsJsonAsync($"{Base}/records/{opp.GetProperty("id").GetInt64()}", new
        {
            values = new Dictionary<string, object> { [op["客戶"]] = newCustomer.GetProperty("id").GetInt64() },
            rowVersion = opp.GetProperty("rowVersion").GetString()
        }));
        var qual = await Transition(_admin, withCustomer, op["階段"], await OptionValue(_admin, opportunity, "階段", "需求確認"));
        var prop = await Transition(_admin, qual, op["階段"], await OptionValue(_admin, opportunity, "階段", "提案報價"));
        var q = await PostOk(_admin, ConvertUrl(prop, "crm-opportunity-to-quote"), new { });

        var prod = await PostOk(_admin, $"{Base}/modules/{product}/records", new
        {
            values = new Dictionary<string, object> { [pk["料號"]] = "P-1", [pk["名稱"]] = "最小品" }
        });
        var put = await _admin.PutAsJsonAsync($"{Base}/records/{q.GetProperty("id").GetInt64()}", new
        {
            values = new Dictionary<string, object>(), rowVersion = q.GetProperty("rowVersion").GetString(),
            details = new Dictionary<string, object[]>
            {
                [quoteLine] = [new { values = new Dictionary<string, object> { [qlk["產品"]] = prod.GetProperty("id").GetInt64(), [qlk["數量"]] = 1 } }]
            }
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
        var fresh = await JsonOf(await _admin.GetAsync($"{Base}/records/{q.GetProperty("id").GetInt64()}"));
        var pending = await Transition(_admin, fresh, qk["狀態"], await OptionValue(_admin, quote, "狀態", "待核准"));
        var approved = await Transition(_admin, pending, qk["狀態"], await OptionValue(_admin, quote, "狀態", "已核准"));
        var lineId = fresh.GetProperty("details").GetProperty(quoteLine)[0].GetProperty("id").GetInt64();
        var created = await PostOk(_admin, ConvertUrl(approved, "crm-quote-to-order"), new { lines = new[] { new { sourceLineId = lineId, quantity = 1 } } });
        created.GetProperty("id").GetInt64().Should().BePositive();
        _ = order;
    }

    [Fact]
    public async Task ApprovalNeedsTheManagerGroup_ASalesUserCannotApprove()
    {
        var app = await OpenCrm();
        var (approved, _, quote, _) = await ApprovedQuote(app);
        approved.GetProperty("values").GetProperty((await FieldKeys(_admin, quote))["狀態"]).GetString().Should().NotBeNullOrEmpty();

        var q = await FieldKeys(_admin, quote);
        var draft = await PostOk(_admin, $"{Base}/modules/{quote}/records", new
        {
            values = new Dictionary<string, object> { [q["客戶"]] = (await _admin.GetFromJsonAsync<JsonElement>($"{Base}/records/{approved.GetProperty("id").GetInt64()}"))
                .GetProperty("values").GetProperty(q["客戶"]).GetInt64() }
        });
        var pending = await Transition(_admin, draft, q["狀態"], await OptionValue(_admin, quote, "狀態", "待核准"));

        // 把 sales01 放進「業務」群組（開啟時新建的群組）
        var groups = await JsonOf(await _admin.GetAsync($"{Base}/admin/groups"));
        var sales = groups.EnumerateArray().Single(g => g.GetProperty("nameI18n").GetProperty("zh-TW").GetString() == "CRM 業務");
        var put = await _admin.PutAsJsonAsync($"{Base}/admin/groups/{sales.GetProperty("id").GetInt64()}/members", new { userIds = new[] { "sales01" } });
        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());

        var rep = _app.AsUser("sales01");
        var attempt = await rep.PutAsJsonAsync($"{Base}/records/{pending.GetProperty("id").GetInt64()}", new
        {
            values = new Dictionary<string, object> { [q["狀態"]] = await OptionValue(_admin, quote, "狀態", "已核准") },
            rowVersion = pending.GetProperty("rowVersion").GetString()
        });
        attempt.StatusCode.Should().Be(HttpStatusCode.BadRequest, "只有業務主管能核准");
    }

    // ---- 轉換時補目標的必填表頭欄位（沒填客戶的商機轉報價）----

    private record NoCustomerOpportunity(JsonElement App, JsonElement Opportunity, string QuoteModule, Dictionary<string, string> QuoteKeys);

    /// <summary>已到「提案報價」階段、但沒有填客戶的商機。</summary>
    private async Task<NoCustomerOpportunity> OpportunityWithoutCustomer()
    {
        var app = await OpenCrm();
        string opportunity = Key(app, "opportunity"), quote = Key(app, "quote");
        var op = await FieldKeys(_admin, opportunity);
        var opp = await PostOk(_admin, $"{Base}/modules/{opportunity}/records", new { values = new Dictionary<string, object> { [op["名稱"]] = "無客戶商機" } });
        var qual = await Transition(_admin, opp, op["階段"], await OptionValue(_admin, opportunity, "階段", "需求確認"));
        var prop = await Transition(_admin, qual, op["階段"], await OptionValue(_admin, opportunity, "階段", "提案報價"));
        return new(app, prop, quote, await FieldKeys(_admin, quote));
    }

    private static string InputsUrl(JsonElement source, string conversion) =>
        $"{Base}/records/{source.GetProperty("id").GetInt64()}/conversions/{conversion}/inputs";

    private async Task<long> NewCustomer(JsonElement app, string name)
    {
        _ = app;
        return (await NewPartner(name)).GetProperty("id").GetInt64();
    }

    [Fact]
    public async Task ConvertInputs_ListOnlyTheRequiredTargetFieldsTheCopyCannotSupply()
    {
        var t = await OpportunityWithoutCustomer();

        var res = await _admin.GetAsync(InputsUrl(t.Opportunity, "crm-opportunity-to-quote"));

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var inputs = (await JsonOf(res)).EnumerateArray().ToList();
        inputs.Select(i => i.GetString()).Should().Equal(t.QuoteKeys["客戶"]);
    }

    [Fact]
    public async Task ConvertInputs_AreEmpty_WhenTheCopyAlreadySuppliesEverything()
    {
        var t = await OpportunityWithoutCustomer();
        var op = await FieldKeys(_admin, Key(t.App, "opportunity"));
        var put = await _admin.PutAsJsonAsync($"{Base}/records/{t.Opportunity.GetProperty("id").GetInt64()}", new
        {
            values = new Dictionary<string, object> { [op["客戶"]] = await NewCustomer(t.App, "宏達科技") },
            rowVersion = t.Opportunity.GetProperty("rowVersion").GetString()
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());

        var res = await _admin.GetAsync(InputsUrl(await JsonOf(put), "crm-opportunity-to-quote"));

        (await JsonOf(res)).GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task ConvertInputs_StillListARequiredField_WhenAnotherValidationRuleAlsoFailsOnTheEmptyValue()
    {
        var t = await OpportunityWithoutCustomer();
        // 客戶除了必填，還有一條「客戶不可為空」的驗證規則：空值會同時產生兩則訊息（必填＋規則訊息）
        var customerId = long.Parse(t.QuoteKeys["客戶"][2..]);
        var put = await _admin.PutAsJsonAsync($"{Base}/modules/{t.QuoteModule}/rules", new
        {
            rules = new object[]
            {
                new
                {
                    kind = "validation", targets = new[] { $"field:{customerId}" }, active = true,
                    message = new Dictionary<string, string> { ["zh-TW"] = "客戶要先建立" },
                    condition = new { field = customerId, op = "isNotEmpty" }
                }
            }
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());

        var res = await _admin.GetAsync(InputsUrl(t.Opportunity, "crm-opportunity-to-quote"));

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        (await JsonOf(res)).EnumerateArray().Select(i => i.GetString()).Should().Equal(t.QuoteKeys["客戶"]);
    }

    [Fact]
    public async Task UserWithAddButNoReadOnTheTarget_CanGetInputs_ButNotTheTargetSchema()
    {
        // 前端對話框要靠目標 schema 才畫得出欄位；沒有目標讀取權限時 schema 是 403，inputs（只要求新增權限）卻是 200
        var t = await OpportunityWithoutCustomer();
        var groups = await JsonOf(await _admin.GetAsync($"{Base}/groups"));
        var salesId = groups.EnumerateArray().Single(g => g.GetProperty("key").GetString() == "sales").GetProperty("id").GetInt64();
        async Task Grant(string module, bool read, bool add)
        {
            var res = await _admin.PutAsJsonAsync($"{Base}/modules/{module}/permissions", new
            {
                groups = new[] { new { groupId = salesId, scope = "all", privileges = new { read, add, edit = false, delete = false, printExport = false, pricing = false, approve = false } } },
                users = Array.Empty<object>(), fields = Array.Empty<object>(), options = Array.Empty<object>()
            });
            res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        }
        await Grant(Key(t.App, "opportunity"), read: true, add: false);
        await Grant(t.QuoteModule, read: false, add: true);
        var sales = _app.AsUser("sales01");

        var inputs = await sales.GetAsync(InputsUrl(t.Opportunity, "crm-opportunity-to-quote"));
        var schema = await sales.GetAsync($"{Base}/modules/{t.QuoteModule}/schema");

        inputs.StatusCode.Should().Be(HttpStatusCode.OK, await inputs.Content.ReadAsStringAsync());
        schema.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task OpportunityWithoutCustomer_ConvertsOnceTheCustomerIsFilledInTheDialog()
    {
        var t = await OpportunityWithoutCustomer();
        var customerId = await NewCustomer(t.App, "宏達科技");

        var q = await PostOk(_admin, ConvertUrl(t.Opportunity, "crm-opportunity-to-quote"), new
        {
            rowVersion = t.Opportunity.GetProperty("rowVersion").GetString(),
            values = new Dictionary<string, object> { [t.QuoteKeys["客戶"]] = customerId }
        });

        q.GetProperty("values").GetProperty(t.QuoteKeys["客戶"]).GetInt64().Should().Be(customerId);
        Str(q, t.QuoteKeys["客戶名稱"]).Should().Be("宏達科技", "補上客戶後仍照帶值規則由系統帶入客戶資料");
    }

    [Fact]
    public async Task OpportunityWithoutCustomer_NotFilled_Is400_AndNothingIsCreated()
    {
        var t = await OpportunityWithoutCustomer();

        var res = await _admin.PostAsJsonAsync(ConvertUrl(t.Opportunity, "crm-opportunity-to-quote"),
            new { rowVersion = t.Opportunity.GetProperty("rowVersion").GetString() });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, await res.Content.ReadAsStringAsync());
        var list = await JsonOf(await _admin.GetAsync($"{Base}/modules/{t.QuoteModule}/records/paged?page=1&size=5"));
        list.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Modules_AreGroupedInSections_SoNamesLikeCustomerAndOrderStayDistinguishable()
    {
        await OpenCrm();
        async Task<string> Section(string key) =>
            (await JsonOf(await _admin.GetAsync($"{Base}/modules/{key}"))).GetProperty("section").GetString() ?? "";

        foreach (var key in new[] { "crm-lead", "crm-activity" }) (await Section(key)).Should().Be("客戶關係", key);
        foreach (var key in new[] { Partner, BuiltInMasterTests.Contact }) (await Section(key)).Should().Be("主檔", key);
        foreach (var key in new[] { "crm-product", "crm-opportunity", "crm-quote", "crm-order" }) (await Section(key)).Should().Be("銷售", key);
    }

    [Fact]
    public async Task Quote_Prints_WithCustomerLinesAndTotals_AndValuesAreEscaped()
    {
        var app = await OpenCrm();
        var (approved, _, quote, _) = await ApprovedQuote(app, customerName: "<script>alert(1)</script>宏達");
        var id = approved.GetProperty("id").GetInt64();

        var templates = await JsonOf(await _admin.GetAsync($"{Base}/modules/{quote}/print-templates"));
        templates.EnumerateArray().Select(t => t.GetProperty("key").GetString()).Should().Contain("quote-sheet");

        var res = await _admin.GetAsync($"{Base}/records/{id}/print/quote-sheet?locale=zh-TW");
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var html = await res.Content.ReadAsStringAsync();
        html.Should().Contain("感測器").And.Contain("控制器");
        html.Should().Contain("QT-");
        html.Should().NotContain("<script>alert(1)</script>").And.Contain("&lt;script&gt;");
        // 小計 10×1200 + 10×5000 = 62000，稅 3100，總計 65100；金額與畫面一樣有千分位，數量是 10 不是 10.00
        html.Should().Contain("未稅小計：62,000").And.Contain("稅額：3,100").And.Contain("含稅總計：65,100");
        html.Should().Contain(">1,200<").And.Contain(">5,000<").And.Contain(">12,000<").And.Contain(">50,000<");
        html.Should().Contain("<td align=\"right\">10</td>").And.NotContain("10.00").And.NotContain("65100");
    }

    [Fact]
    public async Task TwoPeopleConvertingTheSameQuoteAtOnce_NeverOverConvert()
    {
        var app = await OpenCrm();
        var (approved, lineIds, _, quoteLineKey) = await ApprovedQuote(app);
        var url = ConvertUrl(approved, "crm-quote-to-order");

        // 每列共 10，同時送 8 個「各轉 4」：最多只能成功 2 個
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            var res = await _admin.PostAsJsonAsync(url, new { lines = new[] { new { sourceLineId = lineIds[0], quantity = 4 } } });
            return res.StatusCode;
        }));
        var ok = results.Count(s => s == HttpStatusCode.Created);
        ok.Should().BeInRange(1, 2);
        results.Where(s => s != HttpStatusCode.Created).Should().OnlyContain(s => s == HttpStatusCode.Conflict || s == HttpStatusCode.BadRequest);

        var status = await JsonOf(await _admin.GetAsync($"{Base}/records/{approved.GetProperty("id").GetInt64()}/conversions"));
        var line = status.EnumerateArray().Single(c => c.GetProperty("key").GetString() == "crm-quote-to-order")
            .GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("lineId").GetInt64() == lineIds[0]);
        line.GetProperty("converted").GetDecimal().Should().Be(ok * 4m);
        line.GetProperty("remaining").GetDecimal().Should().Be(10m - ok * 4m);
        _ = quoteLineKey;
    }

    [Fact]
    public async Task ClosedApp_DataReadableReferencesShow_WritesBlocked_ReopenKeepsEverything()
    {
        var app = await OpenCrm();
        var (approved, lineIds, quote, _) = await ApprovedQuote(app);
        var id = approved.GetProperty("id").GetInt64();
        var q = await FieldKeys(_admin, quote);

        (await _admin.PostAsJsonAsync($"{Apps}/crm-sales/close", new { cascade = false })).EnsureSuccessStatusCode();

        var read = await _admin.GetAsync($"{Base}/records/{id}");
        read.StatusCode.Should().Be(HttpStatusCode.OK);
        var record = await JsonOf(read);
        Str(record, q["客戶名稱"]).Should().Be("宏達科技");
        var refShown = await _admin.GetAsync($"{Base}/modules/{quote}/records/paged?currentPage=1&pagesSize=20");
        refShown.StatusCode.Should().Be(HttpStatusCode.OK);
        (await refShown.Content.ReadAsStringAsync()).Should().Contain("宏達科技");

        (await _admin.PostAsJsonAsync($"{Base}/modules/{quote}/records", new { values = new Dictionary<string, object>() }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _admin.PutAsJsonAsync($"{Base}/records/{id}", new
        {
            values = new Dictionary<string, object> { [q["備註"]] = "x" }, rowVersion = record.GetProperty("rowVersion").GetString()
        })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _admin.PostAsJsonAsync(ConvertUrl(approved, "crm-quote-to-order"), new { lines = new[] { new { sourceLineId = lineIds[0], quantity = 1 } } }))
            .StatusCode.Should().NotBe(HttpStatusCode.Created, "關閉的應用程式不能再轉單");

        (await _admin.PostAsync($"{Apps}/crm-sales/reopen", null)).EnsureSuccessStatusCode();
        var after = await JsonOf(await _admin.GetAsync($"{Base}/records/{id}"));
        Str(after, q["客戶名稱"]).Should().Be("宏達科技");
        after.GetProperty("details").EnumerateObject().Sum(d => d.Value.GetArrayLength()).Should().Be(2);
        await PostOk(_admin, ConvertUrl(approved, "crm-quote-to-order"), new { lines = new[] { new { sourceLineId = lineIds[0], quantity = 1 } } });
    }

    // ---- 同一組能力跑採購情境：請購 → 採購單。只存在於測試，不是出貨的內建應用程式。 ----

    private const string Procurement = """
        {
          "format": "formwright-app", "formatVersion": 3,
          "manifest": { "id": "procure-flow", "version": "1.0.0", "name": { "zh-TW": "採購情境" } },
          "groups": [ { "code": "buyer", "name": { "zh-TW": "採購" } }, { "code": "boss", "name": { "zh-TW": "主管" } } ],
          "modules": [
            { "code": "supplier", "key": "pr-supplier", "name": { "zh-TW": "供應商" }, "kind": "standalone", "titleField": "name",
              "blocks": [ { "code": "main", "label": { "zh-TW": "基本" }, "collapsed": false, "columns": 2, "fields": [
                { "code": "name", "type": "text", "label": { "zh-TW": "名稱" }, "config": { "required": true } },
                { "code": "terms", "type": "text", "label": { "zh-TW": "付款條件" }, "config": {} } ] } ],
              "permissions": [ { "group": "buyer", "scope": "all", "privileges": { "read": true, "add": true, "edit": true, "delete": true, "printExport": true, "pricing": true, "approve": true } } ] },
            { "code": "req", "key": "pr-req", "name": { "zh-TW": "請購單" }, "kind": "standalone", "titleField": "no",
              "blocks": [ { "code": "main", "label": { "zh-TW": "基本" }, "collapsed": false, "columns": 2, "fields": [
                { "code": "no", "type": "autonumber", "label": { "zh-TW": "單號" }, "config": { "format": "PR-{0000}" } },
                { "code": "status", "type": "enum", "label": { "zh-TW": "狀態" },
                  "options": [ { "code": "open", "label": { "zh-TW": "申請中" }, "color": null }, { "code": "approved", "label": { "zh-TW": "已核准" }, "color": null } ],
                  "config": { "defaultValue": "opt_open", "required": true } } ] } ],
              "permissions": [ { "group": "buyer", "scope": "all", "privileges": { "read": true, "add": true, "edit": true, "delete": true, "printExport": true, "pricing": true, "approve": true } } ] },
            { "code": "req-line", "key": "pr-req-line", "name": { "zh-TW": "請購明細" }, "kind": "standalone", "titleField": "item",
              "blocks": [ { "code": "main", "label": { "zh-TW": "基本" }, "collapsed": false, "columns": 2, "fields": [
                { "code": "req", "type": "reference", "label": { "zh-TW": "請購單" }, "config": { "refTarget": { "kind": "module", "key": "req" }, "relation": "detail" } },
                { "code": "item", "type": "text", "label": { "zh-TW": "品項" }, "config": { "required": true } },
                { "code": "qty", "type": "decimal", "label": { "zh-TW": "數量" }, "config": { "scale": 2 } } ] } ] },
            { "code": "po", "key": "pr-po", "name": { "zh-TW": "採購單" }, "kind": "standalone", "titleField": "no",
              "blocks": [ { "code": "main", "label": { "zh-TW": "基本" }, "collapsed": false, "columns": 2, "fields": [
                { "code": "no", "type": "autonumber", "label": { "zh-TW": "單號" }, "config": { "format": "PO-{0000}" } },
                { "code": "fromReq", "type": "reference", "label": { "zh-TW": "來源請購" }, "config": { "readOnly": true, "refTarget": { "kind": "module", "key": "req" } } },
                { "code": "supplier", "type": "reference", "label": { "zh-TW": "供應商" },
                  "config": { "refTarget": { "kind": "module", "key": "supplier" },
                              "fill": { "mode": "lock", "map": [ { "from": "supplier.terms", "to": "terms" } ] } } },
                { "code": "terms", "type": "text", "label": { "zh-TW": "付款條件" }, "config": { "readOnly": true } } ] } ],
              "permissions": [ { "group": "buyer", "scope": "all", "privileges": { "read": true, "add": true, "edit": true, "delete": true, "printExport": true, "pricing": true, "approve": true } } ] },
            { "code": "po-line", "key": "pr-po-line", "name": { "zh-TW": "採購明細" }, "kind": "standalone", "titleField": "item",
              "blocks": [ { "code": "main", "label": { "zh-TW": "基本" }, "collapsed": false, "columns": 2, "fields": [
                { "code": "po", "type": "reference", "label": { "zh-TW": "採購單" }, "config": { "refTarget": { "kind": "module", "key": "po" }, "relation": "detail" } },
                { "code": "item", "type": "text", "label": { "zh-TW": "品項" }, "config": { "required": true } },
                { "code": "qty", "type": "decimal", "label": { "zh-TW": "數量" }, "config": { "scale": 2 } },
                { "code": "fromLine", "type": "integer", "label": { "zh-TW": "來源列" }, "config": { "readOnly": true } } ] } ] }
          ],
          "workflows": [ { "module": "req", "statusField": "status", "initial": "open",
                           "states": [ { "option": "open" }, { "option": "approved", "readOnly": true } ],
                           "transitions": [ { "from": "open", "to": "approved", "groups": ["boss"] } ] } ],
          "conversions": [ { "code": "pr-req-to-po", "name": { "zh-TW": "請購轉採購" }, "active": true,
                             "source": "req", "target": "po", "sourceStates": ["approved"],
                             "header": { "sourceField": "po.fromReq", "copy": [] },
                             "lines": { "sourceModule": "req-line", "targetModule": "po-line", "sourceLineField": "po-line.fromLine",
                                        "quantity": { "from": "req-line.qty", "to": "po-line.qty" },
                                        "copy": [ { "from": "req-line.item", "to": "po-line.item" } ] } } ],
          "reports": [],
          "printTemplates": []
        }
        """;

    [Fact]
    public async Task SameAbilities_RunAProcurementScenario_RequisitionToPurchaseOrder()
    {
        var imported = await _admin.PostAsync($"{Apps}/packages/import", new StringContent(Procurement, Encoding.UTF8, "application/json"));
        imported.StatusCode.Should().Be(HttpStatusCode.Created, await imported.Content.ReadAsStringAsync());
        var opened = await _admin.PostAsJsonAsync($"{Apps}/packages/procure-flow/open", new
        {
            groupMap = new Dictionary<string, object> { ["buyer"] = new { mode = "create" }, ["boss"] = new { mode = "create" } }
        });
        opened.StatusCode.Should().Be(HttpStatusCode.Created, await opened.Content.ReadAsStringAsync());
        var app = await JsonOf(opened);
        string req = Key(app, "req"), reqLine = Key(app, "req-line"), po = Key(app, "po"), poLine = Key(app, "po-line"), supplier = Key(app, "supplier");
        var r = await FieldKeys(_admin, req);
        var rl = await FieldKeys(_admin, reqLine);
        var p = await FieldKeys(_admin, po);
        var pl = await FieldKeys(_admin, poLine);
        var s = await FieldKeys(_admin, supplier);

        var created = await PostOk(_admin, $"{Base}/modules/{req}/records", new
        {
            values = new Dictionary<string, object>(),
            details = new Dictionary<string, object[]>
            {
                [reqLine] = [new { values = new Dictionary<string, object> { [rl["品項"]] = "鋼板", [rl["數量"]] = 100 } }]
            }
        });
        var lineId = created.GetProperty("details").GetProperty(reqLine)[0].GetProperty("id").GetInt64();
        var url = ConvertUrl(created, "pr-req-to-po");
        (await _admin.PostAsJsonAsync(url, new { lines = new[] { new { sourceLineId = lineId, quantity = 30 } } })).StatusCode
            .Should().NotBe(HttpStatusCode.Created, "未核准的請購單不能轉採購");

        var approved = await Transition(_admin, created, r["狀態"], await OptionValue(_admin, req, "狀態", "已核准"));
        var first = await PostOk(_admin, url, new { lines = new[] { new { sourceLineId = lineId, quantity = 30 } } });
        await PostOk(_admin, url, new { lines = new[] { new { sourceLineId = lineId, quantity = 70 } } });
        (await _admin.PostAsJsonAsync(url, new { lines = new[] { new { sourceLineId = lineId, quantity = 1 } } })).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
        first.GetProperty("values").GetProperty(p["來源請購"]).GetInt64().Should().Be(approved.GetProperty("id").GetInt64());
        var firstPo = await JsonOf(await _admin.GetAsync($"{Base}/records/{first.GetProperty("id").GetInt64()}"));
        firstPo.GetProperty("details").GetProperty(poLine)[0].GetProperty("values").GetProperty(pl["品項"]).GetString().Should().Be("鋼板");

        // 帶值：採購單選供應商，付款條件由系統帶入
        var sup = await PostOk(_admin, $"{Base}/modules/{supplier}/records", new
        {
            values = new Dictionary<string, object> { [s["名稱"]] = "鑫盛鋼鐵", [s["付款條件"]] = "月結 30 天" }
        });
        var withSupplier = await PostOk(_admin, $"{Base}/modules/{po}/records", new
        {
            values = new Dictionary<string, object> { [p["供應商"]] = sup.GetProperty("id").GetInt64() }
        });
        Str(withSupplier, p["付款條件"]).Should().Be("月結 30 天");
    }
}
