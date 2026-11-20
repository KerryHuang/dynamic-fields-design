# 語系包 i4c Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 後端訊息改成可擴充的形狀。新增 `IMessageCatalog`（singleton），內建層沿用現有的 C# 四語表（`MessageText`、`ReportText`，待決 Q1 通過再加 `AlertMailText`），沿設計 §8.3 的退回鏈逐層找句子；回應的解析點（`ServiceResultExtensions`、WebAPI 的例外處理與 `SessionMiddleware`）改從 DI 取它；直接呼叫 `MessageText.Get`／`ReportText.Get` 的檔逐一改成走它；`AsI18n` 改成對**已安裝**語系各產生一句，`ApprovalMessages` 的 3 個 `static readonly` 改方法；Domain 的 `TaskReportFields` 四語表搬進 `ReportText`，由 Application 組虛擬模組時傳入解析函式。使用者看得到的行為：**Demo 上無變化**（內建四語的每一句與 main 逐字相同）；只有資料表裝了內建四語以外的語系（i4d1 才有安裝入口，現在只有測試能造）時，那個語系的後端訊息從「一律繁中」改成走退回鏈（上層語系 → 系統預設 → 繁中），這是設計 U3 已裁決的行為。

**Architecture:** `IMessageCatalog` 放 `Application/Common/Localization/`，實作 `MessageCatalog` 依賴 `IInstalledLocales`（已安裝清單）與 `CachedSystemPolicy`（預設語系與退回鏈），兩者都是記憶體快照，查一句不碰資料庫。主要入口收 `LocaleContext`（語系＋退回鏈，呼叫端已經從 `RequestLocale` 拿到），和 `I18nText.Resolve(LocaleContext)` 同一個鏈來源。內建層是 `BuiltinServerText`：把三張 C# 表合成一份唯讀字典（鍵加命名空間：`MessageText` 不加、`ReportText` 加 `report.`、`AlertMailText` 加 `alert.`），鍵就是設計 §5.1 語系包 `server` 段的鍵。純函式路徑（`RecordValueProcessor`、`FieldDisplay`、`ApproverResolver`）經它們已有的上下文 record 帶 catalog；Domain 的 `TaskReportFields` 只宣告文字鍵，`Build` 多收一個 `Func<string, I18nText>`。i4d1 的覆寫層插在 `MessageCatalog` 每個語系的內建層前面，本段不預留介面。

**Tech Stack:** .NET 8、MediatR、FluentValidation、xUnit＋FluentAssertions；EF Core（SQLite 研發、PostgreSQL 正式，本段沒有 schema 變更）。不新增套件、不動前端。

**Spec:** `docs/superpowers/specs/2026-11-17-language-packs-design.md`（§1 寫死清單的「後端訊息」「任務報表欄位表」兩項、§3 後端訊息解析點、§5.1 `server` 段的鍵、§8.3 退回鏈、§8.4 與 M3 的「每個已安裝語系」口徑、§9 全部、§14、§15（後端訊息剩餘清單、警示信語系「i4c 再定」）、§17 T10、§18 第 9、11 點、§19 退回鏈與安全兩條、§20 i4c 列、§22）。前置計畫：`docs/superpowers/plans/2026-11-17-language-packs-i4a.md`（已上線 `3d0dd59b`；判斷 6、10、23 與「待決」第二條）、`docs/superpowers/plans/2026-11-17-language-packs-i4b.md`（已上線 `7843faa9`；判斷 5–7、16）。repo 規範：`AGENTS.md`、`.claude/rules/{backend,docs}.md`、`CONTRIBUTING.md`、`docs/ai/testing.md`。

**路徑慣例：** 後端相對 `backend/`（`src/…` 指 `backend/src/…`），文件與 `shared/` 相對 repo 根目錄。行號以 main `b138df50` 為準；不符時以符號為準。`Application/…` 是 `backend/src/DynamicModules.Application/…` 的縮寫，`WebAPI/…`、`Domain/…` 同理。

---

## 前置條件（開工前逐項確認，不符就停下回報）

1. **i4a、i4b 已在 main**：`git log --oneline main | grep -E "3d0dd59b|7843faa9"` 兩筆都有；`Application/Common/LocaleRegistry.cs`、`Application/Common/RequestLocale.cs`、`Domain/ValueObjects/LocaleContext.cs`、`Application/Common/Localization/BuiltinLocaleResources.cs` 都在。
2. **i4c 沒開工**：`grep -rn "IMessageCatalog\|BuiltinServerText" backend/src backend/tests` 無結果。
3. **直接呼叫點數量**：`grep -rln "MessageText\.Get(\|ReportText\.Get(" backend/src | wc -l` 是 22、`grep -rn "MessageText\.Get(\|ReportText\.Get(" backend/src | wc -l` 是 69（設計 §3、§9.1 寫「20 個檔」，是 2026-11-17 的數字；不符時以當下為準，記進交付說明）。
4. **表的句數**：`MessageText` 191 句、`ReportText` 37 句、`AlertMailText` 主表＋代碼名稱共約 74 句加 7 個健康檢查名稱（設計 §1 寫 137、31，是 2026-11-17 的數字）。
5. **沒有其他段在改 `MessageText.cs`、`ReportText.cs`、`ApprovalService/`、`ReportService/`**（i3 若同時開工會動報表與簽核的名稱解析，先協調誰先合）。
6. **本段沒有 migration**，可以和其他段平行，但不和會動上面那些檔的段平行。
7. **PostgreSQL 測試環境可用**（`docker info` 成功或設了 `FORMWRIGHT_TEST_PG`）。
8. **plugin 更新**（AGENTS.md「票」）：`claude plugin marketplace update plugharbor && claude plugin update tickets@plugharbor --scope project`，再 `/reload-plugins`；失敗就照舊版跑並記進回報。
9. **待決 Q1、Q2 已有主控裁決**（見「待決」節）。沒有裁決時照推薦做；兩題都是 i4c-2 的獨立 Task（7、8），不推薦的選項＝直接略過那個 Task、把它列進剩餘清單。

## 子段切法（兩個實作者交棒點）

| 批 | 內容 | Task | 手寫行數（估） | migration |
|---|---|---|---|---|
| i4c-1 | catalog 本體與內建層、`ReportText` 鍵加前綴、回應解析點與 WebAPI 例外訊息、`AsI18n` 改已安裝語系與簽核的 3 個 `static readonly`、`TaskReportFields` 搬表 | 1–4 | ≈ 1100（測試約 50%） | 無 |
| i4c-2 | 直接呼叫點：記錄寫入路徑、報表與顯示路徑、警示信（Q1）、匯入是否欄位（Q2）、剩餘清單與文件回填、gate | 5–9 | ≈ 1100（測試約 45%） | 無 |

同一分支 `i4c`（worktree `../formwright-i4c`），每個 Task 各自 commit。i4c-1 收尾跑一次窄範圍 PostgreSQL 雙跑（不跑 gate），交棒給 i4c-2；i4c-2 收尾才跑 gate、合併。

**交棒條件（i4c-1 → i4c-2）**：Task 1–4 驗收全過；`IMessageCatalog` 的介面（判斷 1、2）、`BuiltinServerText` 的鍵命名空間（判斷 3）、各上下文 record 怎麼帶 catalog（判斷 7）寫進交棒說明。i4c-1 單獨存在時使用者行為與 main 相同（內建四語逐字不變），還沒改到的直接呼叫點照舊用靜態 `Get`。分界理由：i4c-1 是形狀與解析點（每個請求都經過），i4c-2 是逐檔替換（量大但機械），分開審比較容易看出解析點有沒有改壞。

---

## 計畫期間的判斷

〔行為〕＝影響使用者看到或能做的事；〔技術〕＝實作選擇。已拍板的不再問。

1. 〔技術〕**主要入口收 `LocaleContext`，不收語系字串**（設計 §9.1 寫的是 `Get(string locale, …)`）。
   - `string Get(LocaleContext lc, string key, params object[] args)`：依序試 `[lc.Locale, ..lc.Fallbacks, "zh-TW"]`（去重），每個語系取內建層（i4d1 起先取覆寫層），第一個有的就用；參數照 `string.Format(CultureInfo.InvariantCulture, …)`。
   - 理由：(a) 呼叫端幾乎都已經有 `LocaleContext`（`RequestLocale.Current`／`For(...)`、報表的 `lc`、`RuleContext.Locale`），鏈的組法只有 `RequestLocale`／`CachedSystemPolicy.FallbacksFor` 一個來源，和設計資料的 `I18nText.Resolve(LocaleContext)` 同鏈，設計 §19「介面、後端訊息、設計資料三處同結果」自然成立；(b) 收字串的話 catalog 要自己決定預設語系與鏈，等於多一份組鏈規則。
   - 不另提供 `Get(string, …)` 多載：只有字串的地方（WebAPI 例外處理、`WorkflowEnforcer`、`GetConversionInputs`）一律改從 `RequestLocale` 取 `LocaleContext`，避免日後又有人繞過退回鏈。
   - 鍵不存在＝程式錯誤，照現有 `Table[key]` 丟 `KeyNotFoundException`（不顯示鍵）。內建繁中層完整，鏈一定在繁中結束，不會走到「顯示鍵」。
