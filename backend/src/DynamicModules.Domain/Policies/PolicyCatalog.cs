namespace DynamicModules.Domain.Policies;

/// <summary>
/// 業務政策的唯一登記處：出廠值（等於政策化之前寫死的值）與技術上下限只寫在這裡。
/// 程式一律透過讀值服務取生效值，不另寫常數。前端文案的代碼清單見 <c>shared/policy-catalog.json</c>。
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

    /// <summary>A6：到期日在今天到今天＋N 天之間（含到期當天）時補一則「即將到期」，每個任務只補一次；改到期日或換負責人後重新計（維運設計 §10.2.9）。</summary>
    public static readonly PolicyDefinition TaskReminderLeadDays =
        new("task.reminderLeadDays", "task", PolicyKind.Integer, PolicyScope.Module, "1", 1, 30, Unit: "days");

    /// <summary>A6：逾期未完成的任務是否提醒負責人：每天最多一則，上一則還沒讀就不再新增。</summary>
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

    /// <summary>A40：子任務全部收尾時通知父任務的誰（看板設計 §10.7.2，ST-Q6 出廠只通知負責人）。</summary>
    public static readonly PolicyDefinition TaskNotifySubtasksDone =
        new("task.notify.subtasksDone", "notification", PolicyKind.RoleSet, PolicyScope.Module, "assignee", Options: NotifyRoles);

    /// <summary>A33：部門主管可看與重派部屬的任務（看板設計 §4）。</summary>
    public static readonly PolicyDefinition TaskSupervisorScope =
        new("task.supervisorScope", "task", PolicyKind.Boolean, PolicyScope.System, "true");

    /// <summary>A34：主管範圍是否含下層部門（看板設計 §4）。</summary>
    public static readonly PolicyDefinition TaskSupervisorScopeRecursive =
        new("task.supervisorScopeRecursive", "task", PolicyKind.Boolean, PolicyScope.System, "true");

    /// <summary>A35：每個父任務的子任務數（含已完成、已取消；看板設計 §10.1）；技術上限 200 是父任務詳細內嵌子任務清單的筆數。</summary>
    public static readonly PolicyDefinition TaskMaxSubtasksPerTask =
        new("task.maxSubtasksPerTask", "task", PolicyKind.Integer, PolicyScope.System, "50", 1, 200, Unit: "count");

    /// <summary>A14：通知保留天數；超過的舊通知由背景排程或下次讀通知時清除。</summary>
    public static readonly PolicyDefinition NotificationRetentionDays =
        new("notification.retentionDays", "notification", PolicyKind.Integer, PolicyScope.System, "90", 1, 3650, Unit: "days");

    /// <summary>O1：背景排程替所有人補到期提醒、清理舊通知的間隔（分鐘；維運設計 §10.6）。改值從下一次排程開始生效。</summary>
    public static readonly PolicyDefinition NotificationReminderScanMinutes =
        new("notification.reminderScanMinutes", "notification", PolicyKind.Integer, PolicyScope.System, "60", 5, 1440, Unit: "minutes");

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

    /// <summary>O1：請求耗時超過這個值，結束時記一則 Warning（設計 ops-observability §10.6）。</summary>
    public static readonly PolicyDefinition OpsSlowRequestMs =
        new("ops.slowRequestMs", "ops", PolicyKind.Integer, PolicyScope.System, "2000", 200, 60000, Unit: "ms");

    /// <summary>O1：是否收集前端回報的程式錯誤（只進 log；錯誤原文可能帶到畫面上的少量文字，log 視同敏感資料）。</summary>
    public static readonly PolicyDefinition OpsClientErrorCollection =
        new("ops.clientErrorCollection", "ops", PolicyKind.Boolean, PolicyScope.System, "true");

    /// <summary>O2：維運的自動結案、重複提醒、各表依保留天數清理的間隔（分鐘；維運設計 §17.6.1）。改值從下一次排程開始生效。</summary>
    public static readonly PolicyDefinition OpsUpkeepMinutes =
        new("ops.upkeepMinutes", "ops", PolicyKind.Integer, PolicyScope.System, "60", 5, 1440, Unit: "minutes");

    /// <summary>O2：稽核紀錄保留天數；超過的由 <c>ops.upkeep</c> 分批清除（維運設計 §17.6.1）。</summary>
    public static readonly PolicyDefinition OpsAuditRetentionDays =
        new("ops.auditRetentionDays", "ops", PolicyKind.Integer, PolicyScope.System, "365", 90, 3650, Unit: "days");

    /// <summary>O2：送告警信、跑排程型健康檢查的 <c>ops.monitor</c> 間隔（分鐘；維運設計 §17.6.1）。</summary>
    public static readonly PolicyDefinition OpsMonitorScanMinutes =
        new("ops.monitorScanMinutes", "ops", PolicyKind.Integer, PolicyScope.System, "1", 1, 60, Unit: "minutes");

    /// <summary>O2：健康檢查與告警寄送紀錄保留天數。</summary>
    public static readonly PolicyDefinition OpsHealthResultRetentionDays =
        new("ops.healthResultRetentionDays", "ops", PolicyKind.Integer, PolicyScope.System, "30", 7, 365, Unit: "days");

    /// <summary>O2：已結案維運事件保留天數（從結案時間起算）。</summary>
    public static readonly PolicyDefinition OpsIncidentRetentionDays =
        new("ops.incidentRetentionDays", "ops", PolicyKind.Integer, PolicyScope.System, "180", 30, 1095, Unit: "days");

    /// <summary>O2：事件原始樣本（伺服器例外與前端錯誤原文）保留天數。</summary>
    public static readonly PolicyDefinition OpsIncidentSampleRetentionDays =
        new("ops.incidentSampleRetentionDays", "ops", PolicyKind.Integer, PolicyScope.System, "14", 1, 90, Unit: "days");

    /// <summary>O2：嚴重事件沒人確認時的重複提醒間隔（時）。</summary>
    public static readonly PolicyDefinition OpsIncidentRenotifyHours =
        new("ops.incidentRenotifyHours", "ops", PolicyKind.Integer, PolicyScope.System, "24", 1, 168, Unit: "hours");

    /// <summary>O2：產品缺陷、客戶設定錯誤碼、安全事件多久沒再出現就自動結案（時）。</summary>
    public static readonly PolicyDefinition OpsIncidentAutoResolveHours =
        new("ops.incidentAutoResolveHours", "ops", PolicyKind.Integer, PolicyScope.System, "72", 1, 720, Unit: "hours");

    /// <summary>O2：基礎設施、產品缺陷、安全事件的站內通知對象（解析為空時改送全部系統管理員）。</summary>
    public static readonly PolicyDefinition OpsAlertRecipients =
        new("ops.alertRecipients", "ops", PolicyKind.Recipients, PolicyScope.System, "admins", 1, 20);

    /// <summary>O2：<c>security.loginFailures</c> 往回數幾分鐘的登入失敗。</summary>
    public static readonly PolicyDefinition OpsLoginFailureWindowMinutes =
        new("ops.loginFailureWindowMinutes", "ops", PolicyKind.Integer, PolicyScope.System, "15", 5, 1440, Unit: "minutes");

    /// <summary>O2：排程工作的下次預定時間落後幾個間隔，<c>scheduler.jobs</c> 直接判嚴重。</summary>
    public static readonly PolicyDefinition OpsSchedulerLagIntervals =
        new("ops.schedulerLagIntervals", "ops", PolicyKind.Integer, PolicyScope.System, "3", 2, 100, Unit: "count");

    /// <summary>A3：一個簽核流程的關卡數。</summary>
    public static readonly PolicyDefinition ApprovalMaxStages =
        new("approval.maxStages", "approval", PolicyKind.Integer, PolicyScope.Module, "10", 1, 20, Unit: "count");

    /// <summary>A37：流程圖中每個分支節點的分支數（流程圖設計 §15）；技術範圍 2 到 10。</summary>
    public static readonly PolicyDefinition ApprovalMaxBranches =
        new("approval.maxBranches", "approval", PolicyKind.Integer, PolicyScope.Module, "5", 2, 10, Unit: "count");

    /// <summary>A3：一關的簽核人數（固定名單執行期只受技術上限 <c>Max</c> 管）。</summary>
    public static readonly PolicyDefinition ApprovalMaxApprovers =
        new("approval.maxApprovers", "approval", PolicyKind.Integer, PolicyScope.Module, "20", 1, 50, Unit: "count");

    /// <summary>A3：一關的逾期時數；8760 小時是一年。</summary>
    public static readonly PolicyDefinition ApprovalMaxDueHours =
        new("approval.maxDueHours", "approval", PolicyKind.Integer, PolicyScope.Module, "8760", 1, 8760, Unit: "hours");

    /// <summary>A3：簽核意見字數；上限 1000 是資料庫欄位長度。</summary>
    public static readonly PolicyDefinition ApprovalMaxCommentLength =
        new("approval.maxCommentLength", "approval", PolicyKind.Integer, PolicyScope.System, "1000", 1, 1000, Unit: "count");

    /// <summary>A3＋A4：主管來源的層數；執行期的主管鏈解析只用技術上限 <c>Max</c>。</summary>
    public static readonly PolicyDefinition ApprovalMaxManagerLevel =
        new("approval.maxManagerLevel", "approval", PolicyKind.Integer, PolicyScope.Module, "5", 1, 10, Unit: "count");

    /// <summary>A12：發起人在關卡名單內時是否保留、要自己簽。</summary>
    public static readonly PolicyDefinition ApprovalAllowSelfApproval =
        new("approval.allowSelfApproval", "approval", PolicyKind.Boolean, PolicyScope.Module, "false");

    /// <summary>A32：同意並加簽時一次最多指定幾位加簽對象（設計 §7.6 的「一到五位」）；技術上限 20。</summary>
    public static readonly PolicyDefinition ApprovalMaxAddSignUsers =
        new("approval.maxAddSignUsers", "approval", PolicyKind.Integer, PolicyScope.Module, "5", 1, 20, Unit: "count");

    /// <summary>A32：一張單最多加簽幾個關卡（設計 §7.6 的「至多 5 個」，退回後重來的不重複計）；技術上限 20。</summary>
    public static readonly PolicyDefinition ApprovalMaxAddSignStages =
        new("approval.maxAddSignStages", "approval", PolicyKind.Integer, PolicyScope.Module, "5", 1, 20, Unit: "count");

    /// <summary>A36：逾期待簽的提醒間隔（設計 §12 的「超過 24 小時」）；負責人讀通知時，上次提醒已超過這個時數才補一則。</summary>
    public static readonly PolicyDefinition ApprovalOverdueReminderHours =
        new("approval.overdueReminderHours", "approval", PolicyKind.Integer, PolicyScope.Module, "24", 1, 720, Unit: "hours");

    /// <summary>A15：系統管理員是否略過狀態轉移上的群組限制（只管轉移群組，不影響其他權限）。</summary>
    public static readonly PolicyDefinition ApprovalAdminBypassTransitionGroups =
        new("approval.adminBypassTransitionGroups", "approval", PolicyKind.Boolean, PolicyScope.System, "true");

    /// <summary>A10：找不到簽核人時的備援對象（流程自己沒設備援名單時才用）。</summary>
    public static readonly PolicyDefinition ApprovalDefaultFallbackApprovers =
        new("approval.defaultFallbackApprovers", "approval", PolicyKind.Recipients, PolicyScope.Module, "admins", 1, 20);

    /// <summary>A11：簽核異常通知的對象。</summary>
    public static readonly PolicyDefinition ApprovalAlertRecipients =
        new("approval.alertRecipients", "approval", PolicyKind.Recipients, PolicyScope.System, "admins", 1, 20);

    public static DayOfWeek WeekStartOf(string choice) => Enum.Parse<DayOfWeek>(choice, ignoreCase: true);

    public static IReadOnlyList<PolicyDefinition> All { get; } =
        [TaskMaxRulesPerModule, TaskMaxTasksPerRule, TaskMaxRulesPerPackage,
         TaskReminderLeadDays, TaskRemindOverdue, TaskNotifyCommented, TaskNotifyCompleted, TaskNotifyCancelled, TaskNotifySubtasksDone,
         TaskSupervisorScope, TaskSupervisorScopeRecursive, TaskMaxSubtasksPerTask,
         NotificationRetentionDays, NotificationReminderScanMinutes, CalendarWeekStart,
         AttachmentMaxFileBytes, AttachmentMaxFilesPerField, AttachmentAllowedExtensions, AttachmentMaxFilesPerComment,
         OpsSlowRequestMs, OpsClientErrorCollection, OpsUpkeepMinutes, OpsAuditRetentionDays,
         OpsMonitorScanMinutes, OpsHealthResultRetentionDays, OpsIncidentRetentionDays, OpsIncidentSampleRetentionDays,
         OpsIncidentRenotifyHours, OpsIncidentAutoResolveHours, OpsAlertRecipients, OpsLoginFailureWindowMinutes, OpsSchedulerLagIntervals,
         ApprovalMaxStages, ApprovalMaxBranches, ApprovalMaxApprovers, ApprovalMaxDueHours, ApprovalMaxCommentLength, ApprovalMaxManagerLevel,
         ApprovalAllowSelfApproval, ApprovalAdminBypassTransitionGroups, ApprovalMaxAddSignUsers, ApprovalMaxAddSignStages,
         ApprovalOverdueReminderHours, ApprovalDefaultFallbackApprovers, ApprovalAlertRecipients];

    public static PolicyDefinition? Find(string code) =>
        All.FirstOrDefault(d => string.Equals(d.Code, code, StringComparison.Ordinal));
}
