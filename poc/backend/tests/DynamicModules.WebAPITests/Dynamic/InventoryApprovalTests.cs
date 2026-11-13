using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;
using static DynamicModules.WebAPITests.Dynamic.InventoryRig;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 內建進銷存（apps/inventory.app.json 1.1.0）的採購單預設簽核流程（簽核 a5c）：草稿 → 已確認 需簽核，
/// 未稅小計 100000 以上走兩關（採購主管、總經理）、10000 以上走一關、不到門檻送出即確認（門檻不含稅，稅率改了不影響簽幾關）。
/// 組織（全部虛構）：總部〔主管 sales03〕→ 營運部〔主管 ops01〕→ 採購部〔主管空缺，成員 sales01 = 採購〕；進銷存主管群組 = manager01。
/// 品項單價 1000、稅率 5%：數量 4 → 未稅 4,000（直接確認）、40 → 40,000（一關）、110 → 110,000（兩關）。
/// </summary>
[Trait("Category", "Postgres")]
public class InventoryApprovalTests : IDisposable
{
    private const string Hq = "sales03", Ops = "ops01";

    private readonly TestApp _app = new();
    private readonly HttpClient _admin;

    public InventoryApprovalTests() => _admin = _app.AsUser();

    public void Dispose() => _app.Dispose();

    private async Task<(InventoryRig Rig, long Supplier, long Item)> ArrangeAsync(bool withOrganization = true)
    {
        var rig = await OpenAsync(_admin);
        var supplier = await rig.SupplierAsync("鑫盛五金");
        var item = await rig.ItemAsync("導柱", price: 1000);
        if (withOrganization) await CreateOrganizationAsync();
        return (rig, supplier, item);
    }

    private async Task CreateOrganizationAsync()
    {
        async Task<long> Dept(string code, string name, long? parent, string? manager) => Id(await PostOk(_admin, $"{Base}/departments", new
        {
            code, name = new Dictionary<string, string> { ["zh-TW"] = name }, parentId = parent, managerUserId = manager, sortOrder = 0, active = true
        }));
        var hq = await Dept("hq", "總部", null, Hq);
        var ops = await Dept("ops", "營運部", hq, Ops);
        var buying = await Dept("buying", "採購部", ops, null);
        (await _admin.PutAsJsonAsync($"{Base}/admin/users/{Buyer}/department", new { departmentId = buying })).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static string[] OpenAssignees(JsonElement current) =>
        [.. current.GetProperty("stages").EnumerateArray().SelectMany(s => s.GetProperty("tasks").EnumerateArray())
            .Where(t => t.GetProperty("status").GetString() == "open").Select(t => t.GetProperty("assigneeUserId").GetString()!)];

    private static string[] StageCodes(JsonElement current) =>
        [.. current.GetProperty("stages").EnumerateArray().Select(s => s.GetProperty("stageCode").GetString()!)];

    private async Task<string> StatusOf(InventoryRig rig, JsonElement po) =>
        InventoryRig.Str(await rig.GetAsync(Id(po)), (await rig.F("po"))["狀態"])!;

    [Fact]
    public async Task Small_DirectConfirms_NoInstance_ThenLocked()
    {
        var (rig, supplier, item) = await ArrangeAsync();
        var buyer = _app.AsUser(Buyer);
        var draft = await rig.PoDraftAsync(supplier, item, 4, buyer);

        var submit = await rig.SubmitPoAsync(buyer, draft);
        submit.StatusCode.Should().Be(HttpStatusCode.OK, await submit.Content.ReadAsStringAsync());
        (await rig.CurrentApprovalAsync(Id(draft))).Should().BeNull("不到門檻沒有流程符合，入口是 direct");
        var confirmed = await rig.GetAsync(Id(draft));
        (await StatusOf(rig, confirmed)).Should().Be(await rig.Opt("po", "狀態", "已確認"));

        // 已確認是簽核鎖定狀態，明細不能再改（簽核設計 §17.4 第 1 步）
        var line = confirmed.GetProperty("details").GetProperty(rig.Key("po-line"))[0];
        var l = await rig.F("po-line");
        var edit = await buyer.PutAsJsonAsync($"{Base}/records/{Id(confirmed)}", new
        {
            values = new Dictionary<string, object>(),
            rowVersion = confirmed.GetProperty("rowVersion").GetString(),
            details = new Dictionary<string, object[]>
            {
                [rig.Key("po-line")] = [new { id = Id(line), values = new Dictionary<string, object> { [l["數量"]] = 5 }, rowVersion = line.GetProperty("rowVersion").GetString() }]
            }
        });
        edit.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await edit.Content.ReadAsStringAsync()).Should().Contain("已經過簽核", "是簽核鎖定擋下的，不是別的驗證");
    }

