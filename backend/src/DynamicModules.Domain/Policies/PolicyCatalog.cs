using DynamicModules.Domain.Localization;

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
    /// A17：營運時區可選的固定 UTC 偏移（世界實際使用的 38 個；不存 IANA 名稱、不支援日光節約，計畫判斷 1）。
    /// 技術清單，不是業務政策。
    /// </summary>
    public static readonly IReadOnlyList<string> UtcOffsets =
    [
        "-12:00", "-11:00", "-10:00", "-09:30", "-09:00", "-08:00", "-07:00", "-06:00", "-05:00", "-04:00", "-03:30", "-03:00",
        "-02:00", "-01:00", "+00:00", "+01:00", "+02:00", "+03:00", "+03:30", "+04:00", "+04:30", "+05:00", "+05:30", "+05:45",
        "+06:00", "+06:30", "+07:00", "+08:00", "+08:45", "+09:00", "+09:30", "+10:00", "+10:30", "+11:00", "+12:00", "+12:45",
        "+13:00", "+14:00",
    ];

    /// <summary>
    /// A17：營運時區。決定「今天」、逾期、報表日期切點與日期時間的顯示；改值時同一交易內平移日期時間欄位的索引。
    /// 多台伺服器時其他台最多 60 秒後才換（快取存活時間）。
    /// </summary>
    public static readonly PolicyDefinition LocaleTimeZone =
        new("locale.timeZone", "locale", PolicyKind.UtcOffset, PolicyScope.System, "+08:00", Options: UtcOffsets);

    /// <summary>
    /// 語系類政策的出廠選項：內建語系（判斷 22、23）。執行期的選項一律經 Application 的 <c>PolicyOptionSource</c> 取已安裝語系。
    /// </summary>
    public static IReadOnlyList<string> FactoryLocales { get; } = BuiltinLocales.Codes;

    /// <summary>A18：預設語系。沒選過語言的人看到的語系，也是請求沒指定語系時的回應語系。</summary>
    public static readonly PolicyDefinition LocaleDefault =
        new("locale.default", "locale", PolicyKind.Choice, PolicyScope.System, "zh-TW", Options: FactoryLocales);

    /// <summary>A18：啟用語系。語言選單與多語輸入框只列這些；停用不刪已輸入的文字。</summary>
    public static readonly PolicyDefinition LocaleEnabled =
        new("locale.enabled", "locale", PolicyKind.RoleSet, PolicyScope.System, "zh-TW,en-US,th,vi", Options: FactoryLocales);

    /// <summary>A18：必填語系。所有必填的多語文字都要填這些語系；增加後只在新增或修改那段文字時檢查；應用程式套件仍要求繁中。</summary>
    public static readonly PolicyDefinition LocaleRequiredText =
        new("locale.requiredText", "locale", PolicyKind.RoleSet, PolicyScope.System, "zh-TW", Options: FactoryLocales);

    /// <summary>A38：語系包檔案大小上限（語系包設計 §13.1、§14；技術上限 20 MB）。匯入語系包時檢查，Excel 翻譯樣板（i4e）共用。</summary>
    public static readonly PolicyDefinition LocalePackMaxBytes =
        new("locale.packMaxBytes", "locale", PolicyKind.Integer, PolicyScope.System, "5242880", 1024, 20971520, Unit: "bytes");

    /// <summary>
    /// A39：已安裝語系數上限（含內建四種，所以下限是 4；技術上限 30）。每個語系都在伺服器記憶體裡留一份合併表。
    /// 改嚴只擋新安裝：改成比目前已安裝數小不擋存檔，既有語系照常可用。
    /// </summary>
    public static readonly PolicyDefinition LocaleMaxInstalled =
        new("locale.maxInstalled", "locale", PolicyKind.Integer, PolicyScope.System, "10", 4, 30, Unit: "count");

    /// <summary>選項是語系清單的政策（預設、啟用、必填語系）；一週起始日同在 locale 分類但不是。</summary>
    public static bool IsLocaleOptions(PolicyDefinition def) => ReferenceEquals(def.Options, FactoryLocales);

    /// <summary>A5：部門層數上限；只在新增部門或改上級部門時檢查，已超過的部門照常可改其他欄位。</summary>
    public static readonly PolicyDefinition OrganizationMaxDepth =
        new("organization.maxDepth", "organization", PolicyKind.Integer, PolicyScope.System, "10", 1, 30, Unit: "count");

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

    /// <summary>
    /// C3（MCP 設計 §20、mcp2a 計畫判斷 8、D4）：同一把 AI 權杖在時間窗內成功修改同一筆記錄幾次，就暫停它對這筆的寫入並開維運事件。
    /// 只計成功、非回放、非預覽的寫入；時間窗滑過自動恢復。
    /// </summary>
    public static readonly PolicyDefinition OpsRuleLoopTriggersPerRecord =
        new("ops.ruleLoopTriggersPerRecord", "ops", PolicyKind.Integer, PolicyScope.System, "30", 5, 1000, Unit: "count");

    /// <summary>計算 <see cref="OpsRuleLoopTriggersPerRecord"/> 的時間窗（分鐘；滑動窗口，mcp2a 計畫 D5）。</summary>
    public static readonly PolicyDefinition OpsRuleLoopWindowMinutes =
        new("ops.ruleLoopWindowMinutes", "ops", PolicyKind.Integer, PolicyScope.System, "10", 1, 1440, Unit: "minutes");

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

    /// <summary>A16：套件內簽核流程數（只在匯入與開啟套件時檢查；使用者 2026-11-20 裁決改成政策）。</summary>
    public static readonly PolicyDefinition DesignMaxApprovalFlowsPerPackage =
        new("design.maxApprovalFlowsPerPackage", "design", PolicyKind.Integer, PolicyScope.System, "50", 1, 100, Unit: "count");

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

    /// <summary>A26：重送保護（Idempotency-Key）紀錄保留天數；過期的在下一次帶 key 的寫入時清掉。代碼 maintenance.*，分類沿用 O1 的 ops。</summary>
    public static readonly PolicyDefinition MaintenanceIdempotencyRetentionDays =
        new("maintenance.idempotencyRetentionDays", "ops", PolicyKind.Integer, PolicyScope.System, "7", 1, 90, Unit: "days");

    /// <summary>A27：孤兒附件清掃的寬限分鐘數；清掃 API 沒帶 graceMinutes 時才用（明確傳值含 0 照參數）。</summary>
    public static readonly PolicyDefinition MaintenanceOrphanSweepGraceMinutes =
        new("maintenance.orphanSweepGraceMinutes", "ops", PolicyKind.Integer, PolicyScope.System, "60", 1, 1440, Unit: "minutes");

    /// <summary>A28：檢查試跑最多回報幾組違規。</summary>
    public static readonly PolicyDefinition CheckScanMaxViolations =
        new("check.scanMaxViolations", "check", PolicyKind.Integer, PolicyScope.System, "50", 1, 500, Unit: "count");

    /// <summary>A28：檢查試跑最多掃描幾組，超過時停止並標示未掃完。</summary>
    public static readonly PolicyDefinition CheckScanMaxGroups =
        new("check.scanMaxGroups", "check", PolicyKind.Integer, PolicyScope.System, "20000", 1000, 200000, Unit: "count");

    /// <summary>A29：一次寫入後最多驗證幾組跨記錄檢查，超過時整筆取消。</summary>
    public static readonly PolicyDefinition CheckMaxGroupsPerWrite =
        new("check.maxGroupsPerWrite", "check", PolicyKind.Integer, PolicyScope.System, "500", 1, 5000, Unit: "count");

    /// <summary>A30：儲存檢查時回填索引的模組記錄數上限（回填與儲存同一個交易，持有寫入鎖）；每批筆數 <c>CheckIndexer.BatchSize</c> 是技術常數。</summary>
    public static readonly PolicyDefinition CheckMaxBackfillRecords =
        new("check.maxBackfillRecords", "check", PolicyKind.Integer, PolicyScope.System, "200000", 1000, 1000000, Unit: "count");

    // mcp1a：AI 連線（MCP 設計 §13.6；出廠值與技術上下限使用者 2026-11-12 確認）。全部只限全系統。

    /// <summary>MCP 總開關：關閉時不能建立權杖、<c>/mcp</c> 一律拒絕；列出與撤銷照常可用（§13.5）。</summary>
    public static readonly PolicyDefinition McpEnabled =
        new("mcp.enabled", "mcp", PolicyKind.Boolean, PolicyScope.System, "false");

    /// <summary>可建立與使用權杖的對象（群組、個別使用者或全部系統管理員）；合法的空值＝全部使用者，有項目但展開為空＝沒有人。</summary>
    public static readonly PolicyDefinition McpAllowedGroups =
        new("mcp.allowedGroups", "mcp", PolicyKind.Recipients, PolicyScope.System, "", 0, 50);

    /// <summary>權杖最長效期（天）。</summary>
    public static readonly PolicyDefinition McpTokenMaxLifetimeDays =
        new("mcp.tokenMaxLifetimeDays", "mcp", PolicyKind.Integer, PolicyScope.System, "90", 1, 365, Unit: "days");

    /// <summary>建立時預設效期（天）；大於最長效期時取用時以最長效期為準（T15，不擋存檔）。</summary>
    public static readonly PolicyDefinition McpTokenDefaultLifetimeDays =
        new("mcp.tokenDefaultLifetimeDays", "mcp", PolicyKind.Integer, PolicyScope.System, "30", 1, 365, Unit: "days");

    /// <summary>每人同時有效（未撤銷、未過期）的權杖數；調低不溯及既有權杖。</summary>
    public static readonly PolicyDefinition McpTokenMaxPerUser =
        new("mcp.tokenMaxPerUser", "mcp", PolicyKind.Integer, PolicyScope.System, "10", 1, 50, Unit: "count");

    /// <summary>每把權杖每分鐘的工具呼叫上限。</summary>
    public static readonly PolicyDefinition McpCallsPerMinute =
        new("mcp.callsPerMinute", "mcp", PolicyKind.Integer, PolicyScope.System, "60", 1, 600, Unit: "count");

    /// <summary>每把權杖每日（營運時區）的工具呼叫上限。</summary>
    public static readonly PolicyDefinition McpCallsPerDay =
        new("mcp.callsPerDay", "mcp", PolicyKind.Integer, PolicyScope.System, "5000", 100, 100000, Unit: "count");

    /// <summary>單次工具結果的筆數上限；上限 200 對齊 <c>PagedRequest.MaxPagesSize</c>。</summary>
    public static readonly PolicyDefinition McpMaxRows =
        new("mcp.maxRows", "mcp", PolicyKind.Integer, PolicyScope.System, "50", 1, 200, Unit: "count");

    /// <summary>高風險工具確認碼的有效時間（秒）。</summary>
    public static readonly PolicyDefinition McpConfirmTtlSeconds =
        new("mcp.confirmTtlSeconds", "mcp", PolicyKind.Integer, PolicyScope.System, "300", 60, 1800, Unit: "seconds");

    public static DayOfWeek WeekStartOf(string choice) => Enum.Parse<DayOfWeek>(choice, ignoreCase: true);

    /// <summary><c>+05:45</c> → 5 小時 45 分、<c>-03:30</c> → 負 3 小時 30 分；只收 <see cref="UtcOffsets"/> 的標準寫法。</summary>
    public static TimeSpan UtcOffsetOf(string canonical)
    {
        if (canonical.Length != 6 || canonical[0] is not ('+' or '-') || canonical[3] != ':'
            || !int.TryParse(canonical.AsSpan(1, 2), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var h)
            || !int.TryParse(canonical.AsSpan(4, 2), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var m))
            throw new FormatException($"不是 UTC 偏移：{canonical}");
        var span = new TimeSpan(h, m, 0);
        return canonical[0] == '-' ? -span : span;
    }

    /// <summary>偏移 → <c>+08:00</c> 寫法（零偏移帶正號 <c>+00:00</c>）。</summary>
    public static string FormatUtcOffset(TimeSpan offset) =>
        (offset < TimeSpan.Zero ? "-" : "+") + offset.Duration().ToString("hh\\:mm", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>營運時區的出廠偏移（給 Domain 的預設參數用；執行期一律由呼叫端傳入生效值）。</summary>
    public static TimeSpan FactoryBusinessOffset => UtcOffsetOf(LocaleTimeZone.Factory);

    public static IReadOnlyList<PolicyDefinition> All { get; } =
        [TaskMaxRulesPerModule, TaskMaxTasksPerRule, TaskMaxRulesPerPackage,
         TaskReminderLeadDays, TaskRemindOverdue, TaskNotifyCommented, TaskNotifyCompleted, TaskNotifyCancelled, TaskNotifySubtasksDone,
         TaskSupervisorScope, TaskSupervisorScopeRecursive, TaskMaxSubtasksPerTask,
         NotificationRetentionDays, NotificationReminderScanMinutes, CalendarWeekStart, LocaleTimeZone, LocaleDefault, LocaleEnabled, LocaleRequiredText,
         LocalePackMaxBytes, LocaleMaxInstalled,
         OrganizationMaxDepth,
         AttachmentMaxFileBytes, AttachmentMaxFilesPerField, AttachmentAllowedExtensions, AttachmentMaxFilesPerComment,
         OpsSlowRequestMs, OpsClientErrorCollection, OpsUpkeepMinutes, OpsAuditRetentionDays,
         OpsMonitorScanMinutes, OpsHealthResultRetentionDays, OpsIncidentRetentionDays, OpsIncidentSampleRetentionDays,
         OpsIncidentRenotifyHours, OpsIncidentAutoResolveHours, OpsRulePauseConsecutiveFailures, OpsRulePauseWindowMinutes,
         OpsRuleLoopTriggersPerRecord, OpsRuleLoopWindowMinutes,
         OpsConfigIssueRecipients, OpsAlertRecipients, OpsAlertEmailCategories, OpsAlertEmailMinSeverity, OpsAlertEmailMaxPerHour,
         OpsLoginFailureWindowMinutes, OpsSchedulerLagIntervals,
         ApprovalMaxStages, ApprovalMaxBranches, ApprovalMaxApprovers, ApprovalMaxDueHours, ApprovalMaxCommentLength, ApprovalMaxManagerLevel,
         ApprovalAllowSelfApproval, ApprovalAdminBypassTransitionGroups, ApprovalMaxAddSignUsers, ApprovalMaxAddSignStages,
         ApprovalOverdueReminderHours, ApprovalDefaultFallbackApprovers, ApprovalAlertRecipients,
         DesignMaxWorkflowStates, DesignMaxWorkflowTransitions, DesignMaxRulesPerModule, DesignMaxGroupsPerPackage,
         DesignMaxModulesPerPackage, DesignMaxFieldsPerModule, DesignMaxOptionsPerField, DesignMaxViewsPerModule,
         DesignMaxIntegrationsPerPackage, DesignMaxApprovalFlowsPerPackage, ModuleCreateTitleField, FormDefaultColumns, FormMaxColumns,
         ImportMaxRows, ImportMaxFileBytes, EventMaxEventsPerModule, EventMaxEffects, EventMaxDepth, EventMaxWritesPerRequest,
         EventMaxEachRows, RecordMaxDetailRows, ReportDrillLimit, ReportDetailLimit, ReportMaxRangeDays, DashboardMaxCards,
         MaintenanceIdempotencyRetentionDays, MaintenanceOrphanSweepGraceMinutes, CheckScanMaxViolations, CheckScanMaxGroups,
         CheckMaxGroupsPerWrite, CheckMaxBackfillRecords,
         McpEnabled, McpAllowedGroups, McpTokenMaxLifetimeDays, McpTokenDefaultLifetimeDays, McpTokenMaxPerUser,
         McpCallsPerMinute, McpCallsPerDay, McpMaxRows, McpConfirmTtlSeconds];

    public static PolicyDefinition? Find(string code) =>
        All.FirstOrDefault(d => string.Equals(d.Code, code, StringComparison.Ordinal));
}
