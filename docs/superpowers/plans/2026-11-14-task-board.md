# 任務看板 kb1–kb3 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 任務從「清單＋記錄頁區塊」升級成可以管工作的畫面：部門主管看得到、重派得了部屬的任務（kb1）；任務頁可以切成看板，依狀態、負責人、到期區間分欄，拖卡片就改值，看板可以存、可以分享給人、部門、群組（kb2 後端、kb3 前端）。每人看到的任務永遠不多於自己本來看得到的。kr（報表）、st（子任務）、gt（甘特圖）不做，但可見條件、檢視資料表、看板設定格式要讓它們直接接上。

**Architecture:** kb1 把 `TaskAccess` 的逐筆查記錄 stub 改成批次，並新增 `TaskVisibility`：在 C# 算好「這位使用者的可見條件」（管理員、自己、部屬名單、每個可讀模組的資料範圍），Persistence 轉成**一個** EF LINQ 條件（kr1 的任務資料來源、kb2 的「全部看得到的」範圍都用它）。部屬解析是 Domain 純函式 `Subordinates.Of(OrgSnapshot, …)`，吃既有的 `OrgSnapshotLoader`。主管重派走既有 `PATCH /tasks/{id}`，寫入後重算可見性，看不到時回一次性的 `undoToken`（Data Protection 加密、無狀態、靠任務版本保證只能用一次，不加資料表）。kb2 加 `Dynamic_TaskView`／`Dynamic_TaskViewShare`（唯一一個 migration）、檢視 CRUD 與分享 API、看板欄查詢與移動 API；看板設定是 Domain 的 `TaskBoardConfig`（有 `version`），到期區間的邊界是 Domain 的 `TaskDueBuckets`，與清單共用同一個「本週最後一天」函式。kb3 在任務頁加「清單／看板」切換、看板元件、Pointer Events 自寫的「拖到欄」、每張卡的「移到…」選單（鍵盤與觸控的路徑）、看板設定、看板選擇器與分享對話框。

**Tech Stack:** .NET 8、EF Core（SQLite；新 WebAPI 測試標 `[Trait("Category", "Postgres")]`，不寫 SQLite 專屬語法）、MediatR、xUnit＋FluentAssertions、ASP.NET Core Data Protection（已在 WebAPI 設定）；Vue 3、Quasar 2、TanStack Query、vue-i18n、vitest、bun。**不新增相依套件**（拖拉自寫，理由見 J9）。

**Spec:**
- `docs/superpowers/specs/2026-11-13-task-board-reports-gantt-design.md`：§2 反目標、§3、§4（主管範圍、可見性、A32／A33）、§5（看板全部）、§6（資料表、權限、分享）、§9 開關組合、§12 API、§13 ERP 對照、§14 可寫死清單、§15、§16 migration、§17 U1–U17／T1–T10／T24、§19 測試重點（主管範圍、可見性查詢、看板拖拉、自訂看板與分享、畫面）、§20 kb1–kb3、§21。
- `docs/superpowers/specs/2026-11-10-task-management-design.md` §5.5 覆寫旗標、§7 可見與權限、§8 畫面。
- `docs/superpowers/specs/2026-11-11-policy-settings-design.md` §4 政策目錄。
- `docs/superpowers/specs/2026-11-10-approval-workflow-design.md` §11 組織骨架（只讀，不改）。
- 樣板：`TaskAccess`、`GetMyTasksHandler`、`TaskWriter`、`ReportShare`＋`SetReportSharesHandler`＋`GetReportShareTargets`、`GetReportsHandler`（管理員看得到所有有分享的）、`ManagerChain`、`last-dashboard.ts`、`presentation/reports/ShareDialog.vue`、`MyTasksPage.vue`、`TaskDialog.vue`。
- repo 規範：`AGENTS.md`、`.claude/rules/backend.md`、`frontend.md`、`docs.md`、`.claude/skills/formwright-purpose-check/SKILL.md`、`docs/ai/testing.md`。

**路徑慣例：** 後端相對 `poc/backend/`，前端相對 `poc/frontend/`，文件與腳本相對 repo 根目錄。worktree：`../dynamic-fields-design.taskboard`，每段一個分支 `feature/kb1`、`feature/kb2`、`feature/kb3`，各自從當時的 `origin/main` 開（前一段合併後才開下一段）。行號以 a3 worktree 讀到的 main 內容為準，Task 0 重新核對。

---

## 待確認的行為決定

設計沒寫死、會改變使用者看到的行為的決定。實作照「推薦」進行；使用者推翻時改對應 Task。

**D1 欄位放不下時用「分段平移」，不換行、不橫向捲動；手機一次看一欄。**
- 推薦：看板依容器寬度算一屏放幾欄（每欄最小寬度約 260px），欄頭列兩側有「上一組／下一組」按鈕與「第 1–4 欄，共 9 欄」；手機（390）一次一欄，上方是可點選的欄位晶片（顯示欄名與數量）切換。狀態看板在筆電、桌機一屏放得下四欄，不會出現平移鈕。
- 理由：專案規定四種寬度都不得整頁或容器內橫向捲動；負責人看板可能有十幾欄，換行會把同一列的卡片擠到很下面，看不出「欄」。甘特圖（T22）也用分段平移，兩個檢視一致。
- 選錯代價：選換行時，負責人多的看板會變成很長的一頁、欄與欄對不齊；選平移時，跨到畫面外的欄不能直接拖，要先平移或用「移到…」。
- 例子：`manager01` 在 1366 寬打開「依負責人」看板，有「未指派、業務一、業務二、業務三、進銷存主管…」共 9 欄 → 一屏 4 欄，按「下一組」看第 5–8 欄；在手機上點晶片「業務二（3）」只看那一欄。

**D2 拖拉的方式依裝置分開；每張卡都有「移到…」，那就是鍵盤與觸控的路徑。**
- 推薦：桌機與筆電用滑鼠按住拖；平板長按約 0.4 秒後開始拖（避免和捲動打架）；手機不拖，只用「移到…」。每張卡右上角都有「移到…」按鈕（所有寬度），鍵盤 Tab 到卡片、Enter 開詳細，Tab 到「移到…」、Enter 開選單、方向鍵選欄、Enter 移動；不能放的欄在選單裡灰掉並寫原因。移動結果用 `aria-live` 念出。不做「空白鍵拿起、方向鍵移動」的鍵盤拖拉模式。
- 理由：手機欄寬只有一欄，拖拉沒有目標；觸控拖細卡片容易誤觸（同 T22 平板只看的理由）。「移到…」一條路徑同時解決鍵盤、手機、跨到畫面外的欄（D1），比再寫一套鍵盤拖拉簡單，也更好測。
- 選錯代價：做鍵盤拖拉模式要多寫一套狀態機與念讀文字，測試量加倍；只靠「移到…」時，鍵盤使用者要多按兩下。
- 例子：`sales01` 在手機看「依狀態」看板的「待辦」欄 → 卡片「回覆恆川機械樣品問題」的「移到…」→ 選「進行中」→ 卡片離開這一欄，晶片「進行中」數量加一。

**D3 重派後「已移出你的範圍」提示顯示 10 秒，期間可按「復原」；復原照一般改負責人處理，已發出的通知不收回。**
- 推薦：提示 10 秒（伺服器的復原憑證 2 分鐘內有效，留網路延遲餘裕）；按「復原」把負責人改回原負責人，留言串多一筆一般的「改負責人」系統記錄；被誤派的人已收到的「指派給你」通知不刪，原負責人會再收到一則「指派給你」。清單、任務詳細、看板三處同一個提示。
- 理由：設計 U16 只說「提示期間可復原」，沒定多久；10 秒是常見的復原提示長度。收回通知要動通知表、還要處理對方已讀的情況，不值得；兩則通知都如實反映發生過的事。
- 選錯代價：太短來不及按、太長提示擋畫面；要收回通知時多一段刪除邏輯與測試。
- 例子：`planner01`（生產部主管）把現場人員的一張任務重派給業務一（不是他的部屬）→ 卡片消失、底部「已移出你的範圍　復原」→ 5 秒內按「復原」→ 任務回到現場人員；業務一的鈴鐺留著「指派給你」，現場人員多一則同名通知。（`manager01` 管總經理室，全公司都是他的部屬，Demo 上要用 `planner01` 或把任務派給管理員才看得到移出。）

**D4 看板設定要按「儲存」才寫回；拖卡片只改任務、不改看板設定。**
- 推薦：看板標題列的「看板設定」開對話框（分欄依據、範圍、篩選、欄順序與隱藏、欄內排序、明列的負責人），按「套用」只影響目前畫面，按「儲存」才寫回自己的看板；有未儲存的變更時標題旁顯示「（未儲存）」。預設看板與分享給我的看板只有「套用」與「另存為新看板」。
- 理由：分享出去的看板被建立者隨手一調就改到所有被分享者的畫面；明確儲存讓建立者知道自己在改共用設定。拖卡片是改任務的值，與看板設定無關（U6 也不存欄內順序）。
- 選錯代價：自動儲存時，建立者試個篩選就改到別人看到的看板；明確儲存時，忘了按儲存的人下次打開會回到舊設定。
- 例子：`manager01` 在自己分享給業務部的「業務部任務」看板加篩選「到期日：本月」→ 按「套用」先看結果 → 確認後按「儲存」，業務一下次打開才看到同樣的篩選。

**D5 主管重派的入口是任務詳細與負責人看板，清單不加行內重派；任務詳細對主管只開放負責人欄。**
- 推薦：主管對部屬的任務打開詳細時，負責人下拉可改、其他欄唯讀，標題下顯示一行「你以主管身分查看，只能改負責人與留言」；看板依負責人分欄時可以拖。清單（含「部屬的任務」分頁）點列開詳細，不另加行內按鈕。
- 理由：U4 只給主管改負責人；把其他欄位變唯讀並說明，比讓主管改了才收到 403 清楚。清單加行內重派要再做一個選人元件與手機版型，價值不高。
- 選錯代價：行內重派省一次點擊，但多一套 UI 與四寬度走查；不說明時主管會以為是權限壞了。
- 例子：`manager01` 在「部屬的任務」分頁點「整理名片」（業務一的個人任務）→ 詳細對話框標題、說明、狀態、到期日都灰色，負責人可選 → 改成「業務二」→ 儲存。

**D6 網址指到的看板已刪除或不再分享給你時，回到預設看板並提示，不顯示錯誤頁。**
- 推薦：`/tasks?view=board&board=<id>` 讀不到（404、403）時，換成預設看板、網址拿掉 `board`，頂端提示「這個看板已刪除或不再分享給你」；瀏覽器記住的「上次打開的看板」同時清掉。
- 理由：分享是隨時會被撤回的；收到舊連結的人需要的是能繼續工作的畫面，而不是錯誤。
- 選錯代價：顯示錯誤頁時要多一步回到任務頁；靜默回預設時使用者不知道為什麼看板變了。
- 例子：`manager01` 刪掉「業務部任務」後，`sales01` 點舊連結 → 看到自己的預設看板與上述提示。

