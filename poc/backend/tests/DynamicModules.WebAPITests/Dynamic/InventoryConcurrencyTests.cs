using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;
using static DynamicModules.WebAPITests.Dynamic.InventoryRig;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 進銷存的併發與重送（檔案型 SQLite：真正的 WriteGate 與 BEGIN IMMEDIATE）。全部透過內建套件的宣告達成，沒有專用程式。
/// </summary>
public class InventoryConcurrencyTests : IDisposable
{
    private readonly TestApp _app = new(fileDatabase: true);
    private readonly HttpClient _admin;

    public InventoryConcurrencyTests() => _admin = _app.AsUser();

    public void Dispose() => _app.Dispose();

    private sealed record World(InventoryRig Rig, long Item, long Warehouse, long Customer, long Supplier);

    private async Task<World> WorldAsync(decimal opening)
    {
        var rig = await OpenAsync(_admin, assignMembers: false);
        var item = await rig.ItemAsync("軸承");
        var wh = await rig.WarehouseAsync("A", "成品倉");
        var customer = await rig.CustomerAsync("立新機械");
        var supplier = await rig.SupplierAsync("大成五金");
        if (opening > 0) await rig.OpeningAsync(wh, item, opening);
        // 欄位對照有快取（Dictionary），併發送出前先載好，避免測試工具自己的競態
        foreach (var code in new[] { "shipment", "shipment-line", "receipt", "receipt-line", "move" }) await rig.F(code);
        return new World(rig, item, wh, customer, supplier);
    }

    /// <summary>
    /// 503（WriteGate 排隊逾時，交易根本沒開始、什麼都沒寫）照 Retry-After 的語意重送，就像真的客戶端。
    /// 全部測試平行跑時 CPU 吃緊，30 個以上同時寫入偶爾會排超過 30 秒；其他狀態碼原樣回傳。
    /// </summary>
    private static async Task<HttpResponseMessage> RetryBusy(Func<Task<HttpResponseMessage>> send)
    {
        for (var attempt = 1; ; attempt++)
        {
            var res = await send();
            if (res.StatusCode != HttpStatusCode.ServiceUnavailable || attempt == 5) return res;
            await Task.Delay(200 * attempt);
        }
    }

    private async Task<int> CountAsync(string moduleKey) =>
        (await JsonOf(await _admin.GetAsync($"{Base}/modules/{moduleKey}/records/paged"))).GetProperty("totalCount").GetInt32();

    [Fact]
    public async Task FiftyShipmentsOfThreePostedAtOnce_AgainstOneHundred_ExactlyThirtyThreeSucceed()
    {
        var w = await WorldAsync(100);
        var drafts = new List<JsonElement>();
        for (var i = 0; i < 50; i++) drafts.Add(await w.Rig.ShipmentDraftAsync(w.Customer, w.Warehouse, w.Item, 3));

        var responses = await Task.WhenAll(drafts.Select(d => RetryBusy(() => w.Rig.TransitionAsync(_admin, d, "shipment", "已出貨"))));

        var codes = responses.Select(r => r.StatusCode).ToList();
        codes.Should().NotContain(HttpStatusCode.InternalServerError);
        codes.Count(c => c == HttpStatusCode.OK).Should().Be(33);
        codes.Count(c => c == HttpStatusCode.BadRequest).Should().Be(17);
        (await w.Rig.BalanceAsync(w.Item, w.Warehouse)).Should().Be(1m);

        // 依提交順序（Id）累計，任何時刻都不為負
        var m = await w.Rig.F("move");
        var running = 0m;
        foreach (var move in await w.Rig.MovesAsync())
        {
            running += Num(move, m["數量（入正出負）"]) ?? 0;
            running.Should().BeGreaterThanOrEqualTo(0m);
        }
    }

