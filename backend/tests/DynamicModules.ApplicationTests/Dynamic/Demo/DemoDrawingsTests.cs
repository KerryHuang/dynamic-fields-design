using System.Buffers.Binary;
using System.IO.Compression;
using DynamicModules.Application.Dynamic.DemoService;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic.Demo;

/// <summary>Demo 設計圖產生器：合法的 PNG、固定結果、各版本不同、體積小。</summary>
public class DemoDrawingsTests
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static List<(string Type, byte[] Data)> Chunks(byte[] png)
    {
        var chunks = new List<(string, byte[])>();
        var pos = 8;
        while (pos < png.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos));
            var type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
            var data = png.AsSpan(pos + 8, length).ToArray();
            var crc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos + 8 + length));
            Crc32(png.AsSpan(pos + 4, 4 + length)).Should().Be(crc, $"{type} 的 CRC");
            chunks.Add((type, data));
            pos += 12 + length;
        }
        return chunks;
    }

    // 測試自備的位元演算 CRC32，不和產生器共用查表
    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }

    [Fact]
    public void Png_SignatureAndChunks()
    {
        var png = DemoDrawings.Png(1);

        png.AsSpan(0, 8).ToArray().Should().Equal(Signature);
        var chunks = Chunks(png);
        chunks[0].Type.Should().Be("IHDR");
        BinaryPrimitives.ReadUInt32BigEndian(chunks[0].Data).Should().Be(640);
        BinaryPrimitives.ReadUInt32BigEndian(chunks[0].Data.AsSpan(4)).Should().Be(400);
        chunks[^1].Type.Should().Be("IEND");
        var idat = chunks.Where(c => c.Type == "IDAT").SelectMany(c => c.Data).ToArray();
        using var z = new ZLibStream(new MemoryStream(idat), CompressionMode.Decompress);
        var raw = new MemoryStream();
        z.CopyTo(raw);
        raw.Length.Should().Be(400 * (1 + 640 * 3), "RGB 8-bit，每列一個 filter 位元組");
    }

    [Fact]
    public void Png_Deterministic() => DemoDrawings.Png(2, DemoDrawings.Kind.MarkedUp).Should().Equal(DemoDrawings.Png(2, DemoDrawings.Kind.MarkedUp));

    [Fact]
    public void Png_VersionsDiffer()
    {
        var v1 = DemoDrawings.Png(1);
        var v2 = DemoDrawings.Png(2);
        var marked = DemoDrawings.Png(1, DemoDrawings.Kind.MarkedUp);
        v1.Should().NotEqual(v2);
        v1.Should().NotEqual(marked);
        v2.Should().NotEqual(marked);
    }

    [Fact]
    public void Png_SmallEnough()
    {
        foreach (var png in new[] { DemoDrawings.Png(1), DemoDrawings.Png(2), DemoDrawings.Png(1, DemoDrawings.Kind.MarkedUp) })
            png.Length.Should().BeLessThan(50 * 1024);
    }

    [Fact]
    public void Note_IsUtf8Text() => System.Text.Encoding.UTF8.GetString(DemoDrawings.Note("指示燈窗改到右上角")).Should().Be("指示燈窗改到右上角");
}
