# 政策設定 p3 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 部門層數（A5）、營運時區（A17，含補盤 A31 各處）、預設／啟用／必填語系（A18）改成可設定，前端時區與語系改從 API 取；出廠值等於現值，部署後沒人改設定時行為不變。

**Architecture:** 時區與語系要在同步程式裡用（`IBusinessTime.Now`、`ICurrentLocale.Locale`、欄位型別處理器、FluentValidation），所以加一個 singleton 的 `SystemPolicySnapshot`（與 O1 的 60 秒程序內快取是同一份）：保存全系統範圍政策的生效值，啟動時、每次系統 PUT 的交易提交後、以及每個請求發現超過 60 秒時從 `IPolicyReader` 重新載入。讀值入口仍是 `IPolicyReader`，快照只是它的同步快取；營運時區偏移只從快照取（`IBusinessTime` 不帶偏移）。改時區時在同一個交易裡把日期時間欄位的索引平移，讓列表篩選與報表日期切點跟著新時區。前端時區與必填語系在登入後從生效值 API 取；預設與啟用語系在登入前從公開的 `GET auth/options` 取。

**Tech Stack:** .NET 8、EF Core（SQLite 研發、PostgreSQL 正式）、MediatR、FluentValidation、xUnit＋FluentAssertions；Vue 3、Quasar 2、TanStack Query、vue-i18n、vitest、bun。不新增相依套件。

**Spec:** `docs/superpowers/specs/2026-11-11-policy-settings-design.md`（origin/main `df680b7a` 版，已含 2026-11-12 裁決：§3.3 時區、語系清單型別；§3.5 時區與必填語系改變只管之後、改時區確認框文字；§3.7 p3 細則；§4 A5、A17、A18 與補盤 A31；§9 p3）。PostgreSQL 驗證依 `docs/superpowers/specs/2026-11-12-postgres-provider-design.md` §5.5、§6.3–§6.6。O1 的 60 秒程序內快取見 `docs/superpowers/specs/2026-11-11-ops-observability-design.md` §10.6（主控判斷 B2）。repo 規範：`AGENTS.md`、`.claude/rules/{backend,frontend,docs}.md`。p1 計畫：`../20261111-policy-settings-p1/plan.md`；p2 計畫：`../20261111-policy-settings-p2/plan.md`。

**路徑慣例：** 後端相對 `poc/backend/`，前端相對 `poc/frontend/`，文件相對 repo 根目錄。

---

## 修訂紀錄（2026-11-12）

依使用者 2026-11-12 裁決與 `../20261111-policy-settings-p2/plan-review.md` 的 p3 部分修訂；逐條處置與理由見 `../20261111-policy-settings-p2/revise-result.md`。

1. 使用者裁決寫進判斷與 Task（不再列待決）：
   - 改營運時區時系統自記的時間不換算、確認框寫明（設計 §3.5 的原文）→ 判斷 5、7，Task 5 `policies.timeZoneWarning`。
   - Q7 固定 UTC 偏移、不處理日光節約 → 判斷 1、Task 1。
   - Q8 前端「今天」改用營運時區 → 判斷 6、Task 5 `todayBusiness`。
   - Q9 改時區後已開著的瀏覽器約每 60 秒檢查、提示並重新整理 → 新判斷 18、Task 5 `useBusinessTimeWatch`。
   - Q10 必填語系管所有必填多語文字，政策名「必填的多語文字要有哪些語系」→ 判斷 9、Task 7 文案、Task 8。
   - Q11 三個語系政策互相約束 → 判斷 10、Task 7 `LocaleRules_*`。
   - Q12 停用語系後保留已存文字 → 判斷 14、Task 10 `I18nTextInput` 測試。
   - Q13 套件仍要求繁中 → 判斷 12、Task 8 `OpenPackage_IgnoresRequiredTextPolicy`。
   - 排程：p3 在 O2 之後、mcp1a 之前 → 前置條件 2–6。
2. p3-M1：`IBusinessTime` 不加 `Offset`（預設介面成員會被 `ActingBusinessTime` 吃掉）；偏移只有一個來源 `SystemPolicySnapshot.BusinessOffset`，呼叫端注入快照；補「真實 DI、含 Acting 裝飾器」的測試 → 新判斷 19、Task 3、4。
3. p3-S1：`PolicyJson.HasOptions`、`FromJson`、`ToJson` 補 `UtcOffset` 分支 → Task 1。
4. p3-S2：改時區的索引平移與快照更新的競態：快照改成交易提交後才換（新的 `SystemPolicySnapshotBehavior` 包在交易外層）、PUT 全程持有重載鎖、並行的過期重載跳過；多台部署的限制寫進 §10、`docs/deploy.md` 與政策說明 → 判斷 3、4、20，Task 3、4、6。
5. p3-S3：已開著的瀏覽器定期比對時區（使用者裁決 Q9）→ Task 5。
6. p3-S4：`ExecuteUpdateAsync` 的 `AddMinutes` 改用 pg0 的 PostgreSQL 測試環境實測（`TimeZoneChangeTests` 標 `Category=Postgres`），另斷言 `Kind` → Task 4；p3a 合併前跑一次 PostgreSQL 全套（pg0 §6.6，動到 Persistence）→ Task 6。
7. p3-S5：語系類 RoleSet 的錯誤訊息改寫「語系」→ 新判斷 21、Task 7 測試。
8. p3-N1–N4：Task 4、5 的殘留 grep 排除誤報；`Program.cs:58`、`IsValid(out` 呼叫端 13 處（`ViewRules` 經 `FieldRules.ValidLabel` 間接呼叫）；快照執行緒安全寫進 Interfaces；auth guard 補「公開頁面不需要營運時區」測試。
9. 核對 main（`67828aa4`）與排程後的過時處：前置條件刪「分支含 1c1ea8ae」；a2d–a6 依排程已合併；O1 會先做 60 秒程序內快取，p3 的快照要接上它、不另做第二份；O2 會在系統 PUT 加稽核寫入；`auth/options` 目前沒有測試，新案例放 `AuthApiTests`；語系標頭測試放既有的 `LocaleApiTests`。

## 前置條件（開工前逐項確認，不符就停下回報）

1. **p1 全部已合併**（main 已成立：`IPolicyReader`、`PolicyJson`、§7 API、`PoliciesPage`、`PolicyValueEditor`、`useEffectivePolicies`；p1c 的 `calendar.weekStart` 與 `locale` 分類）。
2. **排程（使用者 2026-11-12 確認）：p3 在 O2 之後、mcp1a 之前；p2 先於 p3**。開工時 O1、un1、pg0（pg0a–pg0d）、pg1、簽核 a2d–a6、O2、p2 都應已合併；沒合併就停下回報主控。p2 改了 `ReportRules.Check`、`PolicyDefinition`（`Target`）、`policy-catalog.json` 與四語 JSON；p3 照 p2 之後的簽章寫。
3. **a2d 已合併**（依排程應已成立；只影響 p3b）：p3b 要改 `ApprovalDefinitionValidator.cs`、`ApprovalFlowHandlers.cs` 的名稱檢查。
4. **PostgreSQL 測試環境可用**：同 p2 前置條件 6；`gate.sh` 已含 PostgreSQL 兩步。
5. **O1 的程序內政策快取**：`grep -rn "slowRequestMs\|clientErrorCollection" poc/backend/src --include='*.cs'` 找到 O1 讀這兩項的地方，看它是否已有全系統政策的 60 秒快取類別（O1 設計 §10.6 B2）。有就在 Task 3 把它擴充成 `SystemPolicySnapshot`（同一份、同一個 60 秒），讓 O1 的呼叫端改讀快照；沒有就照 Task 3 新建，O1 那兩項改讀快照。不能留兩份各自過期的快取。
6. **rebase 到最新 main 後重新核對**：行號以 main `67828aa4` 為準；O2 會在 `UpdateSystemPolicyHandler` 加稽核寫入（同交易），Task 3、4 的快照與 hook 插在稽核之後、`CommitAsync` 之前。行號不符時以符號為準；介面與本計畫不同時停下回報。

## 子段切法

| 子段 | 內容 | Task | migration |
|---|---|---|---|
| p3a | 部門層數 A5；營運時區 A17（含 A31 補盤各處，前後端） | 1–6 | 無 |
| p3b | 語系 A18（前後端） | 7–11 | 無 |

時區前後端必須一起上線（只改後端，前端仍以 +08:00 送出與顯示，改時區後會錯一段時差），所以 A17 的前後端在同一個子段；政策在 Task 1 登記，p3a 整段一起合併。

---

## 計畫期間的判斷

〔行為〕＝影響使用者看到或能做的事；〔技術〕＝實作選擇。每條：判斷／理由／選錯代價。標「使用者 2026-11-12 確認」的已拍板（設計 §3.5、§3.7），實作時不要再問。