**D7 被分享到範圍是「部屬的任務」的看板、但自己沒有部屬時，看板照開、內容為空並說明，不自動改成別的範圍。**
- 推薦：欄照設定顯示、每欄 0 筆，頂端提示「這個看板的範圍是「部屬的任務」，你目前沒有部屬」；「另存為新看板」時範圍選單沒有「部屬的任務」，預設改成「指派給我」。政策 A32 關閉時對所有人同樣處理。
- 理由：範圍相對於打開的人（§5.7），換成別的範圍等於偷偷改了建立者的設定；空看板加說明最誠實，也不會多看到東西（反目標第 5 條）。
- 選錯代價：自動改成「指派給我」時，被分享者以為那是主管要他看的內容；空看板時他要自己另存一份才有用。
- 例子：`manager01` 把「業務部負載」（依負責人、部屬的任務）分享給業務部 → `sales01` 打開看到空的欄與提示；`ops01` 若也被分享，看到的是他自己部屬（營運部、採購部、生產部的人）的任務。

---

## 與 a3 的共用檔

a3（簽核：轉交、加簽、代理、逾期提醒、列印預覽）正在另一個 worktree 平行實作。kb1–kb3 **不碰任何 `Approval*` 檔案、`DemoApprovalSteps.cs`、`InventoryScenario.cs`、`ApprovalsController.cs`、`MessageText.cs`**（任務的新訊息放 `TaskMessages.cs`、`TaskViewMessages.cs`）。不可避免的共用檔與降衝突做法：

| 檔案 | kb 要做的事 | 衝突怎麼降 |
|---|---|---|
| `poc/frontend/src/i18n/{zh-TW,en-US,th,vi}.json` | 新鍵與改「我的任務」→「任務」 | 檔案是**單行 JSON**，任何改動在文字層面都會衝突，「檔尾附加」做不到。做法：新鍵集中在新命名空間 `taskBoard.*`，`tasks.*` 只加少數鍵；每段把自己要加的鍵另存成 `.tmp/plans/20261114-task-board/i18n/<段>.<語系>.json`（不進 repo）。rebase 撞到衝突時，這四個檔一律取 main 的版本，再用一行 bun 指令把補丁深度合併回去（`bun -e` 讀兩份、`Object.assign` 遞迴、`JSON.stringify` 單行寫回），最後跑 `locales.vitest.spec.ts`。不手動解文字衝突。 |
| `poc/shared/policy-catalog.json` | `codes` 加兩個代碼 | `codes` 是單行陣列，同上：衝突時取 main 版本，再把兩個代碼加回去，跑 `PolicyCatalogFixtureTests` 與 `policy-i18n.vitest.spec.ts`。 |
| `src/DynamicModules.Domain/Policies/PolicyCatalog.cs` | 兩個政策定義 | 定義寫在 `TaskNotifyCancelled` 那一段之後（a3 加在 `Approval*` 之後）；`All` 清單另起一行放在 `TaskNotifyCancelled,` 那行之後，不改 approval 那幾行。 |
| `tests/DynamicModules.DomainTests/Policies/PolicyCatalogFixtureTests.cs`（或 `PolicyDefinitionTests`） | 出廠值斷言 | 加在 task 那一串斷言之後，不碰 approval 段。 |
| `docs/superpowers/specs/2026-11-11-policy-settings-design.md` §4 | 目錄加兩列 | **編號撞號**：a3 計畫已把 A32、A33 給了加簽與逾期提醒，任務看板設計也用 A32、A33（與 st 的 A34）。程式只認政策代碼，編號只是文件標籤：kb1 合併時取當下 main 上下一個未用的編號（a3 先合併就是 A34、A35，子任務上限順延 A36），同時改任務看板設計 §4.5、§14、§16、§17 的編號；kb1 先合併就照設計用 A32、A33，並在 a3 的計畫檔「修訂紀錄」留一行請 a3 順延。列在每段交付說明。 |
| `src/DynamicModules.Application/DependencyInjection.cs` | 註冊 `TaskVisibilityService`、`TaskViewAccess` 等 | 只在既有 `TaskService` 那三行（`TaskAccess`、`TaskSupport`、`TaskWriter`）之後插入，不加在檔尾。 |
| `poc/frontend/src/services/api/dynamic-api.ts` | 任務的新端點 | 只加在既有任務區塊（`getMyTasks`…`getRecordTasks` 附近），a3 加在簽核區塊，兩段相距約 150 行。 |
| `src/DynamicModules.Persistence/Migrations/`（含 `DynamicDbContextModelSnapshot.cs`） | kb2 的 `TaskViews` migration | a3 沒有 migration（a3 J1）。kb2 在**最後一個 commit 前**才產生 migration：先 rebase 到最新 `origin/main`，確認 main 的 snapshot 沒變才 `dotnet ef migrations add TaskViews`；合併前若 main 的 snapshot 變了（例如 fg1 先合併），刪掉自己的 migration 重新產生，不手改 snapshot。kb1、kb3 不加 migration。 |
| `docs/manual.md` | §15 任務 | a3 只改 §21 簽核，不同段落。 |
| `docs/feature-checklist.md` | 「任務管理」段 | a3 改「組織與簽核」段；檔頭統計行兩邊都會由腳本重算 → 衝突時取 main 版本、重新勾自己的項目、重跑 `feature_checklist.py --write`。 |
| `docs/roadmap.md` | 第 44 列 kb1–kb3 | a3 改第 37 列，相距 7 行，正常不衝突。 |
| `tests/DynamicModules.ApplicationTests/Dynamic/Demo/*` | 新增 `TaskBoardScenarioTests.cs` | 不改既有 Demo 測試的斷言（Demo 任務都由負責人自己建立，不產生通知，見 Task 4）；a3g 會改簽核相關的 Demo 斷言，兩邊不重疊。 |
| `src/DynamicModules.Application/Dynamic/DemoService/DemoCatalog.cs` | 情境清單加 `new TaskBoardScenario()` | a3 不改這個檔；加在清單最後一個元素後面。 |

合併順序：kb 與 a3 沒有程式依賴，誰先合併都可以；後合併的那一邊照上表處理。kb 內部依序 kb1 → kb2 → kb3。

---

## Global Constraints

- **業務政策不寫死**（鐵則 2）：
  - 新政策兩個，登記在 `PolicyCatalog`（出廠值只寫在這裡），同步 `poc/shared/policy-catalog.json`、四語 `policies.items.<code>.{name,help}`、`PolicyCatalogFixtureTests`：
    - `task.supervisorScope`：部門主管可看與重派部屬的任務，System、Boolean、出廠 `true`（設計 A32）。
    - `task.supervisorScopeRecursive`：主管範圍含下層部門，System、Boolean、出廠 `true`（設計 A33）。
  - 一週起始日讀既有 `calendar.weekStart`（A7）；「本週最後一天」只有一個函式（`TaskDates.WeekEnd`，見 J7），清單與看板都呼叫它。
  - 看板沒有新的業務數字：每欄頁數、名稱長度、分享上限都是技術限制（可寫死清單）。在製品上限不做。
- **fail closed**：可見條件、部屬名單、模組資料範圍讀不到就當成沒有（不放行）；`TaskVisibility` 遇到模組權限查詢失敗，那個模組不進條件；undo 憑證解不開、過期、不是本人、任務版本不符一律 400，不改任何值。
- **反目標**（設計 §2）是測試對象：看不到記錄時，卡片、清單、移動回應都只給「相關記錄（無權限查看）」，不給模組代號、記錄 Id、記錄標題；分享看板不授予任務權限；主管不因看過或重派成為參與者（T3）。
- **伺服器算日期**：「今天」用 `IBusinessTime.Today`（營運時區），「本週最後一天」與「之後」欄的預設日期由伺服器算好回給前端（T8）；前端不自己算邊界。
- **併發**：卡片帶 `version`（`(ModifiedTime ?? CreatedTime)` 轉 UTC 取到毫秒的字串，J4）；移動 API 版本不符回 409。
- **交易**：寫入 request 實作 `ITransactionalRequest`，不在 handler 開交易。
- **PostgreSQL 相容**：只用 EF LINQ，不寫 raw SQL；關鍵字比對用 `ToLower().Contains`（不依賴 SQLite 的大小寫不敏感）；DateTime 一律明確 `Kind`；新 WebAPI 測試標 `[Trait("Category", "Postgres")]`。migration 目前只有 SQLite 一份（pg0 尚未建立 PostgreSQL migration 目錄，見「與設計文件矛盾」）。
- **i18n**：新句子放 `taskBoard.*`、`tasks.*`、`policies.items.*`；四語鍵集合一致、維持單行 JSON；含 `{`、`}`、`@`、`|` 用字面量；th、vi 不留中文。改之前從當時的 main 重讀檔案，補丁同時存一份到 `.tmp/plans/20261114-task-board/i18n/`（見共用檔表）。
- **RWD 與可及性**：有畫面的子段在本機 Demo 走 390×844、820×1180、1366×768、1920×1080；頁面 `scrollWidth <= innerWidth`，看板容器、欄、對話框 `scrollWidth <= clientWidth`（用 `scripts/unattended/rwd-check.js`）；手機對話框全螢幕。可捲動容器裡的 `column` 加 `no-wrap`。輸入框都有 `label` 或 `aria-label`；卡片與「移到…」可用鍵盤操作（D2）。顏色只用語意 token（`no-hardcoded-colors.vitest.spec.ts`）。
- **測試（新慣例）**：實作者本機**只跑相關測試類別**（`dotnet test <專案> --filter <類名>`、`bunx vitest run <指定檔> --maxWorkers=2`）；完整套件由 GitHub Actions（`.github/workflows/gate.yml`）在推上分支後驗。前端 `bun run typecheck`、`bun run build` 仍要本機跑。動到日期邊界的測試另跑一次 `TZ=UTC`。新測試類 `protected override bool ScopePerRequest => true;`；要控制今天的測試類照 `NotificationApiTests.Clock` 註冊可調的 `IBusinessTime`。不和其他吃 CPU 的工作同時跑 `dotnet test`。
- **Demo 與公開 repo**：資料一律虛構（沿用既有 Demo 人名與客戶名）；不寫公司名、個人絕對路徑、內網 IP；走查紀錄與截圖放 `.tmp/plans/20261114-task-board/walk/`，不進 repo。
- **git**：逐檔 `git add`、commit 前 `git status`；不用 `--no-verify`、不 force push、不 merge commit；Conventional Commits，scope `task`（後端與前端）、`policy`、`demo`、`docs`；不 push main，由主控合併。

### 可寫死清單（kb 新增，屬完整性、安全或技術限制）

