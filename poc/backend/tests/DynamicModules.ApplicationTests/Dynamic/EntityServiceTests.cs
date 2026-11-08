using DynamicModules.Application;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.EntityService.Queries.GetEntityLight;
using DynamicModules.Application.Dynamic.References;
using DynamicModules.ApplicationTests.Persistence;
using DynamicModules.Persistence;
using DynamicModules.Persistence.Seed;
using DynamicModules.TestSupport;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic;

public class EntityServiceTests : IAsyncLifetime
{
    private SqliteTestDb _db = null!;
    private ServiceProvider _sp = null!;

    private sealed class FakeUser : ICurrentUser { public string UserId => "admin"; }

    private sealed class FakeTime : IBusinessTime
    {
        public DateTime Now => new(2026, 11, 3, 9, 0, 0);
        public DateOnly Today => DateOnly.FromDateTime(Now);
    }

    public async Task InitializeAsync()
    {
        _db = await SqliteTestDb.CreateAsync();
        await SeedData.EnsureSeededAsync(_db.Context);
        await DemoPeople.EnsureAsync(_db.Context);
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddPersistenceRepositories();
        services.AddTestEntities();
        services.AddScoped(_ => _db.Context);
        services.AddScoped<ITransactionManager, EfTransactionManager>();
        services.AddScoped<IWorkSavepoints, EfWorkSavepoints>();
        services.AddSingleton<ICurrentUser, FakeUser>();
        services.AddSingleton<IBusinessTime, FakeTime>();
        _sp = services.BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        await _sp.DisposeAsync();
        await _db.DisposeAsync();
    }

    private TestEntityProvider Entities => _sp.GetRequiredService<TestEntityProvider>();

    private async Task<LightPage> Light(string? keyword, int page = 1, int size = 20)
    {
        var r = await _sp.GetRequiredService<IMediator>().Send(new GetEntityLightRequest("test.entity", keyword, page, size));
        r.IsSuccess.Should().BeTrue();
        return (LightPage)r.Data!;
    }

    [Fact]
    public async Task Light_Entity_KeywordMatchesNoOrName()
    {
        var byName = await Light("甲");
        byName.Items.Should().ContainSingle().Which.Should().Be(new LightItem("E-0001", "E-0001 甲號"));

        var byNo = await Light("E-000");
        byNo.Total.Should().Be(9);
        byNo.Items.Select(i => i.Key).Should().Equal("E-0001", "E-0002", "E-0003", "E-0004", "E-0005",
            "E-0006", "E-0007", "E-0008", "E-0009");
    }

    [Fact]
    public async Task Light_DeletedEntity_Excluded()
    {
        Entities.Delete("E-0003");

        (await Light("E-000")).Total.Should().Be(8);
    }

    [Fact]
    public async Task Titles_MixedKeys_OnlyExisting()
    {
        Entities.Delete("E-0003");

        var titles = await _sp.GetRequiredService<ReferenceProviderRegistry>().Get("test.entity")!
            .TitlesAsync(["E-0001", "E-9999", "E-0003"], default);

        titles.Should().BeEquivalentTo(new Dictionary<string, string> { ["E-0001"] = "E-0001 甲號" });
    }

    [Fact]
    public async Task Light_SizeCappedAt50()
    {
        for (var i = 0; i < 60; i++)
            Entities.Add($"X-{i:000}", "測試");

        var page = await Light("X-", size: 100);

        page.Items.Should().HaveCount(50);
        page.Total.Should().Be(60);
    }
}