1. 〔行為〕**營運時區存固定 UTC 偏移（如 `+08:00`），不存 IANA 時區名稱，不支援日光節約。** 選項是世界實際使用的 38 個偏移（`-12:00` 到 `+14:00`，含 `+05:30`、`+05:45`、`+08:45`、`+12:45` 等）。理由：設計出廠值寫 `+08:00`；目標客戶（台、泰、越）都沒有日光節約；資料本來就以「帶偏移的字串」存，固定偏移不必逐筆查時區規則。**使用者 2026-11-12 確認**（Q7）。代價：有日光節約的地區（歐美）每年要手動改兩次，而且改的那一刻會平移索引。
2. 〔技術〕**新增 `PolicyKind.UtcOffset`**（JSON 是字串，畫面顯示 `UTC+08:00`），不沿用 Choice。理由：Choice 的選項要逐一翻成四語，38 個偏移不必翻譯；設計 §3.3 本來就列「時區」為一種型別。代價：多一種型別要在前後端各處 switch 補一個分支。
3. 〔技術〕**加 singleton `SystemPolicySnapshot`**（O1 已有 60 秒程序內快取時，擴充它成為這個快照，不另做一份；前置條件 5），啟動載入、系統 PUT 的交易提交後立即換新值、每個請求發現超過 60 秒就重載（`SystemPolicySnapshot.MaxAge` 寫死，列 §10）。理由：`IBusinessTime.Now`、`ICurrentLocale.Locale`、`FieldTypeRegistry`（singleton）、FluentValidation 都是同步呼叫，不能 await `IPolicyReader`；靜態可變欄位會讓平行測試互相干擾，DI singleton 每個測試容器各一份。執行緒安全：整份不可變字典以 `Volatile.Write` 替換；重載用 `SemaphoreSlim(1, 1)` 只讓一個請求跑，其他請求沿用舊值不等待（審查 p3-N3）。代價：多台伺服器部署時，其他台最多晚 60 秒看到新時區或語系。
4. 〔行為〕**改時區時，同一個交易內把「日期時間」欄位的索引平移（新偏移減舊偏移）**；日期欄位、公式與彙總的日期結果不動。理由：索引存的是營運時區的牆上時間，列表的「區間」篩選、報表的日／週／月分桶都靠它；不平移的話新舊資料會差一段時差。用 EF `ExecuteUpdateAsync` 加 `DateTime.AddMinutes`，不寫 raw SQL；兩種資料庫都實測（Task 4 `Category=Postgres`）。`UpdateSystemPolicyRequest` 是 `ITransactionalRequest`，平移、設定寫入與 O2 稽核同一交易（PostgreSQL 上交易開始就取全庫寫入鎖，pg0 §5.6）。已知限制（審查 p3-S2）：其他台伺服器在最多 60 秒內仍用舊偏移寫入的記錄，日期時間索引會**永久**差一段時差；同一台在改時區那一刻已經算好值、還在等寫入鎖的請求也一樣。處理：政策說明與 `docs/deploy.md` 寫明「多台部署請在離峰時段、先縮成一台再改時區」，§10 列為已知限制；不做自動重建。代價：記錄很多時，改時區的那次存檔要跑一段時間並持有寫入鎖；多台部署要照文件操作。
5. 〔行為〕**系統記錄的時間欄位（建立、修改時間、通知時間、歷程時間、簽核時間等）不換算。** 它們以營運時區的牆上時間存、不帶偏移，改時區後舊資料照原數字顯示（例：+08:00 改 +07:00 後，舊的「10:00」仍顯示 10:00，實際是新時區的 09:00）。理由：要換算得逐表 migration，超出 p3。**使用者 2026-11-12 確認**：不換算，確認框寫明（設計 §3.5）。代價：系統自記的時間在改時區前後不連續。
6. 〔行為〕**前端的「今天」改用營運時區**（目前 `rule-state.ts` 的 `todayLocal` 用瀏覽器時區）。理由：後端規則與篩選用營運時區的今天；前端條件規則若用另一個「今天」，表單上的提示會和存檔結果不一致。**使用者 2026-11-12 確認**（Q8）。代價：電腦時區不是 +08:00 的使用者，在跨日前後看到的規則提示會改變（變成與後端一致）。
7. 〔行為〕**改時區要在政策設定頁先確認**：確認框文字用設計 §3.5 的原文「改時區會讓今天、逾期、報表日期切點跟著變；記錄的日期時間欄位保持原本的絕對時間，系統自記的時間不換算」，按確定才送出。
8. 〔技術〕**部門層數只在「新增部門」或「改上級部門」時檢查**（現在每次更新都檢查）。理由：§3.5；調低層數後改既有深層部門的名稱不能被擋。代價：無；出廠值下更新同一上級時結果本來就相同。
9. 〔行為〕**必填語系是獨立政策 `locale.requiredText`，出廠 `zh-TW`**，套用到所有必填的多語文字（名稱、標籤、選項、檢視名稱、驗證規則與檢查的訊息、部門、事件、轉換、簽核關卡），不只「名稱」。理由：這些今天都是同一條「必須有繁體中文」規則；只改名稱會留下一半寫死。**使用者 2026-11-12 確認**（Q10）；政策名稱「必填的多語文字要有哪些語系」（設計 A18）。
10. 〔行為〕**三個語系政策互相約束**：至少啟用一種；預設語系必須啟用；必填語系必須都啟用、至少一種。PUT 任何一個時，用「這次的新值＋另外兩個目前的生效值」檢查，不合就 400 並說明先改哪一個。**使用者 2026-11-12 確認**（Q11；設計 §3.7 寫「預設在啟用內、必填是啟用的子集」，「至少一種」是同一條的邊界）。代價：要從繁中改成只用英文，得依序改預設語系 → 必填語系 → 啟用語系三次。
11. 〔行為〕**必填語系增加時，只在「新增，或這次改了那段文字」時檢查**（§3.5）。判斷「改了」＝去掉空白與空值後，各語系內容和資料庫裡的不同。理由：設計原文。代價：檢查從 FluentValidation 移到 handler（要比對資料庫的值），錯誤回應的鍵與文字照舊，但同一個請求若同時有別的錯，回報順序可能不同。
12. 〔行為〕**應用程式套件仍要求繁中**（`TemplateValidator.HasZhTw`、`AppPackageValidator.CheckText`），不跟必填語系。理由：繁中是多語文字的退回語系，也是套件格式的一部分，套件要能在任何設定的系統開啟。**使用者 2026-11-12 確認**（Q13）。代價：管理員把必填改成英文後，開啟的套件裡仍可能有只寫繁中的名稱；列 §10。
13. 〔技術〕**多語文字的退回順序改成：要求的語系 → 繁中 → `SupportedLocales` 順序中第一個有值的**（前後端同規則）。理由：必填不再保證有繁中，退回不能只靠繁中。代價：只在新資料缺繁中時才會走到第三步，舊資料行為不變。
14. 〔行為〕**停用的語系**：語言切換選單不列；多語文字輸入框不列（已存的值保留、不刪）；列印範本分頁不列；請求帶停用語系的 `X-Locale` 時，後端當成預設語系。分錄沖銷欄位標籤仍產生四語（不是輸入）。**使用者 2026-11-12 確認**（Q12）。代價：停用後再啟用，舊值會再出現。
15. 〔技術〕**登入前的語系**：`GET auth/options`（公開）多回 `defaultLocale`、`enabledLocales`；第一次導覽時在 auth guard 取得，瀏覽器沒記過語系、或記的語系已停用時套用預設（不寫入 localStorage）。伺服器連不上時維持繁中（vue-i18n 的 `fallbackLocale`，屬翻譯包退回，列 §10）。代價：登入頁多一次請求（可與登入頁共用查詢快取）。
16. 〔技術〕**前端時區與必填語系放在兩個小模組**（`business-time.ts`、`locale-settings.ts`），由 auth guard 在登入後用 `GET policies/effective` 設定；還沒設定就呼叫會丟錯（不留預設值）。vitest 加 `setupFiles` 設成出廠值。理由：鐵則 2 不准前端另存一份；這些函式是同步的、被許多純函式呼叫，不適合每處都拿 query。代價：測試 setup 裡有一份出廠值字面量（只在測試）。
17. 〔技術〕**新分類 `organization`（組織）**；時區與語系放在 p1c 已有的 `locale`（地區與語系）。
18. 〔行為〕**改時區後已開著的瀏覽器**：登入後的主框架（`MainLayout.vue`）每 60 秒重取一次全系統生效值（TanStack Query `refetchInterval: 60_000`，背景分頁不跑、回到前景時立即重取），發現 `locale.timeZone` 和目前的 `businessOffset()` 不同時：先 `setBusinessOffset(新值)`（之後送出的值用新時區），再跳出不能關掉的提示框「營運時區已改成 UTC{新值}，頁面要重新整理才能正確顯示日期時間」，只有「重新整理」一個按鈕（`window.location.reload()`）。改設定的管理員自己的頁面在存檔成功時已換新值，不會跳。**使用者 2026-11-12 確認**定期檢查並提示重整（Q9）；提示框不能關掉是本計畫的推薦，列 revise-result 待決 U1。代價：開著沒存的表單內容在重新整理時會遺失。
19. 〔技術〕**營運時區偏移只有一個來源：`SystemPolicySnapshot.BusinessOffset`**（審查 p3-M1）。`IBusinessTime` 不加 `Offset`：預設介面成員會被 `ActingBusinessTime`（`Program.cs:60` 的 `AddActingOverrides()`、`TestApp.cs:141`）包一層後吃掉，DI 解析到的永遠是出廠 +08:00。需要偏移的呼叫端（`FieldDisplay` 的 5 個呼叫端、匯入、報表、任務規則）注入 `SystemPolicySnapshot`；`DateTimeHandler` 的 Func 也讀它。`ActingBusinessTime` 與二十多個測試替身都不用改。代價：呼叫端多注入一個 singleton。
20. 〔技術〕**快照在系統 PUT 的交易提交之後才換，且 PUT 全程持有重載鎖**（審查 p3-S2）：新增 `SystemPolicySnapshotBehavior`，只處理 `UpdateSystemPolicyRequest`，註冊在 `TransactionBehavior` **外層**；它先 `await snapshot.ReloadGate.WaitAsync(ct)`，`await next()`（交易、handler、提交都在裡面），結果成功才 `await loader.RefreshAsync(ct)`（讀已提交的值），`finally` 釋放鎖。理由：在交易內就換，回滾時快照會領先資料庫；不持鎖的話，並行請求的 60 秒過期重載可能在提交前讀到舊值、在提交後蓋回去。代價：PUT 期間其他請求的過期重載改成跳過（沿用舊值、不阻塞）；提交到換快照之間仍有毫秒級窗口，算進判斷 4 的已知限制。
21. 〔技術〕**語系類 RoleSet 的錯誤訊息寫「語系」**（審查 p3-S5）：`PolicyMessages.Invalid` 的 RoleSet 分支依 `d.Category == "locale"` 回 `只能從以下語系挑選：…`，其餘照舊 `只能從以下角色挑選：…`。不為此擴充 `PolicyDefinition` 參數。代價：之後若有非語系的 RoleSet 放進 `locale` 分類要再分。

---

## Global Constraints

- 出廠值＝現值，部署後沒人改設定時行為不變；既有測試的預期值不改（只改呼叫簽章）。
- 改嚴只管之後的新增（§3.5）：部門層數只在新增或改上級時檢查；必填語系只在新增或改了那段文字時檢查；時區改了之後，已存的帶偏移日期時間保持原本的絕對時間，顯示、今天、報表日期切點照新時區。
- 值的讀取一律經 `IPolicyReader`；同步程式讀 `SystemPolicySnapshot`（由 reader 載入）。Domain 不碰 IO，時區偏移由呼叫端傳入或以函式注入。技術上下限與選項清單只寫在 `PolicyCatalog`。
- **PostgreSQL 相容**：不寫 raw SQL；索引平移用 EF `ExecuteUpdateAsync`；`DateTime` 的 `Kind` 明確（`IBusinessTime.Now` 維持 `Unspecified`，用 `DateTime.SpecifyKind` 明寫；`RecordIndex.DateValue` 名稱不以 `Utc` 結尾，在 PostgreSQL 是 `timestamp`，pg0 的 Kind 守衛會擋 `Kind=Utc` 的寫入）。
- **PostgreSQL 驗證**（pg0 §6.3–§6.6；使用者裁決「之後的功能兩種資料庫都驗」）：
  - 新測試標 `[Trait("Category", "Postgres")]` 進雙跑集：Task 3 的 `BusinessTimeZoneApiTests`、`SystemPolicySnapshotTests` 中經資料庫重載的案例；Task 4 的 `TimeZoneChangeTests` 全部；Task 7 的 `LocaleRules_*` 與 `AuthApiTests` 新案例；Task 8 的 `RequiredTextPolicyTests` 中「改了才檢查」兩條（比對資料庫裡的值）。報表 SQL 路徑的案例 pg1 已合併就照標，沒有就標 `PgDeferred` 並寫原因。
  - 每個標了 `Postgres` 的 Task，Step 4 另跑 PostgreSQL 窄範圍：`FORMWRIGHT_TEST_DB=postgres dotnet test <專案> --filter "<該 Task 的 filter>&Category!=SqliteOnly&Category!=PgDeferred&Category!=Benchmark"`；Docker 起不來就停下回報，不跳過。
  - p3a 動到 Persistence（`RecordRepository.ShiftDateTimeIndexAsync`），合併前另跑一次 PostgreSQL 全套（pg0 §6.6，不進 gate），結果寫進 p3 結果檔。
  - 子段收尾的 gate 已含 PostgreSQL 兩步，`exit=0` 才算過。
- **migration**：p3 兩個子段都不加。
- 後端訊息文字在出廠值時與現在逐字相同（例：`部門最多 10 層`、`名稱必須有繁體中文`、`關卡名稱必須有繁體中文`）。
- 前端新字串四語鍵集合一致、單行 JSON；含 `{`、`}`、`@`、`|` 用字面量寫法。
- 有畫面的改動在 390、820、1366、1920 四種寬度不橫捲；輸入框有可讀名稱。
- 測試只跑窄範圍；後端、前端、gate 不並行；vitest `--maxWorkers=2`。
- git：逐檔 `git add`；commit 前 `git status`；不用 `--no-verify`、不 force push、不 merge commit。公開 repo 不寫公司字串、個人絕對路徑、內網 IP。
- 每個子段收尾跑 `bash scripts/unattended/gate.sh <worktree 絕對路徑>`，並用 `formwright-purpose-check` skill 對照鐵則。

