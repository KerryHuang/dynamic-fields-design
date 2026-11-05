using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace DynamicModules.Domain.AutoNumbers;

/// <summary>
/// 自動編號格式，例如 REP-{yyyyMM}-{0000}。變數：{yyyy}、{yy}、{MM}、{yyyyMM}，以及恰好一個流水號 {0…0}（1–10 位）。
/// 有月份每月重置、只有年每年重置、都沒有永不重置。
/// </summary>
public sealed class AutoNumberFormat
{
    public const int MaxLength = 50;
    private const int MaxDigits = 10;

    private enum Kind { Literal, Year4, Year2, Month, YearMonth, Serial }

    private readonly List<(Kind Kind, string Text)> _parts;

    private AutoNumberFormat(List<(Kind, string)> parts) => _parts = parts;

    public static bool TryParse(string? format, [NotNullWhen(true)] out AutoNumberFormat? parsed)
    {
        parsed = null;
        if (string.IsNullOrEmpty(format) || format.Length > MaxLength) return false;

        var parts = new List<(Kind, string)>();
        var literal = new StringBuilder();
        var serials = 0;
        for (var i = 0; i < format.Length; i++)
        {
            var c = format[i];
            if (c == '}') return false;
            if (c != '{') { literal.Append(c); continue; }

            var end = format.IndexOf('}', i + 1);
            if (end < 0) return false;
            var token = format[(i + 1)..end];
            Kind kind;
            if (token == "yyyy") kind = Kind.Year4;
            else if (token == "yy") kind = Kind.Year2;
            else if (token == "MM") kind = Kind.Month;
            else if (token == "yyyyMM") kind = Kind.YearMonth;
            else if (token.Length is >= 1 and <= MaxDigits && token.All(ch => ch == '0')) { kind = Kind.Serial; serials++; }
            else return false;

            if (literal.Length > 0) { parts.Add((Kind.Literal, literal.ToString())); literal.Clear(); }
            parts.Add((kind, token));
            i = end;
        }
        if (serials != 1) return false;
        if (literal.Length > 0) parts.Add((Kind.Literal, literal.ToString()));

        parsed = new AutoNumberFormat(parts);
        return true;
    }

    /// <summary>期別鍵：有月份 yyyyMM、只有年 yyyy、都沒有空字串。</summary>
    public string PeriodKey(DateTime now)
    {
        if (_parts.Any(p => p.Kind is Kind.Month or Kind.YearMonth)) return Format(now, "yyyyMM");
        if (_parts.Any(p => p.Kind is Kind.Year4 or Kind.Year2)) return Format(now, "yyyy");
        return "";
    }

    /// <summary>流水號超過位數照樣輸出完整數字，不截斷。</summary>
    public string Render(DateTime now, long number)
    {
        var sb = new StringBuilder();
        foreach (var (kind, text) in _parts)
            sb.Append(kind switch
            {
                Kind.Literal => text,
                Kind.Year4 => Format(now, "yyyy"),
                Kind.Year2 => Format(now, "yy"),
                Kind.Month => Format(now, "MM"),
                Kind.YearMonth => Format(now, "yyyyMM"),
                _ => number.ToString("D" + text.Length, CultureInfo.InvariantCulture)
            });
        return sb.ToString();
    }

    private static string Format(DateTime now, string pattern) => now.ToString(pattern, CultureInfo.InvariantCulture);
}
