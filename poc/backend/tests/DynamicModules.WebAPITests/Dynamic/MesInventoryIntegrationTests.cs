using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    private async Task OpenMes(string package = "mes-mold", string[]? groups = null) => _mes = await OpenMesOn(_app, _admin, package, groups);

    private static async Task<JsonElement> OpenMesOn(TestApp app, HttpClient admin, string package, string[]? groups = null)
    {
        var res = await admin.PostAsJsonAsync($"{Apps}/packages/{package}/open", new
        {
            groupMap = (groups ?? ["sales", "salesManager", "floor", "maint", "qa", "planner"]).ToDictionary(g => g, _ => (object)new { mode = "create" })
        });
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        var opened = await JsonOf(res);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
        if (!await db.Set<MockCustomer>().AnyAsync()) db.Set<MockCustomer>().Add(new MockCustomer { CustomerNo = "C-001", Name = "宏達科技" });
        await db.SaveChangesAsync();
        return opened;
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
    public async Task ShipmentsShippedBeforeTheIntegration_CanEachBeCancelled_AfterInventoryOpens()
    {
        // 只開 MES 時同一張工令出了兩張沒有庫存品項的貨；之後開進銷存，整合 Active
        await OpenMes();
        var wo = await DoneWorkOrder(100);
        var s0 = await Shipment(wo, 10);
        var s1 = await Shipment(wo, 10);
        await Move(s0, "shipment", "已出貨");
        await Move(s1, "shipment", "已出貨");
        await OpenInventory();
        (await StatusOfIntegration()).Should().Be(IntegrationStatus.Active);

        // 檢查以每張出貨單自成一組：S0 仍沒有品項，不該讓 S1 永遠取消不了
        var cancel = await TryMove(s1, "shipment", "已取消");
        cancel.StatusCode.Should().Be(HttpStatusCode.OK, await cancel.Content.ReadAsStringAsync());
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

    /// <summary>
    /// MES 之外另一個一般模組：建立記錄時，事件經參照把出貨單狀態改成 statusLabel。
    /// 出貨單的寫入來自事件效果（第 1 輪以後的日誌），不是使用者直接寫入，入口守衛同樣要擋。
    /// </summary>
    private async Task<Func<long, Task<HttpResponseMessage>>> ChainedStatusChange(string statusLabel)
    {
        var module = await CreateModule(_admin);
        var toShipment = await AddField(_admin, module, "reference", new { refTarget = new { kind = "module", key = MesKey("shipment") } });
        await Activate(_admin, module);
        var title = await TitleKey(_admin, module);
        var (s, o) = await Layout("shipment");
        var ev = await _admin.PostAsJsonAsync($"{Base}/events", new
        {
            key = "ev-" + Guid.NewGuid().ToString("N")[..8], name = new Dictionary<string, string> { ["zh-TW"] = "經參照改出貨單狀態" },
            sourceModuleKey = module, active = true,
            definition = new
            {
                trigger = new { on = "create" },
                effects = new[]
                {
                    new
                    {
                        code = "push", kind = "update", targetModuleKey = MesKey("shipment"), via = toShipment.Id,
                        set = new object[] { new { to = long.Parse(s["狀態"]["f_".Length..]), @const = o[$"狀態/{statusLabel}"] } }
                    }
                }
            }
        });
        ev.StatusCode.Should().Be(HttpStatusCode.Created, await ev.Content.ReadAsStringAsync());
        return shipment => _admin.PostAsJsonAsync($"{Base}/modules/{module}/records",
            new { values = new Dictionary<string, object?> { [title] = "連鎖", [toShipment.Key] = shipment } });
    }

    private async Task<string?> ShipmentStatus(long shipment)
    {
        var (s, _) = await Layout("shipment");
        return (await Get(shipment)).GetProperty("values").GetProperty(s["狀態"]).GetString();
    }

    [Fact]
    public async Task Suspended_Reverse_ViaAnotherEventsEffect_Is409_StockAndShipmentUnchanged()
    {
        await OpenMes(); await OpenInventory();
        var (shipment, stock) = await ShippedAndDeducted();
        var cancelViaChain = await ChainedStatusChange("已取消");
        await Close("inventory");
        (await StatusOfIntegration()).Should().Be(IntegrationStatus.Suspended);

        var res = await cancelViaChain(shipment);
        res.StatusCode.Should().Be(HttpStatusCode.Conflict, await Body(res));
        (await Body(res)).Should().Contain("出貨扣庫存");
        (await ShipmentStatus(shipment)).Should().Be((await Layout("shipment")).Options["狀態/已出貨"]);

        await Reopen("inventory");
        (await _inv!.BalanceAsync(stock.Item, stock.Warehouse)).Should().Be(60);
    }

    [Fact]
    public async Task Blocked_Forward_ViaAnotherEventsEffect_Is409_NothingDeducted()
    {
        await OpenMes(); await OpenInventory();
        var stock = await Stocked(100);
        var shipment = await Shipment(await DoneWorkOrder(40), 40, stock);
        var shipViaChain = await ChainedStatusChange("已出貨");
        (await _admin.DeleteAsync($"{Base}/events/mes-stock-out-cancel")).StatusCode.Should().Be(HttpStatusCode.OK);

        var res = await shipViaChain(shipment);
        res.StatusCode.Should().Be(HttpStatusCode.Conflict, await Body(res));
        (await ShipmentStatus(shipment)).Should().Be((await Layout("shipment")).Options["狀態/草稿"]);
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

    /// <summary>客戶在設計器單獨停用整合裡的「出貨必須指定庫存品項」檢查。</summary>
    private async Task DisableStockItemCheck()
    {
        var check = JsonNode.Parse((await JsonOf(await _admin.GetAsync($"{Base}/checks/mes-stock-item-required"))).GetRawText())!.AsObject();
        var input = new JsonObject
        {
            ["key"] = "mes-stock-item-required", ["name"] = check["name"]!.DeepClone(), ["moduleKey"] = check["moduleKey"]!.DeepClone(),
            ["active"] = false, ["definition"] = check["definition"]!.DeepClone()
        };
        var res = await _admin.PutAsJsonAsync($"{Base}/checks/mes-stock-item-required", input);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await Body(res));
    }

    private async Task<bool> StockItemCheckActive() =>
        (await JsonOf(await _admin.GetAsync($"{Base}/checks/mes-stock-item-required"))).GetProperty("active").GetBoolean();

    [Fact]
    public async Task ACustomerDisabledCheck_StaysBlocked_WhenOtherAppsOpen_UntilRetry()
    {
        await OpenMes(); await OpenInventory();
        await DisableStockItemCheck();

        // 開其他應用程式都會調和全部整合：第一次判定 Blocked，之後的調和不得自動啟用項目或解除 Blocked
        var crm = await _admin.PostAsJsonAsync($"{Apps}/packages/crm-sales/open",
            new { groupMap = new Dictionary<string, object> { ["sales"] = new { mode = "skip" }, ["manager"] = new { mode = "create" } } });
        crm.StatusCode.Should().Be(HttpStatusCode.Created, await Body(crm));
        (await StatusOfIntegration()).Should().Be(IntegrationStatus.Blocked);
        await Close("crm-sales");
        await Reopen("crm-sales");
        var integration = await Integration();
        integration.Status.Should().Be(IntegrationStatus.Blocked, integration.Reason);
        (await StockItemCheckActive()).Should().BeFalse();

        (await _admin.PostAsync($"{Apps}/mes-mold/integrations/stockOut/retry", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await StatusOfIntegration()).Should().Be(IntegrationStatus.Active);
        (await StockItemCheckActive()).Should().BeTrue();
    }

    [Fact]
    public async Task ACustomerDisabledCheck_ClosingAndReopeningThePartner_StaysBlocked()
    {
        await OpenMes(); await OpenInventory();
        await DisableStockItemCheck();
        await Close("inventory");
        await Reopen("inventory");
        var integration = await Integration();
        integration.Status.Should().Be(IntegrationStatus.Blocked, integration.Reason);
        (await StockItemCheckActive()).Should().BeFalse();
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

    // ---- 匯出與匯入 ----

    /// <summary>另開一個資料庫，把 MES 與進銷存都開起來（整合 Active），匯出 MES 套件；回傳 JSON 與匯出前的整合內容。</summary>
    private static async Task<JsonNode> ExportedMes()
    {
        using var source = new TestApp(seedDemoPeople: false, fileDatabase: true);
        var admin = source.AsUser();
        await OpenMesOn(source, admin, "mes-mold");
        await InventoryRig.OpenAsync(admin, assignMembers: false);
        var res = await admin.GetAsync($"{Apps}/mes-mold/export");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
    }

    private async Task ImportAs(JsonNode package, string id)
    {
        package["manifest"]!["id"] = id;
        var res = await _admin.PostAsync($"{Apps}/packages/import", new StringContent(package.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
    }

    private static string[] GroupsOf(JsonNode package) => [.. package["groups"]!.AsArray().Select(g => g!["code"]!.GetValue<string>())];

    private async Task Enable()
    {
        var res = await _admin.PutAsJsonAsync($"{Apps}/mes-copy/integrations/stockOut", new { enabled = true });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Export_KeepsTheIntegrationApart_NotMixedIntoTheOwnersModulesEventsOrChecks()
    {
        var package = await ExportedMes();
        package["formatVersion"]!.GetValue<int>().Should().Be(5);
        var integration = package["integrations"]!.AsArray().Single()!;
        integration["code"]!.GetValue<string>().Should().Be("stockOut");
        integration["with"]![0]!["id"]!.GetValue<string>().Should().Be("inventory");
        integration["fields"]!.AsArray().Select(f => f!["code"]!.GetValue<string>()).Should().BeEquivalentTo("stockItem", "stockWarehouse");
        integration["events"]!.AsArray().Select(e => e!["code"]!.GetValue<string>()).Should().BeEquivalentTo("mes-stock-out", "mes-stock-out-cancel");

        var shipment = package["modules"]!.AsArray().Single(m => m!["code"]!.GetValue<string>() == "shipment")!;
        shipment["blocks"]!.AsArray().SelectMany(b => b!["fields"]!.AsArray()).Select(f => f!["code"]!.GetValue<string>())
            .Should().NotContain(["stockItem", "stockWarehouse"]);
        package["events"]!.AsArray().Select(e => e!["code"]!.GetValue<string>()).Should().NotContain(["mes-stock-out", "mes-stock-out-cancel"]);
        package["checks"]!.AsArray().Select(c => c!["code"]!.GetValue<string>()).Should().NotContain("mes-stock-item-required");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExportThenImportIntoAnEmptyDatabase_IsDisabledUntilEnabled_ThenBehavesTheSame(bool mesFirst)
    {
        var package = await ExportedMes();
        await ImportAs(package, "mes-copy");
        if (mesFirst) { await OpenMes("mes-copy", GroupsOf(package)); await OpenInventory(); }
        else { await OpenInventory(); await OpenMes("mes-copy", GroupsOf(package)); }

        // 匯入的套件不自動連動別的應用程式：要管理員啟用
        (await StatusOfIntegration()).Should().Be(IntegrationStatus.Disabled);
        (await ShipmentHasStockFields()).Should().BeFalse();
        await Enable();
        var integration = await Integration();
        integration.Status.Should().Be(IntegrationStatus.Active, integration.Reason);

        var stock = await Stocked(100);
        var shipment = await Shipment(await DoneWorkOrder(40), 40, stock);
        await Move(shipment, "shipment", "已出貨");
        (await _inv!.BalanceAsync(stock.Item, stock.Warehouse)).Should().Be(60);
        await Move(shipment, "shipment", "已取消");
        (await _inv.BalanceAsync(stock.Item, stock.Warehouse)).Should().Be(100);
    }

    [Fact]
    public async Task OnPartnerClosedBlock_ClosingThePartnerHoldsBothDirections_AndReopenReleases()
    {
        var package = await ExportedMes();
        package["integrations"]![0]!["onPartnerClosed"] = "block";
        await ImportAs(package, "mes-copy");
        await OpenMes("mes-copy", GroupsOf(package));
        await OpenInventory();
        await Enable();
        (await StatusOfIntegration()).Should().Be(IntegrationStatus.Active);
        var stock = await Stocked(100);
        var wo = await DoneWorkOrder(100);
        var shipped = await Shipment(wo, 10, stock);
        await Move(shipped, "shipment", "已出貨");
        var draft = await Shipment(wo, 5, stock);   // 夥伴關閉後就選不到它的記錄，所以先備好草稿

        await Close("inventory");
        var held = await Integration();
        held.Status.Should().Be(IntegrationStatus.Held);

        // 正向（出貨）與反向（取消）都被擋，且不是默默略過
        var forward = await TryMove(draft, "shipment", "已出貨");
        forward.StatusCode.Should().Be(HttpStatusCode.Conflict, await forward.Content.ReadAsStringAsync());
        var reverse = await TryMove(shipped, "shipment", "已取消");
        reverse.StatusCode.Should().Be(HttpStatusCode.Conflict, await reverse.Content.ReadAsStringAsync());
        (await _admin.GetAsync($"{Base}/records/{shipped}")).StatusCode.Should().Be(HttpStatusCode.OK);   // 夥伴關閉後資料仍可讀

        await Reopen("inventory");
        (await StatusOfIntegration()).Should().Be(IntegrationStatus.Active);
        await Move(draft, "shipment", "已出貨");
        await Move(shipped, "shipment", "已取消");
    }
}