2. 〔技術〕**`I18nText AsI18n(string key, params object[] args)` 對每個已安裝語系各產生一句**（設計 §9.1、M3 口徑）：語系清單取 `IInstalledLocales.Codes`（含已停用的），每個語系的鏈取 `CachedSystemPolicy.FallbacksFor(code)`，句子＝`Get(new LocaleContext(code, chain), key, args)`。所以 `ja`（上層 `en-US`）拿到的是英文句子、寫進 `ja` 鍵，與 i4b 判斷 16「一次產生多語的地方拿到補齊後的文字」同性質；日後補了日文，已存的標籤不會跟著改（設計 §15 已列）。不採「只產生有自己翻譯的語系、其他留給讀取時退回」：同一份資料在前端（i4b）與後端兩種口徑會讓簽核進度圖與沖銷欄位的行為不一致。
3. 〔技術〕**內建層 `BuiltinServerText`（`Application/Common/Localization/BuiltinServerText.cs`）**：三張表各開一個 `internal static IEnumerable<KeyValuePair<string, (string Zh, string En, string Th, string Vi)>> Entries`，`BuiltinServerText.Table` 合成 `FrozenDictionary`（`StringComparer.Ordinal`），建表時重複鍵丟例外（`BuiltinServerTextTests` 先抓到）。`TryGet(string locale, string key, out string template)` 只認內建四語。`BaseKeys`（排序後的全部鍵）留給 i4d1 的「基準鍵集」（設計 §5.2）。
   - **`ReportText` 的常數值直接帶前綴**：`Yes = "report." + nameof(Yes)`，表與呼叫端照常用常數，catalog 的鍵就是常數值。已查證 `ReportText` 的常數只當文字鍵用（不寫進資料庫、不回給前端當代碼，`grep -rn "ReportText\.[A-Z]" backend/src | grep -v "ReportText.Get("` 全部是 `Fail(...)`、`T(...)`、`IsDisplayed(...)` 的文字鍵參數）。`MessageText` 不加前綴（設計 §5.1：鍵是 `MessageText` 常數名；Domain 的 `FieldErrors` 鍵與它同名）。
   - `AlertMailText`（待決 Q1 通過才做，Task 7）：主表 `alert.<常數名>`、代碼名稱 `alert.label.<代碼>`（例 `alert.label.severity.critical`）、健康檢查名稱 `alert.check.<代碼>`（繁中取自 `HealthCheckCatalog` 的名稱，維持單一來源）。
4. 〔技術〕**`BuiltinMessageCatalog.Instance`（static）只看內建層、已安裝清單固定為內建四語、鏈只用傳入的 `LocaleContext`**。給三種地方用：上下文 record 的 catalog 為 null 時（測試直接 `new` 的 `FieldDisplayContext`、`ResolveContext`）、`ServiceResult.Message`／`Errors`（繁中，給 log 與測試）、單元測試的 `HttpContext` 沒有 `RequestServices` 時。正式路徑一律從 DI 取 `MessageCatalog`，Task 9 的 S4 grep 驗證正式程式沒有用 `BuiltinMessageCatalog.Instance`（除了上述 null 退路）。
5. 〔技術〕**解析點**（設計 §9.1）：
   - `MessageRef` 加 `Resolve(IMessageCatalog messages, LocaleContext lc, bool translateLiteral = false)`：鍵走 `messages.Get`；`I18nText` 參數用 `t.Resolve(lc)`；字面值剛好是無參數句的繁中原文時，用 `MessageText.KeyOfZh(literal)`（新的 internal 查表，取代 `TranslateLiteral` 內部那段）換成鍵再走 catalog。
   - `ServiceResult` 加 `MessageIn(IMessageCatalog, LocaleContext)`、`ErrorsIn(IMessageCatalog, LocaleContext)`。
   - **舊的字串版 `MessageRef.Resolve(string?, …)`、`ServiceResult.MessageIn(string?, …)`／`ErrorsIn(string?, …)` 保留**，內部改呼叫 `BuiltinMessageCatalog.Instance`（傳入的 `fallbacks` 當鏈）。理由：測試裡約 145 處 `.Resolve(null)`、`.Resolve("th")` 比對訊息，全改是一個與本段無關的巨型 diff；內建四語的結果與現在逐字相同（Task 2 有案例鎖住）。
   - `ServiceResultExtensions.ToActionResult` 從 `RequestServices` 取 `RequestLocale` 與 `IMessageCatalog`，兩者都有就走新多載，否則走舊路徑（單元測試的 `HttpContext` 沒有 services，行為同現在）。
6. 〔技術〕**WebAPI 的例外訊息與 middleware**：新增 `WebAPI/Errors/RequestMessages.cs`：`public static string Text(HttpContext ctx, string key, params object[] args)`，從 `ctx.RequestServices` 取 `RequestLocale.Current` 與 `IMessageCatalog`，取不到（services 已釋放、極早期失敗）就用 `BuiltinMessageCatalog.Instance` 加預設繁中。四個例外處理（`ConcurrencyExceptionHandler.cs:15`、`DatabaseBusyExceptionHandler.cs:18`、`DatabaseUnwritableExceptionHandler.cs:31`、`UnhandledExceptionHandler.cs:84`）與 `SessionMiddleware.cs:52,59` 改用它；`SessionMiddleware.InvokeAsync` 的 `ICurrentLocale locale` 參數移除（自己改出來的孤兒要清）。資料庫寫不進、連不上時也能出訊息：`RequestLocale`、`CachedSystemPolicy`、`LocaleRegistry` 都是記憶體快照，不讀資料庫。
7. 〔技術〕**純函式路徑經既有的上下文 record 帶 catalog**，不把它們改成 DI 類別：
   - `RuleContext`（`Application/Dynamic/RecordService/RecordValueProcessor.cs:16`）改成 `RuleContext(DateOnly Today, LocaleContext Locale, IMessageCatalog Messages, LocaleContext? SystemLocale = null)`，**必填**：測試沒有直接 `new` 它（`grep -rn "new RuleContext(" backend/tests` 為 0），正式程式 4 處（`DetailWriter.cs:158`、`RecordCreator.cs:39`、`UpdateRecordHandler.cs:66,70`）一起改。
   - `FieldDisplayContext`（`Application/Dynamic/Display/FieldDisplay.cs:15`）加最後一個選填參數 `IMessageCatalog? Messages = null`（null 用判斷 4 的內建 catalog）：測試有多處直接 `new FieldDisplayContext(locale, offset)`。`CachedSystemPolicy.Display(string locale, IMessageCatalog? messages = null)` 多一個選填參數，正式呼叫端（`GetRecordHistoryHandler.cs:60`、`ExportRecordsHandler.cs:55`、`PrintRecordHandler.cs:50`、`ReportRunner.cs` 的 `RunContext.Display`）都要傳；不讓 `CachedSystemPolicy` 依賴 catalog（catalog 已依賴它，會成環）。
   - `ResolveContext`（`ApproverResolver.cs:25-28`）加最後一個選填參數 `IMessageCatalog? Messages = null`：測試直接建它（`ApproverResolverTests`、`ApprovalRuntimeTextTests`）；正式的 `ContextAsync`（`ApproverResolver.cs:50`）一定傳。
8. 〔技術〕**`ApprovalMessages` 的 `AsI18n` 呼叫點改方法**（設計 §9.1）：`FallbackStageName`、`PickLabelI18n`、`AddSignStageName` 三個 `static readonly`，與已是方法的 `ManagerLabelI18n(int)`，改成 `FallbackStageName(IMessageCatalog m)`、`PickLabelI18n(IMessageCatalog m)`、`AddSignStageName(IMessageCatalog m)`、`ManagerLabelI18n(IMessageCatalog m, int level)`。另有一個設計沒點名的衍生 `static readonly`：`ApprovalViews.FallbackDefinition`（`ApprovalQueryHandlers.cs:419-422`，進度圖用 `FallbackStageName` 組的備援流程定義），改成 `FallbackDefinition(I18nText stageName)` 方法，`ApprovalViews.Instance`（`:366`）與 `ProgressGraph`（`:430`）多收一個 `I18nText fallbackStageName`，由 `GetRecordApprovalHandler`（`:202` 呼叫 `Instance` 的地方，注入 catalog）傳入。`MessageText.AsI18n`（`MessageText.cs:485`）改完後沒有呼叫端，刪除。
9. 〔技術〕**`TaskReportFields`（Domain）只留結構與文字鍵，文字搬進 `ReportText`**（設計 §20 i4c 列「由 Application 組裝時傳入」）：
   - Domain 加巢狀 `public static class TextKeys`，每句一個 `const string`，值就是 catalog 鍵（`"report.TaskFieldTitle"`、`"report.TaskStatusTodo"`、`"report.TaskBlankAssignee"`、`"report.TaskDatasetTasks"`…）。鍵定義只在 Domain 一處，`ReportText.Table` 的列直接引用 `TaskReportFields.TextKeys.X`（Application 可以參照 Domain，反過來不行）。前綴 `report.` 出現在 Domain 是為了「一個鍵一個字串、不在兩層各拼一次」，代價是 Domain 知道語系包的命名空間，可接受。
   - 共 41 句：欄位名 18、狀態 4、來源 3、完成狀況 4、層級 3、事件種類 2、空值字 5、資料來源名 2（設計 §1 寫「約 37 句」）。空值字「所屬模組」「任務規則」的「（無）」繁中相同，仍各留一個鍵（不同語言的「無」可能因名詞而不同，合併省一個鍵不值得）。
   - `Build(string dataset, IReadOnlyList<Department>, IReadOnlyList<Module>, IReadOnlyList<TaskRule>, Func<string, I18nText> text)`：欄位名、固定選項、模組名稱都用 `text(鍵)`；`BlankLabels`、`DatasetNames`（`I18nText` 值）改成 `BlankLabelKeys: IReadOnlyDictionary<long, string>`、`DatasetNameKeys: IReadOnlyDictionary<string, string>`；另加 `AllTextKeys`（測試用，確認每個鍵都在 catalog）。刪掉私有的 `T(zh, en, th, vi)`。
   - Application：`TaskReportSource.CatalogAsync`（`TaskReportSource.cs:46`）注入 catalog、傳 `messages.AsI18n`（虛擬模組的標籤之後照常經 `LabelI18n.Resolve(lc)`，所以要完整的 `I18nText`）；`GetReportDatasetsHandler.cs:14` 與 `ReportRunner.Tasks.cs:70-71` 的空值字直接 `messages.Get(lc, 鍵)`。
   - 「與前端 `status.*` 同字（K2）」的註解搬到 `ReportText` 狀態那四列。
