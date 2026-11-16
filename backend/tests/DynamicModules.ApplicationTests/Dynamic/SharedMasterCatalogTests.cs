using DynamicModules.Application.Dynamic.AppService;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>共用主檔的目錄層（設計 §4.6、§15.2「目錄」）：內建套件對內建主檔逐欄位對帳，requires.masters 也納入循環檢查。</summary>
public class SharedMasterCatalogTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("catalog-masters-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string Master(string id, string requires = "[]") => $$"""
        {
          "format": "formwright-app", "formatVersion": 6,
          "manifest": { "id": "{{id}}", "kind": "master", "version": "1.0.0", "name": { "zh-TW": "{{id}}" },
            "requires": { "masters": {{requires}} } },
          "modules": [ { "code": "party", "key": "master-{{id}}", "name": { "zh-TW": "往來對象" }, "kind": "standalone", "titleField": "name",
            "extensible": true,
            "blocks": [ { "code": "main", "label": { "zh-TW": "基本" }, "fields": [
              { "code": "name", "type": "text", "label": { "zh-TW": "名稱" }, "config": { "required": true } },
              { "code": "phone", "type": "phone", "label": { "zh-TW": "電話" }, "config": {} } ] } ] } ]
        }
        """;

    private static string User(string fillFrom = "party-m/party.phone", string contributionCode = "industry") => $$"""
        {
          "format": "formwright-app", "formatVersion": 6,
          "manifest": { "id": "sales-x", "version": "1.0.0", "name": { "zh-TW": "銷售" },
            "requires": { "masters": [ { "id": "party-m", "modules": ["party"] } ] } },
          "modules": [ { "code": "order", "key": "sales-x-order", "name": { "zh-TW": "訂單" }, "kind": "standalone", "titleField": "no",
            "blocks": [ { "code": "main", "label": { "zh-TW": "表頭" }, "fields": [
              { "code": "no", "type": "text", "label": { "zh-TW": "單號" }, "config": { "required": true } },
              { "code": "customer", "type": "reference", "label": { "zh-TW": "客戶" },
                "config": { "refTarget": { "kind": "module", "key": "party", "app": "party-m" },
                  "fill": { "mode": "default", "map": [ { "from": "{{fillFrom}}", "to": "phone" } ] } } },
              { "code": "phone", "type": "phone", "label": { "zh-TW": "電話" }, "config": {} } ] } ] } ],
          "contributions": [ { "master": "party-m", "module": "party", "block": { "code": "sales", "name": { "zh-TW": "銷售" } },
            "fields": [ { "code": "{{contributionCode}}", "type": "text", "label": { "zh-TW": "產業" }, "config": {} } ] } ]
        }
        """;

    private void Write(string name, string json) => File.WriteAllText(Path.Combine(_dir, $"{name}.app.json"), json);

    private string LoadError()
    {
        var act = () => AppCatalog.Load(_dir);
        return act.Should().Throw<InvalidOperationException>().Which.Message;
    }

    [Fact]
    public void MasterAndUser_Load()
    {
        Write("party-m", Master("party-m"));
        Write("sales-x", User());

        AppCatalog.Load(_dir).BuiltIns.Select(p => p.Manifest.Id).Should().BeEquivalentTo(["party-m", "sales-x"]);
    }

    [Fact]
    public void AMasterFieldThatDoesNotExist_RefusesStartup()
    {
        Write("party-m", Master("party-m"));
        Write("sales-x", User(fillFrom: "party-m/party.fax"));

        LoadError().Should().Contain("sales-x.app.json").And.Contain("party-m/party.fax 不是主檔 party-m 的基本欄位");
    }

    [Fact]
    public void AContributionCodeEqualToABaseField_RefusesStartup()
    {
        Write("party-m", Master("party-m"));
        Write("sales-x", User(contributionCode: "phone"));

        LoadError().Should().Contain("contributions[0].fields[phone].code: 與主檔 party-m 的基本欄位代號相同");
    }

    [Fact]
    public void ARequiredMasterThatIsNotBuiltIn_RefusesStartup()
    {
        Write("sales-x", User());

        LoadError().Should().Contain("requires.masters 的主檔 party-m 不在內建套件裡");
    }

    [Fact]
    public void MastersRequiringEachOther_RefuseStartup()
    {
        Write("m-one", Master("m-one", """[ { "id": "m-two", "modules": ["party"] } ]"""));
        Write("m-two", Master("m-two", """[ { "id": "m-one", "modules": ["party"] } ]"""));

        LoadError().Should().Contain("requires 有循環").And.Contain("m-one").And.Contain("m-two");
    }
}
