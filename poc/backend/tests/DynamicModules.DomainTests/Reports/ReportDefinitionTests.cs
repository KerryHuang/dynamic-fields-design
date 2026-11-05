using System.Text.Json;
using DynamicModules.Domain.Reports;
using FluentAssertions;

namespace DynamicModules.DomainTests.Reports;

public class ReportDefinitionTests
{
    private static ReportFieldRef MainRef(long id, long? via = null) => new("main", id, via);

    [Fact]
    public void ReportDefinition_RoundTrips_AllFields()
    {
        var value = JsonDocument.Parse("""["a","b"]""").RootElement.Clone();
        var def = new ReportDefinition(
            Version: 1,
            MainModuleKey: "orders",
            Child: new ReportChild("order-items", 12),
            Columns: [MainRef(1), new ReportFieldRef("child", 5, null), MainRef(3, 9)],
            Groups: [new ReportGroup(MainRef(2), null), new ReportGroup(MainRef(4), "month")],
            Measures:
            [
                new ReportMeasure("m1", "sum", new ReportFieldRef("child", 6, null), null),
                new ReportMeasure("m2", "countMain", null, new ReportFilter(MainRef(7), "eq", JsonDocument.Parse("\"done\"").RootElement.Clone()))
            ],
            Filters: [new ReportFilter(MainRef(8), "in", value), new ReportFilter(MainRef(9), "isEmpty", null)],
            PrimaryDateField: MainRef(4),
            DateRange: new ReportDateRange("lastDays", 30),
            Sort: [new ReportSort("m1", true), new ReportSort("f2", false)],
            Viz: "bar");

        var json = ReportDefinition.Serialize(def);
        var back = ReportDefinition.Parse(json);

        ReportDefinition.Serialize(back).Should().Be(json);
        back.Should().BeEquivalentTo(def, o => o.ComparingByMembers<ReportFilter>().Excluding(x => x.Path.EndsWith(".Value")));
        back.Filters[0].Value!.Value.GetRawText().Should().Be("""["a","b"]""");
        back.Measures[1].Condition!.Value!.Value.GetString().Should().Be("done");
        back.Filters[1].Value.Should().BeNull();
        back.DateRange!.N.Should().Be(30);
    }

    [Fact]
    public void ReportDefinition_UsesCamelCase()
    {
        var def = new ReportDefinition(1, "orders", null, [MainRef(1)], [], [], [], null, null, [], "table");

        var json = ReportDefinition.Serialize(def);

        json.Should().Contain("\"mainModuleKey\":\"orders\"").And.Contain("\"fieldId\":1").And.Contain("\"viz\":\"table\"");
    }
}
