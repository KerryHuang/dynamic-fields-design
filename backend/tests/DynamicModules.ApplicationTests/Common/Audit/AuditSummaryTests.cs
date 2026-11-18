using System.Text.Json;
using DynamicModules.Application.Common.Audit;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Common.Audit;

/// <summary>稽核摘要的遮蔽與截斷（維運設計 §17.2.1、§17.8 A06 的遮蔽部分）。</summary>
public class AuditSummaryTests
{
    [Fact]
    public void Masks_SecretKeys_AndTokenPrefixedValues_Anywhere()
    {
        var json = new AuditSummary()
            .Diff(new { Name = "舊", Password = "p@ss", Token = (string?)null }, new { Name = "fwp_0123456789abcdef", Password = "new", Token = "t" })
            .Add("nested", new { Secret = 1, List = new[] { "ok", "fwc_abc" }, Note = "含 fwp_ 但不是開頭" })
            .ToJson()!;
        json.Should().NotContain("p@ss").And.NotContain("\"new\"").And.NotContain("fwp_0123").And.NotContain("fwc_abc");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.GetProperty("before").GetProperty("password").GetString().Should().Be("***");
        root.GetProperty("before").GetProperty("token").ValueKind.Should().Be(JsonValueKind.Null, "null 不必遮蔽");
        root.GetProperty("after").GetProperty("name").GetString().Should().Be("***");
        root.GetProperty("nested").GetProperty("secret").GetString().Should().Be("***");
        root.GetProperty("nested").GetProperty("list")[1].GetString().Should().Be("***");
        root.GetProperty("nested").GetProperty("note").GetString().Should().Be("含 fwp_ 但不是開頭");
    }

    [Fact]
    public void OverLimit_KeepsOnlyChangedKeys_AndMarksTruncated()
    {
        var big = new string('x', 9000);
        var json = new AuditSummary().Diff(new { Same = big, Changed = 1 }, new { Same = big, Changed = 2 }).ToJson()!;
        json.Length.Should().BeLessThanOrEqualTo(16_000);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("truncated").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("before").TryGetProperty("same", out _).Should().BeFalse();
        doc.RootElement.GetProperty("after").GetProperty("changed").GetInt32().Should().Be(2);
    }

    [Fact]
    public void StillOverLimit_DropsLargestItems()
    {
        var json = new AuditSummary().Add("huge", new string('y', 20_000)).Add("count", 3).ToJson()!;
        json.Length.Should().BeLessThanOrEqualTo(16_000);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("huge", out _).Should().BeFalse();
        doc.RootElement.GetProperty("count").GetInt32().Should().Be(3);
        doc.RootElement.GetProperty("truncated").GetBoolean().Should().BeTrue();
        new AuditSummary().ToJson().Should().BeNull();
    }
}
