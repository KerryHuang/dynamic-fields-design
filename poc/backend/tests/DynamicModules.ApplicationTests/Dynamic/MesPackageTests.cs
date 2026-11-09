using DynamicModules.Application.Dynamic.AppService;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Domain.Entities.Dynamic;
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
        p.FormatVersion.Should().Be(6);
        p.Modules.Single(m => m.Code == "mold").TitleTemplate.Should().Be("{no} {name}");
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

    [Fact]
    public void Mold_IsMesOwnModule_NotAnExtensionOfACoreEntity()
    {
        // s2d-0：模具歸 MES 自己的模組，核心不再有模擬實體 design.mold
        var p = Package.Value;
        p.Manifest.Requires.Entities.Should().NotContain("design.mold");
        var mold = p.Modules.Single(m => m.Code == "mold");
        mold.Key.Should().Be("mes-mold");
        mold.Kind.Should().Be(ModuleKind.Standalone);
        mold.HostEntityKey.Should().BeNull();
        mold.TitleField.Should().Be("name");
        var fields = mold.Blocks.SelectMany(b => b.Fields).ToDictionary(f => f.Code);
        fields.Keys.Should().BeEquivalentTo("no", "name", "lastCareDate", "cycleDays", "shotCount");
        fields["no"].Type.Should().Be(FieldType.Text);
        fields["no"].Config.GetProperty("required").GetBoolean().Should().BeTrue();
        fields["name"].Config.GetProperty("required").GetBoolean().Should().BeTrue();
        mold.Blocks.Single(b => b.Code == "care").Fields.Select(f => f.Code).Should().Equal("lastCareDate", "cycleDays", "shotCount");
    }

    [Fact]
    public void EveryModuleExceptDetailChildren_HasAMenuSection()
    {
        // 沒有 section 的模組在選單會被歸到「自訂功能」；明細子模組（relation: detail）不上選單，不需要
        var p = Package.Value;
        var detailChildren = p.Modules
            .Where(m => m.Blocks.SelectMany(b => b.Fields).Any(f =>
                f.Type == FieldType.Reference && f.Config.TryGetProperty("relation", out var r) && r.GetString() == "detail"))
            .Select(m => m.Code);
        p.Modules.Where(m => !detailChildren.Contains(m.Code) && m.Kind == ModuleKind.Standalone)
            .Where(m => string.IsNullOrEmpty(m.Section)).Select(m => m.Code)
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData("repair")]
    [InlineData("care")]
    public void RepairAndCare_ReferToTheMoldModule(string module)
    {
        var field = Package.Value.Modules.Single(m => m.Code == module).Blocks.SelectMany(b => b.Fields).Single(f => f.Code == "mold");
        var target = field.Config.GetProperty("refTarget");
        target.GetProperty("kind").GetString().Should().Be("module");
        target.GetProperty("key").GetString().Should().Be("mold");
    }
}
