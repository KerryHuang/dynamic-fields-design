# 政策設定 p2 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把設計與處理上限（A16、A19、A21–A30）從程式常數改成讀政策，出廠值等於現值，部署後沒人改設定時行為不變。

**Architecture:** 沿用 p1 的 `PolicyCatalog`（唯一寫出廠值與技術上下限的地方）、`IPolicyReader`（scoped、同一 request 快取）、§7 API 與兩個設定畫面。各處常數改成 handler 讀政策；靜態驗證（`AppPackageValidator`、`ReportRules`、`DashboardRules`、`CsvReader`）不碰 IO，改由呼叫端把值傳進去。明細列上限（A23）要在明細模組上覆寫，所以登記項多一個「適用哪種模組」的欄位，模組政策分頁對明細模組也顯示。

**Tech Stack:** .NET 8、EF Core（SQLite 研發、PostgreSQL 正式）、MediatR、xUnit＋FluentAssertions；Vue 3、Quasar 2、TanStack Query、vue-i18n、vitest、bun。不新增相依套件。

**Spec:** `docs/superpowers/specs/2026-11-11-policy-settings-design.md`（origin/main `df680b7a` 版，已含 2026-11-12 裁決：§3.5、§3.7 p2 細則、§4 A16、A19、A21–A30，§9 p2，§10 含套件 14 個數量上限）。PostgreSQL 驗證依 `docs/superpowers/specs/2026-11-12-postgres-provider-design.md` §6.3–§6.6。repo 規範：`AGENTS.md`、`.claude/rules/{backend,frontend,app-packages,docs}.md`、`docs/ai/testing.md`。p1 計畫：`../20261111-policy-settings-p1/plan.md`（介面以 main 上的實際程式為準）。

**路徑慣例：** 後端相對 `poc/backend/`，前端相對 `poc/frontend/`，文件相對 repo 根目錄。

---

## 修訂紀錄（2026-11-12）

依使用者 2026-11-12 裁決與 `plan-review.md` 修訂；逐條處置與理由見同資料夾 `revise-result.md`。

1. 使用者裁決寫進判斷與 Task（不再列待決）：
   - A25 欄數上限做成 S 政策、技術上限 4 → 判斷 6、Task 1、3。
   - A19 基本資料區塊一律建、只有「名稱」欄位可關 → 判斷 5、Task 4。
   - Q1 數量上限只在套件匯入與開啟時檢查、設計器不擋 → 判斷 2、Task 1 文案、Task 2。
   - Q2 調低欄數上限後既有區塊照原欄數 → 判斷 7、Task 3。
   - Q3 套件欄數超過上限自動夾到上限 → 判斷 8、Task 3 `Instantiate_PackageColumnsAboveMax_Clamped`。
   - Q4 政策管「每來源模組」事件數 → 判斷 12、Task 7。
   - Q5 其他 14 個套件數量常數寫死（設計 §10 已列）→ 判斷 4、Task 5。
   - Q6 報表查詢天數不超過 3650 → 判斷 13、Task 6、9。
   - 排程：p2 在 O2 之後、mcp1a 之前 → 前置條件 5–7。
2. p2-M1：模組政策 GET／PUT 改用 `includeChildren: true` 載入模組，`IsDetail` 才判得出明細模組 → Task 1 Interfaces、測試。
3. p2-S1：Review Focus 4 改成與 Task 3 規則一致。
4. p2-S2：刪掉「新增區塊對話框初值」「生效值未載入時新增區塊鈕停用」與對應 vitest → Task 3。
5. p2-S3：欄數選項改成 `1..max(上限, 區塊現值)`，補 vitest → Task 3。
6. p2-S4：效果檢查迴圈改成截在 `Math.Max(上限, 既有效果數)`，補大請求測試 → Task 7。
7. p2-S5：前端 `reports.drillTruncated` 四語改帶 `{n}`，補 vitest、grep 掃 i18n → Task 9。
8. p2-S6：`WriteJournalAndIdempotencyTests` 加進 Task 7 的 Files 與測試 filter。
9. p2-N1–N4：`PolicyDefinition` 簽章寫出 `Forbidden`（Task 1）；Task 2 新訊息放 `WorkflowMessages.cs`、`RuleMessages.cs`，Task 3 放 `FieldRules.cs`；`CheckLayout` 只留一種簽章（Task 9）；殘留掃描改用能抓多重宣告與 `decimal` 的 regex（Task 12）。
10. PostgreSQL 驗證（pg0 在 p2 之前上線）：Global Constraints 加「PostgreSQL 驗證」；動到資料庫查詢的新測試標 `Category=Postgres`；Task 1、8、9、11 的 Step 4 加 PostgreSQL 窄範圍指令；Task 5、10、12 的 gate 明寫含 PostgreSQL 兩步。
11. 核對 main（`67828aa4`）後的過時處：前置條件刪「分支落後 1c1ea8ae」；`CreateModuleHandler.cs:63`、`ModuleEditorPage.vue:100,108,391-399` 行號；p2c 的分類改用 O1 已建的維運分類（O1 未建才新增 `maintenance`）。

## 前置條件（開工前逐項確認，不符就停下回報）

1. **p1a–p1d 全部已合併**（main 已成立）。p2 用到：`PolicyCatalog`、`PolicyDefinition`、`IPolicyReader`、`PolicyEnvironmentDefaults`（p1a）；`policy-types.ts`、`useEffectivePolicies`、`PolicyValueEditor`、`PoliciesPage`、`ModulePoliciesTab`（p1b）；`UploadGuard.ApplyBodyLimit(HttpContext, long, int)`、縮小後的 `AttachmentHandlers.cs`（p1d Task 17–18）。
2. **p1b 已刪除前端 `task-rule-draft.ts` 的 `MAX_TASKS`、`MAX_RULES`**（main 已成立）：`grep -rn "MAX_TASKS\|MAX_RULES" poc/frontend/src/domain/dynamic/task-rule-draft.ts` 應無結果。
3. **`feat/form-columns` 已合併**（main 已成立）：`grep -n "DefaultColumns\|MaxColumns" poc/backend/src/DynamicModules.Domain/Entities/Dynamic/Block.cs` 有兩個常數，前端 `form-layout.ts` 有 `col-lg-3`。
4. **`.claude/rules/backend.md` 有 PostgreSQL 相容規則**（main 已成立）。
5. **排程（使用者 2026-11-12 確認）：p2 在 O2 之後、mcp1a 之前**。開工時 O1、un1、pg0（含 pg0a–pg0d）、pg1、簽核 a2d–a6、O2 都應已合併；任何一個沒合併就停下回報主控，不要先做。
6. **PostgreSQL 測試環境可用**：`FORMWRIGHT_TEST_DB=postgres dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~SeedDataTests&Category!=SqliteOnly&Category!=PgDeferred&Category!=Benchmark"` 通過（Docker 在跑，或設了 `FORMWRIGHT_TEST_PG`）；`scripts/unattended/gate.sh` 已含 PostgreSQL 兩步。
7. **rebase 到最新 main 後重新核對**：本計畫的行號以 main `67828aa4` 為準，O1、a2d–a6、O2 合併後會位移；`PolicyCatalog` 會多出 a2d（A3、A4、A10–A12、A15）、O1、O2 的政策；`UpdateSystemPolicyHandler` 會多出 O2 的稽核寫入。行號不符時以符號（類別、方法名）為準；介面與本計畫不同時停下回報。

## 子段切法

| 子段 | 內容 | Task | migration |
|---|---|---|---|
| p2a | 登記項加「適用模組」欄位；設計上限 A16、新模組名稱欄位 A19、表單欄數 A25 | 1–5 | 無 |
| p2b | 匯入 A21、事件 A22、明細列 A23、報表與儀表板 A24 | 6–10 | 無 |
| p2c | 維運上限 A26–A30 | 11–12 | 無 |

每個子段只把自己接上的政策加進 `PolicyCatalog`（p1 判斷 1 的做法），合併後畫面只出現已接上的政策。三段都不加 migration。

---

## 計畫期間的判斷

每條：判斷／理由／選錯代價。〔行為〕＝影響使用者看到或能做的事；〔技術〕＝實作選擇。標「使用者 2026-11-12 確認」的已拍板（設計 §3.7、§10），實作時不要再問。

