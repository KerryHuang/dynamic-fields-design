using System.Globalization;
using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetRecordHistory;
using DynamicModules.Application.Dynamic.SchemaService.Data;
using DynamicModules.Application.Dynamic.SchemaService.Queries.GetModuleSchema;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic.Demo;

[Collection(DemoGeneratedCollection.Name)]
public class MoldCareQualityScenarioTests(DemoGeneratedFixture fx)
{
    private static readonly DateOnly Today = DemoTestHost.Today;

    private static DateOnly Date(string? s) => DateOnly.ParseExact(s!, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static long? Id(DynamicRecordResponse r, string key) =>
        r.Values.GetValueOrDefault(key) is { ValueKind: JsonValueKind.Number } v ? v.GetInt64() : null;

    private async Task<IReadOnlyList<RecordHistoryResponse>> History(long id) =>
        ((PagedResponse<RecordHistoryResponse>)(await fx.Host.Send(new GetRecordHistoryRequest(id, 1, 200, "zh-TW"))).Data!).Items;

    private async Task<ModuleSchemaResponse> Schema(string userId, string module) =>
        (ModuleSchemaResponse)(await fx.As(userId, new GetModuleSchemaRequest(module))).Data!;

    [Fact]
    public async Task MoldInfo_12_CustomerInfo_8()
    {
        fx.Generated();

        (await fx.Records("mold-info")).Select(r => r.HostKey).Should()
            .BeEquivalentTo(Enumerable.Range(1, 12).Select(i => $"M-{i:0000}"));
        var customers = await fx.Records("customer-info");
        customers.Select(r => r.HostKey).Should().BeEquivalentTo(Enumerable.Range(1, 8).Select(i => $"C-{i:000}"));
        var credit = await fx.Key("customer-info", "信用等級");
        customers.Select(c => DemoGeneratedFixture.Text(c, credit)).Distinct().Should().HaveCount(3);
        var email = await fx.Key("customer-info", "聯絡 Email");
        customers.Should().OnlyContain(c => DemoGeneratedFixture.Text(c, email)!.EndsWith(".example.com"));
    }

    [Fact]
    public async Task CustomerInfo_EmailPerCustomer_Distinct()
    {
        fx.Generated();

        var customers = await fx.Records("customer-info");
        var email = await fx.Key("customer-info", "聯絡 Email");
        var emails = customers.Select(c => DemoGeneratedFixture.Text(c, email)).ToList();
        emails.Should().OnlyHaveUniqueItems().And.HaveCount(8);
        DemoGeneratedFixture.Text(customers.Single(c => c.HostKey == "C-001"), email).Should().Be("purchase@c001.example.com");
    }

    [Fact]
    public async Task Care_30To80_AtLeast2PerMold_NextDateSpread()
    {
        fx.Generated();
        var mold = await fx.Key("mold-care", "模具");
        var careDate = await fx.Key("mold-care", "保養日期");
        var next = await fx.Key("mold-care", "下次保養日");
        var cares = await fx.Records("mold-care");

        cares.Count.Should().BeInRange(30, 80);
        var byMold = cares.GroupBy(c => DemoGeneratedFixture.Text(c, mold)).ToList();
        byMold.Should().HaveCount(12).And.OnlyContain(g => g.Count() >= 2 && g.Count() <= 6);
        cares.Should().OnlyContain(c => Date(DemoGeneratedFixture.Text(c, careDate)) < Today
                                        && Date(DemoGeneratedFixture.Text(c, careDate)) >= Today.AddDays(-180));
        var latestNext = byMold.Select(g => Date(DemoGeneratedFixture.Text(
            g.MaxBy(c => Date(DemoGeneratedFixture.Text(c, careDate)))!, next))).ToList();
        latestNext.Count(d => d < Today).Should().BeGreaterThanOrEqualTo(2);
        latestNext.Count(d => d >= Today && d <= Today.AddDays(6)).Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task Care_Parts_CascadeTargetExists()
    {
        fx.Generated();
        var care = await fx.Key("mold-care-part", "保養紀錄");
        var careIds = (await fx.Records("mold-care")).Select(c => c.Id).ToHashSet();
        var parts = await fx.Records("mold-care-part");

        parts.Count.Should().BeGreaterThanOrEqualTo(5);
        parts.Should().OnlyContain(p => careIds.Contains(Id(p, care) ?? 0));
    }

    [Fact]
    public async Task MoldInfo_LastCareDate_MatchesLatestCare()
    {
        fx.Generated();
        var mold = await fx.Key("mold-care", "模具");
        var careDate = await fx.Key("mold-care", "保養日期");
        var last = await fx.Key("mold-info", "上次保養日");
        var latest = (await fx.Records("mold-care")).GroupBy(c => DemoGeneratedFixture.Text(c, mold)!)
            .ToDictionary(g => g.Key, g => g.Max(c => Date(DemoGeneratedFixture.Text(c, careDate))));

        foreach (var info in await fx.Records("mold-info"))
        {
            Date(DemoGeneratedFixture.Text(info, last)).Should().Be(latest[info.HostKey!], info.HostKey);
            (await History(info.Id)).Should().Contain(h => h.Operation == "update" && h.ChangedUserId == "maint01"
                                                           && h.FieldLabel!.Resolve("zh-TW") == "上次保養日");
        }
    }

    [Fact]
    public async Task Quality_30_CriticalOpen_AtLeast2()
    {
        fx.Generated();
        var severity = await fx.Key("quality", "嚴重度");
        var status = await fx.Key("quality", "狀態");
        var critical = await fx.Opt("quality", "嚴重度", "重大");
        var closed = await fx.Opt("quality", "狀態", "結案");
        var issues = await fx.Records("quality");

        issues.Should().HaveCount(30);
        issues.Count(q => q.CreatedUserId == "qa01").Should().Be(20);
        issues.Count(q => DemoGeneratedFixture.Text(q, severity) == critical).Should().Be(6);
        issues.Where(q => DemoGeneratedFixture.Text(q, severity) == critical).Should().OnlyContain(q => q.CreatedUserId == "qa01");
        issues.Count(q => DemoGeneratedFixture.Text(q, severity) == critical && DemoGeneratedFixture.Text(q, status) != closed)
            .Should().BeGreaterThanOrEqualTo(2);
        var reportDate = await fx.Key("quality", "發現日期");
        var expected = new Dictionary<string, string>
        {
            ["開立"] = await fx.Opt("quality", "狀態", "開立"),
            ["處置中"] = await fx.Opt("quality", "狀態", "處置中"),
            ["結案"] = closed
        };
        foreach (var q in issues)
        {
            var days = Today.DayNumber - Date(DemoGeneratedFixture.Text(q, reportDate)).DayNumber;
            var label = days > 20 ? "結案" : days >= 7 ? "處置中" : "開立";
            DemoGeneratedFixture.Text(q, status).Should().Be(expected[label], q.Title);
        }
    }

    [Fact]
    public async Task Quality_OrderBeforeReportDate()
    {
        fx.Generated();
        var order = await fx.Key("quality", "訂單");
        var reportDate = await fx.Key("quality", "發現日期");
        var orderDate = await fx.Key("order", "訂單日期");
        var orders = (await fx.Records("order")).ToDictionary(o => o.Id);

        foreach (var q in await fx.Records("quality"))
        {
            var found = Date(DemoGeneratedFixture.Text(q, reportDate));
            found.Should().BeOnOrAfter(Today.AddDays(-60)).And.BeBefore(Today);
            Date(DemoGeneratedFixture.Text(orders[Id(q, order)!.Value], orderDate)).Should().BeBefore(found);
        }
    }

    [Fact]
    public async Task Quality_FieldPermissions()
    {
        fx.Generated();
        var defectRate = await fx.Key("quality", "不良率");
        var floor = await fx.Records("quality", "floor01");
        floor.Should().NotBeEmpty().And.OnlyContain(q => !q.Values.ContainsKey(defectRate));
        (await fx.Records("quality", "qa01")).Should().Contain(q => q.Values.GetValueOrDefault(defectRate) != null);

        var sales = await Schema("sales01", "quality");
        sales.Blocks.SelectMany(b => b.Fields).Single(f => f.Label.Resolve("zh-TW") == "責任單位").Config.ReadOnly.Should().BeTrue();
        var floorSchema = await Schema("floor01", "quality");
        // schema 仍列出選項（顯示既有值用），不能選的 Active = false
        floorSchema.Blocks.SelectMany(b => b.Fields).Single(f => f.Label.Resolve("zh-TW") == "嚴重度").Options
            .Where(o => o.Active).Select(o => o.Label.Resolve("zh-TW")).Should().NotContain("重大").And.Contain("輕微");
    }

    [Fact]
    public async Task Order_QualityCount_Rollup()
    {
        fx.Generated();
        var count = await fx.Key("order", "品質異常筆數");
        var order = await fx.Key("quality", "訂單");
        var referenced = (await fx.Records("quality")).GroupBy(q => Id(q, order)!.Value).ToDictionary(g => g.Key, g => g.Count());
        var orders = await fx.Records("order");

        foreach (var o in orders)
            o.Values[count]!.Value.GetDecimal().Should().Be(referenced.GetValueOrDefault(o.Id), o.Title);
        orders.Sum(o => o.Values[count]!.Value.GetDecimal()).Should().Be(30);
    }
}
