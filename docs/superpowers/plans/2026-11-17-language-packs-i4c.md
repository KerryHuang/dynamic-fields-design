# 語系包 i4c Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 後端訊息改成可擴充的形狀。新增 `IMessageCatalog`（singleton），內建層沿用現有的 C# 四語表（`MessageText`、`ReportText`、`AlertMailText`（Q1 已裁決併入）），沿設計 §8.3 的退回鏈逐層找句子；回應的解析點（`ServiceResultExtensions`、WebAPI 的例外處理與 `SessionMiddleware`）改從 DI 取它；直接呼叫 `MessageText.Get`／`ReportText.Get` 的檔逐一改成走它；`AsI18n` 改成對**已安裝**語系各產生一句，`ApprovalMessages` 的 3 個 `static readonly` 改方法；Domain 的 `TaskReportFields` 四語表搬進 `ReportText`，由 Application 組虛擬模組時傳入解析函式。使用者看得到的行為：**Demo 上無變化**（內建四語的每一句與 main 逐字相同）；只有資料表裝了內建四語以外的語系（i4d1 才有安裝入口，現在只有測試能造）時，那個語系的後端訊息從「一律繁中」改成走退回鏈（上層語系 → 系統預設 → 繁中），這是設計 U3 已裁決的行為。

**Architecture:** `IMessageCatalog` 放 `Application/Common/Localization/`，實作 `MessageCatalog` 依賴 `LocaleRegistry`（已安裝清單與上層關係）與 `CachedSystemPolicy`（預設語系），兩者都是記憶體快照，查一句不碰資料庫。主要入口收 `LocaleContext`（語系＋退回鏈，呼叫端已經從 `RequestLocale` 拿到），和 `I18nText.Resolve(LocaleContext)` 同一個鏈來源。內建層是 `BuiltinServerText`：把三張 C# 表合成一份唯讀字典（鍵加命名空間：`MessageText` 不加、`ReportText` 加 `report.`、`AlertMailText` 加 `alert.`），鍵就是設計 §5.1 語系包 `server` 段的鍵。純函式路徑（`RecordValueProcessor`、`FieldDisplay`、`ApproverResolver`）經它們已有的上下文 record 帶 catalog；Domain 的 `TaskReportFields` 只宣告文字鍵，`Build` 多收一個 `Func<string, I18nText>`。i4d1 的覆寫層插在 `MessageCatalog` 每個語系的內建層前面，本段不預留介面。

**Tech Stack:** .NET 8、MediatR、FluentValidation、xUnit＋FluentAssertions；EF Core（SQLite 研發、PostgreSQL 正式，本段沒有 schema 變更）。不新增套件、不動前端。

**Spec:** `docs/superpowers/specs/2026-11-17-language-packs-design.md`（§1 寫死清單的「後端訊息」「任務報表欄位表」兩項、§3 後端訊息解析點、§5.1 `server` 段的鍵、§8.3 退回鏈、§8.4 與 M3 的「每個已安裝語系」口徑、§9 全部、§14、§15（後端訊息剩餘清單、警示信語系「i4c 再定」）、§17 T10、§18 第 9、11 點、§19 退回鏈與安全兩條、§20 i4c 列、§22）。前置計畫：`docs/superpowers/plans/2026-11-17-language-packs-i4a.md`（已上線 `3d0dd59b`；判斷 6、10、23 與「待決」第二條）、`docs/superpowers/plans/2026-11-17-language-packs-i4b.md`（已上線 `7843faa9`；判斷 5–7、16）。repo 規範：`AGENTS.md`、`.claude/rules/{backend,docs}.md`、`CONTRIBUTING.md`、`docs/ai/testing.md`。

**路徑慣例：** 後端相對 `backend/`（`src/…` 指 `backend/src/…`），文件與 `shared/` 相對 repo 根目錄。行號以 main `b138df50` 為準（第二輪修正時對 `9c1c8b79` 核對過，後端程式碼沒變）；不符時以符號為準。`Application/…` 是 `backend/src/DynamicModules.Application/…` 的縮寫，`WebAPI/…`、`Domain/…`、`ApplicationTests/…`（`backend/tests/DynamicModules.ApplicationTests/…`）同理。

---

## 第二輪修正對照（跨家族審查，結論「修正後可開工」；審查全文在本機暫存，不進版控）

| # | 審查意見 | 改在哪裡 |
|---|---|---|
| 必修 1 | `AsI18n(string, params object[])` 不能直接當 `Func<string, I18nText>`；Domain 測試改成「只有繁中、值為鍵」後，「模組名＝任務」與四語非空斷言會失敗 | 判斷 9、Task 4：改傳 `k => messages.AsI18n(k)`；Domain 測試改驗「標籤＝解析函式的回傳值」，四語與原文交給 Application 的快照 |
| 必修 2 | catalog 與靜態 `Get` 讀同一份改過的表，誤改文字會一起通過；任務報表與警示信快照只抽代表信件 | Task 1 新增 Step 0：改表前把 `MessageText`／`ReportText` 全部鍵的四語範本存成基準檔進 repo，之後的比較一律對基準檔；Task 4 Step 0 核對 41 句全在快照；Task 7 Step 0 存 `alert.*` 基準檔與「五個入口 × 全部分支」的信件快照 |
| 必修 3 | 「合併表筆數＝public const 數」在 Task 4、7 加鍵後必敗；Task 6 驗收要求 `ReportText.Get` 無結果，但 `FieldImportParser` 到 Task 8 才改；Task 8 刪 `Get` 會破壞 Task 1 的比較測試 | `Table_EqualsBaselines` 讀明列的基準檔清單，Task 4、7 各在 Step 1 加一個檔；基準測試只讀基準檔、不呼叫靜態 `Get`；Task 6 不刪 `ReportText.Get`，驗收改成「只剩 `FieldImportParser.cs`」；Task 8 才刪 |
| 必修 4 | `AsI18n` 分別讀 `Codes` 與每個 `FallbacksFor`，中途刷新會混用新舊快照 | 判斷 2：新增 `LocaleRegistry.ContextsFor(defaultLocale)`，從同一份快照一次產生全部語系的 `LocaleContext`，預設語系也只讀一次；`MessageCatalog` 改依賴 `LocaleRegistry`；Task 1 加 `AsI18n_RefreshMidway_UsesOneSnapshot` |
| 必修 5 | 備援進度圖用查詢時重新產生的名稱，時間軸用 `NameSnapshot`，建立後換鏈會不一致 | 判斷 8：`FallbackDefinition` 的名稱先取該實例備援關卡的 `NameSnapshot`，缺了才用 catalog；Task 3 加 ApplicationTests 案例與 `FallbackGraph_AfterChainChange_SameAsTimeline` |
| 必修 6 | 固定別名留在撞字判定之外，某語系把「否」翻成 `Yes` 時 `yes` 仍判成 true | Task 8 `BooleanWords`：固定別名先併進兩個集合再算撞字，撞字先拒絕再解析；補 `No＝Yes／是／1` 案例 |
| 必修 7 | `ja → en-US` 匯出的是 `Yes／No`，現有 parser 已接受，round-trip 假綠 | Task 8：用 `TestApp.ExtraServices` 換成測試 catalog（`ja` 的是／否是「はい／いいえ」），走完整匯出再匯入，並先斷言 CSV 真的含「はい」；另驗已安裝但停用的 `ja` |
| 必修 8 | null catalog 用的內建 catalog 仍照傳入的鏈，帶 `["en-US", "zh-TW"]` 得 `Yes` 不是「是」 | Task 6 `Boolean_Ja_FollowsChain` 改斷言 `Yes`；另加「鏈只有繁中」退回「是」的案例 |
| 建議 1 | 共用案例有「全部缺值 → 空字串」，nullable `FirstInChain` 怎麼對應要寫明；只測 helper 不夠 | **採用**。判斷 12 寫明 null 對應空字串、catalog 的 `Get` 走不到 null；`SharedChainCases` 另用真的 registry 與政策組鏈，再經 `MessageCatalog.Get` 驗一次 |
| 建議 2 | `AlertMailComposer` 有五個入口；`Label`／`CheckName` 對未知代碼的既有退路要保留 | **採用**。判斷 3、Task 7 Files 與快照列滿五個入口；未知代碼照舊回代碼本身（`CheckName` 回繁中名稱或代碼） |
| 建議 3 | 補 DI scope 驗證，以及 `RequestMessages` 沒有 services、services 已釋放的案例 | **採用**。Task 2 加 `Catalog_SingletonUnderScopeValidation` 與 `RequestMessagesTests` 兩案；`RequestMessages` 改成 `public`（測試專案沒有 `InternalsVisibleTo`，`internal` 測不到） |

另外：待決 Q1、Q2 已裁決照推薦（見「已裁決」節），各處「照推薦才做」的條件分支一併拿掉。第二輪新出現、會影響使用者看到什麼的分岔只有一條，列在「已裁決」節末尾，標「主控裁決待確認」。

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
9. **Q1、Q2 已裁決**（見「已裁決」節）：Task 7、8 都要做。

## 子段切法（兩個實作者交棒點）

| 批 | 內容 | Task | 手寫行數（估） | migration |
|---|---|---|---|---|
| i4c-1 | catalog 本體與內建層、範本基準、`ReportText` 鍵加前綴、回應解析點與 WebAPI 例外訊息、`AsI18n` 改已安裝語系與簽核的 3 個 `static readonly`、`TaskReportFields` 搬表 | 1–4 | ≈ 1310（測試約 55%） | 無 |
| i4c-2 | 直接呼叫點：記錄寫入路徑、報表與顯示路徑、警示信（Q1）、匯入是否欄位（Q2）、剩餘清單與文件回填、gate | 5–9 | ≈ 1270（測試約 50%） | 無 |

同一分支 `i4c`（worktree `../formwright-i4c`），每個 Task 各自 commit。i4c-1 收尾跑一次窄範圍 PostgreSQL 雙跑（不跑 gate），交棒給 i4c-2；i4c-2 收尾才跑 gate、合併。

**交棒條件（i4c-1 → i4c-2）**：Task 1–4 驗收全過；`IMessageCatalog` 的介面（判斷 1、2）、`BuiltinServerText` 的鍵命名空間（判斷 3）、各上下文 record 怎麼帶 catalog（判斷 7）、範本基準檔的清單與「建立後不准改」（判斷 14）寫進交棒說明。i4c-1 單獨存在時使用者行為與 main 相同（內建四語逐字不變），還沒改到的直接呼叫點照舊用靜態 `Get`。分界理由：i4c-1 是形狀與解析點（每個請求都經過），i4c-2 是逐檔替換（量大但機械），分開審比較容易看出解析點有沒有改壞。

---

## 計畫期間的判斷

〔行為〕＝影響使用者看到或能做的事；〔技術〕＝實作選擇。已拍板的不再問。

1. 〔技術〕**主要入口收 `LocaleContext`，不收語系字串**（設計 §9.1 寫的是 `Get(string locale, …)`）。
   - `string Get(LocaleContext lc, string key, params object[] args)`：依序試 `[lc.Locale, ..lc.Fallbacks, "zh-TW"]`（去重），每個語系取內建層（i4d1 起先取覆寫層），第一個有的就用；參數照 `string.Format(CultureInfo.InvariantCulture, …)`，`args` 為空時不呼叫 `Format`、範本原樣回（同現行 `MessageText.Get`，基準檔比對靠這點）。
   - 理由：(a) 呼叫端幾乎都已經有 `LocaleContext`（`RequestLocale.Current`／`For(...)`、報表的 `lc`、`RuleContext.Locale`），鏈的組法只有 `RequestLocale`／`CachedSystemPolicy.FallbacksFor` 一個來源，和設計資料的 `I18nText.Resolve(LocaleContext)` 同鏈，設計 §19「介面、後端訊息、設計資料三處同結果」自然成立；(b) 收字串的話 catalog 要自己決定預設語系與鏈，等於多一份組鏈規則。
   - 不另提供 `Get(string, …)` 多載：只有字串的地方（WebAPI 例外處理、`WorkflowEnforcer`、`GetConversionInputs`）一律改從 `RequestLocale` 取 `LocaleContext`，避免日後又有人繞過退回鏈。
   - 鍵不存在＝程式錯誤，照現有 `Table[key]` 丟 `KeyNotFoundException`（不顯示鍵）。內建繁中層完整，鏈一定在繁中結束，不會走到「顯示鍵」。
