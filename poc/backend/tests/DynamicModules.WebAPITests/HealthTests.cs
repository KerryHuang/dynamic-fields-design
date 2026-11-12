using System.Diagnostics;
using System.Net;
using System.Text.Json;
using DynamicModules.WebAPI.Ops;
using DynamicModules.WebAPITests.Ops;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DynamicModules.WebAPITests;

public class HealthTests
{
    private sealed class FakeCheck(Func<CancellationToken, Task<HealthCheckResult>> run) : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default) => run(ct);
    }

    private static HttpClient WithDatabaseCheck(TestApp app, Func<CancellationToken, Task<HealthCheckResult>> run) =>
        app.WithWebHostBuilder(b => b.ConfigureServices(s => s.Configure<HealthCheckServiceOptions>(o =>
        {
            // 沿用正式註冊的標籤：正式註冊漏掉 ready 標籤時，這些測試會失敗
            var original = o.Registrations.Single(r => r.Name == "database");
            o.Registrations.Remove(original);
            o.Registrations.Add(new HealthCheckRegistration("database", _ => new FakeCheck(run), null, original.Tags));
        }))).CreateClient();

    [Fact]
    public async Task Health_ReturnsOk()
    {
        using var app = new TestApp();
        var res = await app.CreateClient().GetAsync("/api/health");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadAsStringAsync()).Should().Contain("\"status\":\"ok\"");
    }

    [Fact]
    public async Task Live_ReturnsOk()
    {
        using var app = new TestApp();
        var res = await app.CreateClient().GetAsync("/api/health/live");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadAsStringAsync()).Should().Be("{\"status\":\"ok\"}");
    }

    [Fact]
    public async Task Ready_DatabaseOk_200()
    {
        using var app = new TestApp();
        var res = await app.CreateClient().GetAsync("/api/health/ready");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadAsStringAsync()).Should().Be("{\"status\":\"ready\"}");
    }

    [Fact]
    public async Task Ready_DatabaseFails_503_NoDetails_LiveStill200()
    {
        using var app = new TestApp();
        var client = WithDatabaseCheck(app, _ => Task.FromResult(HealthCheckResult.Unhealthy("secret-detail", new Exception("secret-detail"))));
        var res = await client.GetAsync("/api/health/ready");
        res.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var body = await res.Content.ReadAsStringAsync();
        body.Should().Be("{\"status\":\"notReady\",\"checks\":{\"database\":\"fail\"}}");
        body.Should().NotContain("secret-detail");
        (await client.GetAsync("/api/health/live")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Ready_DatabaseHangs_503_BecauseTheTimeoutCancelsTheCheck()
    {
        // 不斷言「幾秒內回來」：整套測試並行時執行緒池塞住，計時器與延續會晚好幾秒，牆鐘上限會隨機失敗。
        // 改證明機制：檢查收到的 token 是被逾時取消的（用戶端沒中止），逾時值是 2 秒，且確實等到逾時才回；
        // 防止「逾時沒生效而永遠卡住」靠 WaitAsync 的卡死保護。
        using var app = new TestApp();
        var cancelledByTimeout = false;
        var client = WithDatabaseCheck(app, async ct =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { cancelledByTimeout = true; throw; }
            return HealthCheckResult.Healthy();
        });
        var sw = Stopwatch.StartNew();
        var res = await client.GetAsync("/api/health/ready").WaitAsync(TimeSpan.FromSeconds(120));
        sw.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1.5));   // 計時器不會提早觸發
        cancelledByTimeout.Should().BeTrue();
        OpsEndpoints.ReadyTimeout.Should().Be(TimeSpan.FromSeconds(2));
        res.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await res.Content.ReadAsStringAsync()).Should().Be("{\"status\":\"notReady\",\"checks\":{\"database\":\"fail\"}}");
        app.Logs.Records.Should().NotContain(r => r.EventId.Id == 5000);
    }

    [Fact]
    public async Task Version_OnlyThreeFields()
    {
        using var app = new TestApp();
        var res = await app.CreateClient().GetAsync("/api/version");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var root = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        root.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("version", "commit", "builtAt");
        root.EnumerateObject().Select(p => p.Value.GetString()).Should().OnlyContain(v => v == "dev");
    }
}
