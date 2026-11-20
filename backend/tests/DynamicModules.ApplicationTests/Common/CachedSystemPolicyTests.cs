using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Policies;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DynamicModules.ApplicationTests.Common;

public class CachedSystemPolicyTests
{
    /// <summary>
    /// 假 reader：<see cref="Calls"/> 是重載輪數（每輪逐項讀全部全系統政策，以第一項計一次）；<see cref="Gate"/> 讓下一輪卡在第一項，放行後才回值。
    /// 值：<see cref="ByCode"/> 有寫的照寫，<c>ops.slowRequestMs</c> 用 <see cref="Value"/>，其餘出廠值。
    /// </summary>
    private sealed class FakeReader : IPolicyReader
    {
        private static readonly PolicyDefinition First = PolicyCatalog.All.First(d => d.Scope == PolicyScope.System);
        public string Value = "2000";
        public readonly Dictionary<string, string> ByCode = new(StringComparer.Ordinal);
        public Exception? Throw;
        public bool Hang;
        public TaskCompletionSource? Gate;
        public int Calls;

        public async Task<PolicyResolution> ResolveAsync(PolicyDefinition def, long? moduleId, CancellationToken ct)
        {
            if (ReferenceEquals(def, First))
            {
                Interlocked.Increment(ref Calls);
                if (Interlocked.Exchange(ref Gate, null) is { } gate) await gate.Task;
            }
            if (Hang) await Task.Delay(Timeout.Infinite, ct);
            if (Throw is not null) throw Throw;
            var v = ByCode.TryGetValue(def.Code, out var x) ? x : def == PolicyCatalog.OpsSlowRequestMs ? Value : def.Factory;
            return new PolicyResolution(def.Factory, null, null, v);
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

    private static (CachedSystemPolicy Policy, FakeReader Reader, ManualTimeProvider Time, WarningCounter Log) Create(
        Dictionary<string, string>? deployment = null)
    {
        var reader = new FakeReader();
        var services = new ServiceCollection();
        services.AddScoped<IPolicyReader>(_ => reader);
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 11, 12, 0, 0, 0, TimeSpan.Zero));
        var log = new WarningCounter();
        return (new CachedSystemPolicy(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), time, new PolicyEnvironmentDefaults(deployment ?? []), log), reader, time, log);
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

    private static readonly TimeSpan H7 = TimeSpan.FromHours(7), H8 = TimeSpan.FromHours(8), H9 = TimeSpan.FromHours(9);
    private static readonly PolicyDefinition Tz = PolicyCatalog.LocaleTimeZone, Depth = PolicyCatalog.OrganizationMaxDepth;

    /// <summary>脫離 xUnit 的同步內容跑（理由同 ReaderHangs_ReturnsFactoryWithinReadTimeout）。</summary>
    private static Task Background(Func<Task> work) => Task.Run(work);

    private static async Task WaitUntil(Func<bool> cond)
    {
        for (var i = 0; i < 500 && !cond(); i++) await Task.Delay(10);
        cond().Should().BeTrue("等候條件逾時");
    }

    private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public void Get_BeforeLoad_UsesDeploymentFactory()
    {
        var (p, _, _, _) = Create();
        p.BusinessOffset.Should().Be(H8);
        p.Get(Depth).Should().Be("10");
        p.Invoking(x => x.Get(PolicyCatalog.TaskMaxRulesPerModule)).Should().Throw<InvalidOperationException>();

        var (deployed, _, _, _) = Create(new() { [Depth.Code] = "12" });
        deployed.Get(Depth).Should().Be("12");
    }

