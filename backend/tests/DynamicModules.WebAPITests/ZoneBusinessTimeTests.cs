using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.WebAPI.Time;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DynamicModules.WebAPITests;

public class ZoneBusinessTimeTests
{
    // 沒載入過資料庫時快照用部署出廠值，借它設定偏移，不必假造 reader
    private static ZoneBusinessTime Create(Dictionary<string, string> deployment) =>
        new(new CachedSystemPolicy(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System, new PolicyEnvironmentDefaults(deployment), NullLogger<CachedSystemPolicy>.Instance));

    [Fact]
    public void FollowsPolicyOffset_KindUnspecified()
    {
        var t = Create(new() { ["locale.timeZone"] = "+07:00" });
        (t.Now - DateTime.UtcNow).TotalSeconds.Should().BeApproximately(7 * 3600, 5);
        t.Now.Kind.Should().Be(DateTimeKind.Unspecified);
        t.Today.Should().Be(DateOnly.FromDateTime(t.Now));
    }

    [Fact]
    public void Factory_IsUtcPlus8()
    {
        var t = Create([]);
        (t.Now - DateTime.UtcNow).TotalHours.Should().BeApproximately(8, 0.01);
    }
}