    [Theory]
    [InlineData(96, true)]    // 未稅 96,000、含稅 100,800：含稅會落入大額，門檻看未稅所以仍是一萬以上一關
    [InlineData(9.6, false)]  // 未稅 9,600、含稅 10,080：含稅會落入一萬以上，門檻看未稅所以送出即確認
    public async Task Threshold_UsesSubtotalNotTaxIncluded(decimal qty, bool oneStage)
    {
        var (rig, supplier, item) = await ArrangeAsync();
        var buyer = _app.AsUser(Buyer);
        var draft = await rig.PoDraftAsync(supplier, item, qty, buyer);

        var submit = await rig.SubmitPoAsync(buyer, draft);
        if (oneStage)
        {
            submit.StatusCode.Should().Be(HttpStatusCode.Created, await submit.Content.ReadAsStringAsync());
            var current = (await rig.CurrentApprovalAsync(Id(draft)))!.Value;
            current.GetProperty("flowName").GetProperty("zh-TW").GetString().Should().Be("採購單（一萬以上）");
            StageCodes(current).Should().Equal("buyer-lead");
        }
        else
        {
            submit.StatusCode.Should().Be(HttpStatusCode.OK, await submit.Content.ReadAsStringAsync());
            (await rig.CurrentApprovalAsync(Id(draft))).Should().BeNull();
        }
    }

    [Fact]
    public async Task PutToConfirmed_400_NeedsSubmit()
    {
        var (rig, supplier, item) = await ArrangeAsync();
        var draft = await rig.PoDraftAsync(supplier, item, 4, _app.AsUser(Buyer));
        var res = await rig.TransitionAsync(_app.AsUser(Buyer), draft, "po", "已確認");
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("需要送出簽核");
    }

    [Fact]
    public async Task Medium_OneStage_ManagerLevel1()
    {
        var (rig, supplier, item) = await ArrangeAsync();
        var buyer = _app.AsUser(Buyer);
        var draft = await rig.PoDraftAsync(supplier, item, 40, buyer);

        var submit = await rig.SubmitPoAsync(buyer, draft);
        submit.StatusCode.Should().Be(HttpStatusCode.Created, await submit.Content.ReadAsStringAsync());
        var current = (await rig.CurrentApprovalAsync(Id(draft)))!.Value;
        current.GetProperty("flowName").GetProperty("zh-TW").GetString().Should().Be("採購單（一萬以上）");
        StageCodes(current).Should().Equal("buyer-lead");
        OpenAssignees(current).Should().Equal([Ops], "採購部主管空缺，往上找到營運部主管");
        current.GetProperty("stages")[0].GetProperty("dueTime").ValueKind.Should().Be(JsonValueKind.String, "第 1 關有逾期時數");
        (await StatusOf(rig, draft)).Should().Be(await rig.Opt("po", "狀態", "草稿"), "核准前記錄仍是草稿");

        var approve = await rig.ApprovePoAsync(_app.AsUser(Ops), Ops, Id(draft));
        approve.StatusCode.Should().Be(HttpStatusCode.OK, await approve.Content.ReadAsStringAsync());
        (await rig.CurrentApprovalAsync(Id(draft))).Should().BeNull();
        (await StatusOf(rig, draft)).Should().Be(await rig.Opt("po", "狀態", "已確認"));
    }

