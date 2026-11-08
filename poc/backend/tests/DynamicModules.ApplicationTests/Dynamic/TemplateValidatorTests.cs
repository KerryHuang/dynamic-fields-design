using System.Text.Json.Nodes;
using DynamicModules.Application.Dynamic.TemplateService;
using DynamicModules.Application.Dynamic.TemplateService.Data;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>第 8 階段決策 7：範本結構驗證。以最小的合法套件為基底，每個測試改壞一處。</summary>
public class TemplateValidatorTests
{
    private const string Minimal = """
    {
      "formatVersion": 1,
      "key": "mini",
      "name": { "zh-TW": "迷你" },
      "modules": [
        {
          "code": "head", "key": "mini-head", "name": { "zh-TW": "表頭" },
          "kind": "standalone", "titleField": "no",
          "blocks": [
            { "code": "main", "label": { "zh-TW": "基本" }, "collapsed": false,
              "fields": [
                { "code": "no", "type": "autonumber", "label": { "zh-TW": "編號" }, "config": { "format": "M-{0000}" } },
                { "code": "status", "type": "enum", "label": { "zh-TW": "狀態" },
                  "options": [ { "code": "draft", "label": { "zh-TW": "草稿" } }, { "code": "done", "label": { "zh-TW": "完成" } } ],
                  "config": { "defaultValue": "opt_draft" } },
                { "code": "qty", "type": "integer", "label": { "zh-TW": "數量" } },
                { "code": "price", "type": "currency", "label": { "zh-TW": "單價" } },
                { "code": "amount", "type": "formula", "label": { "zh-TW": "金額" },
                  "config": { "formula": "{qty} * {price}", "resultType": "currency" } },
                { "code": "total", "type": "rollup", "label": { "zh-TW": "合計" },
                  "config": { "rollup": { "sourceField": "line.head", "function": "sum", "aggregateField": "line.amount",
                                          "condition": { "field": "kind", "op": "eq", "value": "opt_good" } } } },
                { "code": "due", "type": "date", "label": { "zh-TW": "交期" } }
              ] }
          ],
          "rules": [ { "kind": "readOnly", "targets": ["*", "field:qty", "block:main"], "active": true,
                       "condition": { "all": [ { "field": "status", "op": "in", "value": ["opt_done"] },
                                               { "field": "qty", "op": "gt", "value": { "fieldRef": "price" } } ] } } ],
          "views": [ { "name": { "zh-TW": "全部" }, "columns": ["no", "status"], "filter": { "field": "status", "op": "eq", "value": "opt_draft" },
                       "sort": [ { "field": "no", "order": "desc" } ], "isDefault": true } ],
          "permissions": [ { "group": "sales", "scope": "own",
                             "privileges": { "read": true, "add": true, "edit": true, "delete": false,
                                             "printExport": false, "pricing": false, "approve": false } } ]
        },
        {
          "code": "line", "key": "mini-line", "name": { "zh-TW": "明細" },
          "kind": "standalone", "titleField": "name",
          "blocks": [
            { "code": "main", "label": { "zh-TW": "明細" }, "collapsed": false,
              "fields": [
                { "code": "head", "type": "reference", "label": { "zh-TW": "表頭" },
                  "config": { "refTarget": { "kind": "module", "key": "head" }, "relation": "detail" } },
                { "code": "name", "type": "text", "label": { "zh-TW": "品名" } },
                { "code": "kind", "type": "enum", "label": { "zh-TW": "種類" },
                  "options": [ { "code": "good", "label": { "zh-TW": "良品" } } ] },
                { "code": "amount", "type": "currency", "label": { "zh-TW": "金額" } },
                { "code": "due", "type": "date", "label": { "zh-TW": "交期" }, "config": { "parentDefaultField": "head.due" } }
              ] }
          ]
        }
      ]
    }
    """;

    private static JsonNode Base() => JsonNode.Parse(Minimal)!;
    private static JsonNode Head(JsonNode p) => p["modules"]![0]!;
    private static JsonNode Line(JsonNode p) => p["modules"]![1]!;
    private static JsonArray HeadFields(JsonNode p) => Head(p)["blocks"]![0]!["fields"]!.AsArray();
    private static JsonNode HeadField(JsonNode p, string code) => HeadFields(p).First(f => (string)f!["code"]! == code)!;

    private static IReadOnlyList<string> Validate(JsonNode p)
    {
        var (package, error) = TemplateJson.Parse(p.ToJsonString());
        error.Should().BeNull();
        return TemplateValidator.Validate(package!);
    }

    [Fact]
    public void Valid_MinimalPackage_NoErrors() => Validate(Base()).Should().BeEmpty();

    [Fact]
    public void BuiltIn_Order_IsValid()
    {
        var (package, error) = TemplateJson.Parse(File.ReadAllText(OrderTemplate.Path));
        error.Should().BeNull();
        TemplateValidator.Validate(package!).Should().BeEmpty();
    }

