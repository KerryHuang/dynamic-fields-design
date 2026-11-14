using DynamicModules.Application;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.EntityService.Queries.GetEntityLight;
using DynamicModules.Application.Dynamic.EntityService.Queries.ListEntities;
using DynamicModules.Application.Dynamic.References;
using DynamicModules.ApplicationTests.Persistence;
using DynamicModules.Persistence;
using DynamicModules.Persistence.Seed;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>產品 DI（AddApplication、AddPersistenceRepositories）本身不註冊任何參照提供者；不加測試實體時，既有實體清單是空的。</summary>
public class EntityServiceNoProviderTests : IAsyncLifetime
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

    [Fact]
    public async Task ListEntities_WithNoProviderRegistered_IsEmpty()
    {
        _sp.GetRequiredService<ReferenceProviderRegistry>().All.Should().BeEmpty();

        var r = await _sp.GetRequiredService<IMediator>().Send(new ListEntitiesRequest());

        r.IsSuccess.Should().BeTrue();
        ((System.Collections.IEnumerable)r.Data!).Cast<object>().Should().BeEmpty();
    }

    [Fact]
    public async Task LightList_OfAnyEntity_IsNotFound_WhenNoProviderIsRegistered()
    {
        var r = await _sp.GetRequiredService<IMediator>().Send(new GetEntityLightRequest("test.entity", null, 1, 20));

        r.IsNotFound.Should().BeTrue();
    }
}
