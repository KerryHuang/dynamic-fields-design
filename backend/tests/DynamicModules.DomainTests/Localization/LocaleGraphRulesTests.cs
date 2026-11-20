using DynamicModules.Domain.Localization;
using FluentAssertions;

namespace DynamicModules.DomainTests.Localization;

/// <summary>上層語系圖守衛（語系包設計 §4.1；i4d1 計畫判斷 3）。</summary>
public class LocaleGraphRulesTests
{
    private static readonly (string, string?)[] Builtin = [("zh-TW", null), ("en-US", null), ("th", null), ("vi", null)];

    private static IReadOnlyList<(string Code, string? FallbackCode)> With(params (string, string?)[] extra) => [.. Builtin, .. extra];

    [Fact]
    public void FallbackInstalled_Passes() => LocaleGraphRules.Check(With(("ja", "en-US")), "ja").Should().BeNull();

    [Fact]
    public void NoFallback_Passes() => LocaleGraphRules.Check(With(("ja", null)), "ja").Should().BeNull();

    [Fact]
    public void FallbackNotInstalled() => LocaleGraphRules.Check(With(("ja", "ko")), "ja").Should().Be(LocaleGraphRules.FallbackNotInstalled);

    [Fact]
    public void FallbackSelf() => LocaleGraphRules.Check(With(("ja", "ja")), "ja").Should().Be(LocaleGraphRules.FallbackSelf);

    [Fact]
    public void Cycle() => LocaleGraphRules.Check(With(("a", "b"), ("b", "a")), "a").Should().Be(LocaleGraphRules.FallbackCycle);

    [Fact]
    public void FourLevels_TooDeep()
        => LocaleGraphRules.Check(With(("a", "b"), ("b", "c"), ("c", "d"), ("d", "e"), ("e", null)), "a").Should().Be(LocaleGraphRules.FallbackTooDeep);

    [Fact]
    public void ThreeLevels_Passes()
        => LocaleGraphRules.Check(With(("a", "b"), ("b", "c"), ("c", "d"), ("d", null)), "a").Should().BeNull();

    [Fact]
    public void ChangingParent_MakesDownstreamTooDeep()
    {
        // 原本 a→b→c（2 層）；b 改成指向 d→e，a 變成 a→b→d→e→zh-TW 四層
        var after = With(("a", "b"), ("b", "d"), ("d", "e"), ("e", "zh-TW"), ("c", null));

        LocaleGraphRules.Check(after, "b").Should().Be(LocaleGraphRules.FallbackTooDeep);
    }

    [Fact]
    public void CycleThroughOtherLocales_Detected()
        => LocaleGraphRules.Check(With(("a", "b"), ("b", "c"), ("c", "a")), "c").Should().Be(LocaleGraphRules.FallbackCycle);
}