    [Theory, InlineData("repair"), InlineData("mold-care"), InlineData("quality")]
    public void BuiltIn_IsValid(string key)
    {
        var (package, error) = TemplateJson.Parse(File.ReadAllText(OrderTemplate.PathOf(key)));
        error.Should().BeNull();
        package!.Key.Should().Be(key);
        TemplateValidator.Validate(package).Should().BeEmpty();
    }

    [Fact]
    public void Fixtures_LoadFourTemplates() =>
        TemplateCatalog.Load(TemplateCatalog.DefaultDirectory).BuiltIns.Select(t => t.Key)
            .Should().Equal("mold-care", "order", "quality", "repair");

    [Fact]
    public void BlockColumns_MissingMeansTwo()
    {
        var (package, error) = TemplateJson.Parse(Base().ToJsonString());
        error.Should().BeNull();
        package!.Modules[0].Blocks[0].Columns.Should().Be(2);
    }

    [Fact]
    public void BlockColumns_OutOfRange()
    {
        var p = Base();
        Head(p)["blocks"]![0]!["columns"] = 5;
        Validate(p).Should().ContainSingle().Which.Should().Be("head: 區塊 main 的每列欄數必須是 1 到 3");
    }

    [Fact]
    public void FormatVersion_Not1()
    {
        var p = Base();
        p["formatVersion"] = 2;
        Validate(p).Should().ContainSingle().Which.Should().Contain("formatVersion");
    }

    [Fact]
    public void DuplicateFieldCode_InModule()
    {
        var p = Base();
        HeadField(p, "due")["code"] = "qty";
        Validate(p).Should().Contain("head.qty: 欄位代號重複");
    }

    [Fact]
    public void DuplicateOptionCode_AcrossFieldsInModule()
    {
        var p = Base();
        HeadFields(p).Add(JsonNode.Parse("""
            { "code": "phase", "type": "enum", "label": { "zh-TW": "階段" }, "options": [ { "code": "draft", "label": { "zh-TW": "草稿" } } ] }
            """));
        Validate(p).Should().ContainSingle().Which.Should().Be("head.phase: 選項代號重複 draft");
    }

    [Fact]
    public void Formula_UnknownCode()
    {
        var p = Base();
        HeadField(p, "amount")["config"]!["formula"] = "{qty} * {rate}";
        Validate(p).Should().ContainSingle().Which.Should().Be("head.amount: 公式引用了不存在的欄位 rate");
    }

    [Fact]
    public void Condition_UnknownOptionCode()
    {
        var p = Base();
        Head(p)["rules"]![0]!["condition"]!["all"]![0]!["value"] = new JsonArray("opt_nope");
        Validate(p).Should().ContainSingle().Which.Should().Contain("opt_nope");
    }

    [Fact]
    public void OptLikeText_OnNonOptionField_IsNotAnOptionReference()
    {
        var p = Base();
        HeadFields(p).Add(JsonNode.Parse("""
            { "code": "memo", "type": "text", "label": { "zh-TW": "備忘" }, "config": { "defaultValue": "opt_abc" } }
            """));
        Head(p)["rules"]![0]!["condition"]!["all"]!.AsArray().Add(JsonNode.Parse("""{ "field": "memo", "op": "eq", "value": "opt_abc" }"""));
        Validate(p).Should().BeEmpty();
    }

    [Fact]
    public void Enum_DefaultValue_UnknownOption()
    {
        var p = Base();
        HeadField(p, "status")["config"]!["defaultValue"] = "opt_abc";
        Validate(p).Should().ContainSingle().Which.Should().Be("head.status: 預設值引用了不存在的選項 opt_abc");
    }

    [Fact]
    public void Rollup_UnknownSourceField()
    {
        var p = Base();
        HeadField(p, "total")["config"]!["rollup"]!["sourceField"] = "line.nope";
        Validate(p).Should().Contain(e => e.StartsWith("head.total:") && e.Contains("line.nope"));
    }

    [Fact]
    public void ParentDefault_Unknown()
    {
        var p = Base();
        Line(p)["blocks"]![0]!["fields"]![4]!["config"]!["parentDefaultField"] = "head.nope";
        Validate(p).Should().ContainSingle().Which.Should().StartWith("line.due:").And.Contain("head.nope");
    }

    [Fact]
    public void RuleTarget_UnknownBlock()
    {
        var p = Base();
        Head(p)["rules"]![0]!["targets"] = new JsonArray("block:nope");
        Validate(p).Should().ContainSingle().Which.Should().StartWith("head:").And.Contain("block:nope");
    }

    [Fact]
    public void View_UnknownColumn()
    {
        var p = Base();
        Head(p)["views"]![0]!["columns"] = new JsonArray("no", "nope");
        Validate(p).Should().ContainSingle().Which.Should().StartWith("head:").And.Contain("nope");
    }

