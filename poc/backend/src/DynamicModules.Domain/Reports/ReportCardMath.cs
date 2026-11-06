namespace DynamicModules.Domain.Reports;

/// <summary>儀表板卡片選項的計算（spec §4）：純函式，不碰資料庫。</summary>
public static class ReportCardMath
{
    /// <summary>
    /// 往前推的 count 個同長度期間，由近到遠。一期的長度跟著區間走：本月比前 N 個月，近 30 天比前 N 個 30 天，
    /// 近 3 月比前 N 個 3 個月；週、日、年同理。
    /// </summary>
    public static IReadOnlyList<(DateOnly From, DateOnly ToInclusive)> PreviousWindows(ReportDateRange range, DateOnly basis, int count)
    {
        var (from, to) = ReportDates.Resolve(range, basis);
        var months = range.Kind switch { "thisMonth" or "lastMonth" => 1, "lastMonths" => range.N ?? 1, _ => 0 };
        var years = range.Kind is "thisYear" or "lastYear" ? 1 : 0;
        var days = to.DayNumber - from.DayNumber + 1;

        var windows = new List<(DateOnly, DateOnly)>(count);
        for (var k = 1; k <= count; k++)
        {
            if (months > 0)
                windows.Add((from.AddMonths(-months * k), from.AddMonths(-months * (k - 1)).AddDays(-1)));
            else if (years > 0)
                windows.Add((from.AddYears(-k), from.AddYears(-(k - 1)).AddDays(-1)));
            else
                windows.Add((from.AddDays(-days * k), from.AddDays(-days * (k - 1) - 1)));
        }
        return windows;
    }

    /// <summary>目前值與前幾期平均的比較。沒有前期值時都是 null；平均為 0 時算不出百分比。</summary>
    public static (decimal? PreviousAverage, decimal? DeltaPercent) Compare(decimal current, IReadOnlyList<decimal> previous)
    {
        if (previous.Count == 0) return (null, null);
        var average = previous.Sum() / previous.Count;
        return (average, average == 0 ? null : (current - average) / average * 100);
    }

    /// <summary>
    /// 累計占比（百分比）。values 是依數值由大到小排好、已套用前 N 名的各組值，分母是全部分組的總和（不是前 N 名的），
    /// 所以只顯示前幾名時累計也不會被灌到 100%。空值不累加；分母不大於 0 時都是 null。
    /// </summary>
    public static IReadOnlyList<decimal?> CumulativePercent(IReadOnlyList<decimal?> values, decimal allGroupsTotal)
    {
        decimal running = 0;
        return values.Select(v =>
        {
            running += v ?? 0;
            return allGroupsTotal > 0 ? running / allGroupsTotal * 100 : (decimal?)null;
        }).ToList();
    }
}