10. 〔技術〕**改到哪些直接呼叫點、哪些留著**（設計允許分段；剩餘清單在 Task 9 寫進設計 §15）。b138df50 的 22 個檔：

    | 檔（呼叫數） | 處置 | Task |
    |---|---|---|
    | `Common/MessageRef.cs`（1） | 加 catalog 多載，舊多載改走內建 catalog（判斷 5） | 2 |
    | `WebAPI/Errors/` 四個例外處理（各 1）、`WebAPI/Security/SessionMiddleware.cs`（2） | `RequestMessages.Text`（判斷 6） | 2 |
    | `ApprovalService/ApprovalMessages.cs`（2 個 `Get`＋4 個 `AsI18n`） | `AsI18n` 四個改方法（判斷 8）；`ManagerLabel(int)`、`PickLabel` 兩個字串版**留著**（只剩測試引用、固定繁中） | 3 |
    | `RecordService/RecordValueProcessor.cs`（4）、`RecordService/DetailWriter.cs`（4）、`WorkflowService/WorkflowEnforcer.cs`（4＋2 個常數）、`ConversionService/.../GetConversionInputs.cs`（1） | 改。四檔一起改：`GetConversionInputs` 拿「必填」那句和驗證結果比字串，兩邊必須同一個來源，否則新語系時比對不到 | 5 |
    | `ReportService/ReportRunner.cs`（10）、`ReportRunner.Output.cs`（18）、`ReportRunner.Sql.cs`（3）、`ReportRunner.Tasks.cs`（5）、`DashboardService/DashboardCardRunner.cs`（2）、`ReportService/Queries/ExportReport/ExportReportHandler.cs`（2）、`ReportService/Queries/GetReportCatalog/GetReportCatalogHandler.cs`（1）、`Display/FieldDisplay.cs`（3） | 改（`ReportRunner` 的 static 輔助函式凡用到文字的改成 instance 方法；`FieldDisplay` 經 `FieldDisplayContext`） | 6 |
    | `ImportService/FieldImportParser.cs`（1） | 待決 Q2：推薦改（是／否文字比對已安裝語系） | 8 |
    | `WorkflowEnforcer.NotAllowedMessage`／`NotPermittedMessage`（`static readonly`，`MessageText.Get(null, …)`） | **留著**：只有 `CrmDesignCaseAppTests` 拿來比對繁中回應（既有，提出不動） | — |
    | `Common/DatabaseBusyException.cs`（1） | **留著**：例外本身的訊息（log 用，固定繁中）；使用者文字由例外處理經 catalog | — |
    | `Common/Behaviors/MessageRuleExtensions.cs`（1） | **留著**：`WithMessage` 是 FluentValidation 內部的繁中訊息，回給使用者的走 `WithState` 的 `MessageRef`（判斷 5） | — |

    `AlertMailText` 不是 `MessageText.Get`／`ReportText.Get` 的呼叫點，但它是第三張四語表，見待決 Q1。
11. 〔技術〕**票 #66 不併入 i4c**。#66 是 11 行設計資料名稱 `Resolve(I18nText.FallbackLocale)`（套件驗證、`AppApprovalFlowCodec`、`MasterMerger`、`TemplateExporter`、`TemplateValidator`、`FieldRules`、`AppResponses`），b138df50 仍是同樣 11 行。理由：(a) 機制不同：它們是 `I18nText` 的名稱解析（設計 §10），不是後端訊息表；(b) 名稱是嵌進**繁中字面值**的管理類訊息（例「鎖定帶值的目的欄位必須先設為唯讀：{名稱}」），只把名稱換成請求語系會變成中英混雜的一句，正解是先把整句搬進 `MessageText`，那是設計 §2 非目標與 §15 的「管理類訊息（#19–#21）」；(c) 要把語系穿過公開靜態 API（`MasterMerger.MergeDefinition` 等），`V8.cs:289` 還要和 `ApprovalConstraints.Validate` 的訊息同語系比對，改動外擴到套件流程。#66 維持 backlog，Task 9 在票上留言「i4c 判斷 11：不併入，待管理類訊息多語時一起做」。
12. 〔技術〕**退回鏈的共用案例**：`shared/locale-chain-cases.json`（Domain 的 `LocaleChainTests`、前端兩個 vitest 已在讀）加讀一份到 `MessageCatalogTests`：每個案例以 `text` 當唯一一層、鏈取 `[locale, ..fallbacks]`，結果要等於 `expected`。為了能用任意層測，鏈的走法抽成 `public static string? MessageCatalog.FirstInChain(IEnumerable<string> chain, Func<string, string?> layer)`，`Get` 也用它。案例檔本身不改。
13. 〔技術〕**沒有新的使用者訊息、沒有新的業務政策、沒有 migration**。i4c 只改「同一句話從哪裡取」。前端不動。

---

## 待決

i4c-1 不需要等這兩題；i4c-2 的 Task 7、8 各對應一題，沒有裁決時照推薦做，不推薦的選項＝略過那個 Task。

- **Q1 警示信的文字要不要也走語系包（之後可以用語系包翻成日文）？** 主控裁決待確認，推薦＝**要**（Task 7）。
  - 背景：維運的告警信、事件標題用第三張四語表 `AlertMailText`（主表與代碼名稱約 74 句，另 7 個健康檢查名稱），不在設計 §5.1 語系包 `server` 段的兩張表裡。i4a 判斷 23 讓告警通道的「信件語系」可以選任何啟用中的語系，但信件只有內建四語，選日文時實際寄出的是退回鏈上第一個有翻譯的語系（上層是英文就寄英文）。i4a 把這題留到 i4c。
  - 推薦：把 `AlertMailText` 併進 `IMessageCatalog`（鍵前綴 `alert.`，判斷 3），信件每一句沿退回鏈取；i4d1 起語系包的 `server` 段就能帶 `alert.*` 句子。
  - 例：日本廠的系統管理員把信件語系設成「日本語」，匯入含 `alert.*` 的日文語系包後，告警信主旨與內文變成日文；沒翻到的句子顯示上層語系（例如英文）。
  - 選錯代價：選「不要」，日文管理員永遠只收得到英文或繁中的告警信，語系包翻不到，違背設計目標 5「新語系不必改 C#」，之後要另開一段再改；選「要」的代價是語系包基準鍵多約 80 句（翻譯量增加），本段多一個 Task（約 300 行），設計 §5.1 要補 `alert.` 命名空間。i4d1 之前看不出差別（裝不了新語系）。
- **Q2 匯入 CSV 時，「是否」欄位要不要接受所有已安裝語系的「是／否」文字？** 主控裁決待確認，推薦＝**要**（Task 8）。
  - 背景：匯出時「是否」欄位用介面語言寫「是／Yes／ใช่／Có」；匯入目前只比對內建四語的寫法（加上 true／false、yes／no、1／0）。設計 §15 原本把「新語系的『はい』認不得」列成之後再做；但 i4c 讓匯出改走 catalog 之後，i4d1 一旦有日文語系包，匯出的「はい」就匯不回去。
  - 推薦：匯入時從 catalog 對每個已安裝語系取 `report.Yes`、`report.No` 的文字（含退回結果）組成兩個集合比對；同一個字同時出現在兩個集合（某份語系包把「是」與「否」翻成同一個字，或和別的語系撞字）時當成無法判斷，回既有的錯誤「請輸入「是」或「否」」。手冊第 622 行補一句「其他已安裝語言匯出的是／否文字也能匯回」。
  - 例：日籍員工用日文介面匯出訂單，「已付款」欄是「はい」，改完數量後原檔匯入，「はい」照樣當成「是」。
  - 選錯代價：選「不要」，用新語系匯出的檔改完不能直接匯回，要手動把是否欄改成 1／0；選「要」的代價是語系包翻譯錯（把「否」翻成「是」）會讓匯入改判，撞字時才擋得下來，同字不同義擋不下。

**留到之後（不在 i4c 問）**：管理類訊息（#19–#21）與 #66 的名稱解析（判斷 11）。

---

## Global Constraints

- **使用者看得到的行為不變**：內建四語的每一句回應、信件、報表文字、簽核名稱與 main 逐字相同（Task 1 的 `Builtin_SameAsStatic`、Task 3 的 `AsI18n_Builtin_SameAsBefore`、Task 4 的快照測試鎖住）。只有資料表裝了新語系時，該語系的後端訊息改走退回鏈（U3）。既有測試的預期值不改，例外只有：
  - `TaskReportFieldsTests`（Domain）改用新的 `Build(…, text)` 與 `BlankLabelKeys`（判斷 9）。
  - `ApprovalAddSignTests.cs:96`、`ApprovalProgressGraphTests.cs:273` 的 `ApprovalMessages.AddSignStageName`／`FallbackStageName` 改成方法呼叫（判斷 8）。
  - `ReportText` 常數值多了 `report.` 前綴：若有測試把常數值當字面字串比（目前沒有，Task 1 Step 4 grep 確認）。
- **鐵則**：後端訊息只有一份內建來源（三張 C# 表，`BuiltinServerText` 只是合起來讀，不是副本）；退回鏈只由 `RequestLocale`／`CachedSystemPolicy.FallbacksFor` 組；Domain 不依賴 Application（`TaskReportFields` 只收函式）；權限與驗證不變。
- **寫死清單**：見下一節，交付說明列出，Task 9 寫進設計 §14。
- **PostgreSQL**：本段沒有 migration、raw SQL、新 repository。用到 `LocaleTestKit`（直接寫 `Dynamic_Locale`）的 API 測試照 i4a 慣例標 `Category=Postgres`，並在 PostgreSQL 跑過一次（`ServerMessageChainApiTests`，以及 Task 7、8 若加的 API 案例）。純記憶體的單元測試不標。
- **後端新的使用者訊息**：無。**前端**：不動。
- **測試只跑窄範圍**；後端與其他吃 CPU 的測試不並行。本機只跑新增／修改的測試類別；完整套件交 CI（i4c-2 收尾的 gate）。
- **git**：逐檔 `git add`；commit 前 `git status`；不用 `--no-verify`、不 force push、不 merge commit；公開 repo 不寫公司字串、個人絕對路徑、內網 IP。
- 段收尾用 `formwright-purpose-check`；上線用 `formwright-ship`。

## 寫死清單（本段新增，逐條標性質）

| 項目 | 性質 | 理由 |
|---|---|---|
| 後端訊息鍵的命名空間：`MessageText` 無前綴、`ReportText` 為 `report.`、`AlertMailText` 為 `alert.`（`alert.label.`、`alert.check.`） | 技術限制 | 語系包 `server` 段的公開契約（設計 §5.1），改了舊語系包會失效 |
| 鍵不存在丟 `KeyNotFoundException` | 技術限制（資料完整性守衛） | 鍵寫錯是程式錯誤；內建繁中層完整，鏈一定在繁中結束 |
| 後端訊息退回鏈最後一步是 zh-TW | 已列（設計 §14「zh-TW 是平台基準語系」） | — |
| `AsI18n` 產生的語系＝已安裝語系（含已停用） | 已裁決的口徑（M3、設計 §9.1） | — |
| 匯入是否欄位：兩個集合撞字時當無法判斷（Q2） | 資料完整性守衛 | 不讓語系包的錯譯悄悄把「否」判成「是」 |
| 既有的 true／false、yes／no、1／0 | 不是新增（既有行為） | — |

