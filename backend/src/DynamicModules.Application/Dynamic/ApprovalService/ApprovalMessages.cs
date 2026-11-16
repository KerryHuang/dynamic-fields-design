using DynamicModules.Application.Common;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.ApprovalService;

/// <summary>簽核定義與約束的錯誤訊息（設計 docs/superpowers/specs/2026-11-10-approval-workflow-design.md §4.3、§5.2、§9.2）。</summary>
public static class ApprovalMessages
{
    // ── 定義結構 ──
    public const string ShapeInvalid = "流程形狀只能是 sequence 或 graph";
    public static string WhenInvalid(string reason) => $"條件不正確：{reason}";
    public const string RejectToInvalidState = "退回狀態必須是狀態機的狀態";
    public const string FallbackSourceInvalid = "備援簽核人只能是指定使用者、群組或主管";
    public const string TargetStateInvalid = "目標狀態必須是狀態機的狀態，而且不是起始狀態";
    public const string DefinitionMissing = "缺少流程定義";
    public const string FlowKeyReserved = "流程代碼 order 是保留字，請改用其他代碼";
    public const string FlowNotPublished = "這條流程還沒有發布的版本，請先發布";
    /// <summary>通知節點在 fg5 之前存檔拒收（U2）；執行期另由能力守門擋。fg5 刪除。</summary>
    public const string NotifyNodeNotYet = "通知節點尚未開放";
    /// <summary>目前版本已是流程圖格式的流程，存檔時不能換回關卡格式（U4）。</summary>
    public const string GraphCannotRevert = "這條流程已是流程圖格式，不能改回關卡格式";
    public const string NodeNameRequired = "節點名稱必須有繁體中文";
    public const string EdgeLabelInvalid = "連線標籤必須有繁體中文";
    public static string NotifyRecipientsCount(int max) => $"通知收件人設定必須有 1 到 {max} 項";
    public const string NotifyRecipientInvalid = "通知收件人的種類只能是 users、group、initiator、initiatorManager，而且只能帶該種類的參數";
    public const string TemplateRequired = "通知範本的標題與內文都必須有繁體中文";
    public static string TemplateTooLong(int max) => $"通知範本每個語系的文字不能超過 {max} 字";
    public static string TemplatePlaceholderUnknown(string text) => $"通知範本有不認得的佔位符 {text}，可用的是 {{title}}、{{flow}}、{{initiator}}、{{node}}、{{f:欄位編號}}";
    public const string TemplateFieldInvalid = "通知範本引用的欄位必須是本模組表頭、啟用中的欄位";