1. 〔技術〕**登記項加 `PolicyModuleTarget Target`（`Main`、`Detail`、`Any`，預設 `Main`）**，模組政策 GET 只列適用該模組種類的項目，PUT 不適用的回 400；模組設計頁「政策」分頁對明細模組也顯示（推翻 p1 判斷 20 的隱藏條件）。理由：A23 明細列上限覆寫要設在明細模組上，而 p1 的任務、附件政策對明細模組無效。代價：p1 既有政策全部是 `Main`，不必改；若之後有政策要同時適用兩種，用 `Any`。
2. 〔行為〕**A16 只在現在有檢查的地方生效**：狀態、轉換數在流程設計存檔與開啟套件；規則數在規則設計存檔與套件；群組、模組、欄位、選項、檢視、整合數**只在匯入與開啟套件**（設計器目前不限這幾種數量）。理由：上線後行為不變；替設計器新加數量檢查是新功能。代價：管理員把「每模組欄位數」調成 50，設計器仍可加第 51 個欄位；政策說明文字要寫明「匯入與開啟應用程式套件時檢查」。**使用者 2026-11-12 確認**：只在套件匯入與開啟時檢查，設計器不擋（Q1）。
3. 〔技術〕**A16 的套件數量檢查搬出靜態 `Validate`**，比照 p1 判斷 12：新增 `AppPackageValidator.DesignLimitErrors(AppPackageData, DesignLimits)`，在匯入、開啟、主檔合併三處呼叫；`AppCatalog` 啟動時不再檢查這幾個數量。主檔合併多一處檢查是新增的（p1 的 A2 不必，因為主檔不能帶任務規則），出廠值下結果與現在相同。代價：內建套件啟動時不驗這些數量（內建套件都遠低於出廠值）；管理員調低後，超過的內建套件在開啟時被擋（同 A2 已接受的代價）。
4. 〔技術〕**其他套件數量常數不在 A16，列進 §10 技術限制**：事件 100、檢查 100、轉換 50、報表 50、列印範本 100、欄位權限與選項範圍各 2000、`requires.apps` 10、`requires.entities` 20、整合夥伴 5、整合欄位 20、主檔 5、貢獻 20、貢獻欄位 50、事件 set 對應 50（`EventValidator.MaxSets`）。理由：它們保護套件解析，和 1MB、JSON 深度 32 同類。**使用者 2026-11-12 確認**寫死（Q5），設計 §10 已列「應用程式套件的 14 個數量上限與連動事件每套件總數 100」；Task 5 只補具體常數名。
5. 〔行為〕**A19 只控制「名稱」欄位**；「基本資料」區塊一律建立。理由：模組至少要有一個區塊是既有完整性守衛（`DeletePlanner` 的 `LastBlock`、`TemplateValidator` 的「至少要有一個區塊」、`TemplateInstantiator` 把第一個區塊對到預設區塊）。**使用者 2026-11-12 確認**（設計 §3.7、A19 已改字面）。關掉時，模組要先自己加欄位、指定標題欄位才能啟用（`ActivateModule` 既有檢查）。
6. 〔行為〕**A25 做兩個政策**：`form.defaultColumns`（M，出廠 4，範圍 1–4，`Target = Any`）、`form.maxColumns`（S，出廠 4，範圍 1–4）。技術上限 4 只寫在 `PolicyCatalog`（前端 `fieldColClass` 的斷點只排到 lg 四欄，兩邊用註解互指，並用測試釘住）。**使用者 2026-11-12 確認**上限是 S 政策、技術上限 4。代價：管理員可以把上限調到 1–3；不能超過 4。
7. 〔行為〕**調低 `form.maxColumns` 只管之後的設定**：設計器的欄數選項到 `max(上限, 該區塊現值)`（現值超過上限時仍看得到並可改小）；更新區塊時欄數超過上限且比原本多才擋；既有區塊照原欄數顯示，不在表單上夾成上限。理由：§3.5「改嚴只管之後的新增」。**使用者 2026-11-12 確認**（Q2）。代價：管理員調低上限後，既有表單看起來沒變；要立刻變窄得逐區塊改。
8. 〔行為〕**新區塊實際欄數＝`min(該模組的 form.defaultColumns, form.maxColumns)`，只由後端決定**（新增區塊對話框本來就沒有欄數選項，`AddBlockRequest` 也不帶欄數）；開啟套件時，套件寫的欄數超過上限就夾到上限、不報錯；套件沒寫欄數時用新區塊的欄數。**使用者 2026-11-12 確認**（Q3）。代價：套件作者指定 4 欄，在上限 2 的系統開出來是 2 欄。
9. 〔技術〕**刪除 `Block.DefaultColumns`、`Block.MaxColumns` 與 `Block.Columns` 的屬性初值**；兩個建立區塊的入口（`CreateModuleHandler`、`AddBlockHandler`）一律明確給值。`BlockConfiguration` 的 `HasDefaultValue(2)` 不動（改要 migration；值永遠由程式給，碰不到），列進 §10。`TemplateBlock.Columns` 改成 `int?`。代價：之後若有第三個建立區塊的入口忘了給值，資料庫會填 2；Task 3 的測試釘住兩個入口。
10. 〔技術〕**匯入檔案大小以 byte 存（同 p1 判斷 13）**，技術範圍 1024–10485760；匯入端點的 `[RequestSizeLimit(2MB)]` 改成 resource filter，用 `UploadGuard.ApplyBodyLimit(http, maxBytes, files: 2)`（＝2 倍上限＋64KB）。理由：請求上限要跟著政策，否則調高到 2MB 以上會被框架 413。代價：出廠值下請求上限從 2,097,152 變 2,162,688 byte；介於兩者之間的請求原本回 413，之後回 400「檔案不能超過 1 MB」（訊息更清楚，視為可接受）。
11. 〔技術〕**事件串聯層數、單次寫入筆數、逐列筆數在每次請求開頭讀一次政策**；`EffectBudget.TryUse()` 改成 `TryUseAsync`。代價：無行為差異；每個有事件的請求多一次（已快取的）政策讀取。
12. 〔行為〕**A22 的「事件數」＝每個來源模組的事件數（`MaxEventsPerModule`）**，不是套件的事件總數 100（後者列 §10）。更新既有事件不檢查事件數；效果數只在比原本多時檢查。理由：§3.5。**使用者 2026-11-12 確認**（Q4）。代價：無。
13. 〔行為〕**A24 查詢天數的技術上限定為 3650**，等於出廠值：只能調低。理由：同一個「近 N 天」在列表與規則條件由 Domain `ConditionParser.MaxDays = 3650` 檢查（設計 §10 B10 類），報表若可調高會和列表不一致。**使用者 2026-11-12 確認**（Q6）。代價：管理員不能把報表查詢天數調到 10 年以上。
14. 〔行為〕**儀表板卡片數、明細列數、流程狀態與轉換數、規則數只在「這次比原本多」時檢查**（§3.5）。代價：無。
15. 〔技術〕**A27 孤兒附件清掃：`graceMinutes` 查詢參數改成可省略**，省略時用政策；明確傳值（含 0）照舊。理由：清掃 API 給維運腳本用，傳 0 是既有用法。代價：無。
16. 〔技術〕**新分類**：`design`、`form`、`import`、`event`、`record`、`report`、`check`；A26、A27 放 O1 已建的「維運」分類（O1 設計 §10.6；開工時 `grep -n "Category\|\"ops" poc/backend/src/DynamicModules.Domain/Policies/PolicyCatalog.cs` 確認代碼，沒有才新增 `maintenance` 系統維護）。理由：設計 §8 的「設計上限、匯入、事件、報表」，加上 p2 補盤的表單、檢查；維運類不開第二個近義分類。代價：純文案。政策代碼 `maintenance.*` 不跟著改（代碼與分類是兩個欄位）。
17. 〔技術〕**前端 `dashboard-layout.ts` 的 `MAX_CARDS` 直接刪除**：目前沒有任何程式引用（只有定義），留著就是鐵則 2 的重複常數。代價：無。
18. 〔技術〕**明細列上限的訊息改成帶參數**：`MessageText.TooManyDetailRows` 四語都改成 `{0}`，出廠值時文字與現在相同。
19. 〔技術〕**效果檢查迴圈截在 `Math.Max(上限, 既有效果數)`**（審查 p2-S4）：兩者都不超過技術上限 50（舊上限 10、`PolicyCatalog.EventMaxEffects.Max` 50），帶上萬個效果的請求只報數量錯、不逐一查模組與欄位。代價：超量請求只看得到前面效果的個別錯誤（與現在相同）。
20. 〔技術〕**前端下鑽截斷訊息的列數取回應的列數**（審查 p2-S5）：被截斷時回應列數＝生效的 `report.drillLimit`，`DrillDialog`、`CardDrillDialog` 用 `t('reports.drillTruncated', { n: rows.length })`，不必另查生效值。代價：無。
21. 〔技術〕**模組政策 API 載入模組時帶欄位**（審查 p2-M1）：`ModuleRelations.IsDetail` 看「啟用中的明細關聯欄位」，`includeChildren: false` 永遠判成一般模組；GET、PUT 兩個 handler 改 `includeChildren: true`。代價：每次讀寫模組政策多載入區塊與欄位（設計器頁本來就載入，量小）。

---

## Global Constraints

- 出廠值＝現值，部署後沒人改設定時行為不變；既有測試的預期值不改（只改呼叫簽章）。
- 兩層：全系統預設＋模組覆寫；只有 `Scope = Module` 的能覆寫。調全系統值不影響已存在的覆寫。
- 改嚴只管之後的新增；編輯既有資料只在「這次增加數量」時檢查（§3.5）。
- 值的讀取一律經 `IPolicyReader`；Domain 與靜態驗證不碰 IO，由呼叫端傳值。不寫 `const`／`static readonly` 的業務上限；技術上下限只寫在 `PolicyCatalog`。
- **PostgreSQL 相容**：不寫 SQLite 專屬 SQL（raw SQL 走 `ISqlDialect`、不用 `strftime`、`PRAGMA`、`INDEXED BY`、不 catch `SqliteException` 做業務判斷）；DateTime 的 `Kind` 明確。p2 預期不寫任何 raw SQL。
- **PostgreSQL 驗證**（pg0 設計 §6.3–§6.5；使用者裁決「之後的功能兩種資料庫都驗」）：
  - 新測試裡經資料庫查詢判斷政策結果的，類別或方法標 `[Trait("Category", "Postgres")]` 進雙跑集：Task 1 的兩條 `PolicyApiTests`（模組載入）、Task 8 的 `DetailSaveTests` 政策案例與 `Module_Put_DetailPolicyOnMainModule_400`、Task 9 的 `ReportsApiTests` 政策案例（報表 SQL 路徑；pg1 已合併，若沒有就標 `PgDeferred` 並寫原因）、Task 11 的 `Purge_FollowsRetentionPolicy`、`Sweep_NoGrace_UsesPolicy`、`Backfill_RecordPolicy`。純函式、靜態驗證、前端測試不標。
  - 每個標了 `Postgres` 的 Task，Step 4 另跑一次 PostgreSQL 窄範圍：`FORMWRIGHT_TEST_DB=postgres dotnet test <專案> --filter "<該 Task 的 filter>&Category!=SqliteOnly&Category!=PgDeferred&Category!=Benchmark"`；Docker 沒在跑時先啟動，起不來就停下回報，不跳過。
  - 子段收尾的 gate 已含 PostgreSQL 兩步（pg0 §6.5），`exit=0` 才算過。
- **migration**：p2 三個子段都不加。
- 後端使用者看得到的訊息放各功能的 `*Messages.cs`（或既有的 `MessageText` 四語表）；文字在出廠值時與現在逐字相同。
- 前端新字串四語（`zh-TW`、`en-US`、`th`、`vi`）鍵集合一致、單行 JSON；政策文案放 `policies.items.<代碼>.name/help`、分類放 `policies.categories.<分類>`；含 `{`、`}`、`@`、`|` 用字面量寫法。
- 有畫面的改動在 390、820、1366、1920 四種寬度不橫捲；輸入框有 `label` 或 `aria-label`。
- 測試只跑窄範圍；後端、前端、gate 不並行；vitest 一律 `--maxWorkers=2`。
- git：逐檔 `git add`；commit 前 `git status`；不用 `--no-verify`、不 force push、不 merge commit。公開 repo：不寫公司字串、個人絕對路徑、內網 IP。
- 每個子段收尾跑 `bash scripts/unattended/gate.sh <worktree 絕對路徑>`，並用 `formwright-purpose-check` skill 對照鐵則。

**政策登記（p2 全部；各子段只加自己那幾列）**

