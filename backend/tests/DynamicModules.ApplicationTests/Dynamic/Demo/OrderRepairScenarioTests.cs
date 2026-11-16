using System.Globalization;
using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.DemoService;
using DynamicModules.Application.Dynamic.DemoService.Scenarios;
using DynamicModules.Application.Dynamic.DemoService.Commands.GenerateDemoData;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetRecord;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetRecordHistory;
using DynamicModules.Application.Dynamic.SchemaService.Data;
using DynamicModules.Application.Dynamic.SchemaService.Queries.GetModuleSchema;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic.Demo;

[Collection(DemoGeneratedCollection.Name)]
public class OrderRepairScenarioTests(DemoGeneratedFixture fx)
{
    private static readonly DateOnly Today = DemoTestHost.Today;

    private static DateOnly Date(string? s) => DateOnly.ParseExact(s!, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private void Generated() => fx.Generated();

    /// <summary>OrderScenario 直接建立的 40 張訂單（不含由報價單轉出的）。</summary>
    private async Task<List<DynamicRecordResponse>> StandardOrders(string userId = "admin")
    {
        var fromQuote = await fx.Key("mes-order", "來源報價單");
        return [.. (await fx.Records("mes-order", userId)).Where(o => o.Values.GetValueOrDefault(fromQuote) is not { ValueKind: JsonValueKind.Number })];
    }

    private async Task<DynamicRecordResponse> Get(long id) => (DynamicRecordResponse)(await fx.Host.Send(new GetRecordRequest(id))).Data!;

    private async Task<IReadOnlyList<RecordHistoryResponse>> History(long id) =>
        ((PagedResponse<RecordHistoryResponse>)(await fx.Host.Send(new GetRecordHistoryRequest(id, 1, 200, "zh-TW"))).Data!).Items;

    [Fact]
    public async Task Orders_40_WithLines_1To6()
    {
        Generated();
        ((DemoGenerateResult)fx.Result.Data!).Steps.Should().BeGreaterThan(90);
        var orders = await StandardOrders();

        orders.Should().HaveCount(40);
        foreach (var o in orders)
            (await Get(o.Id)).Details!["mes-order-line"].Count.Should().BeInRange(1, 6);
    }

    [Fact]
    public async Task Orders_CreatedAtSimulatedDate()
    {
        Generated();
        var orderDate = await fx.Key("mes-order", "訂單日期");

        foreach (var o in await StandardOrders())
        {
            var date = Date(DemoGeneratedFixture.Text(o, orderDate));
            DateOnly.FromDateTime(o.CreatedTime).Should().Be(date);
            o.CreatedTime.Hour.Should().BeInRange(8, 17);
            o.CreatedUserId.Should().BeOneOf("sales01", "sales02");
            o.Title.Should().StartWith($"SO-{date:yyyyMM}-");
        }
    }

    [Fact]
    public async Task Orders_StatusMix_AndOverdue()
    {
        Generated();
        var status = await fx.Key("mes-order", "狀態");
        var due = await fx.Key("mes-order", "交期");
        var labels = new[] { "草稿", "已確認", "出貨中", "已結案", "取消" };
        var opts = new Dictionary<string, string>();
        foreach (var l in labels) opts[await fx.Opt("mes-order", "狀態", l)] = l;
        var orders = await StandardOrders();

        var byStatus = orders.Select(o => opts[DemoGeneratedFixture.Text(o, status)!]).ToList();
        byStatus.Distinct().Should().BeEquivalentTo(labels);
        byStatus.Count(s => s == "取消").Should().Be(2);
        orders.Count(o => Date(DemoGeneratedFixture.Text(o, due)) < Today
                          && opts[DemoGeneratedFixture.Text(o, status)!] is "已確認" or "出貨中")
            .Should().BeGreaterThanOrEqualTo(4);
        var reason = await fx.Key("mes-order", "取消原因");
        orders.Where(o => opts[DemoGeneratedFixture.Text(o, status)!] == "取消")
            .Should().OnlyContain(o => !string.IsNullOrEmpty(DemoGeneratedFixture.Text(o, reason)));
    }

    [Fact]
    public async Task Orders_ScopeAndPricing()
    {
        Generated();
        var sales01 = await StandardOrders("sales01");
        sales01.Should().HaveCount(20).And.OnlyContain(o => o.OwnerUserId == "sales01");
        (await StandardOrders("manager01")).Should().HaveCount(40);

        var schema = (ModuleSchemaResponse)(await fx.As("floor01", new GetModuleSchemaRequest("mes-order"))).Data!;
        var labels = schema.Blocks.SelectMany(b => b.Fields).Concat(schema.Details!.SelectMany(d => d.Fields))
            .Select(f => f.Label.Resolve("zh-TW")).ToList();
        labels.Should().Contain("數量").And.NotContain(["單價", "金額", "未稅合計", "含稅總額"]);
    }

    [Fact]
    public async Task Orders_HistoryByOtherUser()
    {
        Generated();
        var managerUpdates = 0;
        var qtyChanges = 0;
        foreach (var o in await StandardOrders())
        {
            var history = await History(o.Id);
            var manager = history.Where(h => h.Operation == "update" && h.ChangedUserId == "manager01").ToList();
            managerUpdates += manager.Count;
            // 歷程時間是推進當下的模擬時間，不是建立時間
            manager.Where(h => h.ChangedTime.Date <= o.CreatedTime.Date).Should().BeEmpty();
            qtyChanges += history.Count(h => h.Operation == "update" && h.Detail != null
                                             && h.FieldLabel!.Resolve("zh-TW") == "數量" && h.ChangedUserId == o.CreatedUserId);
        }

        managerUpdates.Should().BeGreaterThan(0);
        qtyChanges.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Repairs_50_AllStatuses_GroupScope()
    {
        Generated();
        var status = await fx.Key("mes-repair", "狀態");
        var labels = new[] { "待處理", "處理中", "待驗收", "結案" };
        var opts = new Dictionary<string, string>();
        foreach (var l in labels) opts[await fx.Opt("mes-repair", "狀態", l)] = l;
        var repairs = await fx.Records("mes-repair");

        repairs.Should().HaveCount(50);
        repairs.Select(r => opts[DemoGeneratedFixture.Text(r, status)!]).Distinct().Should().BeEquivalentTo(labels);
        repairs.Count(r => r.CreatedUserId == "maint01").Should().Be(5);
        var floor = await fx.Records("mes-repair", "floor01");
        floor.Should().HaveCount(45).And.OnlyContain(r => r.CreatedUserId != "maint01");
    }

    [Fact]
    public async Task Repairs_ClosedHaveResolutionAndFinishDate()
    {
        Generated();
        var status = await fx.Key("mes-repair", "狀態");
        var closed = await fx.Opt("mes-repair", "狀態", "結案");
        var resolution = await fx.Key("mes-repair", "處理說明");
        var finish = await fx.Key("mes-repair", "完成日期");
        var reportDate = await fx.Key("mes-repair", "報修日期");
        var handler = await fx.Key("mes-repair", "處理人");

        var rows = (await fx.Records("mes-repair")).Where(r => DemoGeneratedFixture.Text(r, status) == closed).ToList();
        rows.Should().NotBeEmpty();
        foreach (var r in rows)
        {
            DemoGeneratedFixture.Text(r, resolution).Should().NotBeNullOrEmpty();
            var f = Date(DemoGeneratedFixture.Text(r, finish));
            f.Should().BeOnOrAfter(Date(DemoGeneratedFixture.Text(r, reportDate))).And.BeBefore(Today);
            DemoGeneratedFixture.Text(r, handler).Should().Be("maint01");
            r.ModifiedUserId.Should().Be(r.CreatedUserId, "建立者結案");
        }
    }

    [Fact]
    public async Task Repairs_RelatedRepair_Set()
    {
        Generated();
        var related = await fx.Key("mes-repair", "關聯報修單");
        var mold = await fx.Key("mes-repair", "模具");
        var repairs = (await fx.Records("mes-repair")).ToDictionary(r => r.Id);

        var linked = repairs.Values.Where(r => r.Values.GetValueOrDefault(related) is { ValueKind: JsonValueKind.Number }).ToList();
        linked.Should().HaveCount(3);
        foreach (var r in linked)
        {
            var target = repairs[r.Values[related]!.Value.GetInt64()];
            target.Values[mold]!.Value.GetInt64().Should().Be(r.Values[mold]!.Value.GetInt64());
            target.CreatedTime.Should().BeBefore(r.CreatedTime);
        }
    }
    /// <summary>報修第 1–50 筆依序挑到的模具編號：改成依實際模具數抽選後，序列必須與原本寫死 12 支時相同（Demo 資料不變）。</summary>
    private const string MoldSequence =
        "M-0004,M-0005,M-0003,M-0009,M-0008,M-0010,M-0003,M-0002,M-0012,M-0011," +
        "M-0005,M-0003,M-0010,M-0005,M-0003,M-0006,M-0005,M-0003,M-0005,M-0011," +
        "M-0002,M-0004,M-0012,M-0011,M-0008,M-0004,M-0006,M-0009,M-0003,M-0009," +
        "M-0012,M-0003,M-0002,M-0008,M-0003,M-0009,M-0010,M-0001,M-0004,M-0002," +
        "M-0001,M-0004,M-0012,M-0008,M-0012,M-0007,M-0011,M-0003,M-0012,M-0001";

    [Fact]
    public async Task Repairs_MoldSequence_Unchanged()
    {
        Generated();
        var mold = await fx.Key("mes-repair", "模具");
        var reportDate = await fx.Key("mes-repair", "報修日期");
        var no = await fx.Key("mes-mold", "模具編號");
        var molds = (await fx.Records("mes-mold")).ToDictionary(m => m.Id, m => DemoGeneratedFixture.Text(m, no));

        var picked = (await fx.Records("mes-repair")).OrderBy(r => DemoGeneratedFixture.Text(r, reportDate))
            .Select(r => molds[r.Values[mold]!.Value.GetInt64()]);
        string.Join(",", picked).Should().Be(MoldSequence);
    }

    [Fact]
    public void Repair_PlannedWithoutMolds_ThrowsClearError()
    {
        var plan = new DemoPlan(Today, new Random(1), new Dictionary<string, IReadOnlyList<DemoRecord>>());
        var act = () => new RepairScenario().Plan(plan);
        act.Should().Throw<InvalidOperationException>().WithMessage("*模具保養情境必須排在報修之前*");
    }
}
