using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.WebAPITests.Ops;

/// <summary>TestApp 加上 OpsTestController 的 ApplicationPart。</summary>
public sealed class OpsTestApp : TestApp
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(s => s.AddControllers().AddApplicationPart(typeof(OpsTestController).Assembly));
    }
}