**政策登記**

| 代碼 | 段 | 分類 | 型別 | 範圍 | 出廠 | 技術範圍／選項 | 取代的寫死處 |
|---|---|---|---|---|---|---|---|
| `organization.maxDepth` | p3a | organization | Integer | System | `10` | 1–30 | `DepartmentRules.MaxDepth`、`OrganizationMessages.TooDeep` |
| `locale.timeZone` | p3a | locale | UtcOffset | System | `+08:00` | 38 個偏移（判斷 1） | `TaipeiBusinessTime`、`Program.cs:58`、`FieldDisplay.BusinessOffset`、`DateTimeHandler.Taipei`、`FieldImportParser.cs:96,99`、`ReportRules.cs:239`、`TaskRuleEvaluator.cs:52`、`ReportRunner.Cells.cs:148`；前端 `field-type-registry.ts:128-131,161,282`、`rule-engine.ts:25,85`、`DateSelect.vue:4,11`、`FilterValueInput.vue:57-64`、`rule-state.ts` 的 `todayLocal` |
| `locale.default` | p3b | locale | Choice | System | `zh-TW` | `I18nText.SupportedLocales` | `HeaderCurrentLocale`／`FallbackCurrentLocale` 的退回、前端 `DEFAULT_LOCALE` |
| `locale.enabled` | p3b | locale | RoleSet | System | `zh-TW,en-US,th,vi` | 同上 | 前端 `LOCALES` 的全部列出、`I18nTextInput` 的 `others`、`PrintTab` 的 `LOCALES` |
| `locale.requiredText` | p3b | locale | RoleSet | System | `zh-TW` | 同上 | `I18nText.IsValid` 的繁中必填與其 13 處呼叫（main；`ViewRules` 經 `FieldRules.ValidLabel` 間接用）、前端各 draft 的 `['zh-TW']` 必填 |

38 個偏移：`-12:00,-11:00,-10:00,-09:30,-09:00,-08:00,-07:00,-06:00,-05:00,-04:00,-03:30,-03:00,-02:00,-01:00,+00:00,+01:00,+02:00,+03:00,+03:30,+04:00,+04:30,+05:00,+05:30,+05:45,+06:00,+06:30,+07:00,+08:00,+08:45,+09:00,+09:30,+10:00,+10:30,+11:00,+12:00,+12:45,+13:00,+14:00`。

## Review Focus

1. **改時區後的既有資料**：`+08:00` 存的 `2026-11-11T10:00:00+08:00`，改成 `+07:00` 後列表顯示 `2026-11-11 09:00`；「今天」篩選、報表日分桶、日期時間區間篩選都以新時區算；日期欄位（無時間）不平移；兩種資料庫結果相同。→ Task 4 `TimeZoneChange_ShiftsDateTimeIndexOnly`、`TimeZoneChange_ReportDayBucketFollowsNewZone`（`Category=Postgres`）；Task 5 `displays stored offset in business zone`。
2. **前端輸入與送出**：在 `+07:00` 輸入 09:00，送出 `…T09:00:00+07:00`；編輯一筆 `+08:00` 的舊值，輸入框顯示換算後的 09:00，沒動就不改值；已開著的頁面在 60 秒內發現時區變了，換新偏移並要求重新整理。→ Task 5 `DateSelect emits business offset`、`FilterValueInput converts other offsets`、`useBusinessTimeWatch`。
3. **時區快照不同步**：PUT 提交後下一個請求立即用新時區；PUT 被回滾時快照不變；另一個容器（模擬另一台伺服器）60 秒後才用新時區；背景流程（啟動回填、Demo 產生）用啟動時載入的值；在真實 DI（含 `ActingBusinessTime` 裝飾器）下，匯出與顯示跟著新時區。→ Task 3 `Put_RefreshesSnapshotAfterCommit`、`Put_RolledBack_SnapshotUnchanged`、`Middleware_ReloadsWhenStale`；Task 4 `Export_FollowsZoneUnderActingDecorator`。
4. **語系三政策互相約束與改用順序**：直接把啟用語系改成只有英文 → 400「停用的語系不能是預設語系」；依序改預設 → 必填 → 啟用三步都成功；恢復出廠值也要過約束。→ Task 7 `LocaleRules_*`。
5. **必填語系增加後編輯既有資料**：必填改成繁中＋英文後，只有繁中名稱的欄位改「必填」勾選可存、改名稱但沒補英文 400、開啟只有繁中的應用程式套件成功。→ Task 8 `UpdateField_UnchangedLabel_SkipsNewRequiredLocale`、`UpdateField_ChangedLabel_RequiresNewLocale`、`OpenPackage_IgnoresRequiredTextPolicy`。

---

# p3a 部門層數、營運時區

### Task 1: `UtcOffset` 型別與 p3a 政策登記

**Files:**
- Modify: `src/DynamicModules.Domain/Policies/PolicyDefinition.cs`（`PolicyKind` 加 `UtcOffset`；`Normalize`：Options 成員比對，同 Choice）
- Modify: `src/DynamicModules.Domain/Policies/PolicyCatalog.cs`（`OrganizationMaxDepth`、`LocaleTimeZone`；`UtcOffsets` 清單；兩個換算函式）
- Modify: `src/DynamicModules.Application/Dynamic/PolicyService/PolicyModels.cs`（`PolicyJson` 三處都要補 `UtcOffset`：`ToJson`／`FromJson` 與 Choice 同樣是 JSON 字串；`HasOptions` 改成 `Kind is Choice or RoleSet or UtcOffset`，否則 GET 回應的 `options` 是 null、前端編輯器沒有選項，審查 p3-S1）、`PolicyMessages.cs`（`Invalid` 加 `UtcOffset` 分支：`"必須是 UTC 偏移，例如 +08:00"`）、`PolicyReader.cs`（`ChoiceAsync` 也接受 `UtcOffset`）
- Modify: `poc/shared/policy-catalog.json`（codes 加 2 個；categories 加 `organization`；`choices` 不含偏移）
- Modify: `poc/frontend/src/domain/dynamic/policy-types.ts`（`PolicyKind` 加 `'utcOffset'`；`policyValueText` 回 `UTC${v}`）、`poc/frontend/src/presentation/policies/PolicyValueEditor.vue`（`q-select`，選項 `meta.options`，顯示 `UTC${o}`）
- Modify: `poc/frontend/src/i18n/*.json`
- Test: `tests/DynamicModules.DomainTests/Policies/PolicyDefinitionTests.cs`、`PolicyCatalogFixtureTests.cs`、`tests/DynamicModules.WebAPITests/Dynamic/PolicyApiTests.cs`、`poc/frontend/src/domain/dynamic/policy-types.vitest.spec.ts`、`poc/frontend/src/presentation/policies/PolicyValueEditor.vitest.spec.ts`

**Interfaces:**
- Produces（Domain）:
  - `PolicyCatalog.UtcOffsets : IReadOnlyList<string>`（判斷 1 的 38 個）；`LocaleTimeZone = new("locale.timeZone", "locale", PolicyKind.UtcOffset, PolicyScope.System, "+08:00", Options: UtcOffsets)`；`OrganizationMaxDepth = new("organization.maxDepth", "organization", Integer, System, "10", 1, 30, Unit: "count")`。
  - `public static TimeSpan UtcOffsetOf(string canonical)`（`+05:45` → 5h45m、`-03:30` → −3h30m）；`public static string FormatUtcOffset(TimeSpan offset)`（反向，`+00:00` 帶正號）。
  - `PolicyCatalogFixtureTests.Catalog_MatchesSharedFixture` 的 `choices` 改成只聯集 Choice 與 RoleSet 的 Options。
- 文案（zh-TW）：`organization.maxDepth` 部門層數上限／新增部門或改上級部門時檢查；已超過的部門照常可改其他欄位。`locale.timeZone` 營運時區／決定「今天」、逾期、報表日期切點與日期時間的顯示；不支援日光節約；多台伺服器部署請先看部署文件再改（判斷 4）。分類 `organization` 組織。

- [ ] **Step 1: 寫失敗的測試**
  - `PolicyDefinitionTests.UtcOffset_Normalize`：`"+08:00"` → `"+08:00"`；`"+8:00"`、`"+08:15"`、`"UTC+8"`、`null` → null。
  - `PolicyDefinitionTests.UtcOffset_Convert`：`UtcOffsetOf("+05:45") == new TimeSpan(5, 45, 0)`、`UtcOffsetOf("-03:30") == -new TimeSpan(3, 30, 0)`；`FormatUtcOffset(TimeSpan.Zero) == "+00:00"`；38 個選項互轉一致。
  - `PolicyCatalogFixtureTests.Factories_EqualCurrentBehavior` 加 `organization.maxDepth` `"10"`、`locale.timeZone` `"+08:00"`。
  - `PolicyApiTests.System_TimeZone_RoundTrip`：admin GET 那項 `kind == "utcOffset"`、`options` 有 38 個且含 `+05:45`（`HasOptions` 沒補就會是 null）；PUT `{ value: "+07:00" }` 200、回應 `value == "+07:00"`；`{ value: "+07:15" }` 400，`errors.value[0] == "必須是 UTC 偏移，例如 +08:00"`；`{ value: 7 }`（數字）400。
  - `policy-types.vitest.spec.ts`：`policyValueText({ kind: 'utcOffset' }, '+08:00')` 為 `UTC+08:00`。
  - `PolicyValueEditor.vitest.spec.ts`：`utcOffset` 選 `+07:00` emit `'+07:00'`，選單有可讀名稱。
- [ ] **Step 2: 跑測試確認失敗**：`cd poc/backend && dotnet test tests/DynamicModules.DomainTests --filter "FullyQualifiedName~Policies"`；`dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~PolicyApiTests"`；`cd poc/frontend && bunx vitest run src/domain/dynamic/policy-types.vitest.spec.ts src/presentation/policies --maxWorkers=2`
- [ ] **Step 3: 實作型別、登記、JSON 轉換、編輯器、四語文案**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；`bunx vitest run src/domain/dynamic/policy-i18n.vitest.spec.ts src/i18n --maxWorkers=2`；`bun run typecheck`
- [ ] **Step 5: Commit**（`feat(policy): 時區型別，登記部門層數與營運時區`）

### Task 2: A5 部門層數

**Files:**
- Modify: `src/DynamicModules.Application/Dynamic/OrganizationService/OrganizationHandlers.cs:12-14,60-86,119-130`、`OrganizationMessages.cs:9`
- Test: `tests/DynamicModules.ApplicationTests/Dynamic/Organization/DepartmentTests.cs`

**Interfaces:**
- Produces: 刪 `DepartmentRules.MaxDepth`；`OrganizationMessages.TooDeep(int max) => $"部門最多 {max} 層"`；`DepartmentValidator(IOrganizationRepository org, IPermissionRepository permissions, IPolicyReader policies)`；`ValidateAsync(long? selfId, DepartmentInput input, CancellationToken ct, string? currentManager = null, long? currentParentId = null)`；層數只在 `selfId is null || input.ParentId != currentParentId` 時檢查。`UpdateDepartmentHandler` 傳 `d.ParentId`。

- [ ] **Step 1: 寫失敗的測試**
  - `DepartmentTests.Depth_UsesPolicy`：`organization.maxDepth` 2 → 建第 3 層 400、`errors.parentId[0] == "部門最多 2 層"`。
  - `DepartmentTests.Depth_Lowered_RenameDeepDepartmentAllowed`：先建 3 層，上限改 2 → 改第 3 層名稱成功；把一個第 1 層部門移到第 2 層底下（變第 3 層）400。
  - 既有「部門最多 10 層」測試不改斷言。
