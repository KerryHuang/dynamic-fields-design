using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Policies;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DynamicModules.ApplicationTests.Common;

/// <summary>RequestLocale.For 的三條規則（D4，使用者 2026-11-20 裁決；p3 計畫 Task 10）。</summary>
public class RequestLocaleTests
{
    private sealed class Header(string locale) : ICurrentLocale { public string Locale => locale; }

    private static RequestLocale Create()
    {
        // 快照：預設 en-US、啟用 zh-TW 與 en-US；請求標頭是 zh-TW
        var scopes = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var cache = new CachedSystemPolicy(scopes, TimeProvider.System, PolicyEnvironmentDefaults.Empty,
            new LocaleRegistry(scopes, TimeProvider.System, NullLogger<LocaleRegistry>.Instance), NullLogger<CachedSystemPolicy>.Instance);
        cache.Apply(PolicyCatalog.LocaleDefault, "en-US", cache.NextSequence());
        cache.Apply(PolicyCatalog.LocaleEnabled, "zh-TW,en-US", cache.NextSequence());
        return new RequestLocale(new Header("zh-TW"), cache);
    }

    /// <summary>裝了 ja（上層 en-US）、預設 th；<paramref name="enabled"/> 是啟用語系；請求標頭 ja（標頭是否啟用由 HeaderCurrentLocale 判斷，這裡直接給）。</summary>
    private static async Task<RequestLocale> Japanese(string enabled)
    {
        var registry = new FakeLocaleRepository().WithBuiltin().Add("ja", "日本語", fallback: "en-US").Registry(TimeProvider.System);
        await registry.EnsureFreshAsync(default);
        var scopes = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var cache = new CachedSystemPolicy(scopes, TimeProvider.System, PolicyEnvironmentDefaults.Empty, registry, NullLogger<CachedSystemPolicy>.Instance);
        cache.Apply(PolicyCatalog.LocaleDefault, "th", cache.NextSequence());
        cache.Apply(PolicyCatalog.LocaleEnabled, enabled, cache.NextSequence());
        return new RequestLocale(new Header("ja"), cache);
    }

    [Theory]
    [InlineData(null, "zh-TW")]
    [InlineData("", "zh-TW")]
    [InlineData("  ", "zh-TW")]
    [InlineData("en-US", "en-US")]
    [InlineData("zh-TW", "zh-TW")]
    [InlineData("th", "en-US")]
    [InlineData("xx", "en-US")]
    public void For_Rules(string? requested, string expected)
    {
        var lc = Create().For(requested);
        lc.Locale.Should().Be(expected);
        lc.Fallbacks.Should().Equal("en-US", "zh-TW", "th", "vi");
    }

    [Fact]
    public void SystemDefault_And_Current()
    {
        var r = Create();
        r.SystemDefault.Locale.Should().Be("en-US");
        r.SystemDefault.Fallbacks.Should().Equal("en-US", "zh-TW", "th", "vi");
        r.Current.Locale.Should().Be("zh-TW");
        r.Current.Fallbacks.Should().Equal("en-US", "zh-TW", "th", "vi");
    }

    [Fact]
    public async Task InstalledJapanese_ChainHasParentThenDefault()
    {
        var r = await Japanese("zh-TW,en-US,th,ja");
        r.Current.Locale.Should().Be("ja");
        r.Current.Fallbacks.Should().Equal("en-US", "th", "zh-TW", "vi", "ja");
        r.For("ja").Locale.Should().Be("ja");
        r.For("ja").Fallbacks.Should().Equal("en-US", "th", "zh-TW", "vi", "ja");
        r.SystemDefault.Fallbacks.Should().Equal("th", "zh-TW", "en-US", "vi", "ja");
    }

    [Fact]
    public async Task InstalledJapanese_NotEnabled_ForFallsToSystemDefault()
    {
        var r = await Japanese("zh-TW,en-US,th");
        var lc = r.For("ja");
        lc.Locale.Should().Be("th");
        lc.Fallbacks.Should().Equal(r.SystemDefault.Fallbacks);
    }
}
