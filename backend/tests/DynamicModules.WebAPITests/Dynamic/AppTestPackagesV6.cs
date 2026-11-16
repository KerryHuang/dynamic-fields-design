namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 格式 6（共用主檔）的測試套件：虛構的主檔 partner-lite 與在它上面貢獻欄位的 crm6-lite。id 都不是內建的，
/// 測試靠字串替換做出各種錯誤。
/// </summary>
internal static class AppTestPackagesV6
{
    private const string AllPerms = """
        { "read": true, "add": true, "edit": true, "delete": true, "printExport": true, "pricing": true, "approve": true }
        """;

    private const string SalesPerms = """
        { "read": true, "add": true, "edit": true, "delete": false, "printExport": true, "pricing": true, "approve": false }
        """;

    public static readonly string Master = $$"""
        {
          "format": "formwright-app", "formatVersion": 6,
          "manifest": { "id": "partner-lite", "kind": "master", "version": "1.0.0", "name": { "zh-TW": "往來對象（測試）" } },
          "groups": [ { "code": "steward", "name": { "zh-TW": "往來對象管理員" } } ],
          "modules": [ {
            "code": "partner", "key": "master-partner-lite", "name": { "zh-TW": "往來對象" }, "kind": "standalone",
            "titleField": "name", "section": "主檔", "icon": "handshake", "extensible": true,
            "blocks": [ { "code": "main", "label": { "zh-TW": "基本資料" }, "fields": [
              { "code": "no", "type": "autonumber", "label": { "zh-TW": "編號" }, "config": { "format": "BP-{0000}", "showInList": true } },
              { "code": "name", "type": "text", "label": { "zh-TW": "名稱" }, "config": { "required": true, "showInList": true, "isFilterable": true } },
              { "code": "role", "type": "enum", "label": { "zh-TW": "角色" }, "config": { "isFilterable": true },
                "options": [
                  { "code": "customer", "label": { "zh-TW": "客戶" } },
                  { "code": "supplier", "label": { "zh-TW": "供應商" } },
                  { "code": "both", "label": { "zh-TW": "兩者" } } ] },
              { "code": "phone", "type": "phone", "label": { "zh-TW": "電話" }, "config": {} },
              { "code": "taxId", "type": "text", "label": { "zh-TW": "統一編號" }, "config": {}, "active": false }
            ] } ],
            "permissions": [ { "group": "steward", "scope": "all", "privileges": {{AllPerms}} } ]
          } ]
        }
        """;

    /// <summary>用到主檔的應用程式：參照、守衛、帶值、轉換（目標是主檔）、報表與貢獻。帶固定值 header.set 的版本見 <see cref="CrmWithSet"/>。</summary>
    public static readonly string Crm = $$"""
        {
          "format": "formwright-app", "formatVersion": 6,
          "manifest": {
            "id": "crm6-lite", "version": "1.0.0", "name": { "zh-TW": "客戶關係（測試）" },
            "requires": { "apps": [], "entities": [], "masters": [ { "id": "partner-lite", "modules": ["partner"] } ] }
          },
          "groups": [ { "code": "sales", "name": { "zh-TW": "業務" } } ],
          "modules": [
            { "code": "lead", "key": "crm6-lead", "name": { "zh-TW": "潛在客戶" }, "kind": "standalone", "titleField": "company",
              "blocks": [ { "code": "main", "label": { "zh-TW": "基本資料" }, "fields": [
                { "code": "company", "type": "text", "label": { "zh-TW": "公司" }, "config": { "required": true } },
                { "code": "phone", "type": "phone", "label": { "zh-TW": "電話" }, "config": {} }
              ] } ],
              "permissions": [ { "group": "sales", "scope": "all", "privileges": {{SalesPerms}} } ] },
            { "code": "order", "key": "crm6-order", "name": { "zh-TW": "訂單" }, "kind": "standalone", "titleField": "no",
              "blocks": [ { "code": "main", "label": { "zh-TW": "表頭" }, "fields": [
                { "code": "no", "type": "text", "label": { "zh-TW": "單號" }, "config": { "required": true } },
                { "code": "customer", "type": "reference", "label": { "zh-TW": "客戶" },
                  "config": { "required": true, "onDelete": "restrict",
                    "refTarget": { "kind": "module", "key": "partner", "app": "partner-lite" },
                    "refGuard": { "field": "partner-lite/partner.role", "in": ["customer", "both"] },
                    "fill": { "mode": "default", "map": [ { "from": "partner-lite/partner.phone", "to": "phone" } ] } } },
                { "code": "phone", "type": "phone", "label": { "zh-TW": "電話" }, "config": {} }
              ] } ],
              "permissions": [ { "group": "sales", "scope": "all", "privileges": {{SalesPerms}} } ] }
          ],
          "conversions": [ {
            "code": "crm6-lead-to-partner", "name": { "zh-TW": "轉成客戶" }, "active": true,
            "source": "lead", "target": "partner-lite/partner", "sourceStates": [],
            "header": {
              "sourceField": "partner-lite/partner.fromLead",
              "copy": [ { "from": "lead.company", "to": "partner-lite/partner.name" }, { "from": "lead.phone", "to": "partner-lite/partner.phone" } ] }
          } ],
          "reports": [ {
            "code": "byIndustry", "name": "依產業", "shareWith": ["sales"],
            "definition": { "version": 1, "mainModule": "partner-lite/partner", "child": null,
              "columns": [],
              "groups": [ { "field": { "source": "main", "field": "partner-lite/partner.industry", "via": null }, "bucket": null } ],
              "measures": [ { "id": "n", "kind": "countMain", "field": null, "condition": null } ],
              "filters": [], "primaryDateField": null, "dateRange": null, "sort": [], "viz": "donut" }
          } ],
          "contributions": [ {
            "master": "partner-lite", "module": "partner",
            "block": { "code": "crm", "name": { "zh-TW": "CRM" } },
            "fields": [
              { "code": "industry", "type": "enum", "label": { "zh-TW": "產業" }, "config": { "isFilterable": true },
                "options": [ { "code": "manufacturing", "label": { "zh-TW": "製造" } } ] },
              { "code": "fromLead", "type": "reference", "label": { "zh-TW": "來源潛在客戶" },
                "config": { "readOnly": true, "onDelete": "setNull", "refTarget": { "kind": "module", "key": "lead" } } },
              { "code": "credit", "type": "currency", "label": { "zh-TW": "信用額度" }, "config": {} },
              { "code": "nameLength", "type": "formula", "label": { "zh-TW": "名稱長度" }, "config": { "formula": "{credit} * 2", "resultType": "number" }, "active": false }
            ],
            "permissions": [ { "group": "sales", "scope": "all", "privileges": {{SalesPerms}} } ],
            "fieldAccess": [ { "field": "partner-lite/partner.credit", "group": "sales", "access": "readOnly" } ]
          } ]
        }
        """;

