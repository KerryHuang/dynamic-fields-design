namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 格式 4 的測試專用套件（只放測試專案、不隨產品發行）：虛構的「過帳」情境，
/// 單據（草稿 → 完成，完成為唯讀）含明細；轉為完成時每列明細產生一筆異動，異動是分錄（只能沖銷）且同品項加總不得為負。
/// 欄位標籤等於欄位代號。
/// </summary>
internal static class AppTestPackagesV4
{
    public const string DefaultEvents = """
        [ { "code": "ledger-flow-post", "name": { "zh-TW": "完成時過帳" }, "active": true, "source": "doc",
            "trigger": { "on": "transition", "to": "done" }, "when": null,
            "effects": [ { "code": "post", "kind": "create", "target": "move", "each": "doc-line",
              "set": [ { "to": "move.item", "from": { "side": "line", "field": "doc-line.item" } },
                       { "to": "move.qty", "formula": { "side": "line", "expr": "-{doc-line.qty}" } } ] } ] } ]
        """;

    public const string DefaultChecks = """
        [ { "code": "ledger-flow-nonneg", "name": { "zh-TW": "不可為負" }, "active": true, "module": "move",
            "groupBy": ["move.item"], "function": "sum", "field": "move.qty", "where": null,
            "assert": { "op": "gte", "limit": { "const": 0 } }, "message": { "zh-TW": "庫存不足" } } ]
        """;

    public const string DefaultLedgers = """
        [ { "module": "move", "reversalField": "move.reverses", "negate": ["move.qty"], "swap": [] } ]
        """;

    public static string Flow(
        int formatVersion = 4, string? events = null, string? checks = null, string? ledgers = null, string? fieldAccess = null,
        string appId = "ledger-flow", string extraMoveFields = "")
    {
        events ??= DefaultEvents;
        checks ??= DefaultChecks;
        ledgers ??= DefaultLedgers;
        fieldAccess ??= "[]";
        return $$"""
        {
          "format": "formwright-app", "formatVersion": {{formatVersion}},
          "manifest": { "id": "{{appId}}", "version": "1.0.0", "name": { "zh-TW": "過帳測試" } },
          "groups": [ { "code": "lead", "name": { "zh-TW": "測試主管" } } ],
          "modules": [
            { "code": "doc", "key": "{{appId}}-doc", "name": { "zh-TW": "單據" }, "kind": "standalone", "titleField": "no",
              "blocks": [ { "code": "main", "label": { "zh-TW": "基本" }, "collapsed": false, "columns": 2, "fields": [
                { "code": "no", "type": "autonumber", "label": { "zh-TW": "no" }, "config": { "format": "D-{0000}" } },
                { "code": "status", "type": "enum", "label": { "zh-TW": "status" },
                  "options": [ { "code": "draft", "label": { "zh-TW": "草稿" }, "color": null },
                               { "code": "done", "label": { "zh-TW": "完成" }, "color": null } ],
                  "config": { "defaultValue": "opt_draft", "required": true } } ] } ] },
            { "code": "doc-line", "key": "{{appId}}-doc-line", "name": { "zh-TW": "單據明細" }, "kind": "standalone", "titleField": "item",
              "blocks": [ { "code": "main", "label": { "zh-TW": "基本" }, "collapsed": false, "columns": 2, "fields": [
                { "code": "doc", "type": "reference", "label": { "zh-TW": "doc" },
                  "config": { "refTarget": { "kind": "module", "key": "doc" }, "relation": "detail" } },
                { "code": "item", "type": "text", "label": { "zh-TW": "item" }, "config": { "required": true } },
                { "code": "qty", "type": "decimal", "label": { "zh-TW": "qty" }, "config": { "scale": 2 } } ] } ] },
            { "code": "move", "key": "{{appId}}-move", "name": { "zh-TW": "異動" }, "kind": "standalone", "titleField": "item",
              "blocks": [ { "code": "main", "label": { "zh-TW": "基本" }, "collapsed": false, "columns": 2, "fields": [
                { "code": "item", "type": "text", "label": { "zh-TW": "item" }, "config": { "required": true, "isFilterable": true } },
                { "code": "qty", "type": "decimal", "label": { "zh-TW": "qty" }, "config": { "scale": 2, "isFilterable": true } },
                { "code": "reverses", "type": "reference", "label": { "zh-TW": "reverses" },
                  "config": { "readOnly": true, "refTarget": { "kind": "module", "key": "move" } } }{{extraMoveFields}} ] } ] }
          ],
          "fieldAccess": {{fieldAccess}},
          "workflows": [ { "module": "doc", "statusField": "status", "initial": "draft",
                           "states": [ { "option": "draft" }, { "option": "done", "readOnly": true } ],
                           "transitions": [ { "from": "draft", "to": "done", "groups": ["lead"] } ] } ],
          "events": {{events}}, "checks": {{checks}}, "ledgers": {{ledgers}}
        }
        """;
    }
}
