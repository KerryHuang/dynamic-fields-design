# 政策設定 p3 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 部門層數（A5）、營運時區（A17，含補盤 A31 各處）、預設／啟用／必填語系（A18）改成可設定；前端時區與語系改從 API 取；稽核與維運畫面的時間改照營運時區；請求沒指定語系時改用預設語系。出廠值等於現值，部署後沒人改設定時行為不變。

**Architecture:** 時區與語系要在同步程式裡用（`IBusinessTime.Now`、`ICurrentLocale.Locale`、欄位型別處理器、FluentValidation），所以把 O1 的 `CachedSystemPolicy`（singleton）演進成**全系統政策的單一快照**：一次讀齊全部全系統政策，保留 O1 的 60 秒存活、失敗快取 10 秒、讀值逾時 2 秒、讀不到時不讓請求失敗；新增同步讀值（`Get`、`BusinessOffset`、語系三項）。快照在啟動時載入、每個請求發現過期時重載（只讓一個請求跑，其他沿用舊值），系統 PUT 的交易提交後由 `AfterCommitActions` 寫入新值。讀值入口仍是 `IPolicyReader`，快照只是它的同步快取；營運時區偏移只從快照取（`IBusinessTime` 不帶偏移）。改時區時在同一個交易裡把日期時間欄位的索引平移，讓列表篩選與報表日期切點跟著新時區。前端時區與必填語系在登入後從生效值 API 取（每次導覽發現還沒載入就載）；預設與啟用語系在登入前從公開的 `GET auth/options` 取。

**Tech Stack:** .NET 8、EF Core（SQLite 研發、PostgreSQL 正式）、MediatR、FluentValidation、xUnit＋FluentAssertions；Vue 3、Quasar 2、TanStack Query、vue-i18n、vitest、bun。不新增相依套件。

**Spec:** `docs/superpowers/specs/2026-11-11-policy-settings-design.md`（§3.3 時區、語系清單型別；§3.5 時區與必填語系只管之後、改時區確認框文字；§3.7 p3 細則，含使用者 2026-11-20 的兩條裁決；§4 A5、A17、A18 與補盤 A31；§9 p3；§10 已知限制（p3））。語系包設計 `docs/superpowers/specs/2026-11-17-language-packs-design.md` §4.3、§10.1、§18（p3 先留接口，判斷 22–25）。PostgreSQL 驗證依 `docs/superpowers/specs/2026-11-12-postgres-provider-design.md` §5.5、§6.3–§6.6 與 `docs/ai/testing.md`。O1 快取見 `docs/superpowers/specs/2026-11-11-ops-observability-design.md` §10.6（主控判斷 B2）。repo 規範：`AGENTS.md`、`.claude/rules/{backend,frontend,docs}.md`（含 2026-11-20 新增的「上限與政策的通則」「語系退回」「畫面通則」「時間顯示」）。現況檢查：`.tmp/plans/20261120-p3/plan-check.md`（本機，S1–S12 與 i4 的對照）。

**路徑慣例：** 後端相對 `backend/`，前端相對 `frontend/`，`shared/` 與文件相對 repo 根目錄（2026-11-16 `0c356ba5` 已把 `poc/` 搬到根目錄）。行號以 main `e3291990` 為準；不符時以符號為準。

---

## 修訂紀錄

### 2026-11-20 第二輪（codex 審查必修 1–5 與建議；`.tmp/plans/20261120-p3/plan-review.md`）

1. 必修 1：系統 PUT 的順序改成「讀舊值 → 寫設定 → `uow.CommitAsync`（SaveChanges）→ resolve 新值 → hooks → 外層提交 → `AfterCommitActions`」（`SystemSettingRepository` 讀取是 `AsNoTracking`）；補首次設定、修改、還原出廠值、hook 拒絕四案，雙資料庫 → Task 3。
2. 必修 2：快照與序號包成不可變物件，以 `Interlocked.CompareExchange` 發布；序號在交易內（持有寫入鎖）產生，callback 只在序號較新時套用；重載與失敗分支都以發布當下的物件為基礎；補「檢查後插入」「重載失敗」「callback 逆序」三種測試 → 判斷 20、Task 3。
3. 必修 3：改時區先在交易內檢查受影響索引的上下限，超界回 400 帶出筆數與範圍，不發平移的 SQL；雙資料庫斷言設定、索引、快照都沒變 → Task 4。
4. 必修 4：盤點 `backend/src` 所有輸出用的 `Resolve(locale)` 與 `Resolve(FallbackLocale)`，逐處寫處置；Task 11 拆成 11a（輸出標籤與 `"zh-TW"` 內插）、11b（D3 的 `FallbackLocale` 那批）；補「要求 th、預設英文、資料有繁中與英文 → 英文」的 API 測試；`TaskRuleValidator.cs:130` 移到 Task 9 → Task 9、10、11a、11b。
5. 必修 5：Task 12 列出 17 個 `I18nTextInput` 入口的處置與原值來源，補父元件測試（只改其他設定可存、改文字才要求補語系、新增照擋）→ Task 12。
6. 建議：故障時沿用舊值沒有時間上限、冷啟動讀不到用出廠值，寫進設計 §10 與 `docs/deploy.md`；登出與 session 過期後重置「已載入」；Task 12 的 grep 白名單分類 → Task 3、5、12。
7. 使用者 D1–D5 裁決（主控已寫回說明）的範圍補進 Files、測試、驗收：D2 → Task 12；D3 → Task 11b；D4 → Task 10；D5 → Task 9。
8. 新的行為分岔 N1、N2 列在「待決」（不阻擋開工）。

### 2026-11-20（依現況檢查 S1–S12 與 i4 對照修訂）

1. S1：全篇 `poc/` 前綴改成 `backend/`、`frontend/`、`shared/`；行號換成 main 現值。
2. S2：補 `BusinessClock`（Domain 的 static 解析核心）與 `ReportRunner.TryWallClock` 的所有呼叫點；負偏移的下限溢位一併處理 → Task 4。
3. S3：不新建 `SystemPolicySnapshot`，改把 `CachedSystemPolicy` 演進成單一快照，保留失敗快取、2 秒逾時、退回出廠值；提交後換新改用既有的 `AfterCommitActions`，不加新的 pipeline behavior → 判斷 3、19、20，Task 3。
4. S4：登入頁登入後不重新整理就進首頁時，守衛也要載入時區與必填語系：改成「每次導覽發現已登入但還沒載入就載」，不只在第一次導覽 → 判斷 16，Task 5。
5. S5：沿用既有的 `frontend/vitest.setup.ts`，不另建 `src/test-setup.ts` → Task 5、12。
6. S6：`IsValid(out` 是 17 個呼叫端加 1 個定義；補流程圖驗證器 5 處與寫死「必須有繁體中文」的訊息 → Task 9。
7. S7：前端必填語系照前端通則「只在送出一定會被後端擋時才先擋」：新增時照必填語系擋；編輯既有資料時只擋這次改過文字的欄位 → 判斷 11，Task 12。
8. S8：使用者 2026-11-20 裁決「請求沒指定語系時用預設語系」，約 20 處寫死 `"zh-TW"` 納入 p3 → 新 Task 10（查詢參數與 handler 預設）、Task 11（訊息內插的標籤）。
9. S9：`GetAuthOptionsHandler` 的早退分支也帶語系欄位；更正「auth/options 沒有測試」（`DemoLoginApiTests` 已有）→ Task 8。
10. S10：`TaipeiBusinessTimeTests` 改寫成 `ZoneBusinessTimeTests`；`WriteGuardTests` 的測試名一併改 → Task 3。
11. S11：系統 PUT 的 handler 裡沒有稽核寫入（稽核由交易內的 `AuditBehavior` 寫），hook 放在「寫設定後、`CommitAsync` 前」→ 前置條件 6、Task 3。
12. S12：前置條件改成現況（p2、權限軸已合併）；`PgDeferred` 已移除，過濾條件拿掉它。
13. i4 預留：判斷 13 本文同步成「要求的語系 → 系統預設語系 → 繁中 → 第一個有值的」；`I18nText.Resolve` 加退回鏈參數；語系類政策的選項由一個介面提供；`IsWellFormed` 的支援語系檢查隔離在一處；不新增 `SupportedLocales` 使用點 → 判斷 13、22–25，Task 8、9、12。
14. 使用者 2026-11-20 裁決「稽核頁、維運監控頁的時間顯示和日期篩選照營運時區，時間旁標示時區」→ 新 Task 6。
15. 設計 §10 補「任務 `DueDate` 改時區後不重算」等已知限制（本次已寫回設計）。
16. Task 重新編號：p3a 是 Task 1–7、p3b 是 Task 8–13。

### 2026-11-12

依使用者 2026-11-12 裁決與 p2 計畫審查的 p3 部分修訂（逐條處置見 `../20261111-policy-settings-p2/revise-result.md`，本機）：使用者裁決 Q7–Q13 寫進判斷 1、5、6、9、10、12、14、18；審查 p3-M1（`IBusinessTime` 不加 `Offset`）、p3-S1（`PolicyJson` 補 `UtcOffset`）、p3-S2（平移與快照換新的競態）、p3-S3（已開著的瀏覽器定期比對時區）、p3-S4（`AddMinutes` 兩種資料庫實測）、p3-S5（語系類 RoleSet 訊息）、p3-N1–N4。

## 前置條件（開工前逐項確認，不符就停下回報）

1. **main 已含 p1、O1、un1、pg0、pg1、簽核 a2d–a6、O2、權限軸、p2**（p2 完成於 `6d647080`；`docs/roadmap.md:122` 的順序 `… O2 → 權限軸 → p2、p3 → i3、i4 …`）。確認方式：`git log --oneline main | grep 6d647080` 有結果；`PolicyDefinition` 的最後一個參數是 `PolicyModuleTarget Target`；`ReportRules.Check` 的最後一個參數是 `int maxDays`。
2. **i4 還沒開工**：`grep -rn "LocaleRegistry\|useLocales" backend/src frontend/src` 應無結果。i4 已開工就停下回報（p3 的語系接口要跟它對齊）。
3. **O1 的 `CachedSystemPolicy` 在原位**：`backend/src/DynamicModules.Application/Dynamic/PolicyService/CachedSystemPolicy.cs`，使用者是 `RequestContextMiddleware`、`ReportClientErrorHandler`（`OpsHandlers.cs:29`）、`OpsUpkeep.cs:30`，測試 `backend/tests/DynamicModules.ApplicationTests/Common/CachedSystemPolicyTests.cs`。介面不同就停下回報。
4. **`AfterCommitActions` 在原位**：`backend/src/DynamicModules.Application/Common/AfterCommitActions.cs`，由 `TransactionBehavior` 在最外層交易提交後執行、回滾丟棄。
5. **PostgreSQL 測試環境可用**：`docs/ai/testing.md`；`scripts/unattended/gate.sh` 已含 PostgreSQL 兩步（篩 `Category=Postgres&Category!=SqliteOnly&Category!=Benchmark`）。
6. **系統 PUT 的現況**：`UpdateSystemPolicyHandler`（`PolicyHandlers.cs:28-62`）是「寫設定 → `uow.CommitAsync` → `reader.Invalidate()` → `audit.Describe(...)` → 回應」；稽核由 `AuditBehavior`（`DependencyInjection.cs:28`，在 `TransactionBehavior` 內層）在同一交易寫入。hook 放在寫設定之後、`CommitAsync` 之前。

## 子段切法

| 子段 | 內容 | Task | migration |
|---|---|---|---|
| p3a | 部門層數 A5；營運時區 A17（含 A31，前後端）；稽核與維運畫面時間 | 1–7 | 無 |
| p3b | 語系 A18（前後端）；沒指定語系時用預設語系 | 8–13 | 無 |

時區前後端必須一起上線（只改後端，前端仍以 +08:00 送出與顯示，改時區後會錯一段時差），所以 A17 的前後端在同一個子段。語系也一樣：Task 8 登記的政策在畫面上可改，前端沒跟上時必填語系會被前端舊規則擋，所以 p3b 整段一起合併。

建議的工作階段（同一子段內，依序做、各自 commit，子段收尾才合併）：p3a-1＝Task 1–4（後端），p3a-2＝Task 5–7（前端與文件）；p3b-1＝Task 8–11（後端），p3b-2＝Task 12–13（前端與文件）。

---

## 計畫期間的判斷

〔行為〕＝影響使用者看到或能做的事；〔技術〕＝實作選擇。每條：判斷／理由／選錯代價。標「使用者 … 確認」或「使用者 … 裁決」的已拍板，實作時不要再問。

1. 〔行為〕**營運時區存固定 UTC 偏移（如 `+08:00`），不存 IANA 時區名稱，不支援日光節約。** 選項是世界實際使用的 38 個偏移（`-12:00` 到 `+14:00`，含 `+05:30`、`+05:45`、`+08:45`、`+12:45` 等）。**使用者 2026-11-12 確認**（Q7）。代價：有日光節約的地區每年要手動改兩次，而且改的那一刻會平移索引。
2. 〔技術〕**新增 `PolicyKind.UtcOffset`**（JSON 是字串，畫面顯示 `UTC+08:00`），不沿用 Choice。理由：Choice 的選項要逐一翻成四語，38 個偏移不必翻譯。代價：前後端各處 switch 多一個分支（`PolicyJson`、`PolicyMessages.Invalid` 的 `_ =>` default 也要明確補）。
3. 〔技術〕**全系統政策的單一快照＝演進後的 `CachedSystemPolicy`**（主控 2026-11-20 決定：不另做第二份快取）。一次讀齊 `PolicyCatalog.All` 中全部 System 範圍政策（`IPolicyReader` 一次 `ListByPrefixAsync`），整份不可變狀態以 `Volatile.Write` 替換；保留 O1 的 `Ttl` 60 秒、`FailureTtl` 10 秒、`ReadTimeout` 2 秒與「讀不到不讓請求失敗」。過期重載用 `SemaphoreSlim(1, 1)` 的 `WaitAsync(0)`：只讓一個請求跑，其他請求沿用舊值、不等待。理由：同步呼叫端（`IBusinessTime.Now`、`ICurrentLocale.Locale`、singleton 的 `FieldTypeRegistry`、FluentValidation）不能 await；DI singleton 每個測試容器各一份，不會讓平行測試互相干擾。代價：多台伺服器部署時，其他台最多晚 60 秒看到新時區或語系（判斷 4）。
4. 〔行為〕**改時區時，同一個交易內把「日期時間」欄位的索引平移（新偏移減舊偏移）**；日期欄位、公式與彙總的日期結果不動。用 EF `ExecuteUpdateAsync` 加 `DateTime.AddMinutes`，不寫 raw SQL；兩種資料庫都實測（Task 4 `Category=Postgres`）。`UpdateSystemPolicyRequest` 是 `ITransactionalRequest`，平移、設定寫入與稽核同一交易（PostgreSQL 交易開始就取全庫寫入鎖，pg0 §5.6；平移在交易內，不屬於 backend 規則說的「交易外的 `ExecuteUpdate`」，交付說明仍要寫明）。已知限制：其他台伺服器在最多 60 秒內仍用舊偏移寫入的記錄，日期時間索引會**永久**差一段時差；同一台在改時區那一刻已經算好值、還在等寫入鎖的請求也一樣。處理：政策說明與 `docs/deploy.md` 寫明「多台部署請在離峰時段、先縮成一台再改時區」（使用者 2026-11-12 裁決 U2），設計 §10 列為已知限制；不做自動重建。
5. 〔行為〕**系統記錄的時間欄位（建立、修改時間、通知時間、歷程時間、簽核時間等）不換算。** 它們以營運時區的牆上時間存、不帶偏移，改時區後舊資料照原數字顯示。**使用者 2026-11-12 確認**，確認框寫明（設計 §3.5）。注意：稽核與維運的時間是另一回事（存 UTC），照判斷 26 改成營運時區顯示。
6. 〔行為〕**前端的「今天」改用營運時區**（`rule-state.ts` 的 `todayLocal`、`field-type-registry.ts:99-102` 的 `localToday`）。**使用者 2026-11-12 確認**（Q8）。代價：電腦時區不是營運時區的使用者，在跨日前後看到的規則提示與「預設今天」會改變（變成與後端一致）。
7. 〔行為〕**改時區要在政策設定頁先確認**：確認框文字用設計 §3.5 的原文，按確定才送出。
8. 〔技術〕**部門層數只在「新增部門」或「改上級部門」時檢查**（現在每次更新都檢查）；**循環檢查每次都跑**。理由：§3.5、backend 規則「政策調嚴不溯及既有資料」。
9. 〔行為〕**必填語系是獨立政策 `locale.requiredText`，出廠 `zh-TW`**，套用到所有必填的多語文字。**使用者 2026-11-12 確認**（Q10）。
10. 〔行為〕**三個語系政策互相約束**：至少啟用一種；預設語系必須啟用；必填語系必須都啟用、至少一種。PUT 任何一個時，用「這次的新值＋另外兩個目前的生效值」檢查。**使用者 2026-11-12 確認**（Q11）。代價：要從繁中改成只用英文，得依序改三次。
11. 〔行為〕**必填語系增加時，只在「新增，或這次改了那段文字」時檢查**（§3.5）。後端：「改了」＝去掉空白與空值後，各語系內容和資料庫裡的不同；檢查從 FluentValidation 移到 handler（錯誤鍵與文字照舊，同一請求有多個錯時回報順序可能不同）。前端（主控 2026-11-20 依前端畫面通則決定）：新增時照必填語系先擋；編輯既有資料時只對「這次改過文字」的欄位先擋，沒改的照常送出。
12. 〔行為〕**應用程式套件仍要求繁中**（`TemplateValidator.HasZhTw`、`AppPackageValidator.CheckText`），不跟必填語系。**使用者 2026-11-12 確認**（Q13）。
13. 〔技術〕**多語文字的退回鏈：要求的語系 → 系統預設語系 → 繁中 → `SupportedLocales` 順序中第一個有值的**（前後端同規則；backend 規則「語系退回」，使用者 2026-11-20 裁決）。後端由呼叫端組好鏈傳入 `I18nText.Resolve(locale, fallbacks)`（i4 §10.1）；沒傳時是「要求的語系 → 繁中 → 第一個有值的」，只給中文訊息與套件格式用。i4a 再在鏈裡加上層語系。
14. 〔行為〕**停用的語系**：語言切換選單不列；多語文字輸入框不列（已存的值保留、不刪）；列印範本分頁不列；請求帶停用語系的 `X-Locale` 時，後端當成預設語系。分錄沖銷欄位標籤與簽核流程圖的新節點名稱仍產生四語（產生，不是輸入）。**使用者 2026-11-12 確認**（Q12）。
15. 〔技術〕**登入前的語系**：`GET auth/options`（公開）多回 `defaultLocale`、`enabledLocales`（兩個回傳點都帶）；第一次導覽時在 auth guard 取得，瀏覽器沒記過語系、或記的語系已停用時套用預設（不寫入 localStorage）。伺服器連不上時維持繁中（vue-i18n 的 `fallbackLocale`，列 §10）。之後 i4 只**加**欄位，不改 `enabledLocales` 代碼陣列的形狀。
16. 〔技術〕**前端時區與必填語系放在兩個小模組**（`business-time.ts`、`locale-settings.ts`），沒設定就呼叫會丟錯（不留預設值）。auth guard **每次導覽**時，若已登入且 `sessionSettingsLoaded()` 為 false 就呼叫 `loadSessionSettings()`（`GET policies/effective`），失敗導到 service-unavailable。理由（S4）：登入頁登入後不重新整理就導覽，第一次導覽的載入分支不會再跑；改成每次導覽檢查就涵蓋登入頁、Demo 一鍵登入與重新整理三條路，`useStartSession` 不用改。vitest 在既有的 `vitest.setup.ts` 設成出廠值。
17. 〔技術〕**新分類 `organization`（組織）**；時區與語系放在 p1c 已有的 `locale`（地區與語系）。
18. 〔行為〕**改時區後已開著的瀏覽器**：主框架（`MainLayout.vue`）每 60 秒重取一次全系統生效值，發現時區變了就先換偏移，再跳出不能關掉的提示框，只有「重新整理」一個按鈕。**使用者 2026-11-12 確認**（Q9、U1）。代價：開著沒存的表單內容在重新整理時會遺失。
19. 〔技術〕**營運時區偏移只有一個來源：`CachedSystemPolicy.BusinessOffset`**（審查 p3-M1）。`IBusinessTime` 不加 `Offset`：預設介面成員會被 `ActingBusinessTime`（`Program.cs:103` 的 `AddActingOverrides()`、`TestApp.cs:203`）包一層後吃掉。需要偏移的呼叫端注入 `CachedSystemPolicy`；`DateTimeHandler` 的 Func 也讀它。`ActingBusinessTime` 與測試替身都不用改。
20. 〔技術〕**系統 PUT 提交後用 `AfterCommitActions` 寫入新值；快照與序號包成一個不可變物件，以 `Interlocked.CompareExchange` 發布**（主控 2026-11-20 決定，審查必修 2）。handler 在交易內（已持有寫入鎖）取一個遞增的更新序號、算好新的生效值，登記 `afterCommit.Add("policy-cache", () => cache.Apply(def, effective, seq))`；`Apply` 只在序號比那一項目前的序號新時才套用。過期重載與讀取失敗也用同一個 CAS 發布，以「發布當下的最新物件」為基礎，不會蓋掉期間的 `Apply`。理由：`AfterCommitActions` 已是「最外層交易提交後才做、回滾丟棄」的現成機制，不必新增 pipeline behavior，也不必在 PUT 全程持鎖；`TransactionBehavior` 在釋放交易後才執行 callback（`TransactionBehavior.cs:16,37`），兩次 PUT 的 callback 可能逆序，靠序號擋；寫入鎖從交易開始就持有（PostgreSQL 全庫寫入鎖、SQLite 經 `WriteGate` 加 `BEGIN IMMEDIATE`，`EfTransactionManager.cs:13`），所以序號順序等於提交順序。代價：提交到 `Apply` 之間有毫秒級窗口，算進判斷 4 的已知限制。
21. 〔技術〕**語系類 RoleSet 的錯誤訊息寫「語系」**（審查 p3-S5）：`PolicyMessages.Invalid` 的 RoleSet 分支依 `d.Category == "locale"` 回 `只能從以下語系挑選：…`，其餘照舊。
22. 〔技術〕**語系類政策的選項由介面提供**（i4 §4.3、§18 第 2 點）：新增 `IInstalledLocales`（Application，singleton；出廠實作回 `I18nText.SupportedLocales`）與 `PolicyOptionSource`（唯一的選項出處：語系類回已安裝語系，其餘回 `def.Options`）。`PolicyDefinition.Normalize` 改收選項參數；`PolicyJson`、`PolicyMessages.Invalid`、`PolicyReader` 的選項都從 `PolicyOptionSource` 來。i4a 只換 `IInstalledLocales` 的實作。
23. 〔技術〕**`IsWellFormed` 的支援語系檢查集中在一個私有方法**（i4 §10.1、§18 第 5 點），i4a 換成代碼格式檢查；Task 8–12 不新增 `I18nText.SupportedLocales` 的使用點（要用就走 `IInstalledLocales`）。
24. 〔技術〕**`shared/policy-catalog.json` 的 `choices` 與 `policy-i18n.vitest.spec.ts` 排除語系類政策**（i4 §18 第 12 點）：語系選項以各語言自稱顯示，不進翻譯檢查。
25. 〔技術〕**前端只有一處取啟用語系**（`locale-settings.ts` 的 `enabledLocales()`）；`LocaleSwitcher`、`I18nTextInput`、`PrintTab` 改讀它，不再各自用 `LOCALES` 列輸入選項。i4a 把它換成 `useLocales()`。
26. 〔行為〕**稽核頁、維運監控頁的時間顯示和日期篩選照營運時區，時間旁標示時區（例 `+08:00`）**。**使用者 2026-11-20 裁決**（frontend 規則「時間顯示」）。範圍：`frontend/src/domain/ops/ops-format.ts` 的 `formatTime`、`localDayStartUtc`、`localDayEndUtc` 與用到它們的畫面（含任務規則分頁的自動暫停時間）。代價：時間字串變長，手機寬度要重看版面。
27. 〔行為〕**請求沒指定語系時用預設語系政策**。**使用者 2026-11-20 裁決**。查詢參數與 body 沒給語系時用 `ICurrentLocale.Locale`（`X-Locale` 標頭；標頭缺值、不認得或已停用時是預設語系）；訊息內插的欄位標籤改用 `ICurrentLocale.Locale` 加退回鏈（判斷 13）。約 50 處 `Resolve(I18nText.FallbackLocale)`（中文訊息、套件匯出警告）也一併改（D3，使用者 2026-11-20 裁決）。

