using System.Globalization;
using System.Text;

namespace DynamicModules.Domain.Approvals;

/// <summary>範本片段：純文字（Text），或佔位符（Placeholder：title、flow、initiator、node、f；f 時 FieldId 是欄位 Id）。</summary>
public sealed record TemplatePart(string? Text, string? Placeholder, long? FieldId);

/// <summary>通知範本語法（流程圖設計 §12.2）。</summary>
public static class ApprovalNotifyTemplate
{
    private static readonly string[] Names = ["title", "flow", "initiator", "node"];
    private const string FieldPrefix = "f:";

    /// <summary>
    /// {title}、{flow}、{initiator}、{node}、{f:數字} 是佔位符；其他「{不含大括號的文字}」是錯誤，Error 回該段含大括號的原文（第一個）；
    /// 沒成對的單獨大括號照字面。不提供跳脫語法。出錯時 Parts 仍盡量拆出，錯誤的那段當純文字。
    /// </summary>
    public static (IReadOnlyList<TemplatePart> Parts, string? Error) Parse(string text)
    {
        var parts = new List<TemplatePart>();
        var literal = new StringBuilder();
        string? error = null;
        void Flush()
        {
            if (literal.Length == 0) return;
            parts.Add(new TemplatePart(literal.ToString(), null, null));
            literal.Clear();
        }

        text ??= "";
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '{')
            {
                var j = i + 1;
                while (j < text.Length && text[j] != '{' && text[j] != '}') j++;
                if (j < text.Length && text[j] == '}')
                {
                    var token = text.Substring(i, j - i + 1);
                    var name = text.Substring(i + 1, j - i - 1);
                    if (Array.IndexOf(Names, name) >= 0)
                    {
                        Flush();
                        parts.Add(new TemplatePart(null, name, null));
                    }
                    else if (name.StartsWith(FieldPrefix, StringComparison.Ordinal) && TryField(name[FieldPrefix.Length..], out var id))
                    {
                        Flush();
                        parts.Add(new TemplatePart(null, "f", id));
                    }
                    else
                    {
                        error ??= token;
                        literal.Append(token);
                    }
                    i = j + 1;
                    continue;
                }
            }
            literal.Append(text[i]);
            i++;
        }
        Flush();
        return (parts, error);
    }

    /// <summary>範本引用的欄位 Id（去重，語法錯誤的片段略過）。</summary>
    public static IEnumerable<long> FieldIds(string text) =>
        Parse(text).Parts.Where(p => p.FieldId is not null).Select(p => p.FieldId!.Value).Distinct();

    private static bool TryField(string digits, out long id)
    {
        id = 0;
        return digits.Length > 0 && digits.All(char.IsAsciiDigit)
            && long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out id);
    }
}
