using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic.Policies;

/// <summary>
/// 系統 PUT 與全系統政策快照（p3 計畫 Task 3、審查必修 1）：經 MediatR 含 TransactionBehavior；hook 收到的新舊值、
/// 提交後才更新快照、回滾時設定與快照都不變。SQLite 與 PostgreSQL 雙跑。
/// </summary>
[Trait("Category", "Postgres")]
public class SystemPolicyCacheTests : DesignerTestBase
{
    private sealed class RecordingHook : IPolicyChangeHook
    {
        public readonly List<(string Code, string Old, string New)> Calls = [];
        public bool Reject;

        public Task<ServiceResult?> BeforeCommitAsync(PolicyDefinition def, string oldEffective, string newEffective, CancellationToken ct)
        {
            Calls.Add((def.Code, oldEffective, newEffective));
            return Task.FromResult(Reject ? ServiceResult.Invalid(new Dictionary<string, string[]> { ["value"] = ["test"] }) : null);
        }
    }

    private readonly RecordingHook _hook = new();
    private static readonly PolicyDefinition Tz = PolicyCatalog.LocaleTimeZone;

    protected override bool ScopePerRequest => true;

    protected override void ConfigureServices(IServiceCollection services) => services.AddSingleton<IPolicyChangeHook>(_hook);

    private CachedSystemPolicy Cache => Svc<CachedSystemPolicy>();

    private Task<ServiceResult> Put(string? value) =>
        Send(new UpdateSystemPolicyRequest(Tz.Code, value is null ? null : JsonSerializer.SerializeToElement(value)));

    private async Task<string?> StoredAsync() =>
        (await Db.Context.Set<SystemSetting>().AsNoTracking().SingleOrDefaultAsync(s => s.Key == Tz.SettingKey))?.Value;

    [Fact]
    public async Task Put_UpdatesCacheAfterCommit()
    {
        (await Put("+07:00")).IsSuccess.Should().BeTrue();
        Cache.BusinessOffset.Should().Be(TimeSpan.FromHours(7));
        Cache.Get(Tz).Should().Be("+07:00");
    }

    [Fact]
    public async Task Put_RolledBack_CacheUnchanged()
    {
        _hook.Reject = true;
        var r = await Put("+07:00");
        r.IsSuccess.Should().BeFalse();
        r.Errors["value"][0].Should().Be("test");
        (await StoredAsync()).Should().BeNull();
        Cache.BusinessOffset.Should().Be(TimeSpan.FromHours(8));
    }

    [Fact]
    public async Task Put_SameValue_HooksNotCalled()
    {
        (await Put("+08:00")).IsSuccess.Should().BeTrue();
        _hook.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Put_FirstSet_HookSeesNewValue()
    {
        (await Put("+07:00")).IsSuccess.Should().BeTrue();
        _hook.Calls.Should().Equal((Tz.Code, "+08:00", "+07:00"));
        Cache.BusinessOffset.Should().Be(TimeSpan.FromHours(7));
    }

    [Fact]
    public async Task Put_Modify_HookSeesNewValue()
    {
        (await Put("+07:00")).IsSuccess.Should().BeTrue();
        (await Put("+09:00")).IsSuccess.Should().BeTrue();
        _hook.Calls.Last().Should().Be((Tz.Code, "+07:00", "+09:00"));
        Cache.BusinessOffset.Should().Be(TimeSpan.FromHours(9));
    }

    [Fact]
    public async Task Put_ResetToFactory_HookSeesFactory()
    {
        (await Put("+07:00")).IsSuccess.Should().BeTrue();
        (await Put(null)).IsSuccess.Should().BeTrue();
        _hook.Calls.Last().Should().Be((Tz.Code, "+07:00", "+08:00"));
        (await StoredAsync()).Should().BeNull();
        Cache.BusinessOffset.Should().Be(TimeSpan.FromHours(8));
    }

    [Fact]
    public async Task Put_HookRejects_EverythingRolledBack()
    {
        (await Put("+07:00")).IsSuccess.Should().BeTrue();
        _hook.Reject = true;
        var r = await Put("+09:00");
        r.IsSuccess.Should().BeFalse();
        _hook.Calls.Last().Should().Be((Tz.Code, "+07:00", "+09:00"));
        (await StoredAsync()).Should().Be("+07:00");
        // 回滾時 policy-cache callback 不執行，快照仍是上一次提交的值
        Cache.BusinessOffset.Should().Be(TimeSpan.FromHours(7));
    }
}
