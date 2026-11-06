using DynamicModules.Domain.Reports;
using FluentAssertions;

namespace DynamicModules.DomainTests.Reports;

public class DashboardLayoutTests
{
    [Fact]
    public void Layout_RoundTrips()
    {
        var layout = new DashboardLayout(
        [
            new DashboardCard("c1", 12, "本月接單", "indicator", 0, 0, 4, 2,
                new CardOptions(new ReportDateRange("thisMonth", null), null, false, null, false, 3, 7)),
            new DashboardCard("c2", 13, null, "bar", 4, 0, 8, 4,
                new CardOptions(null, "amount", true, 5, true, null, null)),
        ]);

        var json = DashboardLayout.Serialize(layout);
        var back = DashboardLayout.Parse(json);

        back.Cards.Should().BeEquivalentTo(layout.Cards, o => o.WithStrictOrdering());
        json.Should().Contain("\"reportId\":12").And.Contain("\"topN\":5").And.Contain("\"linkDashboardId\":7");
    }

    [Fact]
    public void Parse_MissingOptionsAndCards_UsesDefaults()
    {
        DashboardLayout.Parse("{}").Cards.Should().BeEmpty();
        var card = DashboardLayout.Parse("""{"cards":[{"id":"a","reportId":1,"viz":"table","x":0,"y":0,"w":3,"h":2}]}""").Cards.Single();
        card.Options.Should().Be(CardOptions.None);
    }

    [Fact]
    public void FromAssigned_DefaultsToFalse_AndIsServerOverwritable()
    {
        // 舊資料沒有這個欄位：視為 false
        var legacy = DashboardLayout.Parse("""{"cards":[{"id":"a","reportId":1,"viz":"table","x":0,"y":0,"w":3,"h":2}]}""").Cards.Single();
        legacy.FromAssigned.Should().BeFalse();

        // client 送 true 也能解析，但伺服器存檔前會整批覆寫
        var forged = DashboardLayout.Parse("""{"cards":[{"id":"a","reportId":1,"viz":"table","x":0,"y":0,"w":3,"h":2,"fromAssigned":true},{"id":"b","reportId":2,"viz":"table","x":3,"y":0,"w":3,"h":2,"fromAssigned":true}]}""");
        forged.Cards.Should().OnlyContain(c => c.FromAssigned);
        forged.WithFromAssigned(c => c.ReportId == 2).Cards.Select(c => c.FromAssigned).Should().Equal(false, true);
        forged.WithoutFromAssigned().Cards.Should().OnlyContain(c => !c.FromAssigned);
        DashboardLayout.Parse(DashboardLayout.Serialize(forged)).Cards.Should().OnlyContain(c => c.FromAssigned);
    }

    [Fact]
    public void ReportIds_ListsDistinctReports()
    {
        var layout = new DashboardLayout(
        [
            new DashboardCard("a", 5, null, "table", 0, 0, 2, 2, CardOptions.None),
            new DashboardCard("b", 5, null, "bar", 2, 0, 2, 2, CardOptions.None),
            new DashboardCard("c", 6, null, "bar", 4, 0, 2, 2, CardOptions.None),
        ]);

        layout.ReportIds().Should().BeEquivalentTo(new long[] { 5, 6 });
    }

    [Fact]
    public void WithoutReport_RemovesOnlyThoseCards()
    {
        var layout = new DashboardLayout(
        [
            new DashboardCard("a", 5, null, "table", 0, 0, 2, 2, CardOptions.None),
            new DashboardCard("c", 6, null, "bar", 4, 0, 2, 2, CardOptions.None),
        ]);

        layout.WithoutReport(5).Cards.Select(c => c.Id).Should().Equal("c");
    }
}
