using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.WebAPITests;

/// <summary>測試主機的 DbContext 有掛測試期寫入守衛（PostgreSQL provider 設計 §5.4），而且 Program 原本的選項照舊。</summary>
public class WriteGuardTests : IClassFixture<TestApp>
{
    private readonly TestApp _app;

    public WriteGuardTests(TestApp app) => _app = app;

    [Fact]
    public async Task TestAppContext_HasTheGuard()
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
        db.Set<AppUser>().Add(new AppUser { UserId = "guard-probe", DisplayName = new string('名', 101) });

        var act = () => db.SaveChangesAsync();

        (await act.Should().ThrowAsync<WriteGuardViolationException>()).Which.Message.Should().Contain("DisplayName");
    }

    [Fact]
    public async Task FakeBusinessTime_IsUnspecified_LikeZoneBusinessTime()
    {
        new FakeBusinessTime().Now.Kind.Should().Be(DateTimeKind.Unspecified);
        new FakeBusinessTime { Fixed = DateTime.UtcNow }.Now.Kind.Should().Be(DateTimeKind.Unspecified);
        (await _app.AsUser().GetAsync("/api/health/ready")).IsSuccessStatusCode.Should().BeTrue();
    }
}