---

## 待決（行為分岔，設計沒涵蓋；不阻擋開工，到標示的 Task 前要先問使用者）

- **D1 已裁決（使用者 2026-11-20）：歷程與列表不標時區**，只有存 UTC 的稽核、維運時間標示；Task 6 不動這兩處（規範見 `.claude/rules/frontend.md`「時間顯示」）。原題：歷程（`RecordHistory.vue:26`）與列表的系統欄位（`ListPage.vue:163`）顯示的是「存的當下的營運時區牆上時間」。要不要也在時間旁標示時區？標示目前的偏移，改時區後舊資料會被標錯（判斷 5 不換算）；不標示，與稽核頁的寫法不一致。Task 6 先不動這兩處。
- **D2 已裁決（使用者 2026-11-20）：跟啟用與預設語系**——下拉只列啟用語系、新設定預設用系統預設語系，已存的語言即使之後停用照常寄；併入 Task 12。原題：維運「警示信語言」選項（`OpsChannelTab.vue:7,25,28` 的 `LOCALES`、後端 `AlertMailText.Locales`、`AlertMailText.FactoryLocale`）要不要受啟用語系與預設語系政策影響。Task 12 先不動。
- **D3 已裁決（使用者 2026-11-20）：一起改成請求語系＋退回鏈**；併入 Task 11（量大可拆成 Task 11b 獨立 commit）。原題：約 50 處 `Resolve(I18nText.FallbackLocale)`（中文訊息內插、套件匯出與開啟的警告、`GetMenuHandler` 的排序、`AccountRepository.GetGroupImpactAsync` 的名稱）要不要改成請求語系＋退回鏈。必填改成非繁中後，沒有繁中的名稱在這些地方會走到「第一個有值的」。Task 11 只改裁決列出的那批。
- **D4 已裁決（使用者 2026-11-20）：比照標頭**，查詢參數帶停用或不認得的語系時改用預設語系；併入 Task 10。原題：查詢參數帶了已停用的語系（例 `?locale=th`）時，要照參數輸出，還是比照 `X-Locale` 當成預設語系。判斷 14 只寫了標頭。
- **D5 已裁決（使用者 2026-11-20）：補進每個必填語系**（沿用現做法、擴成全部必填語系）；併入 Task 9。原題：建立群組時，名稱除了寫進目前語系，現在會另存一份到繁中（`CreateGroupHandler.cs:22`，「其他語系看到空白名稱」）。必填改成非繁中後，要改成「寫進每個必填語系」（英文名稱欄會是中文字），還是只寫目前語系、靠退回鏈顯示。

- **N1（Task 11b 前，新分岔）**：D3 要把約 50 處 `Resolve(I18nText.FallbackLocale)` 改成請求語系，但其中 9 行是**寫進資料庫、稽核或 log 的固定繁中文字**，程式註解明寫刻意固定：簽核改道與分支取消的說明（`ApprovalEngine.cs:191`、`ApprovalGraphEngine.cs:277,283`）、簽核歷程說明（`ApproverResolver.cs:179`）、`FrozenFieldException.Message`（`FieldLocks.cs:73`）、匯入套件與樣板的稽核目標名稱（`ImportAppPackage.cs:46`、`ImportTemplateHandler.cs:30`）、記錄標題用的選項標籤（`TitleTemplate.cs:118`、`UpdateOptionHandler.cs:29`，兩處必須一致）。改成請求語系，同一份資料會依操作者的語言存成不同語言；維持繁中，必填改成非繁中後這些地方會走到「第一個有值的語系」；也可以改用系統預設語系。Task 11b 在裁決前不動這 9 行。
- **N2（Task 12 前，新分岔）**：站內通知標題快照的退路 `IncidentTitles.ZhTw`（`IncidentTitles.cs:62`，用 `AlertMailText.FactoryLocale`）不在 D2「警示信語言」的範圍。要不要一起改成系統預設語系？Task 12 刪 `FactoryLocale` 時，這裡先改成引用 `I18nText.FallbackLocale`，行為不變。
- **N3（Task 12 前，新分岔）**：D2 說警示信語言的下拉「只列啟用語系」，已存的語言停用後照常寄。那這時下拉要怎麼顯示已存的停用語言：列出來並標「已停用」（看得到現值，但違反「只列啟用」的字面）、不列（下拉會顯示成空白或代碼，使用者看不出現在寄什麼語言），還是只在下拉旁顯示一行「目前：ไทย（已停用）」。

---

## Global Constraints

- 出廠值＝現值，部署後沒人改設定時行為不變；既有測試的預期值不改（只改呼叫簽章）。
- 改嚴只管之後的新增（§3.5、backend 規則「上限與政策的通則」）：部門層數只在新增或改上級時檢查；必填語系只在新增或改了那段文字時檢查；時區改了之後，已存的帶偏移日期時間保持原本的絕對時間，顯示、今天、報表日期切點照新時區。超過上限的訊息帶出上限數字。
- 值的讀取一律經 `IPolicyReader`；同步程式讀 `CachedSystemPolicy`（由 reader 載入）。Domain 不碰 IO，時區偏移由呼叫端傳入或以函式注入。技術上下限與選項清單只寫在 `PolicyCatalog`；語系類選項的唯一出處是 `PolicyOptionSource`。
- **PostgreSQL 相容**：不寫 raw SQL；索引平移用 EF `ExecuteUpdateAsync`；`DateTime` 的 `Kind` 明確（`IBusinessTime.Now` 維持 `Unspecified`；`RecordIndex.DateValue`（`RecordIndex.cs:13`）在 PostgreSQL 是 `timestamp`，pg0 的 Kind 守衛會擋 `Kind=Utc` 的寫入）。
- **PostgreSQL 驗證**（pg0 §6.3–§6.6）：
  - 新測試標 `[Trait("Category", "Postgres")]` 進雙跑集：Task 3 的 `SystemPolicyCacheTests` 與 `BusinessTimeZoneApiTests`；Task 4 的 `TimeZoneChangeTests` 全部；Task 8 的 `LocaleRules_*` 與 `AuthApiTests` 新案例；Task 9 的 `RequiredTextPolicyTests` 中「改了才檢查」兩條；Task 10 的 `LocaleDefaultApiTests`；Task 11a／11b 的 `LocaleChainApiTests`。Task 3 的競態案例在 `CachedSystemPolicyTests`（純記憶體，不碰資料庫），必修 1 的四案在 `SystemPolicyCacheTests`（雙跑）；Task 4 的平移上下限在 `TimeZoneChangeTests`（雙跑）。
  - 每個標了 `Postgres` 的 Task，Step 4 另跑 PostgreSQL 窄範圍：`FORMWRIGHT_TEST_DB=postgres dotnet test <專案> --filter "(<該 Task 的 filter>)&Category!=SqliteOnly&Category!=Benchmark"`；Docker 起不來就停下回報，不跳過。
  - p3a 動到 Persistence（`RecordRepository.ShiftDateTimeIndexAsync`），合併前另跑一次 PostgreSQL 全套（不進 gate），結果寫進 p3 結果檔。
  - 子段收尾的 gate 已含 PostgreSQL 兩步，`exit=0` 才算過。
- **migration**：p3 兩個子段都不加。
- 後端訊息文字在出廠值時與現在逐字相同（例：`部門最多 10 層`、`名稱必須有繁體中文`、`關卡名稱必須有繁體中文`）。
- 前端新字串四語鍵集合一致、單行 JSON；含 `{`、`}`、`@`、`|` 用字面量寫法。
- 有畫面的改動在 390、820、1366、1920 四種寬度不橫捲；輸入框有可讀名稱。
- 測試只跑窄範圍；後端、前端、gate 不並行；vitest `--maxWorkers=2`。
- git：逐檔 `git add`；commit 前 `git status`；不用 `--no-verify`、不 force push、不 merge commit。公開 repo 不寫公司字串、個人絕對路徑、內網 IP。
- 每個子段收尾跑 `bash scripts/unattended/gate.sh <worktree 絕對路徑>`，並用 `formwright-purpose-check` skill 對照鐵則。

**政策登記**

| 代碼 | 段 | 分類 | 型別 | 範圍 | 出廠 | 技術範圍／選項 |
|---|---|---|---|---|---|---|
| `organization.maxDepth` | p3a | organization | Integer | System | `10` | 1–30 |
| `locale.timeZone` | p3a | locale | UtcOffset | System | `+08:00` | 38 個偏移（判斷 1） |
| `locale.default` | p3b | locale | Choice | System | `zh-TW` | 已安裝語系（判斷 22） |
| `locale.enabled` | p3b | locale | RoleSet | System | `zh-TW,en-US,th,vi` | 同上 |
| `locale.requiredText` | p3b | locale | RoleSet | System | `zh-TW` | 同上 |

取代的寫死處：

- `organization.maxDepth`：`DepartmentRules.MaxDepth`（`OrganizationHandlers.cs:14`）、`OrganizationMessages.TooDeep`（`OrganizationMessages.cs:9`）。
- `locale.timeZone`：後端 `BusinessClock.Offset`（`BusinessClock.cs:11`）、`TaipeiBusinessTime`、`Program.cs:101`、`FieldDisplay.BusinessOffset`（`FieldDisplay.cs:15`）、`DateTimeHandler.Taipei` 與 `"+08:00"` 尾綴（`DateTimeHandler.cs:20,32-33`）、`FieldImportParser.cs:96,99`、`ReportRules.cs:274`、`TaskRuleEvaluator.cs:51`、`ReportRunner.Cells.cs:143`；前端 `field-type-registry.ts:99-102,128-130,142,162,283`、`rule-engine.ts:25,85`、`DateSelect.vue:4,11`、`FilterValueInput.vue:57-64`、`rule-state.ts:5-9`。
- `locale.default`：`HeaderCurrentLocale`／`FallbackCurrentLocale` 的退回、Task 10 列出的查詢參數與 handler 預設、前端 `DEFAULT_LOCALE`。
- `locale.enabled`：前端 `LOCALES` 的輸入選項用法（`LocaleSwitcher`、`I18nTextInput` 的 `others`、`PrintTab`）。
- `locale.requiredText`：`I18nText.IsValid` 的繁中必填與其 17 個呼叫端、寫死「必須有繁體中文」的訊息、前端各 draft 與 `OptionEditor` 的 `['zh-TW']` 必填。

38 個偏移：`-12:00,-11:00,-10:00,-09:30,-09:00,-08:00,-07:00,-06:00,-05:00,-04:00,-03:30,-03:00,-02:00,-01:00,+00:00,+01:00,+02:00,+03:00,+03:30,+04:00,+04:30,+05:00,+05:30,+05:45,+06:00,+06:30,+07:00,+08:00,+08:45,+09:00,+09:30,+10:00,+10:30,+11:00,+12:00,+12:45,+13:00,+14:00`。

## Review Focus

1. **改時區後的既有資料**：`2026-11-11T10:00:00+08:00` 改成 `+07:00` 後列表顯示 `2026-11-11 09:00`；「今天」篩選、報表日分桶、日期時間區間篩選都以新時區算；日期欄位不平移；兩種資料庫結果相同。→ Task 4 `TimeZoneChange_*`；Task 5 `displays stored offset in business zone`。
2. **前端輸入與送出**：在 `+07:00` 輸入 09:00，送出 `…T09:00:00+07:00`；編輯一筆 `+08:00` 的舊值顯示 09:00，沒動就不改值；已開著的頁面 60 秒內發現時區變了，換新偏移並要求重新整理；登入頁登入後不重新整理，首頁的日期元件不丟錯。→ Task 5。
3. **快照不同步與故障**：PUT 提交後下一個請求立即用新時區；PUT 被回滾時快照不變；另一台（直接改資料庫）60 秒後才換；資料庫讀不到時請求照常成功、沿用上次的值；並行的過期重載只讀一次；過期重載讀到舊值但 PUT 先完成時，不會蓋回舊值；在真實 DI（含 `ActingBusinessTime`）下，匯出與顯示跟著新時區。→ Task 3、4。
4. **語系三政策互相約束與改用順序**。→ Task 8 `LocaleRules_*`。
5. **必填語系增加後編輯既有資料**：後端只擋改了的文字；前端編輯時沒改的欄位不先擋。→ Task 9 `UpdateField_*`、Task 12 `I18nTextInput` 與 draft 測試。
6. **沒指定語系時的回應語系**：預設改英文後，不帶 `X-Locale` 也不帶 `?locale=` 的匯出、列印、報表、歷程用英文標籤；訊息裡內插的欄位名稱也是英文。→ Task 10、11a、11b。
7. **稽核頁的時間**：瀏覽器時區不是營運時區時，稽核列表顯示營運時區的時間並標 `+08:00`；日期篩選 `2026-11-20` 送出營運時區的當天 0 點到 23:59:59.999。→ Task 6。

---

# p3a 部門層數、營運時區

### Task 1: `UtcOffset` 型別與 p3a 政策登記

