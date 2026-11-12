using System.Net;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DynamicModules.WebAPITests.Ops;

public class UnhandledExceptionTests
{
    private const string Base = "/api/v3/dynamic";

    private static HttpClient Client(TestApp app, Func<int, Exception> make, string? locale = null)
    {
        var mediator = new ThrowingMediator(make);
        var client = app.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddScoped<IMediator>(_ => mediator))).CreateClient();
        client.DefaultRequestHeaders.Add("X-User", "admin");
        if (locale is not null) client.DefaultRequestHeaders.Add("X-Locale", locale);
        return client;
    }

    private static Func<int, Exception> Boom => n => new InvalidOperationException($"boom {n} 'secret'");

    private static IEnumerable<LogRecord> Unhandled(TestApp app) => app.Logs.Records.Where(r => r.EventId.Id == 5000);

    [Fact]
    public async Task Unhandled_Returns500ProblemDetails_WithTraceIdAndMessage()
    {
        using var app = new TestApp();
        var res = await Client(app, Boom).GetAsync($"{Base}/modules/zz");
        res.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        res.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var text = await res.Content.ReadAsStringAsync();
        text.Should().NotContain("boom");
        var root = JsonDocument.Parse(text).RootElement;
        root.GetProperty("traceId").GetString().Should().Be(res.Headers.GetValues("X-Request-Id").Single());
        root.GetProperty("message").GetString().Should().Be("系統發生錯誤，請稍後再試");

        var en = await Client(app, Boom, "en-US").GetAsync($"{Base}/modules/zz");
        JsonDocument.Parse(await en.Content.ReadAsStringAsync()).RootElement.GetProperty("message").GetString()
            .Should().Be("Something went wrong. Please try again later");
    }

    [Fact]
    public async Task Unhandled_LogsErrorWithFingerprint()
    {
        using var app = new TestApp();
        var res = await Client(app, Boom).GetAsync($"{Base}/modules/zz");
        var rec = Unhandled(app).Should().ContainSingle().Subject;
        rec.Level.Should().Be(LogLevel.Error);
        ((string)rec.Values["Fingerprint"]!).Should().MatchRegex("^[0-9a-f]{16}$");
        rec.Values["FingerprintVersion"].Should().Be(1);
        rec.Values["Route"].Should().Be("api/v3/dynamic/modules/{key}");
        rec.Values["ModuleKey"].Should().Be("zz");
        rec.Values["UserId"].Should().Be("admin");
        rec.Values["ExceptionType"].Should().Be("System.InvalidOperationException");
        rec.Values["requestId"].Should().Be(res.Headers.GetValues("X-Request-Id").Single());
    }

    [Fact]
    public async Task Unhandled_RequestCompleted_HasRouteTemplate()
    {
        using var app = new TestApp();
        await Client(app, Boom).GetAsync($"{Base}/modules/zz");
        var done = app.Logs.Records.Single(r => r.EventId.Id == 5100);
        done.Level.Should().Be(LogLevel.Error);
        done.Values["StatusCode"].Should().Be(500);
        done.Values["Route"].Should().Be("api/v3/dynamic/modules/{key}");
        done.Values["ModuleKey"].Should().Be("zz");
    }

    [Fact]
    public async Task Unhandled_SameDefectTwice_SameFingerprint()
    {
        using var app = new TestApp();
        var client = Client(app, Boom);
        await client.GetAsync($"{Base}/modules/zz");
        await client.GetAsync($"{Base}/modules/zz");
        Unhandled(app).Select(r => r.Values["Fingerprint"]).Distinct().Should().HaveCount(1);
        Unhandled(app).Should().HaveCount(2);
    }

    [Fact]
    public async Task BadHttpRequest_KeepsStatus_NotLoggedAsUnhandled()
    {
        using var app = new OpsTestApp();
        var res = await app.AsUser().GetAsync($"{Base}/test-ops/bad-request");
        res.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        Unhandled(app).Should().BeEmpty();
        app.Logs.Records.Single(r => r.EventId.Id == 5100).Level.Should().Be(LogLevel.Information);
    }

    [Fact]
    public async Task ExistingHandlers_StillWin()
    {
        using var app = new TestApp();
        var res = await Client(app, _ => new DbUpdateConcurrencyException("race")).GetAsync($"{Base}/modules/zz");
        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        Unhandled(app).Should().BeEmpty();
    }

    private static (System.Diagnostics.Metrics.MeterListener Listener, List<string> Tags) ListenRuleFailures()
    {
        var tags = new List<string>();
        var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (inst, l) =>
        {
            if (inst.Meter.Name == "Formwright" && inst.Name == "formwright.rule.failures") l.EnableMeasurementEvents(inst);
        };
        listener.SetMeasurementEventCallback<long>((_, _, t, _) =>
        {
            foreach (var kv in t) if (kv.Key == "ruleKind") lock (tags) tags.Add((string)kv.Value!);
        });
        listener.Start();
        return (listener, tags);
    }

    [Fact]
    public async Task Unhandled_WithRuleTag_CountsRuleFailure()
    {
        using var app = new TestApp();
        var (listener, tags) = ListenRuleFailures();
        using var _ = listener;
        var client = Client(app, _ =>
        {
            var e = new InvalidOperationException("boom");
            DynamicModules.Application.Common.Observability.RuleFailureTag.Mark(e, "check", 7);
            return e;
        });
        await client.GetAsync($"{Base}/modules/zz");

        tags.Should().Equal("check");
        var rec = Unhandled(app).Single();
        rec.Values["RuleKind"].Should().Be("check");
        rec.Values["RuleId"].Should().Be(7L);
    }

    [Fact]
    public async Task HandledConflict_WithRuleTag_NotCounted()
    {
        using var app = new TestApp();
        var (listener, tags) = ListenRuleFailures();
        using var _ = listener;
        var res = await Client(app, _ =>
        {
            var e = new DbUpdateConcurrencyException("race");
            DynamicModules.Application.Common.Observability.RuleFailureTag.Mark(e, "check", 7);
            return e;
        }).GetAsync($"{Base}/modules/zz");

        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        tags.Should().BeEmpty();
    }
}