沒有新的上限、預設值、天數或業務政策。

## 剩餘清單（i4c 完成後仍不走 `IMessageCatalog` 的地方；Task 9 寫進設計 §15）

1. `MessageRuleExtensions.WithText` 的 `WithMessage(MessageText.Get(null, …))`：FluentValidation 內部的繁中訊息；回給使用者的文字走 state 的 `MessageRef`，已經過 catalog。刻意保留。
2. `DatabaseBusyException.DefaultMessage`：例外本身的訊息，只進 log；使用者看到的由例外處理經 catalog。刻意保留。
3. `WorkflowEnforcer.NotAllowedMessage`／`NotPermittedMessage`、`ApprovalMessages.ManagerLabel(int)`／`PickLabel`：只剩測試引用的繁中字串（既有，提出不動；要清可另開票）。
4. `MessageRef.Resolve(string?)`、`ServiceResult.Message`／`Errors`／`MessageIn(string?)`／`ErrorsIn(string?)`：只認內建四語的路徑，給 log 與測試；正式回應已改走 catalog。
5. 管理類訊息（設計器驗證、套件與樣板的警告等，#19–#21）仍是繁中字面值；#66 的 11 行名稱解析隨它們一起做（判斷 11）。
6. 依待決結果：Q1 選「不要」→ `AlertMailText`（告警信、事件標題）；Q2 選「不要」→ `FieldImportParser` 的是否欄位比對（設計 §15 原有那條保留）。

## 檔案地圖

**共用檔（並行工作要協調；★＝本段會動）**

| 共用檔 | 本段 | 說明 |
|---|---|---|
| ★ 後端訊息表 | Task 1、4、7 | `Application/Common/MessageText.cs`、`Application/Dynamic/ReportService/Data/ReportText.cs`（加 41 句任務報表文字）、`Application/Dynamic/OpsService/Alerts/AlertMailText.cs`（Q1） |
| ★ 後端 DI 註冊 | Task 1 | `Application/DependencyInjection.cs:49-54` 旁加 `AddSingleton<IMessageCatalog, MessageCatalog>()` |
| ★ WebAPI 解析點與 middleware | Task 2 | `WebAPI/Controllers/ServiceResultExtensions.cs`、`WebAPI/Errors/*ExceptionHandler.cs`、`WebAPI/Security/SessionMiddleware.cs` |
| ★ 簽核、報表、記錄寫入 | Task 3–6 | 見各 Task |
| ★ `TaskReportFields`（Domain） | Task 4 | 只改文字來源，欄位代碼與選項 Id 不動（存在報表定義裡） |
| DbContext／migration | 不動 | 沒有 schema 變更 |
| 前端、`shared/` | 不動 | 只讀 `shared/locale-chain-cases.json` |
| `backend/apps/*.app.json` | 不動 | — |

**新檔**：`Application/Common/Localization/{IMessageCatalog,MessageCatalog,BuiltinMessageCatalog,BuiltinServerText}.cs`、`WebAPI/Errors/RequestMessages.cs`；測試 `ApplicationTests/Common/{MessageCatalogTests,BuiltinServerTextTests,ServiceResultCatalogTests}.cs`、`ApplicationTests/Dynamic/Tasks/TaskReportTextTests.cs` 與快照 `ApplicationTests/Dynamic/Tasks/task-report-texts.verified.json`、`WebAPITests/Dynamic/ServerMessageChainApiTests.cs`。

**文件（Task 9）**：設計 §5.1、§9.1、§14、§15、§18 第 9、11 點、§20 補註；`.claude/rules/backend.md`；`AGENTS.md` 架構地圖；`docs/feature.md`；`docs/manual.md`（只在 Q2 照推薦時改一句）；`.agents/` 鏡像（腳本重產）。

---

# i4c-1 形狀與解析點

### Task 1: `IMessageCatalog`、內建層、`ReportText` 鍵加前綴

**Files:**
- Create: `Application/Common/Localization/IMessageCatalog.cs`、`MessageCatalog.cs`、`BuiltinMessageCatalog.cs`、`BuiltinServerText.cs`
- Modify: `Application/Common/MessageText.cs`（`:118` 的 `Table` 旁加 `internal static … Entries`；`:481` 的 `KeyByZh` 旁加 `internal static string? KeyOfZh(string literal)`；類別註解補「鍵是語系包契約，改名要留別名（設計 §5.1）；第一步是 `IMessageCatalog` 的內建層」）
- Modify: `Application/Dynamic/ReportService/Data/ReportText.cs`（`:9-17` 常數值改 `"report." + nameof(X)`；`:19` 加 `Entries`；註解同上，並註明「`report.` 前綴是語系包 `server` 段的命名空間」）
- Modify: `Application/DependencyInjection.cs`（註冊 singleton）
- Test: `ApplicationTests/Common/MessageCatalogTests.cs`、`ApplicationTests/Common/BuiltinServerTextTests.cs`（新）

**Interfaces:**
```csharp
namespace DynamicModules.Application.Common.Localization;

public interface IMessageCatalog
{
    /// <summary>沿 [lc.Locale, ..lc.Fallbacks, zh-TW] 找第一個有的句子（判斷 1）；鍵不存在丟 KeyNotFoundException。</summary>
    string Get(LocaleContext lc, string key, params object[] args);
    /// <summary>對每個已安裝語系各產生一句（判斷 2）。</summary>
    I18nText AsI18n(string key, params object[] args);
}

public sealed class MessageCatalog(IInstalledLocales installed, CachedSystemPolicy policy) : IMessageCatalog
{
    public static string? FirstInChain(IEnumerable<string> chain, Func<string, string?> layer);   // 判斷 12
}

public sealed class BuiltinMessageCatalog : IMessageCatalog { public static BuiltinMessageCatalog Instance { get; } }  // 判斷 4

public static class BuiltinServerText
{
    public static FrozenDictionary<string, (string Zh, string En, string Th, string Vi)> Table { get; }
    public static IReadOnlyList<string> BaseKeys { get; }
    public static bool TryGet(string locale, string key, [NotNullWhen(true)] out string? template);
}
```

- [ ] **Step 1: 寫失敗的測試**
  - `BuiltinServerTextTests`：
    - `Table_ContainsEveryMessageAndReportKey`：筆數＝`MessageText` 191＋`ReportText` 37（以當下表為準，用反射數兩個類別的 `public const string`，不寫死數字）。
    - `ReportKeys_AllPrefixed`：`ReportText` 每個常數值都以 `report.` 開頭；`MessageText` 沒有任何鍵含 `.`。
    - `EveryKey_NonBlankInFourLocales`、`Placeholders_MatchZh_AndFormatDoesNotThrow`（同 `MessageTextTests` 的做法，擴到合併表，含 `report.*`）。
    - `TryGet_OnlyBuiltinLocales`：`TryGet("ja", "Forbidden", out _)` 為 false。
  - `MessageCatalogTests`（純記憶體；registry 與 policy 照 `RequestLocaleTests.Create()` 的建法，裝 `ja` 用 `FakeLocaleRepository`）：
    - `SharedChainCases`（判斷 12）：讀 repo 的 `shared/locale-chain-cases.json`（路徑找法照 `BuiltinLocaleResourcesTests` 讀 repo 檔的方式），每案 `FirstInChain([locale, ..fallbacks], l => text.GetValueOrDefault(l))` 等於 `expected`。
    - `Builtin_SameAsStatic`：出廠（預設 zh-TW）對合併表每個鍵 × 四語，`catalog.Get(RequestLocale 組的 lc, key, 假參數)` 等於現在的 `MessageText.Get`／`ReportText.Get`（證明內建四語逐字不變）。
    - `Ja_ParentThenDefault`：`ja` 上層 `en-US`、預設 `th` → `Get(ja, Forbidden)` 是英文；`ja` 無上層 → 泰文。
    - `Ja_NotInstalled_StillChains`：鏈裡的語系不必已安裝（`Get` 只看 `lc`）。
    - `UnknownKey_Throws`。
    - `AsI18n_InstalledLocales`：出廠 → 四個鍵、值等於四語靜態值；裝 `ja`（上層 `en-US`）並刷新 registry → 五個鍵、`ja` 等於英文；停用 `ja`（政策 `locale.enabled` 不含它）→ 仍有 `ja`（M3：已安裝即產生）。
    - `BuiltinInstance_IgnoresRegistry`：`BuiltinMessageCatalog.Instance.AsI18n(...)` 只有四語；`Get(new LocaleContext("ja", []))` 是繁中。
- [ ] **Step 2: 跑測試確認失敗**：`cd backend && dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~BuiltinServerTextTests|FullyQualifiedName~MessageCatalogTests"`（編譯失敗）
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；另跑 `--filter "FullyQualifiedName~MessageTextTests|FullyQualifiedName~FieldImportParserTests|FullyQualifiedName~FieldDisplayTests"`（`ReportText` 前綴不影響既有比對）；`grep -rn "\"\(Yes\|No\|Blank\|Deleted\|NoPermission\|Title\)\"" backend/tests | grep -i reporttext` 無結果（沒有測試把舊常數值當字面字串用）。
- [ ] **Step 5: Commit**（`feat(i18n): IMessageCatalog 與後端訊息內建層，ReportText 鍵加 report. 命名空間`）

**驗收**：Step 4 全過；`MessageCatalog` 不依賴 `IServiceProvider`、不讀資料庫（建構子只有 `IInstalledLocales`、`CachedSystemPolicy`）。

### Task 2: 回應解析點與 WebAPI 例外訊息改走 catalog