| 代碼 | 段 | 分類 | 型別 | 範圍 | 目標 | 出廠 | 技術範圍 | 單位 | 取代的常數 |
|---|---|---|---|---|---|---|---|---|---|
| `design.maxWorkflowStates` | p2a | design | Integer | System | — | `30` | 1–100 | count | `SaveWorkflowHandler.MaxStates` |
| `design.maxWorkflowTransitions` | p2a | design | Integer | System | — | `200` | 1–1000 | count | `SaveWorkflowHandler.MaxTransitions` |
| `design.maxRulesPerModule` | p2a | design | Integer | System | — | `100` | 1–500 | count | `SaveRulesHandler.MaxRules`、`AppPackageValidator.MaxRulesPerModule`、前端 `RulesTab.vue` `MAX_RULES` |
| `design.maxGroupsPerPackage` | p2a | design | Integer | System | — | `20` | 1–100 | count | `AppPackageValidator.MaxGroups` |
| `design.maxModulesPerPackage` | p2a | design | Integer | System | — | `30` | 1–100 | count | `AppPackageValidator.MaxModules`（模組數與 workflows 數） |
| `design.maxFieldsPerModule` | p2a | design | Integer | System | — | `300` | 1–1000 | count | `AppPackageValidator.MaxFieldsPerModule` |
| `design.maxOptionsPerField` | p2a | design | Integer | System | — | `200` | 1–1000 | count | `AppPackageValidator.MaxOptionsPerField`（含 V6 貢獻欄位） |
| `design.maxViewsPerModule` | p2a | design | Integer | System | — | `20` | 1–100 | count | `AppPackageValidator.MaxViewsPerModule` |
| `design.maxIntegrationsPerPackage` | p2a | design | Integer | System | — | `20` | 1–100 | count | `AppPackageValidator.MaxIntegrations` |
| `module.createTitleField` | p2a | design | Boolean | System | — | `true` | — | — | `CreateModuleRequest.CreateTitleField = true` |
| `form.defaultColumns` | p2a | form | Integer | Module | Any | `4` | 1–4 | count | `Block.DefaultColumns`、前端 `ModuleEditorPage.vue` 初值 |
| `form.maxColumns` | p2a | form | Integer | System | — | `4` | 1–4 | count | `Block.MaxColumns`、前端欄數選項 `[1, 2, 3, 4]` |
| `import.maxRows` | p2b | import | Integer | System | — | `1000` | 1–10000 | count | `CsvReader.MaxRows` |
| `import.maxFileBytes` | p2b | import | Integer | System | — | `1048576` | 1024–10485760 | bytes | `ImportPlanner.MaxBytes`、`ImportExportController` 的 2MB |
| `event.maxEventsPerModule` | p2b | event | Integer | System | — | `20` | 1–100 | count | `EventValidator.MaxEventsPerModule` |
| `event.maxEffects` | p2b | event | Integer | System | — | `10` | 1–50 | count | `EventValidator.MaxEffects`、前端 `event-draft.ts` `MAX_EFFECTS` |
| `event.maxDepth` | p2b | event | Integer | System | — | `5` | 1–10 | count | `EventStage.MaxDepth` |
| `event.maxWritesPerRequest` | p2b | event | Integer | System | — | `500` | 1–5000 | count | `EffectBudget.Max` |
| `event.maxEachRows` | p2b | event | Integer | System | — | `200` | 1–1000 | count | `EffectApplier.MaxEachRows` |
| `record.maxDetailRows` | p2b | record | Integer | Module | Detail | `200` | 1–1000 | count | `DetailWriter.MaxRows`、`MessageText.TooManyDetailRows` 的 200 |
| `report.drillLimit` | p2b | report | Integer | System | — | `500` | 1–5000 | count | `ReportRules.DrillLimit` |
| `report.detailLimit` | p2b | report | Integer | System | — | `1000` | 1–10000 | count | `ReportRules.DetailLimit` |
| `report.maxRangeDays` | p2b | report | Integer | System | — | `3650` | 1–3650 | days | `ReportRules.MaxDays` |
| `dashboard.maxCards` | p2b | report | Integer | System | — | `40` | 1–100 | count | `DashboardRules.MaxCards`、前端 `dashboard-layout.ts` `MAX_CARDS` |
| `maintenance.idempotencyRetentionDays` | p2c | O1 維運分類（判斷 16） | Integer | System | — | `7` | 1–90 | days | `IdempotencyBehavior.RetentionDays` |
| `maintenance.orphanSweepGraceMinutes` | p2c | O1 維運分類（判斷 16） | Integer | System | — | `60` | 1–1440 | minutes | `AttachmentsController.Sweep` 的 `graceMinutes = 60` |
| `check.scanMaxViolations` | p2c | check | Integer | System | — | `50` | 1–500 | count | `ScanCheckHandler.MaxViolations` |
| `check.scanMaxGroups` | p2c | check | Integer | System | — | `20000` | 1000–200000 | count | `ScanCheckHandler.MaxGroups` |
| `check.maxGroupsPerWrite` | p2c | check | Integer | System | — | `500` | 1–5000 | count | `CheckStage.MaxGroups` |
| `check.maxBackfillRecords` | p2c | check | Integer | System | — | `200000` | 1000–1000000 | count | `CheckIndexer.MaxRecords` |

（目標欄「—」＝`Main`，System 範圍用不到。）

## Review Focus

1. **調低上限後編輯既有資料**：已有 120 條規則的模組在上限 100 時要能改其中一條；已有 250 列明細的單據在上限 200 時改表頭要能存、加第 251 列要擋；已有 45 張卡片的儀表板在上限 40 時拖動位置要能存。→ Task 2 `SaveRules_LoweredLimit_EditWithoutIncreaseAllowed`；Task 8 `DetailRows_LoweredLimit_EditHeaderAllowed_AddRowRejected`；Task 9 `Layout_LoweredMaxCards_MoveAllowed_AddRejected`。
2. **明細模組的覆寫寫在哪**：覆寫設在明細模組才生效，設在表頭模組不影響；對一般模組 PUT `record.maxDetailRows` 回 400、對明細模組 PUT `task.maxRulesPerModule` 回 400；明細模組要真的被判成明細（handler 有載入欄位，p2-M1）。→ Task 1 `Module_Get_DetailModule_ListsOnlyApplicable`、`Module_Put_TargetMismatch_400`；Task 8 `DetailRows_UsesDetailModuleOverride`、`Module_Put_DetailPolicyOnMainModule_400`。
3. **套件與政策**：管理員把模組數上限調到 2 後，匯入 3 個模組的套件回 400 且訊息文字與現在同形；已匯入的套件在開啟時同樣被擋並完整回滾；內建套件在啟動時不因政策被拒。→ Task 2 `Import_OverModuleLimit_400`、`Open_LimitLoweredAfterImport_Rejected`、`Catalog_StartupIgnoresDesignLimitPolicies`。
4. **表單欄數上下限互動**：模組覆寫預設 4、全系統上限 2 → 新區塊是 2 欄；套件寫 4 欄、上限 2 → 開出來 2 欄不報錯；既有 4 欄區塊改標題可存、改成 3 欄也可存（變少）；2 欄區塊改 3 欄被擋；編輯框打得開 4 欄區塊且看得到現值。→ Task 3 `AddBlock_DefaultAboveMax_UsesMax`、`Instantiate_PackageColumnsAboveMax_Clamped`、`UpdateBlock_ExistingAboveMax_RenameAllowed_IncreaseRejected`、vitest `既有欄數超過上限時仍顯示現值`。
5. **匯入檔案大小與請求上限一致**：政策調到 3MB 後，2.5MB 的 CSV 走到 handler（不是框架 413）；調到 512KB 後，600KB 回 400「檔案不能超過 0.5 MB」，10MB 的請求在讀主體前就 413。→ Task 6 `Import_RaisedFileLimit_AcceptsLargerBody`、`Import_LoweredFileLimit_400WithFormattedSize`、`Import_HugeBody_413BeforeReading`。

---

# p2a 設計上限、新模組名稱欄位、表單欄數

### Task 1: 登記項加「適用模組」與 p2a 政策登記

**Files:**
- Modify: `src/DynamicModules.Domain/Policies/PolicyDefinition.cs`（record 尾端加參數；新 enum）
- Modify: `src/DynamicModules.Domain/Policies/PolicyCatalog.cs`（加 p2a 12 項）
- Modify: `poc/shared/policy-catalog.json`（codes 加 12 個；categories 加 `design`、`form`）
- Modify: `src/DynamicModules.Application/Dynamic/PolicyService/PolicyHandlers.cs`（模組 GET 過濾、模組 PUT 檢查目標）、`PolicyMessages.cs`（`NotForThisModule`）
- Modify: `poc/frontend/src/pages/system/dynamic-modules/ModuleEditorPage.vue`（政策分頁拿掉 `v-if="!mod?.detailOf"`）
- Modify: `poc/frontend/src/i18n/{zh-TW,en-US,th,vi}.json`（`policies.categories.design/form`、12 項 `policies.items.*`）
- Test: `tests/DynamicModules.DomainTests/Policies/PolicyCatalogFixtureTests.cs`、`tests/DynamicModules.WebAPITests/Dynamic/PolicyApiTests.cs`、`poc/frontend/src/pages/system/dynamic-modules/ModuleEditorPage.vitest.spec.ts`

**Interfaces:**
- Produces（`namespace DynamicModules.Domain.Policies`）:
  - `public enum PolicyModuleTarget { Main, Detail, Any }`
  - `PolicyDefinition(..., string? Unit = null, IReadOnlyList<string>? Forbidden = null, PolicyModuleTarget Target = PolicyModuleTarget.Main)`：`Target` 加在 p1d 的 `Forbidden` **之後**，不要插在 `Unit` 後面（會打壞既有的具名引數呼叫）；`public bool AppliesTo(bool isDetailModule) => Target == Any || (Target == Detail) == isDetailModule;`
  - `PolicyCatalog`：`DesignMaxWorkflowStates`、`DesignMaxWorkflowTransitions`、`DesignMaxRulesPerModule`、`DesignMaxGroupsPerPackage`、`DesignMaxModulesPerPackage`、`DesignMaxFieldsPerModule`、`DesignMaxOptionsPerField`、`DesignMaxViewsPerModule`、`DesignMaxIntegrationsPerPackage`、`ModuleCreateTitleField`、`FormDefaultColumns`、`FormMaxColumns`（值照 Global Constraints 表）。
  - `PolicyMessages.NotForThisModule = "這個政策不適用於這種模組"`。
- `GetModulePoliciesHandler`、`UpdateModulePolicyHandler` 載入模組改成 `modules.GetByKeyAsync(key, includeChildren: true, ct)`（main 上兩處都是 `false`；`ModuleRelations.IsDetail` 要看區塊裡的明細關聯欄位，沒載入就永遠是 false，審查 p2-M1）。`GetEffectivePoliciesHandler` 只用 `module.Id`，維持 `false`。
- 模組 GET `modules/{key}/policies` 只列 `Scope == Module && AppliesTo(ModuleRelations.IsDetail(module))`；模組 PUT 在 `SystemOnly` 檢查之後加 `!AppliesTo(ModuleRelations.IsDetail(module))` → 400（鍵 `code`）。
- 文案（zh-TW，name／help）：
  - `design.maxWorkflowStates` 流程狀態數上限／儲存流程或開啟應用程式套件時檢查；已有的流程改其他設定不受影響。
  - `design.maxWorkflowTransitions` 流程轉換數上限／同上。
  - `design.maxRulesPerModule` 每個模組的欄位規則數上限／設計規則與匯入、開啟套件時檢查；既有規則照常可改。
  - `design.maxGroupsPerPackage` 套件內群組數上限／匯入與開啟應用程式套件時檢查。
  - `design.maxModulesPerPackage` 套件內模組數上限／匯入與開啟應用程式套件時檢查。
  - `design.maxFieldsPerModule` 套件內每個模組的欄位數上限／匯入與開啟應用程式套件時檢查；在設計器加欄位不受這個限制。
  - `design.maxOptionsPerField` 套件內每個欄位的選項數上限／同上。
  - `design.maxViewsPerModule` 套件內每個模組的檢視數上限／匯入與開啟應用程式套件時檢查。
  - `design.maxIntegrationsPerPackage` 套件內整合數上限／匯入與開啟應用程式套件時檢查。
  - `module.createTitleField` 新模組自動建立「名稱」欄位／關閉後，新模組只有「基本資料」區塊，要自己加欄位並指定標題欄位才能啟用。
  - `form.defaultColumns` 新區塊的每列欄數／超過「每列欄數上限」時用上限；既有區塊不變。
  - `form.maxColumns` 每列欄數上限／設計區塊時最多能選幾欄；調低後既有區塊照原欄數顯示，但不能再調高。
  - 分類：`design` 設計上限、`form` 表單。

