using System.Text;
using DynamicModules.Application.Dynamic.AppService;
using DynamicModules.Application.Dynamic.TemplateService;
using FluentAssertions;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>套件格式 v2：檔案層、結構層、引用層與 v1 升級（純函式，不需要資料庫）。</summary>
public class AppPackageFormatTests
{
    private static AppValidation Check(string json)
    {
        var parsed = AppJson.Parse(json);
        parsed.Errors.Should().BeEmpty();
        return AppPackageValidator.Validate(parsed.V2!);
    }

    private static IReadOnlyList<string> ParseErrors(byte[] bytes) => AppJson.Parse(bytes).Errors;

    // ---- 範例必須通過 ----

    [Theory]
    [InlineData("Crm")]
    [InlineData("Quote")]
    [InlineData("Procure")]
    public void TestPackages_PassWithNoErrors(string name)
    {
        var json = (string)typeof(AppTestPackages).GetField(name)!.GetValue(null)!;
        var result = Check(json);
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Quote_DeclaresCrmDependency_WithoutWarnings()
    {
        Check(AppTestPackages.Quote).Warnings.Should().BeEmpty();
    }

    // ---- 檔案層 ----

    [Fact]
    public void File_UnknownFormatVersion5_IsRejectedWithUpgradeHint()
    {
        var json = AppTestPackages.Crm.Replace("\"formatVersion\": 2", "\"formatVersion\": 5");
        ParseErrors(Encoding.UTF8.GetBytes(json)).Should().ContainSingle().Which.Should().Contain("較新版本");
    }

    [Fact]
    public void File_Bom_IsAccepted_AndUtf16IsRejected()
    {
        var withBom = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(AppTestPackages.Procure)).ToArray();
        AppJson.Parse(withBom).Ok.Should().BeTrue();

        var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(AppTestPackages.Procure)).ToArray();
        ParseErrors(utf16).Should().NotBeEmpty();
    }

    [Fact]
    public void File_OverOneMebibyte_IsRejected()
    {
        var big = new byte[AppJson.MaxBytes + 1];
        ParseErrors(big).Should().ContainSingle().Which.Should().Contain("MiB");
    }

    [Fact]
    public void File_DuplicateProperty_IsRejected()
    {
        var json = AppTestPackages.Procure.Replace("\"format\": \"formwright-app\",", "\"format\": \"formwright-app\", \"format\": \"x\",");
        ParseErrors(Encoding.UTF8.GetBytes(json)).Should().ContainSingle().Which.Should().Contain("重複的屬性");
    }

    [Fact]
    public void File_DeepNesting_IsRejected()
    {
        var json = new string('[', 40) + new string(']', 40);
        ParseErrors(Encoding.UTF8.GetBytes(json)).Should().NotBeEmpty();
    }

    // ---- 結構層 ----

    [Fact]
    public void Structure_UnknownTopLevelProperty_IsRejected_ButExtensionPropertiesAreDropped()
    {
        var unknown = AppTestPackages.Procure.Replace("\"groups\"", "\"surprise\": 1, \"groups\"");
        AppJson.Parse(unknown).Errors.Should().ContainSingle().Which.Should().Contain("surprise");

        var extension = AppTestPackages.Procure.Replace("\"groups\"", "\"x-tool\": {\"anything\": true}, \"groups\"");
        var parsed = AppJson.Parse(extension);
        parsed.Ok.Should().BeTrue();
        AppPackageValidator.Validate(parsed.V2!).Errors.Should().BeEmpty();
    }

    [Fact]
    public void Structure_UnknownConfigKey_IsRejected()
    {
        var json = AppTestPackages.Procure.Replace("\"required\": true", "\"reqired\": true");
        Check(json).Errors.Should().ContainSingle().Which.Should().Contain("reqired");
    }

    [Theory]
    // workflows、conversions、reports 在格式 3、events 在格式 4 已有型別，格式 2 帶它們被拒的測試見 AppV3Tests
    [InlineData("dashboards")]
    public void Structure_ReservedSectionNonEmpty_IsRejected(string section)
    {
        var json = AppTestPackages.Procure.Replace("\"groups\"", $"\"{section}\": [ {{ \"x\": 1 }} ], \"groups\"");
        Check(json).Errors.Should().ContainSingle().Which.Should().Contain("此版本不支援").And.Contain(section);
    }

    [Fact]
    public void Structure_ReservedSectionEmpty_IsAccepted()
    {
        var json = AppTestPackages.Procure.Replace("\"groups\"", "\"workflows\": [], \"groups\"");
        Check(json).Errors.Should().BeEmpty();
    }

    [Fact]
    public void Structure_UnsupportedLocaleKey_IsRejected()
    {
        var json = AppTestPackages.Procure.Replace("\"name\": { \"zh-TW\": \"簡易採購\" }", "\"name\": { \"zh-TW\": \"簡易採購\", \"fr\": \"x\" }");
        Check(json).Errors.Should().ContainSingle().Which.Should().Contain("fr");
    }

    [Fact]
    public void Structure_TooManyModules_IsRejected()
    {
        var module = """{ "code": "m{0}", "key": "m-{0}x", "name": { "zh-TW": "m" }, "kind": "standalone", "titleField": "t", "blocks": [ { "code": "b", "label": { "zh-TW": "b" }, "collapsed": false, "columns": 2, "fields": [ { "code": "t", "type": "text", "label": { "zh-TW": "t" }, "config": {} } ] } ] }""";
        var modules = string.Join(",", Enumerable.Range(0, 31).Select(i => module.Replace("{0}", $"{i}")));
        var json = $$"""{ "format": "formwright-app", "formatVersion": 2, "manifest": { "id": "many", "version": "1.0.0", "name": { "zh-TW": "多" } }, "modules": [ {{modules}} ] }""";
        Check(json).Errors.Should().Contain(e => e.StartsWith("modules:") && e.Contains("30"));
    }

    // ---- 引用層 ----

    [Fact]
    public void References_ExternalModuleNotDeclaredInRequires_IsRejected()
    {
        var json = AppTestPackages.Quote.Replace("\"requires\": { \"apps\": [ { \"id\": \"crm-lite\", \"modules\": [\"customer\"] } ], \"entities\": [] }", "\"requires\": { \"apps\": [], \"entities\": [] }");
        Check(json).Errors.Should().ContainSingle().Which.Should().Contain("crm-lite.customer");
    }

    [Fact]
    public void References_DeclaredButUnusedRequirement_OnlyWarns()
    {
        var json = AppTestPackages.Procure.Replace("\"name\": { \"zh-TW\": \"簡易採購\" } }", "\"name\": { \"zh-TW\": \"簡易採購\" }, \"requires\": { \"apps\": [ { \"id\": \"crm-lite\", \"modules\": [\"customer\"] } ], \"entities\": [\"sales.customer\"] } }");
        var result = Check(json);
        result.Errors.Should().BeEmpty();
        result.Warnings.Should().HaveCount(2);
    }

    [Fact]
    public void References_TargetModuleMissingInPackage_IsRejected()
    {
        var json = AppTestPackages.Procure.Replace("\"key\": \"supplier\" }", "\"key\": \"nobody\" }");
        Check(json).Errors.Should().ContainSingle().Which.Should().Contain("nobody");
    }

    [Fact]
    public void References_UnknownGroupInPermissions_IsRejected()
    {
        var json = AppTestPackages.Procure.Replace("\"group\": \"buyer\"", "\"group\": \"ghost\"");
        Check(json).Errors.Should().Contain(e => e.Contains("ghost"));
    }

    [Fact]
    public void References_FieldAccessAndOptionScopes_MustPointInsidePackage()
    {
        var badField = AppTestPackages.Crm.Replace("\"customer.phone\"", "\"customer.nope\"");
        Check(badField).Errors.Should().ContainSingle().Which.Should().Contain("customer.nope");

        var badOption = AppTestPackages.Crm.Replace("\"customer.dormant\"", "\"customer.nope\"");
        Check(badOption).Errors.Should().ContainSingle().Which.Should().Contain("customer.nope");

        var hiddenTitle = AppTestPackages.Crm.Replace("{ \"field\": \"customer.phone\", \"group\": \"sales\", \"access\": \"readOnly\" }",
            "{ \"field\": \"customer.name\", \"group\": \"sales\", \"access\": \"hidden\" }");
        Check(hiddenTitle).Errors.Should().ContainSingle().Which.Should().Contain("標題欄位");
    }

    // ---- 相依圖 ----

    [Fact]
    public void Requires_Cycle_IsDetected()
    {
        var cycle = AppDependencyGraph.FindCycle([("a", ["b"]), ("b", ["a"]), ("c", [])]);
        cycle.Should().Equal("a", "b", "a");
        AppDependencyGraph.FindCycle([("a", ["b"]), ("b", []), ("c", ["a", "b"])]).Should().BeNull();
        AppDependencyGraph.FindCycle([("a", ["outside"])]).Should().BeNull();
    }

    // ---- v1 升級 ----

    [Fact]
    public void Upgrade_AllFourBuiltInTemplates_Succeed_AndQualityRequiresOrder()
    {
        var catalog = TemplateCatalog.Load(Path.Combine(AppContext.BaseDirectory, "templates"));
        catalog.BuiltIns.Should().HaveCount(4);
        var known = catalog.BuiltIns.Select(t => new KnownPackage(t.Key, t.Modules.Select(m => (m.Code, m.Key)).ToList())).ToList();

        foreach (var template in catalog.BuiltIns)
        {
            var (package, errors) = AppPackageUpgrader.FromTemplate(template, known.Where(k => k.Id != template.Key).ToList());
            errors.Should().BeEmpty(template.Key);
            AppPackageValidator.Validate(package!).Errors.Should().BeEmpty(template.Key);
        }

        var quality = AppPackageUpgrader.FromTemplate(catalog.BuiltIns.Single(t => t.Key == "quality"),
            known.Where(k => k.Id != "quality").ToList()).Package!;
        quality.Manifest.Requires.Apps.Should().ContainSingle(a => a.Id == "order" && a.Modules.Contains("order"));
    }

    [Fact]
    public void Upgrade_ReferenceToUnknownModule_FailsAndListsIt()
    {
        var catalog = TemplateCatalog.Load(Path.Combine(AppContext.BaseDirectory, "templates"));
        var (package, errors) = AppPackageUpgrader.FromTemplate(catalog.BuiltIns.Single(t => t.Key == "quality"), []);
        package.Should().BeNull();
        errors.Should().ContainSingle().Which.Should().Contain("order");
    }
}
