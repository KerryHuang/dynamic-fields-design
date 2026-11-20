using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using DynamicModules.Application.Dynamic.OpsService.Alerts;
using DynamicModules.Application.Dynamic.ReportService.Data;

namespace DynamicModules.Application.Common.Localization;

/// <summary>
/// 後端訊息的內建層（語系包設計 §5.1、§9；i4c 計畫判斷 3）：把 C# 四語表合成一份唯讀字典，不是副本（來源仍是各張表）。
/// 鍵就是語系包 <c>server</c> 段的鍵：<see cref="MessageText"/> 不加前綴、<see cref="ReportText"/> 是 <c>report.</c>、<see cref="AlertMailText"/> 是 <c>alert.</c>（代碼名稱 <c>alert.label.</c>、健康檢查名稱 <c>alert.check.</c>）。重複鍵在建表時丟例外。
/// </summary>
public static class BuiltinServerText
{
    public static FrozenDictionary<string, (string Zh, string En, string Th, string Vi)> Table { get; } = Build();

    /// <summary>全部鍵（Ordinal 排序）：i4d1 語系包的基準鍵集（設計 §5.2）。</summary>
    public static IReadOnlyList<string> BaseKeys { get; } = Table.Keys.Order(StringComparer.Ordinal).ToArray();

    private static FrozenDictionary<string, (string Zh, string En, string Th, string Vi)> Build()
    {
        var all = new Dictionary<string, (string Zh, string En, string Th, string Vi)>(StringComparer.Ordinal);
        foreach (var (key, texts) in MessageText.Entries.Concat(ReportText.Entries).Concat(AlertMailText.Entries)) all.Add(key, texts);
        return all.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>內建四語之一的範本（未帶參數）；其他語系或沒有這個鍵回 false。</summary>
    public static bool TryGet(string locale, string key, [NotNullWhen(true)] out string? template)
    {
        template = Table.TryGetValue(key, out var e)
            ? locale switch { "zh-TW" => e.Zh, "en-US" => e.En, "th" => e.Th, "vi" => e.Vi, _ => null }
            : null;
        return template is not null;
    }
}