2. 〔技術〕**`I18nText AsI18n(string key, params object[] args)` 對每個已安裝語系各產生一句**（設計 §9.1、M3 口徑），**整批只讀一份快照**：先讀一次 `CachedSystemPolicy.DefaultLocale`，再呼叫新的 `LocaleRegistry.ContextsFor(string defaultLocale)`：它只做一次 `Volatile.Read(ref _current)`，從同一份 `Published.Infos` 產生每個已安裝語系（含已停用的）的 `LocaleContext`，順序同 `Codes`；之後逐一 `Get(lc, key, args)`。不分別讀 `Codes` 與每個語系的 `FallbacksFor`：兩者各讀當下狀態，中途刷新 registry 或改預設語系時，同一個 `I18nText` 會混到新舊兩份退回鏈（第二輪必修 4）。`MessageCatalog` 因此依賴具體的 `LocaleRegistry`（同 `CachedSystemPolicy` 的做法），不依賴 `IInstalledLocales`。測試掛鉤比照 `LocaleRegistry.BeforePublishForTests`：`public Action<string>? BeforeLocaleForTests`，擷取快照之後、產生每個語系之前呼叫。所以 `ja`（上層 `en-US`）拿到的是英文句子、寫進 `ja` 鍵，與 i4b 判斷 16「一次產生多語的地方拿到補齊後的文字」同性質；日後補了日文，已存的標籤不會跟著改（設計 §15 已列）。不採「只產生有自己翻譯的語系、其他留給讀取時退回」：同一份資料在前端（i4b）與後端兩種口徑會讓簽核進度圖與沖銷欄位的行為不一致。
3. 〔技術〕**內建層 `BuiltinServerText`（`Application/Common/Localization/BuiltinServerText.cs`）**：三張表各開一個 `internal static IEnumerable<KeyValuePair<string, (string Zh, string En, string Th, string Vi)>> Entries`，`BuiltinServerText.Table` 合成 `FrozenDictionary`（`StringComparer.Ordinal`），建表時重複鍵丟例外（`BuiltinServerTextTests` 先抓到）。`TryGet(string locale, string key, out string template)` 只認內建四語。`BaseKeys`（排序後的全部鍵）留給 i4d1 的「基準鍵集」（設計 §5.2）。
   - **`ReportText` 的常數值直接帶前綴**：`Yes = "report." + nameof(Yes)`，表與呼叫端照常用常數，catalog 的鍵就是常數值。已查證 `ReportText` 的常數只當文字鍵用（不寫進資料庫、不回給前端當代碼，`grep -rn "ReportText\.[A-Z]" backend/src | grep -v "ReportText.Get("` 全部是 `Fail(...)`、`T(...)`、`IsDisplayed(...)` 的文字鍵參數）。`MessageText` 不加前綴（設計 §5.1：鍵是 `MessageText` 常數名；Domain 的 `FieldErrors` 鍵與它同名）。
   - `AlertMailText`（Q1 已裁決，Task 7）：主表 `alert.<常數名>`、代碼名稱 `alert.label.<代碼>`（例 `alert.label.severity.critical`）、健康檢查名稱 `alert.check.<代碼>`（`HealthCheckCatalog.All` 每個檢查一個鍵；繁中取自它的 `ZhName`，維持單一來源；`CheckNames` 沒列的檢查四語都填繁中，同現行）。
   - **未知代碼的既有退路保留**（第二輪建議 2）：`Label` 的鍵不在 `BuiltinServerText.Table` 時照舊回代碼第一個 `.` 之後那段（例 `severity.foo` → `foo`），不丟例外；`CheckName` 的代碼不在 `HealthCheckCatalog` 時回代碼本身。這兩處是「代碼值來自資料（事件參數）」，和判斷 1「鍵寫錯是程式錯誤」不同。用 `Table.ContainsKey` 判斷就夠：i4d1 的覆寫層只收基準鍵（設計 §5.2），不會有內建層沒有的鍵。
4. 〔技術〕**`BuiltinMessageCatalog.Instance`（static）只看內建層、已安裝清單固定為內建四語、鏈只用傳入的 `LocaleContext`**。給三種地方用：上下文 record 的 catalog 為 null 時（測試直接 `new` 的 `FieldDisplayContext`、`ResolveContext`）、`ServiceResult.Message`／`Errors`（繁中，給 log 與測試）、單元測試的 `HttpContext` 沒有 `RequestServices` 時。正式路徑一律從 DI 取 `MessageCatalog`，Task 9 的 S4 grep 驗證正式程式沒有用 `BuiltinMessageCatalog.Instance`（除了上述 null 退路）。
5. 〔技術〕**解析點**（設計 §9.1）：
   - `MessageRef` 加 `Resolve(IMessageCatalog messages, LocaleContext lc, bool translateLiteral = false)`：鍵走 `messages.Get`；`I18nText` 參數用 `t.Resolve(lc)`；字面值剛好是無參數句的繁中原文時，用 `MessageText.KeyOfZh(literal)`（新的 internal 查表，取代 `TranslateLiteral` 內部那段）換成鍵再走 catalog。
   - `ServiceResult` 加 `MessageIn(IMessageCatalog, LocaleContext)`、`ErrorsIn(IMessageCatalog, LocaleContext)`。
   - **舊的字串版 `MessageRef.Resolve(string?, …)`、`ServiceResult.MessageIn(string?, …)`／`ErrorsIn(string?, …)` 保留**，內部改呼叫 `BuiltinMessageCatalog.Instance`（傳入的 `fallbacks` 當鏈）。理由：測試裡約 145 處 `.Resolve(null)`、`.Resolve("th")` 比對訊息，全改是一個與本段無關的巨型 diff；內建四語的結果與現在逐字相同（Task 2 有案例鎖住）。
   - `ServiceResultExtensions.ToActionResult` 從 `RequestServices` 取 `RequestLocale` 與 `IMessageCatalog`，兩者都有就走新多載，否則走舊路徑（單元測試的 `HttpContext` 沒有 services，行為同現在）。
6. 〔技術〕**WebAPI 的例外訊息與 middleware**：新增 `WebAPI/Errors/RequestMessages.cs`：`public static string Text(HttpContext ctx, string key, params object[] args)`（`public`：測試專案沒有 `InternalsVisibleTo`），從 `ctx.RequestServices` 取 `RequestLocale.Current` 與 `IMessageCatalog`，取不到（`RequestServices` 是 null、services 已釋放丟 `ObjectDisposedException`、極早期失敗）就用 `BuiltinMessageCatalog.Instance` 加 `new LocaleContext("zh-TW", [])`，不往外丟例外（例外處理裡再丟例外會蓋掉原本的錯誤）。四個例外處理（`ConcurrencyExceptionHandler.cs:15`、`DatabaseBusyExceptionHandler.cs:18`、`DatabaseUnwritableExceptionHandler.cs:31`、`UnhandledExceptionHandler.cs:84`）與 `SessionMiddleware.cs:52,59` 改用它；`SessionMiddleware.InvokeAsync` 的 `ICurrentLocale locale` 參數移除（自己改出來的孤兒要清）。資料庫寫不進、連不上時也能出訊息：`RequestLocale`、`CachedSystemPolicy`、`LocaleRegistry` 都是記憶體快照，不讀資料庫。
7. 〔技術〕**純函式路徑經既有的上下文 record 帶 catalog**，不把它們改成 DI 類別：
   - `RuleContext`（`Application/Dynamic/RecordService/RecordValueProcessor.cs:16`）改成 `RuleContext(DateOnly Today, LocaleContext Locale, IMessageCatalog Messages, LocaleContext? SystemLocale = null)`，**必填**：測試沒有直接 `new` 它（`grep -rn "new RuleContext(" backend/tests` 為 0），正式程式 4 處（`DetailWriter.cs:158`、`RecordCreator.cs:39`、`UpdateRecordHandler.cs:66,70`）一起改。
   - `FieldDisplayContext`（`Application/Dynamic/Display/FieldDisplay.cs:15`）加最後一個選填參數 `IMessageCatalog? Messages = null`（null 用判斷 4 的內建 catalog）：測試有多處直接 `new FieldDisplayContext(locale, offset)`。`CachedSystemPolicy.Display(string locale, IMessageCatalog? messages = null)` 多一個選填參數，正式呼叫端（`GetRecordHistoryHandler.cs:60`、`ExportRecordsHandler.cs:55`、`PrintRecordHandler.cs:50`、`ReportRunner.cs` 的 `RunContext.Display`）都要傳；不讓 `CachedSystemPolicy` 依賴 catalog（catalog 已依賴它，會成環）。
   - `ResolveContext`（`ApproverResolver.cs:25-28`）加最後一個選填參數 `IMessageCatalog? Messages = null`：測試直接建它（`ApproverResolverTests`、`ApprovalRuntimeTextTests`）；正式的 `ContextAsync`（`ApproverResolver.cs:50`）一定傳。
8. 〔技術〕**`ApprovalMessages` 的 `AsI18n` 呼叫點改方法**（設計 §9.1）：`FallbackStageName`、`PickLabelI18n`、`AddSignStageName` 三個 `static readonly`，與已是方法的 `ManagerLabelI18n(int)`，改成 `FallbackStageName(IMessageCatalog m)`、`PickLabelI18n(IMessageCatalog m)`、`AddSignStageName(IMessageCatalog m)`、`ManagerLabelI18n(IMessageCatalog m, int level)`。另有一個設計沒點名的衍生 `static readonly`：`ApprovalViews.FallbackDefinition`（`ApprovalQueryHandlers.cs:419-422`，進度圖用 `FallbackStageName` 組的備援流程定義），改成 `FallbackDefinition(I18nText stageName)` 方法，`ApprovalViews.Instance`（`:366`）與 `ProgressGraph`（`:430`）多收一個 `I18nText fallbackStageName`，由 `GetRecordApprovalHandler`（`:202` 呼叫 `Instance` 的地方，注入 catalog）傳入。`MessageText.AsI18n`（`MessageText.cs:485`）改完後沒有呼叫端，刪除。
   - **備援節點的名稱先用該實例的名稱快照**（第二輪必修 5）：`ProgressGraph` 組 `FallbackDefinition` 時，名稱取這個實例 `StageCode == ApprovalEngine.FallbackStageCode` 的關卡中第一個非 null 的 `NameSnapshot`（`runs` 已是該實例全部關卡），沒有才用傳入的 `fallbackStageName`（查詢當下由 catalog 產生）。理由：時間軸的關卡名稱是 `NameSnapshot`（`:396`），進度圖若重新產生，建立實例後改了 `ja` 的上層語系，兩處會顯示不同文字。內建四語的結果不變（快照與重新產生的字相同）；改的只是新語系在換鏈之後的一致性，所以歸技術。