| 項目 | 值 | 理由 |
|---|---|---|
| 「逾期」「今天」「本週」「之後」「無到期日」的邊界定義 | 設計 §5.1；本週起始日仍由 A7 決定 | 定義（政策設計 §4 已裁決逾期定義寫死） |
| 到期區間看板只列未完成的任務；不能放進「逾期」欄；今天是本週最後一天時「本週」欄不能放 | 400 | 完整性（U5） |
| 移動 API 由伺服器算日期，不收瀏覽器的「今天」 | — | 安全（T8） |
| 主管只能改負責人；不因看過、重派成為參與者 | 403；不寫參與者 | 安全（U4、T3） |
| 分享看板不授予任何任務權限 | — | 安全（§6.2） |
| undo 憑證：只限發出它的人、只限同一個任務、任務版本不變、2 分鐘內有效 | 400 | 安全（T24） |
| 復原提示顯示 10 秒 | 前端 | 畫面呈現（D3） |
| 看板每欄每頁 50 張、每欄最多 500 張 | 超過顯示「還有 N 筆」 | 技術限制（T9） |
| 看板名稱 100 字；明列的負責人欄 50 人；負責人看板合計最多 50 欄（自動附加的依任務數補到 50 為止，其餘提示「還有 N 位負責人未列出」） | 400／提示 | 技術限制：欄數決定查詢次數（J5） |
| 每個看板分享對象最多 200 個 | 400 | 技術限制（§14） |
| 關鍵字 100 字 | 400 | 格式 |
| 任務版本精確到毫秒 | 同一毫秒的兩次寫入視為同版本 | 技術限制：PostgreSQL 時間只到微秒、JSON 往返要穩定（J4） |
| 部屬超過 2000 人時查詢方式要改 | 已知限制 | §4.4 |

---

## Review Focus

測試最可能沒覆蓋、但最可能咬人的五個情境；每條的測試已加進負責的子段。

1. **批次可見條件與舊的逐筆判斷不一致，導致洩漏或漏列。** 軟刪除記錄的任務、明細模組、已關閉（凍結）模組、資料範圍 Own 與 All 混用、只有被 @ 的參與者、停用的系統管理員、記錄擁有者被改掉。→ kb1 `TaskVisibilityEquivalenceTests.Matrix_PredicateMatchesEvaluateMany`（每個身分 × 每種任務兩邊結果相同）、`Evaluate_QueryCount_DoesNotGrowWithTaskCount`。
2. **主管範圍邊界算錯。** 遞迴經過停用的子部門、停用部門自己的主管、一人當兩個部門的主管、主管本人、沒有部門的人、未指派但建立者是部屬、A32 或 A33 關閉；還有「主管有權改負責人，但改狀態被拒」。→ kb1 Domain `SubordinatesTests`、Application `SupervisorScopeTests.Reassign_Allowed_StatusChange403`、`UnassignedCreatedBySubordinate_Visible`。
3. **「本週」邊界與時區。** 七種一週起始日、今天就是本週最後一天、主機在 UTC。清單「本週到期」筆數必須等於看板「今天」＋「本週」兩欄總數。→ kb2 Domain `TaskDueBucketsTests.Buckets_ForEveryWeekStart`（Theory 七筆）、Application `TaskBoardQueryTests.ListThisWeek_EqualsTodayPlusThisWeek_ForEveryWeekStart`、`TaskMoveTests.Move_ToThisWeek_WhenTodayIsWeekEnd_400`；本機加跑 `TZ=UTC`。
4. **移動與復原的併發、重放。** 兩人同時拖同一張卡、拖拉和詳細對話框同時改、undo 憑證被重送或被別人拿去用、復原前任務又被別人改過。→ kb1 `TaskUndoTests.Undo_Twice_SecondRejected`、`Undo_ByAnotherUser_400`、`Undo_AfterOtherChange_400`；kb2 `TaskMoveTests.Move_StaleVersion_409_NothingWritten`、WebAPI `TaskBoardApiTests.MoveAndMove_Concurrent_OneWins`。
5. **分享等於分享資料、引用失效讓看板壞掉。** 同一個分享看板，兩個身分打開看到不同的任務；分享給部門只含直屬成員；群組與部門刪除時清分享；看板設定引用的使用者停用、部門刪除；沒有部屬的人打開「部屬的任務」看板（D7）。→ kb2 `TaskViewShareTests.SharedBoard_EachViewerSeesOwnScope`、`ShareToDepartment_DirectMembersOnly`、`DeleteGroup_RemovesBoardShares`、`DeleteDepartment_RemovesBoardShares`、`Config_ReferencesInactiveUser_StillLoads`、`SubordinateScope_NonManager_EmptyWithFlag`。

---

## 範圍確認

### 已在 main（只接、不重做）

| 項目 | 證據 |
|---|---|
| 任務資料、參與者、覆寫旗標、系統記錄、通知 | `Entities/Dynamic/TaskItem.cs`、`TaskWriter`（`ApplyAsync` 設 `AssigneeOverridden`／`DueOverridden`） |
| 任務可見與可改的逐筆判斷 | `TaskService/TaskAccess.cs`（`EvaluateManyAsync` 迴圈內查 stub） |
| 我的任務清單：三個分頁、五個篩選、本週最後一天 | `GetMyTasksHandler`、`GetMyTasksHandler.WeekEnd`、`TaskRepository.QueryMineAsync` |
| 組織骨架與快照 | `OrgSnapshot`、`OrgSnapshotLoader`、`ManagerChain`、`DeleteDepartmentHandler`（有成員或子部門不能刪） |
| 政策讀值 | `IPolicyReader.BoolAsync`、`ChoiceAsync` |
| 報表分享（只到群組）、管理員看得到所有有分享的報表 | `ReportShare`、`SetReportShares`、`GetReportShareTargets`、`GetReportsHandler`、`AccountRepository.DeleteGroupAsync` |
| Demo 組織：`manager01` 管總經理室與業務部、`ops01` 管營運部、`planner01` 管生產部 | `Seed/DemoPeople.cs` 的 `Departments` |
| 前端任務頁、任務詳細、上次打開的儀表板（依使用者存瀏覽器） | `MyTasksPage.vue`、`TaskDialog.vue`、`application/dynamic/last-dashboard.ts` |

### 這次做

| 設計項目 | 子段 |
|---|---|
| §4 主管範圍、A32／A33、§4.4 可見性批次查詢（T4）、主管重派、§5.4 移出範圍與復原（T24、U16）、清單「部屬的任務」分頁（U15）、手冊「本週到期＝看板的今天＋本週」（U17） | kb1 |
| §6 `TaskViews` migration、檢視與分享 API、§5.1–§5.7 與 §5.10 的欄查詢與移動 API | kb2 |
| §5.8 清單／看板切換與預設看板、§5.9 卡片、拖拉與「移到…」、自訂看板、分享對話框、四種寬度、Demo 看板 | kb3 |

### 不做

| 項目 | 理由 |
|---|---|
| 報表資料來源、內建報表、部門暫時篩選 | kr1–kr3；kr1 直接用 kb1 的 `TaskVisibility` |
| 子任務、看板「顯示子任務」、父任務完成或取消的連動確認 | st1、st2；`TaskBoardConfig` 預留欄位名 `showSubtasks`（st2 才加，見 J6） |
| 開始日、甘特檢視 | gt1、gt2；`Dynamic_TaskView.Kind` 先只收 `board`，gt1 開放 `gantt` |
| 欄內手動排序、在製品上限 | U6；💡 |
| 動態模組記錄的看板（路線階段 5） | 另一件事（§16） |
| 報表分享擴充成使用者與部門 | U8：這次不改 |
| 群組刪除影響預覽列出看板 | 報表與儀表板有列，看板先不列（刪群組照樣清分享）；列進 `docs/todo.md` |
| 背景輪詢看板 | TanStack Query 預設的視窗重新取得焦點時重抓，加上自己的移動後重抓即可 |

---

## 子段切法與合併順序

| 子段 | 內容 | 估計行數（含測試） | 畫面 | 依賴 |
|---|---|---|---|---|
| kb1 | 部屬解析、兩個政策、可見性批次化與 `TaskVisibility` 條件、主管重派與復原、「部屬的任務」分頁 API 與畫面、Demo 部屬任務、手冊 | 約 1400 | 有（清單分頁、詳細對話框、移出提示） | 無 |
| kb2 | `TaskViews` migration、檢視 CRUD／複製／分享／分享對象 API、看板設定解析、到期區間、欄查詢、移動 API | 約 1300 | 無 | kb1（可見條件、部屬名單、`CanReassign`、undo） |
| kb3 | 任務頁切換、看板元件、拖拉與「移到…」、日期選擇與取消確認、看板設定、選擇器、分享對話框、Demo 看板、文件總收 | 約 1600 | 看板 | kb2 |

- 每段結束時：手冊 §15 對應段落同步、本段新增的寫死項目列在交付說明、`formwright-purpose-check` 跑一次寫進交付說明、`docs/feature-checklist.md` 該項打勾並重算、`docs/roadmap.md` 第 44 列更新進度。
- 每段合併後的界線：
  - kb1：業務主管在任務頁看得到「部屬的任務」分頁，能重派、能復原；政策頁多兩項；其他人行為不變。
  - kb2：API 可建看板、分享、查欄、移動；畫面沒有變化。
  - kb3：任務頁有「清單／看板」切換；左側選單「我的任務」改名「任務」。

---

## 計畫判斷（技術類，主控決定，可推翻）

