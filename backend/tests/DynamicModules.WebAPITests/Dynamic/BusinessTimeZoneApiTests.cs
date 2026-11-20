using System.Net;
using System.Net.Http.Json;
using DynamicModules.Application.Dynamic.ImportService;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using DynamicModules.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>營運時區（A17）在真實 DI 下的快照行為：另一台改的 60 秒後才換、本機 PUT 立即換、資料庫讀不到時照常回應（p3 計畫 Task 3、4）。</summary>
[Trait("Category", "Postgres")]
public class BusinessTimeZoneApiTests : IDisposable
{
    /// <summary>Down 時 IPolicyReader 一律丟例外（模擬資料庫讀不到）。</summary>
    private sealed class ReaderSwitch { public volatile bool Down; }

    private sealed class DownReader : IPolicyReader
    {
        private static InvalidOperationException Fail() => new("db down");
        public Task<PolicyResolution> ResolveAsync(PolicyDefinition def, long? moduleId, CancellationToken ct) => throw Fail();
        public Task<int> IntAsync(PolicyDefinition def, long? moduleId, CancellationToken ct) => throw Fail();
        public Task<bool> BoolAsync(PolicyDefinition def, long? moduleId, CancellationToken ct) => throw Fail();
        public Task<string> ChoiceAsync(PolicyDefinition def, long? moduleId, CancellationToken ct) => throw Fail();
        public Task<IReadOnlyList<string>> ListAsync(PolicyDefinition def, long? moduleId, CancellationToken ct) => throw Fail();
        public void Invalidate() { }
    }

    private readonly TestApp _app = new();
    private readonly ReaderSwitch _reader = new();

    public BusinessTimeZoneApiTests() =>
        _app.ExtraServices = s => s.AddScoped<IPolicyReader>(sp =>
            _reader.Down ? new DownReader() : ActivatorUtilities.CreateInstance<PolicyReader>(sp));

    public void Dispose() => _app.Dispose();

    private CachedSystemPolicy Cache => _app.Services.GetRequiredService<CachedSystemPolicy>();

    /// <summary>送一個請求觸發 SystemPolicyRefreshMiddleware。打 live 而非 ready：ready 會在 2 秒逾時內查資料庫，CI 負載高時回 503（票 #67）。</summary>
    private async Task Ping() => (await _app.AsUser().GetAsync("/api/health/live")).StatusCode.Should().Be(HttpStatusCode.OK);

    /// <summary>直接寫資料庫（模擬另一台伺服器改的）。</summary>
    private void WriteSetting(string value)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
        var row = db.Set<SystemSetting>().SingleOrDefault(s => s.Key == PolicyCatalog.LocaleTimeZone.SettingKey);
        if (row is null) db.Set<SystemSetting>().Add(new SystemSetting { Key = PolicyCatalog.LocaleTimeZone.SettingKey, Value = value });
        else row.Value = value;
        db.SaveForTests();
    }

    [Fact]
    public async Task Middleware_ReloadsWhenStale()
    {
        await Ping();
        WriteSetting("+07:00");
        await Ping();
        Cache.BusinessOffset.Should().Be(TimeSpan.FromHours(8));

        _app.Time.Advance(TimeSpan.FromSeconds(61));
        await Ping();
        Cache.BusinessOffset.Should().Be(TimeSpan.FromHours(7));
    }

    [Fact]
    public async Task Put_NextRequestUsesNewZone()
    {
        var admin = _app.AsUser();
        (await admin.PutAsJsonAsync($"{Base}/system/policies/locale.timeZone", new { value = "+07:00" })).StatusCode.Should().Be(HttpStatusCode.OK);
        await Ping();
        Cache.BusinessOffset.Should().Be(TimeSpan.FromHours(7));
    }

    [Fact]
    public async Task DatabaseDown_ThenRecovers()
    {
        (await _app.AsUser().PutAsJsonAsync($"{Base}/system/policies/locale.timeZone", new { value = "+07:00" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        _reader.Down = true;
        WriteSetting("+09:00");
        _app.Time.Advance(TimeSpan.FromSeconds(61));
        await Ping();
        Cache.BusinessOffset.Should().Be(TimeSpan.FromHours(7), "讀不到時沿用上次成功的值");

        _reader.Down = false;
        _app.Time.Advance(TimeSpan.FromSeconds(11));
        await Ping();
        Cache.BusinessOffset.Should().Be(TimeSpan.FromHours(9), "恢復後重新讀到資料庫的值");
    }

    /// <summary>TestApp 的 IBusinessTime 是 ActingBusinessTime（包 Clock）：顯示用的偏移只看快照，不受裝飾器影響（Task 4）。</summary>
    [Fact]
    public async Task Export_FollowsZoneUnderActingDecorator()
    {
        var admin = _app.AsUser();
        var key = await CreateModule(admin);
        var at = await AddField(admin, key, "datetime");
        await Activate(admin, key);
        var title = await TitleKey(admin, key);
        var created = await JsonOf(await admin.PostAsJsonAsync($"{Base}/modules/{key}/records",
            new { values = new Dictionary<string, object> { [title] = "x", [at.Key] = "2026-11-11T09:00:00+08:00" } }));
        var id = created.GetProperty("id").GetInt64();
        (await admin.PutAsJsonAsync($"{Base}/records/{id}", new
        {
            values = new Dictionary<string, object> { [at.Key] = "2026-11-11T10:00:00+08:00" },
            rowVersion = created.GetProperty("rowVersion").GetString(),
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        async Task<string> ExportCell()
        {
            var res = await admin.GetAsync($"{Base}/modules/{key}/export?format=csv");
            res.StatusCode.Should().Be(HttpStatusCode.OK);
            var csv = CsvReader.Read(await res.Content.ReadAsByteArrayAsync(), 1000);
            csv.Error.Should().BeNull();
            return csv.Rows.Single().Cells[csv.Header.ToList().IndexOf("欄位")];
        }
        async Task<string?> HistoryNewText()
        {
            var res = await admin.GetAsync($"{Base}/records/{id}/history?currentPage=1&pagesSize=50");
            res.StatusCode.Should().Be(HttpStatusCode.OK);
            return (await JsonOf(res)).GetProperty("items").EnumerateArray()
                .First(h => h.TryGetProperty("fieldKey", out var f) && f.GetString() == at.Key).GetProperty("newText").GetString();
        }

        (await admin.PutAsJsonAsync($"{Base}/system/policies/locale.timeZone", new { value = "+07:00" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ExportCell()).Should().Be("2026-11-11 09:00");
        (await HistoryNewText()).Should().Be("2026-11-11 09:00");

        (await admin.PutAsJsonAsync($"{Base}/system/policies/locale.timeZone", new { value = "+08:00" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ExportCell()).Should().Be("2026-11-11 10:00");
        (await HistoryNewText()).Should().Be("2026-11-11 10:00");
    }
}
