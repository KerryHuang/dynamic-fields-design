using System.Text.Json;
using DynamicModules.Application.Dynamic.McpService;
using DynamicModules.Domain.Mcp;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic.Mcp;

/// <summary>工具呼叫稽核摘要的參數部分（mcp1a 計畫判斷 17）。</summary>
public class McpAuditSummaryTests
{
    private static string Of(string json) => McpAuditSummary.Args(JsonDocument.Parse(json).RootElement).ToJsonString();

    [Fact]
    public void Filter_ValueRemoved_FieldAndOpKept_KeywordLengthOnly()
    {
        var s = Of("""{"module":"orders","keyword":"王小明","filter":{"any":[{"field":"amount","op":"gt","value":99887},{"all":[{"field":"due","op":"lt","fieldRef":"start","value":"2026-01-01"}]}]},"confirmationCode":"fwc_abc"}""");

        s.Should().NotContain("99887").And.NotContain("2026-01-01").And.NotContain("王小明").And.NotContain("fwc_abc").And.NotContain("confirmationCode");
        var root = JsonDocument.Parse(s).RootElement;
        root.GetProperty("module").GetString().Should().Be("orders");
        root.GetProperty("keyword").GetProperty("length").GetInt32().Should().Be(3);
        var first = root.GetProperty("filter").GetProperty("any")[0];
        (first.GetProperty("field").GetString(), first.GetProperty("op").GetString(), first.GetProperty("value").GetString())
            .Should().Be(("amount", "gt", McpAuditSummary.Elided));
        root.GetProperty("filter").GetProperty("any")[1].GetProperty("all")[0].GetProperty("fieldRef").GetString().Should().Be("start");
    }

    [Fact]
    public void Oversized_TruncatedAndStillValidJson()
    {
        var s = Of($$"""{"module":"orders","fields":["{{string.Join("\",\"", Enumerable.Range(0, 500).Select(i => "field_" + i))}}"]}""");
        s.Length.Should().BeLessThanOrEqualTo(McpLimits.AuditArgsMax);
        var root = JsonDocument.Parse(s).RootElement;
        root.GetProperty("truncated").GetBoolean().Should().BeTrue();
        root.GetProperty("keys").EnumerateArray().Select(k => k.GetString()).Should().Equal("module", "fields");
    }

    [Fact]
    public void ClientName_CutTo100()
    {
        McpAuditSummary.Cut(new string('c', 150)).Should().HaveLength(McpAuditSummary.ClientMax);
        McpAuditSummary.Cut("short").Should().Be("short");
        McpAuditSummary.Cut(null).Should().BeNull();
    }

    // ───────────── strict 模式（寫入、高風險、不存在的工具；mcp2a 判斷 15） ─────────────

    private const string Secret = "機密字樣XYZ";

    private static string Strict(string json) => McpAuditSummary.Args(JsonDocument.Parse(json).RootElement, strict: true).ToJsonString();

    [Fact]
    public void Strict_Values_SortedKeysOnly()
    {
        var s = Strict($$$"""{"values":{"b":"{{{Secret}}}","a":12345678}}""");
        s.Should().NotContain(Secret).And.NotContain("12345678");
        JsonDocument.Parse(s).RootElement.GetProperty("values").EnumerateArray().Select(k => k.GetString()).Should().Equal("a", "b");
    }

    [Fact]
    public void Strict_Details_ModuleRowsKeysAndHasId()
    {
        var s = Strict($$$"""{"details":{"lines":[{"id":5,"rowVersion":"rv","values":{"qty":"{{{Secret}}}"}},{"values":{"sku":"{{{Secret}}}"},"_delete":true}]}}""");
        s.Should().NotContain(Secret).And.NotContain("rv");
        var rows = JsonDocument.Parse(s).RootElement.GetProperty("details").GetProperty("lines");
        rows.GetArrayLength().Should().Be(2);
        (rows[0].GetProperty("id").GetBoolean(), rows[1].GetProperty("id").GetBoolean()).Should().Be((true, false));
        rows[0].GetProperty("keys")[0].GetString().Should().Be("qty");
        rows[1].GetProperty("other")[0].GetString().Should().Be("_delete");
    }

