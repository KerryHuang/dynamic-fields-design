using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace DynamicModules.Application.Dynamic.DemoService;

/// <summary>
/// Demo 用的虛構設計圖：程式畫出 640×400 的外殼示意圖（矩形、圓、線），不放二進位檔也不用影像套件。
/// 結果固定（同參數位元組相同）。圖裡沒有文字、沒有任何真實產品。
/// </summary>
public static class DemoDrawings
{
    public enum Kind { Front, MarkedUp }

    private const int Width = 640, Height = 400;

    /// <summary>
    /// 640×400 PNG：外殼輪廓、一個指示燈窗與散熱槽。version 1 的指示燈窗在左上；其餘版本移到右上並多一排散熱槽。
    /// MarkedUp 在同一版上加紅圈（模擬客戶標註）。
    /// </summary>
    public static byte[] Png(int version, Kind kind = Kind.Front)
    {
        var px = new byte[Width * Height * 3];
        Rect(px, 0, 0, Width, Height, (245, 247, 250));
        Rect(px, 60, 50, 520, 300, (120, 130, 145));          // 外殼外框
        Rect(px, 68, 58, 504, 284, (214, 220, 228));           // 外殼本體
        Rect(px, 100, 300, 440, 6, (150, 158, 170));           // 底部線條

        var windowX = version <= 1 ? 110 : 450;                // 指示燈窗位置隨版本改
        Rect(px, windowX, 90, 80, 50, (60, 70, 85));
        Rect(px, windowX + 6, 96, 68, 38, version <= 1 ? (90, 190, 120) : (80, 160, 230));
        Circle(px, 320, 200, 36, (60, 70, 85), filled: false); // 中央開孔
        Circle(px, 320, 200, 20, (60, 70, 85), filled: true);

        for (var i = 0; i < (version <= 1 ? 4 : 8); i++)       // 散熱槽
            Rect(px, 120 + i * 20, 250, 8, 40, (95, 105, 120));
        if (version >= 2) Line(px, 100, 230, 540, 230, (95, 105, 120));

        if (kind == Kind.MarkedUp)
        {
            for (var r = 0; r < 3; r++) Circle(px, windowX + 40, 115, 52 + r, (220, 40, 40), filled: false);
            Line(px, windowX + 40, 167, windowX + 40, 215, (220, 40, 40));
        }
        return Encode(px);
    }

    /// <summary>UTF-8 文字檔（修改說明）。</summary>
    public static byte[] Note(string text) => Encoding.UTF8.GetBytes(text);

    private static void Set(byte[] px, int x, int y, (int R, int G, int B) c)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) return;
        var i = (y * Width + x) * 3;
        px[i] = (byte)c.R;
        px[i + 1] = (byte)c.G;
        px[i + 2] = (byte)c.B;
    }

    private static void Rect(byte[] px, int x, int y, int w, int h, (int, int, int) c)
    {
        for (var yy = y; yy < y + h; yy++)
            for (var xx = x; xx < x + w; xx++) Set(px, xx, yy, c);
    }

    private static void Circle(byte[] px, int cx, int cy, int r, (int, int, int) c, bool filled)
    {
        for (var y = -r; y <= r; y++)
            for (var x = -r; x <= r; x++)
            {
                var d = x * x + y * y;
                if (filled ? d <= r * r : d <= r * r && d >= (r - 3) * (r - 3)) Set(px, cx + x, cy + y, c);
            }
    }

    private static void Line(byte[] px, int x0, int y0, int x1, int y1, (int, int, int) c)
    {
        var steps = Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0));
        for (var i = 0; i <= steps; i++)
        {
            var x = x0 + (x1 - x0) * i / Math.Max(steps, 1);
            var y = y0 + (y1 - y0) * i / Math.Max(steps, 1);
            Rect(px, x, y, 3, 3, c);
        }
    }

    private static byte[] Encode(byte[] rgb)
    {
        var raw = new byte[Height * (1 + Width * 3)];
        for (var y = 0; y < Height; y++)
            Buffer.BlockCopy(rgb, y * Width * 3, raw, y * (1 + Width * 3) + 1, Width * 3); // filter 0 已是預設的 0

        using var zipped = new MemoryStream();
        using (var z = new ZLibStream(zipped, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw);

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, Width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), Height);
        ihdr[8] = 8;   // 位元深度
        ihdr[9] = 2;   // RGB
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", zipped.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> n = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(n, (uint)data.Length);
        s.Write(n);
        var body = new byte[4 + data.Length];
        Encoding.ASCII.GetBytes(type, body);
        data.CopyTo(body, 4);
        s.Write(body);
        BinaryPrimitives.WriteUInt32BigEndian(n, Crc32(body));
        s.Write(n);
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(i =>
    {
        var c = (uint)i;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }
}
