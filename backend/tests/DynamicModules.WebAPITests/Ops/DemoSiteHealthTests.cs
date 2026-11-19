using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Application.Dynamic.OpsService.Health;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static DynamicModules.WebAPITests.Ops.OpsIncidentKit;

namespace DynamicModules.WebAPITests.Ops;

/// <summary>
/// Demo 站關掉磁碟剩餘空間檢查（使用者 2026-11-19 裁決）：Render 免費方案在 Docker overlay 上，DriveInfo 量到的是主機共用根磁碟。
/// 開 Demo 登入時這項檢查不適用、出廠關閉：清單照列（applicable 為假、狀態 notApplicable、訊息代碼 demoSite），看門狗不跑、不開事件，立即檢查 409。
/// </summary>
[Trait("Category", "Postgres")]
public class DemoSiteHealthTests
{
    private const string Disk = HealthCheckCatalog.StorageDiskFree, Writable = HealthCheckCatalog.DatabaseWritable;

    private sealed class DemoSiteApp : TestApp
    {
        public DemoSiteApp(FakeDiskSpace disk)
        {
            Settings["Demo:LoginEnabled"] = "true";
            ExtraServices = s => FakeDisk(s, disk);
        }
    }

    private static JsonElement Find(JsonElement list, string code) => list.EnumerateArray().Single(c => c.GetProperty("code").GetString() == code);

    [Fact]
    public async Task DemoSite_DiskFree_NotApplicable_NotRun_NoIncident_RunNow409()
    {
        // 磁碟只剩 5%：一般站會開嚴重事件
        using var app = new DemoSiteApp(new FakeDiskSpace { FreePercent = 5 });
        var watchdog = app.Services.GetRequiredService<OpsWatchdog>();
        var engine = app.Services.GetRequiredService<HealthCheckEngine>();
        app.Time.Advance(TimeSpan.FromMinutes(5));
        await watchdog.TickAsync(CancellationToken.None);

        engine.Board.Should().ContainKey(Writable, "其他看門狗檢查照跑");
        engine.Board.Should().NotContainKey(Disk, "看門狗不跑");
        (await Incidents(app)).Should().NotContain(i => i.Fingerprint == HealthFingerprint(Disk));
        (await Results(app, Disk)).Should().BeEmpty();

        var list = await app.AsUser().GetFromJsonAsync<JsonElement>($"{Base}/health-checks");
        var disk = Find(list, Disk);
        disk.GetProperty("applicable").GetBoolean().Should().BeFalse();
        disk.GetProperty("enabled").GetProperty("value").GetBoolean().Should().BeFalse("Demo 站出廠關閉");
        disk.GetProperty("enabled").GetProperty("factory").GetBoolean().Should().BeFalse();
        disk.GetProperty("state").GetProperty("status").GetString().Should().Be("notApplicable");
        disk.GetProperty("state").GetProperty("messageCode").GetString().Should().Be("demoSite");
        Find(list, Writable).GetProperty("applicable").GetBoolean().Should().BeTrue();

        var overview = await app.AsUser().GetFromJsonAsync<JsonElement>($"{Base}/overview");
        var state = Find(overview.GetProperty("checks"), Disk);
        state.GetProperty("status").GetString().Should().Be("notApplicable");
        state.GetProperty("messageCode").GetString().Should().Be("demoSite");
        overview.GetProperty("overall").GetString().Should().NotBe("critical");

        var run = await app.AsUser().PostAsync($"{Base}/health-checks/{Disk}/run", null);
        run.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await run.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().Should().Be("healthCheckNotApplicable");

        // PUT 照不適用檢查的現行處理（存得進去），存了開啟也不跑
        (await app.AsUser().PutAsJsonAsync($"{Base}/health-checks/{Disk}", new { enabled = true })).EnsureSuccessStatusCode();
        app.Time.Advance(TimeSpan.FromMinutes(10));
        await watchdog.TickAsync(CancellationToken.None);
        engine.Board.Should().NotContainKey(Disk);
        (await Incidents(app)).Should().NotContain(i => i.Fingerprint == HealthFingerprint(Disk));
        Find(await app.AsUser().GetFromJsonAsync<JsonElement>($"{Base}/health-checks"), Disk)
            .GetProperty("state").GetProperty("status").GetString().Should().Be("notApplicable");
    }

    [Fact]
    public async Task NotDemoSite_DiskFree_RunsAndOpensIncident()
    {
        using var app = new TestApp { ExtraServices = s => FakeDisk(s, new FakeDiskSpace { FreePercent = 5 }) };
        app.Time.Advance(TimeSpan.FromMinutes(5));
        await app.Services.GetRequiredService<OpsWatchdog>().TickAsync(CancellationToken.None);

        app.Services.GetRequiredService<HealthCheckEngine>().Board[Disk].Status.Should().Be(HealthStatus.Critical);
        (await Incidents(app)).Should().Contain(i => i.Fingerprint == HealthFingerprint(Disk));
        var disk = Find(await app.AsUser().GetFromJsonAsync<JsonElement>($"{Base}/health-checks"), Disk);
        disk.GetProperty("applicable").GetBoolean().Should().BeTrue();
        disk.GetProperty("enabled").GetProperty("value").GetBoolean().Should().BeTrue();
    }
}