    private const string CopyEnd = "{ \"from\": \"lead.phone\", \"to\": \"partner-lite/partner.phone\" } ]";

    /// <summary><see cref="Crm"/> 的轉換加上固定值：把新往來對象的 role 設成 customer（設計 §4.5）。</summary>
    public static readonly string CrmWithSet = Crm.Replace(CopyEnd,
        CopyEnd + ",\n              \"set\": [ { \"to\": \"partner-lite/partner.role\", \"option\": \"customer\" } ]");

    /// <summary>不用共用主檔的格式 6 套件：轉換目標是本套件的一般模組，固定值寫入目標的下拉欄位。</summary>
    public const string LocalSet = """
        {
          "format": "formwright-app", "formatVersion": 6,
          "manifest": { "id": "set-lite", "version": "1.0.0", "name": { "zh-TW": "固定值（測試）" }, "requires": { "apps": [], "entities": [] } },
          "groups": [],
          "modules": [
            { "code": "lead", "key": "set-lead", "name": { "zh-TW": "潛在客戶" }, "kind": "standalone", "titleField": "company",
              "blocks": [ { "code": "main", "label": { "zh-TW": "基本資料" }, "fields": [
                { "code": "company", "type": "text", "label": { "zh-TW": "公司" }, "config": { "required": true } }
              ] } ],
              "permissions": [] },
            { "code": "customer", "key": "set-customer", "name": { "zh-TW": "客戶" }, "kind": "standalone", "titleField": "name",
              "blocks": [ { "code": "main", "label": { "zh-TW": "基本資料" }, "fields": [
                { "code": "name", "type": "text", "label": { "zh-TW": "名稱" }, "config": { "required": true } },
                { "code": "role", "type": "enum", "label": { "zh-TW": "角色" }, "config": {},
                  "options": [ { "code": "customer", "label": { "zh-TW": "客戶" } }, { "code": "supplier", "label": { "zh-TW": "供應商" } } ] },
                { "code": "fromLead", "type": "reference", "label": { "zh-TW": "來源潛在客戶" },
                  "config": { "readOnly": true, "onDelete": "setNull", "refTarget": { "kind": "module", "key": "lead" } } }
              ] } ],
              "permissions": [] }
          ],
          "conversions": [ {
            "code": "set-lead-to-customer", "name": { "zh-TW": "轉成客戶" }, "active": true,
            "source": "lead", "target": "customer", "sourceStates": [],
            "header": {
              "sourceField": "customer.fromLead",
              "copy": [ { "from": "lead.company", "to": "customer.name" } ],
              "set": [ { "to": "customer.role", "option": "customer" } ] }
          } ]
        }
        """;
}
