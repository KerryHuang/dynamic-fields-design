using DynamicModules.Application;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Behaviors;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.ModuleService.Commands.CreateModule;
using DynamicModules.Application.Dynamic.ModuleService.Commands.UpdateModule;
using DynamicModules.Application.Dynamic.ModuleService.Queries.GetModule;
using DynamicModules.ApplicationTests.Persistence;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using DynamicModules.Persistence;
using DynamicModules.Persistence.Seed;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Common;

public class DesignerAccessBehaviorTests : IAsyncLifetime
{
    private SqliteTestDb _db = null!;
    private ServiceProvider _sp = null!;
    private readonly FakeUser _user = new();

    private sealed class FakeUser : ICurrentUser { public string UserId { get; set; } = "admin"; }

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
        services.AddScoped(_ => _db.Context);
        services.AddScoped<ITransactionManager, EfTransactionManager>();
        services.AddSingleton<ICurrentUser>(_user);
        services.AddSingleton<IBusinessTime, FakeTime>();
        _sp = services.BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        await _sp.DisposeAsync();
        await _db.DisposeAsync();
    }

    private Task<TRes> Send<TRes>(IRequest<TRes> request) => _sp.GetRequiredService<IMediator>().Send(request);

    private static I18nText Name() => new([KeyValuePair.Create("zh-TW", "訂單")]);

    private async Task GrantDesigner(string groupKey, bool read, bool add, bool edit)
    {
        var group = await _db.Context.Set<PermissionGroup>().SingleAsync(g => g.Key == groupKey);
        _db.Context.Set<GroupProgramPrivilege>().Add(new GroupProgramPrivilege
        {
            GroupId = group.Id, ProgramCode = "DYN00000", CanRead = read, CanAdd = add, CanEdit = edit
        });
        await _db.Context.SaveChangesAsync();
    }

    [Fact]
    public async Task NonAdminWithoutDesignerRead_Forbidden()
    {
        await Send(new CreateModuleRequest("order", Name(), null, null));
        _user.UserId = "sales01";

        var r = await Send(new GetModuleRequest("order"));

        r.IsForbidden.Should().BeTrue();
        r.Message.Should().Be("沒有權限執行此操作");
    }

    [Fact]
    public async Task GroupGrantedDesignerEdit_Allows()
    {
        await Send(new CreateModuleRequest("order", Name(), null, null));
        await GrantDesigner("sales", read: true, add: false, edit: true);
        _user.UserId = "sales01";

        (await Send(new UpdateModuleRequest("order", Name(), null, null, null))).IsForbidden.Should().BeFalse();
        (await Send(new CreateModuleRequest("other", Name(), null, null))).IsForbidden.Should().BeTrue();
    }

    [Fact]
    public async Task Admin_AllowsAll()
    {
        (await Send(new CreateModuleRequest("order", Name(), null, null))).IsSuccess.Should().BeTrue();
        (await Send(new GetModuleRequest("order"))).IsSuccess.Should().BeTrue();
        (await Send(new UpdateModuleRequest("order", Name(), null, null, null))).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task UnknownUser_Forbidden()
    {
        _user.UserId = "";
        (await Send(new CreateModuleRequest("order", Name(), null, null))).IsForbidden.Should().BeTrue();
    }

    private sealed record NonServiceResultDesignerRequest : IRequest<string>, IDesignerRequest
    {
        public PermissionAction RequiredDesignerAction => PermissionAction.Read;
    }

    [Fact]
    public async Task DesignerRequestWithoutServiceResult_ThrowsInsteadOfPassing()
    {
        var behavior = new DesignerAccessBehavior<NonServiceResultDesignerRequest, string>(
            _sp.GetRequiredService<IDynamicAccessGuard>());

        var act = () => behavior.Handle(new NonServiceResultDesignerRequest(), () => Task.FromResult("leaked"), default);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
