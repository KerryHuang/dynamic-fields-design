using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Localization;
using DynamicModules.Application.Dynamic.LocaleService.Packs;
using DynamicModules.Domain.Localization;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic.LocalePacks;

/// <summary>完整度與缺最多的分類（i4d2 計畫判斷 4）：報告與清單共用 <see cref="LocaleCompleteness.Of"/>。</summary>
public class LocaleCompletenessTests
{
    private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>();

    private static LocaleOverlay Overlay(IReadOnlyDictionary<string, string>? ui = null, IReadOnlyDictionary<string, string>? server = null) =>
        new(ui ?? Empty, server ?? Empty, 1);

    /// <summary>除了 <paramref name="missing"/> 以外全部有句子的 ja 覆寫層。</summary>
    private static LocaleOverlay AllBut(IEnumerable<string> missingUi, IEnumerable<string> missingServer)
    {
        var mu = missingUi.ToHashSet();
        var ms = missingServer.ToHashSet();
        return Overlay(BuiltinLocaleResources.BaseKeys.Where(k => !mu.Contains(k)).ToDictionary(k => k, _ => "x"),
            BuiltinServerText.BaseKeys.Where(k => !ms.Contains(k)).ToDictionary(k => k, _ => "x"));
    }

    private static IEnumerable<string> Ui(string prefix, int n) => BuiltinLocaleResources.BaseKeys.Where(k => k.StartsWith(prefix + ".")).Take(n);

    [Fact]
    public void SameAsAnalyzerReport()
    {
        var pack = new ParsedLocalePack("ja", "日本語", null, "en-US", null, null, null, null,
            new Dictionary<string, string> { ["menu.home"] = "ホーム" }, [], new Dictionary<string, string> { ["Forbidden"] = "権限がありません" }, [], 0);
        var world = new LocaleWorld(BuiltinLocales.All.Select(b => new InstalledLocaleInfo(b.Code, b.NativeName, b.QuasarLang, null, true)).ToList(),
            new Dictionary<string, LocaleOverlay>(), "zh-TW", ["zh-TW"], ["zh-TW"]);
        var a = LocalePackAnalyzer.Analyze(pack, world, 10, null);
        a.Report!.Completeness.Should().BeEquivalentTo(LocaleCompleteness.Of("ja", LocalePackAnalyzer.After(world, a.Plan!).Overlays["ja"]));
        a.Report.Completeness.TopMissing.Should().NotBeEmpty();
    }

    [Fact]
    public void Builtin_Complete_NoTopMissing()
    {
        var c = LocaleCompleteness.Of("th", null);
        c.Ui.Should().Be(new CompletenessCount(BuiltinLocaleResources.BaseKeys.Count, BuiltinLocaleResources.BaseKeys.Count));
        c.Server.Have.Should().Be(c.Server.Total);
        c.TopMissing.Should().BeEmpty();
    }

    [Fact]
    public void TopMissing_GroupsAndOrders()
    {
        var c = LocaleCompleteness.Of("ja", AllBut(Ui("approval", 3).Concat(Ui("dynamic", 1)), []));
        c.TopMissing.Should().Equal(new MissingCategory("ui", "approval", 3), new MissingCategory("ui", "dynamic", 1));
        c.Ui.Have.Should().Be(BuiltinLocaleResources.BaseKeys.Count - 4);
    }

    [Fact]
    public void TopMissing_TieByCategoryName_Max5()
    {
        var cats = BuiltinLocaleResources.BaseKeys.Select(k => k.Split('.')[0]).Distinct().Order(StringComparer.Ordinal).Take(7).ToList();
        cats.Should().HaveCount(7);
        var c = LocaleCompleteness.Of("ja", AllBut(cats.SelectMany(cat => Ui(cat, 1)), []));
        c.TopMissing.Should().HaveCount(LocalePackLimits.MaxMissingCategories);
        c.TopMissing.Select(m => m.Category).Should().Equal(cats.Take(5));
    }

    [Fact]
    public void Server_ThreeCategories()
    {
        var report = BuiltinServerText.BaseKeys.Where(k => k.StartsWith("report.")).Take(3);
        var alert = BuiltinServerText.BaseKeys.Where(k => k.StartsWith("alert.")).Take(2);
        var message = BuiltinServerText.BaseKeys.Where(k => !k.StartsWith("report.") && !k.StartsWith("alert.")).Take(1);
        var c = LocaleCompleteness.Of("ja", AllBut([], report.Concat(alert).Concat(message)));
        c.TopMissing.Should().Equal(new MissingCategory("server", "report", 3), new MissingCategory("server", "alert", 2), new MissingCategory("server", "message", 1));
    }
}