- [ ] **Step 2: 跑測試確認失敗**：`cd poc/backend && dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~DepartmentTests"`
- [ ] **Step 3: 實作**（另 `grep -rn "10" poc/frontend/src/pages/system/OrganizationPage.vue` 確認前端沒有層數常數；有就改讀生效值）
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；`--filter "FullyQualifiedName~Organization|FullyQualifiedName~ManagerChain"`
- [ ] **Step 5: Commit**（`feat(policy): 部門層數改讀政策（A5）`）

### Task 3: 全系統政策快照與營運時鐘

**Files:**
- Create: `src/DynamicModules.Application/Dynamic/PolicyService/SystemPolicySnapshot.cs`（`SystemPolicySnapshot`、`SystemPolicySnapshotLoader`）；O1 已有全系統政策的程序內快取時改成擴充那個類別（前置條件 5），O1 的 `ops.slowRequestMs`、`ops.clientErrorCollection` 讀取端改讀快照
- 不改: `src/DynamicModules.Application/Common/Time/IBusinessTime.cs`、`src/DynamicModules.Application/Common/Acting/ActingBusinessTime.cs`（判斷 19：不加 `Offset`）
- Modify: `src/DynamicModules.Application/Dynamic/PolicyService/PolicyHandlers.cs`（系統 PUT 加 hooks 迴圈的掛點，Task 4 才放 `TimeZoneChangeHook`）
- Create: `src/DynamicModules.Application/Common/Behaviors/SystemPolicySnapshotBehavior.cs`（判斷 20）
- Modify: `src/DynamicModules.Application/DependencyInjection.cs`（`SystemPolicySnapshot` singleton、`SystemPolicySnapshotLoader` scoped；`cfg.AddOpenBehavior(typeof(SystemPolicySnapshotBehavior<,>))` 放在 `:23` 的 `TransactionBehavior` 之前；ApplicationTests 經 `AddApplication()` 一起拿到）
- Create: `src/DynamicModules.WebAPI/Time/ZoneBusinessTime.cs`；Delete: `src/DynamicModules.WebAPI/Time/TaipeiBusinessTime.cs`
- Create: `src/DynamicModules.WebAPI/Middleware/SystemPolicySnapshotMiddleware.cs`
- Modify: `src/DynamicModules.WebAPI/Program.cs:58`（`AddSingleton<IBusinessTime, ZoneBusinessTime>()`，仍在 `:60` 的 `AddActingOverrides()` 之前）與啟動流程（migration 之後、`IndexBackfillJob` 之前 `await loader.RefreshAsync`）、middleware 管線（`SessionMiddleware`（`:129`）之前）
- 不改（測試基礎）: `tests/DynamicModules.WebAPITests/TestApp.cs:139-141,196`（`ActingBusinessTime(Clock)`、`FakeBusinessTime` 不需要偏移；偏移由快照提供）
- Test: `tests/DynamicModules.ApplicationTests/Dynamic/Policies/SystemPolicySnapshotTests.cs`、`tests/DynamicModules.WebAPITests/Dynamic/BusinessTimeZoneApiTests.cs`

**Interfaces:**
- Produces:
  - `IBusinessTime` 維持 `Now`、`Today` 兩個成員，不加 `Offset`（審查 p3-M1：`ActingBusinessTime` 沒覆寫預設成員，DI 解析到的會永遠是出廠值）。
  - `public sealed class SystemPolicySnapshot(PolicyEnvironmentDefaults env)`（singleton）：
    - `public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(60);`
    - `string Get(PolicyDefinition def)`（只收 `Scope == System`，否則丟 `InvalidOperationException`；沒載入過時回 `env.FactoryOf(def)`）、`TimeSpan BusinessOffset`（`PolicyCatalog.UtcOffsetOf(Get(LocaleTimeZone))`，換值時一起算好）。
    - `void Replace(IReadOnlyDictionary<string, string> effective, DateTime loadedUtc)`：建一份不可變的內部狀態（`FrozenDictionary`＋`BusinessOffset`＋`loadedUtc`），以 `Volatile.Write` 整份替換；讀取端 `Volatile.Read` 一次後只讀那一份（審查 p3-N3）。
    - `bool IsStale(DateTime utcNow)`；`SemaphoreSlim ReloadGate { get; }`（`new(1, 1)`，過期重載與系統 PUT 共用，判斷 20）。
  - `public sealed class SystemPolicySnapshotLoader(IPolicyReader reader, SystemPolicySnapshot snapshot)`（scoped）：
    - `Task RefreshAsync(CancellationToken ct)`：`PolicyCatalog.All` 中 System 範圍的逐項 `ResolveAsync(def, null).Effective`，然後 `Replace(values, DateTime.UtcNow)`。呼叫端負責持有 `ReloadGate`（behavior、`TryRefreshIfStaleAsync`）；啟動時單執行緒呼叫可不持鎖。
    - `Task TryRefreshIfStaleAsync(CancellationToken ct)`：`IsStale` 時 `ReloadGate.WaitAsync(0)`，拿不到鎖就直接返回（沿用舊值、不阻塞）；拿到後再確認一次 `IsStale` 才 `RefreshAsync`。
  - `ZoneBusinessTime(SystemPolicySnapshot snapshot) : IBusinessTime`：`Now => DateTime.SpecifyKind(DateTime.UtcNow + snapshot.BusinessOffset, DateTimeKind.Unspecified)`；`Today => DateOnly.FromDateTime(Now)`。仍被 `AddActingOverrides()` 包成 `ActingBusinessTime`，Demo 產生器的時間覆寫照舊。
  - `SystemPolicySnapshotMiddleware`：`await loader.TryRefreshIfStaleAsync(ct)` 後 `await next(context)`。
  - `SystemPolicySnapshotBehavior<TReq, TRes>(SystemPolicySnapshot snapshot, SystemPolicySnapshotLoader loader)`：`request is not UpdateSystemPolicyRequest` 直接 `next()`；否則 `await snapshot.ReloadGate.WaitAsync(ct)`，`try { var res = await next(); if (res is ServiceResult { IsSuccess: true }) await loader.RefreshAsync(ct); return res; } finally { snapshot.ReloadGate.Release(); }`。
  - 系統 PUT（`UpdateSystemPolicyHandler`，交易內）：寫設定 → `IPolicyChangeHook` 迴圈（Task 4）→ `CommitAsync` → `Invalidate()` → 回應；不自己碰快照。O2 的稽核寫入保持在同一交易。

- [ ] **Step 1: 寫失敗的測試**
  - `SystemPolicySnapshotTests.BeforeLoad_UsesFactory`：`BusinessOffset == 8h`；`Get(OrganizationMaxDepth) == "10"`；`Get(TaskMaxRulesPerModule)` 丟例外（Module 範圍）。
  - `SystemPolicySnapshotTests.Put_RefreshesSnapshotAfterCommit`（Review Focus 3，`Category=Postgres`）：送 `UpdateSystemPolicyRequest("locale.timeZone", J("\"+07:00\""))`（經 MediatR，含 `TransactionBehavior`）後，singleton `SystemPolicySnapshot.BusinessOffset == 7h`；同一容器新 scope 解析的 `IBusinessTime.Now` 與 `DateTime.UtcNow + 7h` 相差 5 秒內（ApplicationTests 若用假時鐘，改斷言 `snapshot.BusinessOffset`）。
  - `SystemPolicySnapshotTests.Put_RolledBack_SnapshotUnchanged`（審查 p3-S2，`Category=Postgres`）：註冊一個測試用 `IPolicyChangeHook` 回 `ServiceResult.Invalid(...)` → PUT `+07:00` 回 400、`Dynamic_SystemSetting` 沒有 `policy.locale.timeZone`、`snapshot.BusinessOffset` 仍 8h、`ReloadGate.CurrentCount == 1`（鎖已釋放）。（`IPolicyChangeHook` 的介面與 handler 裡的迴圈在本 Task 建，`TimeZoneChangeHook` 留到 Task 4。）
  - `SystemPolicySnapshotTests.StaleReload_SkippedWhilePutHoldsGate`（判斷 20）：手動 `ReloadGate.WaitAsync()` 模擬 PUT 進行中、快照設成過期 → `TryRefreshIfStaleAsync` 立即返回、不讀政策、快照不變；`Release()` 後再呼叫才重載。
  - `SystemPolicySnapshotTests.ConcurrentStaleReload_RunsOnce`（審查 p3-N3）：快照設成過期，平行跑 20 個 `TryRefreshIfStaleAsync`，`IPolicyReader` 替身記錄 `locale.timeZone` 只被讀一次；沒有任何呼叫丟例外。
  - `BusinessTimeZoneApiTests.Middleware_ReloadsWhenStale`（Review Focus 3，`Category=Postgres`）：直接寫 `Dynamic_SystemSetting` 的 `policy.locale.timeZone = "+07:00"`（模擬另一台伺服器改的），立刻 GET 任一 API 後 `snapshot.BusinessOffset` 仍 8h；用 `Replace` 把 `loadedUtc` 調到 61 秒前再 GET，變 7h。
  - `BusinessTimeZoneApiTests.Now_FollowsZone`：用 `ZoneBusinessTime` 真實實作（不用 TestApp 的替身），`+07:00` 時 `Now` 比 `+08:00` 少一小時（容許 5 秒誤差），`Kind == Unspecified`。
- [ ] **Step 2: 跑測試確認失敗**：`cd poc/backend && dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~SystemPolicySnapshotTests"`；`dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~BusinessTimeZoneApiTests"`
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；另跑 `dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~PolicyApiTests|FullyQualifiedName~EndpointRoutesTests|FullyQualifiedName~DemoDataApiTests"`；`dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~ActingScopeTests|FullyQualifiedName~Demo"`（Acting 裝飾器照常）；PostgreSQL：`FORMWRIGHT_TEST_DB=postgres dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~SystemPolicySnapshotTests&Category!=SqliteOnly&Category!=PgDeferred&Category!=Benchmark"`、WebAPITests 同法跑 `BusinessTimeZoneApiTests`
- [ ] **Step 5: Commit**（`feat(policy): 全系統政策快照與依政策的營運時鐘`）

### Task 4: 後端各處改用營運時區；改時區時平移索引

**Files:**
- Modify: `src/DynamicModules.Domain/FieldTypes/Handlers/DateTimeHandler.cs:15,20,30`、`src/DynamicModules.Domain/FieldTypes/FieldTypeRegistry.cs:19-25`
- Modify: `src/DynamicModules.Application/DependencyInjection.cs:83`
- Modify: `src/DynamicModules.Application/Dynamic/Display/FieldDisplay.cs:14-15,17,51`＋5 個呼叫端（`GetRecordHistoryHandler.cs:69`、`ExportRecordsHandler.cs:85`、`PrintValueFormat.cs:21`、`ReportRunner.Output.cs:150,196`）：呼叫端注入 `SystemPolicySnapshot` 傳 `snapshot.BusinessOffset`（判斷 19）；`PrintValueFormat` 是靜態工具時由它的呼叫端傳入
- Modify: `src/DynamicModules.Application/Dynamic/ImportService/FieldImportParser.cs:16,91-99`、`ImportPlanner.cs:88`
- Modify: `src/DynamicModules.Application/Dynamic/ReportService/Data/ReportRules.cs:237-239`（`Check` 多一個 `TimeSpan businessOffset`）、`ReportDefinitionValidator.cs:24`
- Modify: `src/DynamicModules.Application/Dynamic/ReportService/ReportRunner.Cells.cs:139-148`（`ReportRunner` 建構子注入 `SystemPolicySnapshot`，在建執行內容 `ctx` 時存一次偏移，同一次執行用同一個值）
- Modify: `src/DynamicModules.Application/Dynamic/TaskRuleService/TaskRuleEvaluator.cs:37,52`、`TaskRuleApplier.cs:16-17,70,79`（`TaskRuleApplier` 注入 `SystemPolicySnapshot`）
- Create: `src/DynamicModules.Application/Dynamic/RecordService/TimeZoneChangeHook.cs`（`IPolicyChangeHook` 介面已在 Task 3 建）
- Modify: `src/DynamicModules.Domain/Repositories/Dynamic/IRecordRepository.cs`、`src/DynamicModules.Persistence/Repositories/Dynamic/RecordRepository.cs`
- Modify: `PolicyHandlers.cs`（系統 PUT 在 commit 前呼叫 hooks；Task 3 已留掛點）
- Test: `tests/DynamicModules.DomainTests/FieldTypes/DateTimeHandlerTests.cs`、`tests/DynamicModules.ApplicationTests/Dynamic/FieldDisplayTests.cs`、`FieldImportParserTests.cs`、`tests/DynamicModules.ApplicationTests/Dynamic/Policies/TimeZoneChangeTests.cs`（新，`Category=Postgres`）、`ReportSqlEquivalenceTests.cs`、`tests/DynamicModules.WebAPITests/Dynamic/BusinessTimeZoneApiTests.cs`（加一條，審查 p3-M1）

