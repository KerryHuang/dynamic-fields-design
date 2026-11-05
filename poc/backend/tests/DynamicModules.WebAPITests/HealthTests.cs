using System.Net;
using FluentAssertions;

namespace DynamicModules.WebAPITests;

public class HealthTests
{
    [Fact]
    public async Task Health_ReturnsOk()
    {
        using var app = new TestApp();
        var res = await app.CreateClient().GetAsync("/api/health");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadAsStringAsync()).Should().Contain("\"status\":\"ok\"");
    }
}
