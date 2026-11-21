using DynamicModules.Application.Dynamic.McpService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic.Mcp;

/// <summary>MCP 政策快照（mcp1a 計畫判斷 6）：存取類壞值 fail closed、允許對象展開、預設效期夾到最長效期、不快取。</summary>
public class McpPoliciesTests : DesignerTestBase
{
    private async Task SetSystem(PolicyDefinition def, string? value)
    {
        var row = await Db.Context.Set<SystemSetting>().FirstOrDefaultAsync(s => s.Key == def.SettingKey);
        if (row is not null) Db.Context.Remove(row);
        if (value is not null) Db.Context.Add(new SystemSetting { Key = def.SettingKey, Value = value });
        await Db.Context.SaveChangesAsync();
    }

    /// <summary>每次讀取一個新 scope，等同一個新請求（政策讀取在 scope 內快取）。</summary>
    private async Task<(McpPolicySnapshot Snap, string? Denial)> ReadAsync(string userId = "admin")
    {
        using var scope = NewScope();
        var policies = scope.ServiceProvider.GetRequiredService<McpPolicies>();
        var snap = await policies.ReadAsync(default);
        return (snap, await policies.AccessDenialAsync(snap, userId, default));
    }

    [Fact]
    public async Task Factory_DisabledByDefault()
    {
        var (snap, denial) = await ReadAsync();
        snap.Enabled.Should().BeFalse();
        snap.AccessInvalid.Should().BeFalse();
        (snap.TokenMaxLifetimeDays, snap.TokenDefaultLifetimeDays, snap.TokenMaxPerUser).Should().Be((90, 30, 10));
        (snap.CallsPerMinute, snap.CallsPerDay, snap.MaxRows, snap.ConfirmTtlSeconds).Should().Be((60, 5000, 50, 300));
        denial.Should().Be(McpMessages.CodeDisabled);
    }

    [Fact]
    public async Task DefaultLifetime_ClampedToMax_AndNotCached()
    {
        await SetSystem(PolicyCatalog.McpTokenDefaultLifetimeDays, "40");
        await SetSystem(PolicyCatalog.McpTokenMaxLifetimeDays, "30");
        (await ReadAsync()).Snap.TokenDefaultLifetimeDays.Should().Be(30);

        await SetSystem(PolicyCatalog.McpTokenMaxLifetimeDays, "60");
        (await ReadAsync()).Snap.TokenDefaultLifetimeDays.Should().Be(40);
    }

    [Fact]
    public async Task AllowedGroups_Garbage_AccessInvalid()
    {
        await SetSystem(PolicyCatalog.McpEnabled, "true");
        await SetSystem(PolicyCatalog.McpAllowedGroups, "everyone!");
        var (snap, denial) = await ReadAsync();
        snap.AccessInvalid.Should().BeTrue();
        denial.Should().Be(McpMessages.CodePolicyInvalid);
    }

    [Fact]
    public async Task Enabled_Garbage_AccessInvalid()
    {
        await SetSystem(PolicyCatalog.McpEnabled, "yes");
        var (snap, denial) = await ReadAsync();
        snap.AccessInvalid.Should().BeTrue();
        denial.Should().Be(McpMessages.CodePolicyInvalid);
    }

    [Fact]
    public async Task IntegerGarbage_FallsBackToFactory_AccessStillValid()
    {
        await SetSystem(PolicyCatalog.McpEnabled, "true");
        await SetSystem(PolicyCatalog.McpMaxRows, "999");
        var (snap, denial) = await ReadAsync();
        snap.MaxRows.Should().Be(50);
        snap.AccessInvalid.Should().BeFalse();
        denial.Should().BeNull();
    }

    [Fact]
    public async Task AllowedGroups_ValidEmpty_Everyone()
    {
        await SetSystem(PolicyCatalog.McpEnabled, "true");
        await SetSystem(PolicyCatalog.McpAllowedGroups, "");
        (await ReadAsync("admin")).Denial.Should().BeNull();
        (await ReadAsync("sales01")).Denial.Should().BeNull();
    }

    [Fact]
    public async Task AllowedGroups_DeletedGroupOnly_Nobody()
    {
        await SetSystem(PolicyCatalog.McpEnabled, "true");
        await SetSystem(PolicyCatalog.McpAllowedGroups, "group:999999");
        (await ReadAsync("admin")).Denial.Should().Be(McpMessages.CodeNotAllowed);
    }

    [Fact]
    public async Task AllowedGroups_UserItem_OnlyThatUser()
    {
        await SetSystem(PolicyCatalog.McpEnabled, "true");
        await SetSystem(PolicyCatalog.McpAllowedGroups, "user:sales01");
        (await ReadAsync("sales01")).Denial.Should().BeNull();
        (await ReadAsync("admin")).Denial.Should().Be(McpMessages.CodeNotAllowed);
    }
}