- [ ] **Step 1: 寫失敗的測試**
  - `PolicyCatalogFixtureTests.Factories_EqualCurrentBehavior` 加 p2a 12 項出廠值；`FormDefaultColumns.Target == PolicyModuleTarget.Any`；`FormMaxColumns.Max == 4`（註解：前端 `form-layout.ts` 的 `fieldColClass` 斷點只排到 4 欄，兩邊一起改）。
  - `PolicyDefinitionTests.AppliesTo`：`Main` 對一般 true、明細 false；`Detail` 相反；`Any` 都 true。
  - `PolicyApiTests.Module_Get_DetailModule_ListsOnlyApplicable`（`Category=Postgres`）：明細模組用真的明細關聯欄位建立（`Relation = Detail` 的參照欄位，沿用 `DetailSaveTests` 或 `RecordsApiTests` 建明細模組的 helper；不要只設前端的 `detailOf`）；它的清單含 `form.defaultColumns`、不含 `task.maxRulesPerModule`；一般模組含兩者。這條在 handler 仍是 `includeChildren: false` 時必須失敗（Step 2 確認）。
  - `PolicyApiTests.Module_Put_TargetMismatch_400`（`Category=Postgres`）：對上面的明細模組 PUT `task.maxRulesPerModule` `{ value: 3 }` → 400，`errors.code[0] == "這個政策不適用於這種模組"`；對一般模組同樣 PUT → 200。
  - `ModuleEditorPage.vitest.spec.ts`：把 p1b 的 `明細模組沒有政策分頁` 改成 `明細模組也有政策分頁`（`detailOf` 有值時仍有 `tab-policies`）。
- [ ] **Step 2: 跑測試確認失敗**：`cd poc/backend && dotnet test tests/DynamicModules.DomainTests --filter "FullyQualifiedName~Policies"`；`dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~PolicyApiTests"`。Expected：編譯失敗或新測試 FAIL。
- [ ] **Step 3: 實作登記、目標過濾、訊息、四語文案、分頁條件**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；PostgreSQL：`FORMWRIGHT_TEST_DB=postgres dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~PolicyApiTests&Category!=SqliteOnly&Category!=PgDeferred&Category!=Benchmark"`；`cd poc/frontend && bunx vitest run src/domain/dynamic/policy-i18n.vitest.spec.ts src/i18n src/pages/system/dynamic-modules/ModuleEditorPage.vitest.spec.ts --maxWorkers=2`；`bun run typecheck`。
- [ ] **Step 5: Commit**（`feat(policy): 登記項加適用模組種類，登記設計上限與表單政策`）

> 注意：本 Task 只登記；Task 2–4 接線前，畫面上已出現這 12 項但程式還讀常數。p2a 的 Task 1–5 必須一起合併（同 p1a Task 5、6 的做法），不要單獨 cherry-pick。

### Task 2: A16 設計上限接上政策

**Files:**
- Modify: `src/DynamicModules.Application/Dynamic/WorkflowService/Commands/SaveWorkflow/SaveWorkflowHandler.cs:20-21,44-52`
- Modify: `src/DynamicModules.Application/Dynamic/RuleService/Commands/SaveRules/SaveRulesHandler.cs:19,28`
- Modify: `src/DynamicModules.Application/Dynamic/AppService/AppPackageValidator.cs:22-24,87,110,120,129-136`、`AppPackageValidator.V5.cs:14,35`、`AppPackageValidator.V6.cs:116`
- Create: `src/DynamicModules.Application/Dynamic/AppService/PackageLimitReader.cs`
- Create: `src/DynamicModules.Application/Dynamic/WorkflowService/WorkflowMessages.cs`、`src/DynamicModules.Application/Dynamic/RuleService/RuleMessages.cs`（`.claude/rules/backend.md`：訊息放同功能的 `*Messages.cs`，這兩個服務目前沒有訊息檔）
- Modify: `src/DynamicModules.Application/Dynamic/AppService/Commands/ImportAppPackage.cs:44-46,90`（改呼叫 `PackageLimitReader`）、`AppOpenPlanner.cs:59-61`（同上）、`src/DynamicModules.Application/DependencyInjection.cs`
- Modify: `poc/frontend/src/presentation/dynamic/rules/RulesTab.vue:16,125-128`、`poc/frontend/src/i18n/*.json`（`designer.rules.limit` 改「每個模組最多 {max} 條規則」四語都帶 `{max}`）
- Test: `tests/DynamicModules.ApplicationTests/Dynamic/DesignLimitPolicyTests.cs`（新）、`tests/DynamicModules.ApplicationTests/Dynamic/AppTaskRulePackageTests.cs` 旁新增 `AppDesignLimitPackageTests.cs`、`tests/DynamicModules.WebAPITests/Dynamic/AppFormat7Tests.cs`（加兩個 Fact）、`poc/frontend/src/presentation/dynamic/rules/RulesTab.vitest.spec.ts`

**Interfaces:**
- Consumes: Task 1 的 9 個 `Design*` 定義；`IPolicyReader.IntAsync(def, null, ct)`。
- Produces:
  - `public sealed record DesignLimits(int MaxGroups, int MaxModules, int MaxFieldsPerModule, int MaxOptionsPerField, int MaxRulesPerModule, int MaxViewsPerModule, int MaxIntegrations);`（`AppPackageValidator.cs`）
  - `public static IReadOnlyList<string> DesignLimitErrors(AppPackageData p, DesignLimits l)`：訊息文字與現在逐字相同（`groups: 最多 {n} 個`、`modules: 最多 {n} 個`、`workflows: 最多 {n} 個`、`{at}: 欄位最多 {n} 個`、`{at}: 規則最多 {n} 條`、`{at}: 檢視最多 {n} 個`、`{at}.{f.Code}: 選項最多 {n} 個`、V6 `{fat}: 選項最多 {n} 個`、`integrations: 最多 {n} 個`）；null 項略過（結構層已報錯）。
  - 結構層（`Structure`、`IntegrationStructure`、`Format6Structure`）刪掉上述九種數量檢查；常數 `MaxGroups`、`MaxModules`、`MaxFieldsPerModule`、`MaxOptionsPerField`、`MaxRulesPerModule`、`MaxViewsPerModule`、`MaxIntegrations` 刪除；其餘常數留著並加註解「技術限制，見設計 §10」。
  - `public sealed class PackageLimitReader(IPolicyReader policies)`：`Task<IReadOnlyList<string>> ErrorsAsync(AppPackageData p, CancellationToken ct)` ＝ `TaskRuleLimitErrors`（p1a）＋`DesignLimitErrors`，全部用全系統值。註冊 scoped。
  - `ImportAppPackageHandler`：第 44 行改 `await limits.ErrorsAsync(package, ct)`；主檔合併 `MergeAsync` 在 `Validate(merged, others)` 之後也呼叫，錯誤加前綴「合併後的定義：」。`AppOpenPlanner` 第 60 行同樣改呼叫。
  - `WorkflowMessages`：`States(int max) => $"狀態必須是狀態欄位啟用中的選項，不重複，1 到 {max} 個"`、`Transitions(int max)`（照 `SaveWorkflowHandler` 現有轉換數訊息原文，把 200 換成 `{max}`）。`RuleMessages`：`TooManyRules(int max) => $"規則最多 {max} 條"`。handler 不再內插字串。
  - `SaveWorkflowHandler`：狀態數 `count is 0` 一律擋；`count > maxStates && count > 原本狀態數` 才擋，訊息 `WorkflowMessages.States(maxStates)`；轉換數同理。原本數量取 `module.Workflow`（沒有流程時為 0）。
  - `SaveRulesHandler`：`input.Count > max && input.Count > module.Rules.Count` 才擋，訊息 `RuleMessages.TooManyRules(max)`。
  - 前端 `RulesTab.vue`：`useEffectivePolicies(moduleKey)`；新增鈕在生效值未載入或 `draft.length >= max` 時停用；提示 `t('designer.rules.limit', { max })`；刪 `MAX_RULES`。

- [ ] **Step 1: 寫失敗的測試**
  - `DesignLimitPolicyTests.SaveRules_UsesPolicy`：全系統 `design.maxRulesPerModule` 2 → 存 3 條 400，`Errors["rules"][0] == "規則最多 2 條"`。
  - `DesignLimitPolicyTests.SaveRules_LoweredLimit_EditWithoutIncreaseAllowed`（Review Focus 1）：先存 3 條，上限改 2 → 同樣 3 條改一條的條件可存；改成 4 條 400。
  - `DesignLimitPolicyTests.SaveWorkflow_LoweredStateLimit_EditAllowed`：3 個狀態的流程，上限改 2 → 改轉換可存；加第 4 個狀態 400，訊息含「1 到 2 個」。
  - `AppDesignLimitPackageTests.DesignLimitErrors_FactoryValues_MatchOldMessages`：31 個模組的套件 `DesignLimitErrors(p, Factory)` 含 `"modules: 最多 30 個"`；靜態 `Validate` 不再含它。每種數量各一個 `[InlineData]`，`Factory` 用 `PolicyCatalog` 的出廠值組。
  - `AppDesignLimitPackageTests.Catalog_StartupIgnoresDesignLimitPolicies`（Review Focus 3）：內建套件 `AppPackageValidator.Validate` 全部無錯（既有 `AppCatalogChecks` 照常過，這條確認刪掉的檢查沒有搬回靜態層）。
  - `AppFormat7Tests.Import_OverModuleLimit_400`（Review Focus 3）：admin PUT `system/policies/design.maxModulesPerPackage` `{ value: 1 }` 後匯入 `Crm7()`（多個模組）→ 400、含 `"modules: 最多 1 個"`；改回 null 後匯入成功。
  - `AppFormat7Tests.Open_LimitLoweredAfterImport_Rejected`：先匯入，再把 `design.maxFieldsPerModule` 設 1，開啟回 4xx 且含「欄位最多 1 個」，`App` 與模組都不存在。
  - `RulesTab.vitest.spec.ts`：`上限依生效值`：`design.maxRulesPerModule: 2`、已有 2 條 → 新增鈕停用、提示含 `每個模組最多 2 條規則`；`生效值還沒回來時新增鈕停用`。
- [ ] **Step 2: 跑測試確認失敗**：`cd poc/backend && dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~DesignLimitPolicyTests|FullyQualifiedName~AppDesignLimitPackageTests"`；`dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~AppFormat7Tests"`；`cd poc/frontend && bunx vitest run src/presentation/dynamic/rules --maxWorkers=2`。Expected：編譯失敗或 FAIL。
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；另跑 `dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~AppCatalogChecks|FullyQualifiedName~WorkflowTests|FullyQualifiedName~AppTaskRulePackageTests"`、`dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~AppFormat6Tests|FullyQualifiedName~AppPackageFormatTests|FullyQualifiedName~SharedMaster"`；`bunx vitest run src/i18n --maxWorkers=2`；`bun run typecheck`；`grep -rn "MaxGroups\b\|MaxModules\b\|MaxFieldsPerModule\|MaxOptionsPerField\|MaxViewsPerModule\|MaxIntegrations\b" src/DynamicModules.Application/Dynamic/AppService` 只剩 `DesignLimits` 的屬性名。
- [ ] **Step 5: Commit**（`feat(policy): 設計上限改讀政策（A16）`）

### Task 3: A25 表單區塊欄數

