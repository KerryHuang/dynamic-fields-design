using DynamicModules.Application;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.ApplicationTests.Persistence;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Persistence;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Common.Audit;

/// <summary>
/// AuditBehavior 的巢狀與失敗語意（維運設計 §17.2.1、§17.11 偏離 1）：只有最外層的稽核請求寫一列、內層補的目標與摘要被忽略；
/// 不成功（驗證、權限、衝突）不寫；外層不是稽核請求時內層各寫一列。走真的 MediatR 管線與交易，雙跑。
/// </summary>
[Trait("Category", "Postgres")]
public class AuditBehaviorTests : IAsyncLifetime
{
    private TestDb _db = null!;
    private ServiceProvider _sp = null!;

    private sealed class FakeUser : ICurrentUser { public string UserId => "admin"; }

    private sealed class FakeTime : IBusinessTime
    {
        public DateTime Now => new(2026, 11, 19, 9, 0, 0);
        public DateOnly Today => DateOnly.FromDateTime(Now);
    }

    public sealed record Outer(bool InnerSucceeds = true, bool Succeeds = true) : IRequest<ServiceResult>, ITransactionalRequest, IAuditedRequest
    {
        public AuditAction Audit => AuditAction.For("module.update", AuditTargets.Module, "outer");
    }

    public sealed record Inner(bool Succeeds = true, string Id = "inner") : IRequest<ServiceResult>, ITransactionalRequest, IAuditedRequest
    {
        public AuditAction Audit => AuditAction.For("field.add", AuditTargets.Module, Id);
    }

    /// <summary>不是稽核請求的外層（像 Demo 資料產生器）。</summary>
    public sealed record Plain : IRequest<ServiceResult>, ITransactionalRequest;

    private static ServiceResult Fail() => ServiceResult.Invalid(new Dictionary<string, string[]> { ["x"] = ["不行"] });

    public sealed class OuterHandler(ISender sender, AuditContext audit) : IRequestHandler<Outer, ServiceResult>
    {
        public async Task<ServiceResult> Handle(Outer request, CancellationToken ct)
        {
            audit.Target(label: "外層");
            await sender.Send(new Inner(request.InnerSucceeds), ct);
            audit.Detail("outer", true);
            return request.Succeeds ? ServiceResult.Success() : Fail();
        }
    }

    public sealed class InnerHandler(AuditContext audit) : IRequestHandler<Inner, ServiceResult>
    {
        public Task<ServiceResult> Handle(Inner request, CancellationToken ct)
        {
            audit.Target(request.Id + "-new", "內層");
            audit.Describe(new { V = 1 }, new { V = 2 });
            return Task.FromResult(request.Succeeds ? ServiceResult.Success() : Fail());
        }
    }

    public sealed class PlainHandler(ISender sender) : IRequestHandler<Plain, ServiceResult>
    {
        public async Task<ServiceResult> Handle(Plain request, CancellationToken ct)
        {
            await sender.Send(new Inner(Id: "a"), ct);
            await sender.Send(new Inner(Id: "b"), ct);
            return ServiceResult.Success();
        }
    }

    public async Task InitializeAsync()
    {
        _db = await TestDb.CreateAsync();
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddPersistenceRepositories();
        TestDb.AddProviderServices(services);
        var options = (DbContextOptions<DynamicDbContext>)_db.Context.GetService<IDbContextOptions>();
        services.AddScoped(_ => new DynamicDbContext(options));
        services.AddSingleton<ICurrentUser, FakeUser>();
        services.AddSingleton<IBusinessTime, FakeTime>();
        services.AddScoped<IWorkSavepoints, EfWorkSavepoints>();
        services.AddSingleton(new AuthSettings(false));
        services.AddTransient<IRequestHandler<Outer, ServiceResult>, OuterHandler>();
        services.AddTransient<IRequestHandler<Inner, ServiceResult>, InnerHandler>();
        services.AddTransient<IRequestHandler<Plain, ServiceResult>, PlainHandler>();
        _sp = services.BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        await _sp.DisposeAsync();
        await _db.DisposeAsync();
    }

    private async Task<ServiceResult> Send(IRequest<ServiceResult> request)
    {
        using var scope = _sp.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(request);
    }

    private async Task<List<AuditLogEntry>> Rows()
    {
        using var scope = _sp.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<AuditLogEntry>().AsNoTracking().OrderBy(a => a.Id).ToListAsync();
    }

    [Fact]
    public async Task NestedSuccess_WritesOnlyTheOuterRow_InnerTargetAndSummaryIgnored()
    {
        (await Send(new Outer())).IsSuccess.Should().BeTrue();
        var row = (await Rows()).Should().ContainSingle().Subject;
        (row.Action, row.TargetId, row.TargetLabel, row.ActorUserId).Should().Be(("module.update", "outer", "外層", "admin"));
        row.SummaryJson.Should().Be("{\"outer\":true}");
    }

    [Fact]
    public async Task InnerFails_OuterSucceeds_WritesTheOuterRow()
    {
        (await Send(new Outer(InnerSucceeds: false))).IsSuccess.Should().BeTrue();
        (await Rows()).Select(r => r.Action).Should().Equal("module.update");
    }

    [Fact]
    public async Task OuterFails_WritesNothing()
    {
        (await Send(new Outer(Succeeds: false))).IsSuccess.Should().BeFalse();
        (await Send(new Inner(Succeeds: false))).IsSuccess.Should().BeFalse();
        (await Rows()).Should().BeEmpty();
    }

    [Fact]
    public async Task UnauditedOuter_EachInnerWritesItsOwnRow_ContextResetBetween()
    {
        (await Send(new Plain())).IsSuccess.Should().BeTrue();
        (await Rows()).Select(r => (r.Action, r.TargetId, r.TargetLabel)).Should().Equal(("field.add", "a-new", "內層"), ("field.add", "b-new", "內層"));
    }
}
