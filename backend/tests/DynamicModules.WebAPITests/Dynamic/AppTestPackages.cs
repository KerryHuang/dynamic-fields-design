namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 測試專用的虛構套件，只放測試專案、不隨產品發行。兩個不相干的情境（客戶、採購）加一個依賴客戶的報價，
/// 用來證明開關與相依機制不認得任何特定應用程式。
/// </summary>
internal static class AppTestPackages
{
    private const string Perms = """
        { "read": true, "add": true, "edit": true, "delete": false, "printExport": false, "pricing": false, "approve": false }
        """;

    private const string LeadPerms = """
        { "read": true, "add": true, "edit": true, "delete": true, "printExport": true, "pricing": false, "approve": false }
        """;

    /// <summary>客戶與聯絡人（設計文件 §4.10 的範例，去掉保留中的 reports）。</summary>
    public static readonly string Crm = $$"""
        {
          "format": "formwright-app", "formatVersion": 2,
          "manifest": {
            "id": "crm-lite", "version": "1.0.0",
            "name": { "zh-TW": "客戶與聯絡人", "en-US": "Customers and contacts" },
            "icon": "contacts", "vendor": "Example Studio",
            "requires": { "apps": [], "entities": [] }
          },
          "groups": [
            { "code": "sales", "name": { "zh-TW": "客戶業務", "en-US": "Sales" } },
            { "code": "salesLead", "name": { "zh-TW": "客戶業務主管", "en-US": "Sales lead" } }
          ],
          "modules": [
            { "code": "customer", "key": "crm-customer", "name": { "zh-TW": "客戶", "en-US": "Customer" },
              "icon": "business", "section": "客戶關係", "kind": "standalone", "hostEntityKey": null, "titleField": "name",
              "blocks": [ { "code": "main", "label": { "zh-TW": "基本資料" }, "collapsed": false, "columns": 2, "fields": [
                { "code": "name", "type": "text", "label": { "zh-TW": "客戶名稱" },
                  "config": { "required": true, "maxLength": 100, "showInList": true, "isFilterable": true, "isSortable": true } },
                { "code": "status", "type": "enum", "label": { "zh-TW": "狀態" },
                  "options": [
                    { "code": "lead", "label": { "zh-TW": "潛在" }, "color": null },
                    { "code": "active", "label": { "zh-TW": "往來中" }, "color": null },
                    { "code": "dormant", "label": { "zh-TW": "停止往來" }, "color": null } ],
                  "config": { "defaultValue": "opt_lead", "required": true, "showInList": true, "isFilterable": true } },
                { "code": "phone", "type": "phone", "label": { "zh-TW": "電話" }, "config": { "showInList": true } },
                { "code": "dormantReason", "type": "textarea", "label": { "zh-TW": "停止往來原因" }, "config": {} },
                { "code": "contactCount", "type": "rollup", "label": { "zh-TW": "聯絡人數" },
                  "config": { "showInList": true, "rollup": { "sourceField": "contact.customer", "function": "count" } } }
              ] } ],
              "rules": [
                { "kind": "visibility", "targets": ["field:dormantReason"], "active": true, "options": [],
                  "condition": { "all": [ { "field": "status", "op": "eq", "value": "opt_dormant" } ] } } ],
              "views": [ { "name": { "zh-TW": "全部客戶" }, "columns": ["name", "status", "phone", "contactCount"],
                "filter": null, "sort": [ { "field": "name", "order": "asc" } ], "isDefault": true } ],
              "permissions": [
                { "group": "sales", "scope": "own", "privileges": {{Perms}} },
                { "group": "salesLead", "scope": "all", "privileges": {{LeadPerms}} } ] },
            { "code": "contact", "key": "crm-contact", "name": { "zh-TW": "聯絡人", "en-US": "Contact" },
              "icon": "person", "section": "客戶關係", "kind": "standalone", "hostEntityKey": null, "titleField": "name",
              "blocks": [ { "code": "main", "label": { "zh-TW": "基本資料" }, "collapsed": false, "columns": 2, "fields": [
                { "code": "name", "type": "text", "label": { "zh-TW": "姓名" },
                  "config": { "required": true, "maxLength": 50, "showInList": true, "isFilterable": true, "isSortable": true } },
                { "code": "customer", "type": "reference", "label": { "zh-TW": "客戶" },
                  "config": { "required": true, "showInList": true, "isFilterable": true,
                              "refTarget": { "kind": "module", "key": "customer" }, "onDelete": "restrict" } },
                { "code": "email", "type": "email", "label": { "zh-TW": "電子郵件" }, "config": { "showInList": true } }
              ] } ],
              "rules": [], "views": [],
              "permissions": [
                { "group": "sales", "scope": "own", "privileges": {{Perms}} },
                { "group": "salesLead", "scope": "all", "privileges": {{LeadPerms}} } ] }
          ],
          "fieldAccess": [ { "field": "customer.phone", "group": "sales", "access": "readOnly" } ],
          "optionScopes": [ { "option": "customer.dormant", "groups": ["salesLead"] } ]
        }
        """;