- **J1 部屬解析是 Domain 純函式。** `Domain/Organization/Subordinates.cs`：`IReadOnlySet<string> Of(OrgSnapshot org, string managerUserId, bool recursive)`。起點＝`ManagerUserId == m` 且 `Active` 的部門；`recursive` 時往下走所有子部門（經過停用的子部門照走，T1），用 visited 防循環；成員取 `UserDepartment` 值在範圍內的使用者，排除 m 本人，不看是否啟用（T2）。子部門關係由 `Departments` 的 `ParentId` 反查（函式內建一次 children 對照）。
- **J2 可見條件分兩層。** Application 的 `TaskVisibilityService.ForCurrentUserAsync(ct)` 產生 `TaskVisibility(bool Admin, string UserId, IReadOnlySet<string> Subordinates, IReadOnlyList<ModuleReadScope> Modules)`（`ModuleReadScope(long ModuleId, IReadOnlySet<string>? OwnerUserIds)`，null＝全部；只含可讀、非明細的模組；部屬名單只在 A32 開啟時有值），同一請求內快取一次。Persistence 的 `TaskRepository.Visible(TaskVisibility)` 回 `IQueryable<TaskItem>`：管理員不加條件；否則 `參與者 EXISTS` OR `負責人 IN 部屬` OR `(負責人 IS NULL AND 建立者 IN 部屬)` OR `記錄 EXISTS(Id = RecordId AND ModuleId IN 全範圍模組)` OR 逐個有範圍的模組 `(ModuleId = m AND 擁有者 IN 範圍)`；以 `Expression.OrElse` 組，不寫 raw SQL。軟刪除記錄照 stub 規則（`Record` 表含已刪除列）。
- **J3 `TaskAccess` 批次化。** 新增 `ITaskRepository.ListRecordStubsAsync(IReadOnlyCollection<long> recordIds)` 一次取 stub；模組權限仍依模組快取；`EvaluateAsync` 改呼叫批次版。`TaskPermissions` 加 `CanReassign`（看得到是因為「部屬任務的主管」、或已有 `CanEdit`；凍結時 false）與 `Supervised`（只因主管範圍才看得到，前端用來顯示 D5 的說明）。`TaskSummaryResponse`、`TaskDetailResponse` 加 `CanReassign`、`Supervised`、`Version`。
- **J4 任務版本字串。** `TaskVersion.Of(TaskItem)`＝`(ModifiedTime ?? CreatedTime)` 視為 UTC、格式 `yyyyMMddHHmmssfff`。不用 `RowVersion` 欄位（不加 migration）；毫秒精度讓 SQLite 與 PostgreSQL 往返都穩定。代價：同一毫秒的兩次寫入擋不住，列為已知限制。
- **J5 看板欄查詢一次回全部欄。** `POST /task-board/columns` 回每欄的總數（一欄一次 `COUNT`，負責人看板用一次 `GROUP BY AssigneeUserId`）與第一頁 50 張（一欄一次查詢）；查詢次數隨欄數成長、不隨卡片數成長。負責人看板合計最多 50 欄，就是為了把查詢次數壓住。`POST /task-board/columns/{column}/page` 載入更多。
- **J6 看板設定格式。** Domain `Tasks/TaskBoardConfig.cs`：`{ version: 1, groupBy: status|assignee|dueBucket, scope: assigned|involved|subordinates|visible, filters: { statuses?, assigneeUserIds?, includeUnassigned?, creatorUserIds?, sources?: [personal|record|rule], dueRange?: ReportDateRange, keyword? }, columns: { order?, hidden?, assignees?, autoAppendAssignees: true }, sort: { key: dueDate|createdTime|title, dir: asc|desc } }`。寫入時未知屬性回 400（擋打錯字）；讀取時未知屬性忽略（舊程式讀到新版本不壞）。新增選填欄位（例如 st2 的 `showSubtasks`）不升版本，語意改變才升。到期日篩選用既有的 `ReportDateRange`（相對區間，以今天解析，不用報表基準日）。
- **J7 本週最後一天搬到 Domain。** `GetMyTasksHandler.WeekEnd` 搬成 `Domain/Tasks/TaskDates.WeekEnd(today, weekStart)`，清單與看板都呼叫它（T5）；`TaskDueBuckets.Of(due, today, weekEnd)` 與 `TaskDueBuckets.TargetDate(column, today, weekEnd)` 是純函式，「之後」欄的預設日期（下週最後一天）也由它算。
- **J8 undo 憑證無狀態。** Application 定義 `ITaskUndoTokens { string Issue(TaskUndoTicket t); TaskUndoTicket? Read(string token); }`，`TaskUndoTicket(long TaskId, string ActorUserId, string? FromUserId, string? ToUserId, string Version, DateTime ExpiresUtc)`。WebAPI 用既有 Data Protection 的 `ITimeLimitedDataProtector`（purpose `task-undo`）實作；ApplicationTests 註冊測試用的實作（Base64＋到期時間）。只能用一次靠「任務版本必須等於憑證裡的版本」：復原一寫入，版本就變了。不加資料表、不怕重開站台以外的失效（Data Protection 金鑰已持久化在資料庫目錄）。
- **J9 拖拉自寫、不加套件。** 看板不需要欄內排序（U6），只要「放開時在哪一欄」：`presentation/tasks/board/use-card-drag.ts` 用 Pointer Events（滑鼠、觸控、筆同一套），放開時以可注入的 `hitTest(x, y)`（預設 `document.elementFromPoint` 找最近的 `[data-board-column]`）判斷目標；平板長按啟動、手機停用（D2）。SortableJS 一類套件的強項是排序與動畫，這裡用不到，還要處理它和 Quasar 捲動容器的觸控衝突。
- **J10 PATCH 回應形狀。** 寫入後仍看得到：回原本的 `TaskDetailResponse`（多 `CanReassign`、`Supervised`、`Version`）。看不到了：回 `200 { id, visible: false, undoToken }`，不回任務內容。前端以 `visible === false` 判斷。移動 API 一律回 `TaskMoveResponse(bool Visible, TaskCardResponse? Card, string? UndoToken)`。
- **J11 能力端點。** `GET /tasks/capabilities` 回 `{ subordinates: bool }`（A32 開且部屬名單非空），清單分頁與看板範圍選單都用它；不塞進首頁的 `tasks/summary`。
- **J12 檢視與看板分兩個服務資料夾。** `Application/Dynamic/TaskViewService/`（看板存檔、分享、分享對象、權限）與 `TaskService/TaskBoard*.cs`（欄查詢、移動）；控制器新增 `TaskViewsController.cs`、`TaskBoardController.cs`，不擠進 `TasksController`。gt1 的甘特檢視接在 `TaskViewService`。
- **J13 分享看得到的範圍。** 看得到一個看板＝擁有者，或分享給我（`user`），或我所在的任一群組（`group`），或我的直屬部門（`department`，U8）；系統管理員另外看得到所有有分享的看板（只能刪，同報表）。被引用的使用者停用、部門刪除時，查詢照常，回應帶 `inactive`／略過（T10）。

---

## Task 0：核對（不改程式）

- [ ] 開 worktree：`git worktree add ../dynamic-fields-design.taskboard -b feature/kb1 origin/main`；記下基準 commit 與 a3 是否已合併（決定政策編號，見共用檔表）。
- [ ] 重讀並確認行號：`TaskAccess.cs`、`TaskHandlers.cs`（`GetMyTasksHandler`、`UpdateTaskHandler`）、`TaskRepositories.cs`（`QueryMineAsync`、`GetRecordStubAsync`）、`TaskModels.cs`（`TaskViews.ToSummary`／`ToDetail`）、`TaskEditParser.cs`、`OrgSnapshotLoader.cs`、`DependencyInjection.cs` 任務區塊。
- [ ] 確認 ApplicationTests 的服務組成在哪裡（`DesignerTestBase` 或 fixture），J8 的測試用 `ITaskUndoTokens` 註冊在那；確認能否掛 `DbCommandInterceptor` 計查詢次數（沒有就在測試專案新增 `CountingCommandInterceptor`）。
- [ ] 確認 `policies.items` 的鍵形狀（代碼含 `.` 時是巢狀還是字面鍵），照既有 `task.notify.commented` 的寫法。
- [ ] 確認 Demo 裡 `manager01` 對進銷存品項模組沒有 Read（Task 4 的「無權限記錄」示範要靠它；有的話改用他讀不到的其他模組，或改用 `ops01` 管的人在 CRM 記錄上的任務）。
- [ ] grep 既有測試對 Demo 任務總數、`sales01`～`sales03`、`keeper01`、`manager01` 的任務與通知數的斷言（`tests/DynamicModules.ApplicationTests/Dynamic/Demo/`），列進 Task 4。已知 `CampaignTaskScenarioTests.Sales01…`、`Sales02_HasAssignedAndMentionNotifications` 是精確比對。
- [ ] grep 前端對 `menu.myTasks`、`tasks.myTitle` 的測試斷言（`MainLayout.vitest.spec.ts`、`MyTasksPage.vitest.spec.ts`），列進 Task 5 與 Task 8。
- [ ] 核對結果寫在本檔末「修訂紀錄」。

---

## kb1：主管範圍、可見性批次化、重派與復原

### Task 1：部屬解析與政策

**Files:**
- Create: `src/DynamicModules.Domain/Organization/Subordinates.cs`
- Modify: `src/DynamicModules.Domain/Policies/PolicyCatalog.cs`（`TaskSupervisorScope`、`TaskSupervisorScopeRecursive`，位置見共用檔表）
- Modify: `poc/shared/policy-catalog.json`
- Modify: `poc/frontend/src/i18n/*.json`（`policies.items.task.supervisorScope*` 四語）
- Test: `tests/DynamicModules.DomainTests/Organization/SubordinatesTests.cs`（新）、`tests/DynamicModules.DomainTests/Policies/PolicyCatalogFixtureTests.cs`

**介面：** 見 J1；`PolicyCatalog.TaskSupervisorScope = new("task.supervisorScope", "task", PolicyKind.Boolean, PolicyScope.System, "true")`，`TaskSupervisorScopeRecursive` 同形。

**測試（`SubordinatesTests`，用小型手造 `OrgSnapshot`）：**
- `Direct_MembersOfManagedDepartment_ExcludesManager`
- `Recursive_IncludesAllDescendants`；`NonRecursive_OnlyDirectDepartment`
- `Recursive_PassesThroughInactiveChild`（停用子部門的成員仍算，T1）
- `InactiveDepartment_ItsOwnManagerGetsNothing`（T1）
- `ManagerOfTwoDepartments_Union`（U1）
- `UserWithoutDepartment_NeverSubordinate`（U2）
- `InactiveUser_StillSubordinate`（T2）
- `Cycle_DoesNotLoop`（資料若被弄成循環也會結束）
- `PolicyCatalogFixtureTests.Factories_EqualCurrentBehavior` 加兩行出廠值與 `Scope == System`。

**驗收：** `cd poc/backend && dotnet test tests/DynamicModules.DomainTests --filter "FullyQualifiedName~Subordinates|FullyQualifiedName~PolicyCatalog"`；`cd poc/frontend && bunx vitest run src/domain/dynamic/policy-i18n.vitest.spec.ts src/i18n --maxWorkers=2`。

- [ ] 寫失敗測試 → 實作 → 通過 → commit `feat(policy): 主管範圍與含下層部門兩個任務政策，部屬解析純函式`

### Task 2：可見性批次化與 `TaskVisibility` 條件

**Files:**
- Create: `src/DynamicModules.Application/Dynamic/TaskService/TaskVisibility.cs`（`TaskVisibility`、`ModuleReadScope`、`TaskVisibilityService`）
- Modify: `TaskService/TaskAccess.cs`（批次 stub、主管判斷、`CanReassign`、`Supervised`）
- Modify: `TaskService/TaskModels.cs`（回應加欄位、`TaskVersion`）
- Modify: `src/DynamicModules.Domain/Repositories/Dynamic/ITaskRepositories.cs`（`ListRecordStubsAsync`、`IQueryable<TaskItem> Visible(TaskVisibility v)` 放在 `ITaskQueries` 或直接回分頁結果，照既有 repository 不外露 `IQueryable` 的慣例：Task 0 確認；若不外露，改成 `QueryVisibleAsync(TaskVisibility, TaskBoardFilter?, …)` 由 kb2 擴充）
- Modify: `src/DynamicModules.Persistence/Repositories/Dynamic/TaskRepositories.cs`
- Modify: `src/DynamicModules.Application/DependencyInjection.cs`（`TaskVisibilityService`）
- Test: `tests/DynamicModules.ApplicationTests/Dynamic/Tasks/TaskVisibilityEquivalenceTests.cs`（新）、`TaskAccessTests.cs`（既有，全數照過）

**規則：**
1. `TaskVisibilityService` 讀 A32、A33、`OrgSnapshotLoader`（A32 關時不載組織）、各模組 Read 權限與資料範圍；結果在同一請求內快取。
2. `TaskAccess` 的「部屬任務的主管」判斷用同一份部屬名單：`task.AssigneeUserId ∈ 部屬`，或 `AssigneeUserId == null && CreatedBy ∈ 部屬`。
3. 主管只因主管範圍看得到、對記錄沒有 Read（或在範圍外）時，`Record = Hidden`（§4.3）。
4. 管理員判斷沿用 `IsSystemAdminAsync`（停用的管理員不算）。

