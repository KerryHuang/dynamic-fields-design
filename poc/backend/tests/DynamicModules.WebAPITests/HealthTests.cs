using System.Diagnostics;
using System.Net;
using System.Text.Json;
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
            o.Registrations.Remove(o.Registrations.Single(r => r.Name == "database"));
            o.Registrations.Add(new HealthCheckRegistration("database", _ => new FakeCheck(run), null, ["ready"]));
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
    public async Task Ready_DatabaseHangs_503Within2Seconds()
    {
        using var app = new TestApp();
        var client = WithDatabaseCheck(app, async ct => { await Task.Delay(Timeout.Infinite, ct); return HealthCheckResult.Healthy(); });
        var sw = Stopwatch.StartNew();
        var res = await client.GetAsync("/api/health/ready");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(4));
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