9. 〔技術〕**`TaskReportFields`（Domain）只留結構與文字鍵，文字搬進 `ReportText`**（設計 §20 i4c 列「由 Application 組裝時傳入」）：
   - Domain 加巢狀 `public static class TextKeys`，每句一個 `const string`，值就是 catalog 鍵（`"report.TaskFieldTitle"`、`"report.TaskStatusTodo"`、`"report.TaskBlankAssignee"`、`"report.TaskDatasetTasks"`…）。鍵定義只在 Domain 一處，`ReportText.Table` 的列直接引用 `TaskReportFields.TextKeys.X`（Application 可以參照 Domain，反過來不行）。前綴 `report.` 出現在 Domain 是為了「一個鍵一個字串、不在兩層各拼一次」，代價是 Domain 知道語系包的命名空間，可接受。
   - 共 41 句：欄位名 18、狀態 4、來源 3、完成狀況 4、層級 3、事件種類 2、空值字 5、資料來源名 2（設計 §1 寫「約 37 句」）。空值字「所屬模組」「任務規則」的「（無）」繁中相同，仍各留一個鍵（不同語言的「無」可能因名詞而不同，合併省一個鍵不值得）。
   - `Build(string dataset, IReadOnlyList<Department>, IReadOnlyList<Module>, IReadOnlyList<TaskRule>, Func<string, I18nText> text)`：欄位名、固定選項、模組名稱都用 `text(鍵)`；`BlankLabels`、`DatasetNames`（`I18nText` 值）改成 `BlankLabelKeys: IReadOnlyDictionary<long, string>`、`DatasetNameKeys: IReadOnlyDictionary<string, string>`；另加 `AllTextKeys`（測試用，確認每個鍵都在 catalog）。刪掉私有的 `T(zh, en, th, vi)`。
   - Application：`TaskReportSource.CatalogAsync`（`TaskReportSource.cs:46`）注入 catalog、傳 `k => messages.AsI18n(k)`（方法群組 `messages.AsI18n` 的簽章是 `(string, params object[])`，不能直接轉成 `Func<string, I18nText>`；虛擬模組的標籤之後照常經 `LabelI18n.Resolve(lc)`，所以要完整的 `I18nText`）；`GetReportDatasetsHandler.cs:14` 與 `ReportRunner.Tasks.cs:70-71` 的空值字直接 `messages.Get(lc, 鍵)`。
   - 「與前端 `status.*` 同字（K2）」的註解搬到 `ReportText` 狀態那四列。
10. 〔技術〕**改到哪些直接呼叫點、哪些留著**（設計允許分段；剩餘清單在 Task 9 寫進設計 §15）。b138df50 的 22 個檔：

    | 檔（呼叫數） | 處置 | Task |
    |---|---|---|
    | `Common/MessageRef.cs`（1） | 加 catalog 多載，舊多載改走內建 catalog（判斷 5） | 2 |
    | `WebAPI/Errors/` 四個例外處理（各 1）、`WebAPI/Security/SessionMiddleware.cs`（2） | `RequestMessages.Text`（判斷 6） | 2 |
    | `ApprovalService/ApprovalMessages.cs`（2 個 `Get`＋4 個 `AsI18n`） | `AsI18n` 四個改方法（判斷 8）；`ManagerLabel(int)`、`PickLabel` 兩個字串版**留著**（只剩測試引用、固定繁中） | 3 |
    | `RecordService/RecordValueProcessor.cs`（4）、`RecordService/DetailWriter.cs`（4）、`WorkflowService/WorkflowEnforcer.cs`（4＋2 個常數）、`ConversionService/.../GetConversionInputs.cs`（1） | 改。四檔一起改：`GetConversionInputs` 拿「必填」那句和驗證結果比字串，兩邊必須同一個來源，否則新語系時比對不到 | 5 |
    | `ReportService/ReportRunner.cs`（10）、`ReportRunner.Output.cs`（18）、`ReportRunner.Sql.cs`（3）、`ReportRunner.Tasks.cs`（5）、`DashboardService/DashboardCardRunner.cs`（2）、`ReportService/Queries/ExportReport/ExportReportHandler.cs`（2）、`ReportService/Queries/GetReportCatalog/GetReportCatalogHandler.cs`（1）、`Display/FieldDisplay.cs`（3） | 改（`ReportRunner` 的 static 輔助函式凡用到文字的改成 instance 方法；`FieldDisplay` 經 `FieldDisplayContext`） | 6 |
    | `ImportService/FieldImportParser.cs`（1） | Q2 已裁決：改（是／否文字比對已安裝語系，固定別名一起判撞字）；Task 6 之後它是 `ReportText.Get` 唯一的呼叫端，Task 8 改完才刪 `ReportText.Get` | 8 |
    | `WorkflowEnforcer.NotAllowedMessage`／`NotPermittedMessage`（`static readonly`，`MessageText.Get(null, …)`） | **留著**：只有 `CrmDesignCaseAppTests` 拿來比對繁中回應（既有，提出不動） | — |
    | `Common/DatabaseBusyException.cs`（1） | **留著**：例外本身的訊息（log 用，固定繁中）；使用者文字由例外處理經 catalog | — |
    | `Common/Behaviors/MessageRuleExtensions.cs`（1） | **留著**：`WithMessage` 是 FluentValidation 內部的繁中訊息，回給使用者的走 `WithState` 的 `MessageRef`（判斷 5） | — |

    `AlertMailText` 不是 `MessageText.Get`／`ReportText.Get` 的呼叫點，但它是第三張四語表，Q1 已裁決併入（Task 7）。
11. 〔技術〕**票 #66 不併入 i4c**。#66 是 11 行設計資料名稱 `Resolve(I18nText.FallbackLocale)`（套件驗證、`AppApprovalFlowCodec`、`MasterMerger`、`TemplateExporter`、`TemplateValidator`、`FieldRules`、`AppResponses`），b138df50 仍是同樣 11 行。理由：(a) 機制不同：它們是 `I18nText` 的名稱解析（設計 §10），不是後端訊息表；(b) 名稱是嵌進**繁中字面值**的管理類訊息（例「鎖定帶值的目的欄位必須先設為唯讀：{名稱}」），只把名稱換成請求語系會變成中英混雜的一句，正解是先把整句搬進 `MessageText`，那是設計 §2 非目標與 §15 的「管理類訊息（#19–#21）」；(c) 要把語系穿過公開靜態 API（`MasterMerger.MergeDefinition` 等），`V8.cs:289` 還要和 `ApprovalConstraints.Validate` 的訊息同語系比對，改動外擴到套件流程。#66 維持 backlog，Task 9 在票上留言「i4c 判斷 11：不併入，待管理類訊息多語時一起做」。
12. 〔技術〕**退回鏈的共用案例**：`shared/locale-chain-cases.json`（Domain 的 `LocaleChainTests`、前端兩個 vitest 已在讀）加讀一份到 `MessageCatalogTests`，分兩層驗（第二輪建議 1）：
    - **走法**：每個案例以 `text` 當唯一一層、鏈取 `[locale, ..fallbacks]`，`FirstInChain(...) ?? ""` 要等於 `expected`。為了能用任意層測，鏈的走法抽成 `public static string? MessageCatalog.FirstInChain(IEnumerable<string> chain, Func<string, string?> layer)`，`Get` 也用它。**null 的對應**：`FirstInChain` 全部缺值回 null，案例檔的「全部缺值 → 空字串」（U6）對應 `?? ""`，與 `I18nText.Resolve` 回 `""` 同義；`Get` 的鏈尾一定是 zh-TW、內建繁中層完整，所以走不到 null，真的走到（鍵不在內建層）就丟 `KeyNotFoundException`（判斷 1）。
    - **真的入口**：同一組案例依 `installed`、`default` 建 registry 與政策（`FakeLocaleRepository`），先斷言 `policy.FallbacksFor(locale)` 等於案例的 `fallbacks`（證明 catalog 拿到的鏈與設計資料同一個來源），再取一個內建鍵（`Forbidden`）呼叫 `MessageCatalog.Get(new LocaleContext(locale, policy.FallbacksFor(locale)), …)`，結果要等於 `FirstInChain([locale, ..fallbacks, "zh-TW"], l => 基準檔[Forbidden][l])`。不為測試另開「可換內建層」的建構子（本段不預留覆寫層介面）。
    - 案例檔本身不改。
13. 〔技術〕**沒有新的使用者訊息、沒有新的業務政策、沒有 migration**。i4c 只改「同一句話從哪裡取」。前端不動。
14. 〔技術〕**「內建四語逐字不變」的證據是改動前固定的基準檔，不是改動後的靜態 `Get`**（第二輪必修 2、3）：
    - **範本基準**：`ApplicationTests/Common/ServerTextBaseline/`（讀法照 `BuiltinLocaleResourcesTests` 往上找 repo 檔）下每張表一個 JSON，形狀 `{ "<catalog 鍵>": { "zh-TW": "…", "en-US": "…", "th": "…", "vi": "…" } }`，鍵依 `StringComparer.Ordinal` 排序、縮排、`JavaScriptEncoder.UnsafeRelaxedJsonEscaping`（diff 看得懂）。值是**未帶參數的範本**（現行 `Get(locale, key)` 不帶 `args` 就回原樣範本）。三個檔各在動那張表之前、由**現行** API 產生並單獨 commit：`message-report.json`（Task 1 Step 0：`MessageText`、`ReportText` 全部 `public const string`，`ReportText` 的鍵寫成 `report.<常數名>`）、`task-report.json`（Task 4 Step 0：41 句，鍵是 `TextKeys` 預定的值）、`alert.json`（Task 7 Step 0：主表、代碼名稱、健康檢查名稱）。產生用的暫時測試跑完即刪，不留在 repo。
    - **完整性**：`BuiltinServerTextTests.Table_EqualsBaselines` 讀**明列**在測試裡的基準檔清單（Task 1 只有 `message-report.json`，Task 4、7 的 Step 1 各加一個檔名，所以 Step 0 單獨 commit 時測試仍是綠的），斷言 `BuiltinServerText.Table` 的鍵集合＝各檔鍵的聯集、每個鍵四語範本逐字相等。不再用「數 `public const`」：Task 4 加的 41 個鍵在 Domain、Task 7 的代碼名稱不是常數，數常數必敗。
    - **不依賴之後會刪的 API**：`Builtin_SameAsBaseline`（Task 1）、`AsI18n_Builtin_SameAsBefore`（Task 3）、`Factory_FourLocales_Unchanged`（Task 2）一律對基準檔比（有參數的案例用 `string.Format(InvariantCulture, 基準範本, 同樣參數)`）。Task 8 刪 `ReportText.Get`、Task 7 改 `AlertMailText` 的簽章都不影響它們。
    - **輸出快照**（範本基準管不到「組字」）：任務報表的虛擬模組快照（Task 4）與警示信、事件標題快照（Task 7）也在改動前用現行 API 產生，涵蓋全部鍵與分支（各 Task 的 Step 0 列出案例清單與核對方式）。

---

## 已裁決（第一輪待決，2026-11-21 照推薦定案）

- **Q1 警示信的文字走語系包**：`AlertMailText` 併進 `IMessageCatalog`（鍵前綴 `alert.`，判斷 3），信件每一句沿退回鏈取；i4d1 起語系包的 `server` 段就能帶 `alert.*` 句子（Task 7）。
  - 例：日本廠的系統管理員把信件語系設成「日本語」，匯入含 `alert.*` 的日文語系包後，告警信主旨與內文變成日文；沒翻到的句子顯示上層語系（例如英文）。
  - 代價：語系包基準鍵多約 80 句；設計 §5.1 補 `alert.` 命名空間（Task 9）。i4d1 之前看不出差別。
