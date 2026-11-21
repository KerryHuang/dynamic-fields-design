using DynamicModules.Application;
using DynamicModules.Application.Common.Security;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Persistence;
using DynamicModules.Persistence.Repositories.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Common;

/// <summary>
/// <c>AddChannelPermissionMasking</c> 的註冊（mcp1a 計畫判斷 10 第 2 點，第三輪必修 1）：包在 provider 註冊的實作外面、lifetime 不變、
/// 不遞迴解析自己。寫法照 <see cref="ActingScopeTests"/> 的 AddActingOverrides 案例。正式組裝另由 WebAPITests 的 McpPermissionDiTests 經 Program.cs 驗。
/// </summary>
public class ChannelPermissionDecorationTests
{
    private static ServiceCollection Services()
    {
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddPersistenceRepositories();
        return services;
    }

    [Fact]
    public void KeepsLifetime()
    {
        var services = Services();
        var before = services.Last(d => d.ServiceType == typeof(IPermissionRepository)).Lifetime;
        services.AddChannelPermissionMasking();
        before.Should().Be(ServiceLifetime.Scoped);
        services.Last(d => d.ServiceType == typeof(IPermissionRepository)).Lifetime.Should().Be(before);
    }

    [Fact]
    public void WrapsLastRegistration()
    {
        var services = Services();
        services.AddScoped<IRequestChannel, DefaultRequestChannel>();
        using var db = new DynamicDbContext(new DbContextOptionsBuilder<DynamicDbContext>().UseSqlite("DataSource=:memory:").Options);
        services.AddSingleton(db);
        services.AddChannelPermissionMasking();
        services.Count(d => d.ServiceType == typeof(IPermissionRepository)).Should().Be(1);

        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var resolved = scope.ServiceProvider.GetRequiredService<IPermissionRepository>();
        resolved.Should().BeOfType<ChannelPermissionRepository>();
        ((ChannelPermissionRepository)resolved).Inner.Should().BeOfType<PermissionRepository>();
    }

    [Fact]
    public void NoRecursion()
    {
        var probe = new ServiceCollection();
        probe.AddScoped<IRequestChannel, DefaultRequestChannel>();
        probe.AddScoped<IPermissionRepository, FakeRepository>();
        probe.AddChannelPermissionMasking();
        using var sp = probe.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        for (var i = 0; i < 2; i++)
        {
            using var scope = sp.CreateScope();
            scope.ServiceProvider.GetRequiredService<IPermissionRepository>().Should().BeOfType<ChannelPermissionRepository>();
        }
    }

    [Fact]
    public void ThrowsWhenMissing()
    {
        var act = () => new ServiceCollection().AddChannelPermissionMasking();
        act.Should().Throw<InvalidOperationException>().WithMessage("*AddChannelPermissionMasking*IPermissionRepository*");
    }

    [Fact]
    public async Task McpChannel_MarksInputs_UiDoesNot()
    {
        var inner = new FakeRepository();
        var mcp = new ChannelPermissionRepository(inner, new Dynamic.Mcp.TestRequestChannel { Channel = RequestChannel.Mcp });
        var ui = new ChannelPermissionRepository(inner, new DefaultRequestChannel());
        var module = new Domain.Entities.Dynamic.Module();
        (await mcp.LoadInputsAsync("u", module, CancellationToken.None))!.AiMasked.Should().BeTrue();
        (await ui.LoadInputsAsync("u", module, CancellationToken.None))!.AiMasked.Should().BeFalse();
        (await mcp.LoadProgramInputsAsync("u", "DYN00000", CancellationToken.None))!.AiMasked.Should().BeFalse("程式層動作軸不涉欄位");
        (await mcp.LoadInputsAsync("missing", module, CancellationToken.None)).Should().BeNull();
    }

    private sealed class FakeRepository : IPermissionRepository
    {
        private static PermissionInputs? Of(string userId) => userId == "missing" ? null
            : new PermissionInputs(new Domain.Entities.Dynamic.AppUser { UserId = userId }, [], [], null, [], [], [], new HashSet<string>());
        public Task<PermissionInputs?> LoadInputsAsync(string userId, Domain.Entities.Dynamic.Module module, CancellationToken ct) => Task.FromResult(Of(userId));
        public Task<PermissionInputs?> LoadProgramInputsAsync(string userId, string programCode, CancellationToken ct) => Task.FromResult(Of(userId));
        public Task<List<Domain.Entities.Dynamic.PermissionGroup>> ListGroupsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<List<Domain.Entities.Dynamic.UserGroupMapping>> ListMembershipsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<List<Domain.Entities.Dynamic.AppUser>> ListUsersAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<Domain.Entities.Dynamic.AppUser?> GetUserAsync(string userId, CancellationToken ct) => throw new NotSupportedException();
        public Task<ModulePermissionRows> LoadModuleRowsAsync(Domain.Entities.Dynamic.Module module, CancellationToken ct) => throw new NotSupportedException();
        public Task ReplaceModuleRowsAsync(Domain.Entities.Dynamic.Module module, ModulePermissionRows rows, CancellationToken ct) => throw new NotSupportedException();
        public Task<SystemPrivilegeRows> LoadSystemPrivilegeRowsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task ReplaceSystemRowsAsync(long? groupId, string? userId, IReadOnlyList<Domain.Entities.Dynamic.ProgramPrivilege> desired, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
