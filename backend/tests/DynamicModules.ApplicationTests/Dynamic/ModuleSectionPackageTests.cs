using System.Text.Json;
using System.Text.Json.Nodes;
using DynamicModules.Application.Dynamic.AppService;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.TemplateService;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>套件與範本的模組 section：讀取接受字串或多語系物件；寫出時只有繁中寫字串、有其他語系寫物件（格式版號不變）。</summary>
public class ModuleSectionPackageTests
{
    private static readonly Lazy<AppCatalog> Catalog = new(() => AppCatalog.Load(AppCatalog.DefaultDirectory));

    private static AppPackageData Partner => Catalog.Value.BuiltIns.Single(p => p.Manifest.Id == "partner");

    /// <summary>內建往來對象套件，換掉第一個模組的 section 後再讀一次。</summary>
    private static AppPackageData ParseWithSection(JsonNode? section)
    {
        var root = JsonSerializer.SerializeToNode(Partner, TemplateJson.Options)!;
        root["modules"]![0]!["section"] = section;
        var parsed = AppJson.Parse(root.ToJsonString(TemplateJson.Options));
        parsed.Errors.Should().BeEmpty();
        return parsed.V2!;
    }

    private static JsonNode? WrittenSection(I18nText? section)
    {
        var module = Partner.Modules[0] with { Section = section };
        return JsonSerializer.SerializeToNode(module, TemplateJson.Options)!["section"];
    }

    private static I18nText Make(params (string Locale, string Text)[] items) =>
        new(items.Select(i => KeyValuePair.Create(i.Locale, i.Text)));

    [Fact]
    public void Read_StringSection_IsZhTwOnly() =>
        ParseWithSection(JsonValue.Create("主檔")).Modules[0].Section!.Values
            .Should().Equal(new Dictionary<string, string> { ["zh-TW"] = "主檔" });

    [Fact]
    public void Read_ObjectSection_KeepsEveryLocale() =>
        ParseWithSection(new JsonObject { ["zh-TW"] = "主檔", ["en-US"] = "Master data", ["vi"] = "Dữ liệu chủ" }).Modules[0].Section!.Values
            .Should().Equal(new Dictionary<string, string> { ["zh-TW"] = "主檔", ["en-US"] = "Master data", ["vi"] = "Dữ liệu chủ" });

    [Fact]
    public void Read_NullSection_IsNoSection() => ParseWithSection(null).Modules[0].Section.Should().BeNull();

    [Fact]
    public void Read_NumberSection_IsRejected()
    {
        var root = JsonSerializer.SerializeToNode(Partner, TemplateJson.Options)!;
        root["modules"]![0]!["section"] = 3;

        AppJson.Parse(root.ToJsonString()).Errors.Should().ContainSingle().Which.Should().Contain("套件格式錯誤");
    }

    [Fact]
    public void Write_ZhTwOnly_IsAPlainString()
    {
        var node = WrittenSection(Make(("zh-TW", "主檔")));

        node!.GetValueKind().Should().Be(JsonValueKind.String);
        node.GetValue<string>().Should().Be("主檔");
    }

    [Fact]
    public void Write_WithOtherLocales_IsAnObject()
    {
        var node = WrittenSection(Make(("zh-TW", "主檔"), ("en-US", "Master data")));

        node!.GetValueKind().Should().Be(JsonValueKind.Object);
        node["zh-TW"]!.GetValue<string>().Should().Be("主檔");
        node["en-US"]!.GetValue<string>().Should().Be("Master data");
    }

    [Fact]
    public void Write_NoSection_IsOmitted() =>
        JsonSerializer.SerializeToNode(Partner.Modules[0] with { Section = null }, TemplateJson.Options)!.AsObject()
            .ContainsKey("section").Should().BeFalse();

    [Fact]
    public void BuiltInPackages_EverySectionHasFourLocales_TranslatedTheSameEverywhere()
    {
        var sections = Catalog.Value.BuiltIns.SelectMany(p => p.Modules.Select(m => (App: p.Manifest.Id, m.Code, m.Section)))
            .Where(x => x.Section is not null).ToList();
        sections.Should().NotBeEmpty();
        foreach (var (app, code, section) in sections)
            section!.Values.Keys.Should().BeEquivalentTo(I18nText.SupportedLocales, $"{app}.{code} 的分區要有四個語系");

        foreach (var group in sections.GroupBy(x => x.Section!.Values["zh-TW"]))
            foreach (var locale in I18nText.SupportedLocales)
                group.Select(x => x.Section!.Values[locale]).Distinct().Should().ContainSingle($"「{group.Key}」的 {locale} 譯名在所有套件要一致");
    }
}