- **Q2 匯入 CSV 的「是否」欄位接受所有已安裝語系的是／否文字**（含已停用的語系，同 M3「已安裝」口徑），撞字當無法判斷（Task 8）。第二輪審查補定：固定別名（是／true／yes／1、否／false／no／0）也算進兩個集合再判撞字，撞到的字一律拒絕，回既有的錯誤「請輸入「是」或「否」」。手冊第 622 行補一句「其他已安裝語言匯出的是／否文字也能匯回」。
  - 例：日籍員工用日文介面匯出訂單，「已付款」欄是「はい」，改完數量後原檔匯入，「はい」照樣當成「是」。
  - 代價：語系包把「否」翻成別的語系的「是」時，撞到的字在所有使用者的匯入都會被擋（擋下來總比悄悄判反好）；同字不同義而沒撞到的擋不下。
- **第二輪新出現的分岔（主控裁決待確認，推薦＝維持既有錯誤訊息）**：撞到的字剛好是固定別名時，錯誤訊息會自相矛盾。
  - 例：某份日文語系包把「否」誤翻成「是」。繁中使用者匯入自己匯出的檔，「已付款」欄填的就是「是」，卻被擋下並看到「請輸入「是」或「否」」。
  - 推薦：維持既有訊息、不新增文字。理由：只有語系包翻錯才會發生，正解是在源頭擋——i4d1 的語系包驗證器拒絕 `report.Yes`／`report.No` 與固定別名或其他已安裝語系撞字的語系包（Task 9 開 backlog 票給 i4d1）；本段也維持「沒有新的使用者訊息」（判斷 13）。
  - 另一個選項：新增一句「「{0}」在已安裝的語言裡同時代表「是」和「否」，請改填 1 或 0」。代價是新的 `MessageText` 鍵（四語翻譯）、`FieldImportParser` 的錯誤要從繁中字面值改成帶參數的鍵，Task 8 多約 40 行；但 i4d1 的驗證器擋下之後，這句幾乎不會出現。
  - 選錯代價：選「維持」而 i4d1 的驗證器沒做，使用者在壞語系包裝上後會看到矛盾的錯誤，要找系統管理員查；選「新增」是多一句幾乎用不到的翻譯。

**留到之後（不在 i4c 問）**：管理類訊息（#19–#21）與 #66 的名稱解析（判斷 11）。

---

## Global Constraints

- **使用者看得到的行為不變**：內建四語的每一句回應、信件、報表文字、簽核名稱與 main 逐字相同（判斷 14：改動前固定的基準檔與輸出快照鎖住；Task 1 `Builtin_SameAsBaseline`、Task 3 `AsI18n_Builtin_SameAsBefore`、Task 4 `BuiltinLabels_MatchSnapshot`、Task 7 `AlertMail_Builtin_SameAsBefore`）。只有資料表裝了新語系時，該語系的後端訊息改走退回鏈（U3）。既有測試的預期值不改，例外只有：
  - `TaskReportFieldsTests`（Domain）改用新的 `Build(…, text)` 與 `BlankLabelKeys`；「模組名＝任務」與四語非空兩條斷言改成驗解析函式的回傳值，原文改由 Application 的快照驗（判斷 9、Task 4）。
  - `FieldImportParserTests` 的 `new ImportLookups(...)` 補 `BooleanWords` 參數，`:81-82` 的輸入改從內建 catalog 取（Task 8；預期值不變）。
  - `ApprovalAddSignTests.cs:96`、`ApprovalProgressGraphTests.cs:273` 的 `ApprovalMessages.AddSignStageName`／`FallbackStageName` 改成方法呼叫（判斷 8）。
  - `ReportText` 常數值多了 `report.` 前綴：若有測試把常數值當字面字串比（目前沒有，Task 1 Step 4 grep 確認）。
- **鐵則**：後端訊息只有一份內建來源（三張 C# 表，`BuiltinServerText` 只是合起來讀，不是副本）；退回鏈只由 `RequestLocale`／`CachedSystemPolicy.FallbacksFor` 組；Domain 不依賴 Application（`TaskReportFields` 只收函式）；權限與驗證不變。
- **寫死清單**：見下一節，交付說明列出，Task 9 寫進設計 §14。
- **PostgreSQL**：本段沒有 migration、raw SQL、新 repository。用到 `LocaleTestKit`（直接寫 `Dynamic_Locale`）的 API 測試照 i4a 慣例標 `Category=Postgres`，並在 PostgreSQL 跑過一次（`ServerMessageChainApiTests`，含 Task 8 的匯入案例）。純記憶體的單元測試不標。
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
| 匯入是否欄位：固定別名併入後兩個集合撞字時當無法判斷（Q2、第二輪必修 6） | 資料完整性守衛 | 不讓語系包的錯譯悄悄把「否」判成「是」 |
| 固定別名是／true／yes／1、否／false／no／0 | 不是新增（既有行為）；撞字時也會被拒（上一列） | — |
| 告警代碼名稱、健康檢查名稱遇到未知代碼回代碼本身 | 不是新增（既有退路，判斷 3） | — |

沒有新的上限、預設值、天數或業務政策。

## 剩餘清單（i4c 完成後仍不走 `IMessageCatalog` 的地方；Task 9 寫進設計 §15）

1. `MessageRuleExtensions.WithText` 的 `WithMessage(MessageText.Get(null, …))`：FluentValidation 內部的繁中訊息；回給使用者的文字走 state 的 `MessageRef`，已經過 catalog。刻意保留。
2. `DatabaseBusyException.DefaultMessage`：例外本身的訊息，只進 log；使用者看到的由例外處理經 catalog。刻意保留。
3. `WorkflowEnforcer.NotAllowedMessage`／`NotPermittedMessage`、`ApprovalMessages.ManagerLabel(int)`／`PickLabel`：只剩測試引用的繁中字串（既有，提出不動；要清可另開票）。
4. `MessageRef.Resolve(string?)`、`ServiceResult.Message`／`Errors`／`MessageIn(string?)`／`ErrorsIn(string?)`：只認內建四語的路徑，給 log 與測試；正式回應已改走 catalog。
5. 管理類訊息（設計器驗證、套件與樣板的警告等，#19–#21）仍是繁中字面值；#66 的 11 行名稱解析隨它們一起做（判斷 11）。
6. `FieldImportParser` 的其他錯誤訊息（「請輸入數字」「日期格式必須是 yyyy-MM-dd」「請輸入「是」或「否」」等）：既有的繁中字面值，不在 `MessageText`，新語系的使用者看到繁中（與第 5 項同類，搬進 `MessageText` 時一起做）。

## 檔案地圖

**共用檔（並行工作要協調；★＝本段會動）**

| 共用檔 | 本段 | 說明 |
|---|---|---|
| ★ 後端訊息表與語系快照 | Task 1、4、7 | `Application/Common/MessageText.cs`、`Application/Dynamic/ReportService/Data/ReportText.cs`（加 41 句任務報表文字）、`Application/Dynamic/OpsService/Alerts/AlertMailText.cs`（Q1）；`Application/Common/LocaleRegistry.cs` 只加 `ContextsFor`（Task 1） |
| ★ 後端 DI 註冊 | Task 1 | `Application/DependencyInjection.cs:49-54` 旁加 `AddSingleton<IMessageCatalog, MessageCatalog>()` |
| ★ WebAPI 解析點與 middleware | Task 2 | `WebAPI/Controllers/ServiceResultExtensions.cs`、`WebAPI/Errors/*ExceptionHandler.cs`、`WebAPI/Security/SessionMiddleware.cs` |
| ★ 簽核、報表、記錄寫入 | Task 3–6 | 見各 Task |
| ★ `TaskReportFields`（Domain） | Task 4 | 只改文字來源，欄位代碼與選項 Id 不動（存在報表定義裡） |
| DbContext／migration | 不動 | 沒有 schema 變更 |
| 前端、`shared/` | 不動 | 只讀 `shared/locale-chain-cases.json` |
| `backend/apps/*.app.json` | 不動 | — |

**新檔**：`Application/Common/Localization/{IMessageCatalog,MessageCatalog,BuiltinMessageCatalog,BuiltinServerText}.cs`、`WebAPI/Errors/RequestMessages.cs`；測試 `ApplicationTests/Common/{MessageCatalogTests,BuiltinServerTextTests,ServiceResultCatalogTests}.cs`、範本基準 `ApplicationTests/Common/ServerTextBaseline/{message-report,task-report,alert}.json`（判斷 14）、`ApplicationTests/Dynamic/Tasks/TaskReportTextTests.cs` 與快照 `task-report-texts.verified.json`、`ApplicationTests/Dynamic/Ops/AlertMailTextTests.cs` 與快照 `alert-mail.verified.json`、`WebAPITests/Dynamic/ServerMessageChainApiTests.cs`、`WebAPITests/RequestMessagesTests.cs`。

**文件（Task 9）**：設計 §5.1、§9.1、§14、§15、§18 第 9、11 點、§20 補註；`.claude/rules/backend.md`；`AGENTS.md` 架構地圖；`docs/feature.md`；`docs/manual.md`（改一句，Q2）；`.agents/` 鏡像（腳本重產）。

---

# i4c-1 形狀與解析點

### Task 1: `IMessageCatalog`、內建層、`ReportText` 鍵加前綴

**Files:**
- Create: `Application/Common/Localization/IMessageCatalog.cs`、`MessageCatalog.cs`、`BuiltinMessageCatalog.cs`、`BuiltinServerText.cs`
- Create（Step 0，改表前）：`ApplicationTests/Common/ServerTextBaseline/message-report.json`（判斷 14）
- Modify: `Application/Common/MessageText.cs`（`:118` 的 `Table` 旁加 `internal static … Entries`；`:481` 的 `KeyByZh` 旁加 `internal static string? KeyOfZh(string literal)`；類別註解補「鍵是語系包契約，改名要留別名（設計 §5.1）；第一步是 `IMessageCatalog` 的內建層」）
- Modify: `Application/Dynamic/ReportService/Data/ReportText.cs`（`:9-17` 常數值改 `"report." + nameof(X)`；`:19` 加 `Entries`；註解同上，並註明「`report.` 前綴是語系包 `server` 段的命名空間」）
- Modify: `Application/Common/LocaleRegistry.cs`（加 `ContextsFor(string defaultLocale)`，判斷 2）
- Modify: `Application/DependencyInjection.cs`（註冊 singleton）
- Test: `ApplicationTests/Common/MessageCatalogTests.cs`、`ApplicationTests/Common/BuiltinServerTextTests.cs`（新）

**Interfaces:**
```csharp
namespace DynamicModules.Application.Common.Localization;

public interface IMessageCatalog
{
    /// <summary>沿 [lc.Locale, ..lc.Fallbacks, zh-TW] 找第一個有的句子（判斷 1）；鍵不存在丟 KeyNotFoundException。</summary>
    string Get(LocaleContext lc, string key, params object[] args);
    /// <summary>對每個已安裝語系各產生一句，整批只讀一份快照（判斷 2）。</summary>
    I18nText AsI18n(string key, params object[] args);
}

public sealed class MessageCatalog(LocaleRegistry registry, CachedSystemPolicy policy) : IMessageCatalog
{
    public static string? FirstInChain(IEnumerable<string> chain, Func<string, string?> layer);   // 判斷 12；全部缺值回 null
    public Action<string>? BeforeLocaleForTests { get; set; }                                     // 判斷 2
}

public sealed class BuiltinMessageCatalog : IMessageCatalog { public static BuiltinMessageCatalog Instance { get; } }  // 判斷 4

public static class BuiltinServerText
{
    public static FrozenDictionary<string, (string Zh, string En, string Th, string Vi)> Table { get; }
    public static IReadOnlyList<string> BaseKeys { get; }
    public static bool TryGet(string locale, string key, [NotNullWhen(true)] out string? template);
}

// LocaleRegistry（既有類別，新增）
public IReadOnlyList<LocaleContext> ContextsFor(string defaultLocale);   // 一次 Volatile.Read，順序同 Codes
```

