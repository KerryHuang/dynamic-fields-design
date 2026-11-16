using System.Globalization;
using System.Text.Json;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;
using static DynamicModules.DomainTests.FieldTypes.HandlerTestKit;

namespace DynamicModules.DomainTests.FieldTypes;

/// <summary>
/// 金額的雙重表示：記錄值（ValuesJson）與公式、彙總、報表用 decimal；篩選與排序用的索引欄 NumberValue 是 double。
/// 這裡量兩邊在哪個範圍內一致，並要求寫入驗證把值擋在一致的範圍內（規則見 storage-load-report 的「數值精度」）。
/// </summary>
public class NumericPrecisionTests(ITestOutputHelper output)
{
    private static double Indexed(FieldType type, decimal d) =>
        FieldTypeRegistry.CreateDefault().Get(type)
            .ToIndex(J(d.ToString(CultureInfo.InvariantCulture)), F(type)).Single().NumberValue!.Value;

    /// <summary>隨機的 decimal：有效位數 1 到 <paramref name="maxDigits"/>，小數位 0 到 6（不超過有效位數）。</summary>
    private static IEnumerable<decimal> RandomDecimals(int count, int maxDigits, int seed)
    {
        var rnd = new Random(seed);
        for (var i = 0; i < count; i++)
        {
            var digits = rnd.Next(1, maxDigits + 1);
            var scale = rnd.Next(0, Math.Min(6, digits) + 1);
            var integer = 0m;
            for (var k = 0; k < digits; k++) integer = integer * 10 + rnd.Next(10);
            yield return (rnd.Next(2) == 0 ? 1 : -1) * integer / (decimal)Math.Pow(10, scale);
        }
    }

    [Theory]
    [InlineData(FieldType.Currency)]
    [InlineData(FieldType.Decimal)]
    [InlineData(FieldType.Percent)]
    public void IndexedNumber_RoundTripsExactly_UpTo15SignificantDigits(FieldType type)
    {
        foreach (var d in RandomDecimals(200_000, 15, 20261106))
            ((decimal)Indexed(type, d)).Should().Be(d, $"{d} 轉成索引的 double 再轉回來不能變");
    }

    [Fact]
    public void IndexedNumber_KeepsOrderAndDistinctness_UpTo15SignificantDigits()
    {
        var sorted = RandomDecimals(200_000, 15, 7).Distinct().Order().ToList();
        double? previous = null;
        foreach (var d in sorted)
        {
            var current = Indexed(FieldType.Currency, d);
            if (previous is { } p) current.Should().BeGreaterThan(p, $"{d} 與前一個不同的金額在索引裡也必須不同、且順序不變");
            previous = current;
        }
    }

    /// <summary>特徵測試：超過 15 位有效數字，兩個不同的金額會變成同一個 double，篩選的等於與大小比較就會出錯。</summary>
    [Fact]
    public void IndexedNumber_BeyondDoublePrecision_CollidesDistinctAmounts()
    {
        Indexed(FieldType.Currency, 12345678901234567.89m).Should().Be(Indexed(FieldType.Currency, 12345678901234567.88m));
        Indexed(FieldType.Integer, 9007199254740993m).Should().Be(9007199254740992d, "整數超過 2^53 也會被吃掉末位");
    }

    [Fact]
    public void DecimalCast_And_JsonParse_Agree_ForTheValuesWeAccept()
    {
        var mismatches = RandomDecimals(500_000, 15, 99)
            .Count(d => (double)d != JsonDocument.Parse(d.ToString(CultureInfo.InvariantCulture)).RootElement.GetDouble());
        output.WriteLine($"(double)decimal 與 JSON 解析不同的筆數：{mismatches} / 500000");
        mismatches.Should().Be(0, "公式結果若有人改成直接強制轉型，比對就會與篩選值差一個 ulp");
    }

    [Theory]
    [InlineData(FieldType.Currency)]
    [InlineData(FieldType.Decimal)]
    [InlineData(FieldType.Percent)]
    public void Decimals_AcceptUpTo15SignificantDigits_AndRejectMore(FieldType type)
    {
        Run(type, "999999999999.99").IsValid.Should().BeTrue("14 位有效數字");
        Run(type, "999999999999.999", new() { Scale = 3 }).IsValid.Should().BeTrue("15 位有效數字");
        var tooMany = Run(type, "1234567890123.456", new() { Scale = 3 });
        tooMany.IsValid.Should().BeFalse("16 位有效數字會在索引裡失真");
        tooMany.Error.Should().Be(FieldErrors.DigitsExceeded);
        tooMany.ErrorArgs.Should().Equal(15);
        Run(type, "-12345678901234567.89").IsValid.Should().BeFalse();
    }

    [Fact]
    public void Integer_AcceptsUpTo2Pow53_AndRejectsMore()
    {
        Run(FieldType.Integer, "9007199254740991").IsValid.Should().BeTrue();
        Run(FieldType.Integer, "-9007199254740991").IsValid.Should().BeTrue();
        Run(FieldType.Integer, "9007199254740992").IsValid.Should().BeFalse();
        Run(FieldType.Integer, "-9007199254740992").IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData(FieldType.Currency)]
    [InlineData(FieldType.Decimal)]
    [InlineData(FieldType.Percent)]
    public void Decimals_TrailingZerosAreNotSignificantDigits(FieldType type)
    {
        Run(type, "1000000000000000").IsValid.Should().BeTrue("1e15 只有一位有效數字");
        Run(type, "100000000000000000000").IsValid.Should().BeTrue("1e20 只有一位有效數字");
        Run(type, "123456789012345000000").IsValid.Should().BeTrue("15 位有效數字後面補零");
        Run(type, "1234567890123456000").IsValid.Should().BeFalse("16 位有效數字");
        Run(type, "1000000000000000").Value!.Value.GetRawText().Should().Be("1000000000000000", "保留原值");
    }

    [Fact]
    public void IndexedNumber_WithTrailingZeros_RoundTripsAndKeepsOrder()
    {
        var registry = FieldTypeRegistry.CreateDefault();
        double Idx(decimal d) => registry.Get(FieldType.Decimal).ToIndex(J(d.ToString(CultureInfo.InvariantCulture)), F(FieldType.Decimal)).Single().NumberValue!.Value;
        var values = RandomDecimals(20_000, 15, 31).Select(d => Math.Truncate(d) * 100_000m).Where(d => d != 0).Distinct().Order().ToList();
        double? previous = null;
        foreach (var d in values)
        {
            ((decimal)Idx(d)).Should().Be(d);
            if (previous is { } p) Idx(d).Should().BeGreaterThan(p);
            previous = Idx(d);
        }
    }

    /// <summary>上限只管記錄寫入；報表與篩選的條件值要能接受既有的超界值，否則含這種條件的報表連改名、另存都會被擋。</summary>
    [Theory]
    [InlineData(FieldType.Currency, "12345678901234567.89")]
    [InlineData(FieldType.Integer, "9007199254740993")]
    public void ConditionNormalization_DoesNotApplyTheRecordWriteLimits(FieldType type, string json)
    {
        var handler = FieldTypeRegistry.CreateDefault().Get(type);
        var ctx = Ctx() with { EnforceWriteLimits = false };
        handler.Normalize(J(json), F(type, new() { Scale = 2 }), ctx).IsValid.Should().BeTrue();
        handler.Normalize(J(json), F(type, new() { Scale = 2 }), Ctx()).IsValid.Should().BeFalse("記錄寫入仍然擋");
    }
}
