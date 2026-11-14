using System.Text.RegularExpressions;

namespace DynamicModules.Domain.Approvals;

/// <summary>關卡、節點與連線代號的格式：小寫英文開頭，其後小寫英文、數字、連字號，至多 40 字。</summary>
public static partial class ApprovalCodes
{
    [GeneratedRegex("^[a-z][a-z0-9-]{0,39}$")]
    private static partial Regex Pattern();

    public static bool IsValid(string? code) => code is not null && Pattern().IsMatch(code);
}