**Files:**
- Modify: `backend/src/DynamicModules.Domain/Policies/PolicyDefinition.cs`（`PolicyKind` 加 `UtcOffset`；`Normalize`：Options 成員比對，同 Choice）
- Modify: `backend/src/DynamicModules.Domain/Policies/PolicyCatalog.cs`（`OrganizationMaxDepth`、`LocaleTimeZone`；`UtcOffsets`；兩個換算函式）
- Modify: `backend/src/DynamicModules.Application/Dynamic/PolicyService/PolicyModels.cs`（`PolicyJson.ToJson`／`FromJson` 補 `UtcOffset`，與 Choice 同樣是 JSON 字串；`HasOptions`（`:57`）改成 `Kind is Choice or RoleSet or UtcOffset`）、`PolicyMessages.cs`（`Invalid`（`:24`）加 `UtcOffset` 分支）、`PolicyReader.cs`（`ChoiceAsync` 的 `Expect` 也接受 `UtcOffset`）
- Modify: `shared/policy-catalog.json`（codes 加 2 個；categories 加 `organization`；`choices` 不含偏移）
- Modify: `frontend/src/domain/dynamic/policy-types.ts`（`PolicyKind` 加 `'utcOffset'`；`policyValueText` 回 `UTC${v}`）、`frontend/src/presentation/policies/PolicyValueEditor.vue`（`q-select`，選項 `meta.options`，顯示 `UTC${o}`）、`frontend/src/i18n/*.json`
- Test: `backend/tests/DynamicModules.DomainTests/Policies/PolicyDefinitionTests.cs`、`backend/tests/DynamicModules.DomainTests/Policies/PolicyCatalogFixtureTests.cs`、`backend/tests/DynamicModules.WebAPITests/Dynamic/PolicyApiTests.cs`、`frontend/src/domain/dynamic/policy-types.vitest.spec.ts`、`frontend/src/presentation/policies/PolicyValueEditor.vitest.spec.ts`

**Interfaces:**
- `PolicyCatalog.UtcOffsets : IReadOnlyList<string>`（38 個）；`LocaleTimeZone = new("locale.timeZone", "locale", PolicyKind.UtcOffset, PolicyScope.System, "+08:00", Options: UtcOffsets)`；`OrganizationMaxDepth = new("organization.maxDepth", "organization", PolicyKind.Integer, PolicyScope.System, "10", 1, 30, Unit: "count")`。`PolicyDefinition` 的最後一個參數是 p2 的 `Target`（預設 `Main`），一律用具名參數。
- `public static TimeSpan UtcOffsetOf(string canonical)`（`+05:45` → 5h45m、`-03:30` → −3h30m）；`public static string FormatUtcOffset(TimeSpan offset)`（`+00:00` 帶正號）；`public static TimeSpan FactoryBusinessOffset => UtcOffsetOf(LocaleTimeZone.Factory)`（給 Domain 的預設參數用）。
- `PolicyCatalogFixtureTests.Catalog_MatchesSharedFixture` 的 `choices` 只聯集 Choice 與 RoleSet 的 Options（不含 `UtcOffset`；Task 8 再排除語系類）。
- 文案（zh-TW）：`organization.maxDepth` 部門層數上限／新增部門或改上級部門時檢查；已超過的部門照常可改其他欄位。`locale.timeZone` 營運時區／決定「今天」、逾期、報表日期切點與日期時間的顯示；不支援日光節約；多台伺服器部署請先看部署文件再改。分類 `organization` 組織。

- [ ] **Step 1: 寫失敗的測試**
  - `PolicyDefinitionTests.UtcOffset_Normalize`：`"+08:00"` → `"+08:00"`；`"+8:00"`、`"+08:15"`、`"UTC+8"`、`null` → null。
  - `PolicyDefinitionTests.UtcOffset_Convert`：`UtcOffsetOf("+05:45") == new TimeSpan(5, 45, 0)`、`UtcOffsetOf("-03:30") == -new TimeSpan(3, 30, 0)`；`FormatUtcOffset(TimeSpan.Zero) == "+00:00"`；38 個選項互轉一致。
  - `PolicyCatalogFixtureTests.Factories_EqualCurrentBehavior` 加 `organization.maxDepth` `"10"`、`locale.timeZone` `"+08:00"`。
  - `PolicyApiTests.System_TimeZone_RoundTrip`：admin GET 那項 `kind == "utcOffset"`、`options` 有 38 個且含 `+05:45`；PUT `{ value: "+07:00" }` 200、回應 `value == "+07:00"`；`{ value: "+07:15" }` 400，`errors.value[0] == "必須是 UTC 偏移，例如 +08:00"`；`{ value: 7 }` 400。
  - `policy-types.vitest.spec.ts`：`policyValueText({ kind: 'utcOffset' }, '+08:00') === 'UTC+08:00'`。
  - `PolicyValueEditor.vitest.spec.ts`：`utcOffset` 選 `+07:00` emit `'+07:00'`，選單有可讀名稱。
- [ ] **Step 2: 跑測試確認失敗**：`cd backend && dotnet test tests/DynamicModules.DomainTests --filter "FullyQualifiedName~Policies"`；`dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~PolicyApiTests"`；`cd frontend && bunx vitest run src/domain/dynamic/policy-types.vitest.spec.ts src/presentation/policies --maxWorkers=2`
- [ ] **Step 3: 實作型別、登記、JSON 轉換、編輯器、四語文案**（`PolicyJson`、`PolicyMessages.Invalid` 的 switch 明確補 `UtcOffset` 分支，不靠 `_ =>`）
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；`bunx vitest run src/domain/dynamic/policy-i18n.vitest.spec.ts src/i18n --maxWorkers=2`；`bun run typecheck`
- [ ] **Step 5: Commit**（`feat(policy): 時區型別，登記部門層數與營運時區`）

**驗收**：Step 4 的測試全過；GET `system/policies` 回兩個新項目且 `locale.timeZone` 有 38 個選項。

### Task 2: A5 部門層數

**Files:**
- Modify: `backend/src/DynamicModules.Application/Dynamic/OrganizationService/OrganizationHandlers.cs`（`DepartmentRules.MaxDepth` `:14`、`DepartmentValidator` `:61`、`ParentErrorAsync` `:84` 起、`UpdateDepartmentHandler` 約 `:119`）、`OrganizationMessages.cs:9`
- Test: `backend/tests/DynamicModules.ApplicationTests/Dynamic/Organization/DepartmentTests.cs`（繼承 `DesignerTestBase`；不寫授權表，不需要 `TestPermits`）

**Interfaces:**
- 刪 `DepartmentRules.MaxDepth`；`OrganizationMessages.TooDeep(int max) => $"部門最多 {max} 層"`；`DepartmentValidator(IOrganizationRepository org, IPermissionRepository permissions, IPolicyReader policies)`；`ValidateAsync(long? selfId, DepartmentInput input, CancellationToken ct, string? currentManager = null, long? currentParentId = null)`。
- `ParentErrorAsync` 拆成兩段：循環檢查每次都跑；層數只在 `selfId is null || input.ParentId != currentParentId` 時檢查（讀 `policies.IntAsync(OrganizationMaxDepth, null, ct)`）。`UpdateDepartmentHandler` 傳 `d.ParentId`。
- 前端：`frontend/src/pages/system/OrganizationPage.vue` 沒有層數常數（現況檢查已確認），不做前端提示。

- [ ] **Step 1: 寫失敗的測試**
  - `DepartmentTests.Depth_UsesPolicy`：`organization.maxDepth` 2 → 建第 3 層 400、`errors.parentId[0] == "部門最多 2 層"`。
  - `DepartmentTests.Depth_Lowered_RenameDeepDepartmentAllowed`：先建 3 層，上限改 2 → 改第 3 層名稱成功；把一個第 1 層部門移到第 2 層底下（變第 3 層）400。
  - `DepartmentTests.Cycle_StillCheckedWhenParentUnchanged`：上級沒變、但資料已形成循環的情境若無法構造，改測「改上級成自己的子部門」仍回循環錯誤（確認拆段後循環檢查沒被層數條件跳過）。
  - 既有「部門最多 10 層」測試不改斷言。
- [ ] **Step 2: 跑測試確認失敗**：`cd backend && dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~DepartmentTests"`
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；`--filter "FullyQualifiedName~Organization|FullyQualifiedName~ManagerChain"`
- [ ] **Step 5: Commit**（`feat(policy): 部門層數改讀政策（A5）`）

**驗收**：Step 4 全過；`grep -rn "MaxDepth" backend/src/DynamicModules.Application/Dynamic/OrganizationService` 無結果。

### Task 3: 全系統政策快照（演進 `CachedSystemPolicy`）與營運時鐘

**Files:**
- Modify: `backend/src/DynamicModules.Application/Dynamic/PolicyService/CachedSystemPolicy.cs`（判斷 3、20）
- Create: `backend/src/DynamicModules.Application/Dynamic/PolicyService/IPolicyChangeHook.cs`
- Modify: `backend/src/DynamicModules.Application/Dynamic/PolicyService/PolicyHandlers.cs`（`UpdateSystemPolicyHandler`：hook 迴圈、登記 `AfterCommitActions`）
- Modify: `backend/src/DynamicModules.Application/DependencyInjection.cs:49`（`CachedSystemPolicy` 仍是 singleton；不加 behavior）
- Create: `backend/src/DynamicModules.WebAPI/Time/ZoneBusinessTime.cs`；Delete: `backend/src/DynamicModules.WebAPI/Time/TaipeiBusinessTime.cs`
- Create: `backend/src/DynamicModules.WebAPI/Ops/SystemPolicyRefreshMiddleware.cs`
- Modify: `backend/src/DynamicModules.WebAPI/Program.cs`：`:101` 改 `AddSingleton<IBusinessTime, ZoneBusinessTime>()`（仍在 `:103` 的 `AddActingOverrides()` 之前）；`:186` 的 `RequestContextMiddleware` 之後緊接 `UseMiddleware<SystemPolicyRefreshMiddleware>()`（重載的 log 才帶 requestId）；啟動流程 `:224` 的 `MigrateAsync` 之後、`:229` 的 `IndexBackfillJob` 之前 `await cache.EnsureFreshAsync(CancellationToken.None)`
- 不改: `IBusinessTime.cs`、`ActingBusinessTime.cs`（判斷 19）；`TestApp.cs:203` 的 `ActingBusinessTime(Clock)` 與 `:299` 的 `FakeBusinessTime`（偏移由快照提供；只改 `:298` 註解裡的類別名）
- Test: `backend/tests/DynamicModules.ApplicationTests/Common/CachedSystemPolicyTests.cs`（既有 5 案不改斷言，新增案例）、`backend/tests/DynamicModules.ApplicationTests/Dynamic/Policies/SystemPolicyCacheTests.cs`（新，`Category=Postgres`）、`backend/tests/DynamicModules.WebAPITests/Dynamic/BusinessTimeZoneApiTests.cs`（新，`Category=Postgres`）；`backend/tests/DynamicModules.WebAPITests/TaipeiBusinessTimeTests.cs` 改名改寫成 `ZoneBusinessTimeTests.cs`；`WriteGuardTests.cs:29` 的測試名 `FakeBusinessTime_IsUnspecified_LikeTaipeiBusinessTime` 改成 `…_LikeZoneBusinessTime`

**Interfaces:**
- `public sealed class CachedSystemPolicy(IServiceScopeFactory scopes, TimeProvider time, PolicyEnvironmentDefaults env, ILogger<CachedSystemPolicy> logger)`（singleton）：
  - 保留 `Ttl`（60 秒）、`FailureTtl`（10 秒）、`ReadTimeout`（2 秒），都列設計 §10 技術限制。
  - 發布物件 `sealed record Published(FrozenDictionary<string, string> Values, FrozenDictionary<string, long> Seq, long ApplyCount, TimeSpan BusinessOffset, DateTimeOffset LoadedAt, bool Failed, bool Loaded)`（不可變；`Seq` 是每一項最後套用的更新序號，`ApplyCount` 是 `Apply` 成功的累計次數）。欄位 `private Published _current`；讀取端 `Volatile.Read` 一次後只用那一份；所有寫入一律走 `Publish(Func<Published, Published?> next)`：`do { cur = Volatile.Read(ref _current); var n = next(cur); if (n is null) return; } while (Interlocked.CompareExchange(ref _current, n, cur) != cur);`。不用 `Volatile.Write` 直接覆蓋。
  - `private long _seq`；`public long NextSequence() => Interlocked.Increment(ref _seq)`（只由系統 PUT 在交易內呼叫）。
  - `public string Get(PolicyDefinition def)`：只收 `Scope == System`，否則丟 `InvalidOperationException`（同現況）；沒載入過回 `env.FactoryOf(def)`。
  - `public TimeSpan BusinessOffset`（換狀態時一起算好）。
  - `public Task EnsureFreshAsync(CancellationToken ct)`：沒過期（成功看 `Ttl`、失敗看 `FailureTtl`）直接返回；`_gate.WaitAsync(0)` 拿不到就返回（沿用舊值、不等待）；拿到後再確認一次過期；記下 `start = Volatile.Read(ref _current)` 與 `seqAtStart = Volatile.Read(ref _seq)`；用自己的 `ReadTimeout` token 開 scope、逐項 `IPolicyReader.ResolveAsync(def, null).Effective`（reader 在 scope 內只發一次查詢）。
    - 成功：`Publish(cur => cur.ApplyCount != start.ApplyCount ? null : new(values, 每一項 Seq = max(cur.Seq[code], seqAtStart), cur.ApplyCount, offset, now, Failed: false, Loaded: true))`。期間有 `Apply` 就整份丟掉（回 null），`LoadedAt` 不動，下一個請求再重載。`Seq` 標到 `seqAtStart`：讀取開始前已取序號的 PUT，callback 晚到時不會把重載讀到的較新值蓋回去。
    - 讀不到（例外或逾時）：記 Warning；`Publish(cur => cur with { Failed = true, LoadedAt = now })`，以發布當下的物件為基礎，期間 `Apply` 的值保留；從沒成功過時 `Values` 是 `env.FactoryOf`。
    - 呼叫端的 token 取消時照現況往外丟 `OperationCanceledException`，其他例外不往外丟。
    - 已知限制（寫進設計 §10 與 `docs/deploy.md`）：資料庫持續讀不到時，沿用上次成功的值沒有時間上限；冷啟動就讀不到時用部署出廠值（營運時區可能不是管理員設的值）。
  - `public void Apply(PolicyDefinition def, string effective, long seq)`：`Publish(cur => seq <= cur.Seq.GetValueOrDefault(def.Code) ? null : cur with { Values = 換掉那一項, Seq = 那一項設 seq, ApplyCount = cur.ApplyCount + 1, BusinessOffset = 時區時重算 })`；`LoadedAt` 不動。序號不比較新時不套用，並把 `LoadedAt` 設成過期（同樣經 `Publish`），讓下一個請求以資料庫為準重載。
  - 保留 `IntAsync`、`BoolAsync`：`await EnsureFreshAsync(ct)` 後回 `Get(def)` 的解析值（既有呼叫端不改）。
  - 與 O1 的差異（寫進類別註解與交付說明）：讀不到時從「回 `def.Factory`」改成「沿用上次成功的值，從沒成功過才用部署出廠值」。理由：時區或語系在資料庫短暫故障時跳回出廠值，會用錯偏移寫入索引。
- `public interface IPolicyChangeHook { Task<ServiceResult?> BeforeCommitAsync(PolicyDefinition def, string oldEffective, string newEffective, CancellationToken ct); }`；DI 以 `AddScoped<IPolicyChangeHook, …>` 註冊，handler 注入 `IEnumerable<IPolicyChangeHook>`。
- `UpdateSystemPolicyHandler`（交易內；審查必修 1）。`SystemSettingRepository` 的讀取是 `AsNoTracking`（`SystemSettingRepository.cs:11,23`），`SetAsync`／`RemoveAsync` 只改追蹤狀態，`SaveChanges` 之前查不到新值，所以順序固定為：
  1. `oldEffective = (await reader.ResolveAsync(def, null, ct)).Effective`（不是快照）；
  2. 寫設定（`SetAsync` 或 `RemoveAsync`）；
  3. `await uow.CommitAsync(ct)`（SaveChanges，仍在外層交易內）；
  4. `reader.Invalidate()`，`newEffective = (await reader.ResolveAsync(def, null, ct)).Effective`（此時讀得到第 2 步的新增、修改、刪除）；
  5. 新舊不同時依序呼叫 hooks（Task 4 的平移在這裡），回非 null 就直接回該結果，`TransactionBehavior` 回滾整個交易（第 3 步的 SaveChanges 一起退掉），`AfterCommitActions` 丟棄；hook 丟例外同樣回滾；
  6. `var seq = cache.NextSequence()`；`afterCommit.Add("policy-cache", () => cache.Apply(def, newEffective, seq))`；
  7. `audit.Describe(...)`，回應；外層 `TransactionBehavior` 提交後才執行 `AfterCommitActions`。
- `ZoneBusinessTime(CachedSystemPolicy cache) : IBusinessTime`：`Now => DateTime.SpecifyKind(DateTime.UtcNow + cache.BusinessOffset, DateTimeKind.Unspecified)`；`Today => DateOnly.FromDateTime(Now)`。仍被 `AddActingOverrides()` 包成 `ActingBusinessTime`。
- `SystemPolicyRefreshMiddleware(RequestDelegate next, CachedSystemPolicy cache)`：`await cache.EnsureFreshAsync(ctx.RequestAborted)` 後 `await next(ctx)`。
- 選擇 `AfterCommitActions` 而不是新 behavior 的理由見判斷 20。