    /// <summary>流程圖結構驗證（Domain ApprovalGraphValidator）的代碼對照；代碼與規則見流程圖設計 §9。數量上限的訊息要帶政策值。</summary>
    public static string Graph(string code, int maxApprovalNodes = 0, int maxBranches = 0) => code switch
    {
        // G1
        "idInvalid" => "節點與連線的代號只能用小寫英文、數字、連字號，英文開頭，最多 40 字",
        "idDuplicate" => "節點與連線的代號不能重複",
        "kindInvalid" => "節點種類只能是 start、approval、condition、fork、join、end、notify",
        "stagesNotAllowed" => "流程圖格式不能同時有關卡（stages 必須是空的）",
        "nodesMissing" => "流程圖缺少節點",
        "edgeEndpointMissing" => "連線的起點與終點必須是存在的節點",
        "selfLoop" => "連線不能連回自己",
        "edgeDuplicate" => "同一對節點之間不能有兩條連線",
        "attrNotAllowed" => "這種節點不能設定這個屬性",
        // G2
        "startCount" => "必須恰有一個開始節點",
        "endMissing" => "至少要有一個結束節點",
        "inCount" => "這個節點的入線數量不符合規定",
        "outCount" => "這個節點的出線數量不符合規定",
        // G3、G4、G5
        "unreachable" => "這個節點無法從開始節點到達",
        "deadEnd" => "這個節點走不到任何結束節點",
        "cycle" => "流程圖不能有環",
        // G6
        "defaultCount" => "條件節點必須恰有一條預設線",
        "whenMissing" => "條件節點的非預設線必須設定條件",
        "orderInvalid" => "條件節點的非預設線必須有順序（正整數）",
        "orderDuplicate" => "同一個條件節點的順序不能重複",
        "defaultHasWhen" => "預設線不能有條件",
        "conditionAttrNotAllowed" => "只有條件節點的出線可以設定條件、順序與預設",
        // G7
        "forkWithoutJoin" => "每個分支節點都要有一個匯合節點配對",
        "joinForkInvalid" => "匯合節點必須指向一個存在的分支節點，而且一個分支節點只能有一個匯合節點",
        "joinModeInvalid" => "匯合方式只能是 all（全部到齊）或 any（任一到齊）",
        "edgeIntoRegion" => "並行區外的連線不能連進並行區內",
        "edgeOutOfRegion" => "並行區內的連線不能連到並行區外",
        "joinInEdgeOutside" => "匯合節點的入線必須都來自自己的並行區",
        "branchesMerge" => "同一個分支節點的各分支不能在匯合節點以外的地方會合",
        "regionCross" => "並行區可以巢狀，但不能交錯",
        // G8
        "endInRegion" => "結束節點不能在並行區內",
        // G12
        "editableOverlap" => "並行的不同分支，簽核節點的可編輯欄位不能重複",
        // G13
        "approvalNodesNone" => "流程圖至少要有一個簽核節點",
        "approvalNodesTooMany" => $"簽核節點必須有 1 到 {maxApprovalNodes} 個",
        "branchesTooMany" => $"每個分支節點最多 {maxBranches} 條分支",
        "nodesTooMany" => $"節點總數不能超過 {ApprovalGraphLimits.MaxNodes} 個",
        "edgesTooMany" => $"連線總數不能超過 {ApprovalGraphLimits.MaxEdges} 條",
        "nestingTooDeep" => $"並行區最多巢狀 {ApprovalGraphLimits.MaxNesting} 層",
        // 警告 W1–W3（發布回應在 fg3 才帶出）
        "anyJoinWithoutApproval" => "此並行區可能不需簽核就通過",
        "defaultToEndWithoutApproval" => "預設路線不經任何簽核就結束",
        "skipConditionOnConditionBranch" => "這個簽核節點同時有略過條件，又在條件節點的出線上，請確認是刻意的",
        _ => $"流程圖結構不正確（{code}）"
    };
    public static string StagesCount(int max, bool existing = false) => $"關卡必須有 1 到 {max} 關{KeepOld(existing)}";
    public const string StageCodeInvalid = "關卡代號只能用小寫英文、數字、連字號，英文開頭，最多 40 字";
    public const string StageCodeDuplicate = "關卡代號不能重複";
    public const string StageNameRequired = "關卡名稱必須有繁體中文";
    public const string ModeInvalid = "簽核模式只能是 all 或 any";
    public const string SourceInvalid = "簽核人來源只能是 users、group、manager、userField、initiatorPick";
    public static string UsersCount(int max, bool existing = false) => $"指定使用者必須有 1 到 {max} 位{KeepOld(existing)}";
    public const string UsersInvalid = "指定的使用者必須存在且啟用中";
    public const string GroupsInvalid = "群組至少要有一個，而且都必須存在";
    public static string ManagerLevel(int max, bool existing = false) => $"主管層級必須是 1 到 {max}{KeepOld(existing)}";
    public const string UserFieldInvalid = "使用者欄位必須是本模組表頭、啟用中的使用者欄位";
    public const string UserFieldWithoutCandidates = "使用者欄位來源必須設定候選群組";
    public const string CandidateGroupsInvalid = "候選群組都必須存在";
    public const string PickCandidatesUnmatchedRemoved = "候選群組在開啟套件時被略過，要移除略過標記，請先選至少一個候選群組；否則任何使用者都可以被選為簽核人";
    public static string PickRange(int max, bool existing = false) => $"自選人數必須符合 1 ≤ 最少 ≤ 最多 ≤ {max}{KeepOld(existing)}";
    public static string DueHours(int max, bool existing = false) => $"逾期時數必須是 1 到 {max}{KeepOld(existing)}";

    /// <summary>只有既有流程（有舊版可比）才附這句；新建流程沒有舊版，附了反而誤導。</summary>
    private static string KeepOld(bool existing) => existing ? "；已存在的流程沿用原本的數字仍可儲存" : "";
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
    public static MessageRef TooManyApprovers(object stage, int max) => MessageRef.Of(MessageText.ApprovalTooManyApprovers, stage, max);
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

