using System.Net;
using System.Net.Http.Json;
using DynamicModules.WebAPI.Ops;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace DynamicModules.WebAPITests.Ops;

public class ClientErrorsApiTests
{
    private const string Url = "/api/v3/dynamic/ops/client-errors";

    private static object Body(string? message = "x is 3", string kind = "vue", object? extra = null) => new Dictionary<string, object?>
    {
        ["kind"] = kind,
        ["name"] = "TypeError",
        ["message"] = message,
        ["stack"] = "TypeError: x\n    at render (https://h/assets/IndexPage-BxY12abc.js:1:200)",
        ["route"] = "/dynamic/:moduleKey",
        ["moduleKey"] = "crm",
        ["version"] = "1.0",
        ["locale"] = "zh-TW",
        ["browser"] = "TestBrowser/1",
    };

    private static LogRecord Logged(TestApp app) => app.Logs.Records.Single(r => r.EventId.Id == 5001);

    [Fact]
    public async Task Valid_Anonymous_204_AndLogged()
    {
        using var app = new TestApp();
        var res = await app.AsUser(null).PostAsJsonAsync(Url, Body());
        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var rec = Logged(app);
        rec.Level.Should().Be(LogLevel.Warning);
        ((string)rec.Values["Fingerprint"]!).Should().MatchRegex("^[0-9a-f]{16}$");
        rec.Values["Route"].Should().Be("/dynamic/:moduleKey");
        rec.Values["UserId"].Should().BeNull();
        rec.Values["NormalizedMessage"].Should().Be("x is {n}");
    }

    [Fact]
    public async Task LoggedIn_UserIdFromCookie_NotBody()
    {
        using var app = new TestApp();
        var body = new Dictionary<string, object?> { ["kind"] = "window", ["message"] = "m", ["userId"] = "admin" };
        (await app.AsUser("sales01").PostAsJsonAsync(Url, body)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        Logged(app).Values["UserId"].Should().Be("sales01");
    }

    [Fact]
    public async Task StaleSession_TreatedAsAnonymous_204()
    {
        using var app = new TestApp();
        var res = await app.AsUser("ghost").PostAsJsonAsync(Url, Body());
        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
        res.Headers.Contains("Set-Cookie").Should().BeFalse();
        Logged(app).Values["UserId"].Should().BeNull();
    }

    [Fact]
    public async Task TooLarge_413()
    {
        using var app = new TestApp();
        var client = app.AsUser(null);
        app.ResetBodyCount();
        var res = await client.PostAsJsonAsync(Url, Body(message: new string('z', 9 * 1024)));
        res.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        app.BodyBytesRead.Should().BeLessThan(9000);
    }

    [Fact]
    public async Task OverRate_429()
    {
        using var app = new TestApp();
        var client = app.AsUser(null);
        for (var i = 0; i < 30; i++) (await client.PostAsJsonAsync(Url, Body())).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.PostAsJsonAsync(Url, Body())).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task InvalidKind_400()
    {
        using var app = new TestApp();
        (await app.AsUser(null).PostAsJsonAsync(Url, Body(kind: "nope"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task LongFields_Truncated()
    {
        using var app = new TestApp();
        var body = new Dictionary<string, object?>
        {
            ["kind"] = "window", ["message"] = new string('z', 2000), ["stack"] = new string('s', 6000),
        };
        (await app.AsUser(null).PostAsJsonAsync(Url, body)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var rec = Logged(app);
        ((string)rec.Values["RawMessage"]!).Length.Should().Be(500);
        ((string)rec.Values["RawStack"]!).Length.Should().Be(4000);
    }

    [Fact]
    public async Task OtherFields_TruncatedAndValidated()
    {
        using var app = new TestApp();
        var good = new string('a', 32);
        var body = new Dictionary<string, object?>
        {
            ["kind"] = "window", ["name"] = new string('n', 300), ["browser"] = new string('b', 500),
            ["route"] = "/dynamic/:moduleKey?x=1#h", ["lastRequestId"] = "not-hex",
        };
        var client = app.AsUser(null);
        await client.PostAsJsonAsync(Url, body);
        body["route"] = "javascript:x";
        body["lastRequestId"] = good;
        await client.PostAsJsonAsync(Url, body);

        var recs = app.Logs.Records.Where(r => r.EventId.Id == 5001).ToList();
        recs[0].Values["ErrorName"].Should().BeOfType<string>().Which.Length.Should().Be(100);
        ((string)recs[0].Values["Browser"]!).Length.Should().Be(200);
        recs[0].Values["Route"].Should().Be("/dynamic/:moduleKey");
        recs[0].Values["LastRequestId"].Should().BeNull();
        recs[1].Values["Route"].Should().BeNull();
        recs[1].Values["LastRequestId"].Should().Be(good);
    }

    [Fact]
    public void Throttle_ManyIps_DictionaryBounded()
    {
        var time = new FakeTimeProvider();
        var throttle = new ClientErrorThrottle(time);
        var ok = Enumerable.Range(0, 1000).Count(i => throttle.TryAcquire($"203.0.{i / 256}.{i % 256}"));
        ok.Should().Be(600);
        throttle.TrackedKeys.Should().BeLessThanOrEqualTo(600);
        time.Advance(TimeSpan.FromSeconds(61));
        throttle.TryAcquire("198.51.100.9").Should().BeTrue();
        throttle.TrackedKeys.Should().BeLessThanOrEqualTo(1);
    }

    [Fact]
    public async Task PolicyOff_204_NoLog()
    {
        using var app = new TestApp();
        (await app.AsUser().PutAsJsonAsync("/api/v3/dynamic/system/policies/ops.clientErrorCollection", new { value = false }))
            .IsSuccessStatusCode.Should().BeTrue();
        app.Time.Advance(TimeSpan.FromSeconds(61));
        (await app.AsUser(null).PostAsJsonAsync(Url, Body())).StatusCode.Should().Be(HttpStatusCode.NoContent);
        app.Logs.Records.Should().NotContain(r => r.EventId.Id == 5001);
    }
}