- [ ] **Step 1: 寫失敗的測試**
  - `CachedSystemPolicyTests`（沿用既有的 `FakeReader`、`ManualTimeProvider`）：
    - `Get_BeforeLoad_UsesDeploymentFactory`：沒呼叫過 `EnsureFreshAsync` 時 `BusinessOffset == 8h`、`Get(OrganizationMaxDepth) == "10"`；部署出廠值設 `organization.maxDepth = 12` 時回 `"12"`；`Get(TaskMaxRulesPerModule)` 丟例外。
    - `EnsureFresh_LoadsAllSystemPolicies`：reader 回 `locale.timeZone = "+07:00"` → `BusinessOffset == 7h`。
    - `ReaderFails_KeepsLastGoodValues`：先成功載入 `+07:00`，時間前進 61 秒、reader 丟例外 → `BusinessOffset` 仍 7h、記一次 Warning；10 秒內再呼叫不重讀。
    - `ConcurrentEnsureFresh_ReadsOnce`：過期後平行 20 個 `EnsureFreshAsync`，reader 只被呼叫一輪；沒有呼叫丟例外。
    - `Apply_DuringReload_DiscardsReload`：reader 第一次呼叫卡在 `TaskCompletionSource`；同時 `Apply(LocaleTimeZone, "+07:00", seq)`；放行 reader 回 `+08:00` → `BusinessOffset` 仍 7h，且下一次 `EnsureFreshAsync` 會重讀（`LoadedAt` 沒被更新）。
    - `Apply_BetweenCheckAndPublish_NotOverwritten`（必修 2，「檢查後插入」）：用類別內的測試掛點 `internal Action? BeforePublishForTests`，在重載的 CAS 第一次嘗試之前插入 `Apply(+07:00)` → CAS 失敗、重試時看到 `ApplyCount` 變了而放棄 → `BusinessOffset` 仍 7h。
    - `ReloadFails_DuringApply_KeepsAppliedValue`（必修 2，失敗分支）：reader 卡住時 `Apply(+07:00)`，之後 reader 丟例外 → `BusinessOffset` 仍 7h、`Failed`；10 秒內不重讀，前進 11 秒且 reader 恢復回 `+07:00` 後重載成功。
    - `Apply_OutOfOrderCallbacks_NewerWins`（必修 2，callback 逆序）：`s1 = NextSequence()`、`s2 = NextSequence()`；先 `Apply(+07:00, s2)` 再 `Apply(+09:00, s1)` → `BusinessOffset` 是 7h，且狀態被標成過期；不同政策的序號互不影響（`Apply(locale.timeZone, …, s1)` 不擋 `Apply(organization.maxDepth, …, s2)` 之後的 `Apply(organization.maxDepth, …, s3)`）。
    - `Reload_StampsSequence_LateCallbackIgnored`：`s1` 已取、重載讀到 `+07:00`（`s1` 的值）並發布；之後 `Apply(+08:00, s0)`（更舊的 PUT 的晚到 callback）不套用。
    - 既有 5 案（`ReturnsEffectiveValue`、`CachesFor60Seconds_ThenRereads`、`ReaderThrows_ReturnsFactory_CachedFor10Seconds`、`ReaderHangs_ReturnsFactoryWithinReadTimeout`、`ModuleScopedPolicy_Throws`）不改斷言。
  - `SystemPolicyCacheTests`（類別 `Category=Postgres`，經 MediatR 含 `TransactionBehavior`）：
    - `Put_UpdatesCacheAfterCommit`：送 `UpdateSystemPolicyRequest("locale.timeZone", J("\"+07:00\""))` 後 singleton 的 `BusinessOffset == 7h`。
    - `Put_RolledBack_CacheUnchanged`：註冊測試用 `IPolicyChangeHook` 回 `ServiceResult.Invalid(...)` → 400；`Dynamic_SystemSetting` 沒有 `policy.locale.timeZone`；`BusinessOffset` 仍 8h。
    - `Put_SameValue_HooksNotCalled`：PUT `+08:00`（等於生效值）時測試 hook 沒被呼叫。
    - 必修 1 的四個案例（hook 收到的新舊值與快照都要對；SQLite 與 PostgreSQL 雙跑）：
      - `Put_FirstSet_HookSeesNewValue`：沒設過時 PUT `+07:00` → 測試 hook 收到 `("+08:00", "+07:00")`；快照 7h。
      - `Put_Modify_HookSeesNewValue`：已設 `+07:00` 再 PUT `+09:00` → hook 收到 `("+07:00", "+09:00")`；快照 9h。
      - `Put_ResetToFactory_HookSeesFactory`：已設 `+07:00` 再 PUT `null` → hook 收到 `("+07:00", "+08:00")`；設定列已刪；快照 8h。
      - `Put_HookRejects_EverythingRolledBack`：已設 `+07:00`，PUT `+09:00` 時 hook 回 `Invalid` → 400；資料庫仍是 `+07:00`（第 3 步的 SaveChanges 被回滾）；快照仍 7h；沒有 `policy-cache` callback 執行。
  - `BusinessTimeZoneApiTests`（類別 `Category=Postgres`）：
    - `Middleware_ReloadsWhenStale`：直接寫 `Dynamic_SystemSetting` 的 `policy.locale.timeZone = "+07:00"`（模擬另一台改的），GET 任一 API 後快照仍 8h；`TestApp.Time` 前進 61 秒再 GET，變 7h。
    - `Put_NextRequestUsesNewZone`：admin PUT `+07:00` 後立刻 GET，快照 7h（不用等 60 秒）。
    - `DatabaseDown_ThenRecovers`（審查建議）：替換 `IPolicyReader` 讓它丟例外並讓快照過期 → GET 任一 API 仍 200、快照沿用 7h；故障期間 admin PUT 走另一個正常 scope 的情境不構造，改在 `CachedSystemPolicyTests.ReloadFails_DuringApply_KeepsAppliedValue` 涵蓋；恢復後前進 11 秒再 GET，重新讀到資料庫的值。
  - `ZoneBusinessTimeTests`：`+07:00` 時 `Now` 與 `DateTime.UtcNow + 7h` 差 5 秒內、`Kind == Unspecified`；出廠時是 +8h。
- [ ] **Step 2: 跑測試確認失敗**：`cd backend && dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~CachedSystemPolicyTests|FullyQualifiedName~SystemPolicyCacheTests"`；`dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~BusinessTimeZoneApiTests|FullyQualifiedName~ZoneBusinessTimeTests|FullyQualifiedName~WriteGuardTests"`
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；另跑 WebAPITests `--filter "FullyQualifiedName~PolicyApiTests|FullyQualifiedName~EndpointRoutesTests|FullyQualifiedName~DemoDataApiTests|FullyQualifiedName~Ops"`（O1 的慢請求與前端錯誤收集照常）；ApplicationTests `--filter "FullyQualifiedName~ActingScopeTests|FullyQualifiedName~Demo|FullyQualifiedName~TransactionBehaviorTests"`；PostgreSQL：ApplicationTests `SystemPolicyCacheTests`（含必修 1 的四案）、WebAPITests `BusinessTimeZoneApiTests` 各跑一次
- [ ] **Step 5: Commit**（`feat(policy): 全系統政策快照與依政策的營運時鐘`）

**驗收**：Step 4 全過（含 PostgreSQL）；`grep -n "Volatile.Write" backend/src/DynamicModules.Application/Dynamic/PolicyService/CachedSystemPolicy.cs` 無結果（發布只走 CAS）；`grep -rn "TaipeiBusinessTime" backend` 無結果；`grep -rn "class SystemPolicySnapshot" backend` 無結果（只有一份快取）。

### Task 4: 後端各處改用營運時區；改時區時平移索引

**Files:**
- Modify: `backend/src/DynamicModules.Domain/FieldTypes/BusinessClock.cs`（刪 `Offset`；解析改帶偏移參數）、`Handlers/DateTimeHandler.cs:15,20,32-33`、`FieldTypeRegistry.cs`（`CreateDefault` 收偏移 Func）
- Modify: `backend/src/DynamicModules.Application/DependencyInjection.cs:104`
- Modify: `backend/src/DynamicModules.Application/Dynamic/Display/FieldDisplay.cs:14-15,51`＋6 個呼叫端：`GetRecordHistoryHandler.cs:69`、`ExportRecordsHandler.cs:85`、`PrintValueFormat.cs:21`（靜態工具，由它的呼叫端傳入）、`ReportRunner.Output.cs:91,178,226`
- Modify: `backend/src/DynamicModules.Application/Dynamic/ImportService/FieldImportParser.cs:16,96,99`、`ImportPlanner.cs:90`（建 `ImportLookups` 處）
- Modify: `backend/src/DynamicModules.Application/Dynamic/ReportService/Data/ReportRules.cs`（`Check` `:128`、`NormalizeValue` `:231`、`One` `:267`、補值 `:274`）、`ReportDefinitionValidator.cs:26`
- Modify: `backend/src/DynamicModules.Application/Dynamic/ReportService/ReportRunner.cs`（建構子注入 `CachedSystemPolicy`；`RunContext`（`:63`）加 `BusinessOffset`，同一次執行用同一個值）、`ReportRunner.Cells.cs:114,141-143`、`ReportRunner.Conditions.cs:46-50,141-146`（`ScalarFits`、`Span` 是沒有 ctx 的 static，加 `TimeSpan offset` 參數）、`ReportRunner.Sql.cs:190,192`（`Span` 的呼叫端）、`ReportRunner.Output.cs:83-91`（`EqLabel` 加偏移參數）
- Modify: `backend/src/DynamicModules.Application/Dynamic/TaskRuleService/TaskRuleEvaluator.cs:40,51`、`TaskRuleApplier.cs:16,70,79`（注入 `CachedSystemPolicy`）
- Create: `backend/src/DynamicModules.Application/Dynamic/RecordService/TimeZoneChangeHook.cs`
- Modify: `backend/src/DynamicModules.Domain/Repositories/Dynamic/IRecordRepository.cs`、`backend/src/DynamicModules.Persistence/Repositories/Dynamic/RecordRepository.cs`（目前沒有任何 `ExecuteUpdateAsync`，這是第一個）
- Test: `backend/tests/DynamicModules.DomainTests/FieldTypes/BusinessClockTests.cs`、`DateTimeHandlerTests.cs`、`backend/tests/DynamicModules.ApplicationTests/Dynamic/FieldDisplayTests.cs`、`FieldImportParserTests.cs`、`backend/tests/DynamicModules.ApplicationTests/Dynamic/Policies/TimeZoneChangeTests.cs`（新，`Category=Postgres`）、`backend/tests/DynamicModules.WebAPITests/Dynamic/BusinessTimeZoneApiTests.cs`（加一案）

**Interfaces:**
- `BusinessClock.ParseWallClock(string s, TimeSpan offset)`、`TryParseWallClock(string s, TimeSpan offset, out DateTime at)`：帶偏移的值換成 `offset` 的牆上時間，不帶偏移的值原樣；溢位判斷改成雙向：`offset > 0` 時 `dto.UtcDateTime > DateTime.MaxValue - offset` 視為格式錯誤，`offset < 0` 時 `dto.UtcDateTime < DateTime.MinValue - offset` 視為格式錯誤。類別註解的「台北」改「營運時區（政策 locale.timeZone）」。
- `DateTimeHandler(Func<TimeSpan>? businessOffset = null)`（null → `PolicyCatalog.FactoryBusinessOffset`）；`ToIndex` 用 `BusinessClock.ParseWallClock(s, offset())`；`Normalize` 同一套雙向溢位判斷，輸出 `dto.ToOffset(o).ToString("yyyy-MM-dd\\THH:mm:ss") + PolicyCatalog.FormatUtcOffset(o)`。`FieldTypeRegistry.CreateDefault(Func<TimeSpan>? businessOffset = null)`；DI：`services.AddSingleton(sp => FieldTypeRegistry.CreateDefault(() => sp.GetRequiredService<CachedSystemPolicy>().BusinessOffset))`。
- `public sealed record FieldDisplayContext(string Locale, TimeSpan BusinessOffset)`（Task 11a 再加 `LocaleFallbacks`），由 `CachedSystemPolicy.Display(string locale)` 建；`FieldDisplay.Format(Field field, JsonElement? value, FieldDisplayContext display, ReferenceInfo? reference, IReadOnlyDictionary<string, string> userNames)`；刪 `FieldDisplay.BusinessOffset`。理由：呼叫端只改一次簽章，Task 11 加退回鏈時不再改 6 個呼叫端。
- `ImportLookups(..., TimeSpan BusinessOffset)`；`FieldImportParser.DateTime` 用它（`yyyy-MM-dd HH:mm` 視為營運時區、輸出帶營運偏移；`"+08:00"` 字面值改 `FormatUtcOffset`）。
- `ReportRules.Check(d, set, registry, userIds, int maxDays, TimeSpan businessOffset)`；`NormalizeValue(..., maxDays, businessOffset)`、`One(..., businessOffset)`：沒帶偏移的日期時間補 `FormatUtcOffset(businessOffset)`。`ReportDefinitionValidator` 注入 `CachedSystemPolicy` 傳 `BusinessOffset`。
- `ReportRunner`：`RunContext.BusinessOffset` 在建 ctx 時取一次；`TryWallClock(string s, TimeSpan offset, out DateTime at)`；SQL 路徑與記憶體路徑用同一個值。
- `TaskRuleEvaluator.DueDate(TaskRuleTask def, Module module, IReadOnlyDictionary<string, JsonElement?> values, TimeSpan businessOffset)`。已存的任務 `DueDate` 改時區後不重算（設計 §10 已知限制）。
- `TimeZoneChangeHook(IRecordRepository records) : IPolicyChangeHook`：只處理 `LocaleTimeZone`；`delta = UtcOffsetOf(new) - UtcOffsetOf(old)`。先檢查邊界（審查必修 3）再平移：
  1. 可平移的原值範圍：`delta > 0` 時 `DateValue <= DateTime.MaxValue - delta`；`delta < 0` 時 `DateValue >= DateTime.MinValue - delta`（＝`MinValue` 加上 `|delta|`）。
  2. `var (count, min, max) = await records.DateTimeIndexOutOfShiftRangeAsync(delta, ct)`；`count > 0` 就回 `ServiceResult.Invalid(new() { ["value"] = [PolicyMessages.TimeZoneShiftOutOfRange(count, min, max)] })`，不發平移的 SQL；`TransactionBehavior` 回滾，設定、索引、快照都不變。
  3. 否則 `await records.ShiftDateTimeIndexAsync(delta, ct)`，回 null。
  - 訊息（`PolicyMessages`，原文）：`改成這個時區會讓 {count} 筆日期時間超出可儲存的範圍（{min} ～ {max}），請先修改這些記錄再改時區`，`min`／`max` 以 `yyyy-MM-dd HH:mm` 顯示超界那幾筆索引值的最小與最大值；帶出筆數與範圍（backend 規則「超過上限的錯誤訊息要帶出上限數字」的同類做法）。
  - 為什麼要先查：.NET `DateTime` 的範圍比 PostgreSQL `timestamp` 窄，SQLite 存成文字；平移出界時資料庫可能報錯（交易被標成失效），也可能寫進讀不回來的值，兩種都不能靠事後接例外處理（backend 規則：交易內接住 `ExecuteUpdate` 錯誤後不能再發 SQL）。
- `IRecordRepository.DateTimeIndexOutOfShiftRangeAsync(TimeSpan delta, CancellationToken ct) : Task<(int Count, DateTime? Min, DateTime? Max)>`：同一批列（日期時間欄位、`DateValue != null`）中超出第 1 點範圍的筆數與最小、最大值；EF LINQ（`CountAsync`、`MinAsync`、`MaxAsync`），不寫 raw SQL；比較用的界線值以 `DateTime.SpecifyKind(…, Unspecified)` 傳入。
- `IRecordRepository.ShiftDateTimeIndexAsync(TimeSpan delta, CancellationToken ct) : Task<int>`：`RecordIndex` 中 `FieldId` 屬於 `Type == FieldType.DateTime` 欄位、`DateValue != null` 的列，`ExecuteUpdateAsync(s => s.SetProperty(i => i.DateValue, i => i.DateValue!.Value.AddMinutes(delta.TotalMinutes)))`。不寫 raw SQL；在 `ITransactionalRequest` 的交易內（PostgreSQL 的全庫寫入鎖在交易開始時已取）。註解寫「兩種資料庫已由 TimeZoneChangeTests 驗證」，不憑推測宣稱。
- `DemoService` 種子裡的 `+08:00`（`RepairScenario.cs:67`）是一個絕對時間，寫入時會被正規化，保留不改。

- [ ] **Step 1: 寫失敗的測試**
  - `BusinessClockTests`：既有案例改成傳 `+08:00`，斷言不變；新增 `+07:00` 時 `2026-11-11T10:00:00+08:00` → `2026-11-11 09:00`；`-12:00` 時 `0001-01-01T05:00:00Z` 回 false（下限溢位），`+14:00` 時 `9999-12-31T20:00:00Z` 回 false。
  - `DateTimeHandlerTests.Normalize_UsesGivenOffset`：偏移 +07:00，`2026-11-11T02:00:00Z` → `"2026-11-11T09:00:00+07:00"`；`ToIndex` 的 `DateValue == 2026-11-11 09:00`；`-12:00` 時下限值回欄位錯誤、不丟例外。既有測試（無參數建構）不改。
  - `FieldDisplayTests.DateTime_UsesGivenOffset`：值 `2026-11-11T10:00:00+08:00`、偏移 +07:00 → `"2026-11-11 09:00"`。
  - `FieldImportParserTests.DateTime_LocalTextUsesBusinessOffset`：`2026-11-11 09:00`、偏移 +07:00 → `"2026-11-11T09:00:00+07:00"`。
  - `TimeZoneChangeTests`（類別 `Category=Postgres`）：
    - `TimeZoneChange_ShiftsDateTimeIndexOnly`：一筆記錄有日期時間 `2026-11-11T10:00:00+08:00` 與日期 `2026-11-11`；PUT `+07:00` 後日期時間索引 09:00、日期索引仍 00:00；重新讀回的 `DateValue.Kind == Unspecified`；記錄原值字串不變；改回 `+08:00` 回到 10:00。
    - `TimeZoneChange_FractionalOffset`：`+08:00` 改 `+05:45`，索引 07:45。
    - `TimeZoneChange_ReportDayBucketFollowsNewZone`：`2026-11-11T00:30:00+08:00` 在 +08:00 分到 11-11，改 `+07:00` 後分到 11-10；SQL 路徑與記憶體路徑結果相同（沿用 `ReportSqlEquivalenceTests` 的比較 helper；pg1 已合併，直接進雙跑集）。
    - `TimeZoneChange_ListFilterBetweenFollowsNewZone`：列表篩選 `between 2026-11-11T09:00 ~ 09:30`（不帶偏移）在 +07:00 時找得到上面那筆。
    - `SameValue_NoShift`：PUT 同一個值，索引不動。
    - `HookRejects_IndexAndSettingRolledBack`：測試用第二個 hook 在 `TimeZoneChangeHook` 之後回失敗 → 400，索引仍 10:00、設定沒寫入。
    - `ShiftOverflowsUpper_Rejected`（必修 3）：一筆 `9999-12-31T23:30:00+08:00`（索引 `9999-12-31 23:30`），PUT `+09:00` → 400，`errors.value[0]` 是 `改成這個時區會讓 1 筆日期時間超出可儲存的範圍（9999-12-31 23:30 ～ 9999-12-31 23:30），請先修改這些記錄再改時區`；斷言設定列不存在、該筆索引仍 `9999-12-31 23:30`、其他記錄的日期時間索引也沒動、快照仍 8h。
    - `ShiftOverflowsLower_Rejected`（必修 3）：一筆 `0001-01-01T08:30:00+08:00`（UTC `00:30`，索引 `0001-01-01 08:30`），PUT `-12:00`（delta −20h）→ 400，同樣斷言四項都沒變。
    - `ShiftAtBoundary_Allowed`：一筆 `9999-12-31T22:30:00+08:00` 改 `+09:00`（平移後 23:30，仍在範圍內）→ 200、索引 `9999-12-31 23:30`、讀回 `Kind == Unspecified`。
  - `BusinessTimeZoneApiTests.Export_FollowsZoneUnderActingDecorator`：用 `TestApp`（`IBusinessTime` 是 `ActingBusinessTime(Clock)`）；admin PUT `+07:00` 後，匯出含 `2026-11-11T10:00:00+08:00` 的記錄，CSV 那格 `2026-11-11 09:00`；歷程顯示與列印值也是 09:00；改回 `+08:00` 後 10:00。
