namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 共用主檔的開啟、權限、生命週期測試用的虛構套件（只在測試專案註冊，id 都不是內建的）：
/// 主檔 bp（往來對象）、ct（聯絡人，需要 bp）；使用者 crmx（CRM，需要 bp 與 ct）、stk（進銷存，需要 bp）、
/// mx（MES，需要 bp）、cx（只列 ct）。
/// </summary>
internal static class SharedMasterPackages
{
    private const string All = """{ "read": true, "add": true, "edit": true, "delete": true, "printExport": true, "pricing": true, "approve": true }""";
    private const string ReadOnly = """{ "read": true, "add": false, "edit": false, "delete": false, "printExport": true, "pricing": false, "approve": false }""";
    private const string ReadEdit = """{ "read": true, "add": true, "edit": true, "delete": false, "printExport": false, "pricing": false, "approve": false }""";
    private const string Write = """{ "read": true, "add": true, "edit": true, "delete": true, "printExport": true, "pricing": true, "approve": false }""";

    public static readonly string Bp = $$"""
        {
          "format": "formwright-app", "formatVersion": 6,
          "manifest": { "id": "bp", "kind": "master", "version": "1.0.0", "name": { "zh-TW": "往來對象" } },
          "groups": [ { "code": "steward", "name": { "zh-TW": "往來對象管理員" } } ],
          "modules": [ {
            "code": "partner", "key": "master-bp-partner", "name": { "zh-TW": "往來對象" }, "kind": "standalone",
            "titleField": "name", "section": "主檔", "icon": "handshake", "extensible": true,
            "blocks": [ { "code": "main", "label": { "zh-TW": "基本資料" }, "fields": [
              { "code": "no", "type": "autonumber", "label": { "zh-TW": "編號" }, "config": { "format": "BP-{0000}" } },
              { "code": "name", "type": "text", "label": { "zh-TW": "名稱" }, "config": { "required": true, "showInList": true } },
              { "code": "role", "type": "enum", "label": { "zh-TW": "角色" }, "config": {},
                "options": [ { "code": "customer", "label": { "zh-TW": "客戶" } }, { "code": "supplier", "label": { "zh-TW": "供應商" } },
                             { "code": "both", "label": { "zh-TW": "兩者" } } ] },
              { "code": "phone", "type": "phone", "label": { "zh-TW": "電話" }, "config": {} },
              { "code": "headcount", "type": "integer", "label": { "zh-TW": "人數" }, "config": {} },
              { "code": "taxId", "type": "text", "label": { "zh-TW": "統一編號" }, "config": {}, "active": false }
            ] } ],
            "permissions": [ { "group": "steward", "scope": "all", "privileges": {{All}} } ]
          } ]
        }
        """;

    public static readonly string Ct = $$"""
        {
          "format": "formwright-app", "formatVersion": 6,
          "manifest": { "id": "ct", "kind": "master", "version": "1.0.0", "name": { "zh-TW": "聯絡人" },
            "requires": { "masters": [ { "id": "bp", "modules": ["partner"] } ] } },
          "groups": [ { "code": "steward", "name": { "zh-TW": "聯絡人管理員" } } ],
          "modules": [ {
            "code": "contact", "key": "master-ct-contact", "name": { "zh-TW": "聯絡人" }, "kind": "standalone",
            "titleField": "name", "section": "主檔", "extensible": true,
            "blocks": [ { "code": "main", "label": { "zh-TW": "基本資料" }, "fields": [
              { "code": "name", "type": "text", "label": { "zh-TW": "姓名" }, "config": { "required": true } },
              { "code": "partner", "type": "reference", "label": { "zh-TW": "公司" },
                "config": { "onDelete": "restrict", "refTarget": { "kind": "module", "key": "partner", "app": "bp" } } }
            ] } ],
            "permissions": [ { "group": "steward", "scope": "all", "privileges": {{All}} } ]
          } ]
        }
        """;