    [Fact]
    public async Task LocaleFallbacks_DefaultFirst()
    {
        var (p, reader, _, _) = Create();
        p.DefaultLocale.Should().Be("zh-TW");
        p.EnabledLocales.Should().Equal("zh-TW", "en-US", "th", "vi");
        p.RequiredTextLocales.Should().Equal("zh-TW");
        p.LocaleFallbacks.Should().Equal("zh-TW");

        reader.ByCode[PolicyCatalog.LocaleDefault.Code] = "en-US";
        reader.ByCode[PolicyCatalog.LocaleEnabled.Code] = "zh-TW,en-US";
        reader.ByCode[PolicyCatalog.LocaleRequiredText.Code] = "zh-TW,en-US";
        await p.EnsureFreshAsync(default);
        p.DefaultLocale.Should().Be("en-US");
        p.EnabledLocales.Should().Equal("zh-TW", "en-US");
        p.RequiredTextLocales.Should().Equal("zh-TW", "en-US");
        p.LocaleFallbacks.Should().Equal("en-US", "zh-TW");

        // 提交後的 Apply 一起換算
        p.Apply(PolicyCatalog.LocaleDefault, "th", p.NextSequence());
        p.DefaultLocale.Should().Be("th");
        p.LocaleFallbacks.Should().Equal("th", "zh-TW");
        p.Apply(PolicyCatalog.LocaleEnabled, "", p.NextSequence());
        p.EnabledLocales.Should().BeEmpty();
    }

    [Fact]
    public async Task EnsureFresh_LoadsAllSystemPolicies()
    {
        var (p, reader, _, _) = Create();
        reader.ByCode[Tz.Code] = "+07:00";
        reader.ByCode[Depth.Code] = "15";
        await p.EnsureFreshAsync(default);
        p.BusinessOffset.Should().Be(H7);
        p.Get(Depth).Should().Be("15");
        reader.Calls.Should().Be(1);
    }

    [Fact]
    public async Task ReaderFails_KeepsLastGoodValues()
    {
        var (p, reader, time, log) = Create();
        reader.ByCode[Tz.Code] = "+07:00";
        await p.EnsureFreshAsync(default);

        time.Advance(TimeSpan.FromSeconds(61));
        reader.Throw = new InvalidOperationException("db down");
        await p.EnsureFreshAsync(default);
        p.BusinessOffset.Should().Be(H7);
        log.Warnings.Should().Be(1);

        time.Advance(TimeSpan.FromSeconds(9));
        await p.EnsureFreshAsync(default);
        reader.Calls.Should().Be(2);
    }

