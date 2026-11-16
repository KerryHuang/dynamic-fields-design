using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Repositories.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DynamicModules.ApplicationTests.Dynamic.Policies;

public class PolicyReaderTests : DesignerTestBase
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
        services.AddSingleton<ILogger<PolicyReader>>(new CaptureLogger<PolicyReader>(_logs));

    private static readonly PolicyDefinition Def = PolicyCatalog.TaskMaxRulesPerModule;

    private IPolicyReader Reader => Svc<IPolicyReader>();

    private async Task<long> ModuleId(string key)
    {
        await NewModule(key);
        return (await Db.Context.Set<Module>().AsNoTracking().SingleAsync(m => m.Key == key)).Id;
    }

    private async Task SetSystem(string code, string? value)
    {
        var row = await Db.Context.Set<SystemSetting>().FirstOrDefaultAsync(s => s.Key == "policy." + code);
        if (row is not null) Db.Context.Remove(row);
        if (value is not null) Db.Context.Add(new SystemSetting { Key = "policy." + code, Value = value });
        await Db.Context.SaveChangesAsync();
        Reader.Invalidate();
    }

    private async Task SetOverride(long moduleId, string code, string? value)
    {
        var row = await Db.Context.Set<ModulePolicy>().FirstOrDefaultAsync(p => p.ModuleId == moduleId && p.Code == code);
        if (row is not null) Db.Context.Remove(row);
        if (value is not null)
            Db.Context.Add(new ModulePolicy { ModuleId = moduleId, Code = code, Value = value, ModifiedUserId = "admin" });
        await Db.Context.SaveChangesAsync();
        Reader.Invalidate();
    }

    [Fact]
    public async Task Factory_WhenNothingSet()
    {
        var a = await ModuleId("pa");
        (await Reader.IntAsync(Def, null, default)).Should().Be(20);
        (await Reader.IntAsync(Def, a, default)).Should().Be(20);
        (await Reader.ResolveAsync(Def, a, default)).Should().Be(new PolicyResolution("20", null, null, "20"));
    }

    [Fact]
    public async Task SystemValue_OverridesFactory()
    {
        var a = await ModuleId("pa");
        var b = await ModuleId("pb");
        await SetSystem(Def.Code, "5");
        (await Reader.IntAsync(Def, a, default)).Should().Be(5);
        (await Reader.IntAsync(Def, b, default)).Should().Be(5);
        (await Reader.IntAsync(Def, null, default)).Should().Be(5);
    }

    [Fact]
    public async Task ModuleOverride_WinsForThatModuleOnly()
    {
        var a = await ModuleId("pa");
        var b = await ModuleId("pb");
        await SetSystem(Def.Code, "5");
        await SetOverride(a, Def.Code, "50");
        (await Reader.IntAsync(Def, a, default)).Should().Be(50);
        (await Reader.IntAsync(Def, b, default)).Should().Be(5);
        (await Reader.IntAsync(Def, null, default)).Should().Be(5);
        (await Reader.ResolveAsync(Def, a, default)).Should().Be(new PolicyResolution("20", "5", "50", "50"));
    }

    [Fact]
    public async Task ChangingSystem_KeepsExistingOverride()
    {
        var a = await ModuleId("pa");
        var b = await ModuleId("pb");
        await SetSystem(Def.Code, "5");
        await SetOverride(a, Def.Code, "50");
        await SetSystem(Def.Code, "7");
        (await Reader.IntAsync(Def, a, default)).Should().Be(50);
        (await Reader.IntAsync(Def, b, default)).Should().Be(7);
        await SetOverride(a, Def.Code, null);
        (await Reader.IntAsync(Def, a, default)).Should().Be(7);
    }

    [Fact]
    public async Task BadStoredValues_FallBackOneLayer_AndWarn()
    {
        var a = await ModuleId("pa");
        await SetSystem(Def.Code, "abc");
        (await Reader.IntAsync(Def, null, default)).Should().Be(20);
        await SetSystem(Def.Code, "5");
        await SetOverride(a, Def.Code, "999");
        (await Reader.IntAsync(Def, a, default)).Should().Be(5);
        _logs.Where(l => l.Level == LogLevel.Warning && l.Message.Contains(Def.Code)).Should().HaveCount(2);
    }

    [Fact]
    public async Task SameScope_CachesUntilInvalidate()
    {
        (await Reader.IntAsync(Def, null, default)).Should().Be(20);
        Db.Context.Add(new SystemSetting { Key = Def.SettingKey, Value = "5" });
        await Db.Context.SaveChangesAsync();
        (await Reader.IntAsync(Def, null, default)).Should().Be(20);
        Reader.Invalidate();
        (await Reader.IntAsync(Def, null, default)).Should().Be(5);
    }

    [Fact]
    public async Task SystemScope_IgnoresOverrideRows()
    {
        var a = await ModuleId("pa");
        var sys = new PolicyDefinition("test.sys", "task", PolicyKind.Integer, PolicyScope.System, "3", 1, 9);
        await SetOverride(a, "test.sys", "8");
        (await Reader.IntAsync(sys, a, default)).Should().Be(3);
    }

    [Fact]
    public async Task ListAndBool_ParseCanonical()
    {
        var roles = new PolicyDefinition("test.roles", "task", PolicyKind.RoleSet, PolicyScope.Module, "", Options: ["assignee", "creator"]);
        (await Reader.ListAsync(roles, null, default)).Should().BeEmpty();
        await SetSystem("test.roles", "creator,assignee");
        (await Reader.ListAsync(roles, null, default)).Should().Equal("assignee", "creator");
        var flag = new PolicyDefinition("test.flag", "task", PolicyKind.Boolean, PolicyScope.Module, "true");
        (await Reader.BoolAsync(flag, null, default)).Should().BeTrue();
        var day = new PolicyDefinition("test.day", "locale", PolicyKind.Choice, PolicyScope.System, "monday", Options: ["monday", "sunday"]);
        (await Reader.ChoiceAsync(day, null, default)).Should().Be("monday");
    }

    [Fact]
    public async Task KindMismatch_Throws()
    {
        await Reader.Invoking(r => r.BoolAsync(Def, null, default)).Should().ThrowAsync<InvalidOperationException>();
        await Reader.Invoking(r => r.ListAsync(Def, null, default)).Should().ThrowAsync<InvalidOperationException>();
    }
}

