using DynamicModules.Application.Common;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace DynamicModules.ApplicationTests.Common;

/// <summary>已安裝語系的快照（語系包設計 §4.2；i4a 計畫判斷 2–5）：內建取自程式、版本戳記、generation、失效通知不被蓋掉。純記憶體。</summary>
public class LocaleRegistryTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 20, 0, 0, 0, TimeSpan.Zero);

    private sealed class WarningCounter : ILogger<LocaleRegistry>
    {
        public int Warnings;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (logLevel == LogLevel.Warning) Interlocked.Increment(ref Warnings); }
    }

    private static (LocaleRegistry Registry, FakeLocaleRepository Repo, ManualTimeProvider Time, WarningCounter Log) Create()
    {
        var repo = new FakeLocaleRepository().WithBuiltin();
        var time = new ManualTimeProvider(Start);
        var log = new WarningCounter();
        return (repo.Registry(time, log), repo, time, log);
    }

    private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WaitUntil(Func<bool> cond)
    {
        for (var i = 0; i < 500 && !cond(); i++) await Task.Delay(10);
        cond().Should().BeTrue("等候條件逾時");
    }

    [Fact]
    public void NeverLoaded_HasBuiltinFour()
    {
        var (r, _, _, _) = Create();
        r.Codes.Should().Equal("zh-TW", "en-US", "th", "vi");
        r.Find("th")!.NativeName.Should().Be("ไทย");
        r.Find("ja").Should().BeNull();
        r.Generation.Should().Be(0);
    }

    [Fact]
    public async Task Loaded_AppendsInstalledInOrder()
    {
        var repo = new FakeLocaleRepository()
            .Add("zh-TW", "繁體中文", LocaleOrigin.Builtin).Add("en-US", "English", LocaleOrigin.Builtin)
            .Add("th", "x", LocaleOrigin.Builtin).Add("vi", "Tiếng Việt", LocaleOrigin.Builtin)
            .Add("ja", "日本語", fallback: "en-US");
        var r = repo.Registry(new ManualTimeProvider(Start));

        await r.EnsureFreshAsync(default);

        r.Codes.Should().Equal("zh-TW", "en-US", "th", "vi", "ja");
        r.Find("th")!.NativeName.Should().Be("ไทย", "內建語系取自程式");
        r.Find("th")!.Builtin.Should().BeTrue();
        r.Find("ja").Should().Be(new InstalledLocaleInfo("ja", "日本語", null, "en-US", false));
        r.Generation.Should().Be(1);
    }

    [Fact]
    public async Task BuiltinOnly_GenerationSame()
    {
        var (r, _, _, _) = Create();
        await r.EnsureFreshAsync(default);
        r.Codes.Should().Equal("zh-TW", "en-US", "th", "vi");
        r.Generation.Should().Be(0, "內容與從沒載入時相同");
    }

    [Fact]
    public async Task StampUnchanged_DoesNotReloadList()
    {
        var (r, repo, time, _) = Create();
        await r.EnsureFreshAsync(default);
        time.Advance(TimeSpan.FromSeconds(30));
        await r.EnsureFreshAsync(default);
        repo.StampCalls.Should().Be(1, "60 秒內不檢查");

        time.Advance(TimeSpan.FromSeconds(31));
        await r.EnsureFreshAsync(default);
        repo.StampCalls.Should().Be(2);
        repo.ListCalls.Should().Be(1);
    }

    [Fact]
    public async Task StampChanged_Reloads()
    {
        var (r, repo, time, _) = Create();
        await r.EnsureFreshAsync(default);
        var gen = r.Generation;
        repo.Add("ja", "日本語");
        time.Advance(TimeSpan.FromSeconds(61));
        await r.EnsureFreshAsync(default);
        repo.ListCalls.Should().Be(2);
        r.Codes.Should().EndWith("ja");
        r.Generation.Should().Be(gen + 1);
    }

    [Fact]
    public async Task StampUnchanged_GenerationSame()
    {
        var (r, repo, time, _) = Create();
        repo.Add("ja", "日本語");
        await r.EnsureFreshAsync(default);
        var gen = r.Generation;
        time.Advance(TimeSpan.FromSeconds(61));
        await r.EnsureFreshAsync(default);
        r.Generation.Should().Be(gen);
    }

    [Fact]
    public async Task ReadFails_KeepsLastAndRetriesAfterFailureTtl()
    {
        var (r, repo, time, log) = Create();
        repo.Add("ja", "日本語");
        await r.EnsureFreshAsync(default);

        time.Advance(TimeSpan.FromSeconds(61));
        repo.Throw = new InvalidOperationException("db down");
        await r.EnsureFreshAsync(default);
        r.Codes.Should().EndWith("ja", "沿用上次");
        log.Warnings.Should().Be(1);

        time.Advance(TimeSpan.FromSeconds(9));
        await r.EnsureFreshAsync(default);
        repo.StampCalls.Should().Be(2, "失敗後 10 秒內不重試");

        repo.Throw = null;
        time.Advance(TimeSpan.FromSeconds(1));
        await r.EnsureFreshAsync(default);
        repo.StampCalls.Should().Be(3);
    }

    [Fact]
    public async Task NeverLoaded_ReadFails_StillBuiltinFour()
    {
        var (r, repo, _, _) = Create();
        repo.Throw = new InvalidOperationException("db down");
        await r.EnsureFreshAsync(default);
        r.Codes.Should().Equal("zh-TW", "en-US", "th", "vi");
    }

    [Fact]
    public async Task ConcurrentRefresh_ReadsOnce()
    {
        var (r, repo, _, _) = Create();
        var gate = NewGate();
        repo.ListGate = gate;
        await Task.Run(async () =>
        {
            var first = r.EnsureFreshAsync(default);
            await WaitUntil(() => repo.ListCalls == 1);
            await r.EnsureFreshAsync(default); // 拿不到刷新鎖，直接返回
            gate.SetResult();
            await first;
        });
        repo.StampCalls.Should().Be(1);
        repo.ListCalls.Should().Be(1);
    }

    [Fact]
    public async Task Invalidate_ForcesReload()
    {
        var (r, repo, _, _) = Create();
        await r.EnsureFreshAsync(default);
        repo.Add("ja", "日本語");
        await r.EnsureFreshAsync(default);
        r.Codes.Should().NotContain("ja", "還沒過期");

        r.Invalidate();
        await r.EnsureFreshAsync(default);
        r.Codes.Should().EndWith("ja");
    }

    [Fact]
    public async Task InvalidateDuringRefresh_NotLost()
    {
        var (r, repo, _, _) = Create();
        var gate = NewGate();
        repo.ListGate = gate;
        await Task.Run(async () =>
        {
            var refresh = r.EnsureFreshAsync(default);
            await WaitUntil(() => repo.ListCalls == 1);
            repo.Add("ja", "日本語");
            r.Invalidate();
            gate.SetResult();
            await refresh;
        });
        r.Codes.Should().NotContain("ja", "這次讀到的是失效之前的資料");

        await r.EnsureFreshAsync(default);
        repo.ListCalls.Should().Be(2, "失效通知沒有被進行中的刷新蓋掉");
        r.Codes.Should().EndWith("ja");
    }

    [Fact]
    public async Task InvalidateBeforeCas_NotLost()
    {
        var (r, repo, _, _) = Create();
        r.BeforePublishForTests = () =>
        {
            r.BeforePublishForTests = null;
            repo.Add("ja", "日本語");
            r.Invalidate();
        };
        await r.EnsureFreshAsync(default);
        r.Codes.Should().NotContain("ja");

        await r.EnsureFreshAsync(default);
        repo.ListCalls.Should().Be(2);
        r.Codes.Should().EndWith("ja");
    }

    [Fact]
    public async Task FallbacksFor_UsesParentsAndDefault()
    {
        var (r, repo, _, _) = Create();
        repo.Add("ja", "日本語", fallback: "en-US");
        await r.EnsureFreshAsync(default);
        r.FallbacksFor("ja", "th").Should().Equal("en-US", "th", "zh-TW", "vi", "ja");
    }

    [Fact]
    public async Task ContextFor_UsesChain()
    {
        var (r, repo, _, _) = Create();
        repo.Add("ja", "日本語", fallback: "en-US");
        await r.EnsureFreshAsync(default);
        var lc = r.ContextFor("ja", "th");
        lc.Locale.Should().Be("ja");
        lc.Fallbacks.Should().Equal("en-US", "th", "zh-TW", "vi", "ja");
        lc.Fallbacks.Should().Equal(r.FallbacksFor("ja", "th"));
    }
}
