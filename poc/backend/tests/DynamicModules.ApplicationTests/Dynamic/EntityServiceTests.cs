using DynamicModules.Application;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.EntityService.Queries.GetEntityLight;
using DynamicModules.Application.Dynamic.References;
using DynamicModules.ApplicationTests.Persistence;
using DynamicModules.Domain.Entities.Mock;
using DynamicModules.Persistence;
using DynamicModules.Persistence.Seed;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
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
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddPersistenceRepositories();
        services.AddScoped(_ => _db.Context);
        services.AddScoped<ITransactionManager, EfTransactionManager>();
        services.AddSingleton<ICurrentUser, FakeUser>();
        services.AddSingleton<IBusinessTime, FakeTime>();
        _sp = services.BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        await _sp.DisposeAsync();
        await _db.DisposeAsync();
    }

    private async Task<LightPage> Light(string? keyword, int page = 1, int size = 20)
    {
        var r = await _sp.GetRequiredService<IMediator>().Send(new GetEntityLightRequest("design.mold", keyword, page, size));
        r.IsSuccess.Should().BeTrue();
        return (LightPage)r.Data!;
    }

    [Fact]
    public async Task Light_Mold_KeywordMatchesNoOrName()
    {
        var byName = await Light("上蓋");
        byName.Items.Should().ContainSingle().Which.Should().Be(new LightItem("M-0001", "M-0001 外殼上蓋模"));

        var byNo = await Light("M-000");
        byNo.Total.Should().Be(5);
        byNo.Items.Select(i => i.Key).Should().Equal("M-0001", "M-0002", "M-0003", "M-0004", "M-0005");
    }

    [Fact]
    public async Task Light_DeletedMold_Excluded()
    {
        (await _db.Context.Set<MockMold>().SingleAsync(m => m.MoldNo == "M-0003")).DeleteFlag = true;
        await _db.Context.SaveChangesAsync();

        (await Light("M-000")).Total.Should().Be(4);
    }

    [Fact]
    public async Task Titles_MixedKeys_OnlyExisting()
    {
        (await _db.Context.Set<MockMold>().SingleAsync(m => m.MoldNo == "M-0003")).DeleteFlag = true;
        await _db.Context.SaveChangesAsync();

        var titles = await _sp.GetRequiredService<ReferenceProviderRegistry>().Get("design.mold")!
            .TitlesAsync(["M-0001", "M-9999", "M-0003"], default);

        titles.Should().BeEquivalentTo(new Dictionary<string, string> { ["M-0001"] = "M-0001 外殼上蓋模" });
    }

    [Fact]
    public async Task Light_SizeCappedAt50()
    {
        for (var i = 0; i < 60; i++)
            _db.Context.Set<MockMold>().Add(new MockMold { MoldNo = $"X-{i:000}", Name = "測試" });
        await _db.Context.SaveChangesAsync();

        var page = await Light("X-", size: 100);

        page.Items.Should().HaveCount(50);
        page.Total.Should().Be(60);
    }
}
