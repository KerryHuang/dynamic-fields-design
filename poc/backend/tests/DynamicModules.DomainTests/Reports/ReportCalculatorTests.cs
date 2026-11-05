using System.Text.Json;
using DynamicModules.Domain.Reports;
using FluentAssertions;

namespace DynamicModules.DomainTests.Reports;

public class ReportCalculatorTests
{
    private static readonly ReportFieldRef Amount = new("main", 1, null);
    private static readonly ReportFieldRef Qty = new("child", 2, null);
    private static readonly ReportFieldRef Warehouse = new("child", 3, null);
    private static readonly ReportFieldRef Tags = new("main", 4, null);

    private static ReportCell Num(decimal n) => new("ok", n, null, null, null);
    private static ReportCell Txt(string t) => new("ok", null, t, null, null);
    private static ReportCell Empty() => new("empty", null, null, null, null);
    private static ReportCell State(string s) => new(s, null, null, null, null);
    private static ReportCell Multi(params string[] o) => new("ok", null, null, null, o);

    private static ReportRow Row(long main, long? child, params (ReportFieldRef f, ReportCell c)[] cells) =>
        new(main, child, cells.ToDictionary(x => ReportCalculator.KeyOf(x.f), x => x.c));

    private static ReportMeasure M(string kind, ReportFieldRef? f, ReportFilter? cond = null) => new(kind, kind, f, cond);

    private static ReportFilter Gte(ReportFieldRef f, decimal v) =>
        new(f, "gte", JsonSerializer.SerializeToElement(v));

    /// <summary>測試用的條件判斷：只支援 gte。</summary>
    private static bool Matches(ReportFilter f, ReportCell c) =>
        c.State == "ok" && c.Number >= f.Value!.Value.GetDecimal();

    private static string K(ReportFieldRef f) => ReportCalculator.KeyOf(f);

    /// <summary>訂單 A 金額 100、3 筆出貨（1,2,3）；訂單 B 金額 50、無出貨。</summary>
    private static List<ReportRow> TwoOrders() =>
    [
        Row(1, 11, (Amount, Num(100)), (Qty, Num(1))),
        Row(1, 12, (Amount, Num(100)), (Qty, Num(2))),
        Row(1, 13, (Amount, Num(100)), (Qty, Num(3))),
        Row(2, null, (Amount, Num(50)), (Qty, Empty())),
    ];

    private static ReportCalcResult Calc(List<ReportRow> rows, ReportGroup[] groups, ReportMeasure[] measures, bool merged = true) =>
        ReportCalculator.Calculate(rows, groups, measures, merged, Matches);

    [Fact]
    public void Sum_MainField_MergedThreeChildren_CountsOnce()
    {
        var r = Calc(TwoOrders(), [], [M("sum", Amount) with { Id = "a" }, M("sum", Qty) with { Id = "q" }]);
        r.Total["a"].Should().Be(150m);
        r.Total["q"].Should().Be(6m);
        r.RowCount.Should().Be(4);
    }

    [Fact]
    public void Avg_MainField_Merged_DedupesByMain()
    {
        var r = Calc(TwoOrders(), [], [M("avg", Amount)]);
        r.Total["avg"].Should().Be(75m);
    }

    [Fact]
    public void GroupByChildField_ParentInTwoGroups_TotalIsNotSum()
    {
        var rows = new List<ReportRow>
        {
            Row(1, 11, (Amount, Num(100)), (Warehouse, Txt("X"))),
            Row(1, 12, (Amount, Num(100)), (Warehouse, Txt("Y"))),
        };
        var r = Calc(rows, [new(Warehouse, null)], [M("sum", Amount)]);
        r.Groups.Select(g => g.GroupKeys[0]).Should().Equal("X", "Y");
        r.Groups.Select(g => g.Values["sum"]).Should().Equal(100m, 100m);
        r.Total["sum"].Should().Be(100m);
    }

    [Fact]
    public void CountMain_And_CountChild()
    {
        var r = Calc(TwoOrders(), [], [M("countMain", null), M("countChild", null)]);
        r.Total["countMain"].Should().Be(2m);
        r.Total["countChild"].Should().Be(3m);
    }

    [Fact]
    public void Ratio_MainCondition_DedupesByMain()
    {
        var r = Calc(TwoOrders(), [], [M("ratio", null, Gte(Amount, 100))]);
        r.Total["ratio"].Should().Be(0.5m);
    }

