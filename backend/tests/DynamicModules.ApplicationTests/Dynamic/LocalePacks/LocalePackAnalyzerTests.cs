using System.Text;
using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Localization;
using DynamicModules.Application.Dynamic.LocaleService.Packs;
using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.ApplicationTests.Common;
using DynamicModules.Domain.Localization;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic.LocalePacks;

/// <summary>語系包分析與重驗（語系包 i4d1 計畫 Task 5、判斷 6、13、16、25；票 #68）：直接建 <see cref="LocaleWorld"/>，不需要 registry 與政策。</summary>
public class LocalePackAnalyzerTests
{
    private const int Max = 10;
    private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>();

    /// <summary>內建四語加上 <paramref name="extra"/>（代碼、上層）；<paramref name="overlays"/> 是語系 → （介面、後端）覆寫層。</summary>
    private static LocaleWorld World(IEnumerable<(string Code, string? Fallback)>? extra = null,
        Dictionary<string, (IReadOnlyDictionary<string, string> Ui, IReadOnlyDictionary<string, string> Server)>? overlays = null)
    {
        var installed = BuiltinLocales.All.Select(b => new InstalledLocaleInfo(b.Code, b.NativeName, b.QuasarLang, null, true))
            .Concat((extra ?? []).Select(e => new InstalledLocaleInfo(e.Code, e.Code + "-name", null, e.Fallback, false))).ToList();
        var o = (overlays ?? []).ToDictionary(kv => kv.Key, kv => new LocaleOverlay(kv.Value.Ui, kv.Value.Server, 1), StringComparer.Ordinal);
        return new LocaleWorld(installed, o, "zh-TW", ["zh-TW", "en-US", "th", "vi"], ["zh-TW"]);
    }

    private static IReadOnlyDictionary<string, string> D(params (string K, string V)[] items) => items.ToDictionary(i => i.K, i => i.V, StringComparer.Ordinal);

    private static ParsedLocalePack Parse(string json) => LocalePackParser.Parse(Encoding.UTF8.GetBytes(json).AsMemory()).Pack!;

    private static ParsedLocalePack Fixture(string name) => LocalePackParser.Parse(File.ReadAllBytes(Path.Combine(
        BuiltinServerTextTests.RepoRoot(), "backend", "tests", "Fixtures", "langpacks", name)).AsMemory()).Pack!;

    private static ParsedLocalePack Pack(string code, string? fallback = null, IReadOnlyDictionary<string, string>? ui = null,
        IReadOnlyDictionary<string, string>? server = null, string? nativeName = "名") =>
        new(code, nativeName, null, fallback, null, null, null, null, ui ?? Empty, [], server ?? Empty, [], 0);

    private static LocalePackAnalysis Ok(ParsedLocalePack pack, LocaleWorld world, int max = Max)
    {
        var a = LocalePackAnalyzer.Analyze(pack, world, max, platformVersion: null);
        a.Error.Should().BeNull(a.Error?.Resolve("zh-TW"));
        return a;
    }

    private static string Err(ParsedLocalePack pack, LocaleWorld world, int max = Max)
    {
        var a = LocalePackAnalyzer.Analyze(pack, world, max, platformVersion: null);
        a.Plan.Should().BeNull();
        return a.Error!.Resolve("zh-TW");
    }

    // ── 共用案例（票 #68）──

    private static readonly JsonElement Cases = JsonDocument.Parse(File.ReadAllText(Path.Combine(BuiltinServerTextTests.RepoRoot(), "shared", "i18n-message-cases.json")))
        .RootElement.GetProperty("boolean");

    public static TheoryData<string> BooleanCases()
    {
        var data = new TheoryData<string>();
        foreach (var c in Cases.EnumerateArray()) data.Add(c.GetProperty("name").GetString()!);
        return data;
    }

    private static string? Str(JsonElement e, string name) => e.GetProperty(name).ValueKind == JsonValueKind.Null ? null : e.GetProperty(name).GetString();