/// <summary>部署設定檔提供的出廠值蓋過程式出廠值。</summary>
public class PolicyReaderEnvironmentTests : DesignerTestBase
{
    protected override void ConfigureServices(IServiceCollection services) =>
        services.AddSingleton(new PolicyEnvironmentDefaults(new Dictionary<string, string> { ["task.maxRulesPerModule"] = "9" }));

    [Fact]
    public async Task EnvironmentDefault_ReplacesFactory()
    {
        var reader = Svc<IPolicyReader>();
        var def = PolicyCatalog.TaskMaxRulesPerModule;
        (await reader.IntAsync(def, null, default)).Should().Be(9);
        (await reader.ResolveAsync(def, null, default)).Factory.Should().Be("9");
    }
}

/// <summary>部署出廠值不合法（非數字、超出技術上下限）時退回程式出廠值，不丟例外。</summary>
public class PolicyReaderEnvironmentInvalidTests : DesignerTestBase
{
    private sealed class CaptureLogger(List<(LogLevel Level, string Message)> sink) : ILogger<PolicyEnvironmentDefaults>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            sink.Add((logLevel, formatter(state, exception)));
    }

    private static readonly PolicyDefinition Def = PolicyCatalog.TaskMaxRulesPerModule;

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("101")]
    public void EnvironmentDefault_Invalid_FallsBackToCatalogFactory(string bad) =>
        new PolicyEnvironmentDefaults(new Dictionary<string, string> { [Def.Code] = bad }).FactoryOf(Def).Should().Be(Def.Factory);

    [Fact]
    public async Task EnvironmentDefault_Invalid_ReaderDoesNotThrow()
    {
        var env = new PolicyEnvironmentDefaults(new Dictionary<string, string> { [Def.Code] = "abc" });
        var reader = new PolicyReader(Svc<ISystemSettingRepository>(), Svc<IModulePolicyRepository>(), env,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PolicyReader>.Instance);
        (await reader.IntAsync(Def, null, default)).Should().Be(20);
        PolicyJson.System(Def, await reader.ResolveAsync(Def, null, default)).Should().NotBeNull();
    }

    [Fact]
    public void EnvironmentDefault_Invalid_WarnsOnce()
    {
        var logs = new List<(LogLevel Level, string Message)>();
        var env = new PolicyEnvironmentDefaults(new Dictionary<string, string> { [Def.Code] = "abc" }, new CaptureLogger(logs));
        env.FactoryOf(Def);
        env.FactoryOf(Def);
        logs.Should().ContainSingle(l => l.Level == LogLevel.Warning && l.Message.Contains(Def.Code));
    }

    [Fact]
    public void EnvironmentDefault_Valid_StillReplacesFactory() =>
        new PolicyEnvironmentDefaults(new Dictionary<string, string> { [Def.Code] = "9" }).FactoryOf(Def).Should().Be("9");
}

/// <summary>PUT 的「取消覆寫」：Value 為 JSON null／undefined 的 JsonElement 也要當成 null。</summary>
public class PolicyPutNullElementTests : DesignerTestBase
{
    private static readonly PolicyDefinition Def = PolicyCatalog.TaskMaxRulesPerModule;

    private static System.Text.Json.JsonElement Elem(string json) => System.Text.Json.JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task System_NullElement_RemovesSetting()
    {
        (await Send(new UpdateSystemPolicyRequest(Def.Code, Elem("10")))).IsSuccess.Should().BeTrue();
        var res = await Send(new UpdateSystemPolicyRequest(Def.Code, Elem("null")));
        res.IsSuccess.Should().BeTrue();
        (await Svc<IPolicyReader>().IntAsync(Def, null, default)).Should().Be(20);
        (await Send(new UpdateSystemPolicyRequest(Def.Code, default(System.Text.Json.JsonElement)))).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Module_NullElement_RemovesOverride()
    {
        await NewModule("pn");
        (await Send(new UpdateModulePolicyRequest("pn", Def.Code, Elem("3")))).IsSuccess.Should().BeTrue();
        (await Send(new UpdateModulePolicyRequest("pn", Def.Code, Elem("null")))).IsSuccess.Should().BeTrue();
        Db.Context.Set<ModulePolicy>().Count().Should().Be(0);

        (await Send(new UpdateModulePolicyRequest("pn", Def.Code, Elem("3")))).IsSuccess.Should().BeTrue();
        (await Send(new UpdateModulePolicyRequest("pn", Def.Code, default(System.Text.Json.JsonElement)))).IsSuccess.Should().BeTrue();
        Db.Context.Set<ModulePolicy>().Count().Should().Be(0);
    }
}
