using System.Text;
using DynamicModules.Application.Dynamic.ExportService;
using DynamicModules.Application.Dynamic.ImportService;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>第 7 階段決策 6：CSV 讀取與檔案層級限制。</summary>
public class CsvReaderTests
{
    private static byte[] Utf8(string s) => new UTF8Encoding(false).GetBytes(s);

    [Fact]
    public void Read_SkipsBom()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Utf8("a,b\r\n1,2\r\n")).ToArray();
        var r = CsvReader.Read(bytes);
        r.Error.Should().BeNull();
        r.Header.Should().Equal("a", "b");
        r.Rows.Should().ContainSingle().Which.Cells.Should().Equal("1", "2");
    }

    [Fact]
    public void Read_QuotedCommaQuoteNewline_RoundTripsWithCsvWriter()
    {
        var header = new[] { "名稱", "備註" };
        var rows = new[] { new[] { "a,b", "say \"hi\"\r\nnext" }, new[] { "x", "" } };
        var r = CsvReader.Read(CsvWriter.Write(header, rows));
        r.Error.Should().BeNull();
        r.Header.Should().Equal(header);
        r.Rows.Select(x => x.Cells.ToArray()).Should().BeEquivalentTo(rows, o => o.WithStrictOrdering());
    }

    [Fact]
    public void Read_Big5_ReturnsUtf8Error()
    {
        // Big5 的「中文」：A4 A4 A4 E5，不是有效的 UTF-8
        var r = CsvReader.Read([0xA4, 0xA4, 0xA4, 0xE5, 0x0D, 0x0A, 0x31]);
        r.Error.Should().Be("請把檔案存成 UTF-8 的 CSV");
    }

    [Fact]
    public void Read_NoHeader_ReturnsError() =>
        CsvReader.Read([]).Error.Should().Be("檔案沒有表頭");

    [Fact]
    public void Read_UnclosedQuote_ReturnsLineError() =>
        CsvReader.Read(Utf8("a,b\r\n1,2\r\n3,\"oops\r\n4,5\r\n")).Error.Should().Be("第 3 行格式不正確");

    [Fact]
    public void Read_SkipsBlankRows_CountsLimit()
    {
        var ok = "a\r\n" + string.Concat(Enumerable.Range(0, 1000).Select(i => $"{i}\r\n\r\n,\r\n"));
        var r = CsvReader.Read(Utf8(ok));
        r.Error.Should().BeNull();
        r.Rows.Should().HaveCount(1000);

        var over = CsvReader.Read(Utf8(ok + "x\r\n"));
        over.Error.Should().Be("一次最多匯入 1000 筆");
    }

    [Fact]
    public void Read_LineNumbersAccountForEmbeddedNewlines()
    {
        var r = CsvReader.Read(Utf8("a,b\n1,\"x\ny\nz\"\n\n2,3\n"));
        r.Rows.Select(x => x.Line).Should().Equal(2, 6);
    }
}