**Files:**
- Modify: `Application/Common/MessageRef.cs:33-41`（加 catalog 多載；舊多載改走 `BuiltinMessageCatalog.Instance`；`TranslateLiteral` 的查表改用 `MessageText.KeyOfZh`）
- Modify: `Application/Common/ServiceResult.cs:20-38`（加 `MessageIn(IMessageCatalog, LocaleContext)`、`ErrorsIn(IMessageCatalog, LocaleContext)`；`Message`／`Errors` 註解改「繁中，只給 log 與測試」）
- Modify: `WebAPI/Controllers/ServiceResultExtensions.cs:13-16,24-25,37`（判斷 5）
- Create: `WebAPI/Errors/RequestMessages.cs`（判斷 6）
- Modify: `WebAPI/Errors/ConcurrencyExceptionHandler.cs:15`、`DatabaseBusyExceptionHandler.cs:18`、`DatabaseUnwritableExceptionHandler.cs:31`、`UnhandledExceptionHandler.cs:84`、`WebAPI/Security/SessionMiddleware.cs:18,52,59`
- Test: `ApplicationTests/Common/ServiceResultCatalogTests.cs`（新）、`WebAPITests/Dynamic/ServerMessageChainApiTests.cs`（新，`Category=Postgres`）

**Interfaces:**
- `public string Resolve(IMessageCatalog messages, LocaleContext lc, bool translateLiteral = false)`（`MessageRef`）
- `public string? MessageIn(IMessageCatalog messages, LocaleContext lc)`、`public IReadOnlyDictionary<string, string[]> ErrorsIn(IMessageCatalog messages, LocaleContext lc)`（`ServiceResult`）
- `internal static string Text(HttpContext ctx, string key, params object[] args)`（`RequestMessages`）

- [ ] **Step 1: 寫失敗的測試**
  - `ServiceResultCatalogTests`（記憶體 catalog，`ja` 上層 `en-US`）：
    - `KeyedMessage_Ja_English`：`ServiceResult.Forbidden(MessageRef.Of(MessageText.Forbidden)).MessageIn(catalog, ja)` 是英文。
    - `Literal_MatchingZh_TranslatedThroughChain`：字面值等於 `Forbidden` 的繁中原文 → `ja` 時英文；其他字面值原樣。
    - `FieldErrors_LiteralNeverTranslated`（同 `ServiceResultMappingTests.Literal_InFieldErrors_IsNeverTranslated` 的意思，走新多載）。
    - `I18nTextArg_UsesSameChain`：`MessageRef.Of(MessageText.ApprovalNoApprover, I18nText{zh, en}, "x")` 在 `ja` 時參數也是英文。
    - `OldOverload_Unchanged`：四語 × 幾個代表鍵，舊 `MessageIn(locale)` 與新多載（出廠鏈）結果相同。
  - `ServerMessageChainApiTests`（`TestApp`＋`LocaleTestKit`；裝 `ja` 上層 `en-US`、啟用、預設 `th`，照 `LocaleChainApiTests.Japanese()` 的造法）：
    - `NotFound_Ja_ParentEnglish`：帶 `X-Locale: ja` 取不存在的模組 → `message` 等於同一請求帶 `X-Locale: en-US` 的 `message`。
    - `NotFound_JaNoParent_DefaultThai`：`ja` 無上層 → 等於 `th` 的。
    - `Validation_Ja_UsesChain`：任一走 `WithText` 的驗證失敗（例：`POST auth/change-password` 新密碼太短）→ `errors` 的文字是英文。
    - `SignInRequired_Ja_English`：未登入、帶 `X-Locale: ja` 取需登入的端點 → 401 的 `message` 是英文（middleware 路徑）。
    - `Stale_Ja_English`：觸發一次並行衝突（照 `ConcurrencyExceptionTests` 的做法）→ 409 的 `message` 是英文（例外處理路徑）。
    - `Factory_FourLocales_Unchanged`：不裝 `ja`，四語各取一次不存在的模組，與 Task 2 改動前的字串相同（直接比 `MessageText.Get(l, ModuleNotFound)`）。
- [ ] **Step 2: 跑測試確認失敗**：`cd backend && dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~ServiceResultCatalogTests"`；`dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~ServerMessageChainApiTests"`
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；另跑 ApplicationTests `--filter "FullyQualifiedName~ServiceResultTests|FullyQualifiedName~ValidationBehaviorTests"`、WebAPITests `--filter "FullyQualifiedName~ServiceResultMappingTests|FullyQualifiedName~ConcurrencyExceptionTests|FullyQualifiedName~SecurityTests|FullyQualifiedName~LocaleChainApiTests|FullyQualifiedName~FileDatabaseModeTests"`；PostgreSQL：`FORMWRIGHT_TEST_DB=postgres dotnet test tests/DynamicModules.WebAPITests --filter "Category=Postgres&FullyQualifiedName~ServerMessageChainApiTests"`
- [ ] **Step 5: Commit**（`feat(i18n): 回應與例外訊息經 IMessageCatalog 沿退回鏈解析`）

**驗收**：Step 4 全過（含 PostgreSQL）；`grep -rn "MessageText.Get(" backend/src/DynamicModules.WebAPI` 無結果；`grep -n "ICurrentLocale" backend/src/DynamicModules.WebAPI/Security/SessionMiddleware.cs` 無結果。

### Task 3: `AsI18n` 改已安裝語系，簽核的 3 個 `static readonly` 改方法

**Files:**
- Modify: `Application/Dynamic/ApprovalService/ApprovalMessages.cs:150-154,210`（判斷 8；`ManagerLabel(int)`、`PickLabel` 不動）
- Modify: `Application/Dynamic/ApprovalService/ApproverResolver.cs:25-28`（`ResolveContext` 加 `Messages`）、`:50-51`（傳 catalog；建構子注入）、`:115,118,138,152,157`（改呼叫 `ApprovalMessages.X(ctx.Messages ?? BuiltinMessageCatalog.Instance)`；取用處寫一個私有 `Messages(ctx)` 輔助）
- Modify: `Application/Dynamic/ApprovalService/ApprovalEngine.cs:128,230,260,273`（注入 catalog）
- Modify: `Application/Dynamic/ApprovalService/ApprovalActionHandlers.cs:259,431`（所在類別注入 catalog）
- Modify: `Application/Dynamic/ApprovalService/ApprovalQueryHandlers.cs:202,366,419-422,430-435`（判斷 8 的 `FallbackDefinition`）
- Modify: `Application/Common/MessageText.cs:484-486`（刪 `AsI18n`）
- Modify（測試預期改呼叫方法）：`ApplicationTests/Dynamic/Approvals/ApprovalAddSignTests.cs:96`、`ApprovalProgressGraphTests.cs:273`
- Test: `ApplicationTests/Common/MessageCatalogTests.cs`（加案例）、`WebAPITests/Dynamic/ServerMessageChainApiTests.cs`（加案例）

**Interfaces:**
- `public static I18nText FallbackStageName(IMessageCatalog m)`、`PickLabelI18n(IMessageCatalog m)`、`AddSignStageName(IMessageCatalog m)`、`ManagerLabelI18n(IMessageCatalog m, int level)`（`ApprovalMessages`）
- `ResolveContext(..., LocaleContext? NoteLocale = null, IMessageCatalog? Messages = null)`
- `private static ApprovalDefinition FallbackDefinition(I18nText stageName)`；`ApprovalViews.Instance(..., I18nText fallbackStageName)`

- [ ] **Step 1: 寫失敗的測試**
  - `MessageCatalogTests.AsI18n_Builtin_SameAsBefore`：出廠時四個簽核鍵（含 `ManagerLabel` 帶 `2`）的 `catalog.AsI18n` 等於 `new I18nText(BuiltinLocales.Codes.Select(l => KeyValuePair.Create(l, MessageText.Get(l, key, args))))`（即刪掉的舊 `MessageText.AsI18n` 的結果）。
  - `MessageCatalogTests.ApprovalLabels_FollowInstalled`：裝 `ja` 後 `ApprovalMessages.FallbackStageName(catalog)` 含 `ja` 鍵；同一個 catalog 在移除 `ja`、registry 刷新後再呼叫就不含（證明不是型別初始化時算一次）。
  - `ServerMessageChainApiTests.FallbackApproval_SnapshotHasInstalledLocales`：裝 `ja`（上層 `en-US`）後送出一張只會走備援的簽核（照 `ApprovalProgressGraphTests` 的備援情境造資料），讀簽核進度：備援關卡名稱在 `X-Locale: ja` 時是英文，資料庫裡 `NameSnapshot` 含 `ja` 鍵；未安裝的 `ko` 不在裡面。
- [ ] **Step 2: 跑測試確認失敗**：`cd backend && dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~MessageCatalogTests"`（編譯失敗：方法還不存在）
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；ApplicationTests `--filter "FullyQualifiedName~Approvals"`；WebAPITests `--filter "FullyQualifiedName~ServerMessageChainApiTests|FullyQualifiedName~Approval"`；PostgreSQL：`FORMWRIGHT_TEST_DB=postgres dotnet test tests/DynamicModules.WebAPITests --filter "Category=Postgres&FullyQualifiedName~ServerMessageChainApiTests"`
- [ ] **Step 5: Commit**（`feat(i18n): AsI18n 改對已安裝語系產生，簽核的固定名稱改成方法`）

**驗收**：Step 4 全過；`grep -rn "static readonly I18nText" backend/src/DynamicModules.Application/Dynamic/ApprovalService` 無結果；`grep -rn "MessageText.AsI18n" backend/src backend/tests` 無結果。

### Task 4: `TaskReportFields` 的四語表搬進 `ReportText`

**Files:**
- Modify: `Domain/Reports/TaskReportFields.cs:42-125,130-160`（判斷 9：加 `TextKeys`、`BlankLabelKeys`、`DatasetNameKeys`、`AllTextKeys`；`Build` 加 `Func<string, I18nText> text`；刪 `T(...)` 與四語字面值）
- Modify: `Application/Dynamic/ReportService/Data/ReportText.cs`（`Table` 加 41 列，鍵引用 `TaskReportFields.TextKeys`，文字逐字搬自 Domain；狀態四列帶 K2 註解）
- Modify: `Application/Dynamic/ReportService/TaskReportSource.cs:20-21,46`（注入 catalog，`Build(..., messages.AsI18n)`）
- Modify: `Application/Dynamic/ReportService/Queries/GetReportCatalog/GetReportDatasetsHandler.cs:10-14`（注入 catalog，`messages.Get(requestLocale.For(...), DatasetNameKeys[d])`）
- Modify: `Application/Dynamic/ReportService/ReportRunner.Tasks.cs:70-71`（空值字 `messages.Get(lc, F.BlankLabelKeys[id])`；`ReportRunner` 建構子注入 catalog，Task 6 沿用）
- Modify: `DomainTests/Reports/TaskReportFieldsTests.cs:12-14,91-95`（改用 `text` 函式與 `BlankLabelKeys`）
- Create: `ApplicationTests/Dynamic/Tasks/TaskReportTextTests.cs`、`ApplicationTests/Dynamic/Tasks/task-report-texts.verified.json`（csproj 設 `CopyToOutputDirectory` 或測試以 repo 相對路徑讀，照 `BuiltinLocaleResourcesTests` 的讀法）