**測試：**
- `Matrix_PredicateMatchesEvaluateMany`：固定一組任務（個人、記錄、軟刪除記錄、凍結模組、明細模組不會有任務但要確保不在條件內、規則任務、未指派、只有被 @）× 身分（管理員、停用管理員、參與者、記錄 Read 範圍 Own、範圍 All、主管、主管但 A32 關、什麼都不是）；`Visible(v)` 撈出的 Id 集合等於 `EvaluateManyAsync(...).Where(CanSee)`。
- `Evaluate_QueryCount_DoesNotGrowWithTaskCount`：10 筆與 300 筆任務（分屬 3 個模組），`EvaluateManyAsync` 的 SQL 次數相同。
- `Supervisor_RecordOutOfScope_TaskVisible_RecordHidden`：`manager01` 看得到部屬掛在他讀不到的記錄上的任務；回應的 `Record` 是 `Accessible=false`、其餘 null；序列化後的 JSON 不含模組代號、記錄 Id、標題。
- `Supervisor_PolicyOff_NotVisible`、`Supervisor_NonRecursive_OnlyDirect`。
- `Supervisor_IsNotParticipant_NoCommentNotification`（T3）：主管看過、留言前後，參與者表沒有主管。

**驗收：** `dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~TaskVisibility|FullyQualifiedName~TaskAccess|FullyQualifiedName~TaskQuery"`。

- [ ] 寫失敗測試 → 實作 → 通過 → commit `refactor(task): 任務可見性批次判斷與單一查詢條件，加入部屬任務的主管`

### Task 3：主管重派、移出範圍與復原、「部屬的任務」分頁 API

**Files:**
- Modify: `TaskService/TaskHandlers.cs`（`UpdateTaskHandler` 主管分支與 J10 回應、`GetMyTasksHandler` 新分頁、`GetTaskCapabilitiesHandler`、`UndoReassignHandler`；`WeekEnd` 暫不搬，kb2 J7 搬）
- Modify: `TaskService/TaskRequests.cs`（`GetTaskCapabilitiesRequest`、`UndoReassignRequest : ITransactionalRequest`）
- Create: `TaskService/TaskUndoTokens.cs`（`ITaskUndoTokens`、`TaskUndoTicket`）
- Create: `src/DynamicModules.WebAPI/Services/DataProtectionTaskUndoTokens.cs`（Task 0 確認 WebAPI 放服務實作的慣例目錄）
- Modify: `src/DynamicModules.WebAPI/Program.cs`（只加一行註冊；a3 不改這個檔）
- Modify: `TaskService/TaskMessages.cs`（`UndoInvalid`「這次變更已無法復原」、`SupervisorOnlyAssignee`「你以主管身分只能改負責人」）
- Modify: `ITaskRepositories.cs`（`TaskListTab.Subordinates`、`TaskListQuery` 加 `Subordinates`）、`TaskRepositories.cs`（`QueryMineAsync` 新分頁）
- Modify: `src/DynamicModules.WebAPI/Controllers/v3/Dynamic/TasksController.cs`（`GET tasks/capabilities`、`POST tasks/{id}/undo-reassign`；`tab=subordinates`）
- Test: `tests/DynamicModules.ApplicationTests/Dynamic/Tasks/SupervisorScopeTests.cs`、`TaskUndoTests.cs`（新）、`tests/DynamicModules.WebAPITests/Dynamic/TasksApiTests.cs`

**規則：**
1. `PATCH`：`CanEdit` 照舊；否則 `CanReassign` 且 patch 只含 `assigneeUserId` 時放行，其他欄位 403（`SupervisorOnlyAssignee`）；凍結 409。
2. 寫入後重新評估操作者可見性；看不到時回 J10 的 `{ id, visible:false, undoToken }`，憑證帶寫入後的版本、原負責人、新負責人、2 分鐘到期。
3. `undo-reassign`：憑證解得開、未過期、`ActorUserId == 我`、`TaskId == id`、目前負責人 == `ToUserId`、目前版本 == `Version`，才把負責人改回 `FromUserId`（走 `TaskWriter.ApplyAsync`，系統記錄與通知照一般改負責人，D3）；任一不符 400 `UndoInvalid`；任務被刪 404；凍結 409。回復原後的詳細（此時應又看得到）。
4. 「部屬的任務」分頁：A32 關或沒有部屬時回空清單（不回 400，前端本來就不顯示分頁）；篩選與排序同其他分頁；本週篩選維持含今天（U17）。
5. `capabilities`：`{ subordinates }`。

**測試：**
- `SupervisorScopeTests`：`Reassign_Allowed_StatusChange403`、`Reassign_WithOtherFields_403_NothingWritten`、`Reassign_NewAssigneeNotified_OldStaysParticipant`、`Reassign_RuleTask_SetsAssigneeOverridden`、`Reassign_FrozenModule_409`、`UnassignedCreatedBySubordinate_Visible`（U3）、`SubordinateOnlyCreator_NotVisible`（部屬只是建立者、負責人是別人時不算）、`SubordinatesTab_MatchesDefinition_AndFilters`、`SubordinatesTab_PolicyOff_Empty`、`Capabilities_TrueOnlyWithSubordinates`。
- `TaskUndoTests`：`ReassignToNonSubordinate_VisibleFalse_TokenIssued_NoTaskContent`（回應 JSON 只有 `id`、`visible`、`undoToken`）、`Undo_RestoresOriginalAssignee`、`Undo_Twice_SecondRejected`、`Undo_ByAnotherUser_400`、`Undo_AfterOtherChange_400`、`Undo_Expired_400`（可調時鐘）、`Undo_TamperedToken_400`、`Reassign_StillVisible_NoToken`。
- WebAPI（`[Trait("Category","Postgres")]`）：`Patch_SupervisorReassign_RoundTrip_Undo`（真正的 Data Protection 實作）、`Tasks_SubordinatesTab_Http`。

**驗收：** `dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~SupervisorScope|FullyQualifiedName~TaskUndo|FullyQualifiedName~TaskWrite"`；`dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~TasksApi"`。

- [ ] 寫失敗測試 → 實作 → 通過 → commit `feat(task): 主管重派部屬任務、移出範圍時可復原、部屬的任務分頁`

### Task 4：Demo 部屬任務資料

**Files:**
- Create: `src/DynamicModules.Application/Dynamic/DemoService/Scenarios/TaskBoardScenario.cs`
- Modify: `DemoService/DemoCatalog.cs`（清單最後加一個）
- Test: `tests/DynamicModules.ApplicationTests/Dynamic/Demo/TaskBoardScenarioTests.cs`（新）

**資料**（全部虛構；日期相對 `plan.Today`，步驟時間在昨天上班時間內；**每個任務由負責人自己建立**，所以不產生「指派給你」通知，不動既有 Demo 斷言）：

| 負責人 | 任務 | 到期 | 狀態 |
|---|---|---|---|
| 業務一 | 整理嵐岳精密報價比較 | 今天 −2 | 進行中 |
| 業務一 | 回覆恆川機械樣品問題 | 今天 | 待辦 |
| 業務一 | 準備週會業績摘要 | 本週最後一天 | 待辦 |
| 業務一 | 更新客戶聯絡人資料 | 今天 +12 | 待辦 |
| 業務一 | 整理名片 | 無 | 待辦 |
| 業務一 | 寄送曜陽電子型錄 | 今天 −3 | 完成（昨天完成） |
| 業務一 | 確認標準導柱庫存（掛在進銷存品項記錄上，`manager01` 讀不到） | 今天 +2 | 待辦 |
| 業務二 | 確認岳峰精機交期 | 今天 −1 | 待辦 |
| 業務二 | 季度目標草稿 | 無 | 進行中 |
| 業務二 | 整理樣品櫃（建立後清空負責人，未指派、建立者是部屬，U3） | 今天 +5 | 待辦 |
| 業務三 | 整理展場報名資料 | 今天 | 待辦 |
| 倉管人員 | 盤點備品倉 | 今天 +1 | 待辦 |

- 今天剛好是本週最後一天時「準備週會業績摘要」會落在「今天」，測試只斷言「到期日＝本週最後一天」，不斷言欄。
- 「整理樣品櫃」：業務二建立並指派自己，再以業務二 PATCH 清空負責人（未指派不通知）。

**測試（`TaskBoardScenarioTests`，用 `DemoGeneratedFixture`）：**
- `PersonalTasks_CreatedByAssignee_NoAssignedNotifications`：上表任務都在、`CreatedBy == 負責人`，這些任務沒有 `TaskAssigned` 通知。
- `Manager01_SubordinatesTab_ListsSalesAndOthers`：以 `manager01` 查「部屬的任務」分頁含上表全部未完成的任務（`manager01` 管總經理室，範圍遞迴到全公司）。
- `Ops01_SubordinatesTab_ListsKeeperOnlyFromThisSet`：`ops01` 看得到「盤點備品倉」，看不到業務的任務。
- `Manager01_InventoryTask_RecordHidden`：「確認標準導柱庫存」在 `manager01` 的清單是「相關記錄（無權限查看）」。

**驗收：** `dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~TaskBoardScenario|FullyQualifiedName~CampaignTaskScenario|FullyQualifiedName~DemoGeneration"`。

- [ ] 寫失敗測試 → 實作 → 通過 → commit `feat(demo): 部屬任務示範資料`

### Task 5：前端「部屬的任務」分頁、主管重派、移出提示與復原；手冊

**Files:**
- Modify: `src/domain/dynamic/task-types.ts`（`TaskListTab` 加 `subordinates`；`canReassign`、`supervised`、`version`；`TaskHiddenAfterWrite`）
- Modify: `src/services/api/task-normalize.ts`、`src/services/api/dynamic-api.ts`（任務區塊：`getTaskCapabilities`、`undoReassign`；`updateTask` 回應可能是 `visible:false`）
- Modify: `src/application/dynamic/queries/tasks-queries.ts`（`useTaskCapabilities`、`useUndoReassign`；`useUpdateTask` 回傳區分兩種結果）
- Create: `src/presentation/dynamic/tasks/use-moved-out-notice.ts`（D3：Quasar Notify 10 秒、「復原」動作、成功／失敗訊息；清單、詳細、看板共用）
- Modify: `src/pages/tasks/MyTasksPage.vue`（第四個分頁，只在 `capabilities.subordinates` 時出現；這個分頁的列多顯示負責人姓名）
- Modify: `src/presentation/dynamic/tasks/TaskDialog.vue`（D5：`supervised && !canEdit` 時只開放負責人、顯示說明；儲存後 `visible:false` 時關閉對話框並叫 `use-moved-out-notice`）
- Modify: i18n 四語（`tasks.tabs.subordinates`、`tasks.supervisorHint`、`tasks.movedOut`、`tasks.undo`、`tasks.undone`、`tasks.undoFailed`）
- Modify: `docs/manual.md` §15（「我的任務」小節：部屬的任務分頁、主管能做什麼、移出範圍與復原；「本週到期」註明＝看板的「今天」＋「本週」，kb3 上線前先寫「本週到期含今天」）
- Modify: `docs/superpowers/specs/2026-11-10-task-management-design.md` §7（看得到任務加第 4 項主管，指向看板設計 §4.4）、政策設計 §4（兩列，編號見共用檔表）
- Test: `MyTasksPage.vitest.spec.ts`、`TaskDialog.vitest.spec.ts`、`use-moved-out-notice.vitest.spec.ts`（新）、`tasks-queries.vitest.spec.ts`、`dynamic-api.tasks.vitest.spec.ts`

