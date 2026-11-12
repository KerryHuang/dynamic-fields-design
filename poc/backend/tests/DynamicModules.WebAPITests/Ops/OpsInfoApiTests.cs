using System.Net;
using System.Text.Json;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.WebAPITests.Ops;

public class OpsInfoApiTests
{
    private const string Url = "/api/v3/dynamic/system/ops/info";

    [Fact]
    public async Task Info_Anonymous_401()
    {
        using var app = new TestApp();
        (await app.AsUser(null).GetAsync(Url)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Info_NonAdmin_403()
    {
        using var app = new TestApp();
        (await app.AsUser("sales01").GetAsync(Url)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Info_Admin_200_Fields()
    {
        using var app = new TestApp();
        var res = await app.AsUser().GetAsync(Url);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var root = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        root.GetProperty("version").GetString().Should().Be("dev");
        root.GetProperty("commit").GetString().Should().Be("dev");
        root.GetProperty("builtAt").GetString().Should().Be("dev");
        root.GetProperty("edition").GetString().Should().Be("OnPrem");
        root.GetProperty("instanceId").GetString().Should().NotBeNullOrEmpty();
        root.GetProperty("database").GetString().Should().Be("sqlite");
        root.GetProperty("latestMigration").GetString().Should().MatchRegex(@"^\d{14}_\w+$");
        root.GetProperty("jobs").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Info_DeactivatedAdmin_401()
    {
        using var app = new TestApp();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
            db.Set<AppUser>().Add(new AppUser { UserId = "admin2", DisplayName = "管理員二", IsSystemAdmin = true, IsActive = true });
            await db.SaveChangesAsync();
        }
        var client = app.AsUser("admin2");
        (await client.GetAsync(Url)).StatusCode.Should().Be(HttpStatusCode.OK);
        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<AppUser>()
                .Where(u => u.UserId == "admin2").ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        (await client.GetAsync(Url)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Info_EditionCloud()
    {
        using var app = new TestApp();
        app.Settings["Ops:Edition"] = "Cloud";
        var res = await app.AsUser().GetAsync(Url);
        JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("edition").GetString().Should().Be("Cloud");
    }
}