**Interfaces:**
- `public static class TaskReportFields.TextKeys { public const string FieldTitle = "report.TaskFieldTitle", …; }`（41 個）
- `public static IReadOnlyDictionary<long, string> BlankLabelKeys { get; }`、`public static IReadOnlyDictionary<string, string> DatasetNameKeys { get; }`、`public static IReadOnlyList<string> AllTextKeys { get; }`
- `public static Module Build(string dataset, IReadOnlyList<Department> departments, IReadOnlyList<Module> modules, IReadOnlyList<TaskRule> rules, Func<string, I18nText> text)`

- [ ] **Step 0: 先產快照**（搬表前，單獨 commit）：在 `TaskReportTextTests` 寫一個暫時的 `Dump` 測試，用**現行** API 產生兩個資料來源的全部欄位名、固定選項標籤、`BlankLabels`、`DatasetNames`，依（資料來源、欄位代碼、選項 Id、語系）排序序列化成 `task-report-texts.verified.json`；跑一次、人工抽看幾句、刪掉 `Dump` 測試，commit 快照（`test(i18n): 任務報表欄位文字快照（搬表前）`）。
- [ ] **Step 1: 寫失敗的測試**
  - `TaskReportTextTests.BuiltinLabels_MatchSnapshot`：用新 API（`Build(..., BuiltinMessageCatalog.Instance.AsI18n)`、`BlankLabelKeys`／`DatasetNameKeys` 經 catalog 四語各取一次）序列化成同一形狀，必須與快照**逐字相同**。
  - `TaskReportTextTests.EveryTextKey_InCatalog`：`AllTextKeys` 每個都在 `BuiltinServerText.Table`、都以 `report.Task` 開頭、共 41 個。
  - `TaskReportTextTests.Ja_LabelsFollowChain`：記憶體 catalog 裝 `ja`（上層 `en-US`）→ 虛擬模組欄位「狀態」的 `LabelI18n` 含 `ja` 鍵、值是 `Status`。
  - `TaskReportFieldsTests`（Domain）：既有 7 個案例改傳 `k => new I18nText([KeyValuePair.Create("zh-TW", k)])`；`BlankLabels` 那條改成驗 `BlankLabelKeys` 的鍵集合與 `TextKeys.BlankAssignee`。
  - `ServerMessageChainApiTests.TaskReport_Ja_ParentEnglish`：裝 `ja`（上層 `en-US`）→ `X-Locale: ja` 取 `reports/datasets` 與任務資料來源的目錄，資料來源名與欄位名是英文（端點照 `TaskReportsApiTests.cs:66-70` 的 `reports/datasets` 與 `reports/datasets/tasks/catalog`）。
- [ ] **Step 2: 跑測試確認失敗**：`cd backend && dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~TaskReportTextTests"`；`dotnet test tests/DynamicModules.DomainTests --filter "FullyQualifiedName~TaskReportFields"`
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；ApplicationTests `--filter "FullyQualifiedName~TaskReport|FullyQualifiedName~DemoReportsTests|FullyQualifiedName~BuiltinServerTextTests"`；WebAPITests `--filter "FullyQualifiedName~ServerMessageChainApiTests|FullyQualifiedName~TaskReportsApiTests|FullyQualifiedName~ReportCatalogApiTests"`；PostgreSQL：`ServerMessageChainApiTests` 同 Task 2
- [ ] **Step 5: Commit**（`feat(i18n): 任務報表欄位文字搬進 ReportText，Domain 只留鍵`）

**驗收**：Step 4 全過；`grep -n "\"zh-TW\"" backend/src/DynamicModules.Domain/Reports/TaskReportFields.cs` 無結果；欄位代碼、選項 Id 的常數 diff 為零（`git diff b138df50 -- backend/src/DynamicModules.Domain/Reports/TaskReportFields.cs | grep "^[-+].*const long"` 無結果）。

**i4c-1 收尾**：PostgreSQL 窄範圍雙跑：`FORMWRIGHT_TEST_DB=postgres dotnet test tests/DynamicModules.WebAPITests --filter "Category=Postgres&(FullyQualifiedName~ServerMessageChain|FullyQualifiedName~LocaleChainApi|FullyQualifiedName~I18nUi)&Category!=SqliteOnly&Category!=Benchmark"`；`formwright-purpose-check` 對 Task 1–4 跑一次；寫交棒說明（上面「交棒條件」），不推分支、不跑 gate。

---

# i4c-2 直接呼叫點

### Task 5: 記錄寫入路徑（欄位值、明細、狀態轉移、轉單必填比對）

**Files:**
- Modify: `Application/Dynamic/RecordService/RecordValueProcessor.cs:16,59,91,108,119`（`RuleContext` 加必填 `Messages`，判斷 7；四處改 `rules.Messages.Get(rules.Locale, …)`）
- Modify: `Application/Dynamic/RecordService/DetailWriter.cs:43-46,66,96,126,135,158`（注入 catalog）
- Modify: `Application/Dynamic/RecordService/RecordCreator.cs:39`、`Application/Dynamic/RecordService/Commands/UpdateRecord/UpdateRecordHandler.cs:66,70`（`RuleContext` 傳 catalog；所在類別注入）
- Modify: `Application/Dynamic/WorkflowService/WorkflowEnforcer.cs:13-14,73,82,88,95`（`ICurrentLocale` 換成 `RequestLocale`、注入 catalog；`:17-18` 兩個常數不動，判斷 10）
- Modify: `Application/Dynamic/ConversionService/Queries/GetConversionInputs/GetConversionInputs.cs:20-22,52`（`ICurrentLocale` 換 `RequestLocale`，`messages.Get(locale.Current, MessageText.Required)`）
- Test: `WebAPITests/Dynamic/ServerMessageChainApiTests.cs`（加案例）

- [ ] **Step 1: 寫失敗的測試**（都在 `ServerMessageChainApiTests`，`ja` 上層 `en-US`、預設 `th`）
  - `RecordRequired_Ja_English`：新增記錄缺必填欄位 → `errors[欄位鍵]` 是英文的 `Required`。
  - `FieldValue_Ja_English`：數字欄填文字 → 英文的 `MustBeNumber`（Domain `FieldErrors` 鍵經 catalog）。
  - `DetailTooMany_Ja_English`：明細超過上限 → 英文（帶上限數字）。
  - `WorkflowNotAllowed_Ja_English`：不存在的狀態轉移 → 英文。
  - `ConversionInputs_Ja_StillDetectsRequired`：`ja` 使用者取轉單輸入（照 `ConversionApiTests` 造一個目標有必填欄位的轉單）→ 回傳的必填鍵與 `zh-TW` 使用者相同（證明比對與產生同一來源）。
- [ ] **Step 2: 跑測試確認失敗**：`cd backend && dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~ServerMessageChainApiTests"`
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；ApplicationTests `--filter "FullyQualifiedName~RecordValueProcessor|FullyQualifiedName~DetailWriter|FullyQualifiedName~Workflow|FullyQualifiedName~Conversion"`；WebAPITests `--filter "FullyQualifiedName~ConversionApiTests|FullyQualifiedName~CrmDesignCaseAppTests|FullyQualifiedName~RuleWriteTests"`；PostgreSQL：`ServerMessageChainApiTests`
- [ ] **Step 5: Commit**（`feat(i18n): 記錄寫入與狀態轉移的訊息改走 IMessageCatalog`）

**驗收**：Step 4 全過；`grep -rn "MessageText.Get(" backend/src/DynamicModules.Application/Dynamic/{RecordService,ConversionService}` 無結果，`WorkflowEnforcer.cs` 只剩 `:17-18` 兩行。

### Task 6: 報表、儀表板、欄位值顯示（列印、匯出、歷程）

**Files:**
- Modify: `Application/Dynamic/ReportService/ReportRunner.cs:113,123,287,367,376,385-386,389,394`、`ReportRunner.Output.cs:31-38,51-79,162-166,217-222`、`ReportRunner.Sql.cs:346,377,444`、`ReportRunner.Tasks.cs:26,37,55,63,96`（`ReportText.Get(lc.Locale, …)` → `messages.Get(lc, …)`；用到文字的 `private static` 輔助函式改成 instance 方法，其餘 static 不動；`RunContext.Display` 傳 catalog）
- Modify: `Application/Dynamic/DashboardService/DashboardCardRunner.cs:14-16,22,33,51,60`（`locale` 變數改成 `LocaleContext`）
- Modify: `Application/Dynamic/ReportService/Queries/ExportReport/ExportReportHandler.cs:17-18,41,53`、`Application/Dynamic/ReportService/Queries/GetReportCatalog/GetReportCatalogHandler.cs:31,113`
- Modify: `Application/Dynamic/Display/FieldDisplay.cs:15,67-70,89-90`（`FieldDisplayContext` 加 `Messages`；`Boolean`、`ReferenceText` 改收 `FieldDisplayContext`）
- Modify: `Application/Dynamic/PolicyService/CachedSystemPolicy.cs:111`（`Display(string locale, IMessageCatalog? messages = null)`）
- Modify: `Application/Dynamic/RecordService/Queries/GetRecordHistory/GetRecordHistoryHandler.cs:60`、`Application/Dynamic/ExportService/Queries/ExportRecords/ExportRecordsHandler.cs:55`、`Application/Dynamic/PrintService/PrintRecordHandler.cs:50`（注入 catalog 並傳入）
- Modify: `Application/Dynamic/ReportService/Data/ReportText.cs:68-74`（`Get` 沒有呼叫端時刪除；Q2 不做時保留給 `FieldImportParser`）
- Test: `ApplicationTests/Dynamic/FieldDisplayTests.cs`（加案例）、`WebAPITests/Dynamic/ServerMessageChainApiTests.cs`（加案例）