    // ── 設計器的進行中實例守衛（§8.6，管理類，不在執行期語系化範圍） ──
    private static string Listing(IReadOnlyList<string> titles, int total) =>
        string.Concat(titles.Select(t => $"「{t}」")) + (total > titles.Count ? $"等 {total} 筆" : "");
    public static string DefinitionBlockedByPending(IReadOnlyList<string> titles, int total) =>
        $"{Listing(titles, total)}正在簽核中，這項修改會改變簽核中單據的內容（欄位值與異動歷程），請等簽核結束或撤回後再改";
    public static string WorkflowBlockedByPending(IReadOnlyList<string> titles, int total) =>
        $"{Listing(titles, total)}正在簽核中，這項修改會拆掉它們要走的轉移或狀態，請等簽核結束或撤回後再改";

    // ── 設計器的鎖定資料守衛（§8.6，管理類，不在執行期語系化範圍） ──
    public static string UnlockBlockedByRecords(int count) =>
        $"有 {count} 筆記錄停在會被解除鎖定的狀態；請先把它們轉回可編輯的狀態，或保留需簽核設定";
    public static string PurgeBlockedByLocked(string fieldLabel, int count) =>
        $"有 {count} 筆停在簽核鎖定狀態的記錄在「{fieldLabel}」有值，刪除欄位會清掉這些值；請先把它們轉回可編輯的狀態";