**Interfaces:**
- Produces:
  - `DateTimeHandler(Func<TimeSpan>? businessOffset = null)`（null → `PolicyCatalog.UtcOffsetOf(LocaleTimeZone.Factory)`）；`Normalize` 輸出 `dto.ToOffset(o).ToString("yyyy-MM-dd\\THH:mm:ss") + PolicyCatalog.FormatUtcOffset(o)`；`ToIndex` 用同一個偏移。`FieldTypeRegistry.CreateDefault(Func<TimeSpan>? businessOffset = null)`；DI 改 `services.AddSingleton(sp => FieldTypeRegistry.CreateDefault(() => sp.GetRequiredService<SystemPolicySnapshot>().BusinessOffset))`。
  - `FieldDisplay.Format(Field field, JsonElement? value, string locale, ReferenceInfo? reference, IReadOnlyDictionary<string, string> userNames, TimeSpan businessOffset)`；刪 `BusinessOffset`。呼叫端傳 `SystemPolicySnapshot.BusinessOffset`（不是 `IBusinessTime`，判斷 19）。
  - `ImportLookups(..., TimeSpan BusinessOffset)`；`FieldImportParser.DateTime` 用它（`yyyy-MM-dd HH:mm` 視為營運時區、輸出帶營運偏移）。
  - `ReportRules.Check(..., int maxDays, TimeSpan businessOffset)`：沒帶偏移的日期時間補 `FormatUtcOffset(businessOffset)`。
  - `TaskRuleEvaluator.DueDate(TaskRuleTask def, Module module, IReadOnlyDictionary<string, JsonElement?> values, TimeSpan businessOffset)`。
  - `public interface IPolicyChangeHook { Task<ServiceResult?> BeforeCommitAsync(PolicyDefinition def, string oldEffective, string newEffective, CancellationToken ct); }`（Task 3 建）；系統 PUT 在寫入設定後、commit 前依序呼叫，回非 null 就直接回該結果（`TransactionBehavior` 看到失敗結果會回滾）。`oldEffective` 取 PUT 之前 `reader.ResolveAsync(def, null).Effective`，不是快照（快照可能還沒過期重載）。
  - `TimeZoneChangeHook(IRecordRepository records) : IPolicyChangeHook`：只處理 `LocaleTimeZone` 且新舊不同；`delta = UtcOffsetOf(new) - UtcOffsetOf(old)`；`await records.ShiftDateTimeIndexAsync(delta, ct)`。
  - `IRecordRepository.ShiftDateTimeIndexAsync(TimeSpan delta, CancellationToken ct) : Task<int>`：`RecordIndex` 中 `FieldId` 屬於 `Type == FieldType.DateTime` 的欄位、`DateValue != null` 的列，`ExecuteUpdateAsync(s => s.SetProperty(i => i.DateValue, i => i.DateValue!.Value.AddMinutes(delta.TotalMinutes)))`。不寫 raw SQL；`ExecuteUpdateAsync` 不經 `SaveChanges` 攔截器，但在 `ITransactionalRequest` 的交易內，PostgreSQL 的全庫寫入鎖在交易開始時已取（pg0 §5.6）。兩種 provider 的翻譯由 `TimeZoneChangeTests` 在雙跑集實測，註解寫「兩種資料庫已驗」而不是宣稱。
  - 類別註解裡的「台北」「+08:00」改成「營運時區（政策 locale.timeZone）」。`DemoService` 種子裡的 `+08:00` 字面量是一個絕對時間，寫入時會被正規化，保留不改。

- [ ] **Step 1: 寫失敗的測試**
  - `DateTimeHandlerTests.Normalize_UsesGivenOffset`：偏移 +07:00，輸入 `2026-11-11T02:00:00Z` → `"2026-11-11T09:00:00+07:00"`；`ToIndex` 的 `DateValue == 2026-11-11 09:00`。既有測試（無參數建構）不改。
  - `FieldDisplayTests.DateTime_UsesGivenOffset`：值 `2026-11-11T10:00:00+08:00`、偏移 +07:00 → `"2026-11-11 09:00"`。
  - `FieldImportParserTests.DateTime_LocalTextUsesBusinessOffset`：`2026-11-11 09:00`、偏移 +07:00 → `"2026-11-11T09:00:00+07:00"`。
  - `TimeZoneChangeTests`（整個類別標 `Category=Postgres`；審查 p3-S4）：
    - `TimeZoneChange_ShiftsDateTimeIndexOnly`（Review Focus 1）：一筆記錄有日期時間欄位 `2026-11-11T10:00:00+08:00` 與日期欄位 `2026-11-11`；PUT 時區 `+07:00` 後，日期時間索引是 09:00、日期索引仍 00:00；從資料庫重新讀回的 `DateValue.Kind == DateTimeKind.Unspecified`；記錄原值字串不變；再改回 `+08:00` 索引回到 10:00。
    - `TimeZoneChange_FractionalOffset`：`+08:00` 改 `+05:45`，索引是 07:45（`AddMinutes(-135)` 的翻譯在兩種資料庫都對）。
    - `TimeZoneChange_ReportDayBucketFollowsNewZone`（Review Focus 1）：值 `2026-11-11T00:30:00+08:00` 在 +08:00 分到 11-11，改 `+07:00` 後分到 11-10；SQL 路徑與記憶體路徑結果相同（沿用 `ReportSqlEquivalenceTests` 的比較 helper；pg1 沒合併就這一案標 `PgDeferred`）。
    - `TimeZoneChange_ListFilterBetweenFollowsNewZone`：列表篩選 `between 2026-11-11T09:00 ~ 09:30`（不帶偏移，視為營運時區）在 +07:00 時找得到上面那筆。
    - `SameValue_NoShift`：PUT 同一個值，索引不動、`ShiftDateTimeIndexAsync` 沒被呼叫。
    - `HookRejects_IndexAndSettingRolledBack`：測試用的第二個 hook 在 `TimeZoneChangeHook` 之後回失敗 → 400，日期時間索引仍 10:00、設定沒寫入（平移與設定同交易）。
  - `BusinessTimeZoneApiTests.Export_FollowsZoneUnderActingDecorator`（審查 p3-M1，Review Focus 3，`Category=Postgres`）：用 `TestApp`（`IBusinessTime` 是 `ActingBusinessTime(Clock)`，與正式環境同樣被裝飾）；admin PUT `system/policies/locale.timeZone` `"+07:00"` 後，匯出含 `2026-11-11T10:00:00+08:00` 的記錄，CSV 那格是 `2026-11-11 09:00`；同一筆的歷程顯示與列印值也是 09:00。改回 `+08:00` 後是 10:00。這條在偏移仍取 `IBusinessTime` 預設成員的寫法下必須失敗。
- [ ] **Step 2: 跑測試確認失敗**：`cd poc/backend && dotnet test tests/DynamicModules.DomainTests --filter "FullyQualifiedName~DateTimeHandlerTests"`；`dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~FieldDisplayTests|FullyQualifiedName~FieldImportParserTests|FullyQualifiedName~TimeZoneChangeTests"`；`dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~BusinessTimeZoneApiTests"`
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；另跑 `dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~Report|FullyQualifiedName~TaskRule|FullyQualifiedName~Import|FullyQualifiedName~RecordIndex|FullyQualifiedName~Export"`；PostgreSQL：`FORMWRIGHT_TEST_DB=postgres dotnet test tests/DynamicModules.ApplicationTests --filter "(FullyQualifiedName~TimeZoneChangeTests|FullyQualifiedName~RecordIndexTests)&Category!=SqliteOnly&Category!=PgDeferred&Category!=Benchmark"`、WebAPITests 同法跑 `BusinessTimeZoneApiTests`（`AddMinutes` 翻譯錯會在這裡出現，不能只看 SQLite）；殘留掃描（審查 p3-N1，排除 cookie 的 `ExpireTimeSpan = TimeSpan.FromHours(8)` 與新的 `SystemPolicySnapshot.BusinessOffset` 屬性名）：`grep -rnE "Taipei|FieldDisplay\.BusinessOffset|\"\+08:00\"|\+ \"\+08:00\"" poc/backend/src --include='*.cs' | grep -v "Demo\|Seed\|PolicyCatalog"` 應無結果，另 `grep -rn "FromHours(8)" poc/backend/src --include='*.cs'` 只剩 `Program.cs` 的 cookie 那一行
- [ ] **Step 5: Commit**（`feat(policy): 後端日期時間改用營運時區政策，改時區時平移索引（A17）`）

### Task 5: 前端時區改從 API 取

**Files:**
- Create: `src/domain/dynamic/business-time.ts`、`src/test-setup.ts`
- Modify: `vitest.config.ts`（`setupFiles: ['src/test-setup.ts']`）
- Modify: `src/router/auth-guard.ts`（登入後取生效值並設定時區）
- Modify: `src/presentation/dynamic/field-type-registry.ts:128-131,136-142,161,282`、`src/domain/dynamic/rule-engine.ts:24-25,84-85`、`src/components/framework/DateSelect.vue:4-12`、`src/presentation/reports/designer/FilterValueInput.vue:57-64`、`src/presentation/dynamic/rule-state.ts:5-9`（`todayLocal` 改名 `todayBusiness`，呼叫端一併改）
- Modify: `src/pages/system/PoliciesPage.vue`（存 `locale.timeZone` 前確認、成功後 `setBusinessOffset`）、`src/i18n/*.json`（`policies.timeZoneWarning`、`policies.confirm`、`policies.timeZoneChanged`、`policies.reload`）
- Create: `src/application/dynamic/queries/use-business-time-watch.ts`（判斷 18）；Modify: `src/layouts/MainLayout.vue`（呼叫它）
- Test: `src/domain/dynamic/business-time.vitest.spec.ts`、`field-type-registry.vitest.spec.ts`、`rule-engine` 既有 spec、`src/components/framework/DateSelect.vitest.spec.ts`（新或既有）、`FilterValueInput` 既有 spec、`src/router/auth-guard.vitest.spec.ts`、`src/pages/system/PoliciesPage.vitest.spec.ts`、`src/application/dynamic/queries/use-business-time-watch.vitest.spec.ts`（新）、`src/layouts/MainLayout.vitest.spec.ts`（既有，mock 生效值）