    /// <summary>CRM：潛在客戶轉往來對象（目標是主檔、追溯欄位是貢獻欄位）；貢獻產業、來源潛在客戶、主要聯絡人、分數與一個引用基本欄位的公式。</summary>
    public static readonly string Crm = $$"""
        {
          "format": "formwright-app", "formatVersion": 6,
          "manifest": { "id": "crmx", "version": "1.0.0", "name": { "zh-TW": "客戶關係" },
            "requires": { "masters": [ { "id": "bp", "modules": ["partner"] }, { "id": "ct", "modules": ["contact"] } ] } },
          "groups": [ { "code": "sales", "name": { "zh-TW": "業務" } } ],
          "modules": [
            { "code": "lead", "key": "crmx-lead", "name": { "zh-TW": "潛在客戶" }, "kind": "standalone", "titleField": "company",
              "blocks": [ { "code": "main", "label": { "zh-TW": "基本資料" }, "fields": [
                { "code": "company", "type": "text", "label": { "zh-TW": "公司" }, "config": { "required": true } },
                { "code": "phone", "type": "phone", "label": { "zh-TW": "電話" }, "config": {} }
              ] } ],
              "permissions": [ { "group": "sales", "scope": "all", "privileges": {{Write}} } ] },
            { "code": "order", "key": "crmx-order", "name": { "zh-TW": "訂單" }, "kind": "standalone", "titleField": "no",
              "blocks": [ { "code": "main", "label": { "zh-TW": "表頭" }, "fields": [
                { "code": "no", "type": "text", "label": { "zh-TW": "單號" }, "config": { "required": true } },
                { "code": "customer", "type": "reference", "label": { "zh-TW": "客戶" },
                  "config": { "onDelete": "restrict", "refTarget": { "kind": "module", "key": "partner", "app": "bp" },
                    "refGuard": { "field": "bp/partner.role", "in": ["customer", "both"] },
                    "fill": { "mode": "default", "map": [ { "from": "bp/partner.phone", "to": "phone" } ] } } },
                { "code": "phone", "type": "phone", "label": { "zh-TW": "電話" }, "config": {} }
              ] } ],
              "permissions": [ { "group": "sales", "scope": "all", "privileges": {{Write}} } ] }
          ],
          "conversions": [ {
            "code": "crmx-lead-to-partner", "name": { "zh-TW": "轉成往來對象" }, "active": true,
            "source": "lead", "target": "bp/partner", "sourceStates": [],
            "header": { "sourceField": "bp/partner.fromLead",
              "copy": [ { "from": "lead.company", "to": "bp/partner.name" }, { "from": "lead.phone", "to": "bp/partner.phone" } ] }
          } ],
          "contributions": [ {
            "master": "bp", "module": "partner",
            "block": { "code": "crm", "name": { "zh-TW": "CRM" } },
            "fields": [
              { "code": "industry", "type": "enum", "label": { "zh-TW": "產業" }, "config": {},
                "options": [ { "code": "manufacturing", "label": { "zh-TW": "製造" } }, { "code": "retail", "label": { "zh-TW": "零售" } } ] },
              { "code": "fromLead", "type": "reference", "label": { "zh-TW": "來源潛在客戶" },
                "config": { "readOnly": true, "onDelete": "setNull", "refTarget": { "kind": "module", "key": "lead" } } },
              { "code": "mainContact", "type": "reference", "label": { "zh-TW": "主要聯絡人" },
                "config": { "onDelete": "setNull", "refTarget": { "kind": "module", "key": "contact", "app": "ct" } } },
              { "code": "score", "type": "integer", "label": { "zh-TW": "分數" }, "config": {} },
              { "code": "headX2", "type": "formula", "label": { "zh-TW": "人數兩倍" }, "config": { "formula": "{headcount} * 2", "resultType": "number" } }
            ],
            "permissions": [ { "group": "sales", "scope": "all", "privileges": {{ReadOnly}} } ]
          } ]
        }
        """;