**Files:**
- Modify: `src/DynamicModules.Domain/Entities/Dynamic/Block.cs`（刪 `DefaultColumns`、`MaxColumns`、`Columns` 初值）
- Modify: `src/DynamicModules.Application/Dynamic/FieldService/Commands/AddBlock/AddBlockHandler.cs`、`UpdateBlock/UpdateBlockValidator.cs`、`UpdateBlock/UpdateBlockHandler.cs`
- Modify: `src/DynamicModules.Application/Dynamic/ModuleService/Commands/CreateModule/CreateModuleHandler.cs`
- Modify: `src/DynamicModules.Application/Dynamic/TemplateService/Data/TemplatePackage.cs:39-40`、`TemplateInstantiator.cs:87-88`、`TemplateValidator.cs:94`
- Modify: `src/DynamicModules.Application/Dynamic/FieldService/Data/FieldRules.cs`（欄數訊息；FieldService 的訊息常數都在這裡）
- Create: `src/DynamicModules.Application/Dynamic/FieldService/BlockColumns.cs`
- Modify: `poc/frontend/src/pages/system/dynamic-modules/ModuleEditorPage.vue:100,108,395-399`（main 行號：`blockForm` 初值、`openBlock`、欄數 `q-btn-toggle`）、`poc/frontend/src/presentation/dynamic/form-layout.ts`（只加註解）
- Test: `tests/DynamicModules.ApplicationTests/Dynamic/FieldServiceTests.cs`、`tests/DynamicModules.ApplicationTests/Dynamic/TemplateValidatorTests.cs`、`tests/DynamicModules.WebAPITests/Dynamic/TemplateExportTests.cs`、`poc/frontend/src/pages/system/dynamic-modules/ModuleEditorPage.vitest.spec.ts`、`poc/frontend/src/presentation/dynamic/form-layout.vitest.spec.ts`

**Interfaces:**
- Consumes: `PolicyCatalog.FormDefaultColumns`、`FormMaxColumns`。
- Produces:
  - `public sealed class BlockColumns(IPolicyReader policies)`（scoped）：`Task<int> ForNewBlockAsync(long? moduleId, CancellationToken ct)` ＝ `min(IntAsync(FormDefaultColumns, moduleId), IntAsync(FormMaxColumns, null))`；`Task<int> MaxAsync(CancellationToken ct)`。
  - `AddBlockHandler` 建區塊時 `Columns = await blockColumns.ForNewBlockAsync(module.Id, ct)`；`CreateModuleHandler` 建區塊時模組還沒有 Id，也不可能已有覆寫，傳 `null`（取全系統值）。
  - `UpdateBlockValidator`：`Columns` 範圍改成 `1..PolicyCatalog.FormMaxColumns.Max`（技術範圍，訊息 `$"每列欄數必須是 1 到 {Max}"`）。`UpdateBlockHandler`：`request.Columns is { } c && c > max && c > block.Columns` → 400（鍵 `Columns`，`FieldRules.TooManyColumns(int max) => $"每列欄數最多 {max}"`）。規則：增加且超過上限才擋；不變或減少一律放行（4 欄改 3 欄在上限 2 時也可存）。
  - `TemplateBlock(..., int? Columns = null)`；`TemplateValidator`：`b.Columns is < 1 or > PolicyCatalog.FormMaxColumns.Max` 才報錯（null 合法）。`TemplateInstantiator`：取得剛建的區塊欄數 `created`（AddBlock 回應的 `BlockResponse.Columns`；第一個區塊讀 `GetByKeyAsync` 的那個區塊）；`want = Math.Min(b.Columns ?? created, max)`；`i > 0 && !b.Collapsed && want == created` 才略過 `UpdateBlockRequest`。
  - 前端 `ModuleEditorPage.vue`：`const policies = useEffectivePolicies(moduleKey)`；編輯區塊對話框的欄數選項 `1..Math.max(values['form.maxColumns'], blockForm.columns)`（現值超過上限時仍列出現值，讓使用者看得到並能改小；審查 p2-S3）；生效值還沒回來時選項用 `1..blockForm.columns`（只顯示到現值，不擋開啟對話框）。新增區塊的流程不動：對話框本來就不顯示欄數（`v-if="blockForm.id !== undefined"`）、`addBlock` 只送 `label`，新區塊欄數只由後端 `BlockColumns.ForNewBlockAsync` 決定；`blockForm` 的 `columns: 4` 初值只是新增模式的佔位，不送出（審查 p2-S2）。「新增區塊」鈕不因生效值未載入而停用。`form-layout.ts` 的 `fieldColClass` 加註解「斷點只排到 4 欄，對應 PolicyCatalog.FormMaxColumns 的技術上限」。

- [ ] **Step 1: 寫失敗的測試**
  - `FieldServiceTests.AddBlock_UsesModuleDefaultColumns`：模組覆寫 `form.defaultColumns` 2 → 新區塊 `Columns == 2`；另一模組 4。
  - `FieldServiceTests.AddBlock_DefaultAboveMax_UsesMax`（Review Focus 4）：覆寫預設 4、全系統上限 2 → 2。
  - `FieldServiceTests.UpdateBlock_ExistingAboveMax_RenameAllowed_IncreaseRejected`（Review Focus 4）：4 欄區塊，上限改 2 → 改標題（`Columns` 4 不變）成功；同一區塊改成 3 欄成功（變少）；建一個 2 欄區塊改成 3 → 400、`Errors["Columns"][0] == "每列欄數最多 2"`。
  - `FieldServiceTests.CreateModule_FirstBlockUsesPolicy`：全系統預設 3 → 新模組的「基本資料」區塊 3 欄。
  - `TemplateValidatorTests`：`Columns = null` 無錯；`5` 有錯「1 到 4」。
  - `TemplateExportTests.Instantiate_PackageColumnsAboveMax_Clamped`（Review Focus 4）：上限 2，套件區塊寫 4 → 開出來 2，沒有錯誤；套件沒寫欄數、預設 3 上限 4 → 3。既有的 form-columns 測試（欄數 round trip）照常過。
  - `ModuleEditorPage.vitest.spec.ts`：
    - `欄數選項依上限`：`form.maxColumns: 3`、編輯 2 欄區塊 → `block-columns` 只有 1、2、3。
    - `既有欄數超過上限時仍顯示現值`（審查 p2-S3）：`form.maxColumns: 2`、編輯 4 欄區塊 → 選項是 1–4、目前選中 4。
    - `新增區塊不送欄數`：按「新增區塊」→ 對話框沒有 `block-columns`，`dynamicApi.addBlock` 只收到 `(moduleKey, label)`；生效值 mock 成未回應時「新增區塊」鈕仍可按。
  - `form-layout.vitest.spec.ts`：`supports every column count up to the policy hard cap`：1–4 各回不同 class（既有斷言保留）。
- [ ] **Step 2: 跑測試確認失敗**：`cd poc/backend && dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~FieldServiceTests|FullyQualifiedName~TemplateValidatorTests"`；`dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~TemplateExportTests"`；`cd poc/frontend && bunx vitest run src/pages/system/dynamic-modules/ModuleEditorPage.vitest.spec.ts src/presentation/dynamic/form-layout.vitest.spec.ts --maxWorkers=2`
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；另跑 `--filter "FullyQualifiedName~AppCatalogChecks|FullyQualifiedName~SeedDataTests|FullyQualifiedName~ModuleServiceTests"`；`bun run typecheck`；`grep -rn "DefaultColumns\|MaxColumns" poc/backend/src` 只剩 `PolicyCatalog`。
- [ ] **Step 5: Commit**（`feat(policy): 區塊欄數預設與上限改讀政策（A25）`）

### Task 4: A19 新模組是否自動建「名稱」欄位

**Files:**
- Modify: `src/DynamicModules.Application/Dynamic/ModuleService/Commands/CreateModule/CreateModuleRequest.cs:11`、`CreateModuleHandler.cs:63`
- Modify: `src/DynamicModules.Application/Dynamic/TemplateService/TemplateInstantiator.cs:68`（仍明確傳 false，不動語意，只確認型別）
- Test: `tests/DynamicModules.ApplicationTests/Dynamic/ModuleServiceTests.cs`

**Interfaces:**
- Produces: `CreateModuleRequest(..., bool? CreateTitleField = null)`；註解改「null＝依政策 module.createTitleField；範本建立固定傳 false」。Handler：`var create = request.CreateTitleField ?? await policies.BoolAsync(PolicyCatalog.ModuleCreateTitleField, null, ct);`；「基本資料」區塊一律建立（計畫判斷 5）。

- [ ] **Step 1: 寫失敗的測試**
  - `ModuleServiceTests.CreateModule_TitleFieldPolicyOff_OnlyBlock`：全系統 `module.createTitleField` false → 新模組有一個區塊、沒有欄位、`TitleFieldId` 為 null；`ActivateModuleRequest` 回 400「啟用前必須指定記錄標題欄位」。
  - 既有建立模組測試（有「名稱」欄位、是標題欄位）不改斷言。
- [ ] **Step 2: 跑測試確認失敗**：`cd poc/backend && dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~ModuleServiceTests"`
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；另跑 `--filter "FullyQualifiedName~Template|FullyQualifiedName~AppOpen"`（範本路徑仍不建名稱欄位）
- [ ] **Step 5: Commit**（`feat(policy): 新模組是否建名稱欄位改讀政策（A19）`）

### Task 5: p2a 文件、實機與 gate

**Files:**
- Modify: `docs/superpowers/specs/2026-11-11-policy-settings-design.md`：§10 技術限制補 p2a 各項範圍（照 §10 既有「p1a 已登記」的寫法加「p2a 已登記」）、`BlockConfiguration` 的 `HasDefaultValue(2)`、「基本資料」與「名稱」的預設文字；§10 已有的「應用程式套件的 14 個數量上限」那條補上常數名（`AppPackageValidator.MaxConversions` 等，見判斷 4），不重寫裁決；§12「主控判斷彙整」只加技術判斷 1、3、9、21。判斷 2、5、6、7、8 是使用者 2026-11-12 裁決，§3.7 與 A19、A25 那列已寫，不再列進 §12
- Modify: `docs/superpowers/specs/2026-11-06-erp-roadmap-design.md`（政策設定工作段表加 p2a）、`docs/feature-checklist.md`（p2 改 🚧，跑清單腳本）、`docs/manual.md`（設計器規則數上限、區塊欄數、新模組名稱欄位改成「依政策設定，出廠 …」；政策設定一節加「設計上限」「表單」兩類）

- [ ] **Step 1: 實機**（`scripts/unattended/local-demo.sh`；`formwright-demo-verify` skill）：390、820、1366、1920 四種寬度開 `/system/policies`（設計上限、表單兩類各改一個、恢復出廠值一次）、一般模組與明細模組的「政策」分頁（明細模組列出 `form.defaultColumns`、不列任務規則政策）、模組設計的「編輯區塊」對話框（上限改 3 後 2 欄區塊只剩 1–3；4 欄區塊仍看得到 4 並可改小）、新增區塊後的欄數（預設 4、上限 3 → 3 欄）、規則分頁的上限提示。不得橫向捲動；四語切換各看一次分類與政策名稱沒有缺字或露出鍵名。截圖放 repo 外 `.tmp/`。
- [ ] **Step 2: gate**：`bash scripts/unattended/gate.sh <worktree 絕對路徑>`，每步 `exit=0`（含 PostgreSQL 兩步；Docker 起不來就停下回報，不跳過）
- [ ] **Step 3: Commit**（`docs: 設計上限與表單政策的手冊、可寫死清單與 p2a 進度`）

---

# p2b 匯入、事件、明細列、報表與儀表板

### Task 6: A21 匯入筆數與檔案大小

