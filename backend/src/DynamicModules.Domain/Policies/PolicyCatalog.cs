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

    /// <summary>O2：任務規則在時間窗內連續失敗幾次就自動暫停（維運設計 §17.2.7、C3）。改值從下一次失敗起用新值往回數。</summary>
    public static readonly PolicyDefinition OpsRulePauseConsecutiveFailures =
        new("ops.rulePauseConsecutiveFailures", "ops", PolicyKind.Integer, PolicyScope.System, "5", 3, 1000, Unit: "count");

    /// <summary>O2：連續失敗的計算時間窗（分鐘；滑動窗口，O2-U12）。上限 1440 也是 <c>ops.upkeep</c> 清殘留失敗列的界線。</summary>
    public static readonly PolicyDefinition OpsRulePauseWindowMinutes =
        new("ops.rulePauseWindowMinutes", "ops", PolicyKind.Integer, PolicyScope.System, "60", 1, 1440, Unit: "minutes");

    /// <summary>O2：客戶設定類事件與規則自動暫停的站內通知對象（B14）；另允許 <c>ruleEditor</c>（該規則的最後修改者）。解析為空時改送全部系統管理員。</summary>
    public static readonly PolicyDefinition OpsConfigIssueRecipients =
        new("ops.configIssueRecipients", "ops", PolicyKind.Recipients, PolicyScope.System, "ruleEditor,admins", 1, 20,
            DynamicRecipients: [PolicyDefinition.RuleEditor]);

    /// <summary>O2：基礎設施、產品缺陷、安全事件的站內通知對象（解析為空時改送全部系統管理員）。</summary>
    public static readonly PolicyDefinition OpsAlertRecipients =
        new("ops.alertRecipients", "ops", PolicyKind.Recipients, PolicyScope.System, "admins", 1, 20);

    /// <summary>O2：哪些類別的維運事件寄告警信（維運設計 §17.2.6、O2-U2）；出廠不寄客戶設定類（只站內，給設計者）。可為空（一律不寄信）。</summary>
    public static readonly PolicyDefinition OpsAlertEmailCategories =
        new("ops.alertEmailCategories", "ops", PolicyKind.RoleSet, PolicyScope.System, "infra,security,defect",
            Options: ["infra", "security", "defect", "config"]);

    /// <summary>O2：寄告警信的最低嚴重度。</summary>
    public static readonly PolicyDefinition OpsAlertEmailMinSeverity =
        new("ops.alertEmailMinSeverity", "ops", PolicyKind.Choice, PolicyScope.System, "warning", Options: ["warning", "critical"]);

    /// <summary>O2：每小時最多寄幾封告警信，超過的併進下一個整點的摘要信（摘要信本身不受限）。</summary>
    public static readonly PolicyDefinition OpsAlertEmailMaxPerHour =
        new("ops.alertEmailMaxPerHour", "ops", PolicyKind.Integer, PolicyScope.System, "20", 1, 200, Unit: "count");

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

    /// <summary>A16：一個流程的狀態數（流程設計存檔與開啟套件時檢查；改既有流程沒增加狀態不擋）。</summary>
    public static readonly PolicyDefinition DesignMaxWorkflowStates =
        new("design.maxWorkflowStates", "design", PolicyKind.Integer, PolicyScope.System, "30", 1, 100, Unit: "count");

    /// <summary>A16：一個流程的轉換數。</summary>
    public static readonly PolicyDefinition DesignMaxWorkflowTransitions =
        new("design.maxWorkflowTransitions", "design", PolicyKind.Integer, PolicyScope.System, "200", 1, 1000, Unit: "count");

    /// <summary>A16：每個模組的欄位規則數（規則設計存檔與匯入、開啟套件時檢查）。</summary>
    public static readonly PolicyDefinition DesignMaxRulesPerModule =
        new("design.maxRulesPerModule", "design", PolicyKind.Integer, PolicyScope.System, "100", 1, 500, Unit: "count");

    /// <summary>A16：套件內群組數（只在匯入與開啟套件時檢查，設計器不擋）。</summary>
    public static readonly PolicyDefinition DesignMaxGroupsPerPackage =
        new("design.maxGroupsPerPackage", "design", PolicyKind.Integer, PolicyScope.System, "20", 1, 100, Unit: "count");

    /// <summary>A16：套件內模組數（也管 workflows 數；只在匯入與開啟套件時檢查）。</summary>
    public static readonly PolicyDefinition DesignMaxModulesPerPackage =
        new("design.maxModulesPerPackage", "design", PolicyKind.Integer, PolicyScope.System, "30", 1, 100, Unit: "count");

    /// <summary>A16：套件內每個模組的欄位數（只在匯入與開啟套件時檢查）。</summary>
    public static readonly PolicyDefinition DesignMaxFieldsPerModule =
        new("design.maxFieldsPerModule", "design", PolicyKind.Integer, PolicyScope.System, "300", 1, 1000, Unit: "count");

    /// <summary>A16：套件內每個欄位的選項數（含主檔貢獻欄位；只在匯入與開啟套件時檢查）。</summary>
    public static readonly PolicyDefinition DesignMaxOptionsPerField =
        new("design.maxOptionsPerField", "design", PolicyKind.Integer, PolicyScope.System, "200", 1, 1000, Unit: "count");

    /// <summary>A16：套件內每個模組的檢視數（只在匯入與開啟套件時檢查）。</summary>
    public static readonly PolicyDefinition DesignMaxViewsPerModule =
        new("design.maxViewsPerModule", "design", PolicyKind.Integer, PolicyScope.System, "20", 1, 100, Unit: "count");

    /// <summary>A16：套件內整合數（只在匯入與開啟套件時檢查）。</summary>
    public static readonly PolicyDefinition DesignMaxIntegrationsPerPackage =
        new("design.maxIntegrationsPerPackage", "design", PolicyKind.Integer, PolicyScope.System, "20", 1, 100, Unit: "count");

    /// <summary>A19：新模組是否自動建「名稱」欄位並設為標題欄位；「基本資料」區塊一律建立（從範本建立固定不建）。</summary>
    public static readonly PolicyDefinition ModuleCreateTitleField =
        new("module.createTitleField", "design", PolicyKind.Boolean, PolicyScope.System, "true");

    /// <summary>A25：新區塊的每列欄數；超過 <see cref="FormMaxColumns"/> 時用上限。一般與明細模組都可覆寫。</summary>
    public static readonly PolicyDefinition FormDefaultColumns =
        new("form.defaultColumns", "form", PolicyKind.Integer, PolicyScope.Module, "4", 1, 4, Unit: "count", Target: PolicyModuleTarget.Any);

    /// <summary>
    /// A25：區塊每列欄數上限；調低只管之後的設定（既有區塊照原欄數）。技術上限 4 對應前端 <c>form-layout.ts</c> 的
    /// <c>fieldColClass</c> 斷點只排到 4 欄，兩邊一起改。
    /// </summary>
    public static readonly PolicyDefinition FormMaxColumns =
        new("form.maxColumns", "form", PolicyKind.Integer, PolicyScope.System, "4", 1, 4, Unit: "count");

    /// <summary>A21：一次匯入的筆數（CSV 資料列，不含表頭）。</summary>
    public static readonly PolicyDefinition ImportMaxRows =
        new("import.maxRows", "import", PolicyKind.Integer, PolicyScope.System, "1000", 1, 10000, Unit: "count");

    /// <summary>A21：匯入檔案大小（位元組）；匯入端點的請求上限＝兩倍這個值＋multipart 開銷。</summary>
    public static readonly PolicyDefinition ImportMaxFileBytes =
        new("import.maxFileBytes", "import", PolicyKind.Integer, PolicyScope.System, "1048576", 1024, 10485760, Unit: "bytes");

    /// <summary>A22：每個來源模組的連動事件數（新增事件時檢查；更新既有事件不擋）。套件的事件總數 100 是技術限制。</summary>
    public static readonly PolicyDefinition EventMaxEventsPerModule =
        new("event.maxEventsPerModule", "event", PolicyKind.Integer, PolicyScope.System, "20", 1, 100, Unit: "count");

    /// <summary>A22：每個事件的效果數（編輯既有事件只在效果數增加時檢查）。</summary>
    public static readonly PolicyDefinition EventMaxEffects =
        new("event.maxEffects", "event", PolicyKind.Integer, PolicyScope.System, "10", 1, 50, Unit: "count");

    /// <summary>A22：連動事件的串聯層數。</summary>
    public static readonly PolicyDefinition EventMaxDepth =
        new("event.maxDepth", "event", PolicyKind.Integer, PolicyScope.System, "5", 1, 10, Unit: "count");

    /// <summary>A22：一次存檔中連動事件寫入的記錄數。</summary>
    public static readonly PolicyDefinition EventMaxWritesPerRequest =
        new("event.maxWritesPerRequest", "event", PolicyKind.Integer, PolicyScope.System, "500", 1, 5000, Unit: "count");

    /// <summary>A22：逐列產生記錄時，來源明細的列數上限。</summary>
    public static readonly PolicyDefinition EventMaxEachRows =
        new("event.maxEachRows", "event", PolicyKind.Integer, PolicyScope.System, "200", 1, 1000, Unit: "count");

    /// <summary>A23：每張單據的明細列數；覆寫設在明細模組上（調低後只擋這次比原本多的存檔）。</summary>
    public static readonly PolicyDefinition RecordMaxDetailRows =
        new("record.maxDetailRows", "record", PolicyKind.Integer, PolicyScope.Module, "200", 1, 1000, Unit: "count", Target: PolicyModuleTarget.Detail);

    /// <summary>A24：報表下鑽最多回幾筆。</summary>
    public static readonly PolicyDefinition ReportDrillLimit =
        new("report.drillLimit", "report", PolicyKind.Integer, PolicyScope.System, "500", 1, 5000, Unit: "count");

    /// <summary>A24：報表明細最多回幾列。</summary>
    public static readonly PolicyDefinition ReportDetailLimit =
        new("report.detailLimit", "report", PolicyKind.Integer, PolicyScope.System, "1000", 1, 10000, Unit: "count");

    /// <summary>
    /// A24：報表日期區間與「近 N 天」的天數上限。技術上限 3650 等於 Domain <c>ConditionParser.MaxDays</c>（列表與規則條件的「近 N 天」），
    /// 只能調低，避免報表比列表寬（使用者 2026-11-12 裁決）。
    /// </summary>
    public static readonly PolicyDefinition ReportMaxRangeDays =
        new("report.maxRangeDays", "report", PolicyKind.Integer, PolicyScope.System, "3650", 1, 3650, Unit: "days");

    /// <summary>A24：每個儀表板的卡片數（調低後只擋這次比原本多的存檔）。</summary>
    public static readonly PolicyDefinition DashboardMaxCards =
        new("dashboard.maxCards", "report", PolicyKind.Integer, PolicyScope.System, "40", 1, 100, Unit: "count");

    public static DayOfWeek WeekStartOf(string choice) => Enum.Parse<DayOfWeek>(choice, ignoreCase: true);

    public static IReadOnlyList<PolicyDefinition> All { get; } =
        [TaskMaxRulesPerModule, TaskMaxTasksPerRule, TaskMaxRulesPerPackage,
         TaskReminderLeadDays, TaskRemindOverdue, TaskNotifyCommented, TaskNotifyCompleted, TaskNotifyCancelled, TaskNotifySubtasksDone,
         TaskSupervisorScope, TaskSupervisorScopeRecursive, TaskMaxSubtasksPerTask,
         NotificationRetentionDays, NotificationReminderScanMinutes, CalendarWeekStart,
         AttachmentMaxFileBytes, AttachmentMaxFilesPerField, AttachmentAllowedExtensions, AttachmentMaxFilesPerComment,
         OpsSlowRequestMs, OpsClientErrorCollection, OpsUpkeepMinutes, OpsAuditRetentionDays,
         OpsMonitorScanMinutes, OpsHealthResultRetentionDays, OpsIncidentRetentionDays, OpsIncidentSampleRetentionDays,
         OpsIncidentRenotifyHours, OpsIncidentAutoResolveHours, OpsRulePauseConsecutiveFailures, OpsRulePauseWindowMinutes,
         OpsConfigIssueRecipients, OpsAlertRecipients, OpsAlertEmailCategories, OpsAlertEmailMinSeverity, OpsAlertEmailMaxPerHour,
         OpsLoginFailureWindowMinutes, OpsSchedulerLagIntervals,
         ApprovalMaxStages, ApprovalMaxBranches, ApprovalMaxApprovers, ApprovalMaxDueHours, ApprovalMaxCommentLength, ApprovalMaxManagerLevel,
         ApprovalAllowSelfApproval, ApprovalAdminBypassTransitionGroups, ApprovalMaxAddSignUsers, ApprovalMaxAddSignStages,
         ApprovalOverdueReminderHours, ApprovalDefaultFallbackApprovers, ApprovalAlertRecipients,
         DesignMaxWorkflowStates, DesignMaxWorkflowTransitions, DesignMaxRulesPerModule, DesignMaxGroupsPerPackage,
         DesignMaxModulesPerPackage, DesignMaxFieldsPerModule, DesignMaxOptionsPerField, DesignMaxViewsPerModule,
         DesignMaxIntegrationsPerPackage, ModuleCreateTitleField, FormDefaultColumns, FormMaxColumns,
         ImportMaxRows, ImportMaxFileBytes, EventMaxEventsPerModule, EventMaxEffects, EventMaxDepth, EventMaxWritesPerRequest,
         EventMaxEachRows, RecordMaxDetailRows, ReportDrillLimit, ReportDetailLimit, ReportMaxRangeDays, DashboardMaxCards];

    public static PolicyDefinition? Find(string code) =>
        All.FirstOrDefault(d => string.Equals(d.Code, code, StringComparison.Ordinal));
}
