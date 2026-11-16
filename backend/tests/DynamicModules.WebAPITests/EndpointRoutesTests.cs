using FluentAssertions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.WebAPITests;

/// <summary>SessionMiddleware 只保護 /api/v3/dynamic 底下的路徑；新增的端點若落在別處就沒有登入檢查。</summary>
public class EndpointRoutesTests
{
    /// <summary>不需登入的公開路徑，明確列舉（health、版本）；其餘一律要在 /api/v3/dynamic 底下。</summary>
    private static readonly string[] PublicRoutes = ["/api/health", "/api/health/live", "/api/health/ready", "/api/version"];

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
                          && !PublicRoutes.Contains(r, StringComparer.OrdinalIgnoreCase))
            .Should().BeEmpty("所有端點都要在 SessionMiddleware 的保護範圍內");
    }
}
