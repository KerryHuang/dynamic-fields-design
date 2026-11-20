using System.Text.Json;
using System.Text.RegularExpressions;
using DynamicModules.Application.Common.Localization;
using DynamicModules.Domain.Localization;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Common;

/// <summary>內嵌的內建介面文字與啟動自檢（語系包 i4b 計畫判斷 3、4；設計 §8.1）。</summary>
public class BuiltinLocaleResourcesTests
{
    private static string RepoShared(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "shared", relative))) dir = dir.Parent;
        if (dir is null) throw new FileNotFoundException($"找不到 shared/{relative}");
        return File.ReadAllText(Path.Combine(dir.FullName, "shared", relative));
    }

    /// <summary>從內嵌資源複製一份可改的集合。</summary>
    private static (Dictionary<string, string> Ui, Dictionary<string, string> Locale, string? Quasar) Copy()
    {
        var s = BuiltinLocaleResources.Load();
        return (new Dictionary<string, string>(s.UiJson), new Dictionary<string, string>(s.LocaleJson), s.QuasarLangsJson);
    }

    private static IReadOnlyList<string> Verify(Dictionary<string, string> ui, Dictionary<string, string> locale, string? quasar, IReadOnlyList<string>? unexpected = null)
        => BuiltinLocaleResources.Verify(new BuiltinResourceSet(ui, locale, quasar, unexpected ?? []));

    /// <summary>把 th 的 ui.json 壓平後依 <paramref name="edit"/> 改寫，再存回扁平 JSON（巢狀與扁平鍵對 Flatten 結果相同）。</summary>
    private static string EditFlat(string json, Action<Dictionary<string, string>> edit)
    {
        var flat = new Dictionary<string, string>(UiTextMerge.Flatten(JsonDocument.Parse(json).RootElement));
        edit(flat);
        return JsonSerializer.Serialize(flat);
    }

    [Fact]
    public void ResourceNames_EightUnique()
    {
        var names = typeof(BuiltinLocaleResources).Assembly.GetManifestResourceNames().Select(n => n.Replace('\\', '/')).ToList();
        var i18n = names.Where(n => n.StartsWith("i18n/", StringComparison.Ordinal)).ToList();

        i18n.Should().HaveCount(8).And.OnlyHaveUniqueItems();
        i18n.Should().OnlyContain(n => Regex.IsMatch(n, @"^i18n/[A-Za-z0-9-]+/(ui|locale)\.json$"));
        i18n.Select(n => n.Split('/')[1]).Distinct().Should().BeEquivalentTo(BuiltinLocales.Codes);
        names.Should().ContainSingle(n => n == "quasar-langs.json");
    }

    [Fact]
    public void Embedded_HasFourBuiltinLocales()
    {
        var s = BuiltinLocaleResources.Load();

        s.UiJson.Keys.Should().BeEquivalentTo(BuiltinLocales.Codes);
        s.LocaleJson.Keys.Should().BeEquivalentTo(BuiltinLocales.Codes);
        s.UnexpectedResourceNames.Should().BeEmpty();
    }

    [Fact]
    public void Embedded_VerifyPasses()
        => BuiltinLocaleResources.Verify(BuiltinLocaleResources.Load()).Should().BeEmpty();

    [Fact]
    public void Embedded_KeyCountMatchesRepo()
    {
        var repoKeys = UiTextMerge.Flatten(JsonDocument.Parse(RepoShared("i18n/zh-TW/ui.json")).RootElement).Count;

        BuiltinLocaleResources.BaseKeys.Should().HaveCount(repoKeys).And.BeInAscendingOrder(StringComparer.Ordinal);
        BuiltinLocaleResources.Ui["th"].Should().HaveCount(repoKeys);
        BuiltinLocaleResources.Ui["th"]["menu.home"].Should().NotBe(BuiltinLocaleResources.Ui["zh-TW"]["menu.home"]);
    }

    [Fact]
    public void Verify_UnexpectedResourceName_Reported()
    {
        var (ui, locale, quasar) = Copy();

        Verify(ui, locale, quasar, ["i18n/zh-TW/x/ui.json"]).Should().ContainSingle().Which.Should().Contain("i18n/zh-TW/x/ui.json");
    }

    [Fact]
    public void Verify_MissingLocale_Reported()
    {
        var (ui, locale, quasar) = Copy();
        ui.Remove("vi");

        Verify(ui, locale, quasar).Should().ContainSingle().Which.Should().Contain("vi");
    }

    [Fact]
    public void Verify_KeyMismatch_Reported()
    {
        var (ui, locale, quasar) = Copy();
        ui["th"] = EditFlat(ui["th"], f => { f.Remove("menu.home"); f["zz.extra"] = "x"; });

        var problems = Verify(ui, locale, quasar);

        problems.Should().HaveCount(2);
        problems.Should().Contain(p => p.Contains("th") && p.Contains("menu.home"));
        problems.Should().Contain(p => p.Contains("th") && p.Contains("zz.extra"));
    }

    [Fact]
    public void Verify_EmptyString_Reported()
    {
        var (ui, locale, quasar) = Copy();
        ui["vi"] = EditFlat(ui["vi"], f => f["menu.home"] = "");

        Verify(ui, locale, quasar).Should().ContainSingle().Which.Should().Contain("vi").And.Contain("menu.home");
    }

    [Fact]
    public void Verify_BadJson_Reported()
    {
        var (ui, locale, quasar) = Copy();
        ui["en-US"] = "{ not json";

        Verify(ui, locale, quasar).Should().ContainSingle().Which.Should().Contain("en-US");
    }

    [Fact]
    public void Verify_LocaleJsonDiffersFromBuiltinLocales_Reported()
    {
        var (ui, locale, quasar) = Copy();
        locale["th"] = """{ "code": "th", "nativeName": "Thai", "quasarLang": "th", "order": 3 }""";

        Verify(ui, locale, quasar).Should().ContainSingle().Which.Should().Contain("th").And.Contain("nativeName");
    }

    [Fact]
    public void Verify_QuasarLangsMissing_Reported()
    {
        var (ui, locale, _) = Copy();

        Verify(ui, locale, null).Should().ContainSingle().Which.Should().Contain("quasar-langs.json");
    }

    [Fact]
    public void Verify_BuiltinQuasarLangNotInList_Reported()
    {
        var (ui, locale, _) = Copy();

        Verify(ui, locale, """["zh-TW","en-US","vi"]""").Should().ContainSingle().Which.Should().Contain("th");
    }

    [Fact]
    public void QuasarLangs_Has74AndBuiltins()
    {
        BuiltinLocaleResources.QuasarLangs.Should().HaveCount(74);
        BuiltinLocaleResources.QuasarLangs.Should().Contain(BuiltinLocales.All.Select(l => l.QuasarLang));
    }

    [Fact]
    public void UiPlaceholderNames_UnionOfFourLocales()
    {
        var names = BuiltinLocaleResources.UiPlaceholderNames;

        names.Keys.Should().BeEquivalentTo(BuiltinLocaleResources.BaseKeys);
        names["approval.stagesMore"].Should().BeEquivalentTo(["count", "first", "rest"]);
        names["menu.home"].Should().BeEmpty();
        names["print.bodyHint"].Should().BeEmpty("字面量 {'{{f_12}}'} 裡的名稱不算佔位符");
    }

    [Fact]
    public void Builtin_PassesSyntax()
    {
        var bad = new List<string>();
        foreach (var (code, texts) in BuiltinLocaleResources.Ui)
            foreach (var (key, text) in texts)
            {
                var reason = LocaleTextSafety.Check(text, LocalePackLimits.MaxTextLength) ?? UiMessageSyntax.Check(text, BuiltinLocaleResources.UiPlaceholderNames[key]);
                if (reason is not null) bad.Add($"{code} {key}: {reason}");
            }

        bad.Should().BeEmpty("內建層不會被自己的驗證器拒收");
    }
}