    [Fact]
    public async Task Large_TwoStages_Level1ThenLevel2()
    {
        var (rig, supplier, item) = await ArrangeAsync();
        var buyer = _app.AsUser(Buyer);
        var draft = await rig.PoDraftAsync(supplier, item, 110, buyer);

        (await rig.SubmitPoAsync(buyer, draft)).StatusCode.Should().Be(HttpStatusCode.Created);
        var first = (await rig.CurrentApprovalAsync(Id(draft)))!.Value;
        first.GetProperty("flowName").GetProperty("zh-TW").GetString().Should().Be("採購單（大額）");
        StageCodes(first).Should().Equal("buyer-lead");
        OpenAssignees(first).Should().Equal([Ops]);

        (await rig.ApprovePoAsync(_app.AsUser(Ops), Ops, Id(draft))).StatusCode.Should().Be(HttpStatusCode.OK);
        var second = (await rig.CurrentApprovalAsync(Id(draft)))!.Value;
        StageCodes(second).Should().Equal("buyer-lead", "gm");
        OpenAssignees(second).Should().Equal([Hq], "第 2 關是發起人往上第 2 層主管");
        second.GetProperty("stages")[1].GetProperty("dueTime").ValueKind.Should().Be(JsonValueKind.String, "第 2 關有逾期時數");
        (await StatusOf(rig, draft)).Should().Be(await rig.Opt("po", "狀態", "草稿"), "第 2 關還沒同意");

        (await rig.ApprovePoAsync(_app.AsUser(Hq), Hq, Id(draft))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await StatusOf(rig, draft)).Should().Be(await rig.Opt("po", "狀態", "已確認"));
    }

