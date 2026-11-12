using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DynamicModules.WebAPITests.Ops;

public class RequestContextTests
{
    private const string Base = "/api/v3/dynamic";

    private static string RequestId(HttpResponseMessage res) => res.Headers.GetValues("X-Request-Id").Single();

    private static async Task<JsonElement> BodyOf(HttpResponseMessage res) =>
        JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.Clone();

    private static IEnumerable<LogRecord> Completed(TestApp app) => app.Logs.Records.Where(r => r.EventId.Id == 5100);

    private static HttpClient WithMediator(TestApp app, Func<int, Exception> make, string user = "admin")
    {
        var mediator = new ThrowingMediator(make);
        var client = app.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddScoped<IMediator>(_ => mediator))).CreateClient();
        client.DefaultRequestHeaders.Add("X-User", user);
        return client;
    }

    [Fact]
    public async Task EveryResponse_HasRequestIdHeader_32Hex()
    {
        using var app = new TestApp();
        RequestId(await app.AsUser(null).GetAsync("/api/health")).Should().MatchRegex("^[0-9a-f]{32}$");
        RequestId(await app.AsUser(null).GetAsync($"{Base}/me")).Should().MatchRegex("^[0-9a-f]{32}$");
    }

    [Fact]
    public async Task ErrorBodyTraceId_EqualsHeader()
    {
        using var app = new TestApp();

        // 401：SessionMiddleware
        var r401 = await app.AsUser(null).GetAsync($"{Base}/me");
        r401.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var b401 = await BodyOf(r401);
        b401.GetProperty("traceId").GetString().Should().Be(RequestId(r401));
        b401.GetProperty("message").GetString().Should().NotBeNullOrEmpty();

        // 403：必須改密碼
        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<AppUser>()
                .Where(u => u.UserId == "admin").ExecuteUpdateAsync(s => s.SetProperty(u => u.MustChangePassword, true));
        var r403 = await app.AsUser().GetAsync($"{Base}/modules");
        r403.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var b403 = await BodyOf(r403);
        b403.GetProperty("traceId").GetString().Should().Be(RequestId(r403));
        b403.GetProperty("code").GetString().Should().Be("passwordChangeRequired");
        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<AppUser>()
                .Where(u => u.UserId == "admin").ExecuteUpdateAsync(s => s.SetProperty(u => u.MustChangePassword, false));

        // Idempotency 400
        var admin = app.AsUser();
        admin.DefaultRequestHeaders.Add("Idempotency-Key", "!");
        var r400 = await admin.GetAsync($"{Base}/modules");
        r400.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await BodyOf(r400)).GetProperty("traceId").GetString().Should().Be(RequestId(r400));

        // [ApiController] 自動 400
        var bad = await app.AsUser().PutAsync($"{Base}/system/policies/x",
            new StringContent("{bad", System.Text.Encoding.UTF8, "application/json"));
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await BodyOf(bad)).GetProperty("traceId").GetString().Should().Be(RequestId(bad));

        // 409 stale
        var r409 = await WithMediator(app, _ => new DbUpdateConcurrencyException("race")).GetAsync($"{Base}/modules");
        r409.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var b409 = await BodyOf(r409);
        b409.GetProperty("traceId").GetString().Should().Be(RequestId(r409));
        b409.GetProperty("code").GetString().Should().Be("stale");
    }

    [Fact]
    public async Task IncomingTraceparent_IgnoredByDefault()
    {
        using var app = new TestApp();
        var client = app.AsUser(null);
        client.DefaultRequestHeaders.Add("traceparent", "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01");
        RequestId(await client.GetAsync("/api/health")).Should().NotBe("0af7651916cd43dd8448eb211c80319c");
    }

    [Fact]
    public async Task IncomingTraceparent_TrustedWhenConfigured()
    {
        using var app = new TestApp();
        app.Settings["Ops:TrustIncomingTraceParent"] = "true";
        var client = app.AsUser(null);
        client.DefaultRequestHeaders.Add("traceparent", "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01");
        RequestId(await client.GetAsync("/api/health")).Should().Be("0af7651916cd43dd8448eb211c80319c");
    }

    [Fact]
    public async Task RequestLog_NoQueryString_UsesRouteTemplate()
    {
        using var app = new TestApp();
        var res = await app.AsUser().GetAsync($"{Base}/modules/zz?secret=abc123");
        var done = Completed(app).Should().ContainSingle(r => r.Values.ContainsKey("Route") && (string?)r.Values["Route"] == "api/v3/dynamic/modules/{key}").Subject;
        done.Values["Route"].Should().Be("api/v3/dynamic/modules/{key}");
        app.Logs.Records.Should().NotContain(r => r.Message.Contains("abc123") || r.Values.Values.Any(v => v != null && v.ToString()!.Contains("abc123")));
        RequestId(res).Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task RequestLog_HasUserRouteModuleKey()
    {
        using var app = new TestApp();
        var res = await app.AsUser().GetAsync($"{Base}/modules/zz");
        var done = Completed(app).Single(r => (string?)r.Values["Route"] == "api/v3/dynamic/modules/{key}");
        done.Values["UserId"].Should().Be("admin");
        done.Values["ModuleKey"].Should().Be("zz");
        done.Values["requestId"].Should().Be(RequestId(res));
    }

    [Fact]
    public async Task HandlerLog_HasUserAndModuleKeyScope()
    {
        using var app = new OpsTestApp();
        var res = await app.AsUser().GetAsync($"{Base}/test-ops/modules/zz/log");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var rec = app.Logs.Records.Single(r => r.Category.EndsWith("OpsTestController"));
        rec.Values["userId"].Should().Be("admin");
        rec.Values["moduleKey"].Should().Be("zz");
        rec.Values["route"].Should().Be("api/v3/dynamic/test-ops/modules/{key}/log");
        rec.Values["requestId"].Should().Be(RequestId(res));
    }

    [Fact]
    public async Task RequestLog_Levels()
    {
        using var app = new TestApp();
        await app.AsUser().GetAsync($"{Base}/me");                 // 2xx
        await app.AsUser(null).GetAsync($"{Base}/me");              // 401
        await app.AsUser(null).GetAsync("/api/health");
        await app.AsUser(null).GetAsync("/api/health/live");
        var busy = WithMediator(app, _ => new DatabaseBusyException("busy"));
        (await busy.GetAsync($"{Base}/modules")).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        var done = Completed(app).ToList();
        done.Where(r => (int)r.Values["StatusCode"]! == 200).Should().ContainSingle().Which.Level.Should().Be(LogLevel.Debug);
        done.Single(r => (int)r.Values["StatusCode"]! == 401).Level.Should().Be(LogLevel.Information);
        done.Single(r => (int)r.Values["StatusCode"]! == 503).Level.Should().Be(LogLevel.Warning);
        done.Should().NotContain(r => ((string)r.Values["Route"]!).Contains("health"));
    }

    [Fact]
    public async Task SlowRequest_Warning()
    {
        using var app = new OpsTestApp();
        var admin = app.AsUser();
        var put = await admin.PutAsJsonAsync($"{Base}/system/policies/ops.slowRequestMs", new { value = 200 });
        put.IsSuccessStatusCode.Should().BeTrue(await put.Content.ReadAsStringAsync());
        app.Time.Advance(TimeSpan.FromSeconds(61));
        await admin.GetAsync($"{Base}/test-ops/slow?ms=300");
        Completed(app).Single(r => ((string)r.Values["Route"]!).EndsWith("test-ops/slow")).Level.Should().Be(LogLevel.Warning);
    }

    private sealed class CountingReader(IPolicyReader inner, List<string> seen) : IPolicyReader
    {
        public Task<PolicyResolution> ResolveAsync(PolicyDefinition def, long? moduleId, CancellationToken ct) { seen.Add(def.Code); return inner.ResolveAsync(def, moduleId, ct); }
        public Task<int> IntAsync(PolicyDefinition def, long? moduleId, CancellationToken ct) { seen.Add(def.Code); return inner.IntAsync(def, moduleId, ct); }
        public Task<bool> BoolAsync(PolicyDefinition def, long? moduleId, CancellationToken ct) { seen.Add(def.Code); return inner.BoolAsync(def, moduleId, ct); }
        public Task<string> ChoiceAsync(PolicyDefinition def, long? moduleId, CancellationToken ct) { seen.Add(def.Code); return inner.ChoiceAsync(def, moduleId, ct); }
        public Task<IReadOnlyList<string>> ListAsync(PolicyDefinition def, long? moduleId, CancellationToken ct) { seen.Add(def.Code); return inner.ListAsync(def, moduleId, ct); }
        public void Invalidate() => inner.Invalidate();
    }

    [Fact]
    public async Task FastRequest_DoesNotReadPolicy()
    {
        using var app = new TestApp();
        var seen = new List<string>();
        var client = app.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.AddScoped<PolicyReader>(sp => ActivatorUtilities.CreateInstance<PolicyReader>(sp));
            s.AddScoped<IPolicyReader>(sp => new CountingReader(sp.GetRequiredService<PolicyReader>(), seen));
        })).CreateClient();
        client.DefaultRequestHeaders.Add("X-User", "admin");
        for (var i = 0; i < 5; i++) await client.GetAsync($"{Base}/me");
        seen.Should().NotContain("ops.slowRequestMs");
    }

    [Fact]
    public async Task UnmatchedPath_LoggedAsUnmatched()
    {
        using var app = new TestApp();
        await app.AsUser().GetAsync($"{Base}/nope/123");
        var done = Completed(app).Single();
        ((string)done.Values["Route"]!).Should().NotContain("123");
    }

    [Fact]
    public void ConsoleFormatter_JsonOutsideDevelopment()
    {
        using var prod = new TestApp().WithWebHostBuilder(b => b.UseEnvironment("Production"));
        prod.Services.GetRequiredService<IOptionsMonitor<Microsoft.Extensions.Logging.Console.ConsoleLoggerOptions>>()
            .CurrentValue.FormatterName.Should().Be("json");
        using var dev = new TestApp().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        dev.Services.GetRequiredService<IOptionsMonitor<Microsoft.Extensions.Logging.Console.ConsoleLoggerOptions>>()
            .CurrentValue.FormatterName.Should().Be("simple");
    }
}
