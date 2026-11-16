using DynamicModules.Application.Dynamic.ConversionService.Data;
using DynamicModules.Application.Dynamic.ConversionService.Queries.GetRecordConversions;
using DynamicModules.Application.Dynamic.DemoService;
using DynamicModules.Application.Dynamic.DemoService.Commands.GenerateDemoData;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic.Demo;

/// <summary>Demo 預設開啟的「CRM 與銷售」：資料量、六個階段、各種報價狀態、部分轉換，以及 Demo 帳號靠既有群組就能走完流程。</summary>
[Collection(DemoGeneratedCollection.Name)]
public class CrmScenarioTests(DemoGeneratedFixture fx)
{
    private async Task<Dictionary<string, int>> Mix(string module, string statusLabel, string user = "admin")
    {
        fx.Generated();
        var key = await fx.Key(module, statusLabel);
        var mod = await fx.Module(module);
        var labels = mod.Blocks.SelectMany(b => b.Fields).Single(f => f.Key == key).Options.ToDictionary(o => o.Value, o => o.Label.Resolve("zh-TW"));
        return (await fx.Records(module, user)).GroupBy(r => labels[DemoGeneratedFixture.Text(r, key)!]).ToDictionary(g => g.Key, g => g.Count());
    }

    [Fact]
    public async Task OpportunitiesCoverAllSixStages()
    {
        var mix = await Mix("crm-opportunity", "階段");
        mix.Keys.Should().BeEquivalentTo("初步接觸", "需求確認", "提案報價", "議價", "成交", "失敗");
    }

    [Fact]
    public async Task QuotesCoverDraftPendingRejectedAndApproved_AndOrdersExist()
    {
        var mix = await Mix("crm-quote", "狀態");
        mix.Keys.Should().BeEquivalentTo("草稿", "待核准", "已退回", "已核准");
        mix["已核准"].Should().BeGreaterThanOrEqualTo(4);
        (await Mix("crm-order", "狀態")).Values.Sum().Should().Be(4);
        (await Mix("crm-lead", "狀態")).Keys.Should().BeEquivalentTo("新線索", "已聯繫", "已確認", "流失");
    }

    [Fact]
    public async Task SomeApprovedQuotesAreOnlyPartlyConverted_AndSomeNotAtAll()
    {
        fx.Generated();
        var statusKey = await fx.Key("crm-quote", "狀態");
        var approved = await fx.Opt("crm-quote", "狀態", "已核准");
        var partial = 0;
        var untouched = 0;
        foreach (var quote in (await fx.Records("crm-quote")).Where(q => DemoGeneratedFixture.Text(q, statusKey) == approved))
        {
            var availability = (List<ConversionAvailability>)(await fx.As("admin", new GetRecordConversionsRequest(quote.Id))).Data!;
            var lines = availability.Single(c => c.Key == "crm-quote-to-order").Lines!;
            var converted = lines.Sum(l => l.Converted);
            if (converted == 0) untouched++;
            else if (lines.Any(l => l.Remaining > 0)) partial++;
        }
        partial.Should().BeGreaterThanOrEqualTo(1);
        untouched.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task DemoAccounts_SeeTheApp_ThroughTheExistingGroups_AndOthersDoNot()
    {
        fx.Generated();
        foreach (var user in new[] { "sales01", "sales02", "manager01" })
            (await fx.Records("crm-quote", user)).Should().NotBeEmpty(user);
        var floor = await fx.As("floor01", new DynamicModules.Application.Dynamic.RecordService.Queries.GetPagedRecords.GetPagedRecordsRequest(
            "crm-quote", 1, 10, null, [], [], null));
        floor.IsSuccess.Should().BeFalse("現場人員不在業務群組，不該看到 CRM");
    }

    [Fact]
    public void CrmSteps_AreNotMissingFromTheTimeline()
    {
        fx.Generated();
        ((DemoGenerateResult)fx.Result.Data!).Steps.Should().BeGreaterThan(650);
    }
}
