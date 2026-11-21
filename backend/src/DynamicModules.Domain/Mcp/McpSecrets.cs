using System.Security.Cryptography;
using System.Text;

namespace DynamicModules.Domain.Mcp;

/// <summary>
/// 權杖與確認碼的產生與雜湊（設計 §5.1、§6.3；寫死清單：安全）。明文只回給使用者一次，資料表只存 SHA-256 小寫十六進位；
/// 資料庫以雜湊等值查到列之後，應用層再以 <see cref="HashEquals"/>（<see cref="CryptographicOperations.FixedTimeEquals"/>）比對才算命中。
/// </summary>
public static class McpSecrets
{
    public const string TokenPrefix = "fwp_", ConfirmationPrefix = "fwc_";

    private const string Base62 = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    // 隨機部分的字數：43 個 base62 字元 ≥ 256 位元（32 bytes），22 個 ≥ 128 位元（16 bytes）
    private const int TokenChars = 43, ConfirmationChars = 22;

    /// <summary>權杖全長（前綴＋隨機部分）。</summary>
    public const int TokenLength = 4 + TokenChars;

    /// <summary><c>fwp_</c>＋43 個 base62 字元（逐字以 <see cref="RandomNumberGenerator"/> 均勻取樣，熵 ≥ 256 位元）。</summary>
    public static string NewToken() => TokenPrefix + RandomNumberGenerator.GetString(Base62, TokenChars);

    /// <summary><c>fwc_</c>＋22 個 base62 字元（熵 ≥ 128 位元）。</summary>
    public static string NewConfirmationCode() => ConfirmationPrefix + RandomNumberGenerator.GetString(Base62, ConfirmationChars);

    /// <summary>形狀檢查：前綴、全長、隨機部分只含 base62。不查資料庫。</summary>
    public static bool LooksLikeToken(string s) =>
        s.Length == TokenLength && s.StartsWith(TokenPrefix, StringComparison.Ordinal) && s.AsSpan(TokenPrefix.Length).IndexOfAnyExcept(Base62) < 0;

    /// <summary>SHA-256 小寫十六進位（64 字）。</summary>
    public static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();

    /// <summary>固定時間比較兩個雜湊字串；長度不同直接回 false。</summary>
    public static bool HashEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

    /// <summary>末四碼，給使用者辨識權杖。</summary>
    public static string Last4(string token) => token[^4..];
}
