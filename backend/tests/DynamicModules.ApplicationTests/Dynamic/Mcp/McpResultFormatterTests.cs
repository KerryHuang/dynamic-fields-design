using System.Text.Json;
using DynamicModules.Application.Dynamic.McpService;
using DynamicModules.Application.Dynamic.McpService.Tools;
using DynamicModules.Domain.Mcp;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic.Mcp;

/// <summary>工具結果格式與大小判斷（mcp1a 計畫判斷 12、14）。純函式。</summary>
public class McpResultFormatterTests
{
    private const string TooLarge = "結果太大";

    [Fact]
    public void Success_MetaDataSections_AndTextMirrorsStructured()
    {
        var r = McpResultFormatter.Success("query_records", new { page = 1 }, new { rows = new[] { 1, 2 } });

        r.Structured.GetProperty("contentTrust").GetString().Should().Be(McpTexts.ContentTrust);
        r.Structured.GetProperty("meta").GetProperty("tool").GetString().Should().Be("query_records");
        r.Structured.GetProperty("meta").GetProperty("page").GetInt32().Should().Be(1);
        r.Structured.GetProperty("data").GetProperty("rows").GetArrayLength().Should().Be(2);
        var lines = r.Text.Split('\n', 2);
        lines[0].Should().Be(McpTexts.UntrustedNotice);
        JsonDocument.Parse(lines[1]).RootElement.GetRawText().Should().Be(r.Structured.GetRawText());
    }

    /// <summary>大小是文字內容加結構化內容合計：只看任一邊都放得下，合計超過就判為超過。</summary>
    [Fact]
    public void Size_CountsTextPlusStructured()
    {
        var probe = McpResultFormatter.Success("t", null, new { s = "" });
        var max = probe.Size + 100;
        var outcome = McpToolOutcome.Ok(new { s = new string('x', 60) });
        var r = McpResultFormatter.Success("t", null, outcome.Data);
        (r.Text.Length < max && r.Structured.GetRawText().Length < max).Should().BeTrue();

        McpResultFormatter.Fit("t", outcome, TooLarge, max).ErrorCode.Should().Be(McpErrorCodes.ResultTooLarge);
        McpResultFormatter.Fit("t", outcome, TooLarge, r.Size).IsError.Should().BeFalse("剛好等於上限放得下");
    }

    private static readonly long[] AllIds = Enumerable.Range(1, 37).Select(i => (long)i).ToArray();

    /// <summary>假分頁來源：每筆約 500 字。</summary>
    private static McpToolOutcome Page(int page, int pageSize)
    {
        var rows = AllIds.Skip((page - 1) * pageSize).Take(pageSize).Select(id => new { id, note = new string('n', 500) }).ToList();
        return McpToolOutcome.Ok(new { rows }, new { page, pageSize }) with
        {
            Resize = new McpResize(McpOversize.SuggestPageSize, rows.Count, n => (new { page, pageSize = n }, new { rows = rows.Take(n) })),
        };
    }

    [Fact]
    public void Paged_TooLarge_SuggestsPageSize_ThatCoversAllRowsWithoutGaps()
    {
        const int max = 4000;
        var r = McpResultFormatter.Fit("query_records", Page(1, 37), TooLarge, max);

        r.ErrorCode.Should().Be(McpErrorCodes.ResultTooLarge);
        var suggested = r.Structured.GetProperty("error").GetProperty("retry").GetProperty("pageSize").GetInt32();
        McpResultFormatter.Fit("query_records", Page(1, suggested + 1), TooLarge, max).IsError.Should().BeTrue("建議的是放得下的最多筆數");

        var seen = new List<long>();
        for (var page = 1; ; page++)
        {
            var ok = McpResultFormatter.Fit("query_records", Page(page, suggested), TooLarge, max);
            ok.IsError.Should().BeFalse();
            var rows = ok.Structured.GetProperty("data").GetProperty("rows");
            if (rows.GetArrayLength() == 0) break;
            seen.AddRange(rows.EnumerateArray().Select(x => x.GetProperty("id").GetInt64()));
        }
        seen.Should().Equal(AllIds);
    }

    [Fact]
    public void Paged_OneRowStillTooLarge_NoRetry()
    {
        var r = McpResultFormatter.Fit("query_records", Page(1, 5), TooLarge, 400);
        r.ErrorCode.Should().Be(McpErrorCodes.ResultTooLarge);
        r.Structured.GetProperty("error").TryGetProperty("retry", out _).Should().BeFalse();
    }

    [Fact]
    public void Truncate_CutsDetailToFit_AndMarksMeta()
    {
        var rows = Enumerable.Range(1, 30).Select(i => new { i, note = new string('n', 300) }).ToList();
        var outcome = McpToolOutcome.Ok(new { rows }) with
        {
            Resize = new McpResize(McpOversize.Truncate, rows.Count,
                n => (new { truncated = n < rows.Count, rowsReturned = n, rowsTotal = rows.Count }, new { rows = rows.Take(n) })),
        };

        var r = McpResultFormatter.Fit("run_report", outcome, TooLarge, 3000);

        r.IsError.Should().BeFalse();
        r.Size.Should().BeLessThanOrEqualTo(3000);
        var meta = r.Structured.GetProperty("meta");
        meta.GetProperty("truncated").GetBoolean().Should().BeTrue();
        meta.GetProperty("rowsReturned").GetInt32().Should().Be(r.Structured.GetProperty("data").GetProperty("rows").GetArrayLength()).And.BeGreaterThan(0);
        meta.GetProperty("rowsTotal").GetInt32().Should().Be(30);
    }

    [Fact]
    public void Single_TooLarge_WithHint()
    {
        var outcome = McpToolOutcome.Ok(new { text = new string('x', 5000) }) with { OversizeHint = "use fields" };
        var r = McpResultFormatter.Fit("get_record", outcome, TooLarge, 1000);
        r.ErrorCode.Should().Be(McpErrorCodes.ResultTooLarge);
        r.Structured.GetProperty("error").GetProperty("hint").GetString().Should().Be("use fields");
        r.Structured.TryGetProperty("data", out _).Should().BeFalse();
    }

    [Fact]
    public void DefaultLimit_Is40000()
    {
        McpLimits.MaxResultChars.Should().Be(40_000);
        McpResultFormatter.Fit("t", McpToolOutcome.Ok(new { s = new string('x', 20_000) }), TooLarge).ErrorCode.Should().Be(McpErrorCodes.ResultTooLarge);
    }
}
