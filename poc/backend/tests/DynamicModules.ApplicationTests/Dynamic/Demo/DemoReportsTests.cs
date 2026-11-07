using DynamicModules.Application.Common.Acting;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.DemoService;
using DynamicModules.Application.Dynamic.DemoService.Commands.GenerateDemoData;
using DynamicModules.Application.Dynamic.ReportService;
using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Application.Dynamic.ReportService.Queries.RunReport;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Reports;
using DynamicModules.Domain.Repositories.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic.Demo;

[Collection(DemoGeneratedCollection.Name)]
public class DemoReportsTests(DemoGeneratedFixture fx)
{
    /// <summary>各分享群組的主管帳號（示範人員清單的成員）。</summary>
    private static readonly Dictionary<string, string> Managers = new()
    {
        ["sales-manager"] = "manager01", ["maint"] = "maint01", ["qa"] = "qa01"
    };

    private async Task<List<Report>> StoredReports() =>
        await fx.Host.Db.Context.Set<Report>().AsNoTracking().Include(r => r.Shares).OrderBy(r => r.Id).ToListAsync();

    [Fact]
    public async Task Generate_CreatesReportsForEachScenario_AllStateOk()
    {
        fx.Generated();
        var reports = await StoredReports();
        var groups = await fx.Host.Db.Context.Set<PermissionGroup>().AsNoTracking().ToDictionaryAsync(g => g.Id, g => g.Key);
        // 示範報表之外，再加上「CRM 與銷售」套件自帶的兩份
        reports.Select(r => r.Name).Should().BeEquivalentTo([.. DemoReports.Names, "商機階段分布", "各業務訂單金額（未稅）"]);
        DemoReports.All.GroupBy(s => s.GroupKeys[0]).Should().OnlyContain(g => g.Count() >= 2);

        foreach (var spec in DemoReports.All)
        {
            var report = reports.Single(r => r.Name == spec.Name);
            report.Shares.Select(s => groups[s.GroupId]).Should().BeEquivalentTo(spec.GroupKeys, spec.Name);

            // 定義本身通過存檔驗證的結構規則，並以主管身分實際執行
            ReportRules.CheckShape(ReportDefinition.Parse(report.DefinitionJson)).Should().BeEmpty(spec.Name);
            var run = (ReportResult)(await fx.As(Managers[spec.GroupKeys[0]], new RunReportRequest(report.Id, null, "zh-TW"))).Data!;
            run.State.Should().Be("ok", $"{spec.Name}：{run.Message}");
        }
    }

    [Fact]
    public async Task Generate_OrderMergedReport_GroupsByCustomerTitle()
    {
        fx.Generated();
        var spec = DemoReports.All.Single(s => s.Name == "近 90 天各客戶訂單金額");
        var definition = ReportDefinition.Parse((await StoredReports()).Single(r => r.Name == spec.Name).DefinitionJson);
        definition.Child.Should().NotBeNull();
        definition.Groups.Single().Field.ViaFieldId.Should().NotBeNull();
        definition.Measures.Select(m => m.Id).Should().Equal("count", "amount");
    }

    [Fact]
    public async Task FloorStaff_OpenAmountReport_SeesRestricted()
    {
        fx.Generated();
        var report = (await StoredReports()).Single(r => r.Name == "近 90 天各客戶訂單金額");
        var groups = await fx.Host.Db.Context.Set<PermissionGroup>().AsNoTracking().ToDictionaryAsync(g => g.Id, g => g.Key);
        report.Shares.Select(s => groups[s.GroupId]).Should().Contain("floor");

        // 現場人員沒有金額權限：整張無權限、不帶數字；業務主管同一張是 ok
        var floor = (ReportResult)(await fx.As("floor01", new RunReportRequest(report.Id, null, "zh-TW"))).Data!;
        floor.State.Should().Be("restricted");
        floor.Groups.Should().BeNull();
        floor.Total.Should().BeNull();
        var manager = (ReportResult)(await fx.As("manager01", new RunReportRequest(report.Id, null, "zh-TW"))).Data!;
        manager.State.Should().Be("ok");
    }

    [Fact]
    public async Task Generate_RecordsGeneratedOn()
    {
        fx.Generated();
        (await fx.Host.Services.GetRequiredService<ISystemSettingRepository>()
            .GetValueAsync(SystemSetting.DemoGeneratedOn, CancellationToken.None)).Should().Be("2026-11-05");
    }

