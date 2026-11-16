using DynamicModules.Domain.Reports;

namespace DynamicModules.Domain.Tasks;

/// <summary>任務的日期邊界。清單的「本週到期」與看板的到期區間都呼叫這裡，邊界只算一套（看板設計 §5.1、T5）。</summary>
public static class TaskDates
{
    /// <summary>本週最後一天（本週起始日＋6 天；起始日見政策 calendar.weekStart）。</summary>
    public static DateOnly WeekEnd(DateOnly today, DayOfWeek weekStart) =>
        ReportDates.WeekStart(today, weekStart).AddDays(6);
}
