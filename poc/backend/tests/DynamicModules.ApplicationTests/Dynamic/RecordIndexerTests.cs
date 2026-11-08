using System.Text.Json;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic;

public class RecordIndexerTests
{
    [Theory]
    [InlineData(FieldType.Formula)]
    [InlineData(FieldType.Rollup)]
    public void ShouldIndex_ComputedFields_FollowFilterableAndSortable(FieldType type)
    {
        RecordIndexer.ShouldIndex(new Field { Type = type, Config = new FieldConfig { IsFilterable = true } }).Should().BeTrue();
        RecordIndexer.ShouldIndex(new Field { Type = type, Config = new FieldConfig { IsSortable = true } }).Should().BeTrue();
        RecordIndexer.ShouldIndex(new Field { Type = type, Config = new FieldConfig() }).Should().BeFalse();
    }

    private static readonly FieldTypeRegistry Registry = FieldTypeRegistry.CreateDefault();

    private static List<RecordIndex> Index(FieldType type, FieldConfig config, object value) =>
        RecordIndexer.BuildField(new Field { Id = 1, Key = "f", Type = type, Config = config },
            new Dictionary<string, JsonElement?> { ["f"] = JsonSerializer.SerializeToElement(value) }, Registry);

    [Fact]
    public void BuildField_Text_MarksTruncatedOnlyWhenIndexValueDiffersFromOriginal()
    {
        var cfg = new FieldConfig { IsFilterable = true, MaxLength = 4000 };
        Index(FieldType.Text, cfg, new string('a', 440)).Single().TextTruncated.Should().BeFalse();
        var cut = Index(FieldType.Text, cfg, new string('a', 440) + "b").Single();
        cut.TextTruncated.Should().BeTrue();
        cut.TextValue.Should().HaveLength(440);
    }

    [Fact]
    public void BuildField_Text_SurrogatePairAtTheCut_StillMarksTruncated()
    {
        // 第 440 個 UTF-16 單位是高代理字元：索引值只取 439 個單位，但仍是被截斷
        var value = new string('a', 439) + "😀" + "tail";
        var row = Index(FieldType.Text, new FieldConfig { IsFilterable = true, MaxLength = 4000 }, value).Single();
        row.TextValue.Should().HaveLength(439);
        row.TextTruncated.Should().BeTrue();
    }

    [Fact]
    public void BuildField_EnumAndMultiEnum_NeverTruncated()
    {
        Index(FieldType.Enum, new FieldConfig { IsFilterable = true }, "opt_5").Single().TextTruncated.Should().BeFalse();
        Index(FieldType.MultiEnum, new FieldConfig(), new[] { "opt_1", "opt_2" }).Should().OnlyContain(r => !r.TextTruncated);
    }

    [Theory]
    [InlineData(2, 1.23, 123)]
    [InlineData(1, 1.23, null)]   // Scale 被調低後的舊值：不悄悄捨成 12，而是沒有精確值
    [InlineData(1, 1.2, 12)]
    [InlineData(0, 5.0, 5)]
    public void BuildField_Decimal_ScaledValueIsExactOrNull(int scale, double value, int? expected)
    {
        var row = Index(FieldType.Decimal, new FieldConfig { IsFilterable = true, Scale = scale }, (decimal)value).Single();
        row.ScaledValue.Should().Be(expected is null ? null : (long)expected);
    }

    [Fact]
    public void BuildField_Decimal_OverflowingScaledValueIsNull()
    {
        var row = Index(FieldType.Decimal, new FieldConfig { IsFilterable = true, Scale = 2 }, 1e20m).Single();
        row.NumberValue.Should().NotBeNull();
        row.ScaledValue.Should().BeNull();
    }
}