    [Fact]
    public void Ratio_ChildCondition_CountsChildRows()
    {
        var r = Calc(TwoOrders(), [], [M("ratio", null, Gte(Qty, 2))]);
        r.Total["ratio"].Should().Be(2m / 3m);
    }

    [Fact]
    public void Ratio_EmptyDenominator_IsNull_AndMissingMatcherThrows()
    {
        var r = Calc([], [], [M("ratio", null, Gte(Amount, 1))]);
        r.Total["ratio"].Should().BeNull();
        var act = () => ReportCalculator.Calculate(TwoOrders(), [], [M("ratio", null, Gte(Amount, 1))], true);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void EmptyValues_NotCounted_InSumOrAvg()
    {
        var rows = new List<ReportRow>
        {
            Row(1, null, (Amount, Num(10))),
            Row(2, null, (Amount, Empty())),
            Row(3, null, (Amount, Num(20))),
        };
        var r = Calc(rows, [], [M("sum", Amount), M("avg", Amount)], merged: false);
        r.Total["sum"].Should().Be(30m);
        r.Total["avg"].Should().Be(15m);
        Calc([Row(1, null, (Amount, Empty()))], [], [M("sum", Amount), M("avg", Amount)]).Total
            .Values.Should().AllSatisfy(v => v.Should().BeNull());
    }

    [Fact]
    public void MinMax_Number_AndDate()
    {
        var d = new ReportFieldRef("main", 5, null);
        var rows = new List<ReportRow>
        {
            Row(1, null, (Amount, Num(10)), (d, new("ok", null, null, new DateOnly(2026, 1, 2), null))),
            Row(2, null, (Amount, Num(30)), (d, new("ok", null, null, new DateOnly(2026, 3, 4), null))),
        };
        var r = Calc(rows, [], [new("lo", "min", Amount, null), new("hi", "max", d, null)], merged: false);
        r.Total["lo"].Should().Be(10m);
        r.Total["hi"].Should().Be(new DateOnly(2026, 3, 4).DayNumber);
    }

    [Fact]
    public void MultiSelect_GroupsPerOption_DedupesInEachGroup()
    {
        var rows = new List<ReportRow>
        {
            Row(1, 11, (Amount, Num(100)), (Tags, Multi("a", "b"))),
            Row(1, 12, (Amount, Num(100)), (Tags, Multi("a", "b"))),
            Row(2, null, (Amount, Num(50)), (Tags, Multi("a"))),
        };
        var r = Calc(rows, [new(Tags, null)], [M("sum", Amount)]);
        r.Groups.Select(g => g.GroupKeys[0]).Should().Equal("a", "b");
        r.Groups.Select(g => g.Values["sum"]).Should().Equal(150m, 100m);
        r.Total["sum"].Should().Be(150m);
    }

    [Fact]
    public void MissingAndRestricted_FormOwnGroups_NotMixed()
    {
        var rows = new List<ReportRow>
        {
            Row(1, null, (Amount, Num(1)), (Warehouse, Txt("X"))),
            Row(2, null, (Amount, Num(2)), (Warehouse, State("missing"))),
            Row(3, null, (Amount, Num(4)), (Warehouse, State("restricted"))),
            Row(4, null, (Amount, Num(8)), (Warehouse, Empty())),
        };
        var r = Calc(rows, [new(Warehouse, null)], [M("sum", Amount)], merged: false);
        r.Groups.Select(g => g.GroupKeys[0]).Should().Equal("X", "__missing", "__restricted", "");
        r.Groups.Select(g => g.Values["sum"]).Should().Equal(1m, 2m, 4m, 8m);
    }

    [Fact]
    public void DedupedNote_TrueOnlyWhenMergedAndMainSumOrAvg()
    {
        var rows = TwoOrders();
        Calc(rows, [], [M("sum", Amount)]).DedupedNote.Should().BeTrue();
        Calc(rows, [], [M("avg", Amount)]).DedupedNote.Should().BeTrue();
        Calc(rows, [], [M("sum", Amount)], merged: false).DedupedNote.Should().BeFalse();
        Calc(rows, [], [M("countChild", null)]).DedupedNote.Should().BeFalse();
        Calc(rows, [], [M("sum", Qty)]).DedupedNote.Should().BeFalse();
    }

    [Fact]
    public void Decimal_NoFloatDrift()
    {
        var rows = new List<ReportRow>
        {
            Row(1, null, (Amount, Num(0.1m))),
            Row(2, null, (Amount, Num(0.2m))),
        };
        Calc(rows, [], [M("sum", Amount)], merged: false).Total["sum"].Should().Be(0.3m);
    }
}
