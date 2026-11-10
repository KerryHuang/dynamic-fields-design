namespace DynamicModules.Application.Dynamic.ApprovalService;

/// <summary>簽核定義與約束的錯誤訊息（設計 docs/superpowers/specs/2026-11-10-approval-workflow-design.md §4.3、§5.2、§9.2）。</summary>
public static class ApprovalMessages
{
    // ── 定義結構 ──
    public const string ShapeInvalid = "流程形狀只能是 sequence";
    public static string WhenInvalid(string reason) => $"條件不正確：{reason}";
    public const string RejectToInvalidState = "退回狀態必須是狀態機的狀態";
    public const string FallbackSourceInvalid = "備援簽核人只能是指定使用者、群組或主管";
    public const string FlowKeyReserved = "流程代碼 order 是保留字，請改用其他代碼";
    public const string StagesCount = "關卡必須有 1 到 10 關";
    public const string StageCodeInvalid = "關卡代號只能用小寫英文、數字、連字號，英文開頭，最多 40 字";
    public const string StageCodeDuplicate = "關卡代號不能重複";
    public const string StageNameRequired = "關卡名稱必須有繁體中文";
    public const string ModeInvalid = "簽核模式只能是 all 或 any";
    public const string SourceInvalid = "簽核人來源只能是 users、group、manager、userField、initiatorPick";
    public const string UsersCount = "指定使用者必須有 1 到 20 位";
    public const string UsersInvalid = "指定的使用者必須存在且啟用中";
    public const string GroupsInvalid = "群組至少要有一個，而且都必須存在";
    public const string ManagerLevel = "主管層級必須是 1 到 5";
    public const string UserFieldInvalid = "使用者欄位必須是本模組表頭、啟用中的使用者欄位";
    public const string UserFieldWithoutCandidates = "使用者欄位來源必須設定候選群組";
    public const string CandidateGroupsInvalid = "候選群組都必須存在";
    public const string PickRange = "自選人數必須符合 1 ≤ 最少 ≤ 最多 ≤ 20";
    public const string DueHours = "逾期時數必須是 1 到 8760";
    public const string OnEmptyInvalid = "找不到人時的處理只能是 fallback 或 skip";
    public const string EditableFieldInvalid =
        "可編輯欄位必須是本模組表頭、啟用中的欄位，不能是公式、彙總、自動編號、狀態、轉換追溯、分錄沖銷或鎖定帶值的欄位";

    // ── 入口約束（§5.2、§9.2、§9.3） ──
    public static string EntryMismatch(string s) => $"進入狀態「{s}」的轉移必須都需簽核";
    public static string NoFlow(string s) => $"狀態「{s}」需要簽核，但還沒有任何簽核流程";
    public static string DirectNeedsActive(string s) => $"狀態「{s}」有直接轉移的入口，至少要有一個啟用中的流程";
    public static string LockEntry(string from, string to) => $"「{from} → {to}」進入簽核鎖定的狀態，必須設為需簽核";
    public const string InitialLocked = "起始狀態不能是簽核鎖定狀態";
    public static string LockedMarkedEditable(string s) => $"狀態「{s}」是需簽核轉移的目標，不能標記為可編輯";
    public static string RejectToInvalid(string flow, string from, string rt) =>
        $"流程「{flow}」退回到「{rt}」，但「{from} → {rt}」不是已定義、不需簽核的轉移";
    public static string RejectToLocked(string flow) => $"流程「{flow}」的退回狀態是簽核鎖定狀態，會讓單據不簽就進入鎖定";
    public static string EditableFromLocked(string flow) => $"流程「{flow}」套用的起始狀態是唯讀或簽核鎖定狀態，不能設可編輯欄位";
    public static string EditableInClosure(string field, string via, string flow) => $"可編輯欄位「{field}」經由 {via} 影響流程「{flow}」的條件或簽核人";
    public static string EventPushes(string ev, string s) => $"連動事件「{ev}」會把狀態改成需簽核的「{s}」";

    // ── 簽核人解析（§6.5） ──
    public static string TooManyApprovers(string stage) => $"關卡「{stage}」的簽核人超過 20 人";
    public static string NoApprover(string stage, string source) => $"關卡「{stage}」的來源「{source}」找不到簽核人，備援也沒有人";
    public static string FallbackNote(string source) => $"來源 {source} 找不到簽核人，依設定改派備援簽核人";
    public static string ManagerLabel(int level) => $"第 {level} 層主管";
    public const string PickLabel = "發起人自選";
}