    /// <summary>進銷存：出貨單參照往來對象；貢獻送貨地址。對主檔授予「讀取＋編輯／own」。</summary>
    public static readonly string Stock = $$"""
        {
          "format": "formwright-app", "formatVersion": 6,
          "manifest": { "id": "stk", "version": "1.0.0", "name": { "zh-TW": "進銷存" },
            "requires": { "masters": [ { "id": "bp", "modules": ["partner"] } ] } },
          "groups": [ { "code": "clerk", "name": { "zh-TW": "倉管" } } ],
          "modules": [
            { "code": "ship", "key": "stk-ship", "name": { "zh-TW": "出貨單" }, "kind": "standalone", "titleField": "no",
              "blocks": [ { "code": "main", "label": { "zh-TW": "表頭" }, "fields": [
                { "code": "no", "type": "text", "label": { "zh-TW": "單號" }, "config": { "required": true } },
                { "code": "customer", "type": "reference", "label": { "zh-TW": "客戶" },
                  "config": { "onDelete": "restrict", "refTarget": { "kind": "module", "key": "partner", "app": "bp" },
                    "fill": { "mode": "default", "map": [ { "from": "bp/partner.phone", "to": "phone" } ] } } },
                { "code": "phone", "type": "phone", "label": { "zh-TW": "電話" }, "config": {} }
              ] } ],
              "permissions": [ { "group": "clerk", "scope": "all", "privileges": {{Write}} } ] }
          ],
          "contributions": [ {
            "master": "bp", "module": "partner",
            "block": { "code": "stock", "name": { "zh-TW": "進銷存" } },
            "fields": [ { "code": "shipAddress", "type": "text", "label": { "zh-TW": "送貨地址" }, "config": {} } ],
            "permissions": [ { "group": "clerk", "scope": "own", "privileges": {{ReadEdit}} } ]
          } ]
        }
        """;

    /// <summary>MES：只貢獻網站。</summary>
    public static readonly string Mes = $$"""
        {
          "format": "formwright-app", "formatVersion": 6,
          "manifest": { "id": "mx", "version": "1.0.0", "name": { "zh-TW": "製造" },
            "requires": { "masters": [ { "id": "bp", "modules": ["partner"] } ] } },
          "groups": [ { "code": "floor", "name": { "zh-TW": "現場" } } ],
          "modules": [
            { "code": "job", "key": "mx-job", "name": { "zh-TW": "工令" }, "kind": "standalone", "titleField": "no",
              "blocks": [ { "code": "main", "label": { "zh-TW": "表頭" }, "fields": [
                { "code": "no", "type": "text", "label": { "zh-TW": "工令號" }, "config": { "required": true } },
                { "code": "customer", "type": "reference", "label": { "zh-TW": "客戶" },
                  "config": { "onDelete": "restrict", "refTarget": { "kind": "module", "key": "partner", "app": "bp" } } }
              ] } ],
              "permissions": [ { "group": "floor", "scope": "all", "privileges": {{Write}} } ] }
          ],
          "contributions": [ {
            "master": "bp", "module": "partner",
            "block": { "code": "mes", "name": { "zh-TW": "MES" } },
            "fields": [ { "code": "website", "type": "url", "label": { "zh-TW": "網站" }, "config": {} } ],
            "permissions": [ { "group": "floor", "scope": "all", "privileges": {{ReadOnly}} } ]
          } ]
        }
        """;

    /// <summary>只列聯絡人主檔的套件：經聯絡人間接使用往來對象。</summary>
    public static readonly string ContactOnly = $$"""
        {
          "format": "formwright-app", "formatVersion": 6,
          "manifest": { "id": "cx", "version": "1.0.0", "name": { "zh-TW": "拜訪紀錄" },
            "requires": { "masters": [ { "id": "ct", "modules": ["contact"] } ] } },
          "groups": [],
          "modules": [
            { "code": "visit", "key": "cx-visit", "name": { "zh-TW": "拜訪" }, "kind": "standalone", "titleField": "subject",
              "blocks": [ { "code": "main", "label": { "zh-TW": "內容" }, "fields": [
                { "code": "subject", "type": "text", "label": { "zh-TW": "主旨" }, "config": { "required": true } },
                { "code": "contact", "type": "reference", "label": { "zh-TW": "聯絡人" },
                  "config": { "onDelete": "restrict", "refTarget": { "kind": "module", "key": "contact", "app": "ct" } } }
              ] } ],
              "permissions": [] }
          ]
        }
        """;

    public static readonly string[] Masters = [Bp, Ct];
}
