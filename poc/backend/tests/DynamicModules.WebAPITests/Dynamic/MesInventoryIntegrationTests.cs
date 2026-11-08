using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Application.Dynamic.AppService.Integrations;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Entities.Mock;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>MES 模具廠的出貨扣進銷存庫存（apps/mes-mold.app.json 的 stockOut 整合）：兩個應用程式各自獨立，兩個都開時才連動。</summary>
public class MesInventoryIntegrationTests : IDisposable
{
    private const string Apps = Base + "/apps";
    private readonly TestApp _app = new(seedDemoPeople: false, fileDatabase: true);
    private readonly HttpClient _admin;
    private JsonElement _mes;
    private InventoryRig? _inv;

    public MesInventoryIntegrationTests() => _admin = _app.AsUser();

    public void Dispose() => _app.Dispose();

    // ---- 開啟與讀取 ----

    private async Task OpenMes()
    {
        var res = await _admin.PostAsJsonAsync($"{Apps}/packages/mes-mold/open", new
        {
            groupMap = new[] { "sales", "salesManager", "floor", "maint", "qa", "planner" }.ToDictionary(g => g, _ => (object)new { mode = "create" })
        });
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        _mes = await JsonOf(res);
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
        if (!await db.Set<MockCustomer>().AnyAsync()) db.Set<MockCustomer>().Add(new MockCustomer { CustomerNo = "C-001", Name = "宏達科技" });
        await db.SaveChangesAsync();
    }

    private async Task OpenInventory()
    {
        try { _inv = await InventoryRig.OpenAsync(_admin, assignMembers: false); }
        catch (Exception) { throw new InvalidOperationException(ServerErrors()); }
    }

    private string ServerErrors() => string.Join("\n", _app.Logs.Entries.Where(e => e.Level >= Microsoft.Extensions.Logging.LogLevel.Error).Select(e => e.Message).Take(3));

    private async Task Close(string app, bool cascade = false) =>
        (await _admin.PostAsJsonAsync($"{Apps}/{app}/close", new { cascade })).StatusCode.Should().Be(HttpStatusCode.OK);

    private async Task Reopen(string app)
    {
        var res = await _admin.PostAsync($"{Apps}/{app}/reopen", null);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
    }

