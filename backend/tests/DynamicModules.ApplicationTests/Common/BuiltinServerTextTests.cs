using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Localization;
using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Common;

/// <summary>
/// 後端訊息的內建層（語系包 i4c 計畫判斷 3、14）：合併表與改表前固定的範本基準檔逐字相同。
/// 基準檔建立後不准改；比較一律對基準檔，不呼叫靜態 <c>Get</c>（之後會刪）。
/// </summary>
public class BuiltinServerTextTests
{
    public static readonly string[] Locales = ["zh-TW", "en-US", "th", "vi"];

    /// <summary>明列的基準檔；Task 4、7 各加一個，語系包 i4d1 Task 5 加 locale-pack.json（新鍵，建立後同樣不准改）。</summary>
    private static readonly string[] BaselineFiles = ["message-report.json", "task-report.json", "alert.json", "locale-pack.json"];

    /// <summary>repo 根目錄（往上找到有 shared/locale-chain-cases.json 的那層）。</summary>
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "shared", "locale-chain-cases.json"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("找不到 repo 根目錄");
    }

    /// <summary>讀一份範本基準檔：鍵 → 語系 → 未帶參數的範本。</summary>
    public static Dictionary<string, Dictionary<string, string>> Baseline(string file) =>
        JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(Path.Combine(
            RepoRoot(), "backend", "tests", "DynamicModules.ApplicationTests", "Common", "ServerTextBaseline", file)))!;

    private static string[] Placeholders(string text) =>
        Regex.Matches(text, @"\{(\d+)\}").Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x).ToArray();

    private static string Text((string Zh, string En, string Th, string Vi) e, int i) => i switch { 0 => e.Zh, 1 => e.En, 2 => e.Th, _ => e.Vi };

    private static IEnumerable<string> Consts(Type t) =>
        t.GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!);

    [Fact]
    public void Table_EqualsBaselines()
    {
        var expected = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var file in BaselineFiles)
            foreach (var (k, v) in Baseline(file)) expected.Add(k, v);

        BuiltinServerText.Table.Keys.Should().BeEquivalentTo(expected.Keys);
        foreach (var (key, texts) in expected)
            for (var i = 0; i < Locales.Length; i++)
                Text(BuiltinServerText.Table[key], i).Should().Be(texts[Locales[i]], $"{key} 的 {Locales[i]}");
    }

    [Fact]
    public void ReportKeys_AllPrefixed()
    {
        Consts(typeof(ReportText)).Should().NotBeEmpty().And.OnlyContain(k => k.StartsWith("report.", StringComparison.Ordinal));
        Consts(typeof(MessageText)).Should().NotBeEmpty().And.OnlyContain(k => !k.Contains('.'));
    }

    [Fact]
    public void AlertKeys_Prefixed()
    {
        var alert = BuiltinServerText.Table.Keys.Where(k => k.StartsWith("alert.", StringComparison.Ordinal)).ToList();
        alert.Should().BeEquivalentTo(Baseline("alert.json").Keys);
        Consts(typeof(Application.Dynamic.OpsService.Alerts.AlertMailText)).Should().NotBeEmpty()
            .And.OnlyContain(k => k.StartsWith("alert.", StringComparison.Ordinal) && !k.StartsWith("alert.label.", StringComparison.Ordinal));
        alert.Count(k => k.StartsWith("alert.label.", StringComparison.Ordinal)).Should().BeGreaterThan(0);
        foreach (var c in Application.Dynamic.OpsService.Health.HealthCheckCatalog.All)
            BuiltinServerText.Table["alert.check." + c.Code].Zh.Should().Be(c.ZhName);
        alert.Count(k => k.StartsWith("alert.check.", StringComparison.Ordinal)).Should().Be(Application.Dynamic.OpsService.Health.HealthCheckCatalog.All.Count);
    }

    [Fact]
    public void EveryKey_NonBlankInFourLocales()
    {
        foreach (var (key, e) in BuiltinServerText.Table)
            for (var i = 0; i < Locales.Length; i++)
                string.IsNullOrWhiteSpace(Text(e, i)).Should().BeFalse($"{key} 的 {Locales[i]} 不可空白");
    }

    [Fact]
    public void Placeholders_MatchZh_AndFormatDoesNotThrow()
    {
        foreach (var (key, e) in BuiltinServerText.Table)
        {
            var expected = Placeholders(e.Zh);
            var args = expected.Select(_ => (object)1).ToArray();
            for (var i = 0; i < Locales.Length; i++)
            {
                Placeholders(Text(e, i)).Should().Equal(expected, $"{key} 的 {Locales[i]} 佔位符要和 zh-TW 一致");
                var lc = new LocaleContext(Locales[i], []);
                var act = () => BuiltinMessageCatalog.Instance.Get(lc, key, args);
                act.Should().NotThrow($"{key} 的 {Locales[i]} 帶入 {args.Length} 個參數");
            }
        }
    }

    [Fact]
    public void TryGet_OnlyBuiltinLocales()
    {
        BuiltinServerText.TryGet("ja", MessageText.Forbidden, out _).Should().BeFalse();
        BuiltinServerText.TryGet("en-US", MessageText.Forbidden, out var en).Should().BeTrue();
        en.Should().Be(Baseline("message-report.json")[MessageText.Forbidden]["en-US"]);
        BuiltinServerText.TryGet("zh-TW", "NoSuchKey", out _).Should().BeFalse();
        BuiltinServerText.BaseKeys.Should().BeInAscendingOrder(StringComparer.Ordinal).And.HaveCount(BuiltinServerText.Table.Count);
    }
}
