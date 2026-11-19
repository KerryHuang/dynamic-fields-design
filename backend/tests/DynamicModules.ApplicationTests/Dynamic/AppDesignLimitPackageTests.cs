using System.Text.Json;
using DynamicModules.Application.Dynamic.AppService;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.TemplateService.Data;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>
/// 設計上限（政策 A16）的套件數量檢查：搬出靜態 Validate，改由 DesignLimitErrors 帶入生效值；出廠值的訊息與政策化之前逐字相同。
/// 純函式，不需要資料庫。
/// </summary>
public class AppDesignLimitPackageTests
{
    private const string Json = """
        {
          "format": "formwright-app", "formatVersion": 5,
          "manifest": { "id": "dl", "version": "1.0.0", "name": { "zh-TW": "設計上限" } },
          "modules": [ { "code": "doc", "key": "dl-doc", "name": { "zh-TW": "單據" }, "kind": "standalone", "titleField": "name",
            "blocks": [ { "code": "main", "label": { "zh-TW": "基本" }, "collapsed": false, "fields": [
              { "code": "name", "type": "text", "label": { "zh-TW": "名稱" }, "config": { "required": true } },
              { "code": "kind", "type": "enum", "label": { "zh-TW": "種類" }, "config": {},
                "options": [ { "code": "a", "label": { "zh-TW": "甲" } } ] } ] } ] } ]
        }
        """;

    private static int F(PolicyDefinition d) => int.Parse(d.Factory);

    private static readonly DesignLimits Factory = new(
        F(PolicyCatalog.DesignMaxGroupsPerPackage), F(PolicyCatalog.DesignMaxModulesPerPackage), F(PolicyCatalog.DesignMaxFieldsPerModule),
        F(PolicyCatalog.DesignMaxOptionsPerField), F(PolicyCatalog.DesignMaxRulesPerModule), F(PolicyCatalog.DesignMaxViewsPerModule),
        F(PolicyCatalog.DesignMaxIntegrationsPerPackage));

    private static AppPackageData Base()
    {
        var parsed = AppJson.Parse(Json);
        parsed.Errors.Should().BeEmpty();
        return parsed.V2!;
    }

    private static I18nText Text(string zh) => new([KeyValuePair.Create("zh-TW", zh)]);

    /// <summary>每種數量超過出廠值一個（null 項只算數量，DesignLimitErrors 不看內容）。</summary>
    private static AppPackageData Over(string kind)
    {
        var p = Base();
        var mod = p.Modules[0];
        var kindField = mod.Blocks[0].Fields[1];
        switch (kind)
        {
            case "groups": p.Groups.AddRange(Enumerable.Repeat<AppGroup>(null!, 21)); break;
            case "modules": p.Modules.AddRange(Enumerable.Range(1, 30).Select(i => mod with { Code = $"doc{i}", Key = $"dl-doc{i}" })); break;
            case "workflows": p.Workflows.AddRange(Enumerable.Repeat<AppWorkflow>(null!, 31)); break;
            case "fields": mod.Blocks[0].Fields.AddRange(Enumerable.Range(0, 299).Select(i => kindField with { Code = $"f{i}" })); break;
            case "options": kindField.Options.AddRange(Enumerable.Range(0, 200).Select(i => new TemplateOption($"o{i}", Text($"選{i}"), null))); break;
            case "rules": mod.Rules.AddRange(Enumerable.Repeat<TemplateRule>(null!, 101)); break;
            case "views": mod.Views.AddRange(Enumerable.Repeat<TemplateView>(null!, 21)); break;
            case "integrations": p.Integrations.AddRange(Enumerable.Repeat<AppIntegrationDecl>(null!, 21)); break;
            case "contributionOptions":
                var field = kindField with { Code = "tier", Options = Enumerable.Range(0, 201).Select(i => new TemplateOption($"o{i}", Text($"選{i}"), null)).ToList() };
                p.Contributions.Add(new AppContribution("bp", "partner", null!, [field], null, null));
                break;
        }
        return p;
    }

    [Theory]
    [InlineData("groups", "groups: 最多 20 個")]
    [InlineData("modules", "modules: 最多 30 個")]
    [InlineData("workflows", "workflows: 最多 30 個")]
    [InlineData("fields", "doc: 欄位最多 300 個")]
    [InlineData("options", "doc.kind: 選項最多 200 個")]
    [InlineData("rules", "doc: 規則最多 100 條")]
    [InlineData("views", "doc: 檢視最多 20 個")]
    [InlineData("integrations", "integrations: 最多 20 個")]
    [InlineData("contributionOptions", "contributions[0].fields[tier]: 選項最多 200 個")]
    public void DesignLimitErrors_FactoryValues_MatchOldMessages(string kind, string expected)
    {
        var p = Over(kind);
        AppPackageValidator.DesignLimitErrors(p, Factory).Should().Contain(expected);
        // 數量檢查不在靜態層（內建套件啟動時不讀政策）；null 整合在引用層會丟例外（既有問題，另案），這裡只驗結構層以外的種類
        if (kind != "integrations") AppPackageValidator.Validate(p).Errors.Should().NotContain(expected);
    }

    [Fact]
    public void DesignLimitErrors_AtLimit_Empty()
    {
        AppPackageValidator.DesignLimitErrors(Base(), Factory).Should().BeEmpty();
        AppPackageValidator.DesignLimitErrors(Base(), Factory with { MaxModules = 1, MaxFieldsPerModule = 2, MaxOptionsPerField = 1 })
            .Should().BeEmpty();
        AppPackageValidator.DesignLimitErrors(Base(), Factory with { MaxFieldsPerModule = 1 }).Should().Equal("doc: 欄位最多 1 個");
    }

    [Fact]
    public void Catalog_StartupIgnoresDesignLimitPolicies()
    {
        var catalog = AppCatalog.Load(AppCatalog.DefaultDirectory);
        catalog.BuiltIns.Should().NotBeEmpty();
        foreach (var p in catalog.BuiltIns)
            AppPackageValidator.Validate(p, catalog.BuiltIns.Where(b => b.Manifest.IsMaster && b.Manifest.Id != p.Manifest.Id)
                .GroupBy(b => b.Manifest.Id).ToDictionary(g => g.Key, g => g.First())).Errors.Should().BeEmpty(p.Manifest.Id);
        // 上限調到 1 時內建套件會超過：證明數量檢查只在帶入政策值的 DesignLimitErrors，靜態層沒有搬回來
        var ones = new DesignLimits(1, 1, 1, 1, 1, 1, 1);
        catalog.BuiltIns.Should().Contain(p => AppPackageValidator.DesignLimitErrors(p, ones).Count > 0);
    }
}
