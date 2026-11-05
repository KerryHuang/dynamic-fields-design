using DynamicModules.WebAPI.Time;
using FluentAssertions;

namespace DynamicModules.WebAPITests;

public class TaipeiBusinessTimeTests
{
    [Fact]
    public void TaipeiBusinessTime_IsUtcPlus8()
    {
        var t = new TaipeiBusinessTime();
        (t.Now - DateTime.UtcNow).TotalHours.Should().BeApproximately(8, 0.01);
        t.Today.Should().Be(DateOnly.FromDateTime(t.Now));
    }
}