- [ ] **Step 0: 固定範本基準**（改表前，單獨 commit）：在 `BuiltinServerTextTests` 寫一個暫時的 `Dump` 測試，用**現行** API 以反射列出 `MessageText`、`ReportText` 全部 `public const string`，四語各呼叫一次不帶參數的 `MessageText.Get(l, key)`／`ReportText.Get(l, key)`，照判斷 14 的形狀寫成 `message-report.json`（`ReportText` 的鍵寫成 `report.<常數名>`）。任何常數不在表裡（`Get` 丟例外）就停下回報，不跳過。跑一次、核對鍵數等於前置條件 4 的 191＋37（當下數字為準）、人工抽看幾句含 `{0}` 與引號的、刪掉 `Dump`，只 commit 基準檔（`test(i18n): 後端訊息四語範本基準（改表前）`）。
- [ ] **Step 1: 寫失敗的測試**
  - `BuiltinServerTextTests`：
    - `Table_EqualsBaselines`（判斷 14）：測試裡明列基準檔清單 `["message-report.json"]`；`BuiltinServerText.Table` 的鍵集合＝各檔鍵的聯集，每個鍵四語範本與基準檔逐字相等。不數 `public const`。
    - `ReportKeys_AllPrefixed`：`ReportText` 每個常數值都以 `report.` 開頭；`MessageText` 沒有任何鍵含 `.`。
    - `EveryKey_NonBlankInFourLocales`、`Placeholders_MatchZh_AndFormatDoesNotThrow`（同 `MessageTextTests` 的做法，擴到合併表，含 `report.*`）。
    - `TryGet_OnlyBuiltinLocales`：`TryGet("ja", "Forbidden", out _)` 為 false。
  - `MessageCatalogTests`（純記憶體；registry 與 policy 照 `RequestLocaleTests.Create()` 的建法，裝 `ja` 用 `FakeLocaleRepository`）：
    - `SharedChainCases_Walk`、`SharedChainCases_CatalogEntry`（判斷 12 的兩層）：讀 repo 的 `shared/locale-chain-cases.json`（路徑找法照 `BuiltinLocaleResourcesTests`）。前者每案 `FirstInChain([locale, ..fallbacks], l => text.GetValueOrDefault(l)) ?? ""` 等於 `expected`（含 U6「回空字串」那案）；後者每案依 `installed`、`default` 建 registry 與政策，`policy.FallbacksFor(locale)` 等於案例的 `fallbacks`，且 `catalog.Get(那個 lc, Forbidden)` 等於 `FirstInChain([locale, ..fallbacks, "zh-TW"], 基準檔的 Forbidden 四語)`。
    - `Builtin_SameAsBaseline`：出廠（預設 zh-TW）對基準檔每個鍵 × 四語，`catalog.Get(RequestLocale 組的 lc, key)` 等於基準範本；有佔位符的鍵另帶假參數，等於 `string.Format(InvariantCulture, 基準範本, 假參數)`。不呼叫靜態 `Get`（Task 8 會刪 `ReportText.Get`）。
    - `Ja_ParentThenDefault`：`ja` 上層 `en-US`、預設 `th` → `Get(ja, Forbidden)` 是英文；`ja` 無上層 → 泰文。
    - `Ja_NotInstalled_StillChains`：鏈裡的語系不必已安裝（`Get` 只看 `lc`）。
    - `UnknownKey_Throws`。
    - `AsI18n_InstalledLocales`：出廠 → 四個鍵、值等於基準檔四語；裝 `ja`（上層 `en-US`）並刷新 registry → 五個鍵、`ja` 等於英文；停用 `ja`（政策 `locale.enabled` 不含它）→ 仍有 `ja`（M3：已安裝即產生）。
    - `AsI18n_RefreshMidway_UsesOneSnapshot`（第二輪必修 4）：裝 `ja`（上層 `en-US`）、預設 `zh-TW`；`BeforeLocaleForTests` 在第一次被呼叫時把 `ja` 的上層改成 `th`、另裝 `ko`、預設改 `vi`，並刷新 registry 與政策（`ReplaceCurrentForTests`）。這次 `AsI18n` 的結果：語系集合＝刷新前的五個（沒有 `ko`），`ja` 是英文；緊接著再呼叫一次（不掛鉤）→ 有 `ko`、`ja` 是泰文。另驗 `registry.ContextsFor(d)` 每個 `lc.Fallbacks` 等於同一時刻 `registry.FallbacksFor(code, d)`。
    - `BuiltinInstance_IgnoresRegistry`：`BuiltinMessageCatalog.Instance.AsI18n(...)` 只有四語；`Get(new LocaleContext("ja", []))` 是繁中；`Get(new LocaleContext("ja", ["en-US"]))` 是英文（照傳入的鏈，判斷 4）。
- [ ] **Step 2: 跑測試確認失敗**：`cd backend && dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~BuiltinServerTextTests|FullyQualifiedName~MessageCatalogTests"`（編譯失敗）
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；另跑 `--filter "FullyQualifiedName~MessageTextTests|FullyQualifiedName~FieldImportParserTests|FullyQualifiedName~FieldDisplayTests|FullyQualifiedName~LocaleRegistryTests"`（`ReportText` 前綴不影響既有比對）；`grep -rn "\"\(Yes\|No\|Blank\|Deleted\|NoPermission\|Title\)\"" backend/tests | grep -i reporttext` 無結果（沒有測試把舊常數值當字面字串用）。
- [ ] **Step 5: Commit**（`feat(i18n): IMessageCatalog 與後端訊息內建層，ReportText 鍵加 report. 命名空間`）

**驗收**：Step 4 全過；`MessageCatalog` 不依賴 `IServiceProvider`、不讀資料庫（建構子只有 `LocaleRegistry`、`CachedSystemPolicy`）；`git diff <Step 0 的 commit> -- backend/tests/DynamicModules.ApplicationTests/Common/ServerTextBaseline/` 無變化（基準檔建立後不准改）。

### Task 2: 回應解析點與 WebAPI 例外訊息改走 catalog

**Files:**
- Modify: `Application/Common/MessageRef.cs:33-41`（加 catalog 多載；舊多載改走 `BuiltinMessageCatalog.Instance`；`TranslateLiteral` 的查表改用 `MessageText.KeyOfZh`）
- Modify: `Application/Common/ServiceResult.cs:20-38`（加 `MessageIn(IMessageCatalog, LocaleContext)`、`ErrorsIn(IMessageCatalog, LocaleContext)`；`Message`／`Errors` 註解改「繁中，只給 log 與測試」）
- Modify: `WebAPI/Controllers/ServiceResultExtensions.cs:13-16,24-25,37`（判斷 5）
- Create: `WebAPI/Errors/RequestMessages.cs`（判斷 6）
- Modify: `WebAPI/Errors/ConcurrencyExceptionHandler.cs:15`、`DatabaseBusyExceptionHandler.cs:18`、`DatabaseUnwritableExceptionHandler.cs:31`、`UnhandledExceptionHandler.cs:84`、`WebAPI/Security/SessionMiddleware.cs:18,52,59`
- Test: `ApplicationTests/Common/ServiceResultCatalogTests.cs`（新）、`WebAPITests/Dynamic/ServerMessageChainApiTests.cs`（新，`Category=Postgres`）、`WebAPITests/RequestMessagesTests.cs`（新，純記憶體）

**Interfaces:**
- `public string Resolve(IMessageCatalog messages, LocaleContext lc, bool translateLiteral = false)`（`MessageRef`）
- `public string? MessageIn(IMessageCatalog messages, LocaleContext lc)`、`public IReadOnlyDictionary<string, string[]> ErrorsIn(IMessageCatalog messages, LocaleContext lc)`（`ServiceResult`）
- `public static string Text(HttpContext ctx, string key, params object[] args)`（`RequestMessages`；`public` 才測得到，判斷 6）

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
    - `Factory_FourLocales_Unchanged`：不裝 `ja`，四語各取一次不存在的模組，`message` 等於基準檔 `ModuleNotFound` 該語系的範本（判斷 14；不比靜態 `Get`）。
    - `Catalog_SingletonUnderScopeValidation`（第二輪建議 3）：`TestApp` 跑在 Development 環境，host 建置時已開 `ValidateScopes`、`ValidateOnBuild`（實作時確認；若沒開，這個案例自己用 `WithWebHostBuilder` 加 `UseDefaultServiceProvider(o => { o.ValidateScopes = true; o.ValidateOnBuild = true; })`）；從 root provider 解析 `IMessageCatalog` 不丟例外，兩個 scope 拿到同一個實例，且就是 `MessageCatalog`。
  - `RequestMessagesTests`（第二輪建議 3）：
    - `NoRequestServices_BuiltinZh`：`new DefaultHttpContext()`（`RequestServices` 是 null）→ `Text(ctx, Forbidden)` 等於基準檔繁中，不丟例外。
    - `DisposedServices_BuiltinZh`：`RequestServices` 設成一個已 `Dispose` 的 scope 的 provider → 同上，不丟 `ObjectDisposedException`。
    - `WithServices_UsesRequestLocale`：services 裡的 `ICurrentLocale` 回 `en-US` → 英文。
- [ ] **Step 2: 跑測試確認失敗**：`cd backend && dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~ServiceResultCatalogTests"`；`dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~ServerMessageChainApiTests|FullyQualifiedName~RequestMessagesTests"`
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
- Modify: `Application/Dynamic/ApprovalService/ApprovalQueryHandlers.cs:202,366,419-422,430-435`（判斷 8 的 `FallbackDefinition`；名稱先取實例的 `NameSnapshot`）
- Modify: `Application/Common/MessageText.cs:484-486`（刪 `AsI18n`）
- Modify（測試預期改呼叫方法）：`ApplicationTests/Dynamic/Approvals/ApprovalAddSignTests.cs:96`、`ApprovalProgressGraphTests.cs:273`
- Test: `ApplicationTests/Common/MessageCatalogTests.cs`（加案例）、`ApplicationTests/Dynamic/Approvals/ApprovalProgressGraphTests.cs`（加案例）、`WebAPITests/Dynamic/ServerMessageChainApiTests.cs`（加案例）

**Interfaces:**
- `public static I18nText FallbackStageName(IMessageCatalog m)`、`PickLabelI18n(IMessageCatalog m)`、`AddSignStageName(IMessageCatalog m)`、`ManagerLabelI18n(IMessageCatalog m, int level)`（`ApprovalMessages`）
- `ResolveContext(..., LocaleContext? NoteLocale = null, IMessageCatalog? Messages = null)`
- `private static ApprovalDefinition FallbackDefinition(I18nText stageName)`；`ApprovalViews.Instance(..., I18nText fallbackStageName)`