**測試：**
- `MyTasksPage shows the subordinates tab only when capabilities say so`、`subordinates tab rows show the assignee name`。
- `TaskDialog for a supervised task enables only the assignee and shows the hint`、`sends only assigneeUserId`、`closes and shows the moved-out notice when visible is false`。
- `moved-out notice calls undo with the token and refreshes tasks`、`shows the failure message when undo returns 400`。
- `updateTask normalizes a visible:false response without task fields`。

**驗收：** `cd poc/frontend && bunx vitest run src/pages/tasks src/presentation/dynamic/tasks src/application/dynamic/queries/tasks-queries.vitest.spec.ts src/services/api/dynamic-api.tasks.vitest.spec.ts src/i18n --maxWorkers=2`；`bun run typecheck`；`bun run build`。

**實機走查**（`bash scripts/unattended/local-demo.sh ../dynamic-fields-design.taskboard`，`http://127.0.0.1:9180`，照 `formwright-demo-verify`，只用一鍵 Demo 登入按鈕）：
1. 業務主管登入 → 任務頁出現「部屬的任務」分頁 → 列出業務一、業務二、業務三、倉管人員的任務；「確認標準導柱庫存」顯示「相關記錄（無權限查看）」；「整理樣品櫃」負責人顯示「未指派」。
2. 點業務一的「整理名片」→ 詳細只有負責人可改、說明文字出現 → 改成「業務二」→ 儲存 → 清單更新，仍在分頁內。
3. 進銷存主管（`ops01`）登入 →「部屬的任務」有倉管人員的「盤點備品倉」→ 改負責人為「業務一」（不是他的部屬）→ 對話框關閉、底部「已移出你的範圍　復原」→ 按「復原」→ 任務回到倉管人員、留言串兩筆改負責人記錄。再做一次、等提示消失 → 任務不在分頁內；最後以系統管理員把它改回倉管人員。
4. 業務一登入 → 沒有「部屬的任務」分頁；系統管理員登入 → 政策頁看得到兩個新政策；把「部門主管可看與重派部屬的任務」關掉 → 業務主管重新整理後分頁消失；改回開。
5. 四寬度：任務頁四個分頁在手機不溢出（分頁列可收成可捲動分頁時要確認 `scrollWidth`）、詳細對話框、移出提示。結果寫進 `walk/kb1.md`，各項分開宣稱。

- [ ] 寫失敗測試 → 實作 → 通過 → 走查 → commit `feat(task): 任務頁部屬的任務分頁、主管重派與復原提示`
- [ ] kb1 審查、合併（主控）；`docs/feature-checklist.md` kb1 打勾、roadmap 第 44 列寫「kb1 已上線」

---

## kb2：看板資料表與 API

### Task 6：`TaskViews` 資料表與檢視、分享 API

**Files:**
- Create: `src/DynamicModules.Domain/Entities/Dynamic/TaskView.cs`（`TaskView`、`TaskViewShare`、兩個 `IEntityTypeConfiguration`；表名 `Dynamic_TaskView`、`Dynamic_TaskViewShare`；`TaskViewShare` 主鍵 `(ViewId, TargetKind, TargetId)`、隨 `TaskView` cascade 刪除；索引 `OwnerUserId`、`(TargetKind, TargetId)`）
- Create: `src/DynamicModules.Domain/Repositories/Dynamic/ITaskViewRepository.cs`、`src/DynamicModules.Persistence/Repositories/Dynamic/TaskViewRepository.cs`
- Create: `src/DynamicModules.Application/Dynamic/TaskViewService/`（`TaskViewRequests.cs`、`TaskViewHandlers.cs`、`TaskViewAccess.cs`、`TaskViewMessages.cs`、`TaskViewModels.cs`）
- Create: `src/DynamicModules.WebAPI/Controllers/v3/Dynamic/TaskViewsController.cs`
- Modify: `src/DynamicModules.Persistence/Repositories/Dynamic/AccountRepository.cs`（`DeleteGroupAsync` 清 `group` 分享）
- Modify: `src/DynamicModules.Application/Dynamic/OrganizationService/OrganizationHandlers.cs`（`DeleteDepartmentHandler` 清 `department` 分享）
- Modify: `DependencyInjection.cs`（任務區塊後）、Persistence 的 repository 註冊
- Create（最後一步）: migration `TaskViews`（產生方式見共用檔表）
- Test: `tests/DynamicModules.ApplicationTests/Dynamic/Tasks/TaskViewTests.cs`、`TaskViewShareTests.cs`（新）

**介面（API，路由在 `api/v3/dynamic`）：** `GET task-views?kind=board`（`{ mine[], shared[] }`，shared 帶擁有者姓名；管理員的 shared 含所有有分享的）、`POST task-views`、`GET/PUT/DELETE task-views/{id}`（PUT 帶 `rowVersion`，不符 409）、`POST task-views/{id}/copy`（複製設定、不複製分享，名稱加「（副本）」，超過 100 字截斷）、`PUT task-views/{id}/shares`（`[{ kind, id }]`，整批取代）、`GET task-views/share-targets`（啟用中的使用者、啟用中的部門、群組；各帶 Id 與名稱）。`ConfigJson` 在 Task 7 才驗證語意，這裡先接 `TaskBoardConfig.Parse`（Task 7 與本 Task 同段，先後在同一分支）。

**規則：** 建立者才能改、改分享、刪除；管理員可刪除有分享的看板、不能改；被分享者只能讀與複製（§6.2）；`Kind` 這段只收 `board`；名稱 1–100 字；分享最多 200 個、對象必須存在（使用者存在即可，停用者也可留著；部門、群組存在）；刪除群組、部門時清分享。

**測試：**
- `TaskViewTests`：`Create_Read_Update_Delete_ByOwner`、`Update_StaleRowVersion_409`、`NonOwner_Update403_Delete403`、`Admin_CanDeleteShared_CannotUpdate`、`Admin_CannotDeleteUnsharedOfOthers`（同報表：看不到就是 404）、`Copy_ByShared_NewOwner_NoShares`、`Name_BlankOrOver100_400`、`Kind_Gantt_400_ForNow`、`Config_UnknownProperty_400`。
- `TaskViewShareTests`：`ShareToUser_Group_Department_EachSees`、`ShareToDepartment_DirectMembersOnly`（子部門成員看不到）、`UserMovesDepartment_ShareFollows`、`DeleteGroup_RemovesBoardShares`、`DeleteDepartment_RemovesBoardShares`、`Shares_Over200_400`、`ShareTargets_ListsActiveUsersDepartmentsGroups`、`Owner_Deactivated_ViewAndSharesRemain`。

**驗收：** `dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~TaskView|FullyQualifiedName~Department|FullyQualifiedName~DeleteGroup"`。

- [ ] 寫失敗測試 → 實作 → 通過 → commit `feat(task): 任務檢視資料表與看板存檔、分享 API`（migration 在 Task 7 結束後、合併前另一個 commit `feat(task): TaskViews migration`）

### Task 7：看板設定、到期區間、欄查詢與移動 API

**Files:**
- Create: `src/DynamicModules.Domain/Tasks/TaskBoardConfig.cs`（J6：解析、驗證、預設看板 `TaskBoardConfig.Default`＝依狀態、指派給我、隱藏「取消」欄）
- Create: `src/DynamicModules.Domain/Tasks/TaskDates.cs`、`TaskDueBuckets.cs`（J7）
- Modify: `TaskService/TaskHandlers.cs`（`GetMyTasksHandler` 改呼叫 `TaskDates.WeekEnd`，刪掉 `internal static WeekEnd`，修既有呼叫點）
- Create: `TaskService/TaskBoardHandlers.cs`、`TaskBoardRequests.cs`、`TaskBoardModels.cs`（`TaskCardResponse`、`TaskBoardColumnResponse`、`TaskBoardResponse`、`TaskMoveResponse`）
- Modify: `ITaskRepositories.cs`、`TaskRepositories.cs`（`QueryBoardColumnAsync`、`CountBoardColumnsAsync`：可見條件＋範圍＋篩選＋欄條件＋排序＋分頁；留言數用子查詢投影）
- Create: `src/DynamicModules.WebAPI/Controllers/v3/Dynamic/TaskBoardController.cs`
- Test: `tests/DynamicModules.DomainTests/Tasks/TaskBoardConfigTests.cs`、`TaskDueBucketsTests.cs`；`tests/DynamicModules.ApplicationTests/Dynamic/Tasks/TaskBoardQueryTests.cs`、`TaskMoveTests.cs`；`tests/DynamicModules.WebAPITests/Dynamic/TaskBoardApiTests.cs`（新）

**介面：**
- `POST task-board/columns`：`{ viewId? , config? }`（二擇一；`viewId` 要看得到該看板）→ `{ today, weekEnd, laterDefault, scopeAvailable, columns: [{ key, kind, userId?, name?, inactive?, total, cards[], hasMore, truncated, droppable, dropReason? }], omittedAssignees }`。欄鍵：狀態 `todo|inProgress|done|cancelled`；到期 `overdue|today|thisWeek|later|none`；負責人 `user:<id>|unassigned`。
- `POST task-board/columns/{column}/page`：`{ viewId?|config?, offset }` → 同一欄的下一頁 50 張；`offset >= 500` 回空並帶 `truncated`。
- `POST tasks/{id}/move`：`{ groupBy, toColumn, dueDate?, expectedVersion }` → `TaskMoveResponse`（J10）。
- `TaskCardResponse`：`id, title, status, assigneeUserId, assigneeName, assigneeActive, dueDate, overdue, dueToday, record (TaskRecordRef), fromRule, frozen, commentCount, completedTime, version, canEdit, canReassign`。

**規則：**
1. 範圍：`assigned`＝負責人是我；`involved`＝我是參與者；`subordinates`＝部屬任務（A32 關或沒部屬時欄照列、全部 0 筆、`scopeAvailable=false`，D7）；`visible`＝`TaskVisibility`。所有範圍都再 AND 可見條件（縱深防禦）。
2. 到期區間看板固定只取 `Todo`、`InProgress`，忽略設定裡的狀態篩選。
3. 欄內排序照設定；「完成」欄固定 `CompletedTime` 新到舊；到期日排序空的排最後；最後依 `Id` 穩定排序。
4. 負責人欄：明列的人（含停用，標 `inactive`、`droppable=false`）＋「未指派」＋（`autoAppendAssignees`）篩選結果裡有任務的其他人，依任務數多到少補到合計 50 欄；範圍是部屬時自動列出全部部屬（T6），也計入 50 欄。
5. 移動：
   - 版本不符 409（`TaskMessages.Stale`「任務已被別人修改，已重新整理」）；看不到 404；凍結 409。
   - 狀態、到期區間要 `CanEdit`；負責人要 `CanEdit || CanReassign`；否則 403。
   - 到期目標：`overdue` 400；`today`＝今天；`thisWeek`＝本週最後一天（今天就是本週最後一天時 400）；`later` 必須帶 `dueDate` 且晚於本週最後一天，否則 400；`none` 清空。同欄不改值、回 200 原卡片。
   - 負責人目標必須啟用中，否則 400；`unassigned` 清空。
   - 值的改變一律走 `TaskWriter.ApplyAsync`（覆寫旗標、系統記錄、通知與 PATCH 相同，§5.5、T7）。
   - 寫入後重算可見性；看不到時發 undo 憑證（kb1）。
