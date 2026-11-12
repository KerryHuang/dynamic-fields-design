using DynamicModules.Application.Common;
using DynamicModules.Domain.ValueObjects;

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
    public const string OnEmptySkipUnmatched = "這一關的簽核人來源有對不上的群組，找不到人時只能交給備援簽核人，不能跳過";
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
    // 以下到「送出與動作」都是一般使用者執行期會看到的訊息：回 MessageRef（鍵在 MessageText），到 WebAPI 才依請求語系解析。
    // 關卡、來源名稱等參數可直接傳 I18nText（會依語系挑文字）或字串。
    public static MessageRef TooManyApprovers(object stage) => MessageRef.Of(MessageText.ApprovalTooManyApprovers, stage);
    public static MessageRef FlowConditionUndecidable(object flow) => MessageRef.Of(MessageText.ApprovalFlowUndecidable, flow);
    public static MessageRef NoApprover(object stage, object source) => MessageRef.Of(MessageText.ApprovalNoApprover, stage, source);
    /// <summary>備援說明會寫進簽核歷程（資料庫），固定繁中，不隨請求語系。</summary>
    public static string FallbackNote(string source) => $"來源 {source} 找不到簽核人，依設定改派備援簽核人";
    /// <summary>重新比對的歷程說明（設計 §5.5 第 4 到 6 點），同樣固定繁中。</summary>
    public static string RerouteNote(string flow) => $"單據內容已變，改用流程「{flow}」重新簽核";
    public const string RerouteFallbackNote = "單據內容已變，沒有符合的流程，改由備援簽核人重新簽核";
    public static string ReroutePickNote(string flow) => $"單據內容已變，改用流程「{flow}」，需由發起人重新送出";
    public static readonly I18nText FallbackStageName = MessageText.AsI18n(MessageText.ApprovalFallbackStage);
    public static string ManagerLabel(int level) => MessageText.Get(null, MessageText.ApprovalManagerLabel, level);
    public static I18nText ManagerLabelI18n(int level) => MessageText.AsI18n(MessageText.ApprovalManagerLabel, level);
    public static readonly string PickLabel = MessageText.Get(null, MessageText.ApprovalPickLabel);
    public static readonly I18nText PickLabelI18n = MessageText.AsI18n(MessageText.ApprovalPickLabel);

    // ── 簽核中的寫入（§5.3、§8.5） ──
    public static readonly MessageRef NeedsSubmit = MessageRef.Of(MessageText.ApprovalNeedsSubmit);
    public static readonly MessageRef Pending = MessageRef.Of(MessageText.ApprovalPending);
    public static readonly MessageRef WithdrawFirst = MessageRef.Of(MessageText.ApprovalWithdrawFirst);
    public static MessageRef EventBlocked(long id) => MessageRef.Of(MessageText.ApprovalEventBlocked, id);
    public static MessageRef TransitionBlocked(long id) => MessageRef.Of(MessageText.ApprovalTransitionBlocked, id);
    /// <summary>鎖定狀態的記錄被改到鎖定欄位（§8.5、§9.3）；fieldLabel 可傳 I18nText 或字串。</summary>
    public static MessageRef FieldLocked(object fieldLabel) => MessageRef.Of(MessageText.ApprovalFieldLocked, fieldLabel);

    /// <summary>鎖定狀態的記錄不能再上傳附件（§8.5、§9.3）。</summary>
    public static readonly MessageRef AttachmentLocked = MessageRef.Of(MessageText.AttachmentLocked);

    // ── 送出與動作（§5.2、§7） ──
    public static readonly MessageRef NotApprovalTransition = MessageRef.Of(MessageText.ApprovalNotApprovalTransition);
    public static readonly MessageRef AlreadyPending = MessageRef.Of(MessageText.ApprovalAlreadyPending);
    public static readonly MessageRef NoMatchingFlow = MessageRef.Of(MessageText.ApprovalNoMatchingFlow);
    public static readonly MessageRef PickInvalid = MessageRef.Of(MessageText.ApprovalPickInvalid);
    public static readonly MessageRef PickCandidatesUnmatched = MessageRef.Of(MessageText.ApprovalPickCandidatesUnmatched);
    public static MessageRef PickCount(int min, int max) => MessageRef.Of(MessageText.ApprovalPickCount, min, max);
    public static readonly MessageRef UserFieldNotCandidate = MessageRef.Of(MessageText.ApprovalUserFieldNotCandidate);
    public static readonly MessageRef UserFieldIsInitiator = MessageRef.Of(MessageText.ApprovalUserFieldIsInitiator);
    public static readonly MessageRef CommentRequired = MessageRef.Of(MessageText.ApprovalCommentRequired);
    public static readonly MessageRef CommentTooLong = MessageRef.Of(MessageText.ApprovalCommentTooLong);
    public static readonly MessageRef TaskNotFound = MessageRef.Of(MessageText.ApprovalTaskNotFound);
    public static readonly MessageRef StageHandled = MessageRef.Of(MessageText.ApprovalStageHandled);
    public static readonly MessageRef StateChanged = MessageRef.Of(MessageText.ApprovalStateChanged);
    public static readonly MessageRef CannotWithdraw = MessageRef.Of(MessageText.ApprovalCannotWithdraw);
    public static readonly MessageRef NoPreviousStage = MessageRef.Of(MessageText.ApprovalNoPreviousStage);
    public static readonly MessageRef NotEditableField = MessageRef.Of(MessageText.ApprovalNotEditableField);
    public static readonly MessageRef FieldHiddenForApprover = MessageRef.Of(MessageText.ApprovalFieldHiddenForApprover);
    public static readonly MessageRef FieldReadOnlyForApprover = MessageRef.Of(MessageText.ApprovalFieldReadOnlyForApprover);
    public static readonly MessageRef RejectTargetInvalid = MessageRef.Of(MessageText.ApprovalRejectTargetInvalid);
    public static readonly MessageRef RuntimeInvalid = MessageRef.Of(MessageText.ApprovalRuntimeInvalid);
    public static readonly MessageRef RecordGone = MessageRef.Of(MessageText.ApprovalRecordGone);
    public static readonly MessageRef NoWorkflow = MessageRef.Of(MessageText.ApprovalNoWorkflow);
    public static readonly MessageRef FlowVersionMissing = MessageRef.Of(MessageText.ApprovalFlowVersionMissing);
    public static readonly MessageRef StageDefMissing = MessageRef.Of(MessageText.ApprovalStageDefMissing);
    public static readonly MessageRef BoxInvalid = MessageRef.Of(MessageText.ApprovalBoxInvalid);
    public static readonly MessageRef StatusInvalid = MessageRef.Of(MessageText.ApprovalStatusInvalid);

    // 設計器的定義驗證訊息（管理類，不在執行期語系化範圍）
    public const string EditableFieldHidden = "可編輯欄位不能是對這一關簽核人（或備援）所屬群組隱藏或限定可見的欄位";
}

