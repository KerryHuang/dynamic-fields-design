using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Policies;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DynamicModules.ApplicationTests.Common;

public class CachedSystemPolicyTests
{
    private sealed class FakeReader : IPolicyReader
    {
        public string Value = "2000";
        public Exception? Throw;
        public bool Hang;
        public int Calls;

        public async Task<PolicyResolution> ResolveAsync(PolicyDefinition def, long? moduleId, CancellationToken ct)
        {
            Calls++;
            if (Hang) await Task.Delay(Timeout.Infinite, ct);
            if (Throw is not null) throw Throw;
            return new PolicyResolution(def.Factory, null, null, Value);
        }
        public Task<int> IntAsync(PolicyDefinition def, long? moduleId, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> BoolAsync(PolicyDefinition def, long? moduleId, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> ChoiceAsync(PolicyDefinition def, long? moduleId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListAsync(PolicyDefinition def, long? moduleId, CancellationToken ct) => throw new NotSupportedException();
        public void Invalidate() { }
    }

    private sealed class WarningCounter : ILogger<CachedSystemPolicy>
    {
        public int Warnings;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (logLevel == LogLevel.Warning) Warnings++; }
    }

    private static (CachedSystemPolicy Policy, FakeReader Reader, ManualTimeProvider Time, WarningCounter Log) Create()
    {
        var reader = new FakeReader();
        var services = new ServiceCollection();
        services.AddScoped<IPolicyReader>(_ => reader);
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 11, 12, 0, 0, 0, TimeSpan.Zero));
        var log = new WarningCounter();
        return (new CachedSystemPolicy(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), time, log), reader, time, log);
    }

    [Fact]
    public async Task ReturnsEffectiveValue()
    {
        var (p, _, _, _) = Create();
        (await p.IntAsync(PolicyCatalog.OpsSlowRequestMs, default)).Should().Be(2000);
    }

    [Fact]
    public async Task CachesFor60Seconds_ThenRereads()
    {
        var (p, reader, time, _) = Create();
        await p.IntAsync(PolicyCatalog.OpsSlowRequestMs, default);
        reader.Value = "500";
        (await p.IntAsync(PolicyCatalog.OpsSlowRequestMs, default)).Should().Be(2000);
        reader.Calls.Should().Be(1);
        time.Advance(TimeSpan.FromSeconds(61));
        (await p.IntAsync(PolicyCatalog.OpsSlowRequestMs, default)).Should().Be(500);
    }

    [Fact]
    public async Task ReaderThrows_ReturnsFactory_CachedFor10Seconds()
    {
        var (p, reader, time, log) = Create();
        reader.Throw = new InvalidOperationException("db down");
        (await p.IntAsync(PolicyCatalog.OpsSlowRequestMs, default)).Should().Be(2000);
        log.Warnings.Should().Be(1);

        reader.Throw = null;
        reader.Value = "500";
        time.Advance(TimeSpan.FromSeconds(9));
        (await p.IntAsync(PolicyCatalog.OpsSlowRequestMs, default)).Should().Be(2000);
        reader.Calls.Should().Be(1);
        log.Warnings.Should().Be(1);

        time.Advance(TimeSpan.FromSeconds(2));
        (await p.IntAsync(PolicyCatalog.OpsSlowRequestMs, default)).Should().Be(500);
    }

    [Fact]
    public async Task ReaderHangs_ReturnsFactoryWithinReadTimeout()
    {
        var (p, reader, time, _) = Create();
        reader.Hang = true;
        // ReadTimeout 用注入的 TimeProvider 計時，測試中推進假時間觸發逾時。
        // 整段放進 Task.Run 脫離 xUnit 的同步內容：否則逾時後的接續要排進 xUnit 有限的工作執行緒，
        // 其他測試佔滿時會等超過牆鐘上限而誤判失敗。CTS 計時器在 IntAsync 同步段就登記，不必先等。
        var result = await Task.Run(async () =>
        {
            var task = p.IntAsync(PolicyCatalog.OpsSlowRequestMs, CancellationToken.None);
            time.Advance(CachedSystemPolicy.ReadTimeout + TimeSpan.FromMilliseconds(1));
            return await task.WaitAsync(TimeSpan.FromSeconds(30));
        });
        result.Should().Be(2000);
    }

    [Fact]
    public async Task ModuleScopedPolicy_Throws()
    {
        var (p, _, _, _) = Create();
        await p.Invoking(x => x.IntAsync(PolicyCatalog.TaskReminderLeadDays, default)).Should().ThrowAsync<InvalidOperationException>();
    }
}