6. 卡片的記錄資訊用 kb1 的批次評估；看不到記錄時 `record.accessible=false`、其餘 null。

**測試：**
- Domain `TaskDueBucketsTests`：`Buckets_ForEveryWeekStart`（Theory 七種起始日，各自檢查逾期、今天、本週、之後、無）、`TodayIsWeekEnd_ThisWeekEmpty_TargetRejected`、`LaterDefault_IsNextWeekEnd`、`WeekEnd_SameAsBefore`（搬家前後結果相同）。
- Domain `TaskBoardConfigTests`：`Default_IsStatusAssignedHideCancelled`、`Parse_UnknownProperty_Rejected`、`Read_UnknownProperty_Ignored`、`Rejects_BadGroupByScopeSort`、`Rejects_Over50ExplicitAssignees`、`Rejects_KeywordOver100`、`DueBucket_IgnoresStatusFilter`。
- `TaskBoardQueryTests`：`StatusBoard_ColumnsAndTotals`、`AssigneeBoard_ExplicitInactiveAutoAppend_Capped50`、`SubordinateScope_ListsAllSubordinateColumns`、`SubordinateScope_NonManager_EmptyWithFlag`（D7）、`DueBoard_OnlyOpen`、`ListThisWeek_EqualsTodayPlusThisWeek_ForEveryWeekStart`、`Paging_50_UpTo500_ThenTruncated`、`Card_RecordNoAccess_NoModuleKeyOrTitle`、`SharedBoard_EachViewerSeesOwnScope`（同一個 `viewId`，`manager01` 與 `sales01` 結果不同、`sales01` 的每一張都在他自己的 `Visible` 內）、`Config_ReferencesInactiveUser_StillLoads`、`QueryCount_DependsOnColumnsNotCards`。
- `TaskMoveTests`：`Status_ToDone_SetsCompleted_FromDone_Clears`、`Status_Supervisor_403`、`Assignee_Supervisor_Allowed`、`Assignee_ToInactive_400`、`Assignee_RuleTask_SetsAssigneeOverridden`、`Due_Today_ThisWeek_Later_None_Values`、`Due_ToOverdue_400`、`Move_ToThisWeek_WhenTodayIsWeekEnd_400`、`Due_LaterNotAfterWeekEnd_400`、`Due_ClearRuleTask_SetsDueOverridden_RuleNoLongerSyncs`（之後改來源記錄的日期，任務到期日不變）、`Move_StaleVersion_409_NothingWritten`、`Move_Frozen_409`、`Move_SameColumn_NoChange`、`Move_ToNonSubordinate_VisibleFalse_Undo`、`Move_WritesSameSystemEventAsPatch`。
- WebAPI（`[Trait("Category","Postgres")]`）：`Columns_RoundTrip_SavedAndInline`、`MoveAndMove_Concurrent_OneWins`（同一張卡同時送兩個移動，一個 200、一個 409）、`TaskViews_Crud_Http`。

**驗收：** `dotnet test tests/DynamicModules.DomainTests --filter "FullyQualifiedName~TaskBoardConfig|FullyQualifiedName~TaskDueBuckets"`；`dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~TaskBoard|FullyQualifiedName~TaskMove|FullyQualifiedName~TaskQuery"`，同一組再跑一次 `TZ=UTC`；`dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~TaskBoardApi|FullyQualifiedName~TasksApi"`。migration 產生後跑一次 `--filter "FullyQualifiedName~SeedData|FullyQualifiedName~Migration"`（Task 0 確認既有 migration 測試名）。

- [ ] 寫失敗測試 → 實作 → 通過 → commit `feat(task): 看板欄查詢與移動 API`
- [ ] rebase 到最新 `origin/main` → 產生 `TaskViews` migration → 跑上面的驗收 → commit `feat(task): TaskViews migration`
- [ ] 文件：看板設計 §6.1 補 `ConfigJson` 的實際欄位名與升版規則（§21 留給實作者的兩題）、§21「欄查詢一次回全部欄」寫回；roadmap 第 44 列。
- [ ] kb2 審查、合併（主控）

---

## kb3：看板畫面、自訂看板與分享

### Task 8：任務頁切換、看板骨架、卡片、分段平移

**Files:**
- Create: `src/domain/dynamic/task-board-types.ts`（型別；純函式 `columnsPerScreen(width)`、`visibleRange(page, perScreen, count)`、`moveTargets(card, columns, groupBy)` 回每欄可不可放與原因）
- Modify: `src/services/api/dynamic-api.ts`（任務區塊：`getBoardColumns`、`getBoardColumnPage`、`moveTask`、檢視 CRUD、分享、分享對象）
- Create: `src/application/dynamic/queries/task-board-queries.ts`（`useBoardColumns`、`useLoadMoreColumn`、`useTaskViews`；`invalidateTasks` 一併失效 `['task-board']`）
- Create: `src/application/dynamic/last-task-board.ts`（照 `last-dashboard.ts`，鍵 `taskBoard.last.<userId>`，try/catch）
- Create: `src/presentation/tasks/board/TaskBoard.vue`、`TaskBoardColumn.vue`、`TaskCard.vue`、`BoardColumnPager.vue`（D1，手機是欄晶片）
- Modify: `src/pages/tasks/MyTasksPage.vue`（標題「任務」、「清單／看板」切換；網址 `view=board&board=<id>`；看板模式顯示看板選擇器位置，Task 10 填內容）
- Modify: i18n 四語（`menu.myTasks` 與 `tasks.myTitle` 的值改「任務」／Tasks／…，`taskBoard.*` 欄名、欄頭數量、載入更多、還有 N 筆、未儲存、平移按鈕）
- Test: `task-board-types.vitest.spec.ts`、`last-task-board.vitest.spec.ts`、`TaskBoard.vitest.spec.ts`、`TaskCard.vitest.spec.ts`、`MyTasksPage.vitest.spec.ts`、`MainLayout.vitest.spec.ts`

**行為：**
- 卡片（§5.9）：標題兩行截斷、到期日（逾期 `negative`、今天 `warning` 語意色）、負責人（依負責人分欄時省略）、狀態標籤（依狀態分欄時省略）、相關記錄或「相關記錄（無權限查看）」、規則圖示、凍結圖示、留言數；點卡片開既有 `TaskDialog`（`?open=<id>`，與清單同一套），對話框改完看板就地更新。
- 欄頭：欄名＋總數；停用者欄名加「（已停用）」；`truncated` 時欄底「還有 N 筆，請加上篩選或改用清單」。
- 預設看板：沒有 `board` 參數、或選「預設看板」時，用後端的 `TaskBoardConfig.Default`（前端不寫一份預設值：`POST task-board/columns` 不帶設定時就是預設）。
- D6：`board` 讀不到時回預設看板並提示，清掉上次記憶。

**測試：**
- `columnsPerScreen gives 1 on phone and fits four status columns at 1366`、`visibleRange clamps at the end`、`moveTargets marks overdue and thisWeek-on-weekend as not droppable with a reason`。
- `last-task-board is stored per user and ignores broken content`。
- `TaskBoard renders columns with totals and load-more`、`shows the truncated hint`、`phone shows column chips and one column`、`pager moves by one screen`。
- `TaskCard hides the assignee on an assignee board and the status on a status board`、`shows the no-access record text`。
- `MyTasksPage keeps view and board in the url`、`falls back to the default board with a notice when the board is gone`；`MainLayout menu says Tasks`。

**驗收：** `bunx vitest run src/domain/dynamic/task-board-types.vitest.spec.ts src/application/dynamic src/presentation/tasks src/pages/tasks src/layouts src/i18n --maxWorkers=2`；`bun run typecheck`；`bun run build`。

- [ ] 寫失敗測試 → 實作 → 通過 → commit `feat(task): 任務頁清單／看板切換與看板顯示`

### Task 9：拖拉、「移到…」、日期選擇、取消確認、樂觀更新

**Files:**
- Create: `src/presentation/tasks/board/use-card-drag.ts`（J9；`hitTest` 可注入；`mode: 'mouse' | 'longPress' | 'off'` 依寬度與 `pointerType` 決定）
- Create: `src/presentation/tasks/board/MoveToMenu.vue`（桌機 `q-menu`，手機底部全寬對話框）
- Create: `src/presentation/tasks/board/DuePickDialog.vue`（「之後」：預設 `laterDefault`、只能選 `weekEnd` 之後；取消就放回）
- Modify: `TaskBoard.vue`、`TaskCard.vue`、`task-board-queries.ts`（`useMoveTask`：`onMutate` 先把卡片搬到目標欄、`onError` 放回並顯示原因；409 重抓看板；`visible:false` 叫 kb1 的 `use-moved-out-notice`）
- Modify: i18n 四語（拖到取消的確認、無到期日的規則提示、禁止放置原因、移動成功的 `aria-live` 文字、移到…）
- Test: `use-card-drag.vitest.spec.ts`、`MoveToMenu.vitest.spec.ts`、`DuePickDialog.vitest.spec.ts`、`TaskBoard.vitest.spec.ts`

**行為：**
- 能不能拖：狀態與到期區間看板看 `canEdit`，負責人看板看 `canEdit || canReassign`；凍結不能拖。不能拖的卡片游標 `not-allowed`、沒有「移到…」按鈕。
- 拖到「取消」：確認框「取消這個任務？規則產生的任務取消後不會自動恢復」（U5）。
- 拖到「無到期日」且是規則任務：確認框多一行「清空後不再隨記錄日期同步」（U5）。
- 拖到「之後」：開 `DuePickDialog`；取消就放回原欄、不送請求。
- 拖到不能放的欄：欄顯示禁止樣式與原因（「今天已是本週最後一天」、「不能改成過去的日期，請開任務詳細」、「這位使用者已停用」）。
- 拖拉期間 Esc 取消；放開時以 `aria-live` 念「已移到「進行中」」或失敗原因。

**測試：**
- `use-card-drag starts on mouse move past threshold`、`starts on touch only after long press`、`is off on phone width`、`reports the column under the pointer via hitTest`、`Esc cancels`。
- `MoveToMenu lists droppable columns and disables the rest with reasons`、`is keyboard operable`（Enter、方向鍵）。
- `DuePickDialog defaults to laterDefault and rejects dates up to weekEnd`。
- `TaskBoard moves optimistically and rolls back on 400`、`409 refetches and shows the stale message`、`visible false shows the moved-out notice`、`cancel column asks for confirmation`、`clearing a rule task due date shows the sync warning`、`non-draggable card has no move-to button`。