- [ ] **Step 2: 跑測試確認失敗**：`cd backend && dotnet test tests/DynamicModules.DomainTests --filter "FullyQualifiedName~BusinessClockTests|FullyQualifiedName~DateTimeHandlerTests"`；`dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~FieldDisplayTests|FullyQualifiedName~FieldImportParserTests|FullyQualifiedName~TimeZoneChangeTests"`；`dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~BusinessTimeZoneApiTests"`
- [ ] **Step 3: 實作**（先改 `BusinessClock` 讓編譯錯誤列出所有呼叫點，逐一改）
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；另跑 ApplicationTests `--filter "FullyQualifiedName~Report|FullyQualifiedName~TaskRule|FullyQualifiedName~Import|FullyQualifiedName~RecordIndex|FullyQualifiedName~Export|FullyQualifiedName~Print|FullyQualifiedName~History"`；PostgreSQL：ApplicationTests `(FullyQualifiedName~TimeZoneChangeTests|FullyQualifiedName~RecordIndexTests|FullyQualifiedName~ReportSqlEquivalenceTests)`、WebAPITests `BusinessTimeZoneApiTests`（`AddMinutes` 翻譯錯會在這裡出現，不能只看 SQLite）；殘留掃描：`grep -rnE 'Taipei|BusinessClock\.Offset|FieldDisplay\.BusinessOffset|"\+08:00"' backend/src --include='*.cs' | grep -v "DemoService\|Seed\|PolicyCatalog"` 應無結果；`grep -rn "FromHours(8)" backend/src --include='*.cs'` 只剩 `Program.cs:112` 的 cookie `ExpireTimeSpan`
- [ ] **Step 5: Commit**（`feat(policy): 後端日期時間改用營運時區政策，改時區時平移索引（A17）`）

**驗收**：Step 4 全過（含 PostgreSQL，`TimeZoneChangeTests` 的上下限三案在兩種資料庫都過）；兩條殘留掃描符合預期；交付說明寫明 `ShiftDateTimeIndexAsync` 是交易內的 `ExecuteUpdate`。

### Task 5: 前端時區改從 API 取

**Files:**
- Create: `frontend/src/domain/dynamic/business-time.ts`、`frontend/src/application/dynamic/session-settings.ts`
- Modify: `frontend/vitest.setup.ts`（加 `setBusinessOffset('+08:00')`，註解寫「等於後端出廠值，只給測試用」；不另建檔、不改 `vitest.config.ts`）
- Modify: `frontend/src/router/auth-guard.ts:26-34,51-60`（判斷 16；`handleUnauthorized` 重置已載入）、`frontend/src/application/dynamic/queries/auth-queries.ts`（`useLogout` 重置已載入）
- Modify: `frontend/src/presentation/dynamic/field-type-registry.ts:99-102,128-130,142,162,283`（`localToday` 改用 `businessToday`）、`frontend/src/domain/dynamic/rule-engine.ts:25,85`、`frontend/src/components/framework/DateSelect.vue:4,11`、`frontend/src/presentation/reports/designer/FilterValueInput.vue:57-64`、`frontend/src/presentation/dynamic/rule-state.ts:5-9`（`todayLocal` 改名 `todayBusiness`，`:42` 等呼叫端一併改）
- Modify: `frontend/src/pages/system/PoliciesPage.vue`（存 `locale.timeZone` 前確認、成功後 `setBusinessOffset`）、`frontend/src/i18n/*.json`（`policies.timeZoneWarning`、`policies.confirm`、`policies.timeZoneChanged`、`policies.reload`）
- Create: `frontend/src/application/dynamic/queries/use-business-time-watch.ts`（判斷 18）；Modify: `frontend/src/layouts/MainLayout.vue`
- 不改：`FieldEditorDialog.vue:248` 的編號格式範例預覽（`new Date()`，只是格式示意，不影響存檔值）
- Test: `frontend/src/domain/dynamic/business-time.vitest.spec.ts`（新）、`frontend/src/application/dynamic/session-settings.vitest.spec.ts`（新）、`field-type-registry.vitest.spec.ts`、`rule-engine.vitest.spec.ts`、`rule-state.vitest.spec.ts`、`frontend/src/components/framework/DateSelect.vitest.spec.ts`（新）、`frontend/src/presentation/reports/designer/FilterValueInput.vitest.spec.ts`（新）、`frontend/src/router/auth-guard.vitest.spec.ts`、`frontend/src/pages/auth/LoginPage.vitest.spec.ts`、`frontend/src/pages/system/PoliciesPage.vitest.spec.ts`、`frontend/src/application/dynamic/queries/use-business-time-watch.vitest.spec.ts`（新）、`frontend/src/layouts/MainLayout.vitest.spec.ts`

**Interfaces:**
- `business-time.ts`：`setBusinessOffset(offset: string): void`（不合 `^[+-]\d{2}:\d{2}$` 丟錯）；`businessOffset(): string`（沒設定過丟 `Error('business time zone not loaded')`）；`businessOffsetMs(): number`；`isBusinessOffsetLoaded(): boolean`；`toBusinessWallClock(iso: string): string | null`（帶 Z 或任何偏移 → 營運時區 `yyyy-MM-ddTHH:mm:ss`；不帶偏移視為已是營運時區、原樣補秒；解析不了回 null）；`withBusinessOffset(wall: string): string`；`businessToday(now?: Date): string`。檔頭註解寫「只能在登入後的頁面呼叫，未設定就丟錯」。
- `session-settings.ts`：`sessionSettingsLoaded(): boolean`；`resetSessionSettings(): void`（只清「已載入」旗標，不清已設定的偏移，避免登出瞬間仍在畫面上的元件丟錯）；`loadSessionSettings(): Promise<void>`：`const eff = await dynamicApi.getEffectivePolicies(null); setBusinessOffset(String(eff.values['locale.timeZone']))`（Task 12 再加 `setLocaleSettings({ requiredText })`）。
- 登出與 session 過期（審查建議）：`useLogout` 的 `onSuccess`（`auth-queries.ts`）與 `handleUnauthorized`（`auth-guard.ts`）都呼叫 `resetSessionSettings()`，下次登入後的第一次導覽重新載入，不沿用上一個 session 的值。
- auth guard（判斷 16）：在 `if (!identity.userId) …` 之後、強制改密碼判斷之前：`if (!to.meta.public && !sessionSettingsLoaded()) { try { await loadSessionSettings(); } catch { return { path: SERVICE_UNAVAILABLE, query: … } } }`。公開頁面（登入、service-unavailable）不載也不呼叫 `businessOffset()`。
- `use-business-time-watch.ts`：`useBusinessTimeWatch(): void`：`useQuery({ queryKey: ['effective-policies', ''], queryFn: () => dynamicApi.getEffectivePolicies(null), staleTime: 60_000, refetchInterval: 60_000, refetchIntervalInBackground: false, refetchOnWindowFocus: true })`（與 `useEffectivePolicies(null)` 同 key）；`locale.timeZone` 與 `businessOffset()` 不同時先 `setBusinessOffset(新值)`，再 `Dialog.create({ message: t('policies.timeZoneChanged', { offset: 'UTC' + 新值 }), persistent: true, ok: { label: t('policies.reload') }, cancel: false })`，按下 `window.location.reload()`；同一個新值只跳一次。
- 文案（zh-TW）：`policies.timeZoneWarning`＝設計 §3.5 原文；`policies.confirm`＝「確定」；`policies.timeZoneChanged`＝「營運時區已改成 {offset}，請重新整理頁面，日期時間才會正確顯示與存檔」；`policies.reload`＝「重新整理」。四語都要有。

- [ ] **Step 1: 寫失敗的測試**
  - `business-time.vitest.spec.ts`：未設定時丟錯（`vi.resetModules` 取乾淨模組）；`+07:00` 時 `toBusinessWallClock('2026-11-11T10:00:00+08:00') === '2026-11-11T09:00:00'`、`('2026-11-11T02:00:00Z') === '2026-11-11T09:00:00'`、`('2026-11-11T09:00') === '2026-11-11T09:00:00'`；`withBusinessOffset('2026-11-11T09:00') === '2026-11-11T09:00:00+07:00'`；`businessToday(new Date('2026-11-10T17:30:00Z'))` 在 +07:00 是 `2026-11-11`、在 +05:30 是 `2026-11-10`。
  - `session-settings.vitest.spec.ts`：mock 回 `+07:00` → `loadSessionSettings()` 後 `businessOffset() === '+07:00'`、`sessionSettingsLoaded()` true；API 失敗時丟錯、仍 false。
  - `field-type-registry.vitest.spec.ts`：`displays stored offset in business zone`（+07:00 顯示 `2026-11-11 09:00`；`normalizeForRules` 回 `…+07:00`）；`default today uses business zone`（`defaultValueOf` 的預設今天）。既有 +08:00 斷言不改。
  - `DateSelect.vitest.spec.ts`：`emits business offset`（+07:00 輸入 `2026-11-11T09:00` emit `2026-11-11T09:00:00+07:00`；modelValue `…T10:00:00+08:00` 時顯示 `2026-11-11T09:00`）。
  - `FilterValueInput.vitest.spec.ts`：`converts other offsets`（+07:00 時 `…+08:00` 的值顯示換算後的牆上時間）。
  - `rule-engine.vitest.spec.ts`：+07:00 時 datetime 比較以營運時區的日期切點為準。`rule-state.vitest.spec.ts`：`todayBusiness` 用營運時區。
  - `auth-guard.vitest.spec.ts`：`登入後設定營運時區`；`取生效值失敗導到 service-unavailable`；`公開頁面導覽不需要營運時區`（未設定的模組、未登入導到 `/login` 成功、不呼叫 `getEffectivePolicies`）；`登入後的下一次導覽補載`（S4：第一次導覽未登入；之後 store 設成已登入再導覽到 `/` → 呼叫 `getEffectivePolicies` 一次、`businessOffset()` 已設定；再導覽不重打）；`登出後重登入重新載入`：已載入 `+07:00` → 登出（`resetSessionSettings`）→ 伺服器改回 `+08:00` → 再登入並導覽 → 又呼叫一次 `getEffectivePolicies`、`businessOffset() === '+08:00'`；`session 過期後重登入重新載入`：`handleUnauthorized` 之後同上。
  - `LoginPage.vitest.spec.ts`：登入成功後（不重新整理）導到首頁，`businessOffset()` 已設定。
  - `PoliciesPage.vitest.spec.ts`：`改時區要先確認`（對話框文字含 `記錄的日期時間欄位保持原本的絕對時間` 與 `系統自記的時間不換算`；取消不送出；確定後成功 `businessOffset()` 是新值）。
  - `use-business-time-watch.vitest.spec.ts`：`時區沒變不提示`；`時區變了先換偏移再要求重新整理`（`persistent: true`、訊息含 `UTC+07:00`、按確定呼叫 `window.location.reload`）；`同一個新值只提示一次`。
  - `MainLayout.vitest.spec.ts`：既有案例補 mock `getEffectivePolicies` 回出廠值，斷言不變。
- [ ] **Step 2: 跑測試確認失敗**：`cd frontend && bunx vitest run src/domain/dynamic/business-time.vitest.spec.ts src/application/dynamic src/presentation/dynamic/field-type-registry.vitest.spec.ts src/presentation/dynamic/rule-state.vitest.spec.ts src/domain/dynamic/rule-engine.vitest.spec.ts src/components/framework src/presentation/reports/designer src/router src/pages/auth src/pages/system/PoliciesPage.vitest.spec.ts src/layouts --maxWorkers=2`
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；`bunx vitest run src/domain src/presentation/dynamic src/i18n --maxWorkers=2`；`bun run typecheck`；`grep -rnE "08:00|8 \* 3_?600_000|Taipei|台北" src --include='*.ts' --include='*.vue' | grep -v "spec.ts"` 應無結果
- [ ] **Step 5: Commit**（`feat(policy): 前端日期時間改用 API 的營運時區（A17）`）

**驗收**：Step 4 全過；殘留掃描無結果；Review Focus 2 的四件事各有測試。

### Task 6: 稽核與維運畫面的時間照營運時區（使用者 2026-11-20 裁決）

**Files:**
- Modify: `frontend/src/domain/ops/ops-format.ts:69-75`（`formatTime`）、`:133-139`（`localDayStartUtc`、`localDayEndUtc` 改名 `businessDayStartUtc`、`businessDayEndUtc`）
- Modify: `frontend/src/pages/system/AuditPage.vue:10,46-47`（改用新名稱；日期篩選的輸入框加 hint 顯示目前時區 `UTC+08:00`）、`frontend/src/i18n/*.json`（`audit.filters.zoneHint`「依營運時區 {offset}」四語）
- 用到 `formatTime` 的畫面不改程式，只重看版面：`AuditPage.vue`、`AuditDetailPanel.vue`、`IncidentDetailDialog.vue`、`HealthCheckCard.vue`、`OpsIncidentsTab.vue`、`OpsOverviewTab.vue`、`OpsChannelTab.vue`、`TaskRulesTab.vue`
- 不改：歷程與列表的系統欄位（D1 裁決不標時區）
- Test: `frontend/src/domain/ops/ops-format.vitest.spec.ts`、`frontend/src/pages/system/AuditPage.vitest.spec.ts`

**Interfaces:**
- `formatTime(utc)`：UTC ISO → 營運時區 `YYYY-MM-DD HH:mm +08:00`（尾綴是 `businessOffset()`）；缺值「—」、解析不了原樣回傳（同現況）。用 `businessOffsetMs()` 加到 UTC 毫秒後以 `getUTC*` 取欄位，不經瀏覽器時區。
- `businessDayStartUtc(d: string)`：`new Date(`${d}T00:00:00${businessOffset()}`).toISOString()`；`businessDayEndUtc(d)`：同一天 0 點加 86_400_000 減 1 ms（固定偏移、沒有日光節約，一天固定 24 小時）。
- 後端不改：稽核與維運時間本來就存 UTC，API 收 `fromUtc`／`toUtc`。

- [ ] **Step 1: 寫失敗的測試**
  - `ops-format.vitest.spec.ts`：`+07:00` 時 `formatTime('2026-11-20T03:00:00Z') === '2026-11-20 10:00 +07:00'`；`businessDayStartUtc('2026-11-20') === '2026-11-19T17:00:00.000Z'`、`businessDayEndUtc('2026-11-20') === '2026-11-20T16:59:59.999Z'`；在測試裡把 `process.env.TZ` 設成 `America/New_York` 再跑一次，結果相同（不受瀏覽器時區影響）。既有依本機時區的斷言改成營運時區的值。
  - `AuditPage.vitest.spec.ts`：日期篩選 `2026-11-20` 送出 `fromUtc` 是營運時區當天 0 點；列表時間欄有 `+08:00`；篩選輸入框的 hint 含 `UTC+08:00`。
- [ ] **Step 2: 跑測試確認失敗**：`cd frontend && bunx vitest run src/domain/ops src/pages/system/AuditPage.vitest.spec.ts --maxWorkers=2`
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；`bunx vitest run src/presentation/ops src/presentation/dynamic/tasks src/i18n --maxWorkers=2`；`bun run typecheck`；`grep -rn "localDayStartUtc\|localDayEndUtc" src` 無結果
- [ ] **Step 5: Commit**（`feat(ops): 稽核與維運畫面的時間照營運時區並標示時區`）

**驗收**：Step 4 全過；Task 7 實機在 390 寬度看稽核列表與維運總覽，時間加尾綴後不橫捲。

### Task 7: p3a 文件、實機與 gate

**Files:**
- Modify: 設計文件：§10 技術限制補 `organization.maxDepth` 1–30、時區選項清單、`CachedSystemPolicy` 的 60／10／2 秒（與 O1 B2 同一份）；§10「已知限制（p3）」本次修訂已寫好任務 `DueDate` 不重算與多台部署改時區兩條，實作若有出入再更新；§12 加技術判斷 3、19、20（判斷 1、5、6、18、26 是使用者裁決）；`docs/roadmap.md` 的 p3 列（p3a 完成）；`docs/feature.md`（p3 改 🚧，跑 `feature_checklist.py docs/feature.md --write`）；`docs/manual.md`（政策設定的「地區與語系」「組織」、改時區的確認框與「其他人頁面會要求重新整理」以畫面為準；報表、任務、匯入章節的「台北時間」「+08:00」改「營運時區（出廠 +08:00）」；稽核頁與維運頁的時間標示與日期篩選依營運時區）；`docs/deploy.md`（多台部署時政策最多 60 秒生效；改時區請在離峰時段、先縮成一台再改；O1 的快取與時區共用同一份，措辭一致）

- [ ] **Step 1: 實機**：四種寬度開 `/system/policies`，把時區改成 `+07:00`（確認框文字、四語各看一次），回到有日期時間欄位的模組：列表與表單少一小時、新增一筆再編輯值不跳動、報表依日分組的切點改變；另開一個已登入的分頁，60 秒內跳出提示且只能按重新整理；登出再從登入頁登入（不重新整理），首頁與日期元件正常；稽核頁時間標 `+07:00`、日期篩選照營運時區；再改回 `+08:00`。組織頁把層數改 2 後新增第 3 層看到錯誤。不得橫向捲動。
- [ ] **Step 2: PostgreSQL 全套試跑**（動到 Persistence；不進 gate）：`FORMWRIGHT_TEST_DB=postgres dotnet test tests/DynamicModules.ApplicationTests --filter "Category!=SqliteOnly&Category!=Benchmark"`、WebAPITests 同一 filter；全數通過，時間與結果貼進 p3 結果檔。
- [ ] **Step 3: gate**：`bash scripts/unattended/gate.sh <worktree 絕對路徑>`，每步 `exit=0`（含 PostgreSQL 兩步）
- [ ] **Step 4: Commit**（`docs: 營運時區與部門層數政策的手冊、可寫死清單與 p3a 進度`）

**驗收**：實機每項有截圖或逐項記錄；PostgreSQL 全套與 gate `exit=0`；`formwright-purpose-check` 無違反。

---

# p3b 語系

### Task 8: 語系三政策、互相約束、選項接口、公開給登入前

