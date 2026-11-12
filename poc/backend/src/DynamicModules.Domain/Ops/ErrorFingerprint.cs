using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DynamicModules.Domain.Ops;

/// <summary>錯誤指紋與訊息正規化（純函式）：同一個缺陷在不同資料、不同行號下得到同一個指紋。</summary>
public static partial class ErrorFingerprint
{
    // 以下常數皆為技術限制（設計 §12）
    public const int Version = 1;
    public const int Length = 16;
    public const int MessageMax = 200;
    public const int FrameCount = 3;
    public const string ProductPrefix = "DynamicModules.";

    public static string NormalizeMessage(string? message)
    {
        if (string.IsNullOrEmpty(message)) return "";
        var s = QuotedRegex().Replace(message, "{s}");
        s = EmailRegex().Replace(s, "{email}");
        s = GuidRegex().Replace(s, "{guid}");
        s = HexRegex().Replace(s, "{hex}");
        s = NumberRegex().Replace(s, "{n}");
        return s.Length > MessageMax ? s[..MessageMax] : s;
    }

    public static string NormalizeFrame(string declaringTypeFullName, string methodName)
    {
        var type = ArityRegex().Replace(declaringTypeFullName, "");
        var method = ArityRegex().Replace(methodName, "");
        var plus = type.IndexOf("+<", StringComparison.Ordinal);
        if (plus >= 0)
        {
            var inner = type[(plus + 2)..];
            type = type[..plus];
            // 非同步狀態機：+<Handle>d__5 的 MoveNext 即 Handle
            if (method == "MoveNext")
            {
                var end = inner.IndexOf('>');
                if (end > 0) method = inner[..end];
            }
        }
        var m = GroupRegex().Match(method);
        if (m.Success) method = m.Groups[1].Value;
        return $"{type}.{method}";
    }

    public static IReadOnlyList<string> ClientFrames(string? stack)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(stack)) return result;
        foreach (var raw in stack.Split('\n'))
        {
            var line = raw.Trim();
            string? fn = null, url = null;
            var chrome = ChromeRegex().Match(line);
            if (chrome.Success)
            {
                fn = chrome.Groups[1].Success ? chrome.Groups[1].Value : null;
                url = chrome.Groups[2].Value;
            }
            else
            {
                var ff = FirefoxRegex().Match(line);
                if (ff.Success) { fn = ff.Groups[1].Value; url = ff.Groups[2].Value; }
            }
            if (url is null) continue;
            var file = url.Split('?')[0];
            file = file[(file.LastIndexOf('/') + 1)..];
            file = ViteHashRegex().Replace(file, "");
            result.Add($"{(string.IsNullOrEmpty(fn) ? "?" : fn)}@{file}");
            if (result.Count == FrameCount) break;
        }
        return result;
    }

    public static string ForServer(string exceptionType, string? message, IEnumerable<string> frames, string? routeTemplate) =>
        Hash(["server", exceptionType, NormalizeMessage(message),
            string.Join("|", frames.Where(f => f.StartsWith(ProductPrefix, StringComparison.Ordinal)).Take(FrameCount)),
            routeTemplate ?? ""]);

    public static string ForClient(string kind, string? name, string? message, IReadOnlyList<string> frames, string? routeTemplate) =>
        Hash(["client", kind, name ?? "", NormalizeMessage(message),
            string.Join("|", frames.Take(FrameCount)), routeTemplate ?? ""]);

    private static string Hash(string[] parts)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", parts)));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..Length];
    }

    [GeneratedRegex(@"'[^']*'|""[^""]*""")] private static partial Regex QuotedRegex();
    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")] private static partial Regex EmailRegex();
    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b")] private static partial Regex GuidRegex();
    [GeneratedRegex(@"\b(?=[0-9a-fA-F]*[a-fA-F])[0-9a-fA-F]{8,}\b")] private static partial Regex HexRegex();
    [GeneratedRegex(@"\d+")] private static partial Regex NumberRegex();
    [GeneratedRegex(@"`\d+")] private static partial Regex ArityRegex();
    [GeneratedRegex(@"^<([^>]+)>")] private static partial Regex GroupRegex();
    [GeneratedRegex(@"^at (?:(.+?) \()?(.+?):\d+:\d+\)?$")] private static partial Regex ChromeRegex();
    [GeneratedRegex(@"^([^@\s]*)@(.+?):\d+:\d+$")] private static partial Regex FirefoxRegex();
    [GeneratedRegex(@"-[A-Za-z0-9_]{8}(?=\.js$)")] private static partial Regex ViteHashRegex();
}
