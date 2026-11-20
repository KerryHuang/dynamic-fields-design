using System.Net;
using System.Net.Http.Json;
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

    private async Task Ping() => (await _app.AsUser().GetAsync("/api/health/ready")).StatusCode.Should().Be(HttpStatusCode.OK);

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
}
