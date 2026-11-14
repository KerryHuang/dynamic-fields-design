using System.Linq.Expressions;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Tasks;

/// <summary>拖到到期區間欄時不能放的原因。</summary>
public enum DueTargetError { None, UnknownColumn, OverdueNotAllowed, ThisWeekEmpty, LaterNeedsDate, LaterNotAfterWeekEnd }

public sealed record DueTarget(DueTargetError Error, DateOnly? Date)
{
    public bool Ok => Error == DueTargetError.None;
}

/// <summary>
/// 到期區間（看板設計 §5.1、§5.3）：逾期＝早於今天；今天；本週＝今天之後到本週最後一天；之後＝本週最後一天以後；無到期日。
/// 今天就是本週最後一天時「本週」是空的、不能放。邊界定義屬可寫死清單。
/// </summary>
public static class TaskDueBuckets
{
    public const string Overdue = "overdue", Today = "today", ThisWeek = "thisWeek", Later = "later", None = "none";

    public static readonly IReadOnlyList<string> Keys = [Overdue, Today, ThisWeek, Later, None];

    public static string Of(DateOnly? due, DateOnly today, DateOnly weekEnd) =>
        due is not { } d ? None
        : d < today ? Overdue
        : d == today ? Today
        : d <= weekEnd ? ThisWeek
        : Later;

    /// <summary>「之後」欄的預設日期：下週最後一天。</summary>
    public static DateOnly LaterDefault(DateOnly weekEnd) => weekEnd.AddDays(7);

    /// <summary>這一欄整欄不能放的原因（逾期、今天已是本週最後一天的本週）；可以放時是 None。</summary>
    public static DueTargetError DropBlock(string column, DateOnly today, DateOnly weekEnd) => column switch
    {
        Overdue => DueTargetError.OverdueNotAllowed,
        ThisWeek when today >= weekEnd => DueTargetError.ThisWeekEmpty,
        _ when Keys.Contains(column) => DueTargetError.None,
        _ => DueTargetError.UnknownColumn,
    };

    /// <summary>拖進某欄後的新到期日；「之後」要帶 requested 且晚於本週最後一天，「無到期日」是 null。</summary>
    public static DueTarget TargetDate(string column, DateOnly today, DateOnly weekEnd, DateOnly? requested)
    {
        if (DropBlock(column, today, weekEnd) is not DueTargetError.None and var block) return new DueTarget(block, null);
        return column switch
        {
            Today => new DueTarget(DueTargetError.None, today),
            ThisWeek => new DueTarget(DueTargetError.None, weekEnd),
            Later when requested is null => new DueTarget(DueTargetError.LaterNeedsDate, null),
            Later when requested <= weekEnd => new DueTarget(DueTargetError.LaterNotAfterWeekEnd, null),
            Later => new DueTarget(DueTargetError.None, requested),
            _ => new DueTarget(DueTargetError.None, null),
        };
    }

    /// <summary>某欄的資料庫條件，與 <see cref="Of"/> 同一套邊界。</summary>
    public static Expression<Func<TaskItem, bool>> Condition(string column, DateOnly today, DateOnly weekEnd) => column switch
    {
        Overdue => t => t.DueDate != null && t.DueDate < today,
        Today => t => t.DueDate == today,
        ThisWeek => t => t.DueDate != null && t.DueDate > today && t.DueDate <= weekEnd,
        Later => t => t.DueDate != null && t.DueDate > weekEnd,
        None => t => t.DueDate == null,
        _ => throw new ArgumentException($"未知的到期區間：{column}", nameof(column)),
    };
}