**Files:**
- Create: `backend/src/DynamicModules.Application/Common/IInstalledLocales.cs`（`IInstalledLocales`、`FactoryInstalledLocales`）、`backend/src/DynamicModules.Application/Dynamic/PolicyService/PolicyOptionSource.cs`（判斷 22）
- Modify: `backend/src/DynamicModules.Domain/Policies/PolicyDefinition.cs`（`Normalize(string? raw, IReadOnlyList<string>? options = null)`，null 用 `Options`）、`PolicyCatalog.cs`（`LocaleDefault`、`LocaleEnabled`、`LocaleRequiredText`；Options＝`PolicyCatalog.FactoryLocales`（指向 `I18nText.SupportedLocales` 的唯一一處））
- Create: `backend/src/DynamicModules.Domain/Policies/LocalePolicyRules.cs`
- Modify: `backend/src/DynamicModules.Application/Dynamic/PolicyService/PolicyHandlers.cs`（系統 PUT 檢查）、`PolicyModels.cs`（`PolicyJson.FromJson`、回應的 `options` 改收選項參數）、`PolicyMessages.cs`（`Invalid` 收選項；RoleSet 在 `d.Category == "locale"` 時回語系訊息，判斷 21；四條約束訊息）、`PolicyReader.cs`（`Usable` 的正規化用 `PolicyOptionSource`）、`CachedSystemPolicy.cs`（語系三項的同步讀值、`LocaleFallbacks`）
- Modify: `backend/src/DynamicModules.Application/Dynamic/AccountService/Queries/GetAuthOptions/GetAuthOptionsHandler.cs:14,17-19`（兩個回傳點）、`AccountService/Data/AccountResponses.cs:9`（`AuthOptionsResponse`）
- Modify: `backend/src/DynamicModules.Application/DependencyInjection.cs`（`IInstalledLocales`、`PolicyOptionSource` singleton）
- Modify: `shared/policy-catalog.json`（codes 加 3 個；`choices` **不**加語系代碼，判斷 24）、`frontend/src/i18n/*.json`、`frontend/src/domain/dynamic/types.ts`（`AuthOptions` 加兩欄）、`frontend/src/domain/dynamic/policy-i18n.vitest.spec.ts`（語系類政策排除在選項翻譯檢查外）
- Test: `backend/tests/DynamicModules.DomainTests/Policies/LocalePolicyRulesTests.cs`（新）、`PolicyDefinitionTests.cs`、`PolicyCatalogFixtureTests.cs`、`backend/tests/DynamicModules.WebAPITests/Dynamic/PolicyApiTests.cs`、`backend/tests/DynamicModules.WebAPITests/AuthApiTests.cs`（新案例放這裡）、`backend/tests/DynamicModules.WebAPITests/DemoLoginApiTests.cs:97,127,262`（既有 `GET auth/options` 測試，確認欄位增加後照過）、`backend/tests/DynamicModules.ApplicationTests/Common/CachedSystemPolicyTests.cs`

**Interfaces:**
- `public interface IInstalledLocales { IReadOnlyList<string> Codes { get; } }`；`FactoryInstalledLocales` 回 `PolicyCatalog.FactoryLocales`。
- `public sealed class PolicyOptionSource(IInstalledLocales locales)`：`IReadOnlyList<string>? Of(PolicyDefinition def)`：`def.Category == "locale" && def.Kind is Choice or RoleSet` 回 `locales.Codes`，其餘回 `def.Options`。`PolicyJson`、`PolicyMessages.Invalid`、`PolicyReader` 的選項一律從這裡取。
- `public static class LocalePolicyRules { public static string? Check(string defaultLocale, IReadOnlyList<string> enabled, IReadOnlyList<string> required, IReadOnlyCollection<string> installed); }`：回 null 或代碼；Application 轉成下列訊息（放 `PolicyMessages`，原文）：
  - 任一值不在已安裝語系：`只能從以下語系挑選：…`（同判斷 21 的訊息）
  - 啟用為空：`至少要啟用一種語系`
  - 預設不在啟用：`預設語系必須是啟用中的語系；要停用它，請先改預設語系`
  - 必填為空：`至少要有一種必填語系`
  - 必填不在啟用：`必填語系必須都是啟用中的語系；要停用它，請先從必填語系拿掉`
- 系統 PUT：代碼是三者之一時，取「這次的新值（null 時是上一層值）＋另外兩個的生效值」呼叫 `Check`（`installed` 傳 `IInstalledLocales.Codes`），有錯回 400（鍵 `value`），在寫設定之前檢查。
- `CachedSystemPolicy`：`string DefaultLocale`、`IReadOnlyList<string> EnabledLocales`、`IReadOnlyList<string> RequiredTextLocales`、`IReadOnlyList<string> LocaleFallbacks`（`[DefaultLocale, I18nText.FallbackLocale]` 去重），換狀態時一起算好。
- `AuthOptionsResponse(bool DemoLoginEnabled, bool DemoGenerating, List<DemoAccountResponse> DemoAccounts, string DefaultLocale, IReadOnlyList<string> EnabledLocales)`；兩個回傳點都從快照取。i4a 之後只再**加**欄位。
- 文案（zh-TW）：`locale.default` 預設語系／沒選過語言的人看到的語系，也是 API 沒指定語系時的回應語系（使用者 2026-11-20 裁決，Task 10、11a、11b 落實）。`locale.enabled` 啟用語系／語言選單與多語輸入框只列這些；停用不會刪掉已輸入的文字。`locale.requiredText` 必填的多語文字要有哪些語系／名稱、標籤、選項、訊息等所有必填的多語文字都要填這些語系；增加後只在新增或修改那段文字時檢查；應用程式套件仍要求繁體中文。選項以各語言自稱顯示（同 `frontend/src/i18n/locale.ts`）。

- [ ] **Step 1: 寫失敗的測試**
  - `LocalePolicyRulesTests`：出廠組合 null；五種錯各一案。
  - `PolicyDefinitionTests.Normalize_UsesGivenOptions`：`Normalize("ja", ["zh-TW","ja"])` 通過、不傳選項時不通過。
  - `PolicyCatalogFixtureTests.Factories_EqualCurrentBehavior` 加三項；`Catalog_MatchesSharedFixture` 的 `choices` 排除語系類。
  - `PolicyApiTests.LocaleRules_DisableDefault_400`（`Category=Postgres`）：PUT `locale.enabled` `["en-US"]` → 400、訊息含「請先改預設語系」。
  - `PolicyApiTests.LocaleRules_SwitchToEnglishInOrder`（`Category=Postgres`）：依序 PUT `locale.default` `"en-US"` → `locale.requiredText` `["en-US"]` → `locale.enabled` `["en-US"]` 三次 200；再 PUT `locale.default` `null` → 400。
  - `PolicyApiTests.LocaleEnabled_UnsupportedLocale_SaysLocale`：PUT `["zh-TW","ja"]` → 400，`errors.value[0] == "只能從以下語系挑選：zh-TW、en-US、th、vi"`；通知收件角色的 RoleSet 錯誤仍是 `只能從以下角色挑選：…`。
  - `PolicyApiTests.LocaleOptions_FromInstalledLocales`：測試替換 `IInstalledLocales` 回 `["zh-TW","en-US","ja"]` 時，GET 的 `locale.enabled` `options` 有 `ja`、PUT `["zh-TW","ja"]` 200（i4a 接口）。
  - `AuthApiTests.Options_IncludesLocaleSettings`（`Category=Postgres`）：未登入 GET `auth/options` 含 `defaultLocale: "zh-TW"`、`enabledLocales` 四個；PUT `locale.default` `"en-US"` 後未登入再 GET 回 `en-US`。
  - `AuthApiTests.Options_DemoLoginDisabled_StillIncludesLocales`（S9）：Demo 登入關閉時回應仍有兩個語系欄位。
  - `CachedSystemPolicyTests.LocaleFallbacks_DefaultFirst`：預設 `en-US` 時 `LocaleFallbacks == ["en-US","zh-TW"]`；出廠時 `["zh-TW"]`。
- [ ] **Step 2: 跑測試確認失敗**：`cd backend && dotnet test tests/DynamicModules.DomainTests --filter "FullyQualifiedName~Policies"`；`dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~PolicyApiTests|FullyQualifiedName~AuthApiTests|FullyQualifiedName~DemoLoginApiTests"`；`dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~CachedSystemPolicyTests"`
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；PostgreSQL：WebAPITests `(FullyQualifiedName~PolicyApiTests|FullyQualifiedName~AuthApiTests)`；前端 `bunx vitest run src/domain/dynamic/policy-i18n.vitest.spec.ts src/i18n --maxWorkers=2`；`grep -rn "SupportedLocales" backend/src | wc -l` 只比開工前多 1（`PolicyCatalog.FactoryLocales`，判斷 23）
- [ ] **Step 5: Commit**（`feat(policy): 登記語系政策與互相約束，登入前可取預設與啟用語系`）

**驗收**：Step 4 全過（含 PostgreSQL）；`SupportedLocales` 使用點只多一處。

> Task 8 登記後到 Task 12 之前，畫面上的語系政策還沒生效；p3b 整段一起合併。

### Task 9: 多語文字的退回鏈與必填語系（後端）

D5 已裁決：名稱補進每個必填語系。

**Files:**
- Modify: `backend/src/DynamicModules.Domain/ValueObjects/I18nText.cs`
- Create: `backend/src/DynamicModules.Application/Common/RequiredTextGuard.cs`（`RequiredTextGuard`、`LocaleNames`）
- Modify（FluentValidation，建立類改用 guard、更新類只留格式檢查）：`FieldService/Data/FieldRules.cs`（`ValidLabel`，`ViewRules.cs:24` 經它間接檢查）、`FieldService/Commands/{AddField,UpdateField,AddOption,UpdateOption,AddBlock,UpdateBlock}/*Validator.cs`、`ModuleService/Data/ModuleRules.cs`（與用到它的建立、更新模組 validator）、`ViewService/Data/ViewRules.cs:24`
- Modify（更新類 handler 補「改了才檢查」）：`UpdateFieldHandler`、`UpdateOptionHandler`、`UpdateBlockHandler`、`UpdateModuleHandler`、檢視的更新 handler
- Modify（handler 內的驗證器，17 個呼叫端以 `grep -rn "IsValid(out" backend/src` 為準）：`OrganizationService/OrganizationHandlers.cs`、`EventService/EventValidator.cs`、`RuleService/Commands/SaveRules/SaveRulesHandler.cs`、`TaskRuleService/TaskRuleValidator.cs`、`ApprovalService/ApprovalDefinitionValidator.cs:78`、`ApprovalService/ApprovalDefinitionValidator.Graph.cs:39,49,60,72,113`（流程圖節點名稱、邊標籤、樣板）、`ApprovalService/ApprovalFlowHandlers.cs:95`、`ConversionService/ConversionValidator.cs`、`CheckService/CheckValidator.cs`
- Modify（以繁中判斷必填）：`TaskRuleService/TaskRuleValidator.cs:130`（`text.Resolve(I18nText.FallbackLocale)` 判斷任務標題必填，改走 `RequiredTextGuard`；審查必修 4 盤點時找到）
- Modify（寫死「必須有繁體中文」的訊息改走 guard 的訊息）：`ApprovalService/ApprovalMessages.cs:31,32,35,98`、`TaskRuleService/TaskRuleModels.cs:41`、`ApprovalDefinitionValidator.Graph.cs` 的 `NodeNameRequired`、`EdgeLabelInvalid`、`TemplateRequired`
- Modify：`AppService/AppPackageValidator.cs:150`（套件仍要求繁中，改用 `IsWellFormed`＋`HasText(FallbackLocale)`，錯誤文字不變）；`AppPackageValidator.V7.cs:86` 的不支援語系鍵檢查改呼叫 `I18nText` 的同一個隔離方法（判斷 23）
- Modify：`AccountService/Commands/CreateGroup/CreateGroupHandler.cs:22`（名稱寫進目前語系與每個必填語系，D5）
- Test: `backend/tests/DynamicModules.DomainTests/ValueObjects/I18nTextTests.cs`、`backend/tests/DynamicModules.ApplicationTests/Dynamic/Policies/RequiredTextPolicyTests.cs`（新）、`FieldServiceTests.cs`、`backend/tests/DynamicModules.WebAPITests/Dynamic/AppFormat7Tests.cs`

**Interfaces:**
- Domain `I18nText`：
  - `Resolve(string locale, IReadOnlyList<string>? fallbacks = null)`：要求的語系 → `fallbacks`（null 時是 `[FallbackLocale]`）依序 → `SupportedLocales` 順序中第一個有值的 → `""`（判斷 13）。既有呼叫端不改也能編譯。
  - 刪 `IsValid(out string? error)`；新增 `IsWellFormed(out string? error)`：沒有不支援的語系鍵（`不支援的語系：{key}`，文字不變），且至少一個語系有非空白文字（`至少要有一種語系的文字`）。不支援語系的判斷集中在 `private static string? UnknownLocale(IEnumerable<string> keys)`（判斷 23）。
  - `HasText(string locale)`；`MissingLocales(IEnumerable<string> required) : IReadOnlyList<string>`；`SameAs(I18nText? other)`（各語系去空白、忽略空值後比對）。
- `public sealed class RequiredTextGuard(CachedSystemPolicy cache)`（singleton）：`string? Check(I18nText? incoming, I18nText? stored, string subject)`：
  - `incoming` 為 null 或不 `IsWellFormed` → `$"{subject}必須有{Names(required)}"`；
  - `stored` 不為 null 且 `incoming.SameAs(stored)` → null（§3.5）；
  - 否則 `missing = incoming.MissingLocales(cache.RequiredTextLocales)`，有缺 → `$"{subject}必須有{Names(missing)}"`。
  - `Names` 用頓號串 `LocaleNames.Zh`（`zh-TW` 繁體中文、`en-US` 英文、`th` 泰文、`vi` 越南文；不在表內的代碼原樣輸出，i4a 自訂語系用）。出廠值時與現在逐字相同：`名稱必須有繁體中文`、`關卡名稱必須有繁體中文`、`訊息必須有繁體中文`。
- 各處 subject：名稱類 `名稱`、簽核關卡 `關卡名稱`、檢查訊息 `訊息`；流程圖的節點、邊標籤、樣板沿用現有訊息的主詞。`SaveRulesHandler` 的「驗證規則必須有訊息」不提語系，維持原文。
- FluentValidation：建立類 validator 注入 `RequiredTextGuard`，`.Custom((v, ctx) => { if (guard.Check(v, null, "名稱") is { } e) ctx.AddFailure(e); })`；更新類 validator 只檢查 `IsWellFormed`，handler 載入既有實體後呼叫 `guard.Check(request.Label, entity.LabelI18n, "名稱")`，錯誤鍵沿用原本的屬性名。
- handler 內驗證器：新增時 `stored` 傳 null；更新時傳資料庫裡的值（同一檔內做法一致）。

- [ ] **Step 1: 寫失敗的測試**
  - `I18nTextTests`：`IsWellFormed` 對 `{}`、`{"zh-TW":" "}` false、`{"en-US":"Order"}` true、`{"jp":"x"}` false 且 `不支援的語系：jp`；`Resolve("th")` 對 `{"en-US":"Order"}` 回 `Order`；`Resolve("th", ["en-US","zh-TW"])` 對 `{"zh-TW":"訂單","en-US":"Order"}` 回 `Order`、不傳鏈時回 `訂單`；`SameAs` 忽略空白與空值。
  - `RequiredTextPolicyTests.Factory_MessagesUnchanged`：出廠值下缺繁中的欄位、事件、部門、簽核關卡、流程圖節點、檢查訊息，錯誤文字各與現在相同。
  - `RequiredTextPolicyTests.AddField_RequiresEveryRequiredLocale`：必填 `["zh-TW","en-US"]` → 只給繁中 400、`errors.Label[0] == "名稱必須有英文"`。
  - `RequiredTextPolicyTests.UpdateField_UnchangedLabel_SkipsNewRequiredLocale`（`Category=Postgres`）：既有只有繁中的欄位，必填改繁中＋英文後，送同樣標籤只改「必填」→ 200。
  - `RequiredTextPolicyTests.UpdateField_ChangedLabel_RequiresNewLocale`（`Category=Postgres`）：改繁中標籤沒補英文 → 400；補了英文 → 200。
  - `RequiredTextPolicyTests.EnglishOnly_NewFieldWithoutZhTw_ResolvesToEnglish`：必填只有英文時，只給英文的新欄位存檔成功，`schema` 以 `th` 讀到英文標籤。
  - `RequiredTextPolicyTests.CreateGroup_*`：必填繁中＋英文、越南文介面建「Kho」→ 三語都是「Kho」（D5）。
  - `AppFormat7Tests.OpenPackage_IgnoresRequiredTextPolicy`：必填改繁中＋英文後，開啟只有繁中名稱的套件成功；套件缺繁中仍 400 `zh-TW 為必填`。
  - 既有名稱驗證測試不改斷言。
- [ ] **Step 2: 跑測試確認失敗**：`cd backend && dotnet test tests/DynamicModules.DomainTests --filter "FullyQualifiedName~I18nText"`；`dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~RequiredTextPolicyTests|FullyQualifiedName~FieldServiceTests"`
- [ ] **Step 3: 實作**（先改 Domain，讓編譯錯誤列出全部 17 個 `IsValid(out` 呼叫端，逐一改）
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；另跑 ApplicationTests `--filter "FullyQualifiedName~Event|FullyQualifiedName~TaskRule|FullyQualifiedName~Approval|FullyQualifiedName~Conversion|FullyQualifiedName~Check|FullyQualifiedName~Department|FullyQualifiedName~View|FullyQualifiedName~ModuleService|FullyQualifiedName~Template|FullyQualifiedName~Group"`；WebAPITests `--filter "FullyQualifiedName~AppFormat|FullyQualifiedName~AppPackageFormat|FullyQualifiedName~ApprovalApiTests|FullyQualifiedName~AdminGroup"`；PostgreSQL：ApplicationTests `RequiredTextPolicyTests`；`grep -rn "IsValid(out" backend/src` 無結果；`grep -rn "必須有繁體中文" backend/src` 只剩 `LocaleNames` 與套件相關訊息
- [ ] **Step 5: Commit**（`feat(policy): 多語文字的退回鏈與必填語系改讀政策（A18）`）

**驗收**：Step 4 全過（含 PostgreSQL）；兩條 grep 符合預期。

### Task 10: 請求語系與「沒指定語系」的預設（使用者 2026-11-20 裁決）

D4 已裁決：查詢參數帶停用語系時用預設語系。

