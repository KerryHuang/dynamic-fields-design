using System.Globalization;

namespace DynamicModules.Domain.Reports;

/// <summary>報表欄位鍵（<see cref="ReportCalculator.KeyOf"/> 的格式 來源:欄位Id:經由欄位Id）轉回欄位引用；報表排序鍵與卡片排序鍵共用。</summary>
public static class ReportFieldKeys
{
    /// <summary>不是這個格式（如量值 Id）回 null；不對應任何欄位的字串排序時會被忽略。</summary>
    public static ReportFieldRef? TryParse(string key)
    {
        var parts = key.Split(':');
        if (parts.Length != 3 || !long.TryParse(parts[1], CultureInfo.InvariantCulture, out var id)) return null;
        if (parts[2].Length == 0) return new ReportFieldRef(parts[0], id, null);
        return long.TryParse(parts[2], CultureInfo.InvariantCulture, out var via) ? new ReportFieldRef(parts[0], id, via) : null;
    }
}