    /// <summary>報價：依賴 crm-lite 的客戶模組。</summary>
    public static readonly string Quote = $$"""
        {
          "format": "formwright-app", "formatVersion": 2,
          "manifest": {
            "id": "quote-lite", "version": "1.0.0", "name": { "zh-TW": "簡易報價" },
            "requires": { "apps": [ { "id": "crm-lite", "modules": ["customer"] } ], "entities": [] }
          },
          "groups": [ { "code": "sales", "name": { "zh-TW": "報價業務" } } ],
          "modules": [
            { "code": "quote", "key": "quote-lite-quote", "name": { "zh-TW": "報價單" }, "icon": "request_quote", "section": "報價",
              "kind": "standalone", "titleField": "no",
              "blocks": [ { "code": "main", "label": { "zh-TW": "報價" }, "collapsed": false, "columns": 2, "fields": [
                { "code": "no", "type": "autonumber", "label": { "zh-TW": "報價單號" },
                  "config": { "format": "Q-{yyyyMM}-{0000}", "showInList": true } },
                { "code": "customer", "type": "reference", "label": { "zh-TW": "客戶" },
                  "config": { "showInList": true, "refTarget": { "kind": "module", "key": "customer", "app": "crm-lite" } } }
              ] } ],
              "permissions": [ { "group": "sales", "scope": "all", "privileges": {{LeadPerms}} } ] }
          ]
        }
        """;

    /// <summary>採購：與客戶、報價完全不相干的第二個情境（供應商與採購單）。</summary>
    public static readonly string Procure = $$"""
        {
          "format": "formwright-app", "formatVersion": 2,
          "manifest": { "id": "procure-lite", "version": "0.1.0", "name": { "zh-TW": "簡易採購" } },
          "groups": [ { "code": "buyer", "name": { "zh-TW": "採購" } } ],
          "modules": [
            { "code": "supplier", "key": "pl-supplier", "name": { "zh-TW": "供應商" }, "kind": "standalone", "titleField": "name",
              "blocks": [ { "code": "main", "label": { "zh-TW": "基本資料" }, "collapsed": false, "columns": 2, "fields": [
                { "code": "name", "type": "text", "label": { "zh-TW": "名稱" }, "config": { "required": true, "showInList": true } } ] } ],
              "permissions": [ { "group": "buyer", "scope": "all", "privileges": {{LeadPerms}} } ] },
            { "code": "po", "key": "pl-po", "name": { "zh-TW": "採購單" }, "kind": "standalone", "titleField": "no",
              "blocks": [ { "code": "main", "label": { "zh-TW": "採購" }, "collapsed": false, "columns": 2, "fields": [
                { "code": "no", "type": "autonumber", "label": { "zh-TW": "採購單號" }, "config": { "format": "PO-{0000}" } },
                { "code": "supplier", "type": "reference", "label": { "zh-TW": "供應商" },
                  "config": { "refTarget": { "kind": "module", "key": "supplier" } } } ] } ],
              "permissions": [ { "group": "buyer", "scope": "all", "privileges": {{LeadPerms}} } ] }
          ]
        }
        """;

    /// <summary>把 Crm 換成不同的 id，用來測試需要第二份客戶情境的案例。</summary>
    public static string With(string json, string find, string replace) => json.Replace(find, replace);
}