    [Fact]
    public async Task SalesOne_vs_SalesManager_SameReport_DifferentNumbers()
    {
        fx.Generated();
        var report = (await StoredReports()).Single(r => r.Name == "近 90 天訂單狀態分布");

        async Task<ReportResult> RunAs(string user) =>
            (ReportResult)(await fx.As(user, new RunReportRequest(report.Id, null, "zh-TW"))).Data!;

        var manager = await RunAs("manager01");
        var sales = await RunAs("sales01");
        manager.State.Should().Be("ok");
        sales.State.Should().Be("ok");
        manager.Total!["count"].Should().Be(40);
        sales.Total!["count"].Should().BeLessThan(manager.Total["count"]!.Value).And.BeGreaterThan(0);
        manager.BasisIsFrozen.Should().BeFalse();
    }

    [Theory]
    [InlineData(true, "ok", true)]
    [InlineData(false, "empty", false)]
    public async Task Run_120DaysAfterGeneration_DemoLoginDecidesBasis(bool demoLogin, string state, bool frozen)
    {
        var clock = new MutableTime(DemoTestHost.Today);
        await using var host = await DemoTestHost.CreateAsync(configure: s =>
        {
            s.AddSingleton<IBusinessTime>(clock);
            s.AddSingleton(new AuthSettings(demoLogin));
        });
        (await host.Send(new GenerateDemoDataRequest(DemoTestHost.Today))).IsSuccess.Should().BeTrue();
        var report = await host.Db.Context.Set<Report>().AsNoTracking().SingleAsync(r => r.Name == "近 90 天訂單狀態分布");

        clock.Today = DemoTestHost.Today.AddDays(120);
        using var _ = ActingScope.Begin("manager01");
        var result = (ReportResult)(await host.Send(new RunReportRequest(report.Id, null, "zh-TW"))).Data!;

        result.State.Should().Be(state);
        result.BasisDate.Should().Be(demoLogin ? DemoTestHost.Today : clock.Today);
        result.BasisIsFrozen.Should().Be(frozen);
    }

    private sealed class MutableTime(DateOnly today) : IBusinessTime
    {
        public DateOnly Today { get; set; } = today;
        public DateTime Now => Today.ToDateTime(new TimeOnly(10, 0));
    }

    [Fact]
    public async Task BasisDate_DemoLoginOff_UsesToday()
    {
        await using var host = await DemoTestHost.CreateAsync();
        // 就算有產生日期設定也用今天
        await SetGeneratedOn(host, "2026-09-01");
        (await host.Services.GetRequiredService<ReportRunner>().BasisDateAsync(CancellationToken.None))
            .Should().Be(DemoTestHost.Today);
    }

    [Fact]
    public async Task BasisDate_DemoLoginOn_UsesGeneratedOn()
    {
        await using var host = await DemoTestHost.CreateAsync(configure: s => s.AddSingleton(new AuthSettings(true)));
        var runner = host.Services.GetRequiredService<ReportRunner>();

        // 沒有設定時（沒產生過 Demo 資料）退回今天
        (await runner.BasisDateAsync(CancellationToken.None)).Should().Be(DemoTestHost.Today);

        await SetGeneratedOn(host, "2026-09-01");
        (await runner.BasisDateAsync(CancellationToken.None)).Should().Be(new DateOnly(2026, 9, 1));
    }

    [Fact]
    public async Task Preflight_ExistingDemoReport_Conflict()
    {
        await using var host = await DemoTestHost.CreateAsync();
        var name = DemoReports.Names[0];
        host.Db.Context.Set<Report>().Add(new Report { OwnerUserId = "admin", Name = name, RowVersion = "x" });
        await host.Db.Context.SaveChangesAsync();

        var conflicts = await host.Services.GetRequiredService<DemoPreflight>().ConflictsAsync(CancellationToken.None);
        conflicts.Should().Contain($"報表「{name}」已存在");

        var result = await host.Send(new GenerateDemoDataRequest(DemoTestHost.Today));
        result.IsConflict.Should().BeTrue();
    }

    [Fact]
    public async Task FreshInstall_NoReports()
    {
        await using var host = await DemoTestHost.CreateAsync();
        (await host.Db.Context.Set<Report>().CountAsync()).Should().Be(0);
    }

    private static async Task SetGeneratedOn(DemoTestHost host, string value)
    {
        await host.Services.GetRequiredService<ISystemSettingRepository>()
            .SetAsync(SystemSetting.DemoGeneratedOn, value, CancellationToken.None);
        await host.Db.Context.SaveChangesAsync();
    }
}
