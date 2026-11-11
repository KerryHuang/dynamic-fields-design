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

    /// <summary>
    /// 附件副檔名的寫死黑名單（安全檢查，設計 §10）：網頁、腳本與可執行類。白名單從部署設定檔移到畫面可改之後，
    /// 這些項目不論誰設都不收；出廠白名單本來就不含它們。
    /// </summary>
    private static readonly string[] ForbiddenExtensions =
    [
        "html", "htm", "xhtml", "svg", "js", "mjs", "exe", "msi", "bat", "cmd", "com", "scr", "ps1", "sh",
        "jar", "vbs", "dll", "app", "dmg", "php", "asp", "aspx", "jsp"
    ];

    /// <summary>A20：附件單檔大小上限（位元組）。</summary>
    public static readonly PolicyDefinition AttachmentMaxFileBytes =
        new("attachment.maxFileBytes", "attachment", PolicyKind.Integer, PolicyScope.Module, "10485760", 1, 52428800, Unit: "bytes");

    /// <summary>A20：每個附件欄位的檔案數上限。</summary>
    public static readonly PolicyDefinition AttachmentMaxFilesPerField =
        new("attachment.maxFilesPerField", "attachment", PolicyKind.Integer, PolicyScope.Module, "20", 1, 100, Unit: "count");

    /// <summary>A20：允許上傳的副檔名白名單（沒列出的一律拒絕；<see cref="ForbiddenExtensions"/> 不論怎麼設都不收）。</summary>
    public static readonly PolicyDefinition AttachmentAllowedExtensions =
        new("attachment.allowedExtensions", "attachment", PolicyKind.CodeList, PolicyScope.System,
            "pdf,png,jpg,jpeg,webp,txt,csv,docx,xlsx,pptx", 1, 40, Forbidden: ForbiddenExtensions);

    /// <summary>A20：每則任務留言的附件數上限。</summary>
    public static readonly PolicyDefinition AttachmentMaxFilesPerComment =
        new("attachment.maxFilesPerComment", "attachment", PolicyKind.Integer, PolicyScope.Module, "5", 1, 10, Unit: "count");

    public static DayOfWeek WeekStartOf(string choice) => Enum.Parse<DayOfWeek>(choice, ignoreCase: true);

    public static IReadOnlyList<PolicyDefinition> All { get; } =
        [TaskMaxRulesPerModule, TaskMaxTasksPerRule, TaskMaxRulesPerPackage,
         TaskReminderLeadDays, TaskRemindOverdue, TaskNotifyCommented, TaskNotifyCompleted, TaskNotifyCancelled,
         NotificationRetentionDays, CalendarWeekStart,
         AttachmentMaxFileBytes, AttachmentMaxFilesPerField, AttachmentAllowedExtensions, AttachmentMaxFilesPerComment];

    public static PolicyDefinition? Find(string code) =>
        All.FirstOrDefault(d => string.Equals(d.Code, code, StringComparison.Ordinal));
}
