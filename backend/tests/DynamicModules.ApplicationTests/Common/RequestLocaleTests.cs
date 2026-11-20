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
        var cache = new CachedSystemPolicy(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System, PolicyEnvironmentDefaults.Empty, NullLogger<CachedSystemPolicy>.Instance);
        cache.Apply(PolicyCatalog.LocaleDefault, "en-US", cache.NextSequence());
        cache.Apply(PolicyCatalog.LocaleEnabled, "zh-TW,en-US", cache.NextSequence());
        return new RequestLocale(new Header("zh-TW"), cache);
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
        lc.Fallbacks.Should().Equal("en-US", "zh-TW");
    }

    [Fact]
    public void SystemDefault_And_Current()
    {
        var r = Create();
        r.SystemDefault.Locale.Should().Be("en-US");
        r.SystemDefault.Fallbacks.Should().Equal("en-US", "zh-TW");
        r.Current.Locale.Should().Be("zh-TW");
    }
}