- [ ] **Step 1: 寫失敗的測試**
  - `MessageCatalogTests.AsI18n_Builtin_SameAsBefore`：出廠時四個簽核鍵（含 `ManagerLabel` 帶 `2`）的 `catalog.AsI18n` 等於「基準檔四語範本各自 `string.Format(InvariantCulture, 範本, args)`」組成的 `I18nText`（即刪掉的舊 `MessageText.AsI18n` 在 main 的結果；判斷 14，不呼叫改動後的 `MessageText.Get`）。
  - `MessageCatalogTests.ApprovalLabels_FollowInstalled`：裝 `ja` 後 `ApprovalMessages.FallbackStageName(catalog)` 含 `ja` 鍵；同一個 catalog 在移除 `ja`、registry 刷新後再呼叫就不含（證明不是型別初始化時算一次）。
  - `ServerMessageChainApiTests.FallbackApproval_SnapshotHasInstalledLocales`：裝 `ja`（上層 `en-US`）後送出一張只會走備援的簽核（照 `ApprovalProgressGraphTests` 的備援情境造資料），讀簽核進度：備援關卡名稱在 `X-Locale: ja` 時是英文，資料庫裡 `NameSnapshot` 含 `ja` 鍵；未安裝的 `ko` 不在裡面。
  - `ApprovalProgressGraphTests.Progress_Fallback_NodeNameFromSnapshot`（第二輪必修 5）：沿用 `Progress_Fallback_SequenceFlow_StartFallbackEnd` 的情境，把備援關卡的 `NameSnapshot` 直接改成 `{ zh-TW: "快照名稱" }` 後讀進度 → 圖上 `_fallback` 節點名稱等於時間軸那一關的名稱（都是「快照名稱」）；`NameSnapshot` 改成 null → 節點名稱回到 `ApprovalMessages.FallbackStageName(catalog)`。
  - `ServerMessageChainApiTests.FallbackGraph_AfterChainChange_SameAsTimeline`（第二輪必修 5）：裝 `ja`（上層 `en-US`）、送出走備援的簽核；之後用 `LocaleTestKit` 把 `ja` 的上層改成 `th`（同時更新 `ModifiedUtc`）並刷新 registry；`X-Locale: ja` 讀簽核進度 → 圖上備援節點名稱＝時間軸備援關卡名稱＝英文（建立當時的快照），不是泰文。
- [ ] **Step 2: 跑測試確認失敗**：`cd backend && dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~MessageCatalogTests|FullyQualifiedName~ApprovalProgressGraphTests"`（編譯失敗：方法還不存在）
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；ApplicationTests `--filter "FullyQualifiedName~Approvals"`；WebAPITests `--filter "FullyQualifiedName~ServerMessageChainApiTests|FullyQualifiedName~Approval"`；PostgreSQL：`FORMWRIGHT_TEST_DB=postgres dotnet test tests/DynamicModules.WebAPITests --filter "Category=Postgres&FullyQualifiedName~ServerMessageChainApiTests"`
- [ ] **Step 5: Commit**（`feat(i18n): AsI18n 改對已安裝語系產生，簽核的固定名稱改成方法`）

**驗收**：Step 4 全過；`grep -rn "static readonly I18nText" backend/src/DynamicModules.Application/Dynamic/ApprovalService` 無結果；`grep -rn "MessageText.AsI18n" backend/src backend/tests` 無結果。

### Task 4: `TaskReportFields` 的四語表搬進 `ReportText`

**Files:**
- Modify: `Domain/Reports/TaskReportFields.cs:42-125,130-160`（判斷 9：加 `TextKeys`、`BlankLabelKeys`、`DatasetNameKeys`、`AllTextKeys`；`Build` 加 `Func<string, I18nText> text`；刪 `T(...)` 與四語字面值）
- Modify: `Application/Dynamic/ReportService/Data/ReportText.cs`（`Table` 加 41 列，鍵引用 `TaskReportFields.TextKeys`，文字逐字搬自 Domain；狀態四列帶 K2 註解）
- Modify: `Application/Dynamic/ReportService/TaskReportSource.cs:20-21,46`（注入 catalog，`Build(..., k => messages.AsI18n(k))`，判斷 9）
- Modify: `Application/Dynamic/ReportService/Queries/GetReportCatalog/GetReportDatasetsHandler.cs:10-14`（注入 catalog，`messages.Get(requestLocale.For(...), DatasetNameKeys[d])`）
- Modify: `Application/Dynamic/ReportService/ReportRunner.Tasks.cs:70-71`（空值字 `messages.Get(lc, F.BlankLabelKeys[id])`；`ReportRunner` 建構子注入 catalog，Task 6 沿用）
- Modify: `DomainTests/Reports/TaskReportFieldsTests.cs:12-17,39,77-97`（改用 `text` 函式與 `BlankLabelKeys`；`:39` 模組名與 `Labels_FourLocales` 改驗解析函式的回傳值）
- Modify: `ApplicationTests/Common/BuiltinServerTextTests.cs`（`Table_EqualsBaselines` 的清單加 `task-report.json`）
- Create: `ApplicationTests/Dynamic/Tasks/TaskReportTextTests.cs`、`ApplicationTests/Dynamic/Tasks/task-report-texts.verified.json`、`ApplicationTests/Common/ServerTextBaseline/task-report.json`（後兩個在 Step 0 產生；測試以 repo 相對路徑讀，照 `BuiltinLocaleResourcesTests` 的讀法）

**Interfaces:**
- `public static class TaskReportFields.TextKeys { public const string FieldTitle = "report.TaskFieldTitle", …; }`（41 個）
- `public static IReadOnlyDictionary<long, string> BlankLabelKeys { get; }`、`public static IReadOnlyDictionary<string, string> DatasetNameKeys { get; }`、`public static IReadOnlyList<string> AllTextKeys { get; }`
- `public static Module Build(string dataset, IReadOnlyList<Department> departments, IReadOnlyList<Module> modules, IReadOnlyList<TaskRule> rules, Func<string, I18nText> text)`

- [ ] **Step 0: 先產快照與範本基準**（搬表前，單獨 commit）：在 `TaskReportTextTests` 寫一個暫時的 `Dump` 測試，用**現行** API 產生兩份檔：
  - `task-report-texts.verified.json`（輸出快照）：`Build(Tasks)`、`Build(TaskEvents)`、`Build("foo")`（不認得的資料來源：名稱是空的 `I18nText`、沒有欄位）三個虛擬模組的模組名、全部欄位名、全部選項標籤（固定選項與傳入的部門、模組、規則實體選項都列，實體選項驗「名稱原樣帶過」），加 `BlankLabels`、`DatasetNames`；依（資料來源、欄位代碼、選項 Id、語系）排序，每個語系存 `Values` 裡的原值（不經 `Resolve`，缺的語系也看得出來）。
  - `ServerTextBaseline/task-report.json`（範本基準，判斷 14）：`Dump` 裡先寫好「位置 → 預定鍵」對照表（欄位名 18、狀態 4、來源 3、完成狀況 4、層級 3、事件種類 2、空值字 5、資料來源名 2），鍵名就是之後 `TextKeys` 的值；四語取自現行 Domain 的字面值。
  - 核對：對照表 41 個位置、41 個不同的鍵；快照裡固定文字的位置數四語各是 41（少了就停下回報）。跑一次、人工抽看幾句、刪掉 `Dump`，commit 兩個檔（`test(i18n): 任務報表欄位文字快照與範本基準（搬表前）`）。此時 `Table_EqualsBaselines` 的清單還沒加這個檔，仍是綠的。
- [ ] **Step 1: 寫失敗的測試**
  - `BuiltinServerTextTests.Table_EqualsBaselines`：清單加 `task-report.json`（判斷 14；搬表前是紅的）。
  - `TaskReportTextTests.BuiltinLabels_MatchSnapshot`：用新 API（`Build(..., k => BuiltinMessageCatalog.Instance.AsI18n(k))`、`BlankLabelKeys`／`DatasetNameKeys` 經 catalog 四語各取一次）序列化成同一形狀，必須與快照**逐字相同**（含 `Build("foo")` 與實體選項）。
  - `TaskReportTextTests.EveryTextKey_InCatalog`：`AllTextKeys` 每個都在 `BuiltinServerText.Table`、都以 `report.Task` 開頭、共 41 個。
  - `TaskReportTextTests.Ja_LabelsFollowChain`：記憶體 catalog 裝 `ja`（上層 `en-US`）→ 虛擬模組欄位「狀態」的 `LabelI18n` 含 `ja` 鍵、值是 `Status`。
  - `TaskReportFieldsTests`（Domain，第二輪必修 1）：既有 7 個案例改傳 `Text = k => new I18nText([KeyValuePair.Create("zh-TW", k)])`（只有繁中、值為鍵）。因此要同步改的斷言：
    - `Build_Tasks_FieldsNegativeAndTyped` 的 `m.NameI18n.Resolve("zh-TW").Should().Be("任務")` → `Be(F.DatasetNameKeys[ReportDatasets.Tasks])`（驗「模組名來自解析函式」，原文「任務」由 Application 的快照驗）。
    - `Labels_FourLocales` 改名 `Labels_FromResolver`：每個欄位的 `LabelI18n` 等於 `Text(對應的 TextKeys)`、每個固定選項的標籤等於 `Text(它的鍵)`、實體選項的名稱原樣帶過；`BlankLabels` 那兩行改成驗 `BlankLabelKeys` 的鍵集合與 `BlankLabelKeys[F.Assignee] == F.TextKeys.BlankAssignee`；`Restrictable` 那行不動。四語非空的檢查移到 Application（`EveryKey_NonBlankInFourLocales` 已涵蓋 `report.Task*`，加上 `BuiltinLabels_MatchSnapshot`）。
    - 其他 5 個案例只驗結構與 Id，不受影響。
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
- **不動** `ReportText.Get`（`ReportText.cs:68-74`）：Task 6 之後只剩 `FieldImportParser.IsDisplayed` 用它，Task 8 改完才刪（第二輪必修 3）。
- Test: `ApplicationTests/Dynamic/FieldDisplayTests.cs`（加案例）、`WebAPITests/Dynamic/ServerMessageChainApiTests.cs`（加案例）

- [ ] **Step 1: 寫失敗的測試**
  - `FieldDisplayTests.Boolean_Ja_FollowsChain`（第二輪必修 8）：`new FieldDisplayContext("ja", Taipei, ["en-US", "zh-TW"], catalog)` → `Yes`；不帶 catalog（null，判斷 4 的內建 catalog）也照傳入的鏈 → 同樣是 `Yes`（內建 catalog 只是不看 registry，鏈照用）；`new FieldDisplayContext("ja", Taipei, ["zh-TW"])`（鏈只有繁中、null catalog）→ 「是」；`new FieldDisplayContext("ja", Taipei)`（沒有鏈）→ 「是」。`Reference_Deleted_Ja_FollowsChain` 同理（`Deleted`／`NoPermission`）。
  - 注意：main 的 `FieldDisplay.Boolean` 只看 `Locale`、不看 `LocaleFallbacks`，所以「測試直接 `new` 帶英文鏈的 `ja`」在 main 是「是」、改後是 `Yes`。正式路徑的 `ja` 在 main 一律繁中、改後走鏈，正是本段要的行為（U3）；內建四語不受影響（四語自己有字）。
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

**驗收**：Step 4 全過；`grep -rln "ReportText.Get(" backend/src` 只剩 `ReportText.cs` 本身與 `ImportService/FieldImportParser.cs`（Task 8 處理）；`grep -rn "\.Display(" backend/src | grep -v "messages"` 無結果（正式呼叫端都傳 catalog）。

### Task 7: 警示信與事件標題改走 catalog（Q1）

