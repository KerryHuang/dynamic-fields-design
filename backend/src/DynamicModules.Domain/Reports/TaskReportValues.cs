using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Reports;

/// <summary>
/// 任務報表的衍生值（看板設計 §7.3）：只讀任務既有欄位。日期取營運時區的日曆天（CreatedTime、CompletedTime 是台北牆上時間）。
/// 取消的任務不算逾期、延遲天數空白（使用者裁決 B1）；看板的「逾期」也呼叫 <see cref="IsOverdue"/>，兩邊只有一份定義。
/// </summary>
public static class TaskReportValues
{
    public static long SourceOf(TaskItem t) =>
        t.RuleId is not null ? TaskReportFields.SourceRule
        : t.RecordId is not null ? TaskReportFields.SourceRecord
        : TaskReportFields.SourcePersonal;

    public static bool IsOverdue(TaskItem t, DateOnly basis) => t.IsOpen && t.DueDate is { } due && due < basis;

    /// <summary>完成狀況；完成但沒有完成時間（舊資料）回 null。</summary>
    public static long? CompletionOf(TaskItem t)
    {
        if (t.Status != TaskItemStatus.Done) return TaskReportFields.CompletionNotDone;
        if (CompletedDate(t) is not { } done) return null;
        if (t.DueDate is not { } due) return TaskReportFields.CompletionNoDue;
        return done <= due ? TaskReportFields.CompletionOnTime : TaskReportFields.CompletionLate;
    }

    /// <summary>完成日減建立日（日曆天），當天完成是 0；未完成或沒有完成時間為 null。</summary>
    public static int? CompletionDays(TaskItem t) =>
        CompletedDate(t) is { } done ? done.DayNumber - DateOnly.FromDateTime(t.CreatedTime).DayNumber : null;

    /// <summary>完成的是完成日減到期日、待辦與進行中是基準日減到期日，不小於 0；沒有到期日、取消、完成但沒有完成時間為 null。</summary>
    public static int? DelayDays(TaskItem t, DateOnly basis)
    {
        if (t.DueDate is not { } due) return null;
        DateOnly? end = t.IsOpen ? basis : CompletedDate(t);
        return end is { } e ? Math.Max(0, e.DayNumber - due.DayNumber) : null;
    }

    private static DateOnly? CompletedDate(TaskItem t) =>
        t.Status == TaskItemStatus.Done && t.CompletedTime is { } at ? DateOnly.FromDateTime(at) : null;
}