**Interfaces:**
- Produces（`business-time.ts`）:
  - `export function setBusinessOffset(offset: string): void`（不合 `^[+-]\d{2}:\d{2}$` 丟錯）
  - `export function businessOffset(): string`（沒設定過丟 `Error('business time zone not loaded')`）
  - `export function businessOffsetMs(): number`
  - `export function toBusinessWallClock(iso: string): string | null`（帶 Z 或任何偏移 → 營運時區 `yyyy-MM-ddTHH:mm:ss`；不帶偏移視為已是營運時區、原樣補秒；解析不了回 null）
  - `export function withBusinessOffset(wall: string): string`（`yyyy-MM-ddTHH:mm[:ss]` → 補秒＋`businessOffset()`）
  - `export function businessToday(now?: Date): string`（`yyyy-MM-dd`）
- `test-setup.ts`：`setBusinessOffset('+08:00')`（註解：等於後端出廠值，只給測試用）。
- auth guard：`getMe` 成功後 `const eff = await dynamicApi.getEffectivePolicies(null); setBusinessOffset(String(eff.values['locale.timeZone']))`；失敗比照 `getMe` 非 401 的處理（導到 service-unavailable）。公開頁面（登入、service-unavailable）不呼叫 `businessOffset()`，`business-time.ts` 檔頭註解寫明「只能在登入後的頁面呼叫，未設定就丟錯」（審查 p3-N4）。
- `use-business-time-watch.ts`（判斷 18，使用者裁決 Q9）：`export function useBusinessTimeWatch(): void`：`useQuery({ queryKey: ['effective-policies', ''], queryFn: () => dynamicApi.getEffectivePolicies(null), staleTime: 60_000, refetchInterval: 60_000, refetchIntervalInBackground: false, refetchOnWindowFocus: true })`（與 `useEffectivePolicies(null)` 同一個 query key，共用快取）；`watch` 資料，`locale.timeZone` 與 `businessOffset()` 不同時：`setBusinessOffset(新值)`，再 `Dialog.create({ message: t('policies.timeZoneChanged', { offset: 'UTC' + 新值 }), persistent: true, ok: { label: t('policies.reload') }, cancel: false })`，按下後 `window.location.reload()`。同一個新值只跳一次。`MainLayout.vue` 的 `setup` 呼叫一次。
- 文案（zh-TW）：`policies.timeZoneWarning`＝設計 §3.5 原文「改時區會讓今天、逾期、報表日期切點跟著變；記錄的日期時間欄位保持原本的絕對時間，系統自記的時間不換算」（使用者 2026-11-12 確認）；`policies.confirm`＝「確定」；`policies.timeZoneChanged`＝「營運時區已改成 {offset}，請重新整理頁面，日期時間才會正確顯示與存檔」；`policies.reload`＝「重新整理」。四語都要有。

- [ ] **Step 1: 寫失敗的測試**
  - `business-time.vitest.spec.ts`：未設定時 `businessOffset()` 丟錯（測試內先用 `vi.resetModules` 取乾淨模組）；`+07:00` 時 `toBusinessWallClock('2026-11-11T10:00:00+08:00') === '2026-11-11T09:00:00'`、`('2026-11-11T02:00:00Z') === '2026-11-11T09:00:00'`、`('2026-11-11T09:00') === '2026-11-11T09:00:00'`；`withBusinessOffset('2026-11-11T09:00') === '2026-11-11T09:00:00+07:00'`；`businessToday(new Date('2026-11-10T17:30:00Z'))` 在 +07:00 是 `2026-11-11`、在 +05:30 是 `2026-11-10`。
  - `field-type-registry.vitest.spec.ts`：`displays stored offset in business zone`（Review Focus 1）：+07:00 時 datetime 顯示 `2026-11-11 09:00`；`normalizeForRules` 回 `…+07:00`。既有 +08:00 斷言不改（setup 是 +08:00）。
  - `DateSelect emits business offset`（Review Focus 2）：+07:00 輸入 `2026-11-11T09:00` emit `2026-11-11T09:00:00+07:00`；modelValue `2026-11-11T10:00:00+08:00` 時輸入框顯示 `2026-11-11T09:00`。
  - `FilterValueInput converts other offsets`（Review Focus 2）：+07:00 時 `…+08:00` 的值顯示換算後的牆上時間（原本 `endsWith('+08:00')` 的捷徑刪掉）。
  - rule-engine 既有 spec：加一案 +07:00 時 datetime 比較以營運時區的日期切點為準。
  - `auth-guard.vitest.spec.ts`：`登入後設定營運時區`：mock `getEffectivePolicies` 回 `+07:00` → 導覽完成後 `businessOffset() === '+07:00'`；`取生效值失敗導到 service-unavailable`；`公開頁面導覽不需要營運時區`（審查 p3-N4）：`vi.resetModules` 取未設定的 `business-time`，未登入導到 `/login` 成功、不呼叫 `getEffectivePolicies`、沒有丟錯。
  - `PoliciesPage.vitest.spec.ts`：`改時區要先確認`：改 `locale.timeZone` 按儲存出現對話框，文字含 `記錄的日期時間欄位保持原本的絕對時間` 與 `系統自記的時間不換算`；取消不呼叫 `saveSystemPolicy`；確定後呼叫且成功後 `businessOffset()` 是新值。
  - `use-business-time-watch.vitest.spec.ts`（審查 p3-S3，Review Focus 2）：
    - `時區沒變不提示`：生效值回 `+08:00`（setup 值）→ 不呼叫 `Dialog.create`。
    - `時區變了先換偏移再要求重新整理`：生效值第二次回 `+07:00`（`vi.advanceTimersByTime(60_000)` 觸發重取）→ `businessOffset() === '+07:00'`；`Dialog.create` 被呼叫一次，參數 `persistent: true`、訊息含 `UTC+07:00`；按確定呼叫 `window.location.reload`（stub）。
    - `同一個新值只提示一次`：之後再重取仍是 `+07:00` → `Dialog.create` 仍只被呼叫一次。
  - `MainLayout.vitest.spec.ts`：既有案例補 mock `getEffectivePolicies` 回出廠值，斷言不變。
- [ ] **Step 2: 跑測試確認失敗**：`cd poc/frontend && bunx vitest run src/domain/dynamic/business-time.vitest.spec.ts src/presentation/dynamic/field-type-registry.vitest.spec.ts src/components/framework src/presentation/reports/designer src/router src/pages/system/PoliciesPage.vitest.spec.ts src/application/dynamic/queries src/layouts --maxWorkers=2`
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；`bunx vitest run src/domain src/presentation/dynamic src/i18n --maxWorkers=2`；`bun run typecheck`；`grep -rn "08:00\|8 \* 3_\?600_000\|Taipei\|台北" src --include='*.ts' --include='*.vue' | grep -v "spec.ts\|test-setup"` 應無結果（i18n JSON 不在掃描範圍；新文案的 `{offset}` 不含寫死偏移）
- [ ] **Step 5: Commit**（`feat(policy): 前端日期時間改用 API 的營運時區（A17）`）

### Task 6: p3a 文件、實機與 gate

**Files:**
- Modify: 設計文件：§10 技術限制補 `organization.maxDepth` 1–30、時區選項清單、`SystemPolicySnapshot.MaxAge` 60 秒（與 O1 B2 共用同一份快取）；§10 加「營運時區以固定偏移表示，不支援日光節約」與「多台部署改時區的已知限制」（判斷 4）；§3.5、§3.7 已有使用者裁決，不重寫；§12 只加技術判斷 3、19、20（判斷 1、5、6、18 是使用者 2026-11-12 裁決）；路線文件（p3a）；功能清單（p3 改 🚧）；`docs/manual.md`（政策設定一節的「地區與語系」「組織」、改時區的確認框與「其他人頁面會要求重新整理」以畫面為準；報表、任務、匯入章節裡寫「台北時間」「+08:00」的地方改成「營運時區（出廠 +08:00）」）；`docs/deploy.md`（多台部署時政策最多 60 秒生效；改時區請在離峰時段、先縮成一台再改，否則其他台在 60 秒內寫入的日期時間索引會差一段時差）

- [ ] **Step 1: 實機**：四種寬度開 `/system/policies`，把時區改成 `+07:00`（確認對話框文字、四語各看一次），回到任一有日期時間欄位的模組：列表與表單顯示少一小時、新增一筆再編輯值不跳動、報表依日分組的切點改變；另開一個已登入的分頁，60 秒內跳出「營運時區已改成 UTC+07:00」且只能按重新整理；再改回 `+08:00`。組織頁把層數改 2 後新增第 3 層看到錯誤。不得橫向捲動。
- [ ] **Step 2: PostgreSQL 全套試跑**（動到 Persistence，pg0 §6.6；不進 gate）：`FORMWRIGHT_TEST_DB=postgres dotnet test tests/DynamicModules.ApplicationTests --filter "Category!=SqliteOnly&Category!=PgDeferred&Category!=Benchmark"`、WebAPITests 同一 filter；全數通過，時間與結果貼進 p3 結果檔。
- [ ] **Step 3: gate**：`bash scripts/unattended/gate.sh <worktree 絕對路徑>`，每步 `exit=0`（含 PostgreSQL 兩步）
- [ ] **Step 4: Commit**（`docs: 營運時區與部門層數政策的手冊、可寫死清單與 p3a 進度`）

---

# p3b 語系

### Task 7: 語系三政策、互相約束、公開給登入前

**Files:**
- Modify: `src/DynamicModules.Domain/Policies/PolicyCatalog.cs`（`LocaleDefault`、`LocaleEnabled`、`LocaleRequiredText`，Options＝`I18nText.SupportedLocales`）
- Create: `src/DynamicModules.Domain/Policies/LocalePolicyRules.cs`
- Modify: `src/DynamicModules.Application/Dynamic/PolicyService/PolicyHandlers.cs`（系統 PUT 檢查）、`PolicyMessages.cs`、`SystemPolicySnapshot.cs`（三個 getter）
- Modify: `src/DynamicModules.Application/Dynamic/AccountService/Queries/GetAuthOptions/GetAuthOptionsHandler.cs`（與其 response record）
- Modify: `PolicyMessages.cs` 的 `Invalid`：RoleSet 分支在 `d.Category == "locale"` 時回 `只能從以下語系挑選：…`（判斷 21，審查 p3-S5）
- Modify: `poc/shared/policy-catalog.json`（codes 加 3 個；choices 加 `zh-TW`、`en-US`、`th`、`vi`）、`poc/frontend/src/i18n/*.json`、`poc/frontend/src/domain/dynamic/types.ts`（`AuthOptions` 加兩欄）
- Test: `tests/DynamicModules.DomainTests/Policies/LocalePolicyRulesTests.cs`、`PolicyCatalogFixtureTests.cs`、`tests/DynamicModules.WebAPITests/Dynamic/PolicyApiTests.cs`、`tests/DynamicModules.WebAPITests/AuthApiTests.cs`（main 上沒有 `auth/options` 的測試，新案例加在這裡；它在雙跑集）

**Interfaces:**
- Produces:
  - `public static class LocalePolicyRules { public static string? Check(string defaultLocale, IReadOnlyList<string> enabled, IReadOnlyList<string> required); }`：回 null 或下列訊息之一（放在 `PolicyMessages`，Domain 回代碼、Application 轉訊息亦可；擇一即可，但訊息原文如下）：
    - 啟用為空：`至少要啟用一種語系`
    - 預設不在啟用：`預設語系必須是啟用中的語系；要停用它，請先改預設語系`
    - 必填為空：`至少要有一種必填語系`
    - 必填不在啟用：`必填語系必須都是啟用中的語系；要停用它，請先從必填語系拿掉`
  - 系統 PUT：代碼是三者之一時，取「這次的新值（null 時是上一層值）＋另外兩個的生效值」呼叫 `Check`，有錯回 400（鍵 `value`）。
  - `SystemPolicySnapshot`：`string DefaultLocale`、`IReadOnlyList<string> EnabledLocales`、`IReadOnlyList<string> RequiredTextLocales`。
  - `AuthOptionsResponse` 加 `string DefaultLocale, IReadOnlyList<string> EnabledLocales`（從快照取）。