**Files:**
- Modify: `src/DynamicModules.Domain/Policies/PolicyCatalog.cs`（p2b 全部 12 項一次登記，見 Global Constraints 表）、`poc/shared/policy-catalog.json`（codes＋categories `import`、`event`、`record`、`report`）、`poc/frontend/src/i18n/*.json`（12 項文案＋4 個分類）
- Modify: `src/DynamicModules.Application/Dynamic/ImportService/CsvReader.cs:13,15,78`、`ImportPlanner.cs:28,46-48`
- Create: `src/DynamicModules.Application/Dynamic/ImportService/ImportMessages.cs`
- Create: `src/DynamicModules.WebAPI/Controllers/v3/Dynamic/ImportBodyLimitFilter.cs`
- Modify: `src/DynamicModules.WebAPI/Controllers/v3/Dynamic/ImportExportController.cs:25-30`
- Test: `tests/DynamicModules.ApplicationTests/Dynamic/CsvReaderTests.cs`、`ImportTests.cs`、`tests/DynamicModules.WebAPITests/Dynamic/ImportApiTests.cs`、`tests/DynamicModules.DomainTests/Policies/PolicyCatalogFixtureTests.cs`

**Interfaces:**
- Produces:
  - `CsvReader.Read(byte[] content, int maxRows)`；超過時 `Fail(ImportMessages.TooManyRows(maxRows))`。
  - `ImportMessages`：`TooManyRows(int max) => $"一次最多匯入 {max} 筆"`；`FileTooLarge(long maxBytes) => $"檔案不能超過 {(maxBytes / 1048576m).ToString("0.##", CultureInfo.InvariantCulture)} MB"`（1048576 → `1 MB`、524288 → `0.5 MB`）。
  - `ImportPlanner` 注入 `IPolicyReader`，`content.Length > maxBytes` 回 `ImportMessages.FileTooLarge`；`CsvReader.Read(content, maxRows)`。
  - `ImportBodyLimitFilter(IPolicyReader policies) : IAsyncResourceFilter`：讀 `import.maxFileBytes`，`UploadGuard.ApplyBodyLimit(context.HttpContext, maxBytes, files: 2)` 回 413 就直接回；否則 `next()`。Controller 拿掉 `[RequestSizeLimit]`、`[RequestFormLimits]`，加 `[TypeFilter(typeof(ImportBodyLimitFilter))]`；XML 註解改寫成「請求上限＝兩倍檔案上限＋multipart 開銷，讓略超過的檔案拿到欄位錯誤而不是 413」。
- 文案（zh-TW）：
  - `import.maxRows` 一次匯入的筆數上限／超過時整個檔案不匯入，請分批。
  - `import.maxFileBytes` 匯入檔案大小上限（位元組）／1048576 位元組是 1 MB。
  - `event.maxEventsPerModule` 每個來源模組的連動事件數上限／超過時不能再新增事件；既有事件照常可改。
  - `event.maxEffects` 每個事件的效果數上限／編輯既有事件時，只有增加效果數才檢查。
  - `event.maxDepth` 連動事件串聯層數上限／事件寫入的記錄再觸發事件算一層；超過時整筆存檔取消。
  - `event.maxWritesPerRequest` 一次存檔中連動事件最多寫入幾筆／超過時整筆存檔取消。
  - `event.maxEachRows` 逐列產生記錄時的明細列上限／來源明細超過這個列數時，事件不產生記錄並取消存檔。
  - `record.maxDetailRows` 每張單據的明細列上限／在明細模組設定；已超過的單據照常可改，只是不能再加列。
  - `report.drillLimit` 報表下鑽最多顯示幾筆／超過時只顯示前面這些筆，可用匯出看全部。
  - `report.detailLimit` 報表明細最多顯示幾列／同上。
  - `report.maxRangeDays` 報表日期區間與「近 N 天」最多幾天／只能比 3650 小。
  - `dashboard.maxCards` 每個儀表板的卡片數上限／已超過的儀表板照常可調整版面，只是不能再加卡片。
  - 分類：`import` 匯入、`event` 連動事件、`record` 單據、`report` 報表與儀表板。

- [ ] **Step 1: 寫失敗的測試**
  - `PolicyCatalogFixtureTests.Factories_EqualCurrentBehavior` 加 p2b 12 項；`RecordMaxDetailRows.Target == Detail`；`ReportMaxRangeDays.Max == 3650`（註解：等於 `ConditionParser.MaxDays`，見計畫判斷 13）。
  - `CsvReaderTests.Read_RowLimitFromCaller`：3 列資料、`maxRows: 2` → `Error == "一次最多匯入 2 筆"`；既有測試改傳 1000、斷言不變。
  - `ImportTests.Import_RowPolicy`：全系統 `import.maxRows` 2 → 3 列的 CSV 400、`errors.file[0] == "一次最多匯入 2 筆"`。
  - `ImportApiTests.Import_LoweredFileLimit_400WithFormattedSize`（Review Focus 5）：`import.maxFileBytes` 524288 → 600KB 檔 400、訊息 `檔案不能超過 0.5 MB`。
  - `ImportApiTests.Import_RaisedFileLimit_AcceptsLargerBody`（Review Focus 5）：3145728 → 2.5MB 合法 CSV 預覽 200（不是 413）。
  - `ImportApiTests.Import_HugeBody_413BeforeReading`：出廠值下送 Content-Length 10MB → 413，比照 `AttachmentApiTests` 的 `UploadProbe` 寫法斷言主體沒被讀完。
  - 既有「檔案不能超過 1 MB」「一次最多匯入 1000 筆」測試不改斷言。
- [ ] **Step 2: 跑測試確認失敗**：`cd poc/backend && dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~CsvReaderTests|FullyQualifiedName~ImportTests"`；`dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~ImportApiTests"`
- [ ] **Step 3: 實作登記、四語文案、`CsvReader`、`ImportPlanner`、filter**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；`dotnet test tests/DynamicModules.DomainTests --filter "FullyQualifiedName~Policies"`；`cd poc/frontend && bunx vitest run src/domain/dynamic/policy-i18n.vitest.spec.ts src/i18n --maxWorkers=2`
- [ ] **Step 5: Commit**（`feat(policy): 登記處理上限政策，匯入筆數與檔案大小改讀政策（A21）`）

> 同 Task 1 的提醒：Task 6 一次登記 p2b 全部 12 項，Task 7–9 接線前畫面已出現，p2b 要整段一起合併。

### Task 7: A22 連動事件上限

**Files:**
- Modify: `src/DynamicModules.Application/Dynamic/EventService/EventValidator.cs:22,51-53,64-65`
- Modify: `src/DynamicModules.Application/Dynamic/EventService/EventStage.cs:23-24,64`
- Modify: `src/DynamicModules.Application/Dynamic/EventService/EffectWriter.cs:13-20,30,36,43`（`EffectBudget` 在同檔）
- Modify: `src/DynamicModules.Application/Dynamic/EventService/EffectApplier.cs:25,68`
- Create: `src/DynamicModules.Application/Dynamic/EventService/EventMessages.cs`
- Modify: `poc/frontend/src/domain/dynamic/event-draft.ts:43`、`poc/frontend/src/presentation/dynamic/events/EventEditorDialog.vue:11,157,160`
- Modify（既有測試，會因 `EffectBudget.Max` 刪除與 `TryUse` 改 async 而編譯失敗）: `tests/DynamicModules.ApplicationTests/Dynamic/WriteJournalAndIdempotencyTests.cs:53-61`（`EffectBudget_Reset_gives_the_full_allowance_again`）
- Test: `tests/DynamicModules.ApplicationTests/Dynamic/EventDefinitionTests.cs`、`EventRuntimeTests.cs`、`WriteJournalAndIdempotencyTests.cs`、`poc/frontend/src/presentation/dynamic/events/EventEditorDialog.vitest.spec.ts`

**Interfaces:**
- Produces:
  - `EventMessages`：`TooManyEffects(int max) => $"效果至少 1 個、至多 {max} 個"`、`TooManyEvents(int max) => $"每個來源模組至多 {max} 個事件"`、`TooDeep(int max) => $"連動事件串聯過深（上限 {max} 層）"`、`TooManyWrites(int max) => $"連動事件寫入的記錄超過 {max} 筆，請縮小範圍"`、`TooManyEachRows(int max) => $"明細列超過 {max} 列，無法產生連動記錄"`。`EventStage.TooDeep`、`EffectWriter.TooMany` 常數刪除，引用處改用這裡。`EventStage.Loop` 不動。
  - `EventValidator` 注入 `IPolicyReader`；`existing = (await events.ListBySourceAsync(source.Id, ct)).FirstOrDefault(e => e.Key == input.Key)`；效果數 `0` 一律擋，`count > max && count > existingCount` 才擋（`existingCount = existing?.Definition.Effects.Count ?? 0`）；逐一檢查效果的迴圈（main `EventValidator.cs:53`）改成 `i < def.Effects.Count && i < Math.Max(max, existingCount)`，不要拿掉截斷（審查 p2-S4：每個效果都會查模組與欄位，帶上萬個效果的請求不能逐一跑；兩個值都不超過技術上限 50）。事件數只在 `existing is null` 時檢查 `others >= max`。`MaxSets = 50` 留著加註解「技術限制，設計 §10」。
  - `EventStage` 注入 `IPolicyReader`，`RunAsync` 開頭讀一次 `maxDepth`。
  - `EffectBudget(IPolicyReader policies)`：`public async Task<bool> TryUseAsync(CancellationToken ct)`（第一次呼叫時讀 `event.maxWritesPerRequest` 存起來）；`Reset()` 只歸零計數。`EffectWriter.CreateAsync`／`UpdateAsync` 改 `await budget.TryUseAsync(ct)`。
  - `EffectApplier.CreateAsync` 讀 `event.maxEachRows`。
  - 前端：刪 `MAX_EFFECTS`；`EventEditorDialog` 用 `useEffectivePolicies(null)` 的 `event.maxEffects`，新增效果鈕在未載入或已達上限時停用，`effectsHint` 帶生效值。

- [ ] **Step 1: 寫失敗的測試**
  - `EventDefinitionTests.Effects_UsesPolicy_AndEditWithoutIncreaseAllowed`：上限設 2 → 新事件 3 個效果 400（訊息 `效果至少 1 個、至多 2 個`）；先以出廠值建 3 個效果的事件，再設 2 → 改名稱可存，加到 4 個 400。
  - `EventDefinitionTests.Effects_HugeRequest_ChecksAtMostCap`（審查 p2-S4）：出廠上限 10、新事件帶 1000 個效果（每個都指向不存在的模組）→ 400，`errors["definition.effects"][0] == "效果至少 1 個、至多 10 個"`；個別效果的錯誤鍵（`definition.effects[i].…`）只出現 `i` 為 0 到 9，沒有 `definition.effects[10]` 開頭的鍵。
  - `EventDefinitionTests.EventsPerModule_LoweredLimit_UpdateAllowed`：同來源已有 3 個事件，上限改 2 → 更新其中一個成功，新增第 4 個 400。
  - `WriteJournalAndIdempotencyTests.EffectBudget_Reset_gives_the_full_allowance_again` 改寫（審查 p2-S6）：從測試的 DI 容器取 scoped `EffectBudget`（或用 `PolicyReader` 測試替身建構），上限讀 `PolicyCatalog.EventMaxWritesPerRequest.Factory`（500）；`await budget.TryUseAsync(ct)` 500 次 true、第 501 次 false；`Reset()` 後再 true。斷言語意不變。
  - `EventRuntimeTests.Depth_UsesPolicy`：`event.maxDepth` 1 → 兩層串聯的寫入 409、訊息 `連動事件串聯過深（上限 1 層）`；出廠值時同一組資料成功。
  - `EventRuntimeTests.WriteBudget_UsesPolicy`：`event.maxWritesPerRequest` 1 → 一次觸發寫兩筆 409、訊息含「超過 1 筆」。
  - `EventRuntimeTests.EachRows_UsesPolicy`：`event.maxEachRows` 2 → 3 列明細的逐列事件 409、訊息含「超過 2 列」。
  - 既有 `EventRuntimeTests`、`EventDefinitionHardeningTests` 不改斷言。
  - `EventEditorDialog.vitest.spec.ts`：`效果數上限依生效值`：`event.maxEffects: 2` 且已有 2 個 → `event-add-effect` 停用、提示含 `最多 2 個`。