    [Fact]
    public async Task TwentyResendsOfOneShipmentCreate_WithTheSameKey_MakeOneShipment_AndResentPostingDeductsOnce()
    {
        var w = await WorldAsync(100);
        var key = "ship-" + Guid.NewGuid().ToString("N");

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => RetryBusy(async () =>
        {
            var req = await w.Rig.ShipmentRequestAsync(w.Customer, w.Warehouse, [(w.Item, 3)]);
            req.Headers.Add("Idempotency-Key", key);
            return await _admin.SendAsync(req);
        })));

        responses.Select(r => r.StatusCode).Should().OnlyContain(c => c == HttpStatusCode.Created);
        var ids = await Task.WhenAll(responses.Select(async r => Id(await JsonOf(r))));
        ids.Distinct().Should().HaveCount(1);
        (await CountAsync(w.Rig.Key("shipment"))).Should().Be(1);

        // 過帳也重送 10 次（同一把 key）：只扣一次
        var draft = await w.Rig.GetAsync(ids[0]);
        var postKey = "post-" + Guid.NewGuid().ToString("N");
        var posts = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => RetryBusy(() => w.Rig.TransitionAsync(_admin, draft, "shipment", "已出貨", idempotencyKey: postKey))));
        posts.Select(r => r.StatusCode).Should().OnlyContain(c => c == HttpStatusCode.OK);
        (await w.Rig.BalanceAsync(w.Item, w.Warehouse)).Should().Be(97m);
        (await w.Rig.MovesAsync()).Should().HaveCount(2);

        // 不帶 key 重送同一個過帳：舊的 rowVersion 回 409，仍不重複扣
        (await w.Rig.TransitionAsync(_admin, draft, "shipment", "已出貨")).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await w.Rig.BalanceAsync(w.Item, w.Warehouse)).Should().Be(97m);
    }

    [Fact]
    public async Task ReceiptsAndShipmentsPostedAtOnce_BalanceEqualsTheSumOfCommittedMoves()
    {
        var w = await WorldAsync(20);
        var receipts = new List<JsonElement>();
        for (var i = 0; i < 10; i++) receipts.Add(await w.Rig.ReceiptDraftAsync(w.Supplier, w.Warehouse, w.Item, 5));
        var shipments = new List<JsonElement>();
        for (var i = 0; i < 20; i++) shipments.Add(await w.Rig.ShipmentDraftAsync(w.Customer, w.Warehouse, w.Item, 4));

        var kinds = receipts.Select(r => (Kind: "in", Record: r, Code: "receipt", To: "已過帳"))
            .Concat(shipments.Select(x => (Kind: "out", Record: x, Code: "shipment", To: "已出貨")))
            .OrderBy(_ => Guid.NewGuid()).ToList();
        var responses = await Task.WhenAll(kinds.Select(k => RetryBusy(() => w.Rig.TransitionAsync(_admin, k.Record, k.Code, k.To))));
        var results = kinds.Zip(responses, (k, r) => (k.Kind, r.StatusCode)).ToList();
        var odd = await Task.WhenAll(responses.Where(r => r.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.BadRequest))
            .Select(async r => $"{(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}"));
        odd.Should().BeEmpty();

        results.Select(r => r.StatusCode).Should().NotContain(HttpStatusCode.InternalServerError);
        results.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK || r.StatusCode == HttpStatusCode.BadRequest);
        results.Where(r => r.Kind == "in").Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK, "進貨不會被檢查擋");
        var shipped = results.Count(r => r.Kind == "out" && r.StatusCode == HttpStatusCode.OK);

        var expected = 20m + 10 * 5m - shipped * 4m;
        (await w.Rig.BalanceAsync(w.Item, w.Warehouse)).Should().Be(expected).And.BeGreaterThanOrEqualTo(0m);
        (await w.Rig.MovesAsync()).Should().HaveCount(1 + 10 + shipped);
    }

    [Fact]
    public async Task TenConcurrentReversalsOfOneMove_SucceedExactlyOnce()
    {
        var w = await WorldAsync(10);
        var move = (await w.Rig.MovesAsync()).Single();

        var responses = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => RetryBusy(() => _admin.PostAsJsonAsync($"{Base}/records/{Id(move)}/reverse", new { values = new Dictionary<string, object>() }))));

        responses.Count(r => r.StatusCode == HttpStatusCode.Created).Should().Be(1);
        responses.Where(r => r.StatusCode != HttpStatusCode.Created).Select(r => r.StatusCode).Should().OnlyContain(c => c == HttpStatusCode.Conflict);
        (await w.Rig.MovesAsync()).Should().HaveCount(2);
        (await w.Rig.BalanceAsync(w.Item, w.Warehouse)).Should().Be(0m);
    }
}