**Files:**
- Modify: `Application/Dynamic/OpsService/Alerts/AlertMailText.cs`（`:31` 主表、`:89` `Labels`、`:132` `CheckNames` 開 `Entries`，鍵照判斷 3；`Locale(LocaleContext)`（`:147`）、`Get`（`:150`）、`Label`（`:157`）、`CheckName`（`:159`）改成收 `IMessageCatalog` 與 `LocaleContext`；`Label`／`CheckName` 對未知代碼的退路照舊（判斷 3）；`Locales`（`:15`）只有 `Locale(...)`（`:148`）用，隨它一起刪）
- Modify: `Application/Common/Localization/BuiltinServerText.cs`（合併 `alert.*`）
- Modify: `Application/Dynamic/OpsService/Alerts/AlertMailComposer.cs`：五個公開入口 `Incident`（`:23`）、`Direct`（`:46`）、`Digest`（`:72`）、`Test`（`:91`）、`MailTest`（`:98`）多收 `IMessageCatalog`，`locale` 字串改成 `LocaleContext`；私有的 `Line`、`FitBody`（用 `TooLong`）同步改（第二輪建議 2）
- Modify: `Application/Dynamic/OpsService/Incidents/IncidentTitles.cs:61-79`（`Snapshot`、`Text` 同）
- Modify（呼叫端注入 catalog）：`AlertDelivery.cs:30,79,82`、`AlertOutboxWriter.cs:53`、`AlertChannelHandlers.cs:141`、`DirectAlertQueue.cs:164`、`OpsNotifier.cs:41`、`Application/Dynamic/MailService/MailSettingsHandlers.cs:140`
- Create（Step 0，改動前）：`ApplicationTests/Common/ServerTextBaseline/alert.json`、`ApplicationTests/Dynamic/Ops/alert-mail.verified.json`
- Test: `ApplicationTests/Common/BuiltinServerTextTests.cs`（清單加 `alert.json`、加案例）、`ApplicationTests/Dynamic/Ops/AlertMailTextTests.cs`（新，純函式：composer 與標題直接帶記憶體 catalog）、`WebAPITests/Ops/{AlertChannelTests,IncidentTests}.cs`（調整 `AlertMailText`／`IncidentTitles` 的呼叫；`AlertKit`、`OpsIncidentKit` 若有引用一併改）

- [ ] **Step 0: 先產範本基準與信件快照**（改動前，單獨 commit；第二輪必修 2）：暫時的 `Dump` 測試用**現行** API 產生：
  - `ServerTextBaseline/alert.json`：主表每個常數 `alert.<常數名>`（`AlertMailText.Get(l, key)` 不帶參數）、`Labels` 每個代碼 `alert.label.<代碼>`（`Label(l, 代碼)`）、`HealthCheckCatalog.All` 每個檢查 `alert.check.<代碼>`（`CheckName(l, 代碼)`），四語。主表與 `Labels` 是 private，`Dump` 以反射讀它們的鍵（只在這個暫時測試裡這樣做）。
  - `alert-mail.verified.json`（輸出快照，四語 × 下列每一案的主旨與內文，固定時間與編號）：
    - `Incident`：每個嚴重度（info／warning／critical）、每個類別（infra／security／defect／config）× 兩種 edition 讓 `handler.*` 五種都出現、每種轉換（opened／regressed／escalated／renotify／occurred）各至少出現一次；有連結、沒連結各一；標題超過 `TitleMaxInBody` 一案、內文超過上限觸發 `FitBody` 的 `TooLong` 一案。
    - `Direct`：同上的嚴重度、類別、轉換涵蓋，有連結、沒連結各一。
    - `Digest`：0 則、1 則、超過 `DigestMaxLines`（出現 `DigestMore`）、單行超過 `DigestLineMax` 被截斷、`Category` 為 null（顯示 `-`）、`IncidentId` 為 null 各一。
    - `Test`、`MailTest` 各一。
    - `IncidentTitles.Text`：`ServerError`、`ClientError` 各有路由與沒路由；`AdminChanged`、`PrivilegeChanged` 的 granted／revoked；`ChannelFailed` 每個 `stage.*`，有、沒有 `serverCode`；`RulePaused`；`config.<代碼>` 每個 `ruleError.*` 加一個未知代碼；`health.<代碼>` 每個檢查 × 每個 `status.*`，加一個未知檢查代碼；未登記的標題代碼（回代碼本身）。
    - `Label` 未知代碼（例 `severity.foo` → `foo`）、`CheckName` 未知代碼；`IncidentTitles.Snapshot` 超過 200 字截斷一案。
  - 核對：`Dump` 另算「快照輸出中出現過的 `alert.*` 鍵」（以每個鍵的四語範本去參數後比對）與 `alert.json` 的鍵集合相同，漏了就補案例；跑一次、人工抽看、刪掉 `Dump`，commit 兩個檔（`test(i18n): 告警信文字範本基準與信件快照（改動前）`）。
- [ ] **Step 1: 寫失敗的測試**
  - `BuiltinServerTextTests.Table_EqualsBaselines`：清單加 `alert.json`。
  - `BuiltinServerTextTests.AlertKeys_Prefixed`：`alert.` 鍵數＝主表＋代碼名稱＋健康檢查名稱；`alert.check.<代碼>` 的繁中等於 `HealthCheckCatalog` 的 `ZhName`。
  - `AlertMailTextTests.AlertMail_Builtin_SameAsBefore`：用新 API、`BuiltinMessageCatalog.Instance` 與四語各自的 `LocaleContext` 重跑 Step 0 的每一案，序列化結果與 `alert-mail.verified.json` 逐字相同。
  - `AlertMailTextTests.AlertMail_Ja_ParentEnglish`：信件語系 `ja`（上層 `en-US`）→ 主旨、內文、代碼名稱、健康檢查名稱都是英文（行為同現在的「挑第一個有翻譯的」，但改成逐句走鏈）。
  - `AlertMailTextTests.UnknownCodes_KeepFallback`：未知的 `Label` 代碼、`CheckName` 代碼、標題代碼，結果同快照（不丟 `KeyNotFoundException`）。
  - `AlertMailTextTests.IncidentSnapshot_UsesSystemDefault`：`IncidentTitles.Snapshot` 仍用系統預設語系（N1），出廠是繁中、逐字同 o2b。
- [ ] **Step 2: 跑測試確認失敗**：`cd backend && dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~BuiltinServerTextTests|FullyQualifiedName~AlertMailTextTests"`
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；WebAPITests `--filter "FullyQualifiedName~Ops"`（含 `AlertChannelTests`、`IncidentTests`、`RulePauseTests`）
- [ ] **Step 5: Commit**（`feat(i18n): 告警信與事件標題的文字併入 IMessageCatalog（alert. 命名空間）`）

**驗收**：Step 4 全過；看門狗直寄路徑（`DirectAlertQueue`）不因 catalog 讀資料庫（catalog 只讀記憶體快照，判斷 1）；`grep -rn "AlertMailText.Locales" backend/src` 無結果。

### Task 8: 匯入「是否」欄位接受已安裝語系的是／否文字（Q2）

**Files:**
- Modify: `Application/Dynamic/ImportService/FieldImportParser.cs:19-22`（`ImportLookups` 加 `BooleanWords Words`）、`:72-83`（`Boolean` 改用 `lookups.Words`；`IsDisplayed` 與四個固定別名的 `Equals` 刪除，固定別名搬進 `BooleanWords`）
- Modify: `Application/Dynamic/ImportService/ImportPlanner.cs:90`（注入 catalog 與 `LocaleRegistry`；用判斷 2 的 `registry.ContextsFor(policy.DefaultLocale)` 取每個已安裝語系（含停用）的 `lc`，`messages.Get(lc, ReportText.Yes／No)` 組 `BooleanWords`）
- Modify: `Application/Dynamic/ReportService/Data/ReportText.cs:68-74`（刪 `Get`：本 Task 之後沒有呼叫端）
- Modify: `ApplicationTests/Dynamic/FieldImportParserTests.cs:138,153,179`（`new ImportLookups(...)` 補參數，用 `BooleanWords.Builtin`）、`:81-82`（四語匯出字的輸入改從 `BuiltinMessageCatalog.Instance.Get(new LocaleContext(locale, []), ReportText.Yes／No)` 取；預期值 true／false 不變）
- Test: `ApplicationTests/Dynamic/FieldImportParserTests.cs`（加案例）、`WebAPITests/Dynamic/ServerMessageChainApiTests.cs`（加案例）

**Interfaces:**
- `public sealed record BooleanWords`（放在 `FieldImportParser.cs`，與 `ImportLookups` 同檔）`{ public static BooleanWords From(IEnumerable<(string Yes, string No)> perLocale); public static BooleanWords Builtin { get; } public bool? Parse(string text); }`（第二輪必修 6）：
  - 兩個集合都用 `StringComparer.OrdinalIgnoreCase`。`Yes`＝固定別名「是、true、yes、1」∪ 每個語系的 `report.Yes`；`No`＝固定別名「否、false、no、0」∪ 每個語系的 `report.No`。
  - `Ambiguous`＝`Yes ∩ No`，**在兩個集合都併完固定別名之後才算**。`Parse`：在 `Ambiguous` → null（無法判斷）；在 `Yes` → true；在 `No` → false；都不在 → null。先判撞字再解析，所以固定別名撞到也會被拒（某語系把「否」翻成 `Yes`，`yes`／`Yes` 都不再判 true）。
  - `Builtin`＝`From` 內建四語經 `BuiltinMessageCatalog.Instance` 取的字（不呼叫 `ReportText.Get`，它在本 Task 刪除）；`FieldImportParser.Boolean` 遇 null 回既有的「請輸入「是」或「否」」。

- [ ] **Step 1: 寫失敗的測試**
  - `FieldImportParserTests`：
    - `Boolean_InstalledLocaleWords`：`BooleanWords.From` 內建四語加一組 `("はい","いいえ")` → 「はい」是 true、「いいえ」是 false、`ハイ` 不認得；內建四語的字照常。
    - `Boolean_AmbiguousAcrossLocales_Rejected`：兩組裡同一個字一個當 Yes、一個當 No → 回「請輸入「是」或「否」」。
    - `Boolean_AmbiguousWithFixedAlias_Rejected`（撞固定別名）：一組 `("はい", "Yes")` → `Yes`、`yes` 都被拒，`true`、`1`、`是` 仍是 true，`No`、`否`、`0` 仍是 false；一組 `("はい", "是")` → `是` 被拒，`yes` 仍是 true；一組 `("はい", "1")` → `1` 被拒，`0` 仍是 false。
    - `Boolean_FixedWordsStillAccepted`：沒有撞字時 true／false、yes／no、1／0、是／否照常（大小寫不拘，同現行）。
  - `ServerMessageChainApiTests`（第二輪必修 7；`ja` 上層 `en-US`）：
    - 測試 catalog：用 `TestApp.ExtraServices` 把 `IMessageCatalog` 換成包住真的 `MessageCatalog` 的測試 decorator：`lc.Locale == "ja"` 時 `report.Yes`／`report.No` 回「はい／いいえ」，其他全部交給真的 catalog（`AsI18n` 照真的，`ja` 那格再套同一條規則）。
    - `Import_Ja_RoundTrip`：`ja` 使用者（`X-Locale: ja`）匯出含是否欄的記錄 CSV，**先斷言 CSV 的是否欄真的是「はい」「いいえ」**（證明匯出走 catalog，且不是內建四語本來就認得的字），再以 `ja` 使用者原檔匯入：筆數與值不變。
    - `Import_DisabledInstalledLocale_StillAccepted`：同一個測試 catalog，政策把 `ja` 從 `locale.enabled` 拿掉（仍已安裝）；繁中使用者匯入手寫含「はい」「いいえ」的 CSV → 照樣判成 true／false（Q2「所有已安裝語系」）。
    - `Import_Ambiguous_Rejected`：測試 catalog 改成 `ja` 的 `report.No` 回 `Yes` → 匯入含 `Yes` 的 CSV，該格錯誤是「請輸入「是」或「否」」，含 `true` 的格照常。