- 文案（zh-TW）：`locale.default` 預設語系／沒選過語言的人看到的語系，也是 API 沒指定語系時的回應語系。`locale.enabled` 啟用語系／語言選單與多語輸入框只列這些；停用不會刪掉已輸入的文字。`locale.requiredText` 必填的多語文字要有哪些語系（設計 A18 的政策名，使用者 2026-11-12 確認）／名稱、標籤、選項、訊息等所有必填的多語文字都要填這些語系；增加後只在新增或修改那段文字時檢查；應用程式套件仍要求繁體中文。選項：`zh-TW` 繁體中文、`en-US` English、`th` ไทย、`vi` Tiếng Việt（四語都用各語言的自稱，同 `src/i18n/locale.ts`）。

- [ ] **Step 1: 寫失敗的測試**
  - `LocalePolicyRulesTests`：出廠組合 null；四種錯各一案，斷言訊息原文。
  - `PolicyCatalogFixtureTests.Factories_EqualCurrentBehavior` 加三項。
  - `PolicyApiTests.LocaleRules_DisableDefault_400`（Review Focus 4，`Category=Postgres`）：PUT `locale.enabled` `["en-US"]` → 400、訊息含「請先改預設語系」。
  - `PolicyApiTests.LocaleRules_SwitchToEnglishInOrder`（Review Focus 4，`Category=Postgres`）：依序 PUT `locale.default` `"en-US"` → `locale.requiredText` `["en-US"]` → `locale.enabled` `["en-US"]` 三次都 200；再 PUT `locale.default` `null`（回出廠 zh-TW，但 zh-TW 未啟用）→ 400。
  - `PolicyApiTests.LocaleEnabled_UnsupportedLocale_SaysLocale`（審查 p3-S5）：PUT `locale.enabled` `["zh-TW","ja"]` → 400，`errors.value[0] == "只能從以下語系挑選：zh-TW、en-US、th、vi"`；既有通知收件角色的 RoleSet 錯誤仍是 `只能從以下角色挑選：…`。
  - `AuthApiTests.Options_IncludesLocaleSettings`（`Category=Postgres` 隨類別）：未登入 GET `auth/options` 含 `defaultLocale: "zh-TW"`、`enabledLocales` 四個；PUT `locale.default` `"en-US"` 後（快照已換）未登入再 GET 回 `en-US`。
- [ ] **Step 2: 跑測試確認失敗**：`cd poc/backend && dotnet test tests/DynamicModules.DomainTests --filter "FullyQualifiedName~Policies"`；`dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~PolicyApiTests|FullyQualifiedName~AuthApiTests"`
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；PostgreSQL：`FORMWRIGHT_TEST_DB=postgres dotnet test tests/DynamicModules.WebAPITests --filter "(FullyQualifiedName~PolicyApiTests|FullyQualifiedName~AuthApiTests)&Category!=SqliteOnly&Category!=PgDeferred&Category!=Benchmark"`；前端 `bunx vitest run src/domain/dynamic/policy-i18n.vitest.spec.ts src/i18n --maxWorkers=2`
- [ ] **Step 5: Commit**（`feat(policy): 登記語系政策與互相約束，登入前可取預設與啟用語系`）

> Task 7 登記後到 Task 10 之前，畫面上的語系政策還沒生效；p3b 整段一起合併。

### Task 8: 必填語系改讀政策（後端）

**Files:**
- Modify: `src/DynamicModules.Domain/ValueObjects/I18nText.cs`
- Create: `src/DynamicModules.Application/Common/RequiredTextGuard.cs`（`RequiredTextGuard`、`LocaleNames`）
- Modify（FluentValidation，建立類改用 guard、更新類只留格式檢查）: `FieldService/Data/FieldRules.cs:50-51`（`ValidLabel` 擴充方法，`ViewRules.cs:24` 經它間接檢查名稱）、`FieldService/Commands/{AddField,UpdateField,AddOption,UpdateOption,AddBlock,UpdateBlock}/*Validator.cs`、`ModuleService/Data/ModuleRules.cs:16`（與用到它的 Create／Update 模組 validator）、`ViewService/Data/ViewRules.cs:24`（`ValidateShape` 的 `RuleFor(name).ValidLabel()`；建立與更新檢視分開處理）
- Modify（更新類 handler 補「改了才檢查」）: `UpdateFieldHandler`、`UpdateOptionHandler`、`UpdateBlockHandler`、`UpdateModuleHandler`、檢視的更新 handler
- Modify（handler 內的驗證器）: `OrganizationService/OrganizationHandlers.cs:68`、`EventService/EventValidator.cs:37`、`RuleService/Commands/SaveRules/SaveRulesHandler.cs:73`、`TaskRuleService/TaskRuleValidator.cs:40`、`ApprovalService/ApprovalDefinitionValidator.cs:58`、`ApprovalService/ApprovalFlowHandlers.cs:37`、`ConversionService/ConversionValidator.cs:24`、`CheckService/CheckValidator.cs:30,69`
- Modify: `AppService/AppPackageValidator.cs:145-149`（套件仍要求繁中，改用 `IsWellFormed`＋`HasText(FallbackLocale)`，錯誤文字不變）
- Test: `tests/DynamicModules.DomainTests/ValueObjects/I18nTextTests.cs`、`tests/DynamicModules.ApplicationTests/Dynamic/Policies/RequiredTextPolicyTests.cs`（新）、`FieldServiceTests.cs`、`tests/DynamicModules.WebAPITests/Dynamic/AppFormat7Tests.cs`

**Interfaces:**
- Produces（Domain `I18nText`）:
  - 刪 `IsValid(out string? error)`；新增 `public bool IsWellFormed(out string? error)`：沒有不支援的語系鍵（錯誤文字 `不支援的語系：{key}` 不變），且至少一個語系有非空白文字（`至少要有一種語系的文字`）。
  - `public bool HasText(string locale)`；`public IReadOnlyList<string> MissingLocales(IEnumerable<string> required)`；`public bool SameAs(I18nText? other)`（各語系去空白、忽略空值後比對）。
  - `Resolve(string locale)`：要求的語系 → `FallbackLocale` → `SupportedLocales` 順序中第一個有值的 → `""`。
- Produces（Application）:
  - `public sealed class RequiredTextGuard(SystemPolicySnapshot snapshot)`（singleton）：`public string? Check(I18nText? incoming, I18nText? stored, string subject)`：
    - `incoming` 為 null 或不 `IsWellFormed` → `$"{subject}必須有{Names(required)}"`；
    - `stored` 不為 null 且 `incoming.SameAs(stored)` → null（§3.5）；
    - 否則 `missing = incoming.MissingLocales(snapshot.RequiredTextLocales)`，有缺 → `$"{subject}必須有{Names(missing)}"`。
    - `Names` 用頓號串 `LocaleNames.Zh`：`zh-TW` 繁體中文、`en-US` 英文、`th` 泰文、`vi` 越南文。出廠值時產生的文字與現在逐字相同：`名稱必須有繁體中文`、`關卡名稱必須有繁體中文`、`訊息必須有繁體中文`。
  - 各處 subject：名稱類 `名稱`、簽核關卡 `關卡名稱`、檢查訊息 `訊息`。`SaveRulesHandler:73` 的現有文字「驗證規則必須有訊息」不提語系，維持原文（guard 回非 null 就用這句）。
  - FluentValidation：建立類 validator 注入 `RequiredTextGuard`，`.Custom((v, ctx) => { if (guard.Check(v, null, "名稱") is { } e) ctx.AddFailure(e); })`；更新類 validator 改成只檢查 `IsWellFormed`（訊息 `名稱必須有{必填語系}`，同 guard），handler 載入既有實體後呼叫 `guard.Check(request.Label, entity.LabelI18n, "名稱")`，錯誤鍵沿用 validator 原本的屬性名（例 `Label`、`Name`）。
  - handler 內驗證器（Organization、Event、TaskRule、Approval、Conversion、Check、SaveRules）：新增時 `stored` 傳 null；更新時傳資料庫裡的值（各驗證器多一個 `I18nText? stored` 參數，或由 handler 先比對再決定要不要檢查；擇一，同一檔內一致）。

- [ ] **Step 1: 寫失敗的測試**
  - `I18nTextTests`：`IsWellFormed` 對 `{}`、`{"zh-TW":" "}` false、`{"en-US":"Order"}` true、`{"jp":"x"}` false 且訊息 `不支援的語系：jp`；`Resolve("th")` 對 `{"en-US":"Order"}` 回 `Order`、對 `{"zh-TW":"訂單","en-US":"Order"}` 回 `訂單`；`SameAs` 忽略空白與空值。
  - `RequiredTextPolicyTests.Factory_MessagesUnchanged`：出廠值下缺繁中的欄位、事件、部門、簽核關卡、檢查訊息，錯誤文字各與現在相同。
  - `RequiredTextPolicyTests.AddField_RequiresEveryRequiredLocale`：`locale.requiredText` `["zh-TW","en-US"]`（先確認 en-US 已啟用）→ 只給繁中 400、`errors.Label[0] == "名稱必須有英文"`。
  - `RequiredTextPolicyTests.UpdateField_UnchangedLabel_SkipsNewRequiredLocale`（Review Focus 5，`Category=Postgres`：比對資料庫裡存的 JSON）：既有只有繁中的欄位，必填改成繁中＋英文後，送同樣標籤只改「必填」→ 200。
  - `RequiredTextPolicyTests.UpdateField_ChangedLabel_RequiresNewLocale`（Review Focus 5，`Category=Postgres`）：改繁中標籤沒補英文 → 400；補了英文 → 200。
  - `RequiredTextPolicyTests.EnglishOnly_NewFieldWithoutZhTw_ResolvesToEnglish`：必填只有英文時，只給英文的新欄位存檔成功，`schema` 以 `th` 讀到英文標籤。
  - `AppFormat7Tests.OpenPackage_IgnoresRequiredTextPolicy`（Review Focus 5）：必填改繁中＋英文後，匯入並開啟只有繁中名稱的套件成功；套件缺繁中仍 400 `zh-TW 為必填`。
  - 既有名稱驗證測試不改斷言。
- [ ] **Step 2: 跑測試確認失敗**：`cd poc/backend && dotnet test tests/DynamicModules.DomainTests --filter "FullyQualifiedName~I18nText"`；`dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~RequiredTextPolicyTests|FullyQualifiedName~FieldServiceTests"`
- [ ] **Step 3: 實作**（先改 Domain 讓編譯錯誤列出所有 `IsValid(out` 呼叫端，逐一改；main 上是 13 處（含 `AppPackageValidator.cs:149`），a2d–a6 合併後以編譯錯誤為準；`grep -rn "IsValid(out" poc/backend/src` 最後應無結果）
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；另跑 ApplicationTests `--filter "FullyQualifiedName~Event|FullyQualifiedName~TaskRule|FullyQualifiedName~Approval|FullyQualifiedName~Conversion|FullyQualifiedName~Check|FullyQualifiedName~Department|FullyQualifiedName~View|FullyQualifiedName~ModuleService|FullyQualifiedName~Template"`；WebAPITests `--filter "FullyQualifiedName~AppFormat|FullyQualifiedName~AppPackageFormat|FullyQualifiedName~ApprovalApiTests"`；PostgreSQL：`FORMWRIGHT_TEST_DB=postgres dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~RequiredTextPolicyTests&Category!=SqliteOnly&Category!=PgDeferred&Category!=Benchmark"`
- [ ] **Step 5: Commit**（`feat(policy): 多語文字的必填語系改讀政策（A18）`）

