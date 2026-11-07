namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 格式 3 的測試專用套件（只放測試專案、不隨產品發行）：虛構的「單據流」情境，
/// 客戶 → 報價（含明細，狀態機、帶值）→ 訂單（含明細）＋一張報表。欄位標籤等於欄位代號，方便測試用標籤找 key。
/// </summary>
internal static class AppTestPackagesV3
{
    private const string LeadPerms = """
        { "read": true, "add": true, "edit": true, "delete": true, "printExport": true, "pricing": true, "approve": true }
        """;

    private static string Perm(string group) =>
        $$"""{ "group": "{{group}}", "scope": "all", "privileges": {{LeadPerms}} }""";

    /// <summary>modules 區段；fill 與保留 section 由呼叫端決定，方便造出各種壞套件。</summary>
    public static string Sales(
        int formatVersion = 3, string? workflows = null, string? conversions = null, string? reports = null,
        string? fill = null, string appId = "sales-flow", string conversionCode = "sales-quote-to-order")
    {
        fill ??= """, "fill": { "mode": "lock", "map": [ { "from": "customer.name", "to": "customerName" } ] }""";
        workflows ??= """
            [ { "module": "quote", "statusField": "status", "initial": "draft",
                "states": [ { "option": "draft" }, { "option": "approved", "readOnly": true } ],
                "transitions": [ { "from": "draft", "to": "approved", "groups": ["lead"] } ] } ]
            """;
        conversions ??= $$"""
            [ { "code": "{{conversionCode}}", "name": { "zh-TW": "報價轉訂單" }, "active": true,
                "source": "quote", "target": "order", "sourceStates": ["approved"],
                "header": { "sourceField": "order.fromQuote", "copy": [ { "from": "quote.customerName", "to": "order.customerName" } ] },
                "lines": { "sourceModule": "quote-line", "targetModule": "order-line", "sourceLineField": "order-line.fromLine",
                           "quantity": { "from": "quote-line.qty", "to": "order-line.qty" },
                           "copy": [ { "from": "quote-line.item", "to": "order-line.item" } ] } } ]
            """;
        reports ??= """
            [ { "code": "quoteByStatus", "name": "報價狀態分布", "shareWith": ["lead"],
                "definition": { "version": 1, "mainModule": "quote", "child": null, "columns": [],
                  "groups": [ { "field": { "source": "main", "field": "quote.status", "via": null }, "bucket": null } ],
                  "measures": [ { "id": "n", "kind": "countMain", "field": null, "condition": null } ],
                  "filters": [], "primaryDateField": null, "dateRange": null, "sort": [], "viz": "donut" } } ]
            """;
        var sections = formatVersion >= 2
            ? $$""", "workflows": {{workflows}}, "conversions": {{conversions}}, "reports": {{reports}}"""
            : "";
        return $$"""
        {
          "format": "formwright-app", "formatVersion": {{formatVersion}},
          "manifest": { "id": "{{appId}}", "version": "1.0.0", "name": { "zh-TW": "單據流測試" } },
          "groups": [ { "code": "sales", "name": { "zh-TW": "測試業務" } }, { "code": "lead", "name": { "zh-TW": "測試主管" } } ],
          "modules": [
            { "code": "customer", "key": "{{appId}}-customer", "name": { "zh-TW": "客戶" }, "kind": "standalone", "titleField": "name",
              "blocks": [ { "code": "main", "label": { "zh-TW": "基本" }, "collapsed": false, "columns": 2, "fields": [
                { "code": "name", "type": "text", "label": { "zh-TW": "name" }, "config": { "required": true } } ] } ],
              "permissions": [ {{Perm("sales")}}, {{Perm("lead")}} ] },
            { "code": "quote", "key": "{{appId}}-quote", "name": { "zh-TW": "報價" }, "kind": "standalone", "titleField": "no",
              "blocks": [ { "code": "main", "label": { "zh-TW": "基本" }, "collapsed": false, "columns": 2, "fields": [
                { "code": "no", "type": "autonumber", "label": { "zh-TW": "no" }, "config": { "format": "Q-{0000}" } },
                { "code": "customer", "type": "reference", "label": { "zh-TW": "customer" },
                  "config": { "refTarget": { "kind": "module", "key": "customer" }{{fill}} } },
                { "code": "customerName", "type": "text", "label": { "zh-TW": "customerName" }, "config": { "readOnly": true } },
                { "code": "status", "type": "enum", "label": { "zh-TW": "status" },
                  "options": [ { "code": "draft", "label": { "zh-TW": "草稿" }, "color": null },
                               { "code": "approved", "label": { "zh-TW": "已核准" }, "color": null } ],
                  "config": { "defaultValue": "opt_draft", "required": true } } ] } ],
              "permissions": [ {{Perm("sales")}}, {{Perm("lead")}} ] },
            { "code": "quote-line", "key": "{{appId}}-quote-line", "name": { "zh-TW": "報價明細" }, "kind": "standalone", "titleField": "item",
              "blocks": [ { "code": "main", "label": { "zh-TW": "基本" }, "collapsed": false, "columns": 2, "fields": [
                { "code": "quote", "type": "reference", "label": { "zh-TW": "quote" },
                  "config": { "refTarget": { "kind": "module", "key": "quote" }, "relation": "detail" } },
                { "code": "item", "type": "text", "label": { "zh-TW": "item" }, "config": { "required": true } },
                { "code": "qty", "type": "decimal", "label": { "zh-TW": "qty" }, "config": { "scale": 2 } } ] } ] },
            { "code": "order", "key": "{{appId}}-order", "name": { "zh-TW": "訂單" }, "kind": "standalone", "titleField": "no",
              "blocks": [ { "code": "main", "label": { "zh-TW": "基本" }, "collapsed": false, "columns": 2, "fields": [
                { "code": "no", "type": "autonumber", "label": { "zh-TW": "no" }, "config": { "format": "O-{0000}" } },
                { "code": "fromQuote", "type": "reference", "label": { "zh-TW": "fromQuote" },
                  "config": { "readOnly": true, "refTarget": { "kind": "module", "key": "quote" } } },
                { "code": "customerName", "type": "text", "label": { "zh-TW": "customerName" }, "config": {} } ] } ],
              "permissions": [ {{Perm("sales")}}, {{Perm("lead")}} ] },
            { "code": "order-line", "key": "{{appId}}-order-line", "name": { "zh-TW": "訂單明細" }, "kind": "standalone", "titleField": "item",
              "blocks": [ { "code": "main", "label": { "zh-TW": "基本" }, "collapsed": false, "columns": 2, "fields": [
                { "code": "order", "type": "reference", "label": { "zh-TW": "order" },
                  "config": { "refTarget": { "kind": "module", "key": "order" }, "relation": "detail" } },
                { "code": "item", "type": "text", "label": { "zh-TW": "item" }, "config": { "required": true } },
                { "code": "qty", "type": "decimal", "label": { "zh-TW": "qty" }, "config": { "scale": 2 } },
                { "code": "fromLine", "type": "integer", "label": { "zh-TW": "fromLine" }, "config": { "readOnly": true } } ] } ] }
          ]
          {{sections}}
        }
        """;
    }
}