**驗收：** `bunx vitest run src/presentation/tasks src/application/dynamic/queries/task-board-queries.vitest.spec.ts src/i18n --maxWorkers=2`；`bun run typecheck`；`bun run build`。

- [ ] 寫失敗測試 → 實作 → 通過 → commit `feat(task): 看板拖拉與移到選單`

### Task 10：自訂看板、看板選擇器、分享對話框、Demo 看板、文件總收

**Files:**
- Create: `src/presentation/tasks/board/BoardPicker.vue`（預設看板、我的看板、分享給我的（帶建立者姓名）、另存為新看板；管理員在「分享給我的」看得到所有有分享的看板，只有刪除）
- Create: `src/presentation/tasks/board/BoardSettingsDialog.vue`（D4：分欄依據、範圍（`capabilities.subordinates` 為 false 時沒有「部屬的任務」）、篩選、欄順序與隱藏（上下移按鈕，不拖）、欄內排序、明列的負責人與「自動附加其他人」；「套用」「儲存」「另存為新看板」）
- Create: `src/presentation/tasks/board/TaskViewShareDialog.vue`（沿用 `reports/ShareDialog.vue` 版面，使用者／部門／群組三個分頁；部門分頁註明「只含直屬成員」）
- Modify: `TaskBoard.vue`、`MyTasksPage.vue`、`task-board-queries.ts`（存、另存、刪、分享）
- Modify: `DemoService/Scenarios/TaskBoardScenario.cs`（`manager01` 建兩個看板，見下）、`tests/.../Demo/TaskBoardScenarioTests.cs`
- Modify: i18n 四語；`docs/manual.md` §15 新增「看板」小節（三種分欄、拖拉與移到…、裝置差異、自訂與分享、預設看板、「本週到期＝看板的今天＋本週」）；看板設計 §5.8 與 §17 補 D1–D7 的裁決；`docs/feature-checklist.md`（kb2 那行改寫成看板並打勾、統計重算）；`docs/roadmap.md` 第 44 列
- Test: `BoardPicker.vitest.spec.ts`、`BoardSettingsDialog.vitest.spec.ts`、`TaskViewShareDialog.vitest.spec.ts`

**Demo 看板**（`manager01` 建立，虛構）：
- 「業務部負載」：依負責人、範圍「部屬的任務」、明列業務一、業務二、業務三、自動附加開；不分享。
- 「業務部任務」：依狀態、範圍「全部看得到的」、篩選負責人＝業務一、業務二、業務三；分享給部門「業務部」。
- 測試 `Manager01_HasTwoBoards_OneSharedToSalesDepartment`、`Sales01_SeesSharedBoard_OnlyOwnVisibleTasks`。

**測試：**
- `BoardPicker groups mine and shared, shows the owner`、`admin sees delete only on others' shared boards`、`save as new board posts the current config`。
- `BoardSettingsDialog hides the subordinates scope without capability`、`apply does not save`、`save sends rowVersion and marks clean`、`shared board offers only apply and save-as`、`moves columns up and down`。
- `TaskViewShareDialog sends user, department and group targets`、`shows the direct-members note on the department tab`。

**驗收：** `bunx vitest run src/presentation/tasks src/pages/tasks src/application/dynamic src/i18n --maxWorkers=2`；`bun run typecheck`；`bun run build`；`dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~TaskBoardScenario"`。

**實機走查**（同 kb1 的啟動方式；`walk/kb3.md`）：
1. 業務一登入 → 任務頁 →「看板」→ 預設看板：待辦、進行中、完成三欄（沒有取消欄）；拖「回覆恆川機械樣品問題」到「進行中」→ 卡片移過去、欄頭數量更新；開詳細，留言串有「改狀態」系統記錄。
2. 看板設定 → 分欄依據改「到期區間」→ 套用 → 欄：逾期、今天、本週、之後、無到期日；「逾期」欄拖入被擋並顯示原因；拖「整理名片」到「之後」→ 日期選擇預設下週最後一天 → 確定 → 卡片到「之後」；再拖一張到「之後」按取消 → 放回原欄。若今天是本週最後一天，確認「本週」欄禁止放置的提示。
3. 另存為新看板「我的到期」→ 重新整理仍在這個看板（網址帶 `board`）。
4. 業務主管登入 → 看板選擇器有「業務部負載」「業務部任務」→ 打開「業務部負載」：業務一、業務二、業務三、未指派加自動附加的其他部屬欄；平移按鈕在 1366 出現；把業務二的「季度目標草稿」拖到業務三 → 成功，業務三收到「指派給你」；切到依狀態分欄時，這張卡不能拖、沒有「移到…」（主管只能改負責人）。移出範圍的情境：進銷存主管登入，建一個依負責人、範圍「部屬的任務」的看板，明列倉管人員與業務一 → 把「盤點備品倉」拖到業務一 → 卡片消失、「已移出你的範圍　復原」→ 復原（`manager01` 管總經理室，全公司都是他的部屬，看不到這個情境）。
5. 業務主管把「業務部負載」分享給業務部 → 業務一打開：空欄＋「你目前沒有部屬」提示（D7）；打開「業務部任務」：只看到自己看得到的任務（業務二的個人任務不在）。
6. 業務主管刪除「業務部任務」→ 業務一點舊網址 → 回預設看板並提示（D6）。
7. 鍵盤：Tab 到卡片的「移到…」→ Enter → 方向鍵選欄 → Enter；螢幕閱讀器文字（用 devtools 看 `aria-live` 節點內容）。
8. 四寬度：390（欄晶片、只有「移到…」、底部全寬選單）、820（長按拖拉、分段平移）、1366、1920；每個寬度跑 `rwd-check.js`，看板容器與對話框 `scrollWidth <= clientWidth`。觸控長按用 devtools 的觸控模擬確認一次。
9. 系統管理員登入 → 看板選擇器「分享給我的」看得到業務主管分享的看板、只有刪除。
各項分開宣稱；沒做到的標「未驗證」。

- [ ] 寫失敗測試 → 實作 → 通過 → 走查 → commit `feat(task): 自訂看板、看板分享與示範看板`
- [ ] 文件總收 → commit `docs(task): 看板手冊、設計裁決寫回、功能清單與路線`
- [ ] kb3 審查、合併（主控）

---

## 驗收總表

| 項目 | 指令或步驟 | 段 |
|---|---|---|
| 相關後端測試類別 | 各 Task「驗收」的 `dotnet test --filter` | kb1、kb2、kb3 |
| 日期邊界在 UTC 主機 | Task 7 的 filter 加 `TZ=UTC` | kb2 |
| 相關前端測試檔 | 各 Task 的 `bunx vitest run <檔> --maxWorkers=2` | kb1、kb3 |
| 前端型別與建置 | `bun run typecheck`、`bun run build`（本機） | kb1、kb3（kb2 只動後端，但若改了 `dynamic-api.ts` 也要跑） |
| 完整套件 | 推上分支後 GitHub Actions `gate.yml`（`scripts/unattended/ci-wait.sh` 等結果） | 每段 |
| 宗旨檢查 | `formwright-purpose-check` 逐項＋寫死掃描 | 每段 |
| 本機四寬度走查 | kb1 Task 5、kb3 Task 10 | kb1、kb3 |
| Demo 站走查 | 上線後照 kb3 走查第 1、4、5 步（`formwright-ship`） | kb3 |

本機測試、CI、commit、合併、上線、Demo 站走查各自回報，沒跑到的標「未驗證」。

---

## 與 kr、st、gt 的銜接

- **kr1**：任務資料來源的「列的範圍」直接用 kb1 的 `TaskVisibilityService`＋`TaskRepository.Visible`；記錄相關格子的 `restricted` 用 kb1 的批次 stub 評估。kr 不依賴 kb2、kb3。
- **st1、st2**：可見性第 5 項（看得到父任務就看得到子任務）加在 `TaskVisibility` 的 OR 條件裡；`TaskBoardConfig` 加選填 `showSubtasks`（預設 true，不升版本，J6）；卡片加進度欄位；移動到「完成」「取消」時的連動確認接在 kb3 的確認框。
- **gt1、gt2**：`Dynamic_TaskView.Kind` 開放 `gantt`；甘特設定是另一個 Domain 類別，分享、另存、權限沿用 `TaskViewService`；改到期日早於開始日時的連動接在 `TaskMove` 的到期分支（U12）。
- **任務版本**：st、gt 的寫入也經 `TaskWriter`，`ModifiedTime` 照常更新，`version` 不用另做。

---

## 與設計文件矛盾或需要讀成別的意思之處

1. **政策編號撞號**：設計 A32、A33（與 st 的 A34）和 a3 計畫的 A32、A33 撞號。程式只認代碼（`task.supervisorScope`、`task.supervisorScopeRecursive`），編號由後合併的一方順延（共用檔表）。
2. **migration 兩份**：設計 §16 寫 SQLite 與 PostgreSQL 各一份；目前 repo 只有 SQLite 的 migration（pg0b 才建 PostgreSQL 目錄）。kb2 只產生 SQLite 一份，pg0b 一併產生 PostgreSQL 版。
3. **PATCH 的回應形狀**：§12 說 PATCH「同樣回 `visible` 與 `undoToken`」；本計畫看得到時維持原本的詳細回應、看不到時只回 `{ id, visible:false, undoToken }`（J10），不在看不到之後還回任務內容。
4. **功能清單的段號**：清單把「任務看板」整行標成 kb2；kb2 沒有畫面，看板在 kb3 才上線。kb3 收尾時把那一行改成「（段 kb2、kb3）」再打勾。
5. **復原的通知**：U16 沒說復原時通知怎麼處理；本計畫照一般改負責人（D3）。
6. **「本週最後一天」的位置**：T5 說看板與清單共用 `WeekEnd`；它現在是 `GetMyTasksHandler` 的 `internal static`，kb2 搬到 Domain `TaskDates`（J7）。

## 讀程式碼時發現的問題（不在設計文件裡）

1. `TaskAccess.EvaluateManyAsync` 在迴圈內逐筆查記錄 stub（設計 §3 已知），kb1 Task 2 修。
2. `GetMyTasksHandler` 的錯誤訊息「分頁不正確」「篩選不正確」直接寫在 handler，沒放進 `TaskMessages.cs`（違反 backend 規則的訊息集中）；kb1 Task 3 會改這個 handler，**順手不改**，列進 `docs/todo.md`。
3. 新任務的 `ModifiedTime` 是 null（`TaskWriter.Create` 只設 `CreatedTime`），所以版本要用 `ModifiedTime ?? CreatedTime`（J4）。
4. 任務可見性與報表、看板會共用，但目前沒有任何「查詢次數」測試工具；kb1 Task 2 在測試專案加 `CountingCommandInterceptor`（若 Task 0 找到既有的就沿用）。

## 修訂紀錄

- 2026-11-14：初版（主控計畫）。Task 0 核對結果寫在這裡。