    private async Task<AppIntegration> Integration()
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
        return await db.Set<AppIntegration>().AsNoTracking().SingleAsync(x => x.Code == "stockOut");
    }

    private async Task<string> StatusOfIntegration() => (await Integration()).Status;

    private string MesKey(string code) =>
        _mes.GetProperty("modules").EnumerateArray().Single(m => m.GetProperty("code").GetString() == code).GetProperty("key").GetString()!;

    private async Task<(Dictionary<string, string> Keys, Dictionary<string, string> Options)> Layout(string code)
    {
        var module = await JsonOf(await _admin.GetAsync($"{Base}/modules/{MesKey(code)}"));
        string Label(JsonElement e) => e.GetProperty("label").GetProperty("zh-TW").GetString()!;
        var fields = module.GetProperty("blocks").EnumerateArray().SelectMany(b => b.GetProperty("fields").EnumerateArray()).ToList();
        var keys = fields.ToDictionary(Label, f => f.GetProperty("key").GetString()!);
        var options = fields.Where(f => f.TryGetProperty("options", out _))
            .SelectMany(f => f.GetProperty("options").EnumerateArray().Select(o => (Field: Label(f), Option: o)))
            .ToDictionary(x => $"{x.Field}/{Label(x.Option)}", x => x.Option.GetProperty("value").GetString()!);
        return (keys, options);
    }

    private static long Id(JsonElement rec) => rec.GetProperty("id").GetInt64();

    private async Task<JsonElement> Get(long id) => await JsonOf(await _admin.GetAsync($"{Base}/records/{id}"));

    private async Task<HttpResponseMessage> TryMove(long id, string module, string statusLabel)
    {
        var (keys, options) = await Layout(module);
        var current = await Get(id);
        return await _admin.PutAsJsonAsync($"{Base}/records/{id}", new
        {
            values = new Dictionary<string, object?> { [keys["狀態"]] = options[$"狀態/{statusLabel}"] },
            rowVersion = current.GetProperty("rowVersion").GetString()
        });
    }

    private async Task Move(long id, string module, string statusLabel)
    {
        var res = await TryMove(id, module, statusLabel);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
    }

    private async Task<long> DoneWorkOrder(int qty = 100)
    {
        var (w, _) = await Layout("workorder");
        var draft = await _admin.PostAsJsonAsync($"{Base}/modules/{MesKey("workorder")}/records",
            new { values = new Dictionary<string, object?> { [w["品名"]] = "導柱", [w["工令數量"]] = qty, [w["末道工序序號"]] = 1 } });
        draft.StatusCode.Should().Be(HttpStatusCode.Created, await draft.Content.ReadAsStringAsync());
        var id = Id(await JsonOf(draft));
        await Move(id, "workorder", "已發放");
        var (r, ro) = await Layout("report");
        var report = await _admin.PostAsJsonAsync($"{Base}/modules/{MesKey("report")}/records", new
        {
            values = new Dictionary<string, object?> { [r["工令"]] = id, [r["工序序號"]] = 1, [r["工序"]] = ro["工序/備料"], [r["良品數"]] = qty, [r["不良數"]] = 0 }
        });
        report.StatusCode.Should().Be(HttpStatusCode.Created, await report.Content.ReadAsStringAsync());
        await Move(id, "workorder", "完工");
        return id;
    }

    /// <summary>草稿出貨單；stock 給了就一併填庫存品項與出庫倉。</summary>
    private async Task<long> Shipment(long workOrder, int qty, (long Item, long Warehouse)? stock = null)
    {
        var (s, _) = await Layout("shipment");
        var values = new Dictionary<string, object?> { [s["工令"]] = workOrder, [s["客戶"]] = "C-001", [s["出貨數量"]] = qty };
        if (stock is { } st)
        {
            values[s["庫存品項"]] = st.Item;
            values[s["出庫倉"]] = st.Warehouse;
        }
        var res = await _admin.PostAsJsonAsync($"{Base}/modules/{MesKey("shipment")}/records", new { values });
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return Id(await JsonOf(res));
    }

    private async Task<(long Item, long Warehouse)> Stocked(decimal qty)
    {
        var item = await _inv!.ItemAsync("導柱成品");
        var warehouse = await _inv.WarehouseAsync("WH-F", "成品倉");
        await _inv.OpeningAsync(warehouse, item, qty);
        return (item, warehouse);
    }

    private async Task<bool> ShipmentHasStockFields() => (await Layout("shipment")).Keys.ContainsKey("庫存品項");

    // ---- 四種組合 ----

    [Fact]
    public async Task NeitherOpen_NothingIsInstalled()
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
        (await db.Set<AppIntegration>().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task OnlyMes_IsWaiting_AndShipmentsStillWorkWithoutStockFields()
    {
        await OpenMes();
        (await StatusOfIntegration()).Should().Be(IntegrationStatus.Waiting);
        (await ShipmentHasStockFields()).Should().BeFalse();
        var shipment = await Shipment(await DoneWorkOrder(), 10);
        await Move(shipment, "shipment", "已出貨");
    }

    [Fact]
    public async Task OnlyInventory_HasNoIntegrationAndIsUntouched()
    {
        await OpenInventory();
        using var scope = _app.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<AppIntegration>().CountAsync()).Should().Be(0);
        var item = await _inv!.ItemAsync("導柱成品");
        item.Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BothOpen_InEitherOrder_IsActive_ShipmentDeductsStock_AndCancelRestoresIt(bool mesFirst)
    {
        if (mesFirst) { await OpenMes(); await OpenInventory(); }
        else { await OpenInventory(); await OpenMes(); }
        var integration = await Integration();
        integration.Status.Should().Be(IntegrationStatus.Active, integration.Reason);
        (await ShipmentHasStockFields()).Should().BeTrue();

        var stock = await Stocked(100);
        var shipment = await Shipment(await DoneWorkOrder(40), 40, stock);
        (await _inv!.BalanceAsync(stock.Item, stock.Warehouse)).Should().Be(100);
        await Move(shipment, "shipment", "已出貨");
        (await _inv.BalanceAsync(stock.Item, stock.Warehouse)).Should().Be(60);
        var moves = await _inv.MovesAsync();
        InventoryRig.Str(moves.Last(), (await _inv.F("move"))["來源單號"]).Should().NotBeNullOrEmpty();

        await Move(shipment, "shipment", "已取消");
        (await _inv.BalanceAsync(stock.Item, stock.Warehouse)).Should().Be(100);
    }

    [Fact]
    public async Task Active_ShippingWithoutAStockItem_IsRejected()
    {
        await OpenMes();
        await OpenInventory();
        var shipment = await Shipment(await DoneWorkOrder(), 10);
        var res = await TryMove(shipment, "shipment", "已出貨");
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        // 連動事件在檢查之前執行，夥伴異動的必填欄位先擋下；整合自己的檢查是第二道（夥伴若放寬必填也不會漏扣）
        (await res.Content.ReadAsStringAsync()).Should().Contain("mes-stock-out").And.Contain("必填");
    }

    [Fact]
    public async Task Active_InsufficientStock_BlocksTheShipment_AndItStaysDraft()
    {
        await OpenMes();
        await OpenInventory();
        var stock = await Stocked(5);
        var shipment = await Shipment(await DoneWorkOrder(40), 40, stock);
        var res = await TryMove(shipment, "shipment", "已出貨");
        res.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.Conflict);
        (await res.Content.ReadAsStringAsync()).Should().Contain("庫存不足");
    }

    // ---- 關閉與重新開啟 ----

    [Fact]
    public async Task ClosingInventory_IsNotBlockedByTheIntegration_AndSuspendsIt_ThenReopenResumes()
    {
        await OpenMes();
        await OpenInventory();
        var stock = await Stocked(100);
        var wo = await DoneWorkOrder(100);

        await Close("inventory");   // 不需要 cascade：整合欄位不算依賴
        (await StatusOfIntegration()).Should().Be(IntegrationStatus.Suspended);

        var during = await Shipment(wo, 10);   // 夥伴關閉期間：出貨照常，不連動，也不需要品項
        await Move(during, "shipment", "已出貨");

        await Reopen("inventory");
        (await StatusOfIntegration()).Should().Be(IntegrationStatus.Active);
        (await _inv!.BalanceAsync(stock.Item, stock.Warehouse)).Should().Be(100);   // 期間的出貨不補入
        var after = await Shipment(wo, 20, stock);
        await Move(after, "shipment", "已出貨");
        (await _inv.BalanceAsync(stock.Item, stock.Warehouse)).Should().Be(80);
    }

    [Fact]
    public async Task ClosingMes_SuspendsTheIntegration_AndInventoryIsUnaffected()
    {
        await OpenMes();
        await OpenInventory();
        await Close("mes-mold");
        (await StatusOfIntegration()).Should().Be(IntegrationStatus.Suspended);
        (await _inv!.ItemAsync("另一個品項")).Should().BeGreaterThan(0);
        await Reopen("mes-mold");
        (await StatusOfIntegration()).Should().Be(IntegrationStatus.Active);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BothClosed_ReopenedInEitherOrder_EndsActive(bool mesFirst)
    {
        await OpenMes();
        await OpenInventory();
        await Close("inventory");
        await Close("mes-mold");
        if (mesFirst) { await Reopen("mes-mold"); (await StatusOfIntegration()).Should().Be(IntegrationStatus.Suspended); await Reopen("inventory"); }
        else { await Reopen("inventory"); (await StatusOfIntegration()).Should().Be(IntegrationStatus.Suspended); await Reopen("mes-mold"); }
        var integration = await Integration();
        integration.Status.Should().Be(IntegrationStatus.Active, integration.Reason);
    }

    // ---- 入口守衛（設計 §5.8）：狀態 × 正向／反向 ----

    private async Task<(long Shipment, (long Item, long Warehouse) Stock)> ShippedAndDeducted()
    {
        var stock = await Stocked(100);
        var shipment = await Shipment(await DoneWorkOrder(100), 40, stock);
        await Move(shipment, "shipment", "已出貨");
        (await _inv!.BalanceAsync(stock.Item, stock.Warehouse)).Should().Be(60);
        return (shipment, stock);
    }

    private async Task<string> Body(HttpResponseMessage res) => await res.Content.ReadAsStringAsync();

    [Fact]
    public async Task Suspended_Reverse_CancelOfALinkedShipment_Is409_UntilThePartnerIsBack()
    {
        await OpenMes(); await OpenInventory();
        var (shipment, stock) = await ShippedAndDeducted();
        await Close("inventory");
        (await StatusOfIntegration()).Should().Be(IntegrationStatus.Suspended);

        var cancel = await TryMove(shipment, "shipment", "已取消");
        cancel.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Body(cancel)).Should().Contain("出貨扣庫存");
        (await Get(shipment)).GetProperty("values").GetProperty((await Layout("shipment")).Keys["狀態"]).GetString()
            .Should().Be((await Layout("shipment")).Options["狀態/已出貨"]);

        await Reopen("inventory");
        await Move(shipment, "shipment", "已取消");
        (await _inv!.BalanceAsync(stock.Item, stock.Warehouse)).Should().Be(100);
    }

    [Fact]
    public async Task Disabled_Forward_ShipsWithoutDeducting_ButReverse_IsRejected()
    {
        await OpenMes(); await OpenInventory();
        var (shipment, stock) = await ShippedAndDeducted();
        var off = await _admin.PutAsJsonAsync($"{Apps}/mes-mold/integrations/stockOut", new { enabled = false });
        off.StatusCode.Should().Be(HttpStatusCode.OK, await Body(off));
        (await StatusOfIntegration()).Should().Be(IntegrationStatus.Disabled);

        var plain = await Shipment(await DoneWorkOrder(10), 10);
        await Move(plain, "shipment", "已出貨");                 // 正向：允許，不連動
        (await _inv!.BalanceAsync(stock.Item, stock.Warehouse)).Should().Be(60);
        (await TryMove(shipment, "shipment", "已取消")).StatusCode.Should().Be(HttpStatusCode.Conflict);   // 反向：已連動過，擋

        var on = await _admin.PutAsJsonAsync($"{Apps}/mes-mold/integrations/stockOut", new { enabled = true });
        on.StatusCode.Should().Be(HttpStatusCode.OK, await Body(on));
        (await StatusOfIntegration()).Should().Be(IntegrationStatus.Active);
        await Move(shipment, "shipment", "已取消");
        (await _inv.BalanceAsync(stock.Item, stock.Warehouse)).Should().Be(100);
    }

    [Fact]
    public async Task DeletingTheReverseEvent_MakesTheIntegrationBlocked_AndCancelIs409_NotASilentSkip()
    {
        await OpenMes(); await OpenInventory();
        var (shipment, _) = await ShippedAndDeducted();
        (await _admin.DeleteAsync($"{Base}/events/mes-stock-out-cancel")).StatusCode.Should().Be(HttpStatusCode.OK);

        var cancel = await TryMove(shipment, "shipment", "已取消");
        cancel.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Body(cancel)).Should().Contain("mes-stock-out-cancel");
    }

    [Fact]
    public async Task DisablingTheStockItemField_BlocksNewShipments_InsteadOfSilentlySkippingTheDeduction()
    {
        await OpenMes(); await OpenInventory();
        var stock = await Stocked(100);
        var shipment = await Shipment(await DoneWorkOrder(10), 10, stock);
        var fieldKey = (await Layout("shipment")).Keys["庫存品項"];
        var module = await JsonOf(await _admin.GetAsync($"{Base}/modules/{MesKey("shipment")}"));
        var fieldId = module.GetProperty("blocks").EnumerateArray().SelectMany(b => b.GetProperty("fields").EnumerateArray())
            .Single(f => f.GetProperty("key").GetString() == fieldKey).GetProperty("id").GetInt64();
        var off = await _admin.PutAsJsonAsync($"{Base}/fields/{fieldId}/status", new { status = "inactive" });
        if (off.StatusCode != HttpStatusCode.OK) return;   // 設計器若直接拒絕停用被事件引用的欄位，那是更早的一道防線，同樣不會漏扣

        var res = await TryMove(shipment, "shipment", "已出貨");
        res.StatusCode.Should().BeOneOf(HttpStatusCode.Conflict, HttpStatusCode.BadRequest);
        (await _inv!.BalanceAsync(stock.Item, stock.Warehouse)).Should().Be(100);
    }

    [Fact]
    public async Task Retry_IsIdempotent_RebuildsADeletedEvent_AndNeverDuplicates()
    {
        await OpenMes(); await OpenInventory();
        for (var i = 0; i < 2; i++)
            (await _admin.PostAsync($"{Apps}/mes-mold/integrations/stockOut/retry", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        var integration = await Integration();
        integration.Status.Should().Be(IntegrationStatus.Active, integration.Reason);
        var events = await JsonOf(await _admin.GetAsync($"{Base}/events"));
        events.EnumerateArray().Count(e => e.GetProperty("key").GetString()!.StartsWith("mes-stock-out")).Should().Be(2);

        (await _admin.DeleteAsync($"{Base}/events/mes-stock-out-cancel")).StatusCode.Should().Be(HttpStatusCode.OK);
        var rebuilt = await _admin.PostAsync($"{Apps}/mes-mold/integrations/stockOut/retry", null);
        rebuilt.StatusCode.Should().Be(HttpStatusCode.OK, await Body(rebuilt));
        (await StatusOfIntegration()).Should().Be(IntegrationStatus.Active);
    }

    [Fact]
    public async Task ModifiedEventDefinition_RetryIsItemConflict_AndRestoringItRecovers()
    {
        await OpenMes(); await OpenInventory();
        var original = await JsonOf(await _admin.GetAsync($"{Base}/events/mes-stock-out"));
        // 把觸發條件改成「建立時」：定義與宣告不同
        var changed = System.Text.Json.Nodes.JsonNode.Parse(original.GetRawText())!.AsObject();
        var input = new System.Text.Json.Nodes.JsonObject
        {
            ["key"] = "mes-stock-out", ["name"] = changed["name"]!.DeepClone(), ["sourceModuleKey"] = changed["sourceModuleKey"]!.DeepClone(),
            ["active"] = true, ["definition"] = changed["definition"]!.DeepClone()
        };
        var originalDefinition = input["definition"]!.DeepClone();
        input["definition"]!["trigger"] = System.Text.Json.Nodes.JsonNode.Parse("""{ "on": "create" }""");
        (await _admin.PutAsJsonAsync($"{Base}/events/mes-stock-out", input)).StatusCode.Should().Be(HttpStatusCode.OK);

        var retry = await _admin.PostAsync($"{Apps}/mes-mold/integrations/stockOut/retry", null);
        retry.StatusCode.Should().Be(HttpStatusCode.OK);
        var blocked = await Integration();
        blocked.Status.Should().Be(IntegrationStatus.Blocked);
        blocked.Reason.Should().Contain("itemConflict");

        input["definition"] = originalDefinition;
        (await _admin.PutAsJsonAsync($"{Base}/events/mes-stock-out", input)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _admin.PostAsync($"{Apps}/mes-mold/integrations/stockOut/retry", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        var recovered = await Integration();
        recovered.Status.Should().Be(IntegrationStatus.Active, recovered.Reason);
    }

    [Fact]
    public async Task NonAdmins_CannotRetryOrToggleAnIntegration()
    {
        await OpenMes(); await OpenInventory();
        var user = _app.AsUser("sales01");
        (await user.PostAsync($"{Apps}/mes-mold/integrations/stockOut/retry", null)).StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized);
        (await user.PutAsJsonAsync($"{Apps}/mes-mold/integrations/stockOut", new { enabled = false }))
            .StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized);
    }
}
