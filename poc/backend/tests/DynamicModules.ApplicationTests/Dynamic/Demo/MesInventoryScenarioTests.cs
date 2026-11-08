using System.Text.Json;
using DynamicModules.Application.Dynamic.RecordService.Data;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic.Demo;

/// <summary>MES 報價與生產、進銷存三個情境產生的資料（用共用的產生結果）。</summary>
[Collection(DemoGeneratedCollection.Name)]
public class MesInventoryScenarioTests(DemoGeneratedFixture fx)
{
    private static decimal Num(DynamicRecordResponse r, string key) =>
        r.Values.GetValueOrDefault(key) is { ValueKind: JsonValueKind.Number } v ? v.GetDecimal() : 0m;

    private static long? Ref(DynamicRecordResponse r, string key) =>
        r.Values.GetValueOrDefault(key) is { ValueKind: JsonValueKind.Number } v ? v.GetInt64() : null;

    private async Task<Dictionary<string, int>> StatusCounts(string module)
    {
        var status = await fx.Key(module, "狀態");
        var labels = (await fx.Module(module)).Blocks.SelectMany(b => b.Fields).Single(f => f.Key == status).Options
            .ToDictionary(o => o.Value, o => o.Label.Resolve("zh-TW"));
        return (await fx.Records(module)).GroupBy(r => labels[DemoGeneratedFixture.Text(r, status)!]).ToDictionary(g => g.Key, g => g.Count());
    }

    [Fact]
    public async Task Quotes_AllStatuses_And_FiveOrdersConvertedWithTheQuoteTraced()
    {
        fx.Generated();

        var quotes = await StatusCounts("mes-quote");
        quotes.Keys.Should().BeEquivalentTo("草稿", "待核准", "已核准", "已退回");
        quotes.Values.Sum().Should().Be(8);

        var fromQuote = await fx.Key("mes-order", "來源報價單");
        var converted = (await fx.Records("mes-order")).Where(o => Ref(o, fromQuote) is not null).ToList();
        converted.Should().HaveCount(5);
        converted.Select(o => Ref(o, fromQuote)).Distinct().Should().HaveCount(4, "其中一張報價單分兩次轉單");
    }

    [Fact]
    public async Task WorkOrders_CoverEveryEnding_AndShipmentsNeverExceedWhatWasCompleted()
    {
        fx.Generated();

        var counts = await StatusCounts("mes-workorder");
        counts.Keys.Should().Contain(["草稿", "已發放", "生產中", "暫停", "完工", "已取消"]);

        var workOrders = await fx.Records("mes-workorder");
        var completed = await fx.Key("mes-workorder", "已完工量");
        var qty = await fx.Key("mes-workorder", "工令數量");
        var shipped = await fx.Key("mes-workorder", "已出貨量");
        var doneLabel = (await fx.Module("mes-workorder")).Blocks.SelectMany(b => b.Fields).Single(f => f.Label.Resolve("zh-TW") == "狀態")
            .Options.Single(o => o.Label.Resolve("zh-TW") == "完工").Value;
        var status = await fx.Key("mes-workorder", "狀態");
        var done = workOrders.Where(w => DemoGeneratedFixture.Text(w, status) == doneLabel).ToList();
        done.Should().NotBeEmpty();
        foreach (var w in done)
        {
            Num(w, completed).Should().BeGreaterThan(0);
            Num(w, shipped).Should().BeLessThanOrEqualTo(Num(w, completed), "出貨量不超過完工量");
        }
        done.Should().Contain(w => Num(w, shipped) == Num(w, qty), "至少一張全數出貨");
        done.Should().Contain(w => Num(w, shipped) > 0 && Num(w, shipped) < Num(w, completed), "至少一張只出一部分");
    }

    [Fact]
    public async Task Reports_OneWrongEntryIsReversed_AndOverdueWorkOrdersExist()
    {
        fx.Generated();

        var reverses = await fx.Key("mes-report", "沖銷對象");
        (await fx.Records("mes-report")).Count(r => Ref(r, reverses) is not null).Should().Be(1);

        var due = await fx.Key("mes-workorder", "交期");
        var status = await fx.Key("mes-workorder", "狀態");
        var open = (await fx.Module("mes-workorder")).Blocks.SelectMany(b => b.Fields).Single(f => f.Key == status).Options
            .Where(o => o.Label.Resolve("zh-TW") is "已發放" or "生產中" or "暫停").Select(o => o.Value).ToHashSet();
        (await fx.Records("mes-workorder")).Count(w => open.Contains(DemoGeneratedFixture.Text(w, status)!)
                                                      && DemoDb.Date(DemoGeneratedFixture.Text(w, due)) < DemoTestHost.Today)
            .Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Inventory_StockNeverNegative_ReceiptsPosted_CancelledShipmentRestoredStock()
    {
        fx.Generated();

        var po = await StatusCounts("inv-po");
        po.Keys.Should().Contain(["草稿", "已確認", "已結案"]);
        (await StatusCounts("inv-receipt")).Should().ContainKey("已過帳");
        var shipments = await StatusCounts("inv-shipment");
        shipments.Should().ContainKey("已出貨").And.ContainKey("已取消");

        var item = await fx.Key("inv-move", "品項");
        var warehouse = await fx.Key("inv-move", "倉庫");
        var qty = await fx.Key("inv-move", "數量（入正出負）");
        var balances = (await fx.Records("inv-move")).GroupBy(m => (Ref(m, item), Ref(m, warehouse)))
            .ToDictionary(g => g.Key, g => g.Sum(m => Num(m, qty)));
        balances.Should().NotBeEmpty().And.OnlyContain(b => b.Value >= 0, "沒有負庫存");
        balances.Values.Sum().Should().BeGreaterThan(0);
    }
}