    [Fact]
    public async Task NoDepartment_FallsBackToManagerGroup()
    {
        var (rig, supplier, item) = await ArrangeAsync(withOrganization: false);
        var buyer = _app.AsUser(Buyer);
        var draft = await rig.PoDraftAsync(supplier, item, 40, buyer);

        (await rig.SubmitPoAsync(buyer, draft)).StatusCode.Should().Be(HttpStatusCode.Created);
        var current = (await rig.CurrentApprovalAsync(Id(draft)))!.Value;
        OpenAssignees(current).Should().Equal([Manager], "發起人沒有部門：交給流程的備援，進銷存主管群組");
        current.GetProperty("actions").EnumerateArray()
            .Select(a => a.TryGetProperty("comment", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : "")
            .Should().Contain(c => c.Contains("找不到簽核人") && c.Contains("備援"));
    }

    [Fact]
    public async Task Cancel_FromDraftAndConfirmed_StillPlain()
    {
        var (rig, supplier, item) = await ArrangeAsync();
        var buyer = _app.AsUser(Buyer);
        var manager = _app.AsUser(Manager);
        var p = await rig.F("po");

        // 草稿直接取消：「已取消」標成可編輯，所以不算不簽就進鎖定狀態；取消時備註必填
        var d1 = await rig.PoDraftAsync(supplier, item, 4, buyer);
        var c1 = await rig.TransitionAsync(manager, d1, "po", "已取消", new() { [p["備註"]] = "供應商停產" });
        c1.StatusCode.Should().Be(HttpStatusCode.OK, await c1.Content.ReadAsStringAsync());
        (await StatusOf(rig, d1)).Should().Be(await rig.Opt("po", "狀態", "已取消"));

        // 已確認再取消
        var d2 = await rig.PoDraftAsync(supplier, item, 4, buyer);
        var confirmed = await rig.ConfirmPoAsync(buyer, d2, manager, Manager);
        var c2 = await rig.TransitionAsync(manager, confirmed, "po", "已取消", new() { [p["備註"]] = "客戶取消訂單" });
        c2.StatusCode.Should().Be(HttpStatusCode.OK, await c2.Content.ReadAsStringAsync());

        // 取消後唯讀照舊
        var cancelled = await rig.GetAsync(Id(d2));
        await manager.PutAsJsonAsync($"{Base}/records/{Id(cancelled)}", new
        {
            values = new Dictionary<string, object> { [p["備註"]] = "取消後想改" },
            rowVersion = cancelled.GetProperty("rowVersion").GetString()
        });
        // 唯讀狀態除了狀態欄位以外的輸入會被忽略（備註維持取消時填的）
        InventoryRig.Str(await rig.GetAsync(Id(cancelled)), p["備註"]).Should().Be("客戶取消訂單", "已取消是唯讀狀態");
    }

    [Fact]
    public async Task Cancel_FromDraft_NoApprovalInstance_AndNoSubmitNeeded()
    {
        var (rig, supplier, item) = await ArrangeAsync();
        var manager = _app.AsUser(Manager);
        var p = await rig.F("po");
        var draft = await rig.PoDraftAsync(supplier, item, 110, _app.AsUser(Buyer));   // 大額單也一樣，取消不走簽核

        var res = await rig.TransitionAsync(manager, draft, "po", "已取消", new() { [p["備註"]] = "供應商停產" });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        (await StatusOf(rig, draft)).Should().Be(await rig.Opt("po", "狀態", "已取消"));
        (await rig.CurrentApprovalAsync(Id(draft))).Should().BeNull("取消是一般轉移，沒有簽核實例");
    }

    [Fact]
    public async Task OpenInventory_PoWorkflowLocksConfirmedAndClosed_NotCancelled()
    {
        var (rig, _, _) = await ArrangeAsync(withOrganization: false);
        var wf = await JsonOf(await _admin.GetAsync($"{Base}/modules/{rig.Key("po")}/workflow"));
        async Task<long> Option(string label) => long.Parse((await rig.Opt("po", "狀態", label))[4..]);
        wf.GetProperty("lockedStates").EnumerateArray().Select(x => x.GetInt64())
            .Should().BeEquivalentTo([await Option("已確認"), await Option("已結案")], "已確認是簽核目標，已結案從它延伸；已取消標成可編輯");
        wf.GetProperty("editableStates").EnumerateArray().Select(x => x.GetInt64()).Should().Contain(await Option("已取消"));
    }

    [Fact]
    public async Task Open_CreatesBothFlowsAtVersion1_ActiveAndOrdered()
    {
        await ArrangeAsync(withOrganization: false);
        var large = await JsonOf(await _admin.GetAsync($"{Base}/approval-flows/inv-po-large"));
        var medium = await JsonOf(await _admin.GetAsync($"{Base}/approval-flows/inv-po-medium"));
        foreach (var flow in new[] { large, medium })
        {
            flow.GetProperty("version").GetInt32().Should().Be(1);
            flow.GetProperty("active").GetBoolean().Should().BeTrue();
        }
        (large.GetProperty("priority").GetInt32(), medium.GetProperty("priority").GetInt32()).Should().Be((1, 2));
        large.GetProperty("definition").GetProperty("stages").GetArrayLength().Should().Be(2);
        medium.GetProperty("definition").GetProperty("stages").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Export_Inventory_HasBothFlows()
    {
        await ArrangeAsync(withOrganization: false);
        var res = await _admin.GetAsync($"{Apps}/inventory/export");
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var exported = await JsonOf(res);
        exported.GetProperty("formatVersion").GetInt32().Should().Be(8);

        var flows = exported.GetProperty("approvalFlows").EnumerateArray().ToList();
        flows.Select(f => f.GetProperty("code").GetString()).Should().Equal("inv-po-large", "inv-po-medium");
        flows.Select(f => f.GetProperty("stages").GetArrayLength()).Should().Equal(2, 1);

        var po = exported.GetProperty("workflows").EnumerateArray().Single(w => w.GetProperty("module").GetString() == "po");
        var entry = po.GetProperty("transitions").EnumerateArray()
            .Single(t => t.GetProperty("from").GetString() == "draft" && t.GetProperty("to").GetString() == "confirmed");
        entry.GetProperty("approval").GetProperty("required").GetBoolean().Should().BeTrue();
        entry.GetProperty("approval").GetProperty("noMatch").GetString().Should().Be("direct");
        po.GetProperty("states").EnumerateArray().Single(s => s.GetProperty("option").GetString() == "cancelled")
            .GetProperty("editable").GetBoolean().Should().BeTrue();
    }
}
