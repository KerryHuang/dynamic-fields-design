using System.Text.Json;
using System.Text.RegularExpressions;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Localization;
using DynamicModules.Application.Dynamic.LocaleService.Packs;
using DynamicModules.Application.Dynamic.LocaleService.Queries.GetLocaleTemplate;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Localization;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic.LocalePacks;

/// <summary>JSON 翻譯樣板（i4d2 計畫 Task 3、判斷 9、10）：純函式，直接建 <see cref="LocaleWorld"/>。</summary>
public class LocaleTemplateBuilderTests
{
    private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>();
    private static readonly IReadOnlyDictionary<string, LocalePack> NoRows = new Dictionary<string, LocalePack>();
    private static IReadOnlyList<string> UiKeys => BuiltinLocaleResources.BaseKeys;
    private static IReadOnlyList<string> ServerKeys => BuiltinServerText.BaseKeys;

    private static LocaleWorld World(IEnumerable<(string Code, string? Fallback)>? extra = null,
        Dictionary<string, (IReadOnlyDictionary<string, string> Ui, IReadOnlyDictionary<string, string> Server)>? overlays = null)
    {
        var installed = BuiltinLocales.All.Select(b => new InstalledLocaleInfo(b.Code, b.NativeName, b.QuasarLang, null, true))
            .Concat((extra ?? []).Select(e => new InstalledLocaleInfo(e.Code, e.Code + "-name", null, e.Fallback, false))).ToList();
        var o = (overlays ?? []).ToDictionary(kv => kv.Key, kv => new LocaleOverlay(kv.Value.Ui, kv.Value.Server, 1), StringComparer.Ordinal);
        return new LocaleWorld(installed, o, "zh-TW", ["zh-TW", "en-US", "th", "vi"], ["zh-TW"]);
    }

    private static LocaleTemplateOptions Opt(string code, string[]? scopes = null, bool onlyMissing = false, bool onlyBaseChanged = false,
        string source = "zh-TW", string reference = "en-US", string? nativeName = null, string? fallback = null, string? quasar = null) =>
        new(code, scopes ?? ["ui", "server"], onlyMissing, onlyBaseChanged, source, reference, nativeName, fallback, quasar);

    private static JsonElement Build(LocaleWorld world, LocaleTemplateOptions options, IReadOnlyDictionary<string, LocalePack>? rows = null,
        string? platform = null)
    {
        LocaleTemplateBuilder.Validate(world, options).Should().BeNull();
        var (_, bytes) = LocaleTemplateBuilder.Build(world, rows ?? NoRows, options, platform);
        return JsonDocument.Parse(bytes).RootElement.Clone();
    }

