using System.Text.RegularExpressions;

namespace DynamicModules.Application.Dynamic.OpsService.Incidents;

/// <summary>
/// 正規化訊息存進事件前的遮蔽（維運設計 §17.2.2、O2-T27）：<c>fwp_</c>／<c>fwc_</c> 開頭的值、<c>Bearer</c> 之後的字串、
/// <c>password=</c>／<c>token=</c>／<c>secret=</c>／<c>key=</c> 這類鍵值對的值、32 字元以上的 base64 片段一律換成 <c>***</c>。
/// 只是多一層保險，不保證乾淨，所以正規化訊息仍不外送。
/// </summary>
public static partial class SensitiveTextMask
{
    public const string Mask = "***";

    public static string? Apply(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var s = TokenRegex().Replace(text, Mask);
        s = BearerRegex().Replace(s, m => m.Groups[1].Value + Mask);
        s = KeyValueRegex().Replace(s, m => m.Groups[1].Value + Mask);
        return Base64Regex().Replace(s, Mask);
    }

    [GeneratedRegex(@"fw[pc]_\S*")] private static partial Regex TokenRegex();
    [GeneratedRegex(@"(Bearer\s+)\S+", RegexOptions.IgnoreCase)] private static partial Regex BearerRegex();
    [GeneratedRegex(@"((?:password|passwd|pwd|token|secret|key|apikey|api_key)\s*[=:]\s*)[^\s,;&]+", RegexOptions.IgnoreCase)] private static partial Regex KeyValueRegex();
    [GeneratedRegex(@"[A-Za-z0-9+/_-]{32,}={0,2}")] private static partial Regex Base64Regex();
}
