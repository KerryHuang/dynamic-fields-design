using System.Globalization;

namespace DynamicModules.Domain.Reports;

/// <summary>報表日期區間解析與分桶鍵；週一為一週開始，週別採 ISO 8601。</summary>
public static class ReportDates
{
    /// <summary>把區間設定依基準日解析成含頭含尾的日期範圍。last* 系列包含基準日所在的週／月。</summary>
    public static (DateOnly From, DateOnly ToInclusive) Resolve(ReportDateRange r, DateOnly basis)
    {
        var n = r.N ?? 1;
        return r.Kind switch
        {
            "today" => (basis, basis),
            "thisWeek" => Weeks(basis, 1),
            "lastWeek" => (WeekStart(basis).AddDays(-7), WeekStart(basis).AddDays(-1)),
            "thisMonth" => Months(basis, 1),
            "lastMonth" => Months(basis.AddMonths(-1), 1),
            "thisYear" => (new DateOnly(basis.Year, 1, 1), new DateOnly(basis.Year, 12, 31)),
            "lastYear" => (new DateOnly(basis.Year - 1, 1, 1), new DateOnly(basis.Year - 1, 12, 31)),
            "lastDays" => (basis.AddDays(1 - n), basis),
            "lastWeeks" => Weeks(basis, n),
            "lastMonths" => Months(basis, n),
            _ => throw new ArgumentException($"未知的日期區間：{r.Kind}", nameof(r)),
        };
    }

    /// <summary>分桶鍵：day 2026-11-05、week 2026-W45、month 2026-11、quarter 2026-Q4、year 2026。</summary>
    public static string BucketKey(DateOnly d, string bucket) => bucket switch
    {
        "day" => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        "week" => $"{ISOWeek.GetYear(d.ToDateTime(TimeOnly.MinValue))}-W{ISOWeek.GetWeekOfYear(d.ToDateTime(TimeOnly.MinValue)):00}",
        "month" => d.ToString("yyyy-MM", CultureInfo.InvariantCulture),
        "quarter" => $"{d.Year}-Q{(d.Month - 1) / 3 + 1}",
        "year" => d.Year.ToString(CultureInfo.InvariantCulture),
        _ => throw new ArgumentException($"未知的分桶：{bucket}", nameof(bucket)),
    };

    /// <summary>儲存格是否落在區間內。日期時間欄位的 Date 已是台北牆上時間的日期，故隔天 00:00 不屬於前一天。</summary>
    public static bool Contains((DateOnly From, DateOnly ToInclusive) range, ReportCell cell) =>
        cell is { State: "ok", Date: { } d } && d >= range.From && d <= range.ToInclusive;

    private static DateOnly WeekStart(DateOnly d) => d.AddDays(-(((int)d.DayOfWeek + 6) % 7));

    private static (DateOnly, DateOnly) Weeks(DateOnly basis, int count) =>
        (WeekStart(basis).AddDays(-7 * (count - 1)), WeekStart(basis).AddDays(6));

    private static (DateOnly, DateOnly) Months(DateOnly basis, int count)
    {
        var first = new DateOnly(basis.Year, basis.Month, 1);
        return (first.AddMonths(1 - count), first.AddMonths(1).AddDays(-1));
    }
}
