using System.Net;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>格式 4 套件的漏洞回歸（第一輪實作審查 c）：固定記錄／帳號、不存在的選項、事件安裝順序。</summary>
public class AppV4HardeningTests : IDisposable
{
    private const string Apps = Base + "/apps";

    private readonly TestApp _app = new();
    private readonly HttpClient _admin;

    public AppV4HardeningTests() => _admin = _app.AsUser();

    public void Dispose() => _app.Dispose();

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    private async Task<(HttpStatusCode Status, string Text)> Import(string json)
    {
        var res = await _admin.PostAsync($"{Apps}/packages/import", Body(json));
        return (res.StatusCode, await res.Content.ReadAsStringAsync());
    }

    private const string CreateEffect = """
        { "code": "post", "kind": "create", "target": "move", "each": "doc-line",
          "set": [ { "to": "move.item", "from": { "side": "line", "field": "doc-line.item" } }{0} ] }
        """;

    private static string EventWith(string extraSet) =>
        $$"""
        [ { "code": "ledger-flow-post", "name": { "zh-TW": "完成時過帳" }, "active": true, "source": "doc",
            "trigger": { "on": "transition", "to": "done" }, "when": null,
            "effects": [ {{CreateEffect.Replace("{0}", extraSet)}} ] } ]
        """;

    [Fact]
    public async Task A_constant_record_id_on_a_reference_field_is_refused()
    {
        var extra = """, { "code": "src", "type": "reference", "label": { "zh-TW": "src" }, "config": { "refTarget": { "kind": "module", "key": "doc" } } }""";
        var events = EventWith(""", { "to": "move.src", "const": 42 }""");

        var (status, text) = await Import(AppTestPackagesV4.Flow(events: events, extraMoveFields: extra));

        status.Should().Be(HttpStatusCode.BadRequest, text);
        text.Should().Contain("套件不收");
    }

    [Fact]
    public async Task A_check_condition_naming_a_user_account_is_refused_like_the_export_does()
    {
        var extra = """, { "code": "owner", "type": "user", "label": { "zh-TW": "owner" }, "config": { "isFilterable": true } }""";
        var checks = """
            [ { "code": "ledger-flow-bob", "name": { "zh-TW": "bob" }, "active": true, "module": "move",
                "groupBy": ["move.item"], "function": "sum", "field": "move.qty",
                "where": { "all": [ { "field": "move.owner", "op": "eq", "value": "bob" } ] },
                "assert": { "op": "gte", "limit": { "const": 0 } }, "message": { "zh-TW": "不足" } } ]
            """;

        var (status, text) = await Import(AppTestPackagesV4.Flow(checks: checks, extraMoveFields: extra));

        status.Should().Be(HttpStatusCode.BadRequest, text);
        text.Should().Contain("套件不收");
    }

    [Fact]
    public async Task An_enum_constant_naming_a_missing_option_is_refused_at_import()
    {
        var extra = """, { "code": "kind", "type": "enum", "label": { "zh-TW": "kind" }, "options": [ { "code": "a", "label": { "zh-TW": "甲" }, "color": null } ] }""";
        var events = EventWith(""", { "to": "move.kind", "const": "opt_missing" }""");

        var (status, text) = await Import(AppTestPackagesV4.Flow(events: events, extraMoveFields: extra));

        status.Should().Be(HttpStatusCode.BadRequest, text);
        text.Should().Contain("missing");
    }

    [Fact]
    public async Task A_reverse_event_that_sorts_before_the_event_it_reverses_still_opens()
    {
        // 匯出按名稱排序：aa-cancel 排在 zz-post 前面，但它沖銷 zz-post 的 make
        var events = """
            [ { "code": "aa-cancel", "name": { "zh-TW": "取消" }, "active": true, "source": "doc",
                "trigger": { "on": "update" }, "when": null,
                "effects": [ { "code": "undo", "kind": "reverse", "target": "move", "ofEvent": "zz-post", "ofEffect": "make", "set": [] } ] },
              { "code": "zz-post", "name": { "zh-TW": "過帳" }, "active": true, "source": "doc",
                "trigger": { "on": "transition", "to": "done" }, "when": null,
                "effects": [ { "code": "make", "kind": "create", "target": "move", "each": "doc-line",
                  "set": [ { "to": "move.item", "from": { "side": "line", "field": "doc-line.item" } },
                           { "to": "move.qty", "formula": { "side": "line", "expr": "-{doc-line.qty}" } } ] } ] } ]
            """;
        var imported = await Import(AppTestPackagesV4.Flow(events: events));
        imported.Status.Should().Be(HttpStatusCode.Created, imported.Text);

        var opened = await _admin.PostAsJsonAsync($"{Apps}/packages/ledger-flow/open",
            new { groupMap = new Dictionary<string, object> { ["lead"] = new { mode = "create" } } });

        opened.StatusCode.Should().Be(HttpStatusCode.Created, await opened.Content.ReadAsStringAsync());
    }
}
