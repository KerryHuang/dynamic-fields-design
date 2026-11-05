using FluentAssertions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.WebAPITests;

/// <summary>SessionMiddleware 只保護 /api/v3/dynamic 底下的路徑；新增的端點若落在別處就沒有登入檢查。</summary>
public class EndpointRoutesTests
{
    [Fact]
    public void AllEndpoints_AreUnderDynamicApi_OrHealth()
    {
        using var app = new TestApp();
        var routes = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => "/" + e.RoutePattern.RawText!.TrimStart('/'))
            .ToList();

        routes.Should().NotBeEmpty();
        routes.Where(r => !r.StartsWith("/api/v3/dynamic/", StringComparison.OrdinalIgnoreCase)
                          && !r.Equals("/api/v3/dynamic", StringComparison.OrdinalIgnoreCase)
                          && !r.Equals("/api/health", StringComparison.OrdinalIgnoreCase))
            .Should().BeEmpty("所有端點都要在 SessionMiddleware 的保護範圍內");
    }
}
