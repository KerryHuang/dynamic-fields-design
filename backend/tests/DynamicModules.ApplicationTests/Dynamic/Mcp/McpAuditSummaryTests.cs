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
}