    [Fact]
    public void Strict_FreeText_LengthOnly_Picks_Counts_Dates_SetOrClear()
    {
        var s = Strict($$$"""{"title":"{{{Secret}}}","description":"{{{Secret}}}","body":"{{{Secret}}}","comment":"{{{Secret}}}","picks":{"mgr":["u1","u2"]},"dueDate":"2031-02-03","startDate":null}""");
        s.Should().NotContain(Secret).And.NotContain("u1").And.NotContain("2031-02-03");
        var root = JsonDocument.Parse(s).RootElement;
        foreach (var k in new[] { "title", "description", "body", "comment" })
            root.GetProperty(k).GetProperty("length").GetInt32().Should().Be(Secret.Length, k);
        root.GetProperty("picks").GetProperty("mgr").GetInt32().Should().Be(2);
        (root.GetProperty("dueDate").GetString(), root.GetProperty("startDate").GetString()).Should().Be(("set", "clear"));
    }

    [Fact]
    public void Strict_KeptKeys_ShortScalarsOnly()
    {
        var s = Strict($$$"""{"idempotencyKey":"key-12345678","recordId":42,"rowVersion":"rv1","status":"done","to":7,"previewOnly":true,"cascade":"complete","moduleKey":"orders","assigneeUserId":"wenhui","hostKey":{"x":"{{{Secret}}}"},"taskId":"{{{new string('9', 101)}}}"}""");
        var root = JsonDocument.Parse(s).RootElement;
        root.GetProperty("idempotencyKey").GetString().Should().Be("key-12345678");
        root.GetProperty("recordId").GetInt64().Should().Be(42);
        (root.GetProperty("rowVersion").GetString(), root.GetProperty("status").GetString(), root.GetProperty("cascade").GetString())
            .Should().Be(("rv1", "done", "complete"));
        root.GetProperty("to").GetInt64().Should().Be(7);
        root.GetProperty("previewOnly").GetBoolean().Should().BeTrue();
        (root.GetProperty("moduleKey").GetString(), root.GetProperty("assigneeUserId").GetString()).Should().Be(("orders", "wenhui"));
        // 不是純量、或超過上限：只記型別與長度
        s.Should().NotContain(Secret).And.NotContain(new string('9', 101));
        root.GetProperty("hostKey").GetProperty("type").GetString().Should().Be("object");
        root.GetProperty("taskId").GetProperty("length").GetInt32().Should().Be(101);
    }

    [Fact]
    public void Write_UnknownArg_ValueNotStored()
    {
        var s = Strict($$$"""{"fields":{"a":"{{{Secret}}}"},"systemKeys":["{{{Secret}}}"]}""");
        s.Should().NotContain(Secret);
        var root = JsonDocument.Parse(s).RootElement;
        (root.GetProperty("fields").GetProperty("type").GetString(), root.GetProperty("fields").GetProperty("length").GetInt32()).Should().Be(("object", 1));
        (root.GetProperty("systemKeys").GetProperty("type").GetString(), root.GetProperty("systemKeys").GetProperty("length").GetInt32()).Should().Be(("array", 1));
    }

    [Fact]
    public void Values_NotObject_TypeOnly()
    {
        var s = Strict($$$"""{"values":"{{{Secret}}}","details":["{{{Secret}}}"]}""");
        s.Should().NotContain(Secret);
        var root = JsonDocument.Parse(s).RootElement;
        root.GetProperty("values").GetProperty("type").GetString().Should().Be("string");
        root.GetProperty("details").GetProperty("type").GetString().Should().Be("array");

        var nested = Strict($$$"""{"details":{"lines":"{{{Secret}}}","more":[12345,{"values":"{{{Secret}}}"}]}}""");
        nested.Should().NotContain(Secret).And.NotContain("12345");
        var d = JsonDocument.Parse(nested).RootElement.GetProperty("details");
        d.GetProperty("lines").GetProperty("type").GetString().Should().Be("string", "模組的值不是陣列只記型別");
        d.GetProperty("more")[0].GetProperty("type").GetString().Should().Be("number");
        d.GetProperty("more")[1].GetProperty("keys").GetProperty("type").GetString().Should().Be("string");
    }

    [Fact]
    public void NonStrict_UnchangedForReadTools()
    {
        JsonDocument.Parse(Of("""{"module":"orders","pageSize":20}""")).RootElement.GetProperty("pageSize").GetInt32().Should().Be(20);
    }
}
