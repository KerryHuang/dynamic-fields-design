using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Localization;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace DynamicModules.ApplicationTests.Common;

/// <summary>語系快照上的介面文字合併與 uiHash（語系包 i4b 計畫判斷 5–7、21）。純記憶體。</summary>
public class LocaleRegistryUiTests
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

    private static async Task<LocaleRegistry> Loaded(FakeLocaleRepository repo, ILogger<LocaleRegistry>? log = null)
    {
        var r = repo.Registry(new ManualTimeProvider(Start), log);
        await r.EnsureFreshAsync(default);
        return r;
    }

    private static async Task Reload(LocaleRegistry r)
    {
        r.Invalidate();
        await r.EnsureFreshAsync(default);
    }

    private static Dictionary<string, string> Content(UiBundle b) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(b.Json)!;

    [Fact]
    public void Ui_Builtin_EqualsEmbedded()
    {
        var r = new FakeLocaleRepository().WithBuiltin().Registry(new ManualTimeProvider(Start));

        var th = r.Ui("th", "zh-TW")!;

        th.KeyCount.Should().Be(BuiltinLocaleResources.BaseKeys.Count);
        Content(th).Should().Equal(BuiltinLocaleResources.Ui["th"]);
        Content(th)["menu.home"].Should().Be(BuiltinLocaleResources.Ui["th"]["menu.home"]).And.NotBe(BuiltinLocaleResources.Ui["zh-TW"]["menu.home"]);
    }

    [Fact]
    public async Task Ui_InstalledJa_FallsToParent()
    {
        var r = await Loaded(new FakeLocaleRepository().WithBuiltin().Add("ja", "日本語", fallback: "en-US"));

        Content(r.Ui("ja", "th")!).Should().Equal(BuiltinLocaleResources.Ui["en-US"]);
        r.UiHash("ja", "th").Should().Be(r.UiHash("en-US", "th"), "內容相同就同雜湊");
    }

    [Fact]
    public async Task Ui_InstalledJaNoParent_FallsToDefault()
    {
        var r = await Loaded(new FakeLocaleRepository().WithBuiltin().Add("ja", "日本語"));

        Content(r.Ui("ja", "th")!).Should().Equal(BuiltinLocaleResources.Ui["th"]);
    }

    [Fact]
    public async Task UiHash_ChangesWithDefault()
    {
        var r = await Loaded(new FakeLocaleRepository().WithBuiltin().Add("ja", "日本語"));

        r.UiHash("ja", "th").Should().NotBe(r.UiHash("ja", "vi"));
    }

    [Fact]
    public async Task UiHash_ChangesWhenParentChanges()
    {
        var repo = new FakeLocaleRepository().WithBuiltin().Add("ja", "日本語", fallback: "en-US");
        var r = await Loaded(repo);
        var before = r.UiHash("ja", "zh-TW");
        var generation = r.Generation;

        repo.Remove("ja");
        repo.Add("ja", "日本語", fallback: "vi");
        await Reload(r);

        r.Generation.Should().Be(generation + 1);
        r.UiHash("ja", "zh-TW").Should().NotBe(before).And.Be(r.UiHash("vi", "zh-TW"));
    }

    [Fact]
    public async Task Ui_Cached_SameInstance()
    {
        var repo = new FakeLocaleRepository().WithBuiltin();
        var r = await Loaded(repo);
        var first = r.Ui("th", "zh-TW");

        r.Ui("th", "zh-TW").Should().BeSameAs(first);

        repo.Add("ja", "日本語");
        await Reload(r);
        r.Ui("th", "zh-TW").Should().NotBeSameAs(first, "registry 換新後快取整份換掉").And.BeEquivalentTo(first);
    }

    [Fact]
    public async Task Ui_NotInstalled_Null()
    {
        var r = await Loaded(new FakeLocaleRepository().WithBuiltin());

        r.Ui("ja", "zh-TW").Should().BeNull();
        r.UiHash("ja", "zh-TW").Should().BeEmpty();
    }

    [Fact]
    public async Task Build_UnknownQuasarLang_TreatedAsNull()
    {
        var repo = new FakeLocaleRepository().WithBuiltin().Add("ja", "日本語").Add("ko", "한국어");
        repo.Rows.Single(x => x.Code == "ja").QuasarLang = "xx";
        repo.Rows.Single(x => x.Code == "ko").QuasarLang = "ko-KR";
        var log = new WarningCounter();

        var r = await Loaded(repo, log);

        r.Find("ja")!.QuasarLang.Should().BeNull();
        r.Find("ko")!.QuasarLang.Should().Be("ko-KR");
        log.Warnings.Should().Be(1);
    }
}
