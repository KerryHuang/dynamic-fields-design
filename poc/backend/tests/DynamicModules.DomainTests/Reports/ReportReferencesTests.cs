using System.Text.Json;
using DynamicModules.Domain.Reports;
using FluentAssertions;

namespace DynamicModules.DomainTests.Reports;

public class ReportReferencesTests
{
    private static ReportDefinition Def(params ReportSort[] sort) =>
        new(1, "cust", null, [], [], [new ReportMeasure("m1", "countMain", null, null)], [], null, null, [.. sort], "table");

    [Fact]
    public void Of_FieldOnlyUsedAsSortKey_IsReferenced()
    {
        ReportReferences.Of(Def(new ReportSort("main:12:", true))).FieldIds.Should().BeEquivalentTo(new long[] { 12 });
    }

    [Fact]
    public void Of_SortKeyThroughReference_ReferencesBothFields()
    {
        ReportReferences.Of(Def(new ReportSort("main:12:7", false))).FieldIds.Should().BeEquivalentTo(new long[] { 12, 7 });
    }

    [Fact]
    public void Of_SortKeyThatIsAMeasureIdOrGarbage_ReferencesNoField()
    {
        ReportReferences.Of(Def(new ReportSort("m1", true), new ReportSort("not-a-key", false))).FieldIds.Should().BeEmpty();
    }

    [Fact]
    public void FieldKeys_TryParse_ReadsTheKeyOfFormat()
    {
        var parsed = ReportFieldKeys.TryParse(ReportCalculator.KeyOf(new ReportFieldRef("child", 5, 9)));
        parsed.Should().Be(new ReportFieldRef("child", 5, 9));
        ReportFieldKeys.TryParse("main:5:").Should().Be(new ReportFieldRef("main", 5, null));
        ReportFieldKeys.TryParse("main:x:").Should().BeNull();
        ReportFieldKeys.TryParse("main:5:y").Should().BeNull();
        ReportFieldKeys.TryParse("a:b").Should().BeNull();
    }

    private static string Layout(params string?[] sortKeys) => DashboardLayout.Serialize(new DashboardLayout(
        sortKeys.Select((k, i) => new DashboardCard($"c{i}", 1, null, "bar", 0, i, 6, 4,
            CardOptions.None with { SortKey = k })).ToList()));

    [Fact]
    public void Dashboard_CardSortKey_ReferencesField()
    {
        var refs = DashboardReferences.TryFieldIdsOf(Layout("main:21:", "main:22:30", null, "m1"));
        refs.Should().BeEquivalentTo(new long[] { 21, 22, 30 });
    }

    [Fact]
    public void Dashboard_UnparsableLayout_ReturnsNullSoCallerFailsClosed()
    {
        DashboardReferences.TryFieldIdsOf("{ not json").Should().BeNull();
    }
}
