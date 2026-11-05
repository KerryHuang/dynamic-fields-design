using DynamicModules.Application;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Acting;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddField;
using DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;
using DynamicModules.Application.Dynamic.ModuleService.Commands.CreateModule;
using DynamicModules.Application.Dynamic.ModuleService.Commands.UpdateModule;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.ModuleService.Queries.GetModule;
using DynamicModules.Application.Dynamic.PermissionService.Commands.SaveModulePermissions;
using DynamicModules.Application.Dynamic.PermissionService.Data;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.ApplicationTests.Persistence;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using DynamicModules.Persistence;
using DynamicModules.Persistence.Seed;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Common;

public class ActingScopeTests
{
    private static readonly DateTime InnerNow = new(2026, 11, 5, 10, 0, 0);

    private sealed class FakeUser : ICurrentUser { public string UserId => "admin"; }

    private sealed class FakeTime : IBusinessTime
    {
        public DateTime Now => InnerNow;
        public DateOnly Today => DateOnly.FromDateTime(Now);
    }

    private readonly ActingCurrentUser _user = new(new FakeUser());
    private readonly ActingBusinessTime _time = new(new FakeTime());

    [Fact]
    public void Begin_OverridesUserAndTime()
    {
        var at = new DateTime(2026, 8, 15, 10, 30, 0);

        using (ActingScope.Begin("sales01", at))
        {
            _user.UserId.Should().Be("sales01");
            _time.Now.Should().Be(at);
            _time.Today.Should().Be(new DateOnly(2026, 8, 15));
        }
    }

    [Fact]
    public void Dispose_RestoresOuter()
    {
        var t1 = new DateTime(2026, 8, 1, 9, 0, 0);

        using (ActingScope.Begin("a", t1))
        {
            using (ActingScope.Begin("b"))
            {
                _user.UserId.Should().Be("b");
                _time.Now.Should().Be(t1, "內層沒給時間就沿用外層的覆寫時間");
            }
            _user.UserId.Should().Be("a");
            _time.Now.Should().Be(t1);
        }

        _user.UserId.Should().Be("admin");
        _time.Now.Should().Be(InnerNow);
        ActingScope.UserId.Should().BeNull();
        ActingScope.Now.Should().BeNull();
    }

    [Fact]
    public void NoScope_FallsBackToInner()
    {
        _user.UserId.Should().Be("admin");
        _time.Now.Should().Be(InnerNow);
        _time.Today.Should().Be(new DateOnly(2026, 11, 5));
    }

    [Fact]
    public void BeginWithoutTime_NoOuter_UsesInnerTime()
    {
        using (ActingScope.Begin("sales01"))
        {
            _user.UserId.Should().Be("sales01");
            _time.Now.Should().Be(InnerNow);
        }
    }

    [Fact]
    public async Task ParallelFlows_AreIsolated()
    {
        async Task<(string User, DateTime Now)> Flow(string user, DateTime at)
        {
            using (ActingScope.Begin(user, at))
            {
                for (var i = 0; i < 20; i++) await Task.Yield();
                return (_user.UserId, _time.Now);
            }
        }

        var t1 = new DateTime(2026, 7, 1, 8, 0, 0);
        var t2 = new DateTime(2026, 9, 1, 17, 0, 0);
        var results = await Task.WhenAll(Task.Run(() => Flow("sales01", t1)), Task.Run(() => Flow("sales02", t2)));

        results[0].Should().Be(("sales01", t1));
        results[1].Should().Be(("sales02", t2));
        _user.UserId.Should().Be("admin");
        _time.Now.Should().Be(InnerNow);
    }

    [Fact]
    public void AddActingOverrides_KeepsLifetime()
    {
        var services = new ServiceCollection();
        services.AddScoped<ICurrentUser, FakeUser>();
        services.AddSingleton<IBusinessTime>(new FakeTime());
        services.AddActingOverrides();
        using var sp = services.BuildServiceProvider();

        using var s1 = sp.CreateScope();
        using var s2 = sp.CreateScope();
        var u1 = s1.ServiceProvider.GetRequiredService<ICurrentUser>();
        u1.Should().BeOfType<ActingCurrentUser>();
        u1.Should().BeSameAs(s1.ServiceProvider.GetRequiredService<ICurrentUser>());
        u1.Should().NotBeSameAs(s2.ServiceProvider.GetRequiredService<ICurrentUser>());
        u1.UserId.Should().Be("admin");

        var b1 = s1.ServiceProvider.GetRequiredService<IBusinessTime>();
        b1.Should().BeOfType<ActingBusinessTime>();
        b1.Should().BeSameAs(s2.ServiceProvider.GetRequiredService<IBusinessTime>());
        b1.Now.Should().Be(InnerNow);
    }

