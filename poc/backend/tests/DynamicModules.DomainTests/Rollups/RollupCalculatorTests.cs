using System.Text.Json;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Rollups;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using static DynamicModules.DomainTests.Formulas.FormulaTestKit;

namespace DynamicModules.DomainTests.Rollups;

public class RollupCalculatorTests
{
    private static readonly DateOnly Today = new(2026, 11, 4);

    /// <summary>來源模組：10 參照、11 decimal、12 currency、13 date、14 text。</summary>
    private static Module Source() => Mod(
        Fld(10, FieldType.Reference), Fld(11, FieldType.Decimal), Fld(12, FieldType.Currency),
        Fld(13, FieldType.Date), Fld(14, FieldType.Text));

    private static Field Rollup(RollupFunction fn, long? aggregate, string resultType = "number", int? scale = 2, string? condition = null) =>
        Fld(100, FieldType.Rollup, new FieldConfig
        {
            ResultType = resultType,
            Scale = scale,
            Rollup = new RollupConfig(10, fn, aggregate, condition is null ? null : J(condition))
        });

    private static IReadOnlyDictionary<string, JsonElement?> Row(params (long id, string json)[] items) => Values(items);

    private static JsonElement? Compute(Field rollup, params IReadOnlyDictionary<string, JsonElement?>[] rows) =>
        RollupCalculator.Compute(rollup, Source(), rows, Today, new HashSet<string>());

    [Fact]
    public void Sum_Decimal()
    {
        var r = Compute(Rollup(RollupFunction.Sum, 11), Row((11, "1.5")), Row((11, "2.25")), Row((11, "3")));
        r!.Value.GetDecimal().Should().Be(6.75m);
    }

    [Fact]
    public void Count_IgnoresAggregate()
    {
        var r = Compute(Rollup(RollupFunction.Count, null, scale: 0), Row(), Row((11, "5")), Row((11, "null")));
        r!.Value.GetDecimal().Should().Be(3m);
    }

    [Fact]
    public void Min_Max_Dates()
    {
        var rows = new[] { Row((13, "\"2026-03-05\"")), Row((13, "\"2026-01-20\"")), Row((13, "\"2026-12-01\"")) };
        Compute(Rollup(RollupFunction.Min, 13, "date", null), rows)!.Value.GetString().Should().Be("2026-01-20");
        Compute(Rollup(RollupFunction.Max, 13, "date", null), rows)!.Value.GetString().Should().Be("2026-12-01");
    }

    [Fact]
    public void Sum_Empty_Zero()
    {
        Compute(Rollup(RollupFunction.Sum, 11))!.Value.GetDecimal().Should().Be(0m);
        Compute(Rollup(RollupFunction.Count, null, scale: 0))!.Value.GetDecimal().Should().Be(0m);
    }

    [Fact]
    public void Max_Empty_Null()
    {
        Compute(Rollup(RollupFunction.Max, 11)).Should().BeNull();
        Compute(Rollup(RollupFunction.Min, 13, "date", null)).Should().BeNull();
    }

    [Fact]
    public void Condition_FiltersChildren()
    {
        var rollup = Rollup(RollupFunction.Sum, 11, condition: """{"field":14,"op":"eq","value":"done"}""");
        var r = Compute(rollup,
            Row((11, "10"), (14, "\"done\"")), Row((11, "20"), (14, "\"open\"")), Row((11, "5"), (14, "\"done\"")), Row((11, "7")));
        r!.Value.GetDecimal().Should().Be(15m);
    }

    [Fact]
    public void Condition_BeforeToday_CountsOnlyRowsStrictlyBeforeToday()
    {
        var rollup = Rollup(RollupFunction.Sum, 11, condition: """{"field":13,"op":"beforeToday"}""");
        var r = Compute(rollup,
            Row((11, "1"), (13, "\"2026-11-03\"")), Row((11, "10"), (13, "\"2026-11-04\"")),
            Row((11, "100"), (13, "\"2026-11-05\"")), Row((11, "1000")), Row((11, "2"), (13, "\"2026-01-01\"")));
        r!.Value.GetDecimal().Should().Be(3m);
    }

    [Fact]
    public void Condition_OnUserField_UsesUserIds()
    {
        var source = Mod(Fld(10, FieldType.Reference), Fld(11, FieldType.Decimal), Fld(15, FieldType.User));
        var rollup = Rollup(RollupFunction.Sum, 11, condition: """{"field":15,"op":"eq","value":"alice"}""");
        var rows = new[] { Row((11, "10"), (15, "\"alice\"")), Row((11, "5"), (15, "\"bob\"")) };
        RollupCalculator.Compute(rollup, source, rows, Today, new HashSet<string> { "alice", "bob" })!.Value.GetDecimal().Should().Be(10m);
    }

    [Fact]
    public void MissingValues_Skipped()
    {
        var rows = new[] { Row((11, "4")), Row(), Row((11, "null")), Row((11, "6")) };
        Compute(Rollup(RollupFunction.Sum, 11), rows)!.Value.GetDecimal().Should().Be(10m);
        Compute(Rollup(RollupFunction.Min, 11), rows)!.Value.GetDecimal().Should().Be(4m);
        Compute(Rollup(RollupFunction.Max, 11), rows)!.Value.GetDecimal().Should().Be(6m);
        Compute(Rollup(RollupFunction.Count, null, scale: 0), rows)!.Value.GetDecimal().Should().Be(4m);
    }

    [Fact]
    public void Sum_Currency_RoundsToScale()
    {
        var r = Compute(Rollup(RollupFunction.Sum, 12, "currency", 2), Row((12, "0.125")), Row((12, "0.125")), Row((12, "0.005")));
        r!.Value.GetDecimal().Should().Be(0.26m); // 0.255 AwayFromZero
    }

    [Fact]
    public void InvalidCondition_Ignored_LikeRuleEngine()
    {
        // 條件欄位 Scale 被改成 0 後，已存的「> 1.5」解析失敗：比照 RuleEngine 略過條件（全部列都算），不丟例外
        var source = Mod(Fld(10, FieldType.Reference), Fld(11, FieldType.Decimal), Fld(16, FieldType.Decimal, new FieldConfig { Scale = 0 }));
        var rollup = Rollup(RollupFunction.Sum, 11, condition: """{"field":16,"op":"gt","value":1.5}""");
        var rows = new[] { Row((11, "10"), (16, "1")), Row((11, "5"), (16, "3")) };
        var act = () => RollupCalculator.Compute(rollup, source, rows, Today, new HashSet<string>());
        act.Should().NotThrow();
        act()!.Value.GetDecimal().Should().Be(15m);
    }
}