**Files:**
- Modify: `backend/src/DynamicModules.WebAPI/Security/HeaderCurrentLocale.cs`、`backend/src/DynamicModules.Application/Common/ICurrentLocale.cs`（`FallbackCurrentLocale` 改取快照）
- Create: `backend/src/DynamicModules.Application/Common/RequestLocale.cs`（`LocaleContext`、`RequestLocale`，Task 11a、11b 的退回鏈也用它）
- Modify（套件開啟的 body 預設）：`AppService/Data/AppResponses.cs:84`（`Locale ?? I18nText.FallbackLocale` → 可空，`AppOpenPlanner`／`OpenApp` 用 `requestLocale.For(body.Locale)`）
- Modify（查詢參數預設 `"zh-TW"` → `string? locale = null`，request record 的 `Locale` 改可空）：`ReportsController.cs:54,58,63,67,98,102,108,115`、`DashboardsController.cs:66,70`、`AdminDashboardsController.cs:69`、`RecordsController.cs:57`、`ImportExportController.cs:16`
- Modify（body 預設）：`AdminGroupsController.cs:28,32`（`body.Locale ?? "zh-TW"` → 直接傳 `body.Locale`，handler 決定）
- Modify（handler 內預設）：`ExportRecordsHandler.cs:51`、`PrintRecordHandler.cs:47`、`GetRecordHistoryHandler.cs:57`、`ReportRunner.cs:97`、報表與儀表板的其他 handler（以 controller 改成可空後的編譯錯誤為準）、`CreateGroupHandler`、`RenameGroupHandler`
- Test: `backend/tests/DynamicModules.WebAPITests/LocaleApiTests.cs`、`backend/tests/DynamicModules.WebAPITests/Dynamic/LocaleDefaultApiTests.cs`（新，`Category=Postgres`）

**Interfaces:**
- `HeaderCurrentLocale(IHttpContextAccessor accessor, CachedSystemPolicy cache)`：標頭值在 `cache.EnabledLocales` 內就用它，否則（缺值、不認得、已停用、沒有 HttpContext）`cache.DefaultLocale`。`FallbackCurrentLocale(CachedSystemPolicy cache)`：`Locale => cache.DefaultLocale`。`ICurrentLocale` 的註解改「缺值、不認得或已停用時用預設語系（政策 locale.default）」。
- `public sealed record LocaleContext(string Locale, IReadOnlyList<string> Fallbacks)`；`RequestLocale(ICurrentLocale current, CachedSystemPolicy cache)`（scoped）：`LocaleContext Current => new(current.Locale, cache.LocaleFallbacks)`；`LocaleContext For(string? requested)`：`requested` 在 `cache.EnabledLocales` 內就用它，否則（null、空字串、不認得、已停用）用 `current.Locale`（D4）。Application 擴充方法 `text.Resolve(LocaleContext lc) => text.Resolve(lc.Locale, lc.Fallbacks)`。
- handler 的語系：`var lc = requestLocale.For(request.Locale);`，之後所有輸出用 `lc`（Task 11a 把這些 handler 內的 `Resolve(locale)` 換成 `Resolve(lc)`）。
- 群組：`CreateGroupRequest`／`RenameGroupRequest` 的 `Locale` 改 `string?`，handler 用同一行規則；同名檢查用決定後的語系。

- [ ] **Step 1: 寫失敗的測試**
  - `LocaleApiTests.DisabledLocaleHeader_FallsBackToDefault`：啟用 `["zh-TW","en-US"]`、預設 `en-US` → 帶 `X-Locale: th` 的錯誤訊息是英文；不帶標頭也是英文；帶 `zh-TW` 是中文。既有測試不改（類別註解改「退回預設語系，出廠繁中」）。
  - `LocaleDefaultApiTests`（`Category=Postgres`）：預設 `en-US`、不帶標頭也不帶 `?locale=`：
    - `Export_UsesDefaultLocale`：CSV 表頭與選項標籤是英文。
    - `Print_UsesDefaultLocale`、`History_UsesDefaultLocale`、`ReportRun_UsesDefaultLocale`、`DashboardCard_UsesDefaultLocale`：標籤是英文。
    - `CreateGroup_NoLocale_WritesDefaultLocale`：名稱寫進 `en-US`。
    - `ExplicitLocale_StillWins`：帶 `?locale=zh-TW` 時是中文。
    - `DisabledQueryLocale_UsesDefault`（D4）：啟用 `["zh-TW","en-US"]`、預設 `en-US`，匯出與報表帶 `?locale=th` → 英文；帶 `?locale=xx` 也是英文。
    - `OpenApp_NoLocale_UsesDefault`：開啟套件不給 `locale` 時，群組名稱以預設語系解析與檢查同名。
    - 出廠值時（預設 zh-TW）上述各端點與現在輸出相同（既有測試照過即可，不另寫）。
- [ ] **Step 2: 跑測試確認失敗**：`cd backend && dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~LocaleApiTests|FullyQualifiedName~LocaleDefaultApiTests"`
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；另跑 WebAPITests `--filter "FullyQualifiedName~Locale|FullyQualifiedName~MessageText|FullyQualifiedName~Report|FullyQualifiedName~Dashboard|FullyQualifiedName~Export|FullyQualifiedName~Print|FullyQualifiedName~AdminGroup"`；PostgreSQL：WebAPITests `LocaleDefaultApiTests`；`grep -rn 'string locale = "zh-TW"\|?? "zh-TW"\|? "zh-TW" :' backend/src` 無結果
- [ ] **Step 5: Commit**（`feat(policy): 請求沒指定語系時用預設語系政策`）

**驗收**：Step 4 全過（含 PostgreSQL）；grep 無結果。

### Task 11a: 輸出標籤與訊息內插改用請求語系與退回鏈（使用者 2026-11-20 裁決；審查必修 4）

**做法**：所有輸出給使用者的 `I18nText.Resolve(locale)` 改成 `Resolve(lc)`（`lc` 是 Task 10 的 `LocaleContext`，含退回鏈）。`I18nText.Resolve` 的 `fallbacks` 參數維持可選（測試碼約 118 處單參數呼叫不改），完整性改靠 Step 4 的殘留掃描：`backend/src` 下單參數的 `I18nText.Resolve(…)` 只能剩白名單。

**Files（逐處處置；行號以 main `52393c6f` 為準，以符號核對）：**
- A. 已用請求語系、缺退回鏈 → `Resolve(lc)`：
  - 匯出：`ExportService/Queries/ExportRecords/ExportRecordsHandler.cs:58`（表頭）、`:80`（xlsx 工作表名＝模組名）。
  - 列印：`PrintService/PrintRecordHandler.cs:94`（範本名稱＝頁面標題）。
  - 報表目錄：`ReportService/Queries/GetReportCatalog/GetReportCatalogHandler.cs:68,77,99,112-120`（欄位、經由欄位、子模組、`EntityTitle`、選項）、`GetReportMainModulesHandler.cs:22`、`GetReportDatasetsHandler.cs:14`。
  - 報表執行：`ReportService/ReportRunner.cs:305,373,382`、`ReportRunner.Output.cs:33,37,57,58`、`ReportRunner.Tasks.cs:69`、`ReportRunner.Sql.cs:443`（`RunContext` 帶 `LocaleContext`）。
  - 顯示：`Display/FieldDisplay.cs:62`（選項標籤；`FieldDisplayContext` 加 `LocaleFallbacks`，由 `RequestLocale` 建）。
  - 其他：`AccountService/Queries/AdminListUsers/AdminListUsersHandler.cs:29`、`RecordService/RecordValueProcessor.cs:15,126`（`RuleContext.Locale` 改成 `LocaleContext`，刪掉 `= I18nText.FallbackLocale` 預設）、`CheckService/CheckEvaluator.cs:84`、`TaskRuleService/TaskRuleApplier.cs:77,78`（任務標題照現況存建立者的語系，加退回鏈）、`AppService/AppFlowInstaller.cs:169`、`AppService/AppOpenPlanner.cs:121,122`、`AppService/Commands/OpenApp.cs:101`。
  - 訊息參數：`Common/MessageRef.cs:39`（`Resolve(string? locale, …)` 加 `IReadOnlyList<string>? fallbacks`；`ServiceResult.MessageIn`／`ErrorsIn` 與 WebAPI 轉回應處傳 `LocaleFallbacks`）；`WebAPI/Errors/FrozenFieldExceptionHandler.cs:14` 經 `MessageRef` 一併生效。
- B. 寫死 `Resolve("zh-TW")` 的訊息內插 → `Resolve(requestLocale.Current)`：`CheckService/CheckIndexer.cs:47,53,93`、`CheckService/CheckStage.cs:111`、`AppService/Integrations/IntegrationReconciler.cs:97,106,212`、`AppService/Integrations/IntegrationGuardStage.cs:44`、`LedgerService/LedgerValidator.cs:55,64`、`WorkflowService/Commands/SaveWorkflow/SaveWorkflowHandler.cs:105`、`EventService/EventHiddenGuard.cs:36`、`EventService/EventValidator.cs:230,235,238,260`、`ApprovalService/ApprovalConstraints.cs:13,26,29,54,89,108`（刪 `Locale` 常數）。
- 不改：`DemoService`、`Seed`、`AppPackageUpgrader.cs:66`、`CreateModuleHandler.cs:95`、`TaskReportFields.cs:160`（產生四語或套件格式）；`Resolve(I18nText.FallbackLocale)` 那批在 Task 11b。
- Test: `backend/tests/DynamicModules.WebAPITests/Dynamic/LocaleChainApiTests.cs`（新，`Category=Postgres`）、`backend/tests/DynamicModules.ApplicationTests/Dynamic/Policies/LocaleLabelTests.cs`（新）、`FieldDisplayTests.cs`

**Interfaces:**
- 背景流程（整合重算、索引回填）沒有 HttpContext，`ICurrentLocale` 是預設語系（Task 10）。
- 訊息本身仍是中文句子（`MessageText` 表外的字面值照中文回傳），只有內插的標籤跟語系走；這是 backend 規則「使用者看得到的訊息」的現況，不在 p3 翻譯。

- [ ] **Step 1: 寫失敗的測試**
  - `LocaleChainApiTests`（必修 4；`Category=Postgres`）。共用資料：預設 `en-US`、啟用 `["zh-TW","en-US","th"]`；模組名 `{zh-TW:"訂單", en-US:"Order"}`、欄位 `{zh-TW:"客戶", en-US:"Customer"}`、選項 `{zh-TW:"高", en-US:"High"}`、列印範本名 `{zh-TW:"出貨單", en-US:"Packing slip"}`，都**沒有** `th`。每個請求帶 `X-Locale: th`，報表與匯出另各跑一次 `?locale=th`：
    - `Export_HeaderAndSheetName_English`：CSV 表頭含 `Customer`、選項值 `High`；xlsx 的工作表名 `Order`。
    - `Print_Title_English`：HTML `<title>` 是 `Packing slip`。
    - `ReportCatalog_English`：主模組清單有 `Order`；目錄的欄位 `Customer`、選項 `High`。
    - `ReportRun_ColumnsAndRowMeaning_English`：欄位欄名 `Customer`、筆數說明含 `Order`。
    - `History_FieldLabel_English`、`AdminUsers_GroupSourceName_English`（群組名 `{zh-TW:"業務", en-US:"Sales"}`）。
    - `Factory_ZhTwDefault_Unchanged`：預設 `zh-TW` 時同一組請求回繁中（退回鏈的第二步是繁中，行為與現在相同）。
  - `LocaleLabelTests`：預設 `en-US` 時，檢查索引「已自動啟用欄位「…」的可篩選」、分錄驗證「參照欄位「…」…」、事件驗證「來源欄位「…」…」、簽核限制的欄位名，內插的是英文標籤；出廠值時與現在相同。
  - `FieldDisplayTests.Option_UsesLocaleFallbacks`：選項只有繁中與英文，要求 `th`、預設 `en-US` → 顯示英文。
- [ ] **Step 2: 跑測試確認失敗**：`cd backend && dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~LocaleChainApiTests"`；`dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~LocaleLabelTests|FullyQualifiedName~FieldDisplayTests"`
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；另跑 ApplicationTests `--filter "FullyQualifiedName~Check|FullyQualifiedName~Integration|FullyQualifiedName~Ledger|FullyQualifiedName~Workflow|FullyQualifiedName~Event|FullyQualifiedName~Approval|FullyQualifiedName~Export|FullyQualifiedName~Print|FullyQualifiedName~Report"`；WebAPITests `--filter "FullyQualifiedName~Report|FullyQualifiedName~Export|FullyQualifiedName~Print|FullyQualifiedName~Locale"`；PostgreSQL：WebAPITests `LocaleChainApiTests`；`grep -rn 'Resolve("zh-TW")' backend/src` 只剩 `DemoService` 下的檔案；`grep -rnE '\.Resolve\((locale|request\.Locale|body\.Locale|ctx\.Locale|rules\.Locale|locale\.Locale)\)' backend/src` 無結果
- [ ] **Step 5: Commit**（`feat(policy): 輸出標籤與訊息內插改用請求語系與退回鏈`）

**驗收**：Step 4 全過（含 PostgreSQL）；兩條 grep 符合預期。

### Task 11b: `Resolve(I18nText.FallbackLocale)` 改成請求語系與退回鏈（D3，使用者 2026-11-20 裁決）

**Files（逐處處置）：**
- 改成 `Resolve(requestLocale.Current)`（訊息、警告、畫面排序）：
  - 套件與樣板：`AppService/AppApprovalFlowCodec.cs:121,154,160`、`AppExporter.cs:99,262,272,348`、`AppFlowInstaller.cs:265`、`AppOpenPlanner.cs:233,258,259,276,284`、`AppPackageValidator.V8.cs:246,289`、`Commands/CloseReopenApp.cs:63`、`Commands/DeleteApp.cs:113,150`、`Data/AppResponses.cs:126`、`SharedMasters/ContributionOrphans.cs:42`、`SharedMasters/MasterDesigner.cs:72,102`、`SharedMasters/MasterMerger.cs:29`、`TemplateService/TemplateExporter.cs:170,311`、`TemplateInstantiator.cs:352`、`TemplateValidator.cs:135`。
  - 設計器與記錄：`ApprovalService/ApprovalClosureRecheck.cs:46`、`ApprovalService/LockedDataGuard.cs:85`、`DeleteService/DeletePlanner.cs:343`、`FieldService/Data/FieldRules.cs:292`、`ImportService/ImportPlanner.cs:78`、`ModuleService/Commands/UpdateModule/UpdateModuleHandler.cs:81,89`。
  - 選單排序：`SchemaService/Queries/GetMenu/GetMenuHandler.cs:22`（依請求語系的名稱排序）。
  - 群組影響：`Persistence/Repositories/Dynamic/AccountRepository.cs:88,92,103`、`Domain/Repositories/Dynamic/IAccountRepository.cs:49`（`GetGroupImpactAsync` 多收 `LocaleContext`，由 handler 傳入；註解同步）。
- 移到 Task 9（是必填檢查，不是輸出）：`TaskRuleService/TaskRuleValidator.cs:130`（任務標題必填改走 `RequiredTextGuard`）。若 Task 9 已做完，這裡只確認。
- 寫進資料庫、稽核或 log 的固定繁中文字（**新分岔 N1，裁決前不動**，行為與現在相同）：`ApprovalService/ApprovalEngine.cs:191`（改道說明，程式註解寫「comment 是固定繁中，只留給資料庫與稽核」）、`ApprovalService/ApprovalGraphEngine.cs:277,283`（分支取消說明）、`ApprovalService/ApproverResolver.cs:179`（「寫進簽核歷程的說明（資料庫）固定繁中」）、`Common/Security/FieldLocks.cs:73`（`Exception.Message` 給記錄與測試）、`AppService/Commands/ImportAppPackage.cs:46` 與 `TemplateService/Commands/ImportTemplate/ImportTemplateHandler.cs:30`（稽核目標名稱）、`Domain/Modules/TitleTemplate.cs:118` 與 `FieldService/Commands/UpdateOption/UpdateOptionHandler.cs:29`（記錄標題用的選項標籤，兩處要一致，否則改名判斷會錯）。
- 不改：`Module.cs:41,46`、`SectionTextJsonConverter.cs:16,23`（舊資料與套件格式的相容讀法）、`TemplateValidator.cs:276`（套件必有繁中，判斷 12）、`ServiceResult.cs:21,29`（給 log 與測試的繁中 `Message`／`Errors`）。
- Test: `backend/tests/DynamicModules.ApplicationTests/Dynamic/Policies/LocaleLabelTests.cs`（加案例）、`backend/tests/DynamicModules.WebAPITests/Dynamic/LocaleChainApiTests.cs`（加案例）

- [ ] **Step 1: 寫失敗的測試**
  - `LocaleLabelTests.AppExportWarning_UsesRequestLocale`：預設 `en-US`、模組名只有英文與繁中，匯出套件的警告內插英文名。
  - `LocaleLabelTests.DeletePlan_UsesRequestLocale`、`UpdateModule_TitleTemplateHiddenField_UsesRequestLocale`：同上。
  - `LocaleChainApiTests.Menu_SortedByRequestLocale`：兩個模組繁中與英文的字母順序相反，`X-Locale: en-US` 時依英文排序。
  - `LocaleChainApiTests.GroupImpact_ModuleNamesInRequestLocale`：刪群組前的影響清單用請求語系。
  - 出廠值時既有測試的訊息斷言不改。
- [ ] **Step 2: 跑測試確認失敗**：`cd backend && dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~LocaleLabelTests"`；`dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~LocaleChainApiTests"`
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；另跑 ApplicationTests `--filter "FullyQualifiedName~App|FullyQualifiedName~Template|FullyQualifiedName~Master|FullyQualifiedName~Delete|FullyQualifiedName~Import|FullyQualifiedName~ModuleService|FullyQualifiedName~Approval|FullyQualifiedName~Group"`；WebAPITests `--filter "FullyQualifiedName~App|FullyQualifiedName~Menu|FullyQualifiedName~AdminGroup"`；PostgreSQL：WebAPITests `LocaleChainApiTests`、ApplicationTests `FullyQualifiedName~AccountRepository`；`grep -rn "Resolve(I18nText.FallbackLocale)\|Resolve(Domain.ValueObjects.I18nText.FallbackLocale)" backend/src` 只剩 N1 列出的 9 行
- [ ] **Step 5: Commit**（`feat(policy): 其餘以繁中解析的訊息改用請求語系與退回鏈（D3）`）

**驗收**：Step 4 全過（含 PostgreSQL）；grep 只剩 N1 那幾處；交付說明列出 N1 待裁決。

### Task 12: 前端語系改從 API 取

D2 已裁決：警示信語言跟啟用與預設語系（`OpsChannelTab`、`AlertMailText.Locales`、`FactoryLocale`）。

