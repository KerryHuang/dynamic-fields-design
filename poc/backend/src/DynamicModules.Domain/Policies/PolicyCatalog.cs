namespace DynamicModules.Domain.Policies;

/// <summary>
/// 業務政策的唯一登記處：出廠值（等於政策化之前寫死的值）與技術上下限只寫在這裡。
/// 程式一律透過讀值服務取生效值，不另寫常數。前端文案的代碼清單見 <c>poc/shared/policy-catalog.json</c>。
/// </summary>
public static class PolicyCatalog
{
    /// <summary>A1：每個模組的任務規則數。</summary>
    public static readonly PolicyDefinition TaskMaxRulesPerModule =
        new("task.maxRulesPerModule", "task", PolicyKind.Integer, PolicyScope.Module, "20", 1, 100, Unit: "count");

    /// <summary>A1：每條任務規則產生的任務數。</summary>
    public static readonly PolicyDefinition TaskMaxTasksPerRule =
        new("task.maxTasksPerRule", "task", PolicyKind.Integer, PolicyScope.Module, "20", 1, 50, Unit: "count");

    /// <summary>A2：一個應用程式套件內的任務規則總數（上限 500 是因為套件本身還受 1MB 限制）。</summary>
    public static readonly PolicyDefinition TaskMaxRulesPerPackage =
        new("task.maxRulesPerPackage", "task", PolicyKind.Integer, PolicyScope.System, "50", 1, 500, Unit: "count");

    /// <summary>A6：到期日正好是今天＋N 天時提醒一次（N=1 是「明天到期」）。</summary>
    public static readonly PolicyDefinition TaskReminderLeadDays =
        new("task.reminderLeadDays", "task", PolicyKind.Integer, PolicyScope.Module, "1", 1, 30, Unit: "days");

    /// <summary>A6：逾期未完成的任務是否每天提醒負責人。</summary>
    public static readonly PolicyDefinition TaskRemindOverdue =
        new("task.remindOverdue", "task", PolicyKind.Boolean, PolicyScope.Module, "true");

    private static readonly string[] NotifyRoles = ["assignee", "creator"];

    /// <summary>A13：新留言通知誰。</summary>
    public static readonly PolicyDefinition TaskNotifyCommented =
        new("task.notify.commented", "notification", PolicyKind.RoleSet, PolicyScope.Module, "assignee,creator", Options: NotifyRoles);

    /// <summary>A13：任務完成通知誰。</summary>
    public static readonly PolicyDefinition TaskNotifyCompleted =
        new("task.notify.completed", "notification", PolicyKind.RoleSet, PolicyScope.Module, "creator", Options: NotifyRoles);

    /// <summary>A13：任務取消通知誰。</summary>
    public static readonly PolicyDefinition TaskNotifyCancelled =
        new("task.notify.cancelled", "notification", PolicyKind.RoleSet, PolicyScope.Module, "creator", Options: NotifyRoles);

    /// <summary>A14：通知保留天數；超過的舊通知下次讀通知時清除。</summary>
    public static readonly PolicyDefinition NotificationRetentionDays =
        new("notification.retentionDays", "notification", PolicyKind.Integer, PolicyScope.System, "90", 1, 3650, Unit: "days");

    /// <summary>A7：一週從星期幾開始（影響本週篩選與報表、儀表板的本週、上週、近 N 週）。</summary>
    public static readonly PolicyDefinition CalendarWeekStart =
        new("calendar.weekStart", "locale", PolicyKind.Choice, PolicyScope.System, "monday",
            Options: ["monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday"]);

    public static DayOfWeek WeekStartOf(string choice) => Enum.Parse<DayOfWeek>(choice, ignoreCase: true);

    public static IReadOnlyList<PolicyDefinition> All { get; } =
        [TaskMaxRulesPerModule, TaskMaxTasksPerRule, TaskMaxRulesPerPackage,
         TaskReminderLeadDays, TaskRemindOverdue, TaskNotifyCommented, TaskNotifyCompleted, TaskNotifyCancelled,
         NotificationRetentionDays, CalendarWeekStart];


    public static PolicyDefinition? Find(string code) =>
        All.FirstOrDefault(d => string.Equals(d.Code, code, StringComparison.Ordinal));
}
