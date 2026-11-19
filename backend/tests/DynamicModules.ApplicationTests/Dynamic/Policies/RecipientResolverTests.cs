using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.ApplicationTests.Dynamic.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DynamicModules.ApplicationTests.Dynamic.Policies;

/// <summary>
/// 收件人解析（RecipientResolver）：政策項目引用已停用或不存在的帳號、群組時，
/// 停用的略過且不記警告、不存在的略過並記 Warning、結果去重排序；不丟例外。
/// </summary>
[Trait("Category", "Postgres")]
public class RecipientResolverTests : ApprovalTestBase
{
    private sealed class CaptureLogger<T>(List<(LogLevel Level, string Message)> sink) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            sink.Add((logLevel, formatter(state, exception)));
    }

    private readonly List<(LogLevel Level, string Message)> _logs = [];

    protected override void ConfigureServices(IServiceCollection services) =>
        services.AddSingleton<ILogger<RecipientResolver>>(new CaptureLogger<RecipientResolver>(_logs));

    private async Task Disable(string userId)
    {
        (await Db.Context.Set<AppUser>().SingleAsync(u => u.UserId == userId)).IsActive = false;
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
    }

    private async Task<IReadOnlyList<string>> Resolve(params string[] items)
    {
        using var scope = NewScope();
        return await scope.ServiceProvider.GetRequiredService<RecipientResolver>().ResolveAsync(items, CancellationToken.None);
    }

    private IEnumerable<string> Warnings => _logs.Where(l => l.Level == LogLevel.Warning).Select(l => l.Message);

    [Fact]
    public async Task User_Active_Resolved()
    {
        (await Resolve("user:fin1")).Should().Equal("fin1");
        Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task User_Disabled_SkippedWithoutWarning()
    {
        await Disable("fin1");
        (await Resolve("user:fin1")).Should().BeEmpty();
        Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task User_Nonexistent_SkippedWithWarning()
    {
        (await Resolve("user:nobody")).Should().BeEmpty();
        Warnings.Should().ContainSingle().Which.Should().Contain("user:nobody");
    }

    [Fact]
    public async Task User_DisabledAndNonexistentMixedWithActive_OnlyActiveReturned()
    {
        await Disable("fin1");
        (await Resolve("user:fin1", "user:nobody", "user:fin2")).Should().Equal("fin2");
        Warnings.Should().ContainSingle().Which.Should().Contain("user:nobody");
    }

    [Fact]
    public async Task Group_DisabledMemberSkipped()
    {
        await Disable("fin1");
        (await Resolve($"group:{FinanceGroup}")).Should().Equal("fin2");
        Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task Group_AllMembersDisabled_ReturnsEmpty()
    {
        await Disable("gm");
        (await Resolve($"group:{BoardGroup}")).Should().BeEmpty();
    }

    [Fact]
    public async Task Group_Nonexistent_SkippedWithWarning()
    {
        (await Resolve("group:99999")).Should().BeEmpty();
        Warnings.Should().ContainSingle().Which.Should().Contain("group:99999");
    }

    [Fact]
    public async Task Admins_DisabledAdminSkipped()
    {
        Db.Context.Add(new AppUser { UserId = "admin2", DisplayName = "admin2", IsSystemAdmin = true });
        await Db.Context.SaveChangesAsync();
        await Disable("admin2");
        var ids = await Resolve("admins");
        ids.Should().Contain("admin").And.NotContain("admin2");
    }

    [Fact]
    public async Task MixedItems_DeduplicatedAndSorted()
    {
        var ids = await Resolve("user:fin2", $"group:{FinanceGroup}", "user:fin1", "group:99999", "user:nobody");
        ids.Should().Equal("fin1", "fin2");
        Warnings.Should().HaveCount(2);
    }

    [Fact]
    public async Task EmptyOrUnknownPrefixItems_Ignored()
    {
        (await Resolve()).Should().BeEmpty();
        (await Resolve("role:x", "group:abc")).Should().BeEmpty();
    }
}