- [ ] **Step 2: 跑測試確認失敗**：`cd poc/backend && dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~EventDefinitionTests|FullyQualifiedName~EventRuntimeTests|FullyQualifiedName~WriteJournalAndIdempotencyTests"`；`cd poc/frontend && bunx vitest run src/presentation/dynamic/events --maxWorkers=2`
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；另跑 `--filter "FullyQualifiedName~Event|FullyQualifiedName~Demo|FullyQualifiedName~WriteJournal"`（Demo 產生器呼叫 `budget.Reset()`）；WebAPITests `--filter "FullyQualifiedName~EffectsRuntimeApiTests"`（雙跑集，gate 會在 PostgreSQL 上再跑）；`bun run typecheck`
- [ ] **Step 5: Commit**（`feat(policy): 連動事件上限改讀政策（A22）`）

### Task 8: A23 明細列上限

**Files:**
- Modify: `src/DynamicModules.Application/Dynamic/RecordService/DetailWriter.cs:46,85-86`
- Modify: `src/DynamicModules.Application/Common/MessageText.cs:128`（四語改 `{0}`）
- Modify（既有測試）: `poc/frontend/src/presentation/dynamic/details/detail-rows.vitest.spec.ts:58`（訊息仍是 `明細最多 200 列`，不改；確認沒有其他地方拼這個字串）
- Test: `tests/DynamicModules.ApplicationTests/Dynamic/DetailSaveTests.cs`

**Interfaces:**
- Produces: `DetailWriter` 注入 `IPolicyReader`；每個 `child` 讀 `IntAsync(PolicyCatalog.RecordMaxDetailRows, child.Id)`；`after > max && after > existing.Count` 才報錯，訊息 `MessageText.Get(locale.Locale, MessageText.TooManyDetailRows, max)`。`MessageText` 四語：`明細最多 {0} 列`、`At most {0} detail rows`、`รายละเอียดได้ไม่เกิน {0} แถว`、`Tối đa {0} dòng chi tiết`。

- [ ] **Step 1: 寫失敗的測試**
  - 下列三條 `DetailSaveTests` 與 `PolicyApiTests` 的新案例標 `Category=Postgres`。
  - `PolicyApiTests.Module_Put_DetailPolicyOnMainModule_400`（Review Focus 2）：對一般模組 PUT `record.maxDetailRows` `{ value: 2 }` → 400，`errors.code[0] == "這個政策不適用於這種模組"`；對明細模組（真的明細關聯欄位）同樣 PUT → 200；一般模組的 GET 清單不含 `record.maxDetailRows`，明細模組的含。
  - `DetailSaveTests.DetailRows_UsesDetailModuleOverride`（Review Focus 2）：明細模組覆寫 `record.maxDetailRows` 2 → 新增 3 列的單據 400，`errors["details.<key>"][0] == "明細最多 2 列"`；表頭模組不能設這項覆寫（PUT 回 400，見上一條），所以「表頭的值不影響」以全系統值 1 驗證：全系統設 1、明細模組覆寫 2 → 2 列可存、3 列 400。
  - `DetailSaveTests.DetailRows_LoweredLimit_EditHeaderAllowed_AddRowRejected`（Review Focus 1）：先存 3 列，覆寫改 2 → 只改表頭欄位成功；改其中一列成功；加第 4 列 400；刪到 2 列成功後再加 1 列 400（3 > 2 且 3 > 2）。
  - 既有 `明細最多 200 列` 的測試不改斷言。
- [ ] **Step 2: 跑測試確認失敗**：`cd poc/backend && dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~DetailSaveTests"`；`dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~PolicyApiTests"`
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；另跑 `--filter "FullyQualifiedName~Detail|FullyQualifiedName~MessageText"`；`dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~Detail"`；PostgreSQL：`FORMWRIGHT_TEST_DB=postgres dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~DetailSaveTests&Category!=SqliteOnly&Category!=PgDeferred&Category!=Benchmark"`、WebAPITests 同法跑 `PolicyApiTests`
- [ ] **Step 5: Commit**（`feat(policy): 明細列上限改讀政策（A23）`）

### Task 9: A24 報表與儀表板上限

**Files:**
- Modify: `src/DynamicModules.Application/Dynamic/ReportService/Data/ReportRules.cs:17-20,34-36,44-49,196-205`（`Check` 與 `NormalizeValue` 的簽章）
- Create: `src/DynamicModules.Application/Dynamic/ReportService/ReportLimits.cs`
- Modify: `src/DynamicModules.Application/Dynamic/ReportService/ReportDefinitionValidator.cs:24`、`Queries/RunReport/RunReportHandler.cs:15-19`、`Queries/DrillReport/DrillReportHandler.cs:14-20`、`Queries/PreviewReport/PreviewReportHandler.cs:14`
- Modify: `src/DynamicModules.Application/Dynamic/DashboardService/Data/DashboardRules.cs:10,14-17,38`、`DashboardCardRunner.cs:21,45,78-80`、`Queries/AdminPreviewCard/AdminPreviewCardHandler.cs:13`、`Commands/{CreateDashboard,AdminCreateDashboard,SaveDashboardLayout,AdminUpdateDashboard}/*Handler.cs`
- Modify: `poc/frontend/src/domain/dynamic/dashboard-layout.ts:6`（刪 `MAX_CARDS`）
- Modify: `poc/frontend/src/i18n/{zh-TW,en-US,th,vi}.json` 的 `reports.drillTruncated`（四語改帶 `{n}`）、`poc/frontend/src/presentation/reports/DrillDialog.vue`、`poc/frontend/src/presentation/dashboards/CardDrillDialog.vue`（審查 p2-S5）
- Test: `tests/DynamicModules.WebAPITests/ReportsApiTests.cs`、`tests/DynamicModules.WebAPITests/DashboardCardsApiTests.cs`、`tests/DynamicModules.ApplicationTests/Dynamic/ReportRunnerTests.cs`、`DrillDialog`／`CardDrillDialog` 的 vitest（既有就加案例，沒有就新增）

**Interfaces:**
- Produces:
  - `public sealed record ReportLimits(int DrillLimit, int DetailLimit, int MaxRangeDays);`＋`public sealed class ReportLimitReader(IPolicyReader policies)`：`Task<ReportLimits> GetAsync(CancellationToken ct)`（scoped，註冊在 Application DI）。
  - `ReportRules`：刪 `DrillLimit`、`DetailLimit`、`MaxDays`（`MaxListItems`、`MaxTextLength` 留，技術限制）；`CheckRange(ReportDateRange? r, int maxDays)`；`CapDetail(ReportResult r, int detailLimit)`；`Check(definition, set, registry, userIds, int maxDays)` 一路傳到 `NormalizeValue`。訊息文字不變（`日期區間的 N 必須介於 1 到 {maxDays}`）。
  - `DashboardRules.CheckLayout(DashboardLayout layout, ReportLimits limits, int maxCards, int existingCount = 0)`（唯一簽章；`limits` 給 `CheckOptions` 裡的 `CheckRange` 用 `MaxRangeDays`）：`layout.Cards.Count > maxCards && layout.Cards.Count > existingCount` 才報 `$"卡片最多 {maxCards} 張"`。新增類 handler 傳 0；更新類（`SaveDashboardLayoutHandler`、`AdminUpdateDashboardHandler`）傳既有版面的卡片數；預覽卡片傳 0。`Columns`、`MaxHeight`、`MaxTitleLength` 留（技術）。
  - `DrillReportHandler`、`DashboardCardRunner`：`rows.Count > limits.DrillLimit`。
  - 前端 `reports.drillTruncated` 四語：zh-TW `明細太多，只顯示前 {n} 列`，en-US、th、vi 照現有譯文把 500 換成 `{n}`；`DrillDialog.vue`、`CardDrillDialog.vue` 改成 `t('reports.drillTruncated', { n: rows.length })`（被截斷時回應列數就是生效的下鑽上限，判斷 20）。

- [ ] **Step 1: 寫失敗的測試**
  - `ReportRunnerTests.CapDetail_UsesGivenLimit`：1500 列、`detailLimit: 1000` → 1000 列且 `DetailTruncated`；`detailLimit: 2000` → 1500 列不截。
  - 下列三條 `ReportsApiTests` 標 `Category=Postgres`（走報表 SQL 路徑；若 pg1 還沒合併，改標 `PgDeferred` 並在 trait 旁寫「等 pg1」）。
  - `ReportsApiTests.Run_DetailLimitPolicy`：`report.detailLimit` 2 → 3 列明細的報表回 2 列、`detailTruncated: true`。
  - `ReportsApiTests.Drill_DrillLimitPolicy`：`report.drillLimit` 1 → 下鑽 2 筆回 1 筆、`truncated: true`。
  - `ReportsApiTests.Run_RangeDaysPolicy`：`report.maxRangeDays` 30 → `range` `{ kind: "lastDays", n: 31 }` 400、訊息 `日期區間的 N 必須介於 1 到 30`；存報表時條件 `withinLastDays 31` 也被擋。
  - `DashboardCardsApiTests.Layout_LoweredMaxCards_MoveAllowed_AddRejected`（Review Focus 1）：先存 3 張卡片，`dashboard.maxCards` 改 2 → 只改位置存檔成功；加到 4 張 400、`卡片最多 2 張`；新建儀表板 3 張 400。
  - 既有報表與儀表板測試不改斷言（出廠值）。
  - `DrillDialog` vitest `截斷訊息帶實際列數`：回應 `truncated: true`、3 列 → 顯示 `明細太多，只顯示前 3 列`；`CardDrillDialog` 同一案。`truncated: false` 時不顯示。
- [ ] **Step 2: 跑測試確認失敗**：`cd poc/backend && dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~ReportRunnerTests"`；`dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~ReportsApiTests|FullyQualifiedName~DashboardCardsApiTests"`；`cd poc/frontend && bunx vitest run src/presentation/reports src/presentation/dashboards --maxWorkers=2`
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；另跑 `dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~Report|FullyQualifiedName~Dashboard"`；`cd poc/frontend && bunx vitest run src/domain/dynamic/dashboard-layout.vitest.spec.ts --maxWorkers=2`（若有測試引用 `MAX_CARDS` 一併改）；`bunx vitest run src/presentation/reports src/presentation/dashboards src/i18n --maxWorkers=2`；`bun run typecheck`；`grep -rn "DrillLimit\|DetailLimit\|MaxCards\|MAX_CARDS" poc/backend/src poc/frontend/src` 只剩 `ReportLimits` 屬性名；`grep -on '"drillTruncated":"[^"]*500[^"]*"' poc/frontend/src/i18n/*.json` 應無結果（i18n 不留下鑽筆數的副本）；PostgreSQL：`FORMWRIGHT_TEST_DB=postgres dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~ReportsApiTests&Category!=SqliteOnly&Category!=PgDeferred&Category!=Benchmark"`
- [ ] **Step 5: Commit**（`feat(policy): 報表下鑽、明細、查詢天數與儀表板卡片數改讀政策（A24）`）