- [ ] **Step 1: 寫失敗的測試**
  - `FieldDisplayTests.Boolean_Ja_FollowsChain`：`new FieldDisplayContext("ja", Taipei, ["en-US", "zh-TW"], catalog)` → `Yes`；不帶 catalog（null）→ 照現在的內建行為（`ja` 不認得、退回繁中「是」）。`Reference_Deleted_Ja_FollowsChain` 同理。
  - `ServerMessageChainApiTests`（`ja` 上層 `en-US`、預設 `th`）：
    - `ReportRun_Ja_EnglishLabels`：跑一張含「筆數」量測與分組空值的報表 → 欄名（`Count`）、空值字（`(Blank)`）是英文；`rowMeaning` 是英文。
    - `ReportExport_Ja_TotalRowEnglish`：報表匯出 CSV 的總計列是 `Total`、去重說明是英文。
    - `DashboardCard_Ja_UnsharedEnglish`：卡片引用的報表已不分享 → 卡片訊息英文。
    - `RecordExport_Ja_BooleanEnglish`：記錄匯出 CSV，是否欄是 `Yes`／`No`（`FieldDisplay` 經 catalog）。
    - `Print_Ja_BooleanEnglish`：列印的是否欄是 `Yes`，且輸出照常經 HTML 編碼（設計 §19 安全那條的前置；含 `<script>` 的覆寫要等 i4d1 才造得出來，本段只驗「列印文字來自 catalog」）。
- [ ] **Step 2: 跑測試確認失敗**：`cd backend && dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~FieldDisplayTests"`；`dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~ServerMessageChainApiTests"`
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；ApplicationTests `--filter "FullyQualifiedName~Report|FullyQualifiedName~Dashboard|FullyQualifiedName~FieldDisplay|FullyQualifiedName~Print|FullyQualifiedName~Export|FullyQualifiedName~RecordHistory|FullyQualifiedName~CachedSystemPolicyTests"`；WebAPITests `--filter "FullyQualifiedName~ReportsApiTests|FullyQualifiedName~ReportCatalogApiTests|FullyQualifiedName~Dashboard|FullyQualifiedName~LocaleChainApiTests|FullyQualifiedName~ServerMessageChainApiTests"`；PostgreSQL：`FORMWRIGHT_TEST_DB=postgres dotnet test tests/DynamicModules.WebAPITests --filter "Category=Postgres&(FullyQualifiedName~ServerMessageChain|FullyQualifiedName~ReportsApi|FullyQualifiedName~Dashboard)&Category!=SqliteOnly&Category!=Benchmark"`
- [ ] **Step 5: Commit**（`feat(i18n): 報表、儀表板與欄位值顯示的系統文字改走 IMessageCatalog`）

**驗收**：Step 4 全過；`grep -rn "ReportText.Get(" backend/src` 只剩 `FieldImportParser.cs`（Q2 不做時）或無結果；`grep -rn "\.Display(" backend/src | grep -v "messages"` 無結果（正式呼叫端都傳 catalog）。

### Task 7（待決 Q1 照推薦才做）: 警示信與事件標題改走 catalog

**Files:**
- Modify: `Application/Dynamic/OpsService/Alerts/AlertMailText.cs`（`:31` 主表、`:89` `Labels`、`:132` `CheckNames` 開 `Entries`，鍵照判斷 3；`Locale(LocaleContext)`（`:147`）、`Get`（`:150`）、`Label`（`:157`）、`CheckName`（`:159`）改成收 `IMessageCatalog` 與 `LocaleContext`；`Locales`（`:15`）只有 `Locale(...)`（`:148`）用，隨它一起刪）
- Modify: `Application/Common/Localization/BuiltinServerText.cs`（合併 `alert.*`）
- Modify: `Application/Dynamic/OpsService/Alerts/AlertMailComposer.cs`（四個 static 方法多收 `IMessageCatalog`）、`Application/Dynamic/OpsService/Incidents/IncidentTitles.cs:61-79`（`Snapshot`、`Text` 同）
- Modify（呼叫端注入 catalog）：`AlertDelivery.cs:30,79,82`、`AlertOutboxWriter.cs:53`、`AlertChannelHandlers.cs:141`、`DirectAlertQueue.cs:164`、`OpsNotifier.cs:41`、`Application/Dynamic/MailService/MailSettingsHandlers.cs:140`
- Test: `ApplicationTests/Common/BuiltinServerTextTests.cs`（加案例）、`ApplicationTests/Dynamic/Ops/AlertMailTextTests.cs`（新，純函式：composer 與標題直接帶記憶體 catalog）、`WebAPITests/Ops/{AlertChannelTests,IncidentTests}.cs`（調整 `AlertMailText`／`IncidentTitles` 的呼叫；`AlertKit`、`OpsIncidentKit` 若有引用一併改）

- [ ] **Step 1: 寫失敗的測試**
  - `BuiltinServerTextTests.AlertKeys_Prefixed`：`alert.` 鍵數＝主表＋代碼名稱＋健康檢查名稱；`alert.check.<代碼>` 的繁中等於 `HealthCheckCatalog` 的名稱。
  - `AlertMail_Builtin_SameAsBefore`：四語 × 每種信（事件、直寄、摘要、兩種測試信）的主旨與內文，與改動前逐字相同（改動前先用現行 API 產生一份快照，做法同 Task 4 Step 0，快照檔 `alert-mail.verified.json`）。
  - `AlertMail_Ja_ParentEnglish`：信件語系 `ja`（上層 `en-US`）→ 主旨、內文、代碼名稱、健康檢查名稱都是英文（行為同現在的「挑第一個有翻譯的」，但改成逐句走鏈）。
  - `IncidentSnapshot_UsesSystemDefault`：`IncidentTitles.Snapshot` 仍用系統預設語系（N1），出廠是繁中、逐字同 o2b。
- [ ] **Step 2: 跑測試確認失敗**：`cd backend && dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~BuiltinServerTextTests|FullyQualifiedName~AlertMailTextTests"`
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；WebAPITests `--filter "FullyQualifiedName~Ops"`（含 `AlertChannelTests`、`IncidentTests`、`RulePauseTests`）
- [ ] **Step 5: Commit**（`feat(i18n): 告警信與事件標題的文字併入 IMessageCatalog（alert. 命名空間）`）

**驗收**：Step 4 全過；看門狗直寄路徑（`DirectAlertQueue`）不因 catalog 讀資料庫（catalog 只讀記憶體快照，判斷 1）。

### Task 8（待決 Q2 照推薦才做）: 匯入「是否」欄位接受已安裝語系的是／否文字

**Files:**
- Modify: `Application/Dynamic/ImportService/FieldImportParser.cs:19-22`（`ImportLookups` 加 `BooleanWords Words`）、`:72-83`（`Boolean` 改用 `lookups.Words`；`IsDisplayed` 刪除）
- Modify: `Application/Dynamic/ImportService/ImportPlanner.cs:90`（注入 catalog，對 `IInstalledLocales.Codes` 每個語系取 `report.Yes`／`report.No`，組 `BooleanWords`）
- Modify: `ApplicationTests/Dynamic/FieldImportParserTests.cs:138,153,179`（`new ImportLookups(...)` 補參數，用 `BooleanWords.Builtin`）
- Test: `ApplicationTests/Dynamic/FieldImportParserTests.cs`（加案例）、`WebAPITests/Dynamic/ServerMessageChainApiTests.cs`（加案例）

**Interfaces:**
- `public sealed record BooleanWords(IReadOnlySet<string> Yes, IReadOnlySet<string> No) { public static BooleanWords From(IEnumerable<(string Yes, string No)> perLocale); public static BooleanWords Builtin { get; } }`：集合不分大小寫（`StringComparer.OrdinalIgnoreCase`）；`From` 把同時出現在兩邊的字從兩邊都移除（寫死清單的撞字守衛）。

- [ ] **Step 1: 寫失敗的測試**
  - `FieldImportParserTests.Boolean_InstalledLocaleWords`：`BooleanWords.From` 加一組 `("はい","いいえ")` → 「はい」是 true、「いいえ」是 false；內建四語的字照常。
  - `Boolean_AmbiguousWord_Rejected`：兩組裡同一個字一個當 Yes、一個當 No → 回「請輸入「是」或「否」」。
  - `Boolean_FixedWordsStillAccepted`：true／false、yes／no、1／0、是／否照常（不受 `Words` 影響）。
  - `ServerMessageChainApiTests.Import_Ja_RoundTrip`：裝 `ja`（上層 `en-US`）→ `ja` 使用者匯出含是否欄的記錄 CSV、原檔匯入，筆數與值不變。
- [ ] **Step 2: 跑測試確認失敗**：`cd backend && dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~FieldImportParserTests"`
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；ApplicationTests `--filter "FullyQualifiedName~Import"`；WebAPITests `--filter "FullyQualifiedName~Import|FullyQualifiedName~ServerMessageChainApiTests"`；PostgreSQL：`ServerMessageChainApiTests`
- [ ] **Step 5: Commit**（`feat(i18n): 匯入是否欄位接受各已安裝語系的是／否文字`）

**驗收**：Step 4 全過；`grep -rn "ReportText.Get(" backend/src` 無結果，`ReportText.Get` 已刪。

### Task 9: 剩餘清單、文件回填、gate

**Files:**
- Modify: `docs/superpowers/specs/2026-11-17-language-packs-design.md`：
  - §5.1 `server` 段：Q1 照推薦時補「`AlertMailText` 的鍵加 `alert.` 前綴（代碼名稱 `alert.label.`、健康檢查名稱 `alert.check.`）」。
  - §9.1：補一行「實作：`Get` 收 `LocaleContext`（i4c 計畫判斷 1）；`AsI18n` 的語系取已安裝清單；`ApprovalViews.FallbackDefinition` 一併改方法（判斷 8）」。
  - §14：本計畫「寫死清單」的新項目。
  - §15：「後端訊息第一步只有改過的呼叫點…（i4c 收尾時列出剩餘清單）」那條改成本計畫「剩餘清單」；「警示信語言…（i4c 再定）」依 Q1 結果改寫；`FieldImportParser` 那條依 Q2 結果保留或刪除；加「i4c 交付時的已知限制」（下面「已知限制」節）。
  - §18 第 9 點標「（i4c 完成）」；第 11 點補「i4c 完成：`backend.md`、`AGENTS.md`、`MessageText`／`ReportText` 註解」。
  - §20：列表加「**i4c 計畫**：[i4c 計畫](../plans/2026-11-17-language-packs-i4c.md)」（若規劃者已在同一 commit 加過就只補「已上線」）。
