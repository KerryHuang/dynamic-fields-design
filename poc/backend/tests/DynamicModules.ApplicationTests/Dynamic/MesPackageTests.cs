using DynamicModules.Application.Dynamic.AppService;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>內建應用程式「MES 模具廠」（apps/mes-mold.app.json）的載入驗證與約定：四語系、代碼前綴、不依賴其他應用程式。</summary>
public class MesPackageTests
{
    private static readonly Lazy<AppPackageData> Package = new(() =>
        AppCatalog.Load(AppCatalog.DefaultDirectory).BuiltIns.Single(p => p.Manifest.Id == "mes-mold"));

    [Fact]
    public void Catalog_LoadsMes_AndTheValidatorReportsNoErrors()
    {
        var p = Package.Value;
        p.FormatVersion.Should().Be(4);
        p.Manifest.Requires.Apps.Should().BeEmpty("三個應用程式彼此獨立");
        AppPackageValidator.Validate(p).Errors.Should().BeEmpty();
        p.Modules.Should().HaveCount(14);
        p.Modules.Should().OnlyContain(m => m.Key.StartsWith("mes-"));
        p.Events.Should().OnlyContain(e => e.Code.StartsWith("mes-"));
        p.Checks.Should().OnlyContain(c => c.Code.StartsWith("mes-"));
        p.Conversions.Should().OnlyContain(c => c.Code.StartsWith("mes-"));
        p.PrintTemplates.Should().OnlyContain(t => t.Code.StartsWith("mes-"));
    }

    [Fact]
    public void EveryLabel_HasAllFourLocales()
    {
        var p = Package.Value;
        var texts = new List<(string Where, I18nText Text)> { ("manifest.name", p.Manifest.Name), ("manifest.description", p.Manifest.Description!) };
        texts.AddRange(p.Groups.Select(g => ($"group {g.Code}", g.Name)));
        foreach (var m in p.Modules)
        {
            texts.Add(($"{m.Code}", m.Name));
            foreach (var b in m.Blocks)
            {
                texts.Add(($"{m.Code}.{b.Code}", b.Label));
                foreach (var f in b.Fields)
                {
                    texts.Add(($"{m.Code}.{f.Code}", f.Label));
                    texts.AddRange(f.Options.Select(o => ($"{m.Code}.{f.Code}.{o.Code}", o.Label)));
                }
            }
            texts.AddRange(m.Rules.Where(r => r.Message is not null).Select(r => ($"{m.Code} rule", r.Message!)));
            texts.AddRange(m.Views.Select(v => ($"{m.Code} view", v.Name)));
        }
        texts.AddRange(p.Events.Select(e => ($"event {e.Code}", e.Name)));
        texts.AddRange(p.Checks.SelectMany(c => new[] { ($"check {c.Code}", c.Name), ($"check {c.Code} message", c.Message) }));
        texts.AddRange(p.Conversions.Select(c => ($"conversion {c.Code}", c.Name)));
        texts.AddRange(p.PrintTemplates.Select(t => ($"print {t.Code}", t.Name)));

        foreach (var (where, text) in texts)
            text.Values.Keys.Should().BeEquivalentTo(I18nText.SupportedLocales, where);
    }
}
