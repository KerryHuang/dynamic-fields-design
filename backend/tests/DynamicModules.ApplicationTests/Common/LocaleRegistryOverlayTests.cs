using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Localization;
using DynamicModules.Domain.Localization;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace DynamicModules.ApplicationTests.Common;

/// <summary>覆寫層接進語系快照與介面文字（語系包設計 §4.2、§6.1；i4d1 計畫判斷 10）。純記憶體。</summary>
public class LocaleRegistryOverlayTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 21, 0, 0, 0, TimeSpan.Zero);

    /// <summary>記下 Warning 的訊息（測「沒有讀取失敗」與「壞列有記錄」）。</summary>
    internal sealed class WarningLog<T> : ILogger<T>
    {
        public readonly List<string> Warnings = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (logLevel == LogLevel.Warning) lock (Warnings) Warnings.Add(formatter(state, exception)); }
    }

    private static string Json(params (string Key, string Value)[] pairs) => JsonSerializer.Serialize(pairs.ToDictionary(p => p.Key, p => p.Value));

    private static (LocaleRegistry Registry, FakeLocaleRepository Repo, ManualTimeProvider Time, WarningLog<LocaleRegistry> Log) Create(Action<FakeLocaleRepository>? setup = null)
    {
        var repo = new FakeLocaleRepository().WithBuiltin();
        setup?.Invoke(repo);
        var time = new ManualTimeProvider(Start);
        var log = new WarningLog<LocaleRegistry>();
        return (repo.Registry(time, log), repo, time, log);
    }

    private static async Task Reload(LocaleRegistry r)
    {
        r.Invalidate();
        await r.EnsureFreshAsync(default);
    }

    private static Dictionary<string, string> Content(UiBundle b) => JsonSerializer.Deserialize<Dictionary<string, string>>(b.Json)!;

    /// <summary>獨立的預期值：沿鏈只疊內建層（截到繁中），不依賴改動後的 <see cref="LocaleRegistry.Ui"/>。</summary>
    private static SortedDictionary<string, string> BuiltinMerge(LocaleRegistry r, string code, string defaultLocale)
    {
        var chain = new List<string>();
        foreach (var c in r.FallbacksFor(code, defaultLocale).Prepend(code).Distinct(StringComparer.Ordinal))
        {
            chain.Add(c);
            if (c == I18nText.FallbackLocale) break;
        }
        return UiTextMerge.Merge(BuiltinLocaleResources.BaseKeys,
            chain.Select(c => BuiltinLocaleResources.Ui.GetValueOrDefault(c)).OfType<IReadOnlyDictionary<string, string>>());
    }

    [Theory]
    [InlineData("zh-TW")]
    [InlineData("th")]
    public async Task NoOverlay_UiSameAsBuiltinMerge(string defaultLocale)
    {
        var (r, _, _, _) = Create();
        await r.EnsureFreshAsync(default);

        foreach (var code in BuiltinLocales.Codes)
        {
            var expected = BuiltinMerge(r, code, defaultLocale);
            Content(r.Ui(code, defaultLocale)!).Should().Equal(expected, code);
            r.UiHash(code, defaultLocale).Should().Be(UiTextMerge.Serialize(expected).Hash, code);
        }
    }

    [Fact]
    public async Task Overlay_TakesPrecedence_ThenBuiltin()
    {
        var (r, repo, _, _) = Create();
        await r.EnsureFreshAsync(default);
        var before = r.UiHash("th", "zh-TW");

        repo.Packs.Set("th", Json(("menu.home", "หน้าแรก (ทดสอบ)")));
        await Reload(r);

        var th = Content(r.Ui("th", "zh-TW")!);
        th["menu.home"].Should().Be("หน้าแรก (ทดสอบ)");
        th["menu.users"].Should().Be(BuiltinLocaleResources.Ui["th"]["menu.users"]);
        r.UiHash("th", "zh-TW").Should().NotBe(before);
        r.Texts().Overlay("th")!.Ui["menu.home"].Should().Be("หน้าแรก (ทดสอบ)");
    }

    [Fact]
    public async Task Overlay_NewLocale_ChainsToParentThenDefault()
    {
        var overlay = Json(("menu.home", "ホーム"), ("menu.users", "ユーザー"), ("menu.groups", "グループ"));
        var (r, repo, _, _) = Create(repo => repo.Add("ja", "日本語", fallback: "en-US").Add("ko", "한국어"));
        repo.Packs.Set("ja", overlay);
        repo.Packs.Set("ko", overlay);
        await r.EnsureFreshAsync(default);

        var ja = Content(r.Ui("ja", "th")!);
        ja["menu.home"].Should().Be("ホーム");
        ja["menu.groups"].Should().Be("グループ");
        ja["menu.reports"].Should().Be(BuiltinLocaleResources.Ui["en-US"]["menu.reports"], "有上層語系先退英文");

        var ko = Content(r.Ui("ko", "th")!);
        ko["menu.home"].Should().Be("ホーム");
        ko["menu.reports"].Should().Be(BuiltinLocaleResources.Ui["th"]["menu.reports"], "沒有上層語系退到預設語系");
    }

    [Fact]
    public async Task Overlay_ParentOverrideFlowsDown()
    {
        var (r, repo, _, _) = Create(repo => repo.Add("ja", "日本語", fallback: "en-US"));
        repo.Packs.Set("en-US", Json(("menu.home", "Start page")));
        await r.EnsureFreshAsync(default);

        Content(r.Ui("ja", "zh-TW")!)["menu.home"].Should().Be("Start page");
    }

    [Fact]
    public async Task Overlay_KeysOutsideBase_Dropped()
    {
        var (r, repo, _, _) = Create();
        await r.EnsureFreshAsync(default);
        var before = r.UiHash("th", "zh-TW");

        repo.Packs.Set("th", Json(("no.such.key", "x")), Json(("NoSuchServerKey", "y")));
        await Reload(r);

        Content(r.Ui("th", "zh-TW")!).Should().NotContainKey("no.such.key");
        r.UiHash("th", "zh-TW").Should().Be(before);
        r.Texts().Overlay("th")!.Ui.Should().BeEmpty();
        r.Texts().ServerOverride("th", "NoSuchServerKey").Should().BeNull();
    }

    [Fact]
    public async Task Overlay_UninstalledLocale_Dropped()
    {
        var (r, repo, _, _) = Create();
        repo.Packs.Set("ja", Json(("menu.home", "ホーム")));
        await r.EnsureFreshAsync(default);

        r.Texts().Overlay("ja").Should().BeNull();
    }

    [Fact]
    public async Task Overlay_CorruptRow_TreatedAsEmpty()
    {
        var (r, repo, _, log) = Create(repo => repo.Add("ja", "日本語"));
        repo.Packs.Set("ja", "{not json");
        repo.Packs.Set("th", Json(("menu.home", "หน้าแรก (ทดสอบ)")));

        await r.Invoking(x => x.EnsureFreshAsync(default)).Should().NotThrowAsync();

        r.Texts().Overlay("ja").Should().BeNull();
        r.Codes.Should().Contain("ja");
        Content(r.Ui("th", "zh-TW")!)["menu.home"].Should().Be("หน้าแรก (ทดสอบ)");
        log.Warnings.Should().ContainSingle(w => w.Contains("ja"));
    }

    [Fact]
    public async Task StampUnchanged_NoReload()
    {
        var (r, repo, time, _) = Create();
        await r.EnsureFreshAsync(default);
        var calls = repo.Packs.ListCurrentCalls;

        time.Advance(TimeSpan.FromMinutes(2));
        await r.EnsureFreshAsync(default);

        repo.Packs.ListCurrentCalls.Should().Be(calls);
    }

    [Fact]
    public async Task Invalidate_ReloadsEvenIfStampSame()
    {
        var (r, repo, time, _) = Create(repo => repo.Add("ja", "日本語"));
        repo.Packs.Set("ja", Json(("menu.home", "旧")));
        await r.EnsureFreshAsync(default);
        repo.PinStamp = await repo.StampAsync(default);
        await r.EnsureFreshAsync(default);
        r.Texts().Overlay("ja")!.Ui["menu.home"].Should().Be("旧");

        // 戳記釘住、換掉內容：TTL 到了也不重讀
        repo.Packs.Set("ja", Json(("menu.home", "新")));
        time.Advance(TimeSpan.FromMinutes(2));
        await r.EnsureFreshAsync(default);
        r.Texts().Overlay("ja")!.Ui["menu.home"].Should().Be("旧");

        // 收到 Invalidate 一定重讀
        await Reload(r);
        r.Texts().Overlay("ja")!.Ui["menu.home"].Should().Be("新");

        // 刷新途中又收到 Invalidate：旗標不被清掉，下一次照樣重讀
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        r.Invalidate();
        repo.ListGate = gate;
        var listCalls = repo.ListCalls;
        await Task.Run(async () =>
        {
            var refresh = r.EnsureFreshAsync(default);
            for (var i = 0; i < 500 && repo.ListCalls == listCalls; i++) await Task.Delay(10);
            r.Invalidate();
            gate.SetResult();
            await refresh;
        });
        var packCalls = repo.Packs.ListCurrentCalls;
        await r.EnsureFreshAsync(default);
        repo.Packs.ListCurrentCalls.Should().Be(packCalls + 1, "刷新途中的失效通知沒有被清掉");
    }

    [Fact]
    public async Task Registry_ResolvesPackRepository()
    {
        var (r, repo, _, log) = Create();
        repo.Packs.Set("vi", Json(("menu.home", "Trang chủ (thử)")));

        await r.EnsureFreshAsync(default);

        r.Texts().Overlay("vi")!.Ui["menu.home"].Should().Be("Trang chủ (thử)");
        r.Texts().Overlay("vi")!.PackId.Should().Be(repo.Packs.Rows.Single(p => p.IsCurrent).Id);
        log.Warnings.Should().BeEmpty("覆寫層的 repository 有註冊，刷新沒有失敗");
    }

    [Fact]
    public async Task OverlayChange_NewUiCache_SameGeneration()
    {
        var (r, repo, _, _) = Create();
        await r.EnsureFreshAsync(default);
        var generation = r.Generation;
        _ = r.Ui("th", "zh-TW");

        repo.Packs.Set("th", Json(("menu.home", "หน้าแรก (ทดสอบ)")));
        await Reload(r);

        r.Generation.Should().Be(generation, "清單沒變，政策快照不必重載");
        Content(r.Ui("th", "zh-TW")!)["menu.home"].Should().Be("หน้าแรก (ทดสอบ)");
    }

    [Fact]
    public async Task Texts_ContextsFor_SameAsRegistry()
    {
        var (r, _, _, _) = Create(repo => repo.Add("ja", "日本語", fallback: "en-US"));
        await r.EnsureFreshAsync(default);

        var contexts = r.Texts().ContextsFor("th");

        contexts.Select(c => c.Locale).Should().Equal(r.Codes);
        foreach (var lc in contexts) lc.Fallbacks.Should().Equal(r.FallbacksFor(lc.Locale, "th"));
        r.ContextsFor("th").Select(c => c.Locale).Should().Equal(r.Codes);
    }
}