    private static Dictionary<string, string> Section(JsonElement root, string name) =>
        root.GetProperty(name).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);

    private static Dictionary<string, string> D(params (string K, string V)[] items) => items.ToDictionary(i => i.K, i => i.V, StringComparer.Ordinal);

    private static IReadOnlyList<string> PlainUiKeys(int take) => UiKeys
        .Where(k => BuiltinLocaleResources.UiPlaceholderNames.GetValueOrDefault(k) is not { Count: > 0 } && !BuiltinLocaleResources.Ui["zh-TW"][k].Contains('{'))
        .Take(take).ToList();

    private static LocalePack Row(string code, IReadOnlyDictionary<string, string>? ui = null, IReadOnlyDictionary<string, string>? server = null,
        IEnumerable<string>? staleUi = null) => new()
    {
        Code = code, IsCurrent = true, UiJson = JsonSerializer.Serialize(ui ?? Empty), ServerJson = JsonSerializer.Serialize(server ?? Empty),
        BaseHashesJson = JsonSerializer.Serialize(new { ui = (staleUi ?? []).ToDictionary(k => k, _ => "0000000000000000"), server = new Dictionary<string, string>() }),
    };

    private static string Zh(string key) => BuiltinServerText.TryGet("zh-TW", key, out var zh) ? zh : "";

    private static string ServerKey(params int[] args) => ServerKeys.First(k =>
        Regex.Matches(Zh(k), @"(?<!\{)\{(\d+)\}").Select(m => int.Parse(m.Groups[1].Value)).ToHashSet().SetEquals(args) && !Zh(k).Contains("{{"));

    // ── 內容與篩選 ──

    [Fact]
    public void Builtin_Full_AllKeysOwnText()
    {
        var world = World(overlays: new() { ["th"] = (D(("menu.home", "หน้าแรก (แก้)")), Empty) });
        var root = Build(world, Opt("th"));
        root.GetProperty("format").GetString().Should().Be("formwright-langpack");
        root.GetProperty("formatVersion").GetInt32().Should().Be(1);
        root.GetProperty("ui").EnumerateObject().Select(p => p.Name).Should().Equal(UiKeys.Order(StringComparer.Ordinal));
        root.GetProperty("server").EnumerateObject().Select(p => p.Name).Should().Equal(ServerKeys.Order(StringComparer.Ordinal));
        var ui = Section(root, "ui");
        ui["menu.home"].Should().Be("หน้าแรก (แก้)");
        ui["dynamic.save"].Should().Be(BuiltinLocaleResources.Ui["th"]["dynamic.save"]);
        BuiltinServerText.TryGet("th", "Forbidden", out var forbidden);
        Section(root, "server")["Forbidden"].Should().Be(forbidden);
        var locale = root.GetProperty("locale");
        (locale.GetProperty("code").GetString(), locale.GetProperty("nativeName").GetString()).Should().Be(("th", "ไทย"));
        root.TryGetProperty("review", out _).Should().BeFalse("沒有目前版");
    }

    [Fact]
    public void OnlyMissing_OnlyBaseChanged_Union()
    {
        var thirty = PlainUiKeys(30).ToDictionary(k => k, k => "（日）" + k, StringComparer.Ordinal);
        var world = World([("ja", "en-US")], new() { ["ja"] = (thirty, Empty) });
        var stale = thirty.Keys.Take(5).ToList();
        var rows = new Dictionary<string, LocalePack> { ["ja"] = Row("ja", thirty, staleUi: stale) };

        var missing = Section(Build(world, Opt("ja", ["ui"], onlyMissing: true), rows), "ui");
        missing.Keys.Should().BeEquivalentTo(UiKeys.Except(thirty.Keys));
        missing.Values.Should().OnlyContain(v => v == "");

        var changed = Section(Build(world, Opt("ja", ["ui"], onlyBaseChanged: true), rows), "ui");
        changed.Should().Equal(stale.ToDictionary(k => k, k => thirty[k]));

        var both = Section(Build(world, Opt("ja", ["ui"], onlyMissing: true, onlyBaseChanged: true), rows), "ui");
        both.Keys.Should().BeEquivalentTo(UiKeys.Except(thirty.Keys).Concat(stale));
    }

    [Fact]
    public void OnlyBaseChanged_MoreThanListed()
    {
        var sixty = PlainUiKeys(60).ToDictionary(k => k, k => "（日）" + k, StringComparer.Ordinal);
        var mismatch = ServerKey(0);
        var server = D((mismatch, "{0} {1}"));
        var world = World([("ja", "en-US")], new() { ["ja"] = (sixty, server) });
        var rows = new Dictionary<string, LocalePack> { ["ja"] = Row("ja", sixty, server, sixty.Keys) };
        var root = Build(world, Opt("ja", onlyBaseChanged: true), rows);
        Section(root, "ui").Keys.Should().HaveCount(60).And.BeEquivalentTo(sixty.Keys);
        Section(root, "server").Keys.Should().Equal(mismatch);
        root.GetProperty("review").GetProperty("baseChanged").GetProperty("ui").GetArrayLength().Should().Be(60, "樣板的檢視段是完整清單");
    }

    [Fact]
    public void SourceAndReference()
    {
        var world = World([("ja", "en-US")], new()
        {
            ["en-US"] = (D(("menu.home", "Home (edited)")), Empty),
            ["ja"] = (D(("menu.home", "ホーム")), Empty),
        });
        var root = Build(world, Opt("th"));
        var source = root.GetProperty("source");
        source.GetProperty("locale").GetString().Should().Be("zh-TW");
        Section(source, "ui")["dynamic.save"].Should().Be(BuiltinLocaleResources.Ui["zh-TW"]["dynamic.save"]);
        var reference = root.GetProperty("reference");
        reference.GetProperty("locale").GetString().Should().Be("en-US");
        Section(reference, "ui")["dynamic.save"].Should().Be(BuiltinLocaleResources.Ui["en-US"]["dynamic.save"], "預設語系是繁中時參考仍是英文");
        Section(reference, "ui")["menu.home"].Should().Be("Home (edited)");
        BuiltinServerText.TryGet("en-US", "Forbidden", out var en);
        Section(reference, "server")["Forbidden"].Should().Be(en);
        root.GetProperty("pack").GetProperty("baseLocale").GetString().Should().Be("zh-TW");

        Build(world, Opt("th", reference: "")).TryGetProperty("reference", out _).Should().BeFalse();

        var fromJa = Build(world, Opt("th", source: "ja"));
        Section(fromJa.GetProperty("source"), "ui")["menu.home"].Should().Be("ホーム");
        Section(fromJa.GetProperty("source"), "ui")["dynamic.save"].Should().Be(BuiltinLocaleResources.Ui["en-US"]["dynamic.save"], "ja 缺的句子沿 ja 的鏈（上層 en-US）");
        fromJa.GetProperty("pack").GetProperty("baseLocale").GetString().Should().Be("ja");
    }

    [Fact]
    public void ScopeUiOnly()
    {
        var root = Build(World(), Opt("th", ["ui"]));
        root.TryGetProperty("server", out _).Should().BeFalse();
        root.GetProperty("source").TryGetProperty("server", out _).Should().BeFalse();
        root.GetProperty("reference").TryGetProperty("server", out _).Should().BeFalse();
        root.GetProperty("source").GetProperty("ui").EnumerateObject().Should().HaveCount(UiKeys.Count);
    }

    [Fact]
    public void Review_OrphanedTexts()
    {
        var ui = D(("menu.home", "หน้าแรก!"), ("zz.oldKey", "ข้อความเก่า"));
        var world = World(overlays: new() { ["th"] = (D(("menu.home", "หน้าแรก!")), Empty) });
        var root = Build(world, Opt("th"), new Dictionary<string, LocalePack> { ["th"] = Row("th", ui) });
        var review = root.GetProperty("review");
        Section(review.GetProperty("orphaned"), "ui").Should().Equal(D(("zz.oldKey", "ข้อความเก่า")));
        Section(review.GetProperty("orphaned"), "server").Should().BeEmpty();
        review.GetProperty("baseChanged").GetProperty("ui").GetArrayLength().Should().Be(0);
        review.GetProperty("runtimeFallback").GetProperty("server").GetArrayLength().Should().Be(0);
        Section(root, "ui").Should().NotContainKey("zz.oldKey");
    }

    [Fact]
    public void NewCode()
    {
        var root = Build(World(), Opt("id", nativeName: "Bahasa Indonesia", fallback: "en-US", quasar: "id"));
        var locale = root.GetProperty("locale");
        (locale.GetProperty("code").GetString(), locale.GetProperty("nativeName").GetString(), locale.GetProperty("fallback").GetString(),
            locale.GetProperty("quasarLang").GetString()).Should().Be(("id", "Bahasa Indonesia", "en-US", "id"));
        Section(root, "ui").Should().HaveCount(UiKeys.Count).And.OnlyContain(kv => kv.Value == "");
        Section(root, "server").Should().HaveCount(ServerKeys.Count).And.OnlyContain(kv => kv.Value == "");
        root.TryGetProperty("review", out _).Should().BeFalse();
        LocaleTemplateBuilder.Build(World(), NoRows, Opt("ID", nativeName: "Bahasa Indonesia"), null).FileName.Should().Be("id.langpack.json");
    }

    [Fact]
    public void BuiltFor()
    {
        var platform = "2026.11.21-build.123456";
        platform.Length.Should().BeGreaterThan(20);
        Build(World(), Opt("th"), platform: platform).GetProperty("pack").GetProperty("builtFor").GetString().Should().Be(platform[..20]);
        Build(World(), Opt("th"), platform: null).GetProperty("pack").GetProperty("builtFor").GetString().Should().Be("dev");
    }

    // ── 驗證 ──

    private static string? Err(LocaleWorld world, LocaleTemplateOptions options) => LocaleTemplateBuilder.Validate(world, options)?.Resolve("zh-TW");

    [Fact]
    public void Validate()
    {
        var world = World([("xa", "en-US"), ("xb", "xa"), ("xc", "xb")]);
        Err(world, Opt("JA_x")).Should().Be("語系代碼「JA_x」格式不對（例：ja、zh-Hant-HK）");
        Err(world, Opt("id")).Should().Be("新語系要填自稱（locale.nativeName）");
        Err(world, Opt("id", nativeName: new string('a', 51))).Should().Be("locale.nativeName 不能超過 50 字，也不能含控制字元");
        Err(world, Opt("id", nativeName: "Bahasa", quasar: "xx-YY")).Should().Be("找不到日期與表格元件的語言包「xx-YY」");
        Err(world, Opt("id", nativeName: "Bahasa", fallback: "ko")).Should().Be("上層語系「ko」還沒安裝");
        Err(world, Opt("id", nativeName: "Bahasa", fallback: "id")).Should().Be("上層語系不能是自己");
        Err(world, Opt("id", nativeName: "Bahasa", fallback: "xc")).Should().Be("上層語系最多 3 層");
        Err(world, Opt("id", nativeName: "Bahasa", fallback: "xb")).Should().BeNull();
        Err(world, Opt("th", source: "ko")).Should().Be("語系 ko 沒有安裝");
        Err(world, Opt("th", reference: "ko")).Should().Be("語系 ko 沒有安裝");
        Err(world, Opt("th", [])).Should().Be("匯出範圍只能是 ui、server，至少要選一個");
        Err(world, Opt("th", ["ui", "designData"])).Should().Be("匯出範圍只能是 ui、server，至少要選一個");
        Err(world, Opt("th", reference: "")).Should().BeNull();
        Err(world, Opt("th", nativeName: new string('a', 51))).Should().BeNull("已安裝的語系不看 locale 欄位");
    }

    // ── 往返 ──

    private static IEnumerable<(LocaleWorld World, LocaleTemplateOptions Options, IReadOnlyDictionary<string, LocalePack> Rows)> CleanCases()
    {
        var thirty = PlainUiKeys(30).ToDictionary(k => k, k => "（日）" + k, StringComparer.Ordinal);
        var ja = World([("ja", "en-US")], new() { ["ja"] = (thirty, Empty), ["th"] = (D(("menu.home", "หน้าแรก!")), Empty) });
        var rows = new Dictionary<string, LocalePack> { ["ja"] = Row("ja", thirty, staleUi: thirty.Keys.Take(5)) };
        yield return (ja, Opt("th"), NoRows);
        yield return (ja, Opt("ja"), rows);
        yield return (ja, Opt("ja", onlyMissing: true), rows);
        yield return (ja, Opt("ja", onlyBaseChanged: true), rows);
        yield return (ja, Opt("ja", ["server"], reference: ""), rows);
        yield return (ja, Opt("en-US", source: "ja"), rows);
        yield return (ja, Opt("id", nativeName: "Bahasa Indonesia", fallback: "en-US"), rows);
    }

    private static ParsedLocalePack Parse(LocaleWorld world, LocaleTemplateOptions options, IReadOnlyDictionary<string, LocalePack> rows)
    {
        var (_, bytes) = LocaleTemplateBuilder.Build(world, rows, options, null);
        var (pack, error) = LocalePackParser.Parse(bytes.AsMemory());
        error.Should().BeNull(error?.Resolve("zh-TW"));
        pack!.UiProblems.Should().BeEmpty();
        pack.ServerProblems.Should().BeEmpty();
        return pack;
    }

    [Fact]
    public void RoundTrip_Parses()
    {
        foreach (var (world, options, rows) in CleanCases()) Parse(world, options, rows).Code.Should().Be(options.Code);
    }

    [Fact]
    public void RoundTrip_Analyzes_Clean()
    {
        foreach (var (world, options, rows) in CleanCases())
        {
            var a = LocalePackAnalyzer.Analyze(Parse(world, options, rows), world, 10, null);
            a.Error.Should().BeNull(options.ToString());
            (a.Report!.Ui.Rejected, a.Report.Server.Rejected, a.Report.Ui.Extra, a.Report.Server.Extra).Should().Be((0, 0, 0, 0), options.ToString());
        }
    }

    [Fact]
    public void RoundTrip_Analyzes_ParamsMismatch_RejectedAsExpected()
    {
        var tooMany = ServerKey(0);
        var tooFew = ServerKey(0, 1);
        var server = D((tooMany, "{0} และ {1}"), (tooFew, "เฉพาะ {0}"));
        var world = World(overlays: new() { ["th"] = (Empty, server) });
        var rows = new Dictionary<string, LocalePack> { ["th"] = Row("th", server: server) };
        var a = LocalePackAnalyzer.Analyze(Parse(world, Opt("th"), rows), world, 10, null);
        a.Report!.Server.Rejected.Should().Be(2);
        a.Report.Server.RejectedItems.Select(r => r.Key).Should().BeEquivalentTo(tooMany, tooFew);
        a.Report.Server.RejectedItems.Should().OnlyContain(r => r.Reason == LocalePackReasons.Placeholder);
        (a.Report.Ui.Rejected, a.Report.Ui.Extra, a.Report.Server.Extra).Should().Be((0, 0, 0));
    }
}