- [ ] **Step 2: 跑測試確認失敗**：`cd backend && dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~FieldImportParserTests"`；`dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~ServerMessageChainApiTests"`
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；ApplicationTests `--filter "FullyQualifiedName~Import|FullyQualifiedName~BuiltinServerTextTests|FullyQualifiedName~MessageCatalogTests"`（確認刪 `ReportText.Get` 不影響基準測試）；WebAPITests `--filter "FullyQualifiedName~Import|FullyQualifiedName~ServerMessageChainApiTests"`；PostgreSQL：`ServerMessageChainApiTests`
- [ ] **Step 5: Commit**（`feat(i18n): 匯入是否欄位接受各已安裝語系的是／否文字`）

**驗收**：Step 4 全過；`grep -rn "ReportText.Get(" backend/src backend/tests` 無結果，`ReportText.Get` 已刪。

### Task 9: 剩餘清單、文件回填、gate

**Files:**
- Modify: `docs/superpowers/specs/2026-11-17-language-packs-design.md`：
  - §5.1 `server` 段：補「`AlertMailText` 的鍵加 `alert.` 前綴（代碼名稱 `alert.label.`、健康檢查名稱 `alert.check.`）」（Q1）。
  - §9.1：補一行「實作：`Get` 收 `LocaleContext`（i4c 計畫判斷 1）；`AsI18n` 的語系取已安裝清單，整批讀同一份快照（判斷 2）；`ApprovalViews.FallbackDefinition` 一併改方法，名稱先用實例的快照（判斷 8）」。
  - §14：本計畫「寫死清單」的新項目。
  - §15：「後端訊息第一步只有改過的呼叫點…（i4c 收尾時列出剩餘清單）」那條改成本計畫「剩餘清單」；「警示信語言…（i4c 再定）」改成「i4c 起信件逐句沿退回鏈取」；`FieldImportParser` 那條改成「匯入接受各已安裝語系的是／否文字，撞字（含固定別名）時拒絕」；加「i4c 交付時的已知限制」（下面「已知限制」節）。
  - §18 第 9 點標「（i4c 完成）」；第 11 點補「i4c 完成：`backend.md`、`AGENTS.md`、`MessageText`／`ReportText` 註解」。
  - §20：列表加「**i4c 計畫**：[i4c 計畫](../plans/2026-11-17-language-packs-i4c.md)」（若規劃者已在同一 commit 加過就只補「已上線」）。
- Modify: `.claude/rules/backend.md:14`：「四語字串表在 `Application/Common/MessageText.cs`」改成「後端訊息經 `IMessageCatalog`（`Application/Common/Localization/`）沿退回鏈解析；內建層是 `MessageText`、`ReportText`（鍵 `report.`）、`AlertMailText`（鍵 `alert.`）的 C# 四語表，鍵是語系包契約、改名要留別名；新程式不直接呼叫靜態 `Get`」。
- Modify: `AGENTS.md` 架構地圖 `Application` 那條補「後端訊息走 `Common/Localization/IMessageCatalog`」。
- Modify: `docs/feature.md:294`：補「i4c 已實作（後端訊息改走 `IMessageCatalog`）」，**不改核取狀態**（主控收尾時處理）；跑 lifecycle 的 `feature_checklist.py docs/feature.md --write`（找不到腳本就照 `.claude/rules/docs.md` 回報「統計行待重算」）。
- Modify: `docs/manual.md:622`：補「其他已安裝語言匯出的是／否文字也能匯回」（Q2）。其餘手冊不改：內建四語行為不變，新語系的安裝入口在 i4d1。
- 重產鏡像：`node .claude/scripts/sync-codex-mirror.mjs`，`.agents/rules/backend.md` 一起 commit。
- 票：#66 留言（判斷 11）；開 backlog 票「i4d1 語系包驗證器拒絕 `report.Yes`／`report.No` 與固定別名或其他已安裝語系撞字的語系包」（「已裁決」節第二輪分岔的推薦做法；主控若改選「新增訊息」則改成本段 Task 8 的工作，不開這張票）；本段審查與實測發現的新待辦用 `tickets:ticket-create` 開 backlog 票。

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
#   DatabaseBusyException.cs:9、WorkflowEnforcer.cs:17-18、ApprovalMessages.cs 的 ManagerLabel／PickLabel 兩行（ReportText.Get 已在 Task 8 刪除）
# S2 簽核沒有型別初始化時算好的多語名稱
grep -rn "static readonly I18nText" backend/src/DynamicModules.Application/Dynamic/ApprovalService   # 無結果
grep -rn "MessageText.AsI18n" backend/src backend/tests                                               # 無結果
# S3 Domain 沒有四語字面值
grep -rn "\"en-US\"\|\"th\"\|\"vi\"" backend/src/DynamicModules.Domain/Reports                       # 無結果
# S4 正式程式不直接用內建 catalog（只允許 null 退路與 ServiceResult 的繁中屬性）
grep -rn "BuiltinMessageCatalog.Instance" backend/src | grep -v "?? BuiltinMessageCatalog.Instance\|Localization/BuiltinMessageCatalog.cs\|ServiceResult.cs\|MessageRef.cs\|RequestMessages.cs\|FieldImportParser.cs"   # 無結果（FieldImportParser.cs 只有 BooleanWords.Builtin）
# S5 #66 的 11 行原樣（判斷 11，不併入）
grep -rn "Resolve(I18nText.FallbackLocale)" backend/src | wc -l                                       # 11
# S6 範本基準與輸出快照建立後沒被改過（判斷 14）：每個檔在分支上只有它自己 Step 0 那一個 commit
for f in backend/tests/DynamicModules.ApplicationTests/Common/ServerTextBaseline/*.json $(git ls-files '*.verified.json'); do echo "$(git log --format=%h main..HEAD -- "$f" | wc -l) $f"; done   # 每行都是 1
```

## 預估規模

| 批 | Task | 新檔 | 改檔 | 手寫行數（估，含測試） |
|---|---|---|---|---|
| i4c-1 | 1 | 7（4 正式、2 測試、1 基準檔） | 4 | ≈ 400（測試約 240） |
| | 2 | 4 | 8 | ≈ 330（測試約 210） |
| | 3 | 0 | 10 | ≈ 250（測試約 130） |
| | 4 | 3（含快照與基準檔） | 8 | ≈ 330（測試約 140；另有約 90 行逐字搬移的文字列） |
| | 小計 | 14 | 30 | ≈ 1310 |
| i4c-2 | 5 | 0 | 6 | ≈ 170（測試約 90） |
| | 6 | 0 | 13 | ≈ 410（測試約 160） |
| | 7（Q1） | 3（測試、快照、基準檔） | 12 | ≈ 420（測試約 230） |
| | 8（Q2） | 0 | 5 | ≈ 190（測試約 120） |
| | 9 | 0 | 7 | ≈ 80 |
| | 小計 | 3 | 43 | ≈ 1270 |
| 合計 | | 17 | 約 73 | ≈ 2580，其中測試約 50% |

兩批都超過單批約 800 行的指引，主因是測試（每條改過的路徑都要一個 `ja` 走鏈的 API 案例，加上內建四語逐字不變的守衛）。基準檔、快照 JSON 與 Step 0 的暫時 `Dump` 測試不計入（`Dump` 跑完即刪）。第二輪比第一輪多約 370 行，幾乎都是測試（基準比對、快照分支、刷新插入、撞字與 round-trip）。Task 6 若超過 500 行就拆兩個 commit（`ReportRunner*`／其餘）；Task 7 同理（`AlertMailComposer`／`IncidentTitles` 與呼叫端）。

## 已知限制（i4c，交付時寫回設計 §15）

- 本段之後仍只有內建層：新語系的後端訊息全部是退回鏈上的內建語系（上層或預設語系），要等 i4d1 匯入語系包才有自己的句子。
- `AsI18n` 寫進資料的名稱（簽核備援關卡、加簽關卡、主管第 N 層、發起人自選），新語系存的是當時沿鏈補齊的句子；之後補了翻譯，已存的不會跟著改（判斷 2，設計 §15 已列的同類限制）。
- 剩餘清單第 3、4 項的繁中字串只給 log 與測試，使用者看不到；第 5、6 項（管理類訊息、#66、匯入的其他錯誤訊息）新語系的使用者仍看到繁中。
- 匯入是否欄位：語系包把是／否翻成和固定別名或其他已安裝語系相同的字時，那個字在所有使用者的匯入都被拒（Q2 撞字守衛）；同字不同義而沒撞到的擋不下。源頭的擋法在 i4d1 的語系包驗證器（Task 9 開票）。

## 自我檢查

1. **設計覆蓋（§20 i4c 列）**：`IMessageCatalog` → Task 1；解析點改 DI → Task 2；直接呼叫點改注入並列剩餘清單 → Task 5–9、判斷 10；`TaskReportFields` 走 catalog（Domain 由 Application 傳入）→ Task 4、判斷 9；`AsI18n` 改已安裝語系、3 個 `static readonly` 改方法 → Task 3、判斷 8；§15 警示信「i4c 再定」→ Q1（已裁決）、Task 7；§18 第 11 點 → Task 9。
2. **§19**：退回鏈「後端訊息與設計資料同一組案例」→ Task 1 `SharedChainCases_Walk`、`SharedChainCases_CatalogEntry`；安全那條（覆寫含 `<script>` 的 `report.Yes`）要覆寫層，屬 i4d1，本段 Task 6 先確認列印、匯出的文字來自 catalog。
3. **型別一致**：`IMessageCatalog.{Get,AsI18n}`、`MessageCatalog.{FirstInChain,BeforeLocaleForTests}`、`LocaleRegistry.ContextsFor(string)`、`BuiltinMessageCatalog.Instance`、`BuiltinServerText.{Table,BaseKeys,TryGet}`、`MessageText.{Entries,KeyOfZh}`、`ReportText.Entries`、`MessageRef.Resolve(IMessageCatalog, LocaleContext, bool)`、`ServiceResult.{MessageIn,ErrorsIn}(IMessageCatalog, LocaleContext)`、`RequestMessages.Text`、`RuleContext.Messages`、`FieldDisplayContext.Messages`、`ResolveContext.Messages`、`CachedSystemPolicy.Display(string, IMessageCatalog?)`、`ApprovalMessages.{FallbackStageName,PickLabelI18n,AddSignStageName,ManagerLabelI18n}(IMessageCatalog…)`、`ApprovalViews.FallbackDefinition(I18nText)`、`TaskReportFields.{TextKeys,BlankLabelKeys,DatasetNameKeys,AllTextKeys,Build(…, Func<string, I18nText>)}`、`BooleanWords.{From,Builtin,Parse}`、`AlertMailComposer.{Incident,Direct,Digest,Test,MailTest}(…, IMessageCatalog, LocaleContext)`。
4. **裁決**：Q1（警示信走語系包）、Q2（匯入是否欄位接受已安裝語系，固定別名一起判撞字）已定案，各是一個 Task；第二輪新分岔「撞到固定別名時的錯誤訊息」待主控確認（「已裁決」節），推薦選項不改任何 Task。

## 使用者裁決（沿用，不再問）

- 2026-11-17：U3（退回鏈：要求的語系 → 上層 → 系統預設 → 繁中），T10（第一步沿用 C# 四語表當內建層、呼叫點可分段改）、T11（C# 表搬 JSON 不排進 i4）。
- 2026-11-18：M3（一次產生多語的地方對每個已安裝語系各產生一句）。
- 2026-11-20：業務政策不寫死、權限 fail closed、驗證不只做在前端；寫進資料的系統文字用系統預設語系（`backend.md`，`IncidentTitles.Snapshot` 與簽核說明照舊）。
- 2026-11-21：Q1 警示信文字併進 `IMessageCatalog`（`alert.`）；Q2 匯入接受所有已安裝語系的是／否文字，撞字（含固定別名）當無法判斷；`Get` 收 `LocaleContext`（判斷 1）；#66 不併入；兩段切法保留。