**Files:**
- Create: `frontend/src/i18n/locale-settings.ts`
- Modify: `frontend/vitest.setup.ts`（`setLocaleSettings` 出廠值）
- Modify: `frontend/src/i18n/locale.ts`（`DEFAULT_LOCALE` 改名 `FALLBACK_LOCALE`，註解寫「伺服器連不上時的翻譯包退回」；`readLocale()` 回 `LocaleCode | null`）、`frontend/src/boot/i18n.ts:11-12`（沒記過時先用 `FALLBACK_LOCALE`）
- Modify: `frontend/src/router/auth-guard.ts`（第一次導覽取 `getAuthOptions()` → `setLocaleSettings({ defaultLocale, enabled })`，沒記過或記的已停用時 `applyLocale(default, …, false)`）、`frontend/src/application/dynamic/session-settings.ts`（`loadSessionSettings` 同一次 `getEffectivePolicies(null)` 一併 `setLocaleSettings({ requiredText })`）、`use-business-time-watch.ts`（每次拿到生效值一併更新 `requiredText`，不提示）
- Modify: `frontend/src/presentation/locale/LocaleSwitcher.vue`（只列啟用）、`frontend/src/presentation/dynamic/designer/I18nTextInput.vue`、`frontend/src/domain/dynamic/i18n-text.ts`、`frontend/src/domain/dynamic/event-draft.ts:125`、`task-rule-draft.ts:128,148`、`check-draft.ts:48`、`frontend/src/presentation/dynamic/designer/OptionEditor.vue`、`frontend/src/presentation/dynamic/list/ViewMenu.vue`、`frontend/src/presentation/dynamic/print/PrintTab.vue`、`frontend/src/domain/dynamic/menu-nav.ts`
- Modify（其他寫死 `'zh-TW'`）：`frontend/src/presentation/dynamic/field-type-registry.ts:246`（篩選選項標籤改用目前語系）、`frontend/src/presentation/dynamic/details/detail-rows.ts:104` 與 `frontend/src/presentation/dynamic/build-zod-schema.ts:32` 的 `locale = 'zh-TW'` 預設參數（改成必傳或 `currentLocale()`，以呼叫端為準）、`frontend/src/pages/system/dynamic-modules/ModuleEditorPage.vue`（分區文字「繁中空白就清掉 section」的判斷改成「沒有任何語系有值」）
- Modify（D2 警示信語言）：前端 `frontend/src/presentation/ops/OpsChannelTab.vue:7,25,28`（選項改 `enabledLocales()`；「出廠」那一項顯示目前的預設語系；已存的語言之後被停用時，下拉怎麼顯示依 N3）；後端 `OpsService/Alerts/AlertMailText.cs:12,14,143`（刪 `FactoryLocale` 常數；`Locale(string? mailLocale, string defaultLocale)`：`mailLocale` 有翻譯表（`Locales`）就用它，否則用 `defaultLocale`）、`OpsService/Alerts/AlertChannelHandlers.cs:32,69`（GET 的出廠值回 `cache.DefaultLocale`；PUT 驗證改成「必須是啟用語系」，訊息照舊 `OptionInvalid`）、`OpsService/Alerts/AlertContracts.cs:18`（寄信時傳 `cache.DefaultLocale`）。`IncidentTitles.cs:62` 的 `ZhTw`（站內通知標題快照的退路）不在 D2 範圍，見新分岔 N2。
- Modify: `frontend/src/i18n/*.json`（`designer.zhRequired` → `designer.localeRequired`「{locale} 必填」四語；`ops.channel.localeDisabled`「已停用」四語）
- 不改：`LedgerTab.vue` 與 `approval-designer/graph/designer-graph.ts:68` 的 `LOCALES`（產生四語文字，判斷 14）；`services/api/http.ts:29-31`、`services/ops/error-reporter.ts:116` 的 `X-Locale: currentLocale()`（目前語系已是預設或使用者選的）
- Test: `frontend/src/i18n/locale-settings.vitest.spec.ts`（新）、`frontend/src/i18n/locale.vitest.spec.ts`、`frontend/src/domain/dynamic/i18n-text.vitest.spec.ts`、`frontend/src/presentation/dynamic/designer/I18nTextInput.vitest.spec.ts`、`frontend/src/presentation/locale/LocaleSwitcher.vitest.spec.ts`（新）、`event-draft`／`task-rule-draft`／`check-draft` 的 spec、`frontend/src/presentation/dynamic/designer/OptionEditor.vitest.spec.ts`（新）、`ViewMenu.vitest.spec.ts`、`PrintTab.vitest.spec.ts`、`menu-nav.vitest.spec.ts`、`auth-guard.vitest.spec.ts`、`use-business-time-watch.vitest.spec.ts`、`frontend/src/presentation/ops/OpsChannelTab.vitest.spec.ts`（新）；後端 `backend/tests/DynamicModules.WebAPITests/Ops/AlertChannelTests.cs`（D2）；父元件測試見下方「編輯入口」

**編輯入口（審查必修 5）**：每個用 `I18nTextInput` 的地方逐一處理。「原值」一律在開啟對話框或載入資料時 `structuredClone` 一份獨立保存，不能直接傳 `v-model` 綁的同一個物件（否則使用者一改，原值跟著變，永遠判成「沒改」）。新增時不傳。

| 入口 | 編輯的文字 | 原值來源 | 父元件測試 |
|---|---|---|---|
| `presentation/dynamic/designer/FieldEditorDialog.vue:339` | 欄位標籤 | 開啟時 `props.field.label` | `FieldEditorDialog.vitest.spec.ts`（既有，加案例） |
| `presentation/dynamic/designer/OptionEditor.vue` | 既有選項標籤、新增選項 | 載入時每個 `o.label`；新增那列不傳 | `OptionEditor.vitest.spec.ts`（新） |
| `pages/system/dynamic-modules/ModuleEditorPage.vue` | 區塊標籤（`blockForm.label`） | 開啟區塊對話框時的區塊標籤；新增區塊不傳 | `ModuleEditorPage.vitest.spec.ts`（既有） |
| `presentation/dynamic/events/EventEditorDialog.vue:119` | 事件名稱 | 開啟時的事件名稱 | `EventEditorDialog.vitest.spec.ts`（既有） |
| `presentation/dynamic/checks/CheckEditorDialog.vue` | 名稱、訊息 | 開啟時的兩個值 | `CheckEditorDialog.vitest.spec.ts`（既有） |
| `presentation/dynamic/conversions/ConversionEditorDialog.vue` | 轉換名稱 | 開啟時的名稱 | `ConversionEditorDialog.vitest.spec.ts`（新） |
| `presentation/dynamic/rules/RuleEditorDialog.vue` | 驗證規則訊息 | 開啟時的訊息 | `RuleEditorDialog.vitest.spec.ts`（既有） |
| `presentation/dynamic/tasks/TaskRuleEditorDialog.vue` | 規則名稱、每個任務的標題 | 開啟時的名稱與各任務標題（以任務的索引或 id 對應；新加的任務不傳） | `TaskRuleEditorDialog.vitest.spec.ts`（新） |
| `presentation/organization/DepartmentDetail.vue:106` | 部門名稱 | 載入部門時的名稱；新增部門不傳 | `DepartmentDetail.vitest.spec.ts`（新） |
| `presentation/dynamic/approval-designer/FlowBasicFields.vue` | 簽核流程名稱 | 父層 `ApprovalDesigner` 載入流程時的名稱，經 prop 傳入 | `FlowBasicFields.vitest.spec.ts`（新，掛父層傳 prop） |
| `presentation/dynamic/approval-designer/ApprovalStageFields.vue` | 關卡名稱 | 載入流程時以關卡 id 取的名稱；新關卡不傳 | `ApprovalStageFields.vitest.spec.ts`（新） |
| `ModuleListPage.vue`、`TemplateDialog.vue` | 新模組名稱 | 只有新增，不傳 | 既有 spec 確認照必填擋 |
| `optional` 的輸入（`ModuleListPage`／`ModuleEditorPage` 的分區、`TaskRuleEditorDialog` 的任務說明、流程圖的 `ForkNodePanel`、`ConditionNodePanel`、`EdgePanel`、`ConditionEdgeFields`） | — | 不必填，不受影響 | 不另測 |

每個父元件測試同一組三案（必填 `['zh-TW','en-US']`、既有資料只有繁中）：`只改其他設定可以存`（例：欄位改「必填」勾選、事件改觸發條件、部門改主管）→ 呼叫儲存的 mutation；`改了文字才要求補語系`（改繁中文字）→ 不呼叫 mutation、顯示 `en-US 必填`；`新增照必填擋`（新增模式只給繁中）→ 不呼叫 mutation。草稿函式（`event-draft`、`task-rule-draft`、`check-draft`）由父元件把原值傳進建立 draft 的函式。

**Interfaces:**
- `locale-settings.ts`：`setLocaleSettings(s: { defaultLocale?: LocaleCode; enabled?: LocaleCode[]; requiredText?: LocaleCode[] }): void`；`defaultLocale()`、`enabledLocales()`、`requiredTextLocales()`（沒設定過丟錯）。前端取啟用語系只有這一處（判斷 25）。
- `i18n-text.ts`：`resolveI18n(text, locale)` 退回鏈同後端（要求的 → `defaultLocale()` → 繁中 → 第一個有值的；判斷 13）；`compactI18n` 去空後沒有任何值才回 undefined；`sameI18n(a, b)`（去空白、忽略空值）；`missingRequired(text, original?): LocaleCode[]`：`original` 有值且 `sameI18n(text, original)` 回 `[]`，否則回缺的必填語系（判斷 11 前端部分）。
- `I18nTextInput.vue`：新 prop `original?: I18nText`（編輯既有資料時傳載入的值，新增時不傳）。必填語系各一個輸入框（`label` `${label} (${l}) *`），其餘啟用語系放「其他語系」展開區；停用語系不顯示但 `set()` 保留原值；`optional` 時全部不必填；必填規則只在「沒有 `original`」或「目前值與 `original` 不同」時生效；`validate()` 同一套規則。`data-testid` 改成 `i18n-${l}`（`i18n-zh-TW` 保持可用）。
- 三個 draft 與 `OptionEditor`：繁中必填改 `missingRequired(text, original)`，`original` 取 draft 建立時的既有值（新增時 undefined）；錯誤鍵不變。`ViewMenu` 另存時名稱寫進目前語系與每個必填語系。`menu-nav` 分組鍵改「繁中 → 第一個有值的語系」。

- [ ] **Step 1: 寫失敗的測試**
  - `locale-settings.vitest.spec.ts`：未設定時丟錯；設定後讀回。
  - `i18n-text`：`resolveI18n({ 'en-US': 'Order' }, 'th') === 'Order'`；預設 `en-US` 時 `resolveI18n({ 'zh-TW': '訂單', 'en-US': 'Order' }, 'th') === 'Order'`；`compactI18n({ 'en-US': ' Order ' })` 回 `{ 'en-US': 'Order' }`；`missingRequired` 新增與「沒改」兩種情境。
  - `I18nTextInput`：必填 `['zh-TW','en-US']` 時兩個必填框有 `*`；啟用 `['zh-TW','en-US']` 時展開區沒有 `th`、`vi`；modelValue 帶 `th` 時改繁中後 emit 仍有 `th`；`編輯沒改的既有值不擋`（`original` 只有繁中、必填繁中＋英文 → `validate()` 為 true）；`編輯改了就擋`（改繁中文字後 `validate()` 為 false、訊息 `en-US 必填`）。
  - `LocaleSwitcher`：啟用 `['zh-TW','en-US']` 只列兩項。
  - 三個 draft：必填 `['en-US']` 時新增只有繁中名稱報 `nameRequired`、只有英文通過；編輯既有只有繁中的名稱、沒改時通過。
  - `OptionEditor`：新增選項照必填擋；既有選項沒改標籤時「儲存」不停用。
  - `auth-guard`：`沒記過語系時套用預設`（`getAuthOptions` 回 `en-US` → i18n locale `en-US`、localStorage 沒寫入）；`記過但已停用時改用預設`。
  - `menu-nav`：沒有繁中的分區以英文分組。
  - `use-business-time-watch`：生效值的 `locale.requiredText` 變成 `['zh-TW','en-US']` → `requiredTextLocales()` 跟著變，不呼叫 `Dialog.create`。
  - 編輯入口表的父元件測試（每個入口三案，見上）。
  - `OpsChannelTab`（D2）：啟用 `['zh-TW','en-US']` 時語言選項只有兩種加「出廠（English）」（預設 `en-US`）；存檔不改信件語言時不送 `mailLocale`（已存 `th` 又停用時照常寄泰文，後端測試驗）；已停用語言的下拉顯示依 N3 補一案。
  - `AlertChannelTests`（D2，後端）：預設 `en-US`、`MailLocale` 是 null 時測試信是英文、GET 的出廠值是 `en-US`；已存 `th` 後停用 `th`，告警信仍是泰文；PUT `mailLocale: "th"`（已停用）→ 400。
- [ ] **Step 2: 跑測試確認失敗**：`cd frontend && bunx vitest run src/i18n src/domain/dynamic src/presentation/dynamic src/presentation/locale src/presentation/organization src/presentation/ops src/pages/system/dynamic-modules src/router src/application/dynamic --maxWorkers=2`；`cd backend && dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~AlertChannelTests"`
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；另跑 `bunx vitest run src/presentation src/pages --maxWorkers=2`；`bun run typecheck`；`grep -rn "'zh-TW'" src --include='*.ts' --include='*.vue' | grep -v "spec.ts"` 只剩下列分類（審查建議），每類逐檔核對：
  - 語系清單與型別：`i18n/locale.ts`（`LOCALES`）、`domain/dynamic/types.ts:4`（`Locale` 型別）。
  - 翻譯包退回：`boot/i18n.ts:11-13`（`fallbackLocale` 與四語 messages；`locale` 初值改成 `FALLBACK_LOCALE` 後這行若仍是字面值也算）。
  - 退回鏈的繁中那一步：`domain/dynamic/i18n-text.ts`、`domain/dynamic/menu-nav.ts`。
  - 產生四語文字：`presentation/dynamic/approval-designer/graph/designer-graph.ts:68`、`presentation/dynamic/ledger/LedgerTab.vue`。
  - 測試用 helper（非 spec 檔）：`pages/dashboards/dashboard-test-kit.ts:113`、`presentation/tasks/board/board-fixtures.ts:63-64`。
  - 畫面內部的暫時物件：`presentation/dynamic/designer/FieldEditorDialog.vue:140`（預設值輸入框用的暫時欄位標籤；改成 `{ [currentLocale()]: … }` 更好，改了就不在白名單）。
  - 其他任何一處（特別是 draft、`I18nTextInput`、`OptionEditor`、`ViewMenu`、`PrintTab`、`detail-rows`、`build-zod-schema`、`field-type-registry`）出現都算沒改完。
- [ ] **Step 5: Commit**（`feat(policy): 前端語系選單、多語輸入與必填語系改從 API 取（A18）`）

**驗收**：Step 4 全過（含 `AlertChannelTests`）；編輯入口表每一列都有父元件測試或標明不另測的理由；grep 只剩白名單分類。

### Task 13: p3b 文件、實機與 gate；p3 收尾

**Files:**
- Modify: 設計文件：§10 補「繁中是多語文字的退回語系與套件格式的必填語系」「`SupportedLocales` 四種是平台已有翻譯的語系（i4a 換成已安裝語系）」「前端 `FALLBACK_LOCALE`」；§12 加技術判斷 11（前端部分）、13、15、16、21–25（判斷 9、10、12、14、27 是使用者裁決，§3.7 已寫）；`docs/roadmap.md` p3 完成；`docs/feature.md` p3 打勾（跑腳本）；`docs/manual.md`（語言切換只列啟用語系；設計器多語輸入：必填語系依政策、編輯時沒改的不擋；政策設定的「地區與語系」三項與改用順序；沒指定語系時以預設語系回應）

- [ ] **Step 1: 實機**：四種寬度。依序把預設改英文 → 必填改英文 → 啟用只留繁中與英文：語言選單只剩兩種、登出後（清 localStorage）登入頁是英文、模組設計的欄位標籤輸入只要求英文、既有只有繁中的欄位改其他設定可存、改了它的標籤才要求英文；匯出（不帶語系參數）用英文標籤。再全部恢復出廠值（先啟用 → 再必填 → 最後預設）。每一步四語畫面不露出鍵名。
- [ ] **Step 2: gate**（同 Task 7 Step 3，含 PostgreSQL 兩步）
- [ ] **Step 3: Commit**（`docs: 語系政策的手冊、可寫死清單與 p3 完成`）

**驗收**：實機逐項記錄；gate `exit=0`；`formwright-purpose-check` 無違反。

---

## 自我檢查結果

1. **設計覆蓋**：A5 → Task 1、2；A17＋A31（`BusinessClock`、`TaipeiBusinessTime`、`Program.cs:101`、`FieldImportParser`、`ReportRules`、`DateTimeHandler`、`FieldDisplay`、`TaskRuleEvaluator`、`ReportRunner` 各處，前端 `field-type-registry`、`rule-engine`、`DateSelect`、`FilterValueInput`、`rule-state`）→ Task 1、3、4、5；稽核與維運時間（使用者 2026-11-20）→ Task 6；A18 → Task 8、9、12；沒指定語系用預設（使用者 2026-11-20）→ Task 10、11；§3.5 → Task 2、4、5、9、12；§10 → Task 7、13。
2. **現況檢查 S1–S12 與 i4**：逐條對應見修訂紀錄 2026-11-20。
3. **型別一致**：`PolicyKind.UtcOffset`／`'utcOffset'`、`UtcOffsetOf`／`FormatUtcOffset`／`FactoryBusinessOffset`、`CachedSystemPolicy`（`Get`、`BusinessOffset`、`EnsureFreshAsync`、`Apply`、`Display`、`DefaultLocale`、`EnabledLocales`、`RequiredTextLocales`、`LocaleFallbacks`）、`IPolicyChangeHook.BeforeCommitAsync`、`ShiftDateTimeIndexAsync`、`FieldDisplayContext`、`IInstalledLocales`、`PolicyOptionSource`、`RequiredTextGuard.Check`、`I18nText.Resolve(locale, fallbacks)`；前端 `setBusinessOffset`、`loadSessionSettings`、`setLocaleSettings`、`missingRequired`、`useBusinessTimeWatch`、`businessDayStartUtc` 在使用處名稱一致。`IBusinessTime` 不加 `Offset`；沒有 `SystemPolicySnapshot`。
4. **Review Focus**：七條都有指定測試與所屬 Task。
5. **待決**：D1–D5 已於 2026-11-20 由使用者裁決（見上）；第二輪新增 N1–N3，各標了要在哪個 Task 前問，不阻擋其他 Task。

## 使用者裁決

- 2026-11-12 追加：U1 改時區後其他人畫面的提示不能關，只能按「重新整理」；U2 多台部署改時區只寫進文件與政策說明，不做跨伺服器同步。
- 2026-11-20：請求沒指定語系時用預設語系政策（判斷 27，Task 10、11a、11b）；稽核與維運畫面的時間照營運時區並標示時區（判斷 26，Task 6）。兩條都已寫回設計 §3.7。
