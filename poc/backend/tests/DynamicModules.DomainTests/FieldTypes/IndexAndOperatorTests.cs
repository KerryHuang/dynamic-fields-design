using System.Text.Json;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using FluentAssertions;
using Xunit;
using static DynamicModules.DomainTests.FieldTypes.HandlerTestKit;

namespace DynamicModules.DomainTests.FieldTypes;

public class IndexAndOperatorTests
{
    private static IReadOnlyList<IndexValue> Index(FieldType type, string json) =>
        FieldTypeRegistry.CreateDefault().Get(type).ToIndex(J(json), F(type));

    private static string FixturePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "poc", "shared", "filter-operators.json")))
            dir = dir.Parent;
        return dir is null
            ? throw new FileNotFoundException("找不到 poc/shared/filter-operators.json")
            : Path.Combine(dir.FullName, "poc", "shared", "filter-operators.json");
    }

    private static string CamelCase(FilterOperator op) => char.ToLowerInvariant(op.ToString()[0]) + op.ToString()[1..];

    [Fact]
    public void Operators_MatchSharedFixture()
    {
        var fixture = JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(FixturePath()))!;
        var registry = FieldTypeRegistry.CreateDefault();
        var types = Enum.GetValues<FieldType>();

        var dateFormula = new DynamicModules.Domain.ValueObjects.FieldConfig { ResultType = "date" };
        fixture.Keys.Where(k => !k.Contains(':')).Should()
            .BeEquivalentTo(types.Select(t => t.ToString().ToLowerInvariant()));
        foreach (var (key, ops) in fixture)
        {
            var field = key switch
            {
                "formula:date" => F(FieldType.Formula, dateFormula),
                "rollup:date" => F(FieldType.Rollup, dateFormula),
                _ => F(Enum.Parse<FieldType>(key, true)),
            };
            registry.Get(field.Type).OperatorsFor(field).Select(CamelCase).Should().Equal(ops, $"型別 {key}");
        }
    }

    [Fact]
    public void Operators_TypeLevel_Formula_IsNumber() =>
        FieldTypeRegistry.CreateDefault().Get(FieldType.Formula).Operators.Select(CamelCase)
            .Should().Equal(FieldTypeRegistry.CreateDefault().Get(FieldType.Integer).Operators.Select(CamelCase));

    [Fact]
    public void ToIndex_Formula_NumberAndDate()
    {
        Index(FieldType.Formula, "12.5").Single().Should().Be(new IndexValue(0, null, 12.5, null, null));
        var dateField = F(FieldType.Formula, new DynamicModules.Domain.ValueObjects.FieldConfig { ResultType = "date" });
        FieldTypeRegistry.CreateDefault().Get(FieldType.Formula).ToIndex(J("\"2026-11-03\""), dateField)
            .Single().DateValue.Should().Be(new DateTime(2026, 11, 3));
    }

    [Fact]
    public void Rollup_Operators_FollowResultType()
    {
        var handler = FieldTypeRegistry.CreateDefault().Get(FieldType.Rollup);
        var date = F(FieldType.Rollup, new DynamicModules.Domain.ValueObjects.FieldConfig { ResultType = "date" });
        var currency = F(FieldType.Rollup, new DynamicModules.Domain.ValueObjects.FieldConfig { ResultType = "currency" });
        handler.OperatorsFor(date).Should().Contain(FilterOperator.WithinNextDays);
        handler.OperatorsFor(currency).Should().Equal(FieldTypeRegistry.CreateDefault().Get(FieldType.Integer).Operators);
    }

    [Fact]
    public void Rollup_ToIndex_DateAndNumber()
    {
        Index(FieldType.Rollup, "12.5").Single().Should().Be(new IndexValue(0, null, 12.5, null, null));
        var dateField = F(FieldType.Rollup, new DynamicModules.Domain.ValueObjects.FieldConfig { ResultType = "date" });
        FieldTypeRegistry.CreateDefault().Get(FieldType.Rollup).ToIndex(J("\"2026-11-03\""), dateField)
            .Single().DateValue.Should().Be(new DateTime(2026, 11, 3));
    }

    [Fact]
    public void ToIndex_AutoNumber_UsesTextValue() =>
        Index(FieldType.AutoNumber, "\"REP-202611-0007\"").Single()
            .Should().Be(new IndexValue(0, "REP-202611-0007", null, null, null));

    [Theory]
    [InlineData(FieldType.Formula)]
    [InlineData(FieldType.AutoNumber)]
    public void Normalize_SystemComputed_IgnoresInput(FieldType type)
    {
        Run(type, "123").Should().Match<FieldValueResult>(r => r.IsValid && r.Value == null);
        Run(type, "\"x\"").Should().Match<FieldValueResult>(r => r.IsValid && r.Value == null);
    }

    [Fact]
    public void ToIndex_Text_TruncatesTo440Chars()
    {
        var rows = Index(FieldType.Text, $"\"{new string('字', 500)}\"");
        rows.Should().ContainSingle().Which.TextValue!.Length.Should().Be(440);
    }

    [Fact]
    public void ToIndex_Text_DoesNotSplitSurrogatePair()
    {
        var rows = Index(FieldType.Text, $"\"{new string('a', 439)}😀\"");
        rows.Single().TextValue.Should().Be(new string('a', 439));
    }

    [Fact]
    public void ToIndex_Date_IsMidnight() =>
        Index(FieldType.Date, "\"2026-11-03\"").Single().DateValue.Should().Be(new DateTime(2026, 11, 3));

    [Fact]
    public void ToIndex_DateTime_IsTaipeiLocal()
    {
        var value = Index(FieldType.DateTime, "\"2026-11-03T09:30:00+08:00\"").Single().DateValue!.Value;
        value.Should().Be(new DateTime(2026, 11, 3, 9, 30, 0));
        value.Kind.Should().Be(DateTimeKind.Unspecified);
    }

    [Fact]
    public void ToIndex_Boolean_IsOneOrZero()
    {
        Index(FieldType.Boolean, "true").Single().NumberValue.Should().Be(1);
        Index(FieldType.Boolean, "false").Single().NumberValue.Should().Be(0);
    }

    [Fact]
    public void ToIndex_Number_UsesNumberValue() =>
        Index(FieldType.Decimal, "12.5").Single().Should().Be(new IndexValue(0, null, 12.5, null, null));

    [Fact]
    public void ToIndex_Time_UsesTextValue() =>
        Index(FieldType.Time, "\"09:30\"").Single().Should().Be(new IndexValue(0, "09:30", null, null, null));

    [Fact]
    public void ToIndex_Enum_HasTextAndOptionId() =>
        Index(FieldType.Enum, "\"opt_7\"").Single().Should().Be(new IndexValue(0, "opt_7", 7, null, null));

    [Fact]
    public void ToIndex_MultiEnum_OneRowPerOption() =>
        Index(FieldType.MultiEnum, "[\"opt_3\",\"opt_7\"]").Should().Equal(
            new IndexValue(0, "opt_3", 3, null, null),
            new IndexValue(1, "opt_7", 7, null, null));

    [Fact]
    public void ToIndex_User_UsesRefKey() =>
        Index(FieldType.User, "\"u1\"").Single().Should().Be(new IndexValue(0, null, null, null, "u1"));

    [Fact]
    public void ToIndex_Textarea_IsEmpty() => Index(FieldType.Textarea, "\"long text\"").Should().BeEmpty();
}