    [Fact]
    public void TitleField_NotTextOrAutonumber()
    {
        var p = Base();
        Head(p)["titleField"] = "qty";
        Validate(p).Should().ContainSingle().Which.Should().StartWith("head:").And.Contain("標題");
    }

    [Fact]
    public void ComputedCycle()
    {
        var p = Base();
        HeadFields(p).Add(JsonNode.Parse("""{ "code": "a", "type": "formula", "label": { "zh-TW": "A" }, "config": { "formula": "{b} + 1", "resultType": "number" } }"""));
        HeadFields(p).Add(JsonNode.Parse("""{ "code": "b", "type": "formula", "label": { "zh-TW": "B" }, "config": { "formula": "{a} + 1", "resultType": "number" } }"""));
        Validate(p).Should().ContainSingle().Which.Should().Contain("循環");
    }

    [Fact]
    public void DetailModule_WithPermissions()
    {
        var p = Base();
        Line(p)["permissions"] = Head(p)["permissions"]!.DeepClone();
        Validate(p).Should().ContainSingle().Which.Should().StartWith("line:").And.Contain("權限");
    }

    [Fact]
    public void RefTarget_ExternalModuleKey_IsNotAnError()
    {
        var p = Base();
        HeadFields(p).Add(JsonNode.Parse("""
            { "code": "cust", "type": "reference", "label": { "zh-TW": "客戶" }, "config": { "refTarget": { "kind": "module", "key": "customer-x" } } }
            """));
        Validate(p).Should().BeEmpty();
    }

    [Fact]
    public void MissingCollections_AreEmptyLists()
    {
        var (package, error) = TemplateJson.Parse("""
            { "formatVersion": 1, "key": "x1", "name": { "zh-TW": "X" },
              "modules": [ { "code": "m1", "key": "m1", "name": { "zh-TW": "M" }, "kind": "standalone", "titleField": "t",
                             "blocks": [ { "code": "b", "label": { "zh-TW": "B" }, "fields": [ { "code": "t", "type": "text", "label": { "zh-TW": "T" } } ] } ] } ] }
            """);
        error.Should().BeNull();
        var m = package!.Modules.Single();
        m.Rules.Should().BeEmpty();
        m.Views.Should().BeEmpty();
        m.Permissions.Should().BeEmpty();
        m.Blocks[0].Fields[0].Options.Should().BeEmpty();
        TemplateValidator.Validate(package).Should().BeEmpty();
    }

    [Fact]
    public void Parse_UnknownFieldType_ReturnsError()
    {
        var (package, error) = TemplateJson.Parse(Minimal.Replace("\"type\": \"integer\"", "\"type\": \"bogus\""));
        package.Should().BeNull();
        error.Should().Contain("bogus");
    }

    [Fact]
    public void Parse_NumericFieldType_ReturnsError()
    {
        var (package, error) = TemplateJson.Parse(Minimal.Replace("\"type\": \"integer\"", "\"type\": 2"));
        package.Should().BeNull();
        error.Should().NotBeNull();
    }

    [Fact]
    public void Parse_NotJson_ReturnsError() => TemplateJson.Parse("{ nope").Error.Should().StartWith("範本格式錯誤");

    [Fact]
    public void TitleField_Null_OnlyAllowedForExtension()
    {
        var p = Base();
        Line(p)["titleField"] = null;
        Validate(p).Should().ContainSingle().Which.Should().StartWith("line:").And.Contain("標題");

        // 擴充模組沒有自己的標題欄位（標題取自既有資料）
        var ext = Base();
        Line(ext)["titleField"] = null;
        Line(ext)["kind"] = "extension";
        Line(ext)["hostEntityKey"] = "design.mold";
        Validate(ext).Should().BeEmpty();
    }

    [Fact]
    public void Formula_OnNonFormulaField_Rejected()
    {
        var p = Base();
        HeadField(p, "qty")["config"] = JsonNode.Parse("""{ "formula": "{nope}" }""");
        Validate(p).Should().ContainSingle().Which.Should().Be("head.qty: 只有公式欄位可以設定公式");
    }

    [Fact]
    public void Rollup_OnNonRollupField_Rejected()
    {
        var p = Base();
        HeadField(p, "qty")["config"] = JsonNode.Parse("""{ "rollup": { "sourceField": "line.nope", "function": "count" } }""");
        Validate(p).Should().ContainSingle().Which.Should().Be("head.qty: 只有彙總欄位可以設定彙總");
    }
}

/// <summary>內建範本的路徑：由 csproj 複製到測試輸出目錄的 templates/。</summary>
internal static class OrderTemplate
{
    public static string Path => PathOf("order");

    public static string PathOf(string key) => System.IO.Path.Combine(AppContext.BaseDirectory, "templates", $"{key}.json");
}