- Modify: `.claude/rules/backend.md:14`：「四語字串表在 `Application/Common/MessageText.cs`」改成「後端訊息經 `IMessageCatalog`（`Application/Common/Localization/`）沿退回鏈解析；內建層是 `MessageText`、`ReportText`（鍵 `report.`）、`AlertMailText`（鍵 `alert.`）的 C# 四語表，鍵是語系包契約、改名要留別名；新程式不直接呼叫靜態 `Get`」。
- Modify: `AGENTS.md` 架構地圖 `Application` 那條補「後端訊息走 `Common/Localization/IMessageCatalog`」。
- Modify: `docs/feature.md:294`：補「i4c 已實作（後端訊息改走 `IMessageCatalog`）」，**不改核取狀態**（主控收尾時處理）；跑 lifecycle 的 `feature_checklist.py docs/feature.md --write`（找不到腳本就照 `.claude/rules/docs.md` 回報「統計行待重算」）。
- Modify: `docs/manual.md:622`（只在 Q2 照推薦時）：補「其他已安裝語言匯出的是／否文字也能匯回」。其餘手冊不改：內建四語行為不變，新語系的安裝入口在 i4d1。
- 重產鏡像：`node .claude/scripts/sync-codex-mirror.mjs`，`.agents/rules/backend.md` 一起 commit。
- 票：#66 留言（判斷 11）；本段審查與實測發現的新待辦用 `tickets:ticket-create` 開 backlog 票。

- [ ] **Step 1: 整段驗收 grep**（下面 S1–S5）全符合。
- [ ] **Step 2: 文件回填**（上列）。
- [ ] **Step 3: 本機走查**（`scripts/unattended/local-demo.sh`）：繁中、English、ไทย、Tiếng Việt 各觸發一次 404 訊息、一次欄位驗證錯誤、跑一張報表、匯出一次 CSV，文字與 main 相同；沒有畫面改動，不做四種寬度。
- [ ] **Step 4: gate**：推分支前外洩掃描；`bash scripts/unattended/ci-wait.sh <sha>`（含 PostgreSQL 兩步），`exit=0`。
- [ ] **Step 5: `formwright-purpose-check`**（重點：單一來源（三張 C# 表是唯一內建來源、鏈只由 `RequestLocale` 組）、Domain 不依賴 Application、空系統（沒開應用程式時訊息照常）、沒有新寫死的業務政策）。
- [ ] **Step 6: Commit**（`docs(i18n): i4c 完成，剩餘清單、設計、規則與地圖補註`），之後照 `formwright-ship` fast-forward 推 main。

**驗收**：S1–S5 全符合；走查逐項記錄；gate `exit=0`；宗旨檢查無違反。Demo 資料：**無可示範**（內建四語行為不變，新語系要等 i4d1 才裝得到），交付說明寫明。

---

## 整段驗收

設計 §1 寫死清單中屬於 i4c 的兩項：

| # | 項目 | 處置 | 驗證 |
|---|---|---|---|
| 1 | 後端訊息 `MessageText`、`ReportText`（四語 C# 欄位） | 保留為內建層（T10），新語系經 `IMessageCatalog` 走退回鏈（Task 1、2） | `MessageCatalogTests`、`ServerMessageChainApiTests` |
| 2 | 任務報表欄位表 `TaskReportFields`（Domain 四語） | 文字搬進 `ReportText`，Domain 只留鍵（Task 4） | `TaskReportTextTests`、S3 |
| 3 | `AsI18n` 的 3 個 `static readonly`（設計 §9.1） | 改方法、對已安裝語系（Task 3） | S2 |
| 4 | 直接呼叫點（設計 §9.1、§15） | 判斷 10 的表；剩餘清單寫進 §15（Task 5–9） | S1 |

```bash
# S1 直接呼叫只剩剩餘清單那幾處（repo 根目錄）
grep -rn "MessageText\.Get(\|ReportText\.Get(" backend/src
#   預期只剩：MessageText.cs 本身、MessageRef.cs（舊多載經內建 catalog 時若仍引用）、MessageRuleExtensions.cs:10、
#   DatabaseBusyException.cs:9、WorkflowEnforcer.cs:17-18、ApprovalMessages.cs 的 ManagerLabel／PickLabel 兩行；Q2 不做時加 FieldImportParser.cs
# S2 簽核沒有型別初始化時算好的多語名稱
grep -rn "static readonly I18nText" backend/src/DynamicModules.Application/Dynamic/ApprovalService   # 無結果
grep -rn "MessageText.AsI18n" backend/src backend/tests                                               # 無結果
# S3 Domain 沒有四語字面值
grep -rn "\"en-US\"\|\"th\"\|\"vi\"" backend/src/DynamicModules.Domain/Reports                       # 無結果
# S4 正式程式不直接用內建 catalog（只允許 null 退路與 ServiceResult 的繁中屬性）
grep -rn "BuiltinMessageCatalog.Instance" backend/src | grep -v "?? BuiltinMessageCatalog.Instance\|Localization/BuiltinMessageCatalog.cs\|ServiceResult.cs\|MessageRef.cs\|RequestMessages.cs"   # 無結果
# S5 #66 的 11 行原樣（判斷 11，不併入）
grep -rn "Resolve(I18nText.FallbackLocale)" backend/src | wc -l                                       # 11
```

## 預估規模

| 批 | Task | 新檔 | 改檔 | 手寫行數（估，含測試） |
|---|---|---|---|---|
| i4c-1 | 1 | 6（4 正式、2 測試） | 3 | ≈ 330（測試約 180） |
| | 2 | 3 | 8 | ≈ 280（測試約 160） |
| | 3 | 0 | 9 | ≈ 200（測試約 90） |
| | 4 | 2（含快照） | 6 | ≈ 300（測試約 110；另有約 90 行逐字搬移的文字列、快照 JSON 不計） |
| | 小計 | 11 | 26 | ≈ 1110 |
| i4c-2 | 5 | 0 | 6 | ≈ 170（測試約 90） |
| | 6 | 0 | 13 | ≈ 400（測試約 150） |
| | 7（Q1） | 1（快照） | 12 | ≈ 320（測試約 130） |
| | 8（Q2） | 0 | 3 | ≈ 130（測試約 70） |
| | 9 | 0 | 7 | ≈ 80 |
| | 小計 | 1 | 41 | ≈ 1100 |
| 合計 | | 12 | 約 67 | ≈ 2210，其中測試約 45% |

兩批都超過單批約 800 行的指引，主因是測試（每條改過的路徑都要一個 `ja` 走鏈的 API 案例，加上內建四語逐字不變的守衛）。Task 6 若超過 500 行就拆兩個 commit（`ReportRunner*`／其餘）。Q1、Q2 若裁決為「不要」，i4c-2 約 650 行。

## 已知限制（i4c，交付時寫回設計 §15）

- 本段之後仍只有內建層：新語系的後端訊息全部是退回鏈上的內建語系（上層或預設語系），要等 i4d1 匯入語系包才有自己的句子。
- `AsI18n` 寫進資料的名稱（簽核備援關卡、加簽關卡、主管第 N 層、發起人自選），新語系存的是當時沿鏈補齊的句子；之後補了翻譯，已存的不會跟著改（判斷 2，設計 §15 已列的同類限制）。
- 剩餘清單第 3、4 項的繁中字串只給 log 與測試，使用者看不到；第 5 項（管理類訊息、#66）新語系的使用者仍看到繁中。

## 自我檢查

1. **設計覆蓋（§20 i4c 列）**：`IMessageCatalog` → Task 1；解析點改 DI → Task 2；直接呼叫點改注入並列剩餘清單 → Task 5–9、判斷 10；`TaskReportFields` 走 catalog（Domain 由 Application 傳入）→ Task 4、判斷 9；`AsI18n` 改已安裝語系、3 個 `static readonly` 改方法 → Task 3、判斷 8；§15 警示信「i4c 再定」→ 待決 Q1、Task 7；§18 第 11 點 → Task 9。
2. **§19**：退回鏈「後端訊息與設計資料同一組案例」→ Task 1 `SharedChainCases`；安全那條（覆寫含 `<script>` 的 `report.Yes`）要覆寫層，屬 i4d1，本段 Task 6 先確認列印、匯出的文字來自 catalog。
3. **型別一致**：`IMessageCatalog.{Get,AsI18n}`、`MessageCatalog.FirstInChain`、`BuiltinMessageCatalog.Instance`、`BuiltinServerText.{Table,BaseKeys,TryGet}`、`MessageText.{Entries,KeyOfZh}`、`ReportText.Entries`、`MessageRef.Resolve(IMessageCatalog, LocaleContext, bool)`、`ServiceResult.{MessageIn,ErrorsIn}(IMessageCatalog, LocaleContext)`、`RequestMessages.Text`、`RuleContext.Messages`、`FieldDisplayContext.Messages`、`ResolveContext.Messages`、`CachedSystemPolicy.Display(string, IMessageCatalog?)`、`ApprovalMessages.{FallbackStageName,PickLabelI18n,AddSignStageName,ManagerLabelI18n}(IMessageCatalog…)`、`ApprovalViews.FallbackDefinition(I18nText)`、`TaskReportFields.{TextKeys,BlankLabelKeys,DatasetNameKeys,AllTextKeys,Build(…, Func<string, I18nText>)}`、`BooleanWords`。
4. **待決**：Q1（警示信走語系包）、Q2（匯入是否欄位接受已安裝語系），都照推薦寫成獨立 Task，不推薦的選項＝略過該 Task。

## 使用者裁決（沿用，不再問）

- 2026-11-17：U3（退回鏈：要求的語系 → 上層 → 系統預設 → 繁中），T10（第一步沿用 C# 四語表當內建層、呼叫點可分段改）、T11（C# 表搬 JSON 不排進 i4）。
- 2026-11-18：M3（一次產生多語的地方對每個已安裝語系各產生一句）。
- 2026-11-20：業務政策不寫死、權限 fail closed、驗證不只做在前端；寫進資料的系統文字用系統預設語系（`backend.md`，`IncidentTitles.Snapshot` 與簽核說明照舊）。
