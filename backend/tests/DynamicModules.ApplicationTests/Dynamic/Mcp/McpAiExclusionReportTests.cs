using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.ReportService;
using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Domain.Reports;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.TestSupport;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic.Mcp;

/// <summary>
/// 報表的 AI 遮罩回歸（mcp1a 計畫判斷 10「匯出與報表」）：MCP 通道裡被標「不提供給 AI」的欄位，行為與「使用者看不到該欄位」相同。
/// 對照組是畫面通道的 sales01 看對他隱藏的欄位（機密備註、客戶機密）。這個類別走記憶體引擎，<see cref="McpAiExclusionReportSqlTests"/> 走 SQL 聚合。
/// </summary>
[Trait("Category", "Postgres")]
public class McpAiExclusionReportTests : ReportTestBase
{
    private readonly TestRequestChannel _channel = new();

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        services.AddSingleton<IRequestChannel>(_channel);
        services.AddChannelPermissionMasking();
    }

    private async Task Exclude(params long[] fieldIds)
    {
        foreach (var f in await Db.Context.Set<Field>().Where(f => fieldIds.Contains(f.Id)).ToListAsync()) f.ExcludeFromAi = true;
        await Db.Context.SaveForTestsAsync();
        Db.Context.ChangeTracker.Clear();
    }

    private async Task<ReportResult> Mcp(ReportDefinition d)
    {
        As("admin");
        _channel.Channel = RequestChannel.Mcp;
        try { return await Run(d); }
        finally { _channel.Channel = RequestChannel.Ui; }
    }

    private async Task<ReportResult> Ui(string user, ReportDefinition d)
    {
        As(user);
        return await Run(d);
    }

    [Fact]
    public async Task Report_Mcp_ColumnHidden()
    {
        await Arrange();
        await TwoOrders();
        await Exclude(Note.Id);

        var mcp = await Mcp(Def(columns: [MainF(Note)]));
        var hidden = await Ui("sales01", Def(columns: [MainF(Secret)]));
        mcp.State.Should().Be(hidden.State).And.NotBe("ok");
        (await Ui("admin", Def(columns: [MainF(Note)]))).State.Should().Be("ok");
    }

    [Fact]
    public async Task Report_Mcp_ConditionOnExcluded()
    {
        await Arrange();
        await TwoOrders();
        await Exclude(SecretNum.Id);

        var mcp = await Mcp(Def(measures: [M("n", "countMain")], filters: [F(MainF(SecretNum), "gt", 1)]));
        var hidden = await Ui("sales01", Def(measures: [M("n", "countMain")], filters: [F(MainF(SecretNum), "gt", 1)]));
        mcp.State.Should().Be(hidden.State).And.NotBe("ok");
        (await Ui("admin", Def(measures: [M("n", "countMain")], filters: [F(MainF(SecretNum), "gt", 1)]))).State.Should().NotBe(hidden.State);
    }

    [Fact]
    public async Task Report_Mcp_ReferenceCarryOut()
    {
        await Arrange();
        await TwoOrders();
        await Exclude(Region.Id);

        var mcp = await Mcp(Def(columns: [RegionVia]));
        var hidden = await Ui("sales01", Def(columns: [new ReportFieldRef(Main, CustomerSecret.Id, CustomerRef.Id)]));
        mcp.State.Should().Be(hidden.State).And.NotBe("ok");
        (await Ui("admin", Def(columns: [RegionVia]))).State.Should().Be("ok");
    }
}

/// <summary>同一組案例走 SQL 聚合路徑（切換寫法照 ReportSqlEquivalenceTests／ReportRunnerSqlTests）。</summary>
[Trait("Category", "Postgres")]
public class McpAiExclusionReportSqlTests : McpAiExclusionReportTests
{
    protected override bool FieldsFilterable => true;
}
