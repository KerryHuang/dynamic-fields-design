using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace DynamicModules.WebAPITests;

public class SecurityTests(TestApp app) : IClassFixture<TestApp>
{
    private const string Base = "/api/v3/dynamic";

    [Theory]
    [InlineData(null)]
    [InlineData("nobody")]
    public async Task MissingOrUnknownUser_Returns401(string? user)
    {
        var res = await app.AsUser(user).GetAsync($"{Base}/menu");

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await res.Content.ReadFromJsonAsync<Dictionary<string, string>>())!["message"].Should().Be("請先選擇使用者");
    }

    [Fact]
    public async Task UsersEndpointIsAnonymous()
    {
        var res = await app.AsUser(null).GetAsync($"{Base}/users");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Health_IsNotGuarded() =>
        (await app.AsUser(null).GetAsync("/api/health")).StatusCode.Should().Be(HttpStatusCode.OK);

    [Fact]
    public async Task DesignerEndpoint_NonAdmin_Returns403()
    {
        var res = await app.AsUser("sales01").PostAsJsonAsync($"{Base}/modules",
            new { key = "x-sec", name = new Dictionary<string, string> { ["zh-TW"] = "x" } });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await res.Content.ReadFromJsonAsync<Dictionary<string, string>>())!["message"].Should().Be("沒有權限執行此操作");
    }
}
