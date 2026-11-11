using System.Globalization;
using System.Text.RegularExpressions;

namespace DynamicModules.Domain.Policies;

/// <summary>政策範圍：只限全系統，或全系統預設＋可模組覆寫。</summary>
public enum PolicyScope { System, Module }

/// <summary>政策值的型別；值一律以標準字串存放，由型別解析。</summary>
public enum PolicyKind { Integer, Boolean, Choice, RoleSet, CodeList }

/// <summary>
/// 一個業務政策的登記項。<see cref="Min"/>／<see cref="Max"/> 是寫死的技術上下限（Integer 是數值，CodeList 是項目數）。
/// 標準字串：Integer 不帶符號的十進位；Boolean <c>true</c>／<c>false</c>；Choice 選項原字；
/// RoleSet 依選項順序、逗號分隔、可為空字串；CodeList 小寫、去前導點、去重保留首次順序、逗號分隔。
/// </summary>
public sealed partial record PolicyDefinition(
    string Code,
    string Category,
    PolicyKind Kind,
    PolicyScope Scope,
    string Factory,
    int Min = 0,
    int Max = 0,
    IReadOnlyList<string>? Options = null,
    string? Unit = null)
{
    public string SettingKey => $"policy.{Code}";

    /// <summary>合法就回標準字串，否則 null。</summary>
    public string? Normalize(string? raw)
    {
        if (raw is null) return null;
        switch (Kind)
        {
            case PolicyKind.Integer:
                return int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= Min && n <= Max
                    ? n.ToString(CultureInfo.InvariantCulture)
                    : null;
            case PolicyKind.Boolean:
                return bool.TryParse(raw, out var b) && raw.Trim() == raw ? (b ? "true" : "false") : null;
            case PolicyKind.Choice:
                return Options is not null && Options.Contains(raw, StringComparer.Ordinal) ? raw : null;
            case PolicyKind.RoleSet:
            {
                var options = Options ?? [];
                var items = Split(raw);
                if (items.Any(i => !options.Contains(i, StringComparer.Ordinal))) return null;
                return string.Join(",", options.Where(o => items.Contains(o, StringComparer.Ordinal)));
            }
            case PolicyKind.CodeList:
            {
                var items = Split(raw).Select(CodeItem).ToList();
                if (items.Any(i => !ValidCode(i))) return null;
                var distinct = items.Distinct(StringComparer.Ordinal).ToList();
                return distinct.Count >= Min && distinct.Count <= Max ? string.Join(",", distinct) : null;
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// 給部署設定檔用：Integer 能解析就夾到上下限；CodeList 丟掉不合格項目、截到上限項數，不足下限回 null；其他型別等於 <see cref="Normalize"/>。
    /// </summary>
    public string? Clamp(string? raw)
    {
        if (raw is null) return null;
        switch (Kind)
        {
            case PolicyKind.Integer:
                return long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)
                    ? Math.Clamp(n, Min, Max).ToString(CultureInfo.InvariantCulture)
                    : null;
            case PolicyKind.CodeList:
            {
                var items = Split(raw).Select(CodeItem).Where(i => ValidCode(i))
                    .Distinct(StringComparer.Ordinal).Take(Max).ToList();
                return items.Count >= Min ? string.Join(",", items) : null;
            }
            default:
                return Normalize(raw);
        }
    }

    private static List<string> Split(string raw) =>
        raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();

    private static string CodeItem(string item) => item.TrimStart('.').ToLowerInvariant();

    /// <summary>CodeList 單一項目的長度上限（技術限制）；訊息與檢查共用。</summary>
    public const int CodeItemMaxLength = 10;

    [GeneratedRegex("^[a-z0-9]+$")]
    private static partial Regex CodePattern();

    private static bool ValidCode(string item) => item.Length <= CodeItemMaxLength && CodePattern().IsMatch(item);
}