    [Theory]
    [MemberData(nameof(BooleanCases))]
    public void SharedBooleanCases(string name)
    {
        var c = Cases.EnumerateArray().Single(x => x.GetProperty("name").GetString() == name);
        var extra = new List<(string, string?)>();
        var overlays = new Dictionary<string, (IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>)>();
        foreach (var i in c.GetProperty("installed").EnumerateArray())
        {
            var code = Str(i, "code")!;
            if (!BuiltinLocales.Contains(code)) extra.Add((code, Str(i, "fallback")));
            overlays[code] = (Empty, D((ReportText.Yes, Str(i, "yes")!), (ReportText.No, Str(i, "no")!)));
        }
        var p = c.GetProperty("pack");
        var server = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Str(p, "yes") is { } yes) server[ReportText.Yes] = yes;
        if (Str(p, "no") is { } no) server[ReportText.No] = no;

        var a = Ok(Pack(Str(p, "code")!, Str(p, "fallback"), server: server), World(extra, overlays));

        var expected = c.GetProperty("expectedRejected").EnumerateArray().Select(x => x.GetString()!).ToList();
        a.Plan!.BooleanRejected.Should().BeEquivalentTo(expected);
        a.Report!.Server.RejectedItems.Where(r => r.Reason == LocalePackReasons.BooleanCollision).Select(r => r.Key).Should().BeEquivalentTo(expected);
        a.Plan.ServerSet.Keys.Should().NotIntersectWith(expected);
        a.Report.Warnings.Should().HaveCount(c.GetProperty("expectedWarnings").GetInt32());
    }

    // ── 重驗（判斷 16 第 3 步）──

    [Fact]
    public void Recheck_GraphAndCount()
    {
        var preview = World([("ja", "en-US"), ("ko", null)]);
        var plan = Ok(Pack("ja", "ko"), preview).Plan!;
        LocalePackAnalyzer.Recheck(plan, preview, Max).Invalid.Should().BeNull();

        LocalePackAnalyzer.Recheck(plan, World([("ja", "en-US")]), Max).Invalid!.Resolve("zh-TW").Should().Be("上層語系「ko」還沒安裝");
        LocalePackAnalyzer.Recheck(plan, World([("ja", "en-US"), ("ko", "ja")]), Max).Invalid!.Resolve("zh-TW").Should().StartWith("上層語系形成循環：ja → ko → ja");

        var newPlan = Ok(Pack("de"), preview).Plan!;
        LocalePackAnalyzer.Recheck(newPlan, World([("ja", null), ("ko", null)]), 6).Invalid!.Resolve("zh-TW").Should().Be("最多只能安裝 6 個語系（含內建）");
        LocalePackAnalyzer.Recheck(plan, preview, 6).Invalid.Should().BeNull("既有語系更新不受 A39 影響");
    }

    [Fact]
    public void Recheck_BooleanChanged()
    {
        var preview = World();
        var plan = Ok(Pack("ja", "en-US", server: D((ReportText.Yes, "はい"), (ReportText.No, "いいえ"))), preview).Plan!;
        plan.BooleanRejected.Should().BeEmpty();
        LocalePackAnalyzer.Recheck(plan, preview, Max).BooleanChanged.Should().BeFalse();

        var later = World([("ko", null)], new() { ["ko"] = (Empty, D((ReportText.Yes, "예"), (ReportText.No, "はい"))) });
        LocalePackAnalyzer.Recheck(plan, later, Max).Should().Be(new LocalePackRecheck(null, true));
    }

    private static readonly LocaleWorld KoNoHai = World([("ko", null)], new() { ["ko"] = (Empty, D((ReportText.Yes, "예"), (ReportText.No, "はい"))) });

    [Fact]
    public void Recheck_AlreadyRejected_WorldUnchanged()
    {
        var plan = Ok(Pack("ja", "en-US", server: D((ReportText.Yes, "はい"))), KoNoHai).Plan!;
        plan.BooleanRejected.Should().Equal(ReportText.Yes);
        plan.ServerSet.Should().NotContainKey(ReportText.Yes);
        plan.BooleanCandidates[ReportText.Yes].Should().Be(new BooleanCandidate("はい", false));
        LocalePackAnalyzer.Recheck(plan, KoNoHai, Max).BooleanChanged.Should().BeFalse();
    }

    [Fact]
    public void Recheck_OriginalCollisionGone()
    {
        var plan = Ok(Pack("ja", "en-US", server: D((ReportText.Yes, "はい"))), KoNoHai).Plan!;
        LocalePackAnalyzer.Recheck(plan, World(), Max).BooleanChanged.Should().BeTrue();
        var changed = World([("ko", null)], new() { ["ko"] = (Empty, D((ReportText.Yes, "예"), (ReportText.No, "아니요"))) });
        LocalePackAnalyzer.Recheck(plan, changed, Max).BooleanChanged.Should().BeTrue();
    }

    [Fact]
    public void Recheck_RemoveIntent()
    {
        BuiltinServerText.TryGet("th", ReportText.No, out var thNo);
        var preview = World(overlays: new() { ["th"] = (Empty, D((ReportText.No, "ไม่"))) });
        var plan = Ok(Pack("th", server: D((ReportText.No, thNo!))), preview).Plan!;
        plan.BooleanCandidates[ReportText.No].Should().Be(new BooleanCandidate(thNo!, true));
        plan.ServerRemove.Should().Equal(ReportText.No);
        LocalePackAnalyzer.Recheck(plan, preview, Max).BooleanChanged.Should().BeFalse();

        var later = World([("ja", null)], new() { ["th"] = (Empty, D((ReportText.No, "ไม่"))), ["ja"] = (Empty, D((ReportText.Yes, thNo!))) });
        LocalePackAnalyzer.Recheck(plan, later, Max).BooleanChanged.Should().BeTrue();
    }

    // ── 計數、合併、拒收 ──

    [Fact]
    public void NewLocale_Counts()
    {
        var a = Ok(Fixture("ja-partial.langpack.json"), World());
        a.Report!.Locale.IsNew.Should().BeTrue();
        a.Report.Locale.FallbackCode.Should().Be("en-US");
        a.Report.Ui.Added.Should().Be(30);
        a.Report.Ui.Rejected.Should().Be(0);
        a.Report.Server.Added.Should().Be(5);
        a.Report.Completeness.Ui.Should().Be(new CompletenessCount(30, BuiltinLocaleResources.BaseKeys.Count));
        a.Report.Completeness.Server.Should().Be(new CompletenessCount(5, BuiltinServerText.BaseKeys.Count));
        a.Plan!.Locale.Should().Be(new LocalePackLocale("日本語", null, "en-US"));
        a.Plan.UiSet.Should().HaveCount(30);
        a.Plan.NoChanges.Should().BeFalse();
    }

    [Fact]
    public void Merge_U5()
    {
        var keys = BuiltinLocaleResources.BaseKeys;
        var old = keys.Take(30).ToDictionary(k => k, k => "舊" + k, StringComparer.Ordinal);
        var world = World([("ja", "en-US")], new() { ["ja"] = (old, Empty) });
        var file = keys.Skip(30).Take(17).ToDictionary(k => k, k => "新" + k, StringComparer.Ordinal);
        file[keys[0]] = old[keys[0]];
        file[keys[1]] = old[keys[1]];
        file[keys[2]] = "";
        var a = Ok(Pack("ja", ui: file, nativeName: null), world);
        (a.Report!.Ui.Added, a.Report.Ui.Unchanged, a.Report.Ui.Skipped, a.Report.Ui.Changed).Should().Be((17, 2, 1, 0));
        LocalePackAnalyzer.After(world, a.Plan!).Overlays["ja"].Ui.Should().HaveCount(47);
        a.Plan!.Locale.Should().BeNull("沒改語系欄位");
    }

    [Fact]
    public void SameAsBuiltin_NotStored()
    {
        var th = BuiltinLocaleResources.Ui["th"];
        var a = Ok(Fixture("th-fix.langpack.json"), World());
        a.Report!.Ui.SameAsBuiltin.Should().Be(1);
        a.Report.Ui.Added.Should().Be(2);
        a.Plan!.UiSet.Should().HaveCount(2).And.NotContainKey("menu.custom");
        a.Plan.UiRemove.Should().BeEmpty();

        var before = World(overlays: new() { ["th"] = (D(("menu.custom", "舊覆寫")), Empty) });
        var b = Ok(Fixture("th-fix.langpack.json"), before);
        b.Plan!.UiRemove.Should().Equal("menu.custom");
        th["menu.custom"].Should().NotBe("舊覆寫");
    }

    [Fact]
    public void Rejected_ReasonsAndFirst50()
    {
        var a = Ok(Fixture("bad-mixed.langpack.json"), World());
        a.Report!.Ui.RejectedItems.Concat(a.Report.Server.RejectedItems).Select(r => r.Reason).Should().BeEquivalentTo(new[]
        {
            LocalePackReasons.Syntax, LocalePackReasons.Placeholder, LocalePackReasons.Control, LocalePackReasons.TooLong,
            LocalePackReasons.NotString, LocalePackReasons.Duplicate, LocalePackReasons.FormatClause, LocalePackReasons.BooleanCollision,
        });
        a.Report.Ui.Skipped.Should().Be(1);
        a.Report.Ui.Added.Should().Be(1);
        a.Report.Server.Added.Should().Be(1);
        a.Report.Ui.RejectedItems.Single(r => r.Reason == LocalePackReasons.Placeholder).Args.Should().BeEquivalentTo("{count}", "{first}", "{rest}");

        var many = BuiltinLocaleResources.BaseKeys.Take(60).ToDictionary(k => k, _ => "{zzz}", StringComparer.Ordinal);
        var b = Ok(Pack("ja", "en-US", ui: many), World());
        b.Report!.Ui.Rejected.Should().Be(60);
        b.Report.Ui.RejectedItems.Should().HaveCount(LocalePackLimits.MaxListedItems);
    }

    [Fact]
    public void Extra_Dropped()
    {
        var a = Ok(Pack("ja", "en-US", ui: D(("no.such.key", "x"), ("menu.home", "ホーム")), server: D(("NoSuchKey", "y"))), World());
        a.Report!.Ui.Extra.Should().Be(1);
        a.Report.Ui.ExtraKeys.Should().Equal("no.such.key");
        a.Report.Server.Extra.Should().Be(1);
        a.Plan!.UiSet.Keys.Should().Equal("menu.home");
        a.Plan.ServerSet.Should().BeEmpty();
    }

    [Fact]
    public void Placeholder_UsesUnion()
    {
        Ok(Pack("ja", "en-US", ui: D(("approval.stagesMore", "{first} ほか {rest}"))), World()).Report!.Ui.Added.Should().Be(1);
        Ok(Pack("ja", "en-US", ui: D(("approval.stagesMore", "{total} 件"))), World()).Report!.Ui.RejectedItems.Single().Reason
            .Should().Be(LocalePackReasons.Placeholder);
    }

    [Fact]
    public void Server_Placeholder_MatchesZh()
    {
        var a = Ok(Pack("ja", "en-US", server: D(("Forbidden", "権限 {0}"), (ReportText.Yes, "<script>はい</script>"), (ReportText.No, "=いいえ"))), World());
        a.Report!.Server.RejectedItems.Should().ContainSingle().Which.Should().Be(a.Report.Server.RejectedItems[0]);
        a.Report.Server.RejectedItems[0].Key.Should().Be("Forbidden");
        a.Report.Server.RejectedItems[0].Reason.Should().Be(LocalePackReasons.Placeholder);
        a.Plan!.ServerSet.Keys.Should().BeEquivalentTo(ReportText.Yes, ReportText.No);
    }

    [Fact]
    public void Builtin_LocaleFieldsIgnored()
    {
        var a = Ok(Parse("{\"format\":\"formwright-langpack\",\"formatVersion\":1,\"locale\":{\"code\":\"th\",\"nativeName\":\"ภาษาไทย\",\"fallback\":\"en-US\"}}"), World());
        a.Report!.Locale.Ignored.Should().Equal("nativeName", "fallback");
        a.Report.Locale.Builtin.Should().BeTrue();
        a.Report.Locale.NativeName.Should().Be("ไทย");
        a.Plan!.Locale.Should().BeNull();
        a.Plan.NoChanges.Should().BeTrue();
    }

    [Fact]
    public void Fallback_Invalid()
    {
        Err(Pack("ja", "ko"), World()).Should().Be("上層語系「ko」還沒安裝");
        Err(Pack("ja", "ja"), World([("ja", "en-US")])).Should().Be("上層語系不能是自己");
        Err(Pack("ja", "ko"), World([("ja", "en-US"), ("ko", "ja")])).Should().Be("上層語系形成循環：ja → ko → ja");
        Err(Pack("ja", "x1"), World([("x1", "x2"), ("x2", "x3"), ("x3", "en-US")])).Should().Be("上層語系最多 3 層");
        Err(Pack("ja", "en-US", nativeName: null), World()).Should().Be("新語系要填自稱（locale.nativeName）");
    }

    [Fact]
    public void MaxInstalled_BlocksNewOnly()
    {
        var world = World([("ja", null), ("ko", null), ("de", null), ("fr", null), ("es", null), ("it", null)]);
        world.Installed.Should().HaveCount(10);
        Err(Pack("pt"), world).Should().Be("最多只能安裝 10 個語系（含內建）");
        Ok(Pack("ja", ui: D(("menu.home", "ホーム"))), world).Plan.Should().NotBeNull();
    }

    [Fact]
    public void BuiltFor_ShownNotCompared()
    {
        var a = Ok(Fixture("ja-partial.langpack.json"), World());
        a.Report!.Pack.BuiltFor.Should().Be("2026.11");
        a.Report.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void DesignData_CountedNotApplied()
    {
        var a = Ok(Parse("{\"format\":\"formwright-langpack\",\"formatVersion\":1,\"locale\":{\"code\":\"th\"},\"designData\":{\"a\":{},\"b\":{},\"c\":{}}}"), World());
        a.Report!.DesignData.Count.Should().Be(3);
        a.Plan!.NoChanges.Should().BeTrue();
    }

    [Fact]
    public void EmptyPack_NoChanges()
    {
        var a = Ok(Pack("th", nativeName: null), World());
        var r = a.Report!;
        (r.Ui.Added, r.Ui.Changed, r.Ui.Unchanged, r.Ui.Skipped, r.Ui.Rejected, r.Server.Added, r.Server.Rejected).Should().Be((0, 0, 0, 0, 0, 0, 0));
        a.Plan!.NoChanges.Should().BeTrue();
    }

    [Fact]
    public void Plan_RoundTripsThroughJson()
    {
        var plan = Ok(Pack("ja", "en-US", server: D((ReportText.Yes, "はい"))), KoNoHai).Plan!;
        var back = JsonSerializer.Deserialize<LocalePackPlan>(JsonSerializer.Serialize(plan))!;
        back.BooleanRejected.Should().Equal(plan.BooleanRejected);
        back.BooleanCandidates.Should().BeEquivalentTo(plan.BooleanCandidates);
        back.Locale.Should().Be(plan.Locale);
        LocalePackAnalyzer.Recheck(back, KoNoHai, Max).BooleanChanged.Should().BeFalse();
    }

    // ── i4d2：平台版本比對、原文已更新的確認、缺最多的分類（i4d2 計畫 Task 1）──

    /// <summary>沒有佔位符的介面文字基準鍵（值可以是任意文字）。</summary>
    private static IReadOnlyList<string> PlainUiKeys(int take) => BuiltinLocaleResources.BaseKeys
        .Where(k => BuiltinLocaleResources.UiPlaceholderNames.GetValueOrDefault(k) is not { Count: > 0 } && !BuiltinLocaleResources.Ui["zh-TW"][k].Contains('{'))
        .Take(take).ToList();

    private static ParsedLocalePack WithBuiltFor(string? builtFor, IReadOnlyDictionary<string, string> ui) =>
        new("ja", "日本語", null, "en-US", null, builtFor, null, null, ui, [], Empty, [], 0);

    /// <summary>一筆目前版：<paramref name="ui"/> 的每個鍵都記一個過期的原文雜湊。</summary>
    private static Domain.Entities.Dynamic.LocalePack StaleRow(string code, IReadOnlyDictionary<string, string> ui) => new()
    {
        Code = code, IsCurrent = true, UiJson = JsonSerializer.Serialize(ui), ServerJson = "{}",
        BaseHashesJson = JsonSerializer.Serialize(new { ui = ui.Keys.ToDictionary(k => k, _ => "0000000000000000"), server = new Dictionary<string, string>() }),
    };

    [Fact]
    public void BuiltFor_DiffersFromPlatform_Warns()
    {
        var ui = PlainUiKeys(30).ToDictionary(k => k, k => "（日）" + k, StringComparer.Ordinal);
        var a = LocalePackAnalyzer.Analyze(WithBuiltFor("2026.10", ui), World(), Max, "2026.11");
        var total = BuiltinLocaleResources.BaseKeys.Count + BuiltinServerText.BaseKeys.Count;
        a.Report!.Warnings.Should().ContainSingle(w => w.Code == LocalePackAnalyzer.WarningBuiltForDiffers)
            .Which.Args.Should().Equal("2026.10", "2026.11", (total - 30).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("dev", "2026.11")]
    [InlineData("2026.10", "dev")]
    [InlineData(null, "2026.11")]
    [InlineData("2026.10", null)]
    [InlineData("2026.10", "")]
    [InlineData("2026.11", "2026.11")]
    public void BuiltFor_Dev_NoWarning(string? builtFor, string? platform)
    {
        var a = LocalePackAnalyzer.Analyze(WithBuiltFor(builtFor, D(("menu.home", "ホーム"))), World(), Max, platform);
        a.Report!.Warnings.Should().NotContain(w => w.Code == LocalePackAnalyzer.WarningBuiltForDiffers);
    }

    [Fact]
    public void BuiltFor_LongPlatformVersion_SameBuild_NoWarning()
    {
        var platform = "2026.11.21-build.12345";
        platform.Length.Should().BeGreaterThan(LocalePackLimits.MaxPackBuiltFor);
        var a = LocalePackAnalyzer.Analyze(WithBuiltFor(platform[..LocalePackLimits.MaxPackBuiltFor], D(("menu.home", "ホーム"))), World(), Max, platform);
        a.Report!.Warnings.Should().NotContain(w => w.Code == LocalePackAnalyzer.WarningBuiltForDiffers);
    }

    [Fact]
    public void Unchanged_BaseChanged_IsConfirmed()
    {
        var current = D(("dynamic.save", "X"));
        var world = World(overlays: new() { ["th"] = (current, Empty) });
        var pack = Pack("th", ui: D(("dynamic.save", "X")), nativeName: null);

        var a = LocalePackAnalyzer.Analyze(pack, world, Max, null, LocaleOverlayReview.Of(StaleRow("th", current)));
        a.Plan!.UiConfirm.Should().Equal("dynamic.save");
        a.Report!.Ui.Confirmed.Should().Be(1);
        a.Plan.OverlayChanged.Should().BeTrue();
        a.Plan.NoChanges.Should().BeFalse();

        var fresh = new Domain.Entities.Dynamic.LocalePack { Code = "th", IsCurrent = true, UiJson = JsonSerializer.Serialize(current), BaseHashesJson = "{}" };
        var b = LocalePackAnalyzer.Analyze(pack, world, Max, null, LocaleOverlayReview.Of(fresh));
        b.Plan!.UiConfirm.Should().BeEmpty();
        b.Report!.Ui.Confirmed.Should().Be(0);
        b.Plan.OverlayChanged.Should().BeFalse();
        b.Plan.NoChanges.Should().BeTrue();
    }

    [Fact]
    public void Confirm_MoreThanListed()
    {
        var ui = PlainUiKeys(60).ToDictionary(k => k, k => "（日）" + k, StringComparer.Ordinal);
        ui.Should().HaveCount(60);
        var world = World([("ja", "en-US")], new() { ["ja"] = (ui, Empty) });
        var a = LocalePackAnalyzer.Analyze(Pack("ja", ui: ui, nativeName: null), world, Max, null, LocaleOverlayReview.Of(StaleRow("ja", ui)));
        a.Plan!.UiConfirm.Should().HaveCount(60).And.BeEquivalentTo(ui.Keys);
        a.Report!.Ui.Confirmed.Should().Be(60);
        a.Report.Ui.Unchanged.Should().Be(60);
    }

    /// <summary>測試端獨立算的「缺最多的分類」（計畫判斷 4 的規則）。</summary>
    private static List<MissingCategory> ExpectedTop(string code, LocaleOverlay? overlay)
    {
        var builtinUi = BuiltinLocaleResources.Ui.GetValueOrDefault(code);
        var ui = BuiltinLocaleResources.BaseKeys
            .Where(k => !(overlay?.Ui.GetValueOrDefault(k) is { Length: > 0 }) && !(builtinUi?.GetValueOrDefault(k) is { Length: > 0 }))
            .GroupBy(k => k.Split('.')[0]).Select(g => new MissingCategory("ui", g.Key, g.Count()));
        var server = BuiltinServerText.BaseKeys
            .Where(k => !(overlay?.Server.GetValueOrDefault(k) is { Length: > 0 }) && !(BuiltinServerText.TryGet(code, k, out var t) && t.Length > 0))
            .GroupBy(k => k.StartsWith("report.") ? "report" : k.StartsWith("alert.") ? "alert" : "message")
            .Select(g => new MissingCategory("server", g.Key, g.Count()));
        return ui.Concat(server).OrderByDescending(c => c.Missing).ThenBy(c => c.Category, StringComparer.Ordinal).ThenBy(c => c.Segment, StringComparer.Ordinal)
            .Take(LocalePackLimits.MaxMissingCategories).ToList();
    }

    [Fact]
    public void Report_TopMissing_UsesAfterWorld()
    {
        var world = World([("ja", "en-US")]);
        var biggest = BuiltinLocaleResources.BaseKeys.GroupBy(k => k.Split('.')[0]).OrderByDescending(g => g.Count()).First().ToList();
        var half = biggest.Take(biggest.Count / 2).ToDictionary(k => k, k => BuiltinLocaleResources.Ui["zh-TW"][k], StringComparer.Ordinal);
        var a = Ok(Pack("ja", ui: half, nativeName: null), world);
        a.Report!.Ui.Added.Should().Be(half.Count);

        var after = LocalePackAnalyzer.After(world, a.Plan!).Overlays["ja"];
        a.Report.Completeness.TopMissing.Should().Equal(ExpectedTop("ja", after));
        a.Report.Completeness.TopMissing.Should().NotEqual(ExpectedTop("ja", null), "用套用後的世界，不是目前的");
        a.Report.Completeness.Ui.Have.Should().Be(half.Count);
    }

    [Fact]
    public void ParamsChanged_Server_ReimportRejected()
    {
        var key = BuiltinServerText.BaseKeys.First(k => BuiltinServerText.TryGet("zh-TW", k, out var zh) && zh.Contains("{0}") && !zh.Contains("{1}"));
        var stale = D((key, "舊 {0} {1}"));
        var world = World(overlays: new() { ["th"] = (Empty, stale) });
        var row = new Domain.Entities.Dynamic.LocalePack
        {
            Code = "th", IsCurrent = true, UiJson = "{}", ServerJson = JsonSerializer.Serialize(stale),
            BaseHashesJson = JsonSerializer.Serialize(new { ui = new Dictionary<string, string>(), server = new Dictionary<string, string> { [key] = "0000000000000000" } }),
        };
        var a = LocalePackAnalyzer.Analyze(Pack("th", server: stale, nativeName: null), world, Max, null, LocaleOverlayReview.Of(row));
        a.Report!.Server.RejectedItems.Should().ContainSingle().Which.Reason.Should().Be(LocalePackReasons.Placeholder);
        a.Plan!.ServerConfirm.Should().BeEmpty();
        a.Report.Server.Confirmed.Should().Be(0);
    }
}