    public static string FillTargetEditable(string fieldLabel) =>
        $"鎖定帶值不能指向簽核人可編輯的欄位「{fieldLabel}」；請改成預設帶值，或先從流程的可編輯欄位拿掉";

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
    public static MessageRef PickCount(int min, int max) =>
        min == max ? MessageRef.Of(MessageText.ApprovalPickExact, min) : MessageRef.Of(MessageText.ApprovalPickCount, min, max);
    public static readonly MessageRef UserFieldNotCandidate = MessageRef.Of(MessageText.ApprovalUserFieldNotCandidate);
    public static readonly MessageRef UserFieldIsInitiator = MessageRef.Of(MessageText.ApprovalUserFieldIsInitiator);
    public static readonly MessageRef CommentRequired = MessageRef.Of(MessageText.ApprovalCommentRequired);
    public static MessageRef CommentTooLong(int max) => MessageRef.Of(MessageText.ApprovalCommentTooLong, max);
    public static readonly MessageRef TaskNotFound = MessageRef.Of(MessageText.ApprovalTaskNotFound);
    public static readonly MessageRef StageHandled = MessageRef.Of(MessageText.ApprovalStageHandled);
    public static readonly MessageRef StateChanged = MessageRef.Of(MessageText.ApprovalStateChanged);
    public static readonly MessageRef CannotWithdraw = MessageRef.Of(MessageText.ApprovalCannotWithdraw);
    public static readonly MessageRef NoPreviousStage = MessageRef.Of(MessageText.ApprovalNoPreviousStage);
    public static readonly MessageRef NotEditableField = MessageRef.Of(MessageText.ApprovalNotEditableField);
    public static readonly MessageRef FieldHiddenForApprover = MessageRef.Of(MessageText.ApprovalFieldHiddenForApprover);
    public static readonly MessageRef FieldReadOnlyForApprover = MessageRef.Of(MessageText.ApprovalFieldReadOnlyForApprover);
    public static readonly MessageRef RejectTargetInvalid = MessageRef.Of(MessageText.ApprovalRejectTargetInvalid);
    public static readonly MessageRef TargetInactive = MessageRef.Of(MessageText.ApprovalTargetInactive);
    public static readonly MessageRef TargetSelf = MessageRef.Of(MessageText.ApprovalTargetSelf);
    public static readonly MessageRef TargetInitiator = MessageRef.Of(MessageText.ApprovalTargetInitiator);
    public static readonly MessageRef TargetInStage = MessageRef.Of(MessageText.ApprovalTargetInStage);
    public static readonly I18nText AddSignStageName = MessageText.AsI18n(MessageText.ApprovalAddSignStage);
    public static readonly MessageRef AddSignUsersRequired = MessageRef.Of(MessageText.ApprovalAddSignUsersRequired);
    public static MessageRef AddSignTooManyUsers(int max) => MessageRef.Of(MessageText.ApprovalAddSignTooManyUsers, max);
    public static MessageRef AddSignLimit(int max) => MessageRef.Of(MessageText.ApprovalAddSignLimit, max);
    public static readonly MessageRef AddSignModeInvalid = MessageRef.Of(MessageText.ApprovalAddSignModeInvalid);
    public static readonly MessageRef DelegateSelf = MessageRef.Of(MessageText.ApprovalDelegateSelf);
    public static readonly MessageRef DelegateInactive = MessageRef.Of(MessageText.ApprovalDelegateInactive);
    public static readonly MessageRef DelegatePeriod = MessageRef.Of(MessageText.ApprovalDelegatePeriod);
    public static readonly MessageRef DelegateEnded = MessageRef.Of(MessageText.ApprovalDelegateEnded);
    public static readonly MessageRef RuntimeInvalid = MessageRef.Of(MessageText.ApprovalRuntimeInvalid);
    /// <summary>graph 實例的結構再驗不過（送出、同意、加簽、退回上一關）：回 409 並登記異常通知，所以文字說已通知（計畫 fg2 J7、U11）。</summary>
    public static readonly MessageRef GraphRuntimeInvalid = MessageRef.Of(MessageText.ApprovalGraphRuntimeInvalid);
    /// <summary>預覽與設計器試算遇到結構不合法的流程圖：預覽不寫入、不通知，所以只請發起人聯絡管理員（U12）。</summary>
    public static MessageRef GraphPreviewInvalid(object flow) => MessageRef.Of(MessageText.ApprovalGraphPreviewInvalid, flow);
    /// <summary>條件節點的出線無法判斷（引用的欄位或選項已不在）：409 並登記異常通知，不走預設線（流程圖設計 §8.8；J8）。name 是節點名稱（I18nText）或代號。</summary>
    public static MessageRef ConditionNodeUndecidable(object name) => MessageRef.Of(MessageText.ApprovalConditionNodeUndecidable, name);
    /// <summary>graph 處理權在重試次數內拿不到（同一張單同時有太多人動作；流程圖設計 §8.13、J16）。</summary>
    public static readonly MessageRef Busy = MessageRef.Of(MessageText.ApprovalBusy);
    /// <summary>graph 實例層動作（撤回、終止）帶的版本已過期，而單據仍在簽核中：請重新整理（U13 ③）。</summary>
    public static readonly MessageRef InstanceChanged = MessageRef.Of(MessageText.ApprovalInstanceChanged);
    /// <summary>graph 晚到動作：自己的任務因任一到齊被取消（U13 ②）。</summary>
    public static readonly MessageRef BranchCompleted = MessageRef.Of(MessageText.ApprovalBranchCompleted);
    /// <summary>graph 晚到動作：單據已被退回（U13 ①）。</summary>
    public static readonly MessageRef InstanceReturned = MessageRef.Of(MessageText.ApprovalInstanceReturned);
    /// <summary>任一到齊通過、取消其他分支的歷程 comment：固定繁中、只給資料庫與稽核；畫面依 DataJson 組字（U8）。</summary>
    public static string BranchCancelledNote(string name) => $"分支「{name}」已完成，其他分支自動取消";
    public static readonly MessageRef RecordGone = MessageRef.Of(MessageText.ApprovalRecordGone);
    public static readonly MessageRef NoWorkflow = MessageRef.Of(MessageText.ApprovalNoWorkflow);
    public static readonly MessageRef FlowVersionMissing = MessageRef.Of(MessageText.ApprovalFlowVersionMissing);
    public static readonly MessageRef StageDefMissing = MessageRef.Of(MessageText.ApprovalStageDefMissing);
    public static readonly MessageRef BoxInvalid = MessageRef.Of(MessageText.ApprovalBoxInvalid);
    public static readonly MessageRef StatusInvalid = MessageRef.Of(MessageText.ApprovalStatusInvalid);

    // 設計器的定義驗證訊息（管理類，不在執行期語系化範圍）
    public const string EditableFieldHidden = "可編輯欄位不能是對這一關簽核人（或備援）所屬群組隱藏或限定可見的欄位";
}