### Task 10: p2b 文件、實機與 gate

**Files:**
- Modify: 設計文件 §10 技術限制補 p2b 範圍（「p2b 已登記」）、`EventValidator.MaxSets 50`、效果檢查迴圈截在上限（判斷 19）、匯入請求上限＝兩倍檔案上限＋64KB；§12 只加技術判斷 10、11、19、20（判斷 12、13 是使用者 2026-11-12 裁決，§3.7 已寫）；路線文件 p2b；功能清單；`docs/manual.md`（匯入一章的「1000 筆、1 MB」、連動事件一章的層數與筆數、明細列上限、報表下鑽與明細筆數、儀表板卡片數，改成「依政策設定，出廠 …」）

- [ ] **Step 1: 實機**：四種寬度開 `/system/policies`（匯入、連動事件、單據、報表四類）、明細模組的「政策」分頁覆寫 `record.maxDetailRows` 2 後在單據上加第 3 列看到「明細最多 2 列」；一般模組的「政策」分頁沒有這項；把 `import.maxRows` 設 2 後匯入 3 筆看到錯誤；事件編輯對話框的效果上限提示；`report.drillLimit` 設 2 後下鑽看到「只顯示前 2 列」。四語切換各看一次。
- [ ] **Step 2: gate**（同 Task 5 Step 2，含 PostgreSQL 兩步）
- [ ] **Step 3: Commit**（`docs: 處理上限政策的手冊、可寫死清單與 p2b 進度`）

---

# p2c 維運上限：A26–A30

### Task 11: 登記並接上 A26–A30

**Files:**
- Modify: `src/DynamicModules.Domain/Policies/PolicyCatalog.cs`（6 項）、`poc/shared/policy-catalog.json`（codes＋categories `check`；A26、A27 用 O1 的維運分類，O1 沒建時才加 `maintenance`，判斷 16）、`poc/frontend/src/i18n/*.json`
- Modify: `src/DynamicModules.Application/Common/Behaviors/IdempotencyBehavior.cs:18-22,55`
- Modify: `src/DynamicModules.WebAPI/Controllers/v3/Dynamic/AttachmentsController.cs:37-39`、`src/DynamicModules.Application/Dynamic/AttachmentService/AttachmentRequests.cs:22`、`AttachmentHandlers.cs`（`SweepAttachmentsHandler`）
- Modify: `src/DynamicModules.Application/Dynamic/CheckService/CheckHandlers.cs:95-96,119`、`CheckStage.cs:17,63-64`、`CheckIndexer.cs:20,65-66`
- Create: `src/DynamicModules.Application/Dynamic/CheckService/CheckMessages.cs`（若同功能已有訊息檔就加在那裡）
- Test: `tests/DynamicModules.ApplicationTests/Dynamic/WriteJournalAndIdempotencyTests.cs`、`tests/DynamicModules.WebAPITests/Dynamic/AttachmentApiTests.cs`、`tests/DynamicModules.ApplicationTests/Dynamic/CheckRuntimeTests.cs`、`CheckHardeningTests.cs`、`PolicyCatalogFixtureTests.cs`

**Interfaces:**
- Produces:
  - `IdempotencyBehavior` 注入 `IPolicyReader`；刪 `RetentionDays`；`PurgeOlderThanAsync(time.Now.AddDays(-await policies.IntAsync(PolicyCatalog.MaintenanceIdempotencyRetentionDays, null, ct)), ct)`。
  - `SweepAttachmentsRequest(TimeSpan? Grace)`；Controller `[FromQuery] int? graceMinutes = null` → `graceMinutes is { } g ? TimeSpan.FromMinutes(Math.Max(0, g)) : null`；handler 為 null 時讀 `maintenance.orphanSweepGraceMinutes`。
  - `ScanCheckHandler` 注入 `IPolicyReader`，刪兩個常數；`CheckStage` 注入，刪 `MaxGroups`，訊息 `CheckMessages.TooManyGroups(int max) => $"這次請求牽涉的檢查組數超過 {max}，請分批處理"`；`CheckIndexer` 注入，刪 `MaxRecords`，訊息 `CheckMessages.TooManyToBackfill(int count, int max) => $"這個模組有 {count} 筆記錄，超過 {max} 筆，一次回填索引會長時間占住寫入鎖；請在低峰時段分次處理"`。`CheckIndexer.BatchSize` 留（B31）。
- 文案（zh-TW）：
  - `maintenance.idempotencyRetentionDays` 重送保護紀錄保留天數／同一個請求在這段期間內重送不會重複寫入；過期的紀錄在下次寫入時清掉。
  - `maintenance.orphanSweepGraceMinutes` 未使用附件的清理寬限（分鐘）／上傳後超過這麼久仍沒存進記錄的檔案，清理時會刪除；維運清理指令可另外指定。
  - `check.scanMaxViolations` 檢查試跑最多回報幾組違規／超過時只列前面這些組。
  - `check.scanMaxGroups` 檢查試跑最多掃描幾組／超過時停止並標示「未掃完」。
  - `check.maxGroupsPerWrite` 一次存檔最多驗證幾組跨記錄檢查／超過時這次存檔取消，請分批處理。
  - `check.maxBackfillRecords` 儲存檢查時最多回填幾筆索引／模組記錄數超過時不能儲存這個檢查，請在低峰時段處理。
  - 分類：`check` 跨記錄檢查；A26、A27 進 O1 的「維運」分類（沒有時才新增 `maintenance` 系統維護）。

- [ ] **Step 1: 寫失敗的測試**
  - `PolicyCatalogFixtureTests.Factories_EqualCurrentBehavior` 加 6 項。
  - 下列 `Purge_FollowsRetentionPolicy`、`Sweep_NoGrace_UsesPolicy`、`Backfill_RecordPolicy` 標 `Category=Postgres`（資料庫端的時間比較與計數；`Sweep` 所在的 `AttachmentApiTests` 若整類不在雙跑集，只在這個方法上標）。
  - `WriteJournalAndIdempotencyTests.Purge_FollowsRetentionPolicy`：`maintenance.idempotencyRetentionDays` 1 → 2 天前的紀錄在下一次帶 key 的寫入後被清，12 小時前的保留；既有 7 天的測試不改。
  - `AttachmentApiTests.Sweep_NoGrace_UsesPolicy`：`maintenance.orphanSweepGraceMinutes` 5 → 10 分鐘前的孤兒檔被清；不帶參數、出廠值 60 時不清；帶 `?graceMinutes=0` 照舊立即清。
  - `CheckRuntimeTests.Scan_ViolationPolicy`：`check.scanMaxViolations` 1 → 兩組違規只回 1 組、`truncated: true`。
  - `CheckRuntimeTests.Write_GroupPolicy`：`check.maxGroupsPerWrite` 1 → 一次寫入牽涉兩組 409、訊息 `這次請求牽涉的檢查組數超過 1，請分批處理`。
  - `CheckHardeningTests.Backfill_RecordPolicy`：`check.maxBackfillRecords` 1000 且模組有 1001 筆記錄 → 儲存檢查 409、訊息含「超過 1000 筆」（技術下限 1000，資料用既有大量建檔 helper；若太慢，改測 `CheckIndexer` 單元層並在結果檔註明）。
- [ ] **Step 2: 跑測試確認失敗**：`cd poc/backend && dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~WriteJournalAndIdempotencyTests|FullyQualifiedName~CheckRuntimeTests|FullyQualifiedName~CheckHardeningTests"`；`dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~AttachmentApiTests"`
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；`dotnet test tests/DynamicModules.DomainTests --filter "FullyQualifiedName~Policies"`；`--filter "FullyQualifiedName~Check|FullyQualifiedName~Idempot"`；PostgreSQL：`FORMWRIGHT_TEST_DB=postgres dotnet test tests/DynamicModules.ApplicationTests --filter "(FullyQualifiedName~WriteJournalAndIdempotencyTests|FullyQualifiedName~CheckHardeningTests)&Category!=SqliteOnly&Category!=PgDeferred&Category!=Benchmark"`、WebAPITests 同法跑 `AttachmentApiTests`；前端 `bunx vitest run src/domain/dynamic/policy-i18n.vitest.spec.ts src/i18n --maxWorkers=2`
- [ ] **Step 5: Commit**（`feat(policy): 冪等保留、孤兒附件寬限與跨記錄檢查上限改讀政策（A26–A30）`）

### Task 12: p2c 文件、實機與 gate；p2 收尾

**Files:**
- Modify: 設計文件 §10 補 p2c 範圍（「p2c 已登記」）；§12 加計畫判斷 15、16；路線文件 p2 全部完成；功能清單 p2 打勾（跑腳本）；`docs/manual.md` 政策設定一節加「系統維護」「跨記錄檢查」；`docs/deploy.md`（清掃 API 的 `graceMinutes` 改為可省略，省略時用政策）

- [ ] **Step 1: 實機**：四種寬度開 `/system/policies` 的兩個新分類；四語各看一次。
- [ ] **Step 2: 常數殘留掃描**：`grep -rnE "(const|static readonly) (int|long|decimal|double) .*\bMax[A-Za-z]* =" poc/backend/src --include='*.cs' | grep -v Migrations`（抓得到一行多個宣告、第一個不叫 Max 的寫法，例如 `DashboardRules` 的 `Columns = 12, MaxCards = 40, …`，以及 `decimal` 的 `FormulaEvaluator.MaxDays`）；另跑 `grep -rnE "(const|static readonly) (int|long|decimal|double) .*\b(Limit|Days|Rows)[A-Za-z]* =" poc/backend/src --include='*.cs' | grep -v Migrations` 抓不以 Max 開頭的上限（例如舊的 `DrillLimit`、`RetentionDays`）。逐條確認剩下的都已列在設計 §10；把結果貼進 p2 結果檔。
- [ ] **Step 3: gate**（同 Task 5 Step 2，含 PostgreSQL 兩步）
- [ ] **Step 4: Commit**（`docs: 維運上限政策的手冊與 p2 完成`）

---

## 自我檢查結果

1. **設計覆蓋**：A16→Task 2；A19→Task 4；A21→Task 6；A22→Task 7；A23→Task 1（目標）、8；A24→Task 9；A25→Task 1、3；A26–A30→Task 11；§3.5 只管新增→Task 2、3、7、8、9；§3.6 前端取用→Task 2（規則）、3（欄數）、7（效果數）；§10 更新→Task 5、10、12。
2. **步驟掃描**：每個測試步驟寫了測試名與關鍵斷言值；程式步驟給簽章、檔案與出廠值時的訊息原文。
3. **型別一致**：`PolicyModuleTarget`、`DesignLimits`、`PackageLimitReader.ErrorsAsync`、`BlockColumns.ForNewBlockAsync`、`ReportLimits`／`ReportLimitReader`、`EffectBudget.TryUseAsync`、`CheckLayout(layout, limits, maxCards, existingCount)` 在使用處名稱一致。
4. **Review Focus**：五條都有指定的測試與所屬 Task。
5. **比例**：只放簽章、測試名、斷言與出廠訊息原文；政策數是 p1 的四倍，篇幅主要在登記表與文案。
6. **2026-11-12 修訂**：使用者裁決 9 條都指到判斷與 Task（見開頭修訂紀錄第 1 點）；審查 p2-M1、S1–S6、N1–N4 各有對應 Task 步驟或測試；PostgreSQL 驗證寫在 Global Constraints 與 Task 1、5、8、9、10、11、12。
