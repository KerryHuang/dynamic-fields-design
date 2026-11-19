using DynamicModules.Application.Common.Security;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Common;

public class LoginThrottleTests
{
    [Fact]
    public void TryBegin_ReservesQuota_SoConcurrentAttemptsFromOneIpCannotExceedIt()
    {
        var throttle = new LoginThrottle(TimeProvider.System);
        for (var i = 0; i < 19; i++)
        {
            throttle.TryBegin($"ghost{i}", "203.0.113.9").Should().BeTrue();
            throttle.Complete($"ghost{i}", "203.0.113.9", success: false);
        }

        var admitted = Enumerable.Range(0, 5).Count(i => throttle.TryBegin($"other{i}", "203.0.113.9"));

        admitted.Should().Be(1, "IP 只剩 1 次額度，進行中的嘗試也要算");
    }

    [Fact]
    public void Release_ReturnsReservedQuota_WhenAttemptThrows()
    {
        var throttle = new LoginThrottle(TimeProvider.System);
        for (var i = 0; i < 19; i++)
        {
            throttle.TryBegin($"ghost{i}", "203.0.113.9");
            throttle.Complete($"ghost{i}", "203.0.113.9", success: false);
        }
        throttle.TryBegin("a", "203.0.113.9").Should().BeTrue();
        throttle.TryBegin("b", "203.0.113.9").Should().BeFalse();

        throttle.Release("a", "203.0.113.9");

        throttle.TryBegin("b", "203.0.113.9").Should().BeTrue();
    }

    [Fact]
    public void Complete_Success_ClearsOnlyThatAccountIpPair()
    {
        var throttle = new LoginThrottle(TimeProvider.System);
        for (var i = 0; i < 4; i++)
        {
            throttle.TryBegin("alice", "198.51.100.7");
            throttle.Complete("alice", "198.51.100.7", success: false);
        }
        throttle.TryBegin("alice", "198.51.100.7");
        throttle.Complete("alice", "198.51.100.7", success: true);

        for (var i = 0; i < 5; i++) throttle.TryBegin("alice", "198.51.100.7").Should().BeTrue();
    }

    [Fact]
    public void TryDemoAttempt_CountsEveryAdmittedAttemptPerSource_SuccessIncluded()
    {
        var throttle = new LoginThrottle(TimeProvider.System);
        for (var i = 0; i < 30; i++) throttle.TryDemoAttempt("203.0.113.9").Should().BeTrue();

        throttle.TryDemoAttempt("203.0.113.9").Should().BeFalse("Demo 不需要秘密，成功也計入總量");
        throttle.TryDemoAttempt("198.51.100.7").Should().BeTrue("只限該來源");
        throttle.TryBegin("alice", "203.0.113.9").Should().BeTrue("密碼登入不受 Demo 總量影響");
    }
}
