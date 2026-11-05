using System.Globalization;

namespace DynamicModules.Domain.Rules;

/// <summary>規則目標：field:&lt;欄位 Id&gt;、block:&lt;區塊 Id&gt; 或 *（整筆記錄）。</summary>
public abstract record RuleTarget
{
    public static bool TryParse(string? s, out RuleTarget target)
    {
        target = null!;
        if (s == "*") { target = new RecordTarget(); return true; }
        if (s is null) return false;
        if (s.StartsWith("field:", StringComparison.Ordinal) && TryId(s[6..], out var fieldId))
        {
            target = new FieldTarget(fieldId);
            return true;
        }
        if (s.StartsWith("block:", StringComparison.Ordinal) && TryId(s[6..], out var blockId))
        {
            target = new BlockTarget(blockId);
            return true;
        }
        return false;
    }

    /// <summary>只收正整數的標準寫法（無正負號、前導零或空白），目標字串才能直接比對。</summary>
    private static bool TryId(string s, out long id) =>
        long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0
        && s == id.ToString(CultureInfo.InvariantCulture);
}

public sealed record FieldTarget(long FieldId) : RuleTarget;

public sealed record BlockTarget(long BlockId) : RuleTarget;

public sealed record RecordTarget : RuleTarget;