    [Fact]
    public async Task ConcurrentEnsureFresh_ReadsOnce()
    {
        var (p, reader, _, _) = Create();
        var gate = NewGate();
        reader.Gate = gate;
        var tasks = Enumerable.Range(0, 20).Select(_ => Task.Run(() => p.EnsureFreshAsync(default))).ToList();
        await WaitUntil(() => reader.Calls >= 1);
        gate.SetResult();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));
        reader.Calls.Should().Be(1);
    }

    [Fact]
    public Task Apply_DuringReload_DiscardsReload() => Background(async () =>
    {
        var (p, reader, _, _) = Create();
        var gate = NewGate();
        reader.Gate = gate;
        var reload = p.EnsureFreshAsync(default);
        await WaitUntil(() => reader.Calls == 1);
        p.Apply(Tz, "+07:00", p.NextSequence());
        gate.SetResult();   // reader 回出廠 +08:00
        await reload.WaitAsync(TimeSpan.FromSeconds(30));

        p.BusinessOffset.Should().Be(H7);
        await p.EnsureFreshAsync(default);
        reader.Calls.Should().Be(2, "重載被丟掉時 LoadedAt 沒更新，下一次要重讀");
    });

    [Fact]
    public async Task Apply_BetweenCheckAndPublish_NotOverwritten()
    {
        var (p, _, _, _) = Create();
        var seq = p.NextSequence();
        var inserted = false;
        p.BeforePublishForTests = () =>
        {
            if (inserted) return;
            inserted = true;
            p.Apply(Tz, "+07:00", seq);
        };
        await p.EnsureFreshAsync(default);   // reader 回出廠 +08:00
        inserted.Should().BeTrue();
        p.BusinessOffset.Should().Be(H7);
    }

    [Fact]
    public Task ReloadFails_DuringApply_KeepsAppliedValue() => Background(async () =>
    {
        var (p, reader, time, _) = Create();
        var gate = NewGate();
        reader.Gate = gate;
        var reload = p.EnsureFreshAsync(default);
        await WaitUntil(() => reader.Calls == 1);
        p.Apply(Tz, "+07:00", p.NextSequence());
        reader.Throw = new InvalidOperationException("db down");
        gate.SetResult();
        await reload.WaitAsync(TimeSpan.FromSeconds(30));
        p.BusinessOffset.Should().Be(H7);

        time.Advance(TimeSpan.FromSeconds(9));
        await p.EnsureFreshAsync(default);
        reader.Calls.Should().Be(1, "失敗快取 10 秒");

        time.Advance(TimeSpan.FromSeconds(2));
        reader.Throw = null;
        reader.ByCode[Tz.Code] = "+07:00";
        await p.EnsureFreshAsync(default);
        reader.Calls.Should().Be(2);
        p.BusinessOffset.Should().Be(H7);
    });

    [Fact]
    public async Task Apply_OutOfOrderCallbacks_NewerWins()
    {
        var (p, reader, _, _) = Create();
        await p.EnsureFreshAsync(default);
        var s1 = p.NextSequence();
        var s2 = p.NextSequence();
        p.Apply(Tz, "+07:00", s2);
        p.Apply(Tz, "+09:00", s1);
        p.BusinessOffset.Should().Be(H7);
        await p.EnsureFreshAsync(default);
        reader.Calls.Should().Be(2, "較舊的 callback 被擋下時標成過期，下一個請求以資料庫為準重載");

        // 不同政策的序號互不影響
        var (q, _, _, _) = Create();
        var t1 = q.NextSequence();
        var t2 = q.NextSequence();
        var t3 = q.NextSequence();
        q.Apply(Tz, "+09:00", t3);
        q.Apply(Depth, "5", t1);
        q.Get(Depth).Should().Be("5");
        q.Apply(Depth, "6", t2);
        q.Get(Depth).Should().Be("6");
        q.BusinessOffset.Should().Be(H9);
    }

    [Fact]
    public async Task Publish_UsesReferenceEquality()
    {
        var (p, reader, _, _) = Create();
        reader.ByCode[Tz.Code] = "+07:00";
        var replaced = false;
        p.BeforePublishForTests = () =>
        {
            if (replaced) return;
            replaced = true;
            // 內容相同、參考不同、ApplyCount 不變：用 != 判斷會誤判交換成功、把新值丟掉，停在 8h
            p.ReplaceCurrentForTests(cur => cur with { });
        };
        await p.EnsureFreshAsync(default);
        replaced.Should().BeTrue();
        p.BusinessOffset.Should().Be(H7);
    }

    [Fact]
    public async Task Reload_BetweenSeqAndCommit_CallbackStillApplies()
    {
        var (p, _, _, _) = Create();
        var s = p.NextSequence();            // PUT 已配發序號、還沒提交
        await p.EnsureFreshAsync(default);   // 重載讀到舊值 +08:00
        p.BusinessOffset.Should().Be(H8);
        p.Apply(Tz, "+07:00", s);            // 提交後的 callback
        p.BusinessOffset.Should().Be(H7);
    }

    [Fact]
    public async Task Reload_AfterCallback_StaleCallbackIgnored()
    {
        var (p, reader, time, _) = Create();
        await p.EnsureFreshAsync(default);
        var s0 = p.NextSequence();
        var s1 = p.NextSequence();
        p.Apply(Tz, "+07:00", s1);
        time.Advance(TimeSpan.FromSeconds(61));
        reader.ByCode[Tz.Code] = "+07:00";   // 資料庫已含兩筆，最後是 +07:00
        await p.EnsureFreshAsync(default);
        reader.Calls.Should().Be(2);
        p.Apply(Tz, "+08:00", s0);           // 較舊 PUT 的晚到 callback
        p.BusinessOffset.Should().Be(H7);
    }

    [Fact]
    public async Task Reload_BeforeCallbacks_ConvergesToNewest()
    {
        var (p, reader, _, _) = Create();
        var s0 = p.NextSequence();
        var s1 = p.NextSequence();
        reader.ByCode[Tz.Code] = "+07:00";   // 兩筆都已提交、callback 都還沒跑；資料庫是 s1 的值
        await p.EnsureFreshAsync(default);
        p.Apply(Tz, "+08:00", s0);
        // 中間短暫是 8h：判斷 20 的已知窗口（提交到 Apply 之間），不另外處理
        p.BusinessOffset.Should().Be(H8);
        p.Apply(Tz, "+07:00", s1);
        p.BusinessOffset.Should().Be(H7);
    }
}