    [Fact]
    public void AddActingOverrides_FactoryDescriptor_Wrapped()
    {
        var services = new ServiceCollection();
        services.AddTransient<ICurrentUser>(_ => new FakeUser());
        services.AddSingleton<IBusinessTime, FakeTime>();
        services.AddActingOverrides();
        using var sp = services.BuildServiceProvider();

        using (ActingScope.Begin("sales01"))
        {
            sp.GetRequiredService<ICurrentUser>().UserId.Should().Be("sales01");
        }
        sp.GetRequiredService<ICurrentUser>().UserId.Should().Be("admin");
        sp.GetRequiredService<ICurrentUser>().Should().NotBeSameAs(sp.GetRequiredService<ICurrentUser>(), "transient 維持 transient");
        sp.GetRequiredService<IBusinessTime>().Now.Should().Be(InnerNow);
    }

    [Fact]
    public void AddActingOverrides_MissingRegistration_Throws()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IBusinessTime, FakeTime>();

        var act = () => services.AddActingOverrides();

        act.Should().Throw<InvalidOperationException>().WithMessage("*ICurrentUser*");
    }

    [Fact]
    public async Task CreateRecord_InScope_UsesActingUserAndSimulatedTime()
    {
        await using var db = await SqliteTestDb.CreateAsync();
        await SeedData.EnsureSeededAsync(db.Context);
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddPersistenceRepositories();
        services.AddScoped(_ => db.Context);
        services.AddScoped<ITransactionManager, EfTransactionManager>();
        services.AddSingleton<ICurrentUser>(new FakeUser());
        services.AddSingleton<IBusinessTime, FakeTime>();
        services.AddActingOverrides();
        await using var sp = services.BuildServiceProvider();
        Task<ServiceResult> Send(IRequest<ServiceResult> request) => sp.GetRequiredService<IMediator>().Send(request);
        static void Ok(ServiceResult r) =>
            (r.IsSuccess || r.IsCreated).Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));

        var name = new I18nText([KeyValuePair.Create("zh-TW", "工單")]);
        Ok(await Send(new CreateModuleRequest("ticket", name, null, null, CreateTitleField: false)));
        var block = ((ModuleDetailResponse)(await Send(new GetModuleRequest("ticket"))).Data!).Blocks[0].Id;
        var add = await Send(new AddFieldRequest("ticket", block, FieldType.AutoNumber,
            new I18nText([KeyValuePair.Create("zh-TW", "單號")]), new FieldConfig { Format = "T-{yyyyMM}-{0000}" }));
        Ok(add);
        var no = (FieldResponse)add.Data!;
        Ok(await Send(new UpdateModuleRequest("ticket", name, null, null, no.Id)));
        Ok(await Send(new ActivateModuleRequest("ticket")));
        var sales = await db.Context.Set<PermissionGroup>().AsNoTracking().SingleAsync(g => g.Key == "sales");
        Ok(await Send(new SaveModulePermissionsRequest("ticket", new ModulePermissionsDto(
            [new GroupPermissionDto(sales.Id, new PrivilegesDto(true, true, false, false, false, false, false), null)], [], [], []))));

        var at = new DateTime(2026, 8, 15, 10, 30, 0);
        ServiceResult created;
        using (ActingScope.Begin("sales01", at))
        {
            created = await Send(new CreateRecordRequest("ticket", []));
        }

        Ok(created);
        var rec = (DynamicRecordResponse)created.Data!;
        rec.OwnerUserId.Should().Be("sales01");
        rec.CreatedUserId.Should().Be("sales01");
        rec.CreatedTime.Should().Be(at);
        rec.Title.Should().Be("T-202608-0001");
        var history = await db.Context.Set<RecordHistory>().AsNoTracking()
            .SingleAsync(h => h.RecordId == rec.Id && h.Operation == RecordHistoryOperation.Create);
        history.ChangedUserId.Should().Be("sales01");
        history.ChangedTime.Should().Be(at);
    }
}