### Task 9: 請求語系依啟用與預設語系

**Files:**
- Modify: `src/DynamicModules.WebAPI/Security/HeaderCurrentLocale.cs`、`src/DynamicModules.Application/Common/ICurrentLocale.cs`（`FallbackCurrentLocale` 改取快照）
- Test: `tests/DynamicModules.WebAPITests/LocaleApiTests.cs`（main 上已有的 X-Locale 測試類別，新案例加在這裡，不另開 `LocaleHeaderTests`）

**Interfaces:**
- Produces: `HeaderCurrentLocale(IHttpContextAccessor accessor, SystemPolicySnapshot snapshot)`：標頭值在 `snapshot.EnabledLocales` 內就用它，否則 `snapshot.DefaultLocale`。`FallbackCurrentLocale(SystemPolicySnapshot snapshot)`：`Locale => snapshot.DefaultLocale`。註解改「缺值、不認得或已停用時用預設語系（政策 locale.default）」。

- [ ] **Step 1: 寫失敗的測試**
  - `LocaleApiTests.DisabledLocaleHeader_FallsBackToDefault`：啟用 `["zh-TW","en-US"]`、預設 `en-US` → 帶 `X-Locale: th` 的錯誤訊息是英文；不帶標頭也是英文；帶 `zh-TW` 是中文。
  - 既有 X-Locale 測試不改（類別註解「缺值或不認得退回繁中」改成「退回預設語系，出廠繁中」）。
- [ ] **Step 2: 跑測試確認失敗**：`cd poc/backend && dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~LocaleApiTests"`
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；`--filter "FullyQualifiedName~Locale|FullyQualifiedName~MessageText"`
- [ ] **Step 5: Commit**（`feat(policy): 請求語系依啟用與預設語系政策`）

### Task 10: 前端語系改從 API 取

**Files:**
- Create: `src/i18n/locale-settings.ts`
- Modify: `src/test-setup.ts`（設定出廠語系）
- Modify: `src/i18n/locale.ts`（`DEFAULT_LOCALE` 改名 `FALLBACK_LOCALE`，註解寫「伺服器連不上時的翻譯包退回」；`readLocale()` 回 `LocaleCode | null`，沒記過回 null）、`src/boot/i18n.ts`（沒記過時先用 `FALLBACK_LOCALE`）
- Modify: `src/router/auth-guard.ts`（第一次導覽取 `getAuthOptions()` → `setLocaleSettings`，沒記過或記的已停用時 `applyLocale(default, …, false)`；登入後從 Task 5 的同一次 `getEffectivePolicies(null)` 設 `requiredText`）
- Modify: `src/presentation/locale/LocaleSwitcher.vue`（只列啟用）、`src/presentation/dynamic/designer/I18nTextInput.vue`、`src/domain/dynamic/i18n-text.ts`、`src/domain/dynamic/event-draft.ts:124`、`task-rule-draft.ts:119,138`、`check-draft.ts:48`、`src/presentation/dynamic/designer/OptionEditor.vue:64,72`、`src/presentation/dynamic/list/ViewMenu.vue:32-34`、`src/presentation/dynamic/print/PrintTab.vue:18,118`、`src/domain/dynamic/menu-nav.ts:24`
- Modify: `src/i18n/*.json`（`designer.zhRequired` → `designer.localeRequired`「{locale} 必填」四語）
- Test: `src/i18n/locale-settings.vitest.spec.ts`、`src/domain/dynamic/i18n-text` 既有 spec、`I18nTextInput` 既有 spec、`LocaleSwitcher` 既有 spec、三個 draft 既有 spec、`src/router/auth-guard.vitest.spec.ts`、`menu-nav.vitest.spec.ts`

**Interfaces:**
- Produces（`locale-settings.ts`）: `setLocaleSettings(s: { defaultLocale?: LocaleCode; enabled?: LocaleCode[]; requiredText?: LocaleCode[] }): void`；`defaultLocale(): LocaleCode`、`enabledLocales(): LocaleCode[]`、`requiredTextLocales(): LocaleCode[]`（沒設定過丟錯）。
- `i18n-text.ts`：`resolveI18n` 退回順序同後端（判斷 13）；`compactI18n` 去空後沒有任何值才回 undefined；新增 `hasRequiredText(text: I18nText | undefined): boolean`、`missingRequired(text): LocaleCode[]`。
- `I18nTextInput.vue`：必填語系各一個必填輸入框（`label` 為 `${label} (${l}) *`，規則訊息 `t('designer.localeRequired', { locale: l })`），其餘啟用語系放在「其他語系」展開區；停用語系不顯示但 `set()` 保留原值；`optional` 時全部不必填；`validate()` 驗所有必填框。`data-testid` 改成 `i18n-${l}`（`i18n-zh-TW` 保持可用）。
- 三個 draft 與 `OptionEditor` 的繁中必填改 `hasRequiredText`；錯誤鍵不變。`ViewMenu` 另存時名稱寫進每個必填語系與目前語系。`menu-nav` 分組鍵改「繁中 → 第一個有值的語系」，與 `resolveI18n` 的退回一致。
- `LedgerTab.vue` 的 `LOCALES` 不改（產生四語標籤，不是輸入）。
- Task 5 的 `useBusinessTimeWatch` 每次拿到生效值時一併 `setLocaleSettings({ requiredText })`（不提示）；啟用與預設語系來自 `auth/options`，已開著的頁面要重新整理才更新（停用語系的請求後端已退回預設語系，判斷 14）。〔技術〕理由：同一個查詢、不多打 API；必填語系前端只是提示，後端會再驗。

- [ ] **Step 1: 寫失敗的測試**
  - `locale-settings.vitest.spec.ts`：未設定時丟錯；設定後讀回。
  - `i18n-text` spec：`resolveI18n({ 'en-US': 'Order' }, 'th') === 'Order'`；`compactI18n({ 'en-US': ' Order ' })` 回 `{ 'en-US': 'Order' }`；`hasRequiredText` 依設定。
  - `I18nTextInput` spec：必填 `['zh-TW','en-US']` 時兩個必填框都有 `*`；啟用 `['zh-TW','en-US']` 時展開區沒有 `th`、`vi`；modelValue 帶 `th` 時改繁中後 emit 的物件仍有 `th`。
  - `LocaleSwitcher` spec：啟用 `['zh-TW','en-US']` 只列兩項。
  - 三個 draft spec：必填 `['en-US']` 時只有繁中名稱報 `nameRequired`、只有英文通過。
  - `auth-guard` spec：`沒記過語系時套用預設`：`getAuthOptions` 回 `defaultLocale: 'en-US'` → 導覽後 i18n locale 是 `en-US`、localStorage 沒寫入；`記過但已停用時改用預設`。
  - `menu-nav` spec：沒有繁中的分區以英文分組。
  - `use-business-time-watch` spec 加一案：生效值的 `locale.requiredText` 變成 `['zh-TW','en-US']` → `requiredTextLocales()` 跟著變，不呼叫 `Dialog.create`。
- [ ] **Step 2: 跑測試確認失敗**：`cd poc/frontend && bunx vitest run src/i18n src/domain/dynamic src/presentation/dynamic/designer src/presentation/locale src/router src/application/dynamic/queries --maxWorkers=2`
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；另跑 `bunx vitest run src/presentation src/pages --maxWorkers=2`；`bun run typecheck`；`grep -rn "\['zh-TW'\]" src --include='*.ts' --include='*.vue' | grep -v spec.ts` 只剩 `menu-nav.ts`、`i18n-text.ts` 的退回順序與註解說明過的位置
- [ ] **Step 5: Commit**（`feat(policy): 前端語系選單、多語輸入與必填語系改從 API 取（A18）`）

### Task 11: p3b 文件、實機與 gate；p3 收尾

**Files:**
- Modify: 設計文件：§10 補「繁中是多語文字的退回語系與套件格式的必填語系」「`SupportedLocales` 四種是平台已有翻譯的語系」「前端 `FALLBACK_LOCALE`」；§12 只加技術判斷 11、13、15、16、21（判斷 9、10、12、14 是使用者 2026-11-12 裁決，§3.7 與 A18 那列已寫，不重寫）；路線文件 p3 完成；功能清單 p3 打勾（跑腳本）；`docs/manual.md`（語言切換一章：只列啟用語系；設計器多語輸入：必填語系依政策；政策設定的「地區與語系」三項與改用順序）

- [ ] **Step 1: 實機**：四種寬度。依序把預設改英文 → 必填改英文 → 啟用只留繁中與英文：語言選單只剩兩種、登出後（清 localStorage）登入頁是英文、模組設計的欄位標籤輸入只要求英文、既有只有繁中的欄位改其他設定可存。再全部恢復出廠值（注意順序：先啟用 → 再必填 → 最後預設）。每一步四語畫面不露出鍵名。
- [ ] **Step 2: gate**（同 Task 6 Step 3，含 PostgreSQL 兩步）
- [ ] **Step 3: Commit**（`docs: 語系政策的手冊、可寫死清單與 p3 完成`）

---

## 自我檢查結果

1. **設計覆蓋**：A5→Task 1、2；A17＋A31（`TaipeiBusinessTime.cs`、`Program.cs:58`、`FieldImportParser.cs:99`、`ReportRules.cs:239`、`field-type-registry.ts:130`、`FilterValueInput.vue:61-64`，以及盤點外另找到的 `FieldDisplay`、`DateTimeHandler`、`TaskRuleEvaluator`、`ReportRunner.Cells`、`rule-engine.ts`、`DateSelect.vue`、`rule-state.ts`）→Task 1、3、4、5；A18→Task 7–10；§3.5 時區與必填語系→Task 4、5、8；§3.5 要求的改時區提示→Task 5；§10 更新→Task 6、11。
2. **步驟掃描**：測試步驟都有測試名與斷言值；程式步驟有簽章與出廠訊息原文。
3. **型別一致**：`PolicyKind.UtcOffset`／`'utcOffset'`、`UtcOffsetOf`／`FormatUtcOffset`、`SystemPolicySnapshot`（`BusinessOffset`、`ReloadGate`、`DefaultLocale`、`EnabledLocales`、`RequiredTextLocales`）、`SystemPolicySnapshotLoader.RefreshAsync`／`TryRefreshIfStaleAsync`、`SystemPolicySnapshotBehavior`、`IPolicyChangeHook.BeforeCommitAsync`、`ShiftDateTimeIndexAsync`、`RequiredTextGuard.Check`、前端 `setBusinessOffset`／`setLocaleSettings`／`useBusinessTimeWatch` 在使用處名稱一致。`IBusinessTime` 不加 `Offset`（判斷 19）。
4. **Review Focus**：五條都有指定測試與所屬 Task。
5. **比例**：只放簽章、測試名與斷言；Task 8 的呼叫端清單是為了讓實作者一次改齊，不是程式內容。
6. **2026-11-12 修訂**：使用者裁決 9 條都指到判斷與 Task（見開頭修訂紀錄第 1 點）；審查 p3-M1、S1–S5、N1–N4 各有對應 Task 步驟或測試；PostgreSQL 驗證寫在 Global Constraints 與 Task 3、4、6、7、8、11。

## 使用者裁決（2026-11-12 追加）

- U1：改時區後其他人畫面的提示不能關，只能按「重新整理」（照推薦）。
- U2：多台部署改時區只寫進文件與政策說明（離峰、先縮成一台再改），不做跨伺服器同步（照推薦）。
