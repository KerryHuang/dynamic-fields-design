# 簽核 a4a：簽核設計器 Implementation Plan

> **修訂紀錄：** Task 0 核對完成（2026-11-13）：origin/main 仍是 `3f0849d0`；引用的型別與檔案名稱無出入；`ApprovalDefinitionValidator` 建構子改注入 `ApprovalEditableFields`（Task 2）；試算草稿的 `PreviewAsync` 參數改為帶 Key 的 `flowSet`（Task 3，用來回報 `FlowKey`）。

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 設計者在模組設計頁就能把簽核設好，不必再呼叫 API：狀態機分頁的轉移可以設「需簽核」與「條件都不符合時」的處理，狀態可以標「可編輯（退回重送）」，並看得到簽核鎖定狀態與鎖定欄位；新分頁「簽核流程」依目標狀態列出流程卡片，可以啟停、調整優先序、刪除沒用過的流程；流程編輯對話框分「基本／關卡／試算」三段，上限與預設值一律讀政策生效值；390 寬全螢幕、不橫捲。順手修掉 a2d7 留下的收件人 chip 誤報。

**Architecture:** 後端只補四個缺口，都沒有 migration：流程回應加「目前版本是否已被用過」並讓只改啟停、名稱、優先序的存檔不產生新版本；狀態機回應加「可當可編輯欄位的候選欄位」；新增「試算編輯中的流程」端點（不寫入），設計器試算回報命中的流程鍵與每關的解析問題。前端照既有分層：`domain/dynamic/approval-flow-types.ts`、`approval-flow-draft.ts`（純函式）→ `services/api/dynamic-api.ts` → `application/dynamic/queries/approval-flow-queries.ts` → `presentation/dynamic/approval-designer/*`（分頁、對話框、關卡、簽核人來源、試算）。狀態機分頁沿用 `WorkflowTab.vue` 擴充，流程對話框沿用 `ConversionEditorDialog` 與 `RuleEditorDialog` 的寫法，條件用既有 `ConditionEditor`，政策值走 `useEffectivePolicies` 與 `useModulePolicies`，收件人名稱走 `useRecipientNames`。

**Tech Stack:** .NET 8、EF Core（只驗 SQLite，PostgreSQL 等 pg0 合併後補驗）、MediatR、xUnit＋FluentAssertions；Vue 3、Quasar 2、TanStack Query、vue-i18n、vitest、bun。不新增相依套件（桌機拖曳用原生 HTML5 drag）。

**Spec:**
- `docs/superpowers/specs/2026-11-10-approval-workflow-design.md`：§13.1（本段範圍）；§4.3、§4.4（定義與版本）、§5.2（入口約束、`noMatch`、`direct` 說明文字）、§6.1（來源與主管層級上限讀政策）、§6.5（備援、`onEmpty`）、§8.6（進行中實例守衛）、§9.2（可編輯欄位限制）、§9.3（鎖定狀態、鎖定欄位、「核准之後建議唯讀」提示）、§10.4（設計器權限）、§20.4（第 3、9、11、16、27、37、38 條）。
- `docs/superpowers/specs/2026-11-11-policy-settings-design.md`：§3.5（改嚴只管之後）、§3.6（前端取用生效值）、§4 A3、A10、§10 可寫死清單。
- a2d 計畫 `../.tmp/plans/20261112-approval-a2d/plan.md`：判斷 15–17、21，政策登記表，可寫死清單，使用者裁決 1–17；a2d7 重審 `a2d7-rereview.md` 的 N-M1、N-M2。
- a4b 計畫 `../.tmp/plans/20261112-approval-a4b/plan.md`：J9（設計器存檔保留 `approval`／`editable`，a4a 加介面）、J23（預覽時間軸的呈現）。
- repo 規範：`AGENTS.md`、`.claude/rules/frontend.md`、`backend.md`、`docs.md`、`docs/ai/testing.md`。

**路徑慣例：** 後端相對 `poc/backend/`，前端相對 `poc/frontend/`，文件與腳本相對 repo 根目錄。worktree：`../dynamic-fields-design.a4a`（分支 `feature/a4a`，已建）。行號以 main `3f0849d0` 為準，Task 0 重新核對。

---

## Global Constraints

- **業務政策不寫死**（鐵則 2，已拍板）：關卡數 `approval.maxStages`、每關人數 `approval.maxApprovers`、逾期時數 `approval.maxDueHours`、主管層數 `approval.maxManagerLevel` 一律讀 `useEffectivePolicies(moduleKey)` 的模組生效值；技術上限（每關 50、主管 10）讀 `useModulePolicies(moduleKey)` 該政策的 `meta.max`，前端不寫數字。讀不到時不顯示數字、不限制輸入，由後端 400 把關。預設備援讀 `useModulePolicies` 的 `approval.defaultFallbackApprovers` 生效值（生效值 API 不含收件人類，a2d 判斷 21）。
- **改嚴只管之後**：既有關卡的值超過目前上限時照樣顯示、可存；選單保留舊值並標「沿用舊值」；只有新增或加多才受限（政策設計 §3.5、a2d 判斷 16）。
- **提示文字**（與已拍板規則一致，四語同義）：
  - 固定名單（指定使用者）：「存檔時最多 {max} 位（模組政策）。名單存檔後，執行時只受技術上限 {tech} 位限制。」
  - 群組、主管、使用者欄位：「每一關啟動時，解析出的人數不能超過當時的每關人數上限（目前 {max} 位）。」
  - 發起人自選：「最多人數不能超過 {max}。送出時實際上限取這裡的最多人數與當時政策上限的較小值；最少人數比實際上限大時，改用實際上限。」
  - 備援：「備援簽核人頂替找不到人的來源時，不計入每關人數上限；整關合計（含備援，去重後）不超過 {tech} 位。」
  - `noMatch = direct`：「條件都不符合的單據會直接轉移，不經任何人同意」（§5.2 原文）。
  - 版本：「儲存會產生第 {next} 版，進行中的 {n} 張單照舊版本走完」；n 為 0 時「儲存會產生第 {next} 版，已送出的單不受影響」。新增、調整優先序、停用：「只影響之後送出的單」（§4.4）。
  - 狀態清單：「核准之後的狀態，建議設成唯讀狀態」（§9.3）。
- **關卡代碼不從名稱產生**（a2d6 審查）：改名稱永遠不動代碼。
- **i18n**：新句子放 `approval.designer.*`，狀態機分頁沿用 `docflow.workflow.*`；四語鍵集合一致、單行 JSON；含 `{`、`}`、`@`、`|` 用字面量；英文不留中文。改 i18n 前從當時的 main 重讀檔案。後端設計器驗證訊息目前只有繁中（多語系 #21），前端照原文顯示，不另翻。
- **RWD**：390、820、1366、1920 四寬度不橫捲（`scripts/unattended/rwd-check.js`；分頁切換後另以 `document.documentElement.scrollWidth <= innerWidth` 量）；`$q.screen.lt.sm` 時對話框 `maximized`、主要按鈕固定底部；可捲動容器裡的 `column` 加 `no-wrap`。
- **無障礙**：輸入框都有 `label` 或 `aria-label`；只有圖示的按鈕有 `aria-label`；可點的卡片 `tabindex="0"`、Enter／Space 可開；拖曳一定有上移／下移按鈕替代。
- **顏色**：語意 token 或 Quasar 品牌色名，`no-hardcoded-colors.vitest.spec.ts` 要過。
- **後端**：新測試類 `protected override bool ScopePerRequest => true;`；新 WebAPI 測試標 `[Trait("Category", "Postgres")]`；不寫 SQLite 專屬語法；新訊息進 `ApprovalMessages.cs`。
- **測試**：只跑窄範圍；vitest 一律 `--maxWorkers=2`；改完跑 `bun run typecheck`；改 i18n 跑 `src/i18n`。gate 一次一個。
- **git**：逐檔 `git add`、commit 前 `git status`；不用 `--no-verify`、不 force push、不 merge commit；Conventional Commits，scope `approval`（N-M1 用 `policy`）；不 push，由主控合併。公開 repo 不寫公司字串、個人絕對路徑、內網 IP；走查腳本放計畫資料夾，不進 repo。

### 可寫死清單（a4a 新增）

- 新關卡預填代碼的前綴 `stage-`：畫面預設值，使用者可改，不屬業務政策。
- 沒有新的數量上限或預設值寫進程式；技術上限由政策 `meta.max` 取得。

---

## Review Focus

1. **未存檔的狀態機草稿不會被流程變動洗掉**：在狀態機分頁打開「需簽核」、切到簽核流程分頁建流程、再切回來，草稿仍在、鎖定標示已更新。→ Task 5 `unsaved draft survives a workflow refetch`。
2. **啟停不產生新版本**：被用過的流程按停用，版本號不變。→ Task 1 `Update_UsedVersion_OnlyActiveChanged_NoNewVersion`、Task 8 `deactivate … saves active false with the same definition`。
3. **上限不寫死、提示一致**：關卡數、人數、時數、主管層數的數字都來自政策；四段提示文字與 Global Constraints 相同。→ Task 9 的 hint 測試、Task 13 `add is disabled at the policy stage limit`。
4. **關卡代碼不從名稱產生**：改名稱不動代碼；改既有關卡代碼有警告。→ Task 11、Task 13。
5. **試算不寫入、用的是草稿**：試算後資料庫的流程與版本筆數不變；新流程沒存也能試算。→ Task 3 `DraftPreview_NewFlow_NotSaved_ShowsDraftStages`、Task 15。
6. **收件人 chip 不誤報**：名稱載入前或 `/groups` 失敗時，不出現「找不到或已停用」。→ Task 9。
7. **錯誤落在對的位置**：`transitions[a>b]` 落在那條轉移、`definition.stages[1].*` 落在第 2 張關卡卡片；任何編輯後清掉（索引會變）。→ Task 6、Task 12、Task 13。
8. **被用過的流程**：沒有刪除、目標狀態鎖住並說明；只能停用。→ Task 8、Task 12。
9. **390 不橫捲**：狀態機分頁的轉移與狀態改卡片、對話框全螢幕上方分段、關卡參數上下堆疊。→ Task 7、Task 14、Task 17 走查。

---

## 範圍確認

### 已在 main（只接、不重做）

| 項目 | 證據（main `3f0849d0`） |
|---|---|
| 流程 CRUD、排序、試算（已存版本） | `ApprovalFlowsController`：`GET/POST approval-flows?module=`、`GET/PUT/DELETE approval-flows/{key}`、`PUT approval-flows/order`、`POST approval-flows/{key}/preview` |
| 流程卡片要的版本、是否用過、進行中實例數 | `ApprovalFlowResponse(…, Version, Used, PendingCount, Definition)`（`ApprovalFlowModels.cs`） |
| 狀態機回應的鎖定狀態、可編輯狀態、鎖定欄位 | `WorkflowResponse.LockedStates／EditableStates／LockedFieldIds`（`WorkflowModels.cs`），`GET/PUT modules/{key}/workflow` |
| 入口約束與錯誤鍵 | `ApprovalConstraints.Validate`：`transitions[{from}>{to}]`、`states[{id}]`、`initialOptionId`、`flows[{key}]`；存狀態機另有 `events[{key}]` |
| 定義驗證與「只管之後」 | `ApprovalDefinitionValidator.ValidateAsync(…, previous, …)`，錯誤鍵 `definition.stages[i].code／name／when／approvers／mode／editableFields／dueHours／onEmpty` |
| 進行中實例、鎖定資料的守衛（409） | `PendingApprovalGuard`、`LockedDataGuard`（a2d4、a2d5） |
| 簽核類政策與生效值 | `ApprovalPolicies.LimitsAsync`；`GET policies/effective?module=`（不含收件人類）；`GET modules/{key}/policies`（含 `approval.defaultFallbackApprovers` 與 `meta.max`） |
| 設計器存狀態機保留 `approval`、`editable` | a4b J9：`buildWorkflow`（`domain/dynamic/workflow.ts`） |
| 預覽時間軸元件 | `presentation/dynamic/approval/PreviewStages.vue` |
| 條件編輯器、條件摘要 | `presentation/dynamic/list/ConditionEditor.vue`；摘要是 `RulesTab.vue` 的區域函式 `summary` |

### 這次做

| 設計項目 | 落點 | 後端 |
|---|---|---|
| §13.1 轉移「需簽核」開關、`noMatch` 選擇、「目標狀態已掛 N 個流程」連結、N 為 0 不能存 | Task 5 | 就緒 |
| §13.1 狀態「可編輯（退回重送）」開關（起始狀態固定打開）、鎖定狀態標示、鎖定欄位清單、存檔錯誤對到列 | Task 6 | 就緒 |
| §13.1 簽核流程分頁：依目標狀態分組（標入口轉移）、卡片、啟停、上移下移與桌機拖曳、刪除沒用過的 | Task 8 | 就緒；啟停不產生新版本要補（Task 1） |
| §13.1 對話框「基本」：名稱與鍵、目標狀態、啟動條件、退回狀態、備援簽核人；版本提示 | Task 12 | 「目前版本是否已被用過」要補（Task 1） |
| §13.1 對話框「關卡」：卡片串、插入、來源與參數、模式、可編輯欄位、逾期、找不到人時 | Task 9、13 | 可編輯欄位候選要補（Task 2） |
| §13.1 對話框「試算」：記錄或欄位值、發起人、時間軸 | Task 15 | 試算草稿、命中的流程鍵、每關問題要補（Task 3，待決 U1） |
| §13.1 390 寬行為 | Task 7、14、17 | — |
| a2d7 N-M1（收件人 chip 誤報）＋N-M2（查詢改走 `useUsers`／`useGroups`） | Task 9 | — |
| 手冊、設計寫回、功能清單、roadmap | Task 16 | — |

### 不做

| 項目 | 理由 |
|---|---|
| 套件格式 8、內建流程範本 | a5 |
| 轉交、加簽、代理、逾期提醒的發送 | a3；`dueHours` 照存，簽核中心的逾期標示已在 a4b |
| 事件設計器對「轉回草稿前改鎖定欄位」的提示（a2d1 fix 新問題 1） | 屬事件設計器，不在 §13.1；Task 16 記進 `docs/todo.md` |
| a2d7 N-M3 後端測試、N-M4（收件人 0 項或 20 項以上的提示） | 不影響本段畫面；Task 16 記進 `docs/todo.md`（N-M3 前端部分隨 Task 9 補） |
| 設計器驗證訊息多語 | 多語系 #21，已列功能清單 |
| 前端即時計算鎖定狀態 | 待決 U3 |

---

## 子段切法與合併順序

| 子段 | 內容 | Task | 估計行數（含測試） | 畫面 |
|---|---|---|---|---|
| a4a1 | 後端補齊：流程回應與版本、可編輯欄位候選、試算草稿 | 1–4 | 約 550 | 無 |
| a4a2 | 狀態機分頁：需簽核開關、`noMatch`、流程數連結、可編輯開關、鎖定標示、錯誤對列、390 卡片化 | 5–7 | 約 750 | 有 |
| a4a3 | 簽核流程分頁（卡片、啟停、排序、刪除）＋簽核人來源編輯器＋N-M1 | 8–10 | 約 950 | 有 |
| a4a4 | 流程編輯對話框：基本＋關卡、新增與編輯入口、版本提示 | 11–14 | 約 1100 | 有 |
| a4a5 | 試算、文件、總收 | 15–17 | 約 600 | 有 |

另有 Task 0。合計 18 個 Task（0–17），**沒有 migration**。

- **a4a1 與 a4a2 沒有共用檔案**（後端 vs `WorkflowTab`、`workflow.ts`、i18n 只有 a4a2 改），可以平行：主控另開 `../dynamic-fields-design.a4a2`（workspace:worktree）派第二個 subagent；先完成者先 fast-forward，另一個 rebase 再合。不開第二個 worktree 時就依序做。
- a4a3 起依序：都改四語 i18n（單行 JSON，平行必衝突），而且後段用前段的元件。a4a3 需要 a4a1（啟停不產生新版本）與 a4a2（流程 API 型別、`open-flows` 事件）。
- 每段合併後既有行為不變的界線：a4a1 只多回應欄位與一個新端點；a4a2 起才改畫面。沒有狀態機的模組，狀態機分頁與現在相同；簽核流程分頁只顯示提示。a4a3 合併後、a4a4 之前，流程分頁沒有「新增」「編輯」（還沒有對話框），其餘可用。

---

## 計畫判斷（技術類，可推翻）

每條：判斷／理由／選錯代價。

- **J1 沒有 migration。** 四個缺口都是查詢或比較。實作中若發現非加不可，停下回報。
- **J2 `ApprovalFlowResponse` 加 `CurrentVersionUsed`。** 版本提示要判斷「這次存檔會不會產生新版本」，看的是目前版本（`IsVersionUsedAsync`），不是 `Used`（任一版本）。代價：回應多一個欄位、每張卡多一次查詢（設計器頻率低）。
- **J3 只改名稱、啟停、優先序時不產生新版本。** `UpdateApprovalFlowHandler` 在目前版本已被用過時，先比對新舊定義（兩邊都以 `JsonSerializer.Serialize` 的結果比），相同就不建版本。理由：卡片上按停用要送完整的 PUT，否則每按一次就多一版，版本號失去意義；名稱、啟停、優先序本來就存在流程上，不在版本裡。代價：同一份定義因 JSON 屬性順序不同會被當成改過（前端原樣回送，實務上不會發生）。
- **J4 可編輯欄位候選放在狀態機回應 `EditableFieldIds`。** 把 `ApprovalDefinitionValidator.NonEditableFieldIdsAsync` 抽成 `ApprovalEditableFields`（scoped），驗證器與 `WorkflowApprovals` 共用，候選＝本模組啟用中的欄位扣掉不可編輯集合。對簽核人隱藏、落在條件閉包、`from` 是唯讀或鎖定的限制要看流程內容，留給存檔 400（訊息已含路徑）。理由：同一條規則只在後端一份；狀態機回應本來就是設計器專用、已帶鎖定欄位。代價：候選在設計者改轉換或分錄後要重整頁面才更新，存檔時後端照樣擋。
- **J5 試算草稿用新端點 `POST approval-flows/preview?module=`**（待決 U1 照推薦）。本文 `{ draft: ApprovalFlowInput, recordId?, values?, initiatorUserId }`；只跑 `ApprovalDefinitionValidator`（400，鍵 `definition.*`），不跑入口約束與優先序唯一（試算不是存檔）；流程集合＝目標狀態上現有流程，同鍵的換成草稿、沒有就加進去，草稿一律當成啟用。`ApprovalPreviewer.PreviewAsync` 加選用參數 `IReadOnlyList<FlowSnapshotEntry>? snapshot`。路由不和 `approval-flows/{key}` 衝突（那裡沒有 POST），不必加保留字。既有 `POST approval-flows/{key}/preview` 保留。代價：一個新端點。
- **J6 設計器試算回報 `FlowKey` 與每關 `Problem`。** `ApprovalPreviewResponse` 加 `string? FlowKey`、`PreviewStage` 加 `string? Problem`（`noApprover`、`tooManyApprovers`，從 `StageResolution.Error` 的訊息鍵對應），**只在設計器試算填**，送出前預覽一律 null，不改 a4b 的揭露範圍。理由：試算要能說「這筆會先符合別的流程」「這一關送出時會失敗」；用代碼而不是訊息字串，前端四語顯示。代價：兩個選用欄位。
- **J7 狀態機分頁把伺服器回應與草稿分開。** `WorkflowTab` 的 `watch(query.data)` 只在「沒有未存變更」、換模組、或自己存檔／刪除成功後才重設草稿；鎖定標示、鎖定欄位、可編輯候選一律讀最新的伺服器回應。流程存檔會失效 `['workflow', key]`（鎖定欄位隨流程定義變），不這樣分開就會洗掉使用者剛打開的「需簽核」。代價：多一個 `dirty` 判斷（草稿與伺服器回應的 `buildWorkflow` 結果比對）。
- **J8 「需簽核」打開時預設 `noMatch = block`**（§5.2 預設）；關掉時轉移不帶 `approval`。「目標狀態已掛 N 個流程」的 N 取 `GET approval-flows?module=` 的 `toOptionId` 計數（含停用）；任何需簽核轉移的目標 N 為 0 時停用儲存鈕並說明「先在簽核流程分頁為「{狀態}」建立流程」。連結發出 `open-flows(toOptionId)`，`ModuleEditorPage` 切到簽核流程分頁並傳入目標；分頁 `keep-alive`，狀態機草稿保留。
- **J9 入口一致、進入鎖定一律要簽等跨轉移規則不在前端重算**，靠後端 400 對到列（`transitions[a>b]` 標在那條轉移、`states[id]` 標在那個狀態、`initialOptionId` 標在起始欄、`flows[key]` 與 `events[key]` 列在錯誤清單並帶流程名稱）。前端只做兩個由草稿直接看得出的停用：起始狀態的「可編輯」固定打開、需簽核轉移目標的「可編輯」關閉並說明「需簽核轉移的目標狀態以鎖定為準」。理由：規則只在後端一份。
- **J10 簽核流程分頁的啟停、排序立即存檔**（待決 U4 照推薦）；新增、編輯走對話框。排序呼叫 `PUT approval-flows/order`，桌機用原生 HTML5 drag（只在同一目標狀態內），手機只有上移／下移。
- **J11 流程卡片的條件摘要**：把 `RulesTab.vue` 的 `summary` 抽成 `presentation/dynamic/rules/condition-summary.ts`，規則分頁改用它、行為不變；`when` 為 null 或空群組顯示「一律符合」。
- **J12 目標狀態下拉列所有非起始狀態**（§20.4 第 3 條，取代 §13.1「只列需簽核轉移的目標狀態」）：選項註明「已有需簽核的入口」或「還沒有需簽核的入口」；被用過的流程停用下拉並說明「被使用過的流程不能改目標狀態，請另建流程」。分頁上沒有需簽核入口的分組顯示警告「這個狀態還沒有需簽核的入口，流程不會被用到」。
- **J13 新流程**：優先序＝該目標狀態最大值＋1（排在最後，不搶既有流程）；預設啟用；流程鍵手動輸入，提示「慣例以應用程式代碼當前綴」（同 `ConversionEditorDialog` 的鍵欄）。編輯時改目標狀態，優先序改成新目標的最大值＋1。
- **J14 新關卡預設**：來源「發起人的主管」第 1 層、或簽、找不到人時交給備援、不設逾期、沒有可編輯欄位；代碼見待決 U2。
- **J15 模式在單人來源時隱藏並存 `any`**：主管、使用者欄位、只指定 1 位使用者、自選最多 1 位。
- **J16 備援簽核人**：「沿用模組預設備援（目前：{名稱}）」與「自訂」二選一；自訂只給指定使用者、群組、主管（§20.4 第 11 條）。預設備援的名稱用 `policyValueText` 加 `useRecipientNames`，名稱沒載入前顯示原始項目。
- **J17 可編輯欄位多選**的選項＝狀態機回應的 `editableFieldIds`，提示「只列可以修改的欄位；跟條件或簽核人有關的欄位，存檔時會檢查」。
- **J18 對話框版面**：寬度 ≥ sm 時左側直向分段（基本／關卡／試算）、右側內容；< sm 時全螢幕、上方分段標籤、儲存鈕固定底部。不加「放棄變更？」確認，沿用 `ConversionEditorDialog` 的取消即關。
- **J19 錯誤鍵依索引對到關卡卡片，任何編輯後清空**（同 `RulesTab` 的做法，索引會變）。
- **J20 試算的欄位值**：只列會影響比對與解析的欄位——目標狀態上所有流程（含草稿）的啟動條件、關卡條件引用的欄位，加上 `userField` 來源的欄位；輸入元件用 `fieldTypeRegistry[type].filterInput(f, 'eq')`（`ConditionEditor` 的同一套）。值以 `f_{id}` 為鍵送出。
- **J21 收件人名稱就緒旗標**：`useRecipientNames` 改呼叫 `useUsers({ enabled })`、`useGroups({ enabled })`（兩者加選用參數，寫法同 `useSystemPolicies`），回傳 `ready`；`PolicyValueEditor` 加 `recipientsReady` prop（預設 false），false 時已選但不在選項裡的項目標籤顯示原始代碼，不顯示「找不到或已停用」。簽核人來源編輯器的使用者、群組 chip 用同一個規則。
- **J22 走查資料**：Demo 的 CRM 設計案已有流程 `crm-design-case-review`（`DemoApprovals.cs`），足夠看卡片、版本、啟停；新增流程與試算在走查時用畫面建立（這正是本段要驗的）。Task 0 寫一支只讀的檢查腳本確認 Demo 有這個流程。

---

## 待決（使用者行為類，本計畫先照推薦寫，裁決不同時改對應 Task）

**U1. 試算用編輯中的內容，還是已儲存的版本？**
- 例子：設計者把啟動條件從「總額 ≥ 100000」改成「≥ 50000」，還沒存，想先拿一張 80000 的單試算。設計 §13.1 只說「按試算呼叫預覽端點」，現有端點只吃已儲存的版本，新流程沒存檔前不能試。
- 推薦：**試算編輯中的內容（不寫入）**，新流程沒存也能試；試算時把這個流程視為啟用。理由：流程沒有「草稿／發布」，一存檔就影響之後送出的單，用過的流程還會多一版；先試再存才安全。
- 替代：只能試算已存版本，對話框有未存變更時提示「請先儲存」。省掉後端一個端點（約 150 行含測試），但設計者只能「先上線再試」。
- 選錯代價：選替代時，改錯條件要等存檔後才發現，期間送出的單已走新流程。
- 落點：Task 3、Task 15。

**U2. 新增關卡時，關卡代碼要預填嗎？**
- 例子：設計者在第 1 關後按「＋」，新卡片的代碼欄位是 `stage-2`（可改），還是空白要自己填？代碼要符合「小寫英文開頭、只有小寫英文數字連字號」，存檔後用來對照舊版（改代碼等於新關卡）。
- 推薦：**預填目前沒被用到的最小 `stage-N`**，可改；從不跟著名稱變；已存過的關卡改代碼時顯示「改代碼會被當成新關卡，要符合目前的上限」。理由：中文名稱產不出合法代碼（a2d6 審查不准從名稱產生），空白必填會讓每加一關都多一步，而且多數人不知道代碼的規則。
- 替代：空白必填，旁邊放規則說明。
- 選錯代價：預填的話，代碼沒有意義（`stage-3` 不見得是第 3 關，插入後不重編）；空白的話，設計者每關都要想代碼、常被 400 擋。
- 落點：Task 11、Task 13。

**U3. 狀態機分頁還沒存檔時，鎖定標示要即時跟著變嗎？**
- 例子：設計者把「草稿 → 已確認」打開需簽核，還沒按儲存。「已確認」「部分進貨」要不要立刻顯示鎖頭？
- 推薦：**顯示上次儲存的結果，草稿有變更時在表格上方註明「鎖定標示與鎖定欄位依上次儲存的狀態機；儲存後重新計算」**。理由：鎖定狀態與鎖定欄位只在後端算（`ApprovalTopology`、`ApprovalLockService`），鎖定欄位還要看所有流程與版本，前端算不出；只即時算鎖定狀態、不算欄位，會讓兩個標示對不上。
- 替代：前端照 `ApprovalTopology.LockedStates` 的規則即時算鎖定狀態（約 40 行，邏輯多一份），鎖定欄位仍等存檔。
- 選錯代價：推薦做法下，設計者要存一次才看得到新的鎖定範圍；替代做法下，前後端規則日後可能不一致。
- 落點：Task 6。

**U4. 流程卡片上的停用、調整優先序，要按了就存，還是像規則分頁一樣最後一起存？**
- 例子：設計者把流程 B 上移到 A 前面，或把 A 停用。
- 推薦：**按了就存**；停用先跳確認「停用後，之後送出的單不再比對這個流程；進行中的 {n} 張照原流程走完」，啟用與上移／下移直接存、存完提示「已調整優先序，只影響之後送出的單」。理由：每個流程是獨立的 API 資源（不像規則是整包存），而且每個變動都要通過入口約束（例如 `direct` 入口至少一個啟用流程），一次一個比較能看出是哪個動作被擋。
- 替代：分頁底部一個「儲存」鈕，一次送出所有啟停與排序；被擋時要從多個變動裡找原因。
- 選錯代價：按了就存的話，連續調整排序會發多次請求、每次都生效；一起存的話，多個變動中一個被擋就全部不生效。
- 落點：Task 8。

---

## Task 0：開工前核對

**Files:** 只改本計畫與同資料夾的腳本，不改程式碼。

- [ ] **Step 1：** `git -C ../dynamic-fields-design.a4a fetch && git log --oneline -1 origin/main`；main 若已前進，確認 `git status --short` 乾淨後 `git rebase origin/main`。
- [ ] **Step 2：** 核對本計畫引用的型別與檔案，有出入先改計畫：`ApprovalFlowResponse`、`ApprovalFlowInput`、`UpdateApprovalFlowHandler`、`ApprovalFlowEditor.ResponseAsync`、`IApprovalFlowRepository.IsVersionUsedAsync`、`WorkflowResponse`／`WorkflowMapper.ToResponse`、`ApprovalDefinitionValidator.NonEditableFieldIdsAsync`、`ApprovalPreviewer.PreviewAsync`、`PreviewApprovalFlowHandler`、`ApprovalPreviewResponse`、`PreviewStage`、`StageResolution`；前端 `WorkflowTab.vue`、`workflow.ts`、`ModuleEditorPage.vue`、`RulesTab.vue`、`ConditionEditor.vue`、`PreviewStages.vue`、`PolicyValueEditor.vue`、`useRecipientNames.ts`、`permissions-queries.ts`、`policies-queries.ts`、`dynamic-api.ts`。
- [ ] **Step 3：** 寫 `../.tmp/plans/20261113-approval-a4a/walkthrough-check.sh`（不進 repo）：對 `scripts/unattended/local-demo.sh` 起的本機 Demo（後端 5180），以系統管理員登入後 `GET approval-flows?module=<設計案模組鍵>`，印出流程鍵、版本、`used`、`pendingCount`，確認 `crm-design-case-review` 存在；另印出 CRM 業務群組的一位一般設計者帳號（走查唯讀模式用，沒有就註明）。只讀，跑一次確認能用。
- [ ] **Step 4：** 計畫頂端加修訂紀錄一行「Task 0 核對完成：<日期>、<改了哪些引用>」。

---

## a4a1：後端補齊

### Task 1：流程回應帶「目前版本是否已被用過」，只改啟停不產生新版本

**Files:**
- Modify: `src/DynamicModules.Application/Dynamic/ApprovalService/ApprovalFlowModels.cs`（`ApprovalFlowResponse` 末尾加 `bool CurrentVersionUsed`）
- Modify: `ApprovalFlowHandlers.cs`（`ResponseAsync`；`UpdateApprovalFlowHandler` 的版本判斷）
- Create: `src/DynamicModules.Domain/Approvals/ApprovalDefinitionJson.cs`（`static bool SameAs(ApprovalDefinition a, ApprovalDefinition b)`）
- Test: `tests/DynamicModules.ApplicationTests/Dynamic/Approvals/ApprovalFlowDesignerTests.cs`（新檔，`: ApprovalTestBase`）、`tests/DynamicModules.DomainTests/Approvals/ApprovalDefinitionJsonTests.cs`、`tests/DynamicModules.WebAPITests/Dynamic/ApprovalFlowsApiTests.cs`

**Interfaces:**
- Produces：`ApprovalFlowResponse(…, int PendingCount, ApprovalDefinition Definition, bool CurrentVersionUsed)`。
- 規則：目前版本已被用過 **且** `!SameAs(舊定義, 新定義)` → 新版本；其他照舊（沒被用過就覆寫）。名稱、目標狀態、優先序、啟停照常更新。驗證與入口約束照跑（停用可能違反 `direct` 規則）。

- [ ] **Step 1：寫失敗的測試**
  - Domain：`SameAs_IdenticalAfterRoundTrip_True`（序列化再反序列化的同一份定義）、`SameAs_StageConditionDiffers_False`、`SameAs_EditableFieldsOrderDiffers_False`（順序不同視為不同，不做正規化）。
  - Application：
    - `Update_UsedVersion_OnlyActiveChanged_NoNewVersion`：送出一張單讓版本被綁定，原定義＋`Active=false` → `Version` 不變、`Active == false`、版本表筆數不變。
    - `Update_UsedVersion_NameAndPriorityChanged_NoNewVersion`。
    - `Update_UsedVersion_DefinitionChanged_NewVersion`：→ `Version == 2`、`CurrentVersionUsed == false`、`Used == true`。
    - `Response_CurrentVersionUsed_TrueWhenBound`、`_FalseForNewFlow`。
    - `Response_CurrentVersionUsed_TrueWhenOnlyInSnapshot`：流程 B 只出現在別人的流程快照裡 → true（§4.4「被用過」的定義）。
  - WebAPI（`[Trait("Category", "Postgres")]`）：`GetFlows_Json_HasCurrentVersionUsed`（camelCase `currentVersionUsed`）。
- [ ] **Step 2：跑測試確認失敗**：`cd poc/backend && dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~ApprovalFlowDesignerTests"`；Domain 同理。預期編譯失敗。
- [ ] **Step 3：實作。**
- [ ] **Step 4：跑測試確認通過**：同 Step 2，另跑 `--filter "FullyQualifiedName~ApprovalFlowDefinitionTests|FullyQualifiedName~ApprovalPolicyLimitsTests"` 防回歸；WebAPI `--filter "FullyQualifiedName~ApprovalFlowsApiTests"`。
- [ ] **Step 5：commit** `feat(approval): 流程回應帶目前版本是否已用過，只改啟停不產生新版本`。

### Task 2：狀態機回應帶可編輯欄位候選

**Files:**
- Create: `src/DynamicModules.Application/Dynamic/ApprovalService/ApprovalEditableFields.cs`（從 `ApprovalDefinitionValidator.NonEditableFieldIdsAsync` 搬出；`DependencyInjection.cs` 註冊 scoped）
- Modify: `ApprovalDefinitionValidator.cs`（改用新服務，行為不變）
- Modify: `WorkflowService/WorkflowApprovals.cs`（加 `EditableFieldIdsAsync`）、`WorkflowService/Data/WorkflowModels.cs`（`WorkflowResponse` 末尾加 `IReadOnlyList<long> EditableFieldIds`；`ToResponse` 加參數）、`GetWorkflowHandler.cs`、`SaveWorkflowHandler.cs`
- Test: `tests/DynamicModules.ApplicationTests/Dynamic/Approvals/ApprovalFlowDesignerTests.cs`、`ApprovalFlowsApiTests.cs`

**Interfaces:**
- Produces：`ApprovalEditableFields.NonEditableAsync(Module, Workflow, ct) : Task<HashSet<long>>`；`CandidatesAsync(Module, Workflow, ct) : Task<IReadOnlyList<long>>`＝本模組啟用中欄位扣掉不可編輯集合，依 Id 排序。
- `WorkflowResponse(…, IReadOnlyList<long> LockedFieldIds, IReadOnlyList<long> EditableFieldIds)`。

- [ ] **Step 1：寫失敗的測試**
  - `GetWorkflow_EditableFieldIds_ExcludeComputedStatusAndLockFill`：模組有數字、公式、彙總、自動編號、狀態欄位、`lock` 帶值目的欄位、停用欄位 → 只剩數字與一般欄位。
  - `GetWorkflow_EditableFieldIds_ExcludeConversionTraceAndLedgerReversal`（rig 有轉換與分錄時；沒有現成 rig 就各建最小設定）。
  - `SaveWorkflow_ResponseHasEditableFieldIds`。
  - `Validator_StillRejectsFormulaAsEditable`（搬移後行為不變）。
  - WebAPI：`GetWorkflow_Json_HasEditableFieldIds`（Postgres trait）。
- [ ] **Step 2：跑測試確認失敗**：`--filter "FullyQualifiedName~ApprovalFlowDesignerTests"`。
- [ ] **Step 3：實作。**
- [ ] **Step 4：跑測試確認通過**：同 Step 2，另跑 `--filter "FullyQualifiedName~Workflow|FullyQualifiedName~ApprovalFlowDefinitionTests"`。
- [ ] **Step 5：commit** `feat(approval): 狀態機回應帶可編輯欄位候選`。

### Task 3：試算編輯中的流程（不寫入），回報命中的流程與每關問題

**Files:**
- Modify: `ApprovalRequests.cs`（新 `PreviewApprovalDraftRequest`）、`ApprovalHandlers.cs`（新 `PreviewApprovalDraftHandler`，共用 `PreviewApprovalFlowHandler` 的記錄讀取與 `noMatch` 取法，抽成私有靜態方法）
- Modify: `ApprovalPreviewer.cs`（`PreviewAsync` 加選用 `snapshot`；設計器模式填 `FlowKey`、`Problem`）
- Modify: `ApprovalModels.cs`（`ApprovalPreviewResponse` 加 `string? FlowKey`；`PreviewStage` 加 `string? Problem`；`PreviewProblems { NoApprover = "noApprover", TooManyApprovers = "tooManyApprovers" }`）
- Modify: `src/DynamicModules.WebAPI/Controllers/v3/Dynamic/ApprovalFlowsController.cs`（`[HttpPost("approval-flows/preview")] Preview([FromQuery] string module, DraftPreviewBody body)`）
- Test: `tests/DynamicModules.ApplicationTests/Dynamic/Approvals/ApprovalPreviewTests.cs`、`ApprovalFlowsApiTests.cs`

**Interfaces:**
- Produces：
  ```csharp
  public record PreviewApprovalDraftRequest(string ModuleKey, ApprovalFlowInput Draft, long? RecordId,
      Dictionary<string, JsonElement?>? Values, string InitiatorUserId) : IRequest<ServiceResult>, IDesignerRequest
  { public PermissionAction RequiredDesignerAction => PermissionAction.Read; }
  public sealed record ApprovalPreviewResponse(string Outcome, I18nText? FlowName, IReadOnlyList<PreviewStage> Stages,
      IReadOnlyList<PickRequirement> Picks, string? FlowKey = null);
  public sealed record PreviewStage(string? Code, I18nText Name, string Status, IReadOnlyList<UserRef> Approvers, bool UsesFallback, string? Problem = null);
  ```
- 規則見 J5、J6。模組不合格（`CheckModule`）照既有 400；`Draft.ToOptionId` 不是狀態機的非起始狀態 → 400 `toOptionId`；定義錯 → 400 `definition.*`（`ApprovalDefinitionValidator`，`previous` 傳同鍵流程的目前版本，讓「只管之後」和存檔一致）。帶記錄的讀取規則同既有試算（沒有 Read 或不在資料範圍 → 404）。

- [ ] **Step 1：寫失敗的測試**（`ApprovalPreviewTests`）
  - `DraftPreview_NewFlow_NotSaved_ShowsDraftStages`：模組沒有任何流程，草稿兩關 → `Outcome == flow`、`FlowKey == 草稿鍵`、兩關都列；流程表與版本表筆數不變。
  - `DraftPreview_ReplacesSavedVersionOfSameKey`：已存流程 `when 總額 ≥ 100000`，草稿改成 `≥ 1`，值 50 → 命中草稿。
  - `DraftPreview_HigherPriorityFlowWins_FlowKeyIsOther`。
  - `DraftPreview_InactiveDraft_TreatedAsActive`。
  - `DraftPreview_InvalidStageCode_Returns400WithDefinitionKey`：鍵 `definition.stages[0].code`。
  - `DesignerPreview_UnresolvableStage_ReportsNoApproverProblem`：來源是沒有成員的群組、流程備援也解析為空 → 那一關 `Problem == "noApprover"`、`Approvers` 空。
  - `DesignerPreview_OverStageLimit_ReportsTooManyProblem`：模組覆寫 `approval.maxApprovers = 1`、群組 2 人 → `tooManyApprovers`。
  - `RecordPreview_NoFlowKeyNoProblem`：送出前預覽同樣情境 → `FlowKey`、`Problem` 都是 null。
  - `DraftPreview_RecordOfOtherModule_Returns404`。
  - WebAPI（Postgres trait）：`DraftPreview_Json_HasFlowKeyAndProblem`、`DraftPreview_NonDesigner_Returns403`。
- [ ] **Step 2：跑測試確認失敗**：`--filter "FullyQualifiedName~ApprovalPreviewTests"`。
- [ ] **Step 3：實作。** `Problem` 依 `StageResolution.Error` 的訊息鍵對應（`MessageText.ApprovalNoApprover` → noApprover、`ApprovalTooManyApprovers` → tooManyApprovers），其他錯誤記成 noApprover。
- [ ] **Step 4：跑測試確認通過**：同 Step 2，另跑 `--filter "FullyQualifiedName~Approval"`（ApplicationTests 簽核全部，確認 a4b 的預覽行為不變）、WebAPI `ApprovalApiTests|ApprovalFlowsApiTests`。
- [ ] **Step 5：commit** `feat(approval): 設計器可試算編輯中的流程，回報命中的流程與每關問題`。

### Task 4：a4a1 收尾

**Files:** `docs/roadmap.md`（a4a 列改「進行中：a4a1 完成」）；本計畫檔尾「pg0 合併後補驗」。

- [ ] **Step 1：gate**：`bash scripts/unattended/gate.sh ../dynamic-fields-design.a4a`，每步 `exit=0`。
- [ ] **Step 2：** 沒有畫面，不做四寬度；本機 Demo 以 curl 確認 `GET approval-flows?module=` 有 `currentVersionUsed`、`GET modules/{key}/workflow` 有 `editableFieldIds`、`POST approval-flows/preview?module=` 對未存草稿回時間軸且之後 `GET approval-flows` 筆數不變。
- [ ] **Step 3：** 交付說明：本機測試、gate、curl 驗證各自宣稱；可寫死清單無新增；pg0 補驗清單。
- [ ] **Step 4：commit** `docs(approval): a4a1 進度`。

---

## a4a2：狀態機分頁

### Task 5：轉移的需簽核開關、條件都不符合時、流程數連結；草稿不被重抓洗掉

**Files:**
- Modify: `src/domain/dynamic/types.ts`（`WorkflowResponse = Workflow & { lockedStates: number[]; editableStates: number[]; lockedFieldIds: number[]; editableFieldIds?: number[] }`）
- Create: `src/domain/dynamic/approval-flow-types.ts`（`ApproverSourceKind`、`ApproverSource`、`ApprovalStageDef`、`ApprovalDefinition`、`ApprovalFlowInput`、`ApprovalFlow`，欄位照後端 camelCase；`ApprovalFlow` 含 `version`、`used`、`pendingCount`、`currentVersionUsed?`）
- Modify: `src/services/api/dynamic-api.ts`（`getApprovalFlows(module)`、`getApprovalFlow(key)`、`createApprovalFlow(module, input)`、`updateApprovalFlow(key, input)`、`deleteApprovalFlow(key)`、`reorderApprovalFlows(module, toOptionId, keys)`；`getWorkflow` 回 `WorkflowResponse`）
- Create: `src/application/dynamic/queries/approval-flow-queries.ts`（`useApprovalFlows(moduleKey)` key `['approval-flows', key]`；`useSaveApprovalFlow`、`useDeleteApprovalFlow`、`useReorderApprovalFlows`，成功後失效 `['approval-flows', key]` 與 `['workflow', key]`）
- Modify: `src/domain/dynamic/workflow.ts`（`setTransitionApproval(d, i, approval | null)`、`draftTargets(d)`＝需簽核轉移的目標、`isDraftDirty(d, saved)`）
- Modify: `src/presentation/dynamic/workflow/WorkflowTab.vue`；`src/pages/system/dynamic-modules/ModuleEditorPage.vue`（接 `open-flows`，先記下目標，分頁在 Task 8 加）
- Modify: i18n 四語（`docflow.workflow.approval.*`）
- Test: `src/domain/dynamic/workflow.vitest.spec.ts`、`src/presentation/dynamic/workflow/WorkflowTab.vitest.spec.ts`、`src/application/dynamic/queries/approval-flow-queries.vitest.spec.ts`（新檔）

**Interfaces:**
- `WorkflowTab` emits `open-flows: [toOptionId: number]`。
- 草稿重設規則見 J7；需簽核、`noMatch`、流程數與停用儲存見 J8。

- [ ] **Step 1：寫失敗的測試**
  - workflow：`setTransitionApproval on sets required with block`、`off removes approval`、`draftTargets lists approval targets only`、`isDraftDirty false right after draftFromWorkflow, true after a toggle`。
  - queries：`create posts module query and input`、`reorder puts module, toOptionId and keys`、`save invalidates approval-flows and workflow of the module`。
  - WorkflowTab：
    - `approval toggle adds required approval with block and shows the noMatch select`。
    - `turning approval off sends no approval for that transition`。
    - `direct noMatch shows the direct warning text`（§5.2 原文）。
    - `flow count link shows N flows on the target and emits open-flows with the target option`。
    - `save disabled while an approval transition targets a state with no flow, with the reason`。
    - `unsaved draft survives a workflow refetch`：草稿打開需簽核後，以 `setQueryData` 換一份伺服器回應 → 草稿仍有那條 `approval`，鎖定資料換成新回應的。
    - `saving resets the draft from the server response`。
    - `read-only designer sees the toggle disabled`。
- [ ] **Step 2：跑測試確認失敗**：`cd poc/frontend && bunx vitest run src/domain/dynamic/workflow.vitest.spec.ts src/presentation/dynamic/workflow src/application/dynamic/queries/approval-flow-queries.vitest.spec.ts --maxWorkers=2`。
- [ ] **Step 3：實作。** 開關用 `q-toggle` 加 `aria-label`；`noMatch` 用 `q-select`，選項文字「擋下（預設）」「直接轉移」。
- [ ] **Step 4：跑測試確認通過**：同 Step 2，另跑 `src/pages/system src/i18n`、`bun run typecheck`。
- [ ] **Step 5：commit** `feat(approval): 狀態機分頁可設需簽核與條件都不符合時的處理`。

### Task 6：狀態的可編輯開關、鎖定標示、鎖定欄位、存檔錯誤對到列

**Files:**
- Modify: `WorkflowTab.vue`、`workflow.ts`（`setStateEditable(d, id, on)`）、i18n 四語
- Test: `WorkflowTab.vitest.spec.ts`、`workflow.vitest.spec.ts`

**Interfaces:**
- 狀態列新增「可編輯（退回重送）」欄：起始狀態固定打開並停用；`draftTargets` 內的狀態關閉並停用，說明「需簽核轉移的目標狀態以鎖定為準」（J9）。
- 鎖定標示：伺服器回應 `lockedStates` 內的狀態加 `lock` 圖示徽章「簽核鎖定」；`editableStates` 內加「可編輯」徽章（取代現在只看草稿 `editable` 的徽章）。草稿有變更時表格上方顯示 U3 的說明。
- 鎖定欄位：表格下方「簽核鎖定狀態中不能修改的欄位：{名稱清單}（附件欄位一律鎖定）」，沒有時不顯示；另一行 §9.3 的唯讀建議。
- 錯誤對列見 J9：`transitions[{from}>{to}]` → 該轉移列紅框與訊息；`states[{id}]` → 該狀態列；`initialOptionId` → 起始欄說明；其餘（`flows[key]` 前綴流程名稱、`events[key]`、409 訊息）列在錯誤清單。任何編輯清空。

- [ ] **Step 1：寫失敗的測試**
  - `initial state editable toggle is on and disabled`。
  - `approval target editable toggle is disabled with the lock reason`。
  - `editable toggle round-trips in the save body`。
  - `locked states show the lock badge from the saved workflow`。
  - `locked field names are listed under the states`。
  - `dirty draft shows the recompute-after-save caption`。
  - `transition error key lands on that transition row`、`state error key lands on that state row`、`flow error lists the flow name`、`errors clear on the next edit`。
  - `409 from the pending guard is shown in the error list`。
- [ ] **Step 2：跑測試確認失敗**：同 Task 5 的 WorkflowTab 指令。
- [ ] **Step 3：實作。**
- [ ] **Step 4：跑測試確認通過**：同上，另跑 `src/i18n`、`bun run typecheck`。
- [ ] **Step 5：commit** `feat(approval): 狀態機分頁標出可編輯與鎖定狀態、鎖定欄位，錯誤對到列`。

### Task 7：狀態機分頁的 390 版面與 a4a2 收尾

**Files:** `WorkflowTab.vue`、`WorkflowTab.vitest.spec.ts`、`docs/roadmap.md`

- [ ] **Step 1：寫失敗的測試**：`mobile width renders state cards without a table`、`mobile width stacks transition fields in a card`（`$q.screen.lt.sm` 以 mock 設定，斷言沒有 `q-markup-table`、轉移欄位都在 `col-12`）。
- [ ] **Step 2：實作**：< sm 時狀態改成卡片清單（狀態名＋徽章，下排「納入」「起始」「唯讀」「可編輯」四個帶標籤的勾選）；轉移列改成卡片，起訖、群組、需簽核、`noMatch`、流程數連結上下堆疊，刪除鈕在卡片右上。≥ sm 維持表格與單列，但轉移列改 `row wrap`（820 寬時群組與需簽核換行而不是溢出）。
- [ ] **Step 3：跑測試**：Task 5 指令。
- [ ] **Step 4：走查**（formwright-demo-verify，本機 Demo）：設計案模組的狀態機分頁，四寬度不橫捲；打開一條轉移的需簽核、看到流程數 0 時儲存鈕停用與說明；設計案原有的需簽核轉移顯示開關打開、目標「待主管確認」有鎖定徽章、鎖定欄位清單出現附件欄位；原樣儲存成功。
- [ ] **Step 5：gate**；roadmap a4a 列「進行中：a4a2 完成」；commit `feat(approval): 狀態機分頁手機版面` 與 `docs(approval): a4a2 進度`。

---

## a4a3：簽核流程分頁與簽核人來源編輯器

### Task 8：簽核流程分頁

**Files:**
- Create: `src/presentation/dynamic/rules/condition-summary.ts`（自 `RulesTab.vue` 抽出 `conditionSummary(n, fields, t, locale)`）；Modify: `RulesTab.vue` 改用它
- Create: `src/presentation/dynamic/approval-designer/ApprovalFlowsTab.vue`、`flow-groups.ts`（純函式：依目標狀態分組、入口轉移文字、上移下移後的鍵順序）
- Modify: `ModuleEditorPage.vue`（分頁 `approvalFlows`，`v-if="!mod?.detailOf && mod?.kind !== 'extension'"`，標籤「簽核流程」；`open-flows` 切到這個分頁並傳 `focus-target`）
- Modify: i18n 四語（`approval.designer.tab`、`approval.designer.list.*`）
- Test: `condition-summary.vitest.spec.ts`、`ApprovalFlowsTab.vitest.spec.ts`、`flow-groups.vitest.spec.ts`、`ModuleEditorPage` 既有 spec

**Interfaces:**
- `ApprovalFlowsTab` props `{ module: ModuleDetail; focusTarget?: number | null }`；emits `edit: [flow: ApprovalFlow]`、`create: [toOptionId: number]`（a4a4 接上；本段不顯示新增、編輯鈕）。
- `groupFlows(flows, workflow, locale) : { toOptionId; label; entries: string[]; hasApprovalEntry: boolean; flows: ApprovalFlow[] }[]`：分組順序照狀態機的狀態順序，組內照 `priority`。
- 卡片：名稱、鍵（caption）、啟動條件摘要（J11）、「{n} 關」、「第 {v} 版」、「進行中 {n} 張」、啟用開關；動作：上移、下移、刪除（只有 `!used`）。手機動作收進 `RowActionMenu`，桌機為圖示鈕。
- 啟停、排序見 J10 與 U4；停用、刪除被後端擋時顯示後端訊息（例：`direct` 入口需要啟用中的流程、被用過不能刪）。
- 沒有狀態機：「這個模組還沒有狀態機，請先到「狀態機」分頁建立」；`focusTarget` 有值時捲到該組並短暫標示。

- [ ] **Step 1：寫失敗的測試**
  - condition-summary：搬移前後輸出相同（複製 `RulesTab.vitest.spec.ts` 既有斷言的輸入）；`null and empty group read always`。
  - flow-groups：`groups by target in workflow state order, flows by priority`、`entries list from → to with approval marked`、`moveKeys swaps within the group only`。
  - ApprovalFlowsTab：
    - `groups flows by target state with entry transitions in the header`。
    - `card shows name, condition summary, stage count, version and pending count`。
    - `target without approval entries shows the no-entry warning`。
    - `move down sends the reordered keys for that target and notifies future-only`。
    - `deactivate asks for confirmation with the pending count, then saves active false with the same definition`。
    - `activate saves immediately`。
    - `used flow has no delete action; unused flow delete confirms and calls delete`。
    - `backend 400 on deactivate shows the message`。
    - `desktop cards are draggable and a drop reorders; mobile cards are not draggable`。
    - `read-only designer sees no toggles or actions`。
    - `module without workflow shows the create-workflow hint`。
    - `focusTarget highlights that group`。
  - ModuleEditorPage：`approval flows tab hidden for detail and extension modules`、`workflow open-flows switches to the approval flows tab`。
- [ ] **Step 2：跑測試確認失敗**：`bunx vitest run src/presentation/dynamic/approval-designer src/presentation/dynamic/rules src/pages/system --maxWorkers=2`。
- [ ] **Step 3：實作。**
- [ ] **Step 4：跑測試確認通過**：同上，另跑 `src/i18n`、`bun run typecheck`。
- [ ] **Step 5：commit** `feat(approval): 模組設計新增簽核流程分頁`。

### Task 9：收件人 chip 不誤報（a2d7 N-M1、N-M2），簽核人來源編輯器

**Files:**
- Modify: `src/application/dynamic/queries/permissions-queries.ts`（`useUsers(opts?: { enabled? })`、`useGroups(opts?)`）
- Modify: `src/presentation/policies/useRecipientNames.ts`（改呼叫上面兩個；回傳 `ready`；刪掉只給測試用的 `userLabel` 預設參數，測試改傳 i18n 版 callback）
- Modify: `PolicyValueEditor.vue`（prop `recipientsReady?: boolean`，J21）、`ModulePoliciesTab.vue`、`src/pages/system/PoliciesPage.vue`（傳 `ready`）
- Create: `src/presentation/dynamic/approval-designer/ApproverSourceEditor.vue`、`approver-source.ts`（純函式：`defaultSourceOf(kind)`、`isSingleApprover(source)`、`levelOptions(max, saved?)`、`hintOf(kind, limits, t)`）
- Modify: i18n 四語（`approval.designer.source.*`）
- Test: `PolicyValueEditor.vitest.spec.ts`、`src/presentation/policies/useRecipientNames.vitest.spec.ts`（新檔）、`ApproverSourceEditor.vitest.spec.ts`、`approver-source.vitest.spec.ts`

**Interfaces:**
- `ApproverSourceEditor` props `{ modelValue: ApproverSource; module: ModuleDetail; allowed: ApproverSourceKind[]; limits: ApproverLimits; saved?: ApproverSource | null; error?: string; label: string; disable?: boolean }`，emits `update:modelValue`。
  ```ts
  export interface ApproverLimits { maxApprovers?: number; techMaxApprovers?: number; maxManagerLevel?: number }   // undefined = 讀不到政策
  ```
- 參數：指定使用者（`useUsers` 多選 chips，可搜尋）、群組（`useGroups` 多選）、主管層級（1 到 `maxManagerLevel`；`saved` 的層級較大時加一項並標「沿用舊值」）、使用者欄位（本模組啟用中的使用者欄位＋候選群組，必填）、發起人自選（候選群組選填、最少、最多）。換來源時參數重設成該來源的預設。提示文字照 Global Constraints，數字由 `limits` 帶入；`allowed` 只有三種時（備援）顯示備援那段提示。
- 已選但不在選項裡的使用者或群組：名稱就緒前顯示原始代碼，就緒後顯示「找不到或已停用（{代碼}）」。

- [ ] **Step 1：寫失敗的測試**
  - N-M1／N-M3：`editor shows raw items, not missing, before names are ready`、`editor shows missing after ready for an unknown item`、`useRecipientNames sends no request when disabled`、`names and ready stay unset until both lists load`、`user option label uses the i18n userLabel`。
  - approver-source：`isSingleApprover for manager, userField, one user, pick max 1`、`levelOptions keeps a saved level above max and marks it`、`hint texts carry policy numbers and omit them when undefined`。
  - ApproverSourceEditor：`switching source resets params to that source's defaults`、`manager level options run from 1 to the policy max`、`users hint names the save limit and the technical limit`、`group hint says the current per-stage limit applies at activation`、`pick hint explains the effective max and the min clamp`、`fallback mode offers only users, group and manager with the fallback hint`、`user chips show missing only after users load`、`userField without candidate groups shows the error`、`every input has a label or aria-label`。
- [ ] **Step 2：跑測試確認失敗**：`bunx vitest run src/presentation/policies src/presentation/dynamic/approval-designer --maxWorkers=2`。
- [ ] **Step 3：實作。**
- [ ] **Step 4：跑測試確認通過**：同上，另跑 `src/pages/system src/i18n`、`bun run typecheck`。
- [ ] **Step 5：commit** 兩筆：`fix(policy): 收件人名稱載入前不顯示找不到或已停用` 與 `feat(approval): 簽核人來源編輯器`。

### Task 10：a4a3 收尾

- [ ] **Step 1：走查**（四寬度）：設計案模組的簽核流程分頁：卡片內容、停用確認與重新啟用（版本號不變）、上移下移（只有一個流程時停用）、手機 ⋮；模組政策分頁與系統政策頁的收件人 chip 重新整理時不閃「找不到或已停用」（DevTools 把 `/groups` 設為失敗時同樣不誤報）。
- [ ] **Step 2：gate**；roadmap 「進行中：a4a3 完成」；commit `docs(approval): a4a3 進度`。

---

## a4a4：流程編輯對話框（基本＋關卡）

### Task 11：流程草稿的純函式

**Files:**
- Create: `src/domain/dynamic/approval-flow-draft.ts`
- Test: `src/domain/dynamic/approval-flow-draft.vitest.spec.ts`

**Interfaces:**
```ts
export interface StageDraft { code: string; name: I18nText; when?: ConditionNode; approvers: ApproverSource; mode: 'all' | 'any';
  editableFields: number[]; dueHours: number | null; onEmpty: 'fallback' | 'skip' }
export interface FlowDraft { key: string; name: I18nText; toOptionId: number | null; priority: number; active: boolean;
  when?: ConditionNode; rejectTo: number | null; fallback: ApproverSource | null; stages: StageDraft[] }   // fallback null = 沿用政策
export function emptyFlowDraft(toOptionId: number | null, priority: number): FlowDraft;   // 一個新關卡
export function draftFromFlow(f: ApprovalFlow): FlowDraft;
export function newStage(existingCodes: string[]): StageDraft;        // J14、U2：最小未用的 stage-N
export function insertStage(d: FlowDraft, at: number): FlowDraft;
export function moveStage(d: FlowDraft, i: number, delta: -1 | 1): FlowDraft;
export function removeStage(d: FlowDraft, i: number): FlowDraft;       // 只剩一關時不動
export function buildFlowInput(d: FlowDraft): ApprovalFlowInput;      // shape sequence；空條件 → null；單人來源 mode → any
export function nextPriority(flows: ApprovalFlow[], toOptionId: number, exceptKey?: string): number;
export function stageErrors(errors: Record<string, string[]> | undefined, i: number): Record<string, string>;   // code／name／when／approvers／mode／editableFields／dueHours／onEmpty
```

- [ ] **Step 1：寫失敗的測試**：`newStage picks the smallest unused stage-N code`、`insert keeps existing codes and never renames`、`buildFlowInput prunes empty conditions to null`、`single approver source forces mode any`、`draftFromFlow then buildFlowInput equals the original definition`、`nextPriority is max+1 in the target, 1 when empty, ignores the flow itself`、`removeStage keeps the last stage`、`stageErrors maps definition.stages[i] keys by field`。
- [ ] **Step 2–4：** `bunx vitest run src/domain/dynamic/approval-flow-draft.vitest.spec.ts --maxWorkers=2`，先紅後綠；`bun run typecheck`。
- [ ] **Step 5：commit** `feat(approval): 簽核流程草稿的純函式`。

### Task 12：對話框外框與「基本」段，新增與編輯入口

**Files:**
- Create: `src/presentation/dynamic/approval-designer/FlowEditorDialog.vue`
- Modify: `ApprovalFlowsTab.vue`（每組「新增流程」、分頁頂端「新增流程」選目標狀態、卡片點擊與 Enter 開編輯；`focusTarget` 來自狀態機連結時直接開新增並帶入目標）
- Modify: i18n 四語（`approval.designer.dialog.*`、`approval.designer.basic.*`）
- Test: `FlowEditorDialog.vitest.spec.ts`、`ApprovalFlowsTab.vitest.spec.ts`

**Interfaces:**
- props `{ modelValue: boolean; module: ModuleDetail; workflow: WorkflowResponse; flows: ApprovalFlow[]; flow?: ApprovalFlow; presetTarget?: number | null }`。
- 版面見 J18；本段只有「基本」「關卡」兩段（試算在 Task 15 加）。
- 基本：鍵（編輯時唯讀；提示 J13）、名稱 `I18nTextInput`、目標狀態（J12）、啟動條件 `ConditionEditor allow-field-ref`（`toSchemaFields(module)`；空白說明「空白代表一律符合」）、退回狀態（「留在原狀態」＋狀態機的狀態）、備援（J16）、啟用（新增時）。
- 版本提示：編輯且 `currentVersionUsed` → Global Constraints 的版本句（`next = version + 1`、`n = pendingCount`）；新增或目標、優先序變動 →「只影響之後送出的單」。存檔成功提示「已儲存」，版本號變了加「（第 {v} 版）」。
- 錯誤：`key`（含 409「流程代碼已存在」）、`name`、`toOptionId`、`priority`、`definition.when`、`definition.rejectTo`、`definition.fallbackApprovers` 落在對應輸入；`definition.stages*` 交給 Task 13；其餘在對話框頂端清單。

- [ ] **Step 1：寫失敗的測試**
  - `create posts key, name, target, next priority, active and a sequence definition`。
  - `edit locks the key; a used flow disables the target with the reason`。
  - `target options exclude the initial state and mark states without approval entries`。
  - `used current version shows the new-version banner with the pending count`；`zero pending uses the not-affected variant`；`unused current version shows no banner`。
  - `fallback inherit shows the module default with names; custom offers users, group and manager only`。
  - `fallback default shows raw items, not missing, while names load`。
  - `empty start condition is saved as null`。
  - `field errors land on their inputs; constraint errors list at the top`。
  - `changing target on edit moves priority to the end of the new target`。
  - `mobile width opens maximized with top section tabs; desktop shows side tabs`。
  - ApprovalFlowsTab：`add in a group opens the dialog with that target`、`card click and Enter open the edit dialog`、`focusTarget from the workflow tab opens create with the target`。
- [ ] **Step 2：跑測試確認失敗**：`bunx vitest run src/presentation/dynamic/approval-designer --maxWorkers=2`。
- [ ] **Step 3：實作。**
- [ ] **Step 4：跑測試確認通過**：同上，另跑 `src/i18n`、`bun run typecheck`。
- [ ] **Step 5：commit** `feat(approval): 簽核流程編輯對話框與基本設定`。

### Task 13：「關卡」段

**Files:**
- Create: `src/presentation/dynamic/approval-designer/FlowStagesEditor.vue`
- Modify: `FlowEditorDialog.vue`（接上）、i18n 四語（`approval.designer.stage.*`）
- Test: `FlowStagesEditor.vitest.spec.ts`

**Interfaces:**
- props `{ modelValue: StageDraft[]; module: ModuleDetail; editableFieldIds: number[]; limits: ApproverLimits & { maxStages?: number; maxDueHours?: number }; saved?: ApprovalStageDef[]; errors?: Record<string, string[]>; disable?: boolean }`。
- 直向卡片串；第一張之前、每兩張之間、最後一張之後各一個「＋」（`aria-label`「在這裡插入關卡」）；關卡數達 `maxStages` 時停用並提示「關卡數上限 {max}（模組政策）」。
- 卡片：名稱、代碼（U2；`saved` 有同位置舊代碼而被改時顯示警告）、條件（`ConditionEditor`，空白「一律進入」）、簽核人來源（`ApproverSourceEditor`，五種，`saved` 傳同代碼的舊關卡）、模式「會簽（每人都要同意）／或簽（任一人同意）」（J15 隱藏）、可編輯欄位（J17）、逾期時數（空白＝不提醒，上限 `maxDueHours`，提示「超過時數未處理，簽核中心標示逾期」）、找不到人時「交給備援簽核人（預設）／跳過這一關」，選跳過時提示「整關所有來源都找不到人時才跳過，等於少一道核准」。卡片動作：上移、下移、刪除（只剩一關時停用）。
- 手機：卡片全寬、參數上下堆疊、動作收進 `RowActionMenu`。

- [ ] **Step 1：寫失敗的測試**
  - `plus between cards inserts a stage at that position with a fresh stage-N code`。
  - `add is disabled at the policy stage limit`；`no limit shown when policies fail to load`。
  - `typing a name never changes the code`；`changing a saved stage code warns it counts as a new stage`。
  - `mode hidden for manager and a single user; shown for group`。
  - `editable field options come from the workflow candidates`。
  - `due hours blank saves null; max comes from policy`。
  - `skip shows the fewer-approvals warning`。
  - `move and delete; the last stage cannot be deleted`。
  - `definition.stages[1].approvers error shows on the second card`；`errors clear after any edit`。
  - `mobile width stacks params and moves actions into the menu`。
- [ ] **Step 2–4：** `bunx vitest run src/presentation/dynamic/approval-designer --maxWorkers=2` 先紅後綠；`src/i18n`、`bun run typecheck`。
- [ ] **Step 5：commit** `feat(approval): 簽核流程的關卡編輯`。

### Task 14：a4a4 收尾

- [ ] **Step 1：走查**（四寬度，本機 Demo）：
  1. 設計案：編輯 `crm-design-case-review`（已被用過）→ 看到版本提示；只改名稱存檔版本不變；改關卡條件存檔版本＋1、卡片版本號跟著變。
  2. 在一個沒有流程的非起始狀態新增流程（兩關：群組會簽、主管第 1 層），存檔成功；再從狀態機分頁把一條進入它的轉移設成需簽核並儲存成功（先建流程再開開關的順序）。
  3. 故意讓可編輯欄位落在條件閉包，確認 400 訊息落在關卡卡片或頂端清單。
  4. 390：對話框全螢幕、上方分段、儲存鈕在底部、關卡卡片不橫捲；820、1366、1920 側邊分段。
- [ ] **Step 2：gate**；roadmap 「進行中：a4a4 完成」；commit `docs(approval): a4a4 進度`。

---

## a4a5：試算與總收

### Task 15：「試算」段

**Files:**
- Modify: `approval-types.ts`（`ApprovalPreview.flowKey?: string | null`；`PreviewStage.problem?: 'noApprover' | 'tooManyApprovers' | null`）
- Modify: `dynamic-api.ts`（`previewApprovalDraft(module, body)`）、`approval-flow-queries.ts`（`usePreviewApprovalDraft()`，mutation，不失效任何快取）
- Create: `src/presentation/dynamic/approval-designer/FlowTrialPanel.vue`、`trial-fields.ts`（J20：`trialFieldIds(definitions)`）
- Modify: `PreviewStages.vue`（顯示 `problem`：「找不到簽核人，送出或推進到這一關時會失敗」「人數超過每關上限，送出或推進到這一關時會失敗」）、`FlowEditorDialog.vue`（第三段）、i18n 四語（`approval.designer.trial.*`）
- Test: `FlowTrialPanel.vitest.spec.ts`、`trial-fields.vitest.spec.ts`、`PreviewStages` 的 spec（新檔或併入 `SubmitApprovalDialog.vitest.spec.ts`）

**Interfaces:**
- props `{ module: ModuleDetail; draft: FlowDraft; flows: ApprovalFlow[] }`。
- 輸入：發起人（`useUsers`，必填）、記錄（選填；`getPagedRecords(module, { q, pagesSize: 20 })` 搜尋，標籤用記錄標題；403／404 時說明「你沒有這個模組的讀取權，請改用欄位值試算」）、欄位值（J20 的欄位，各一個輸入；有選記錄時預填記錄值）。
- 結果：`flow` 且 `flowKey === draft.key` → 流程名稱＋`PreviewStages`；`flowKey` 不同 →「這筆會先符合流程「{name}」（優先序在前），不會走這個流程」＋那個流程的時間軸；`blocked`「沒有流程符合，送出會被擋下」；`direct`「沒有流程符合，會直接轉移」。下方註明「試算用的是編輯中的內容，視為啟用；實際簽核人依當時的組織決定」。草稿 400 → 列出錯誤、不顯示時間軸。

- [ ] **Step 1：寫失敗的測試**
  - trial-fields：`collects fields from flow and stage conditions of all flows on the target plus userField sources`、`ignores flows on other targets`。
  - FlowTrialPanel：`trial button disabled until an initiator is chosen`、`trial posts the current draft, initiator, record and overridden values keyed f_id`、`override rows list only trial fields and prefill from the record`、`other flow matched shows the which-flow banner`、`blocked and direct outcomes show their texts`、`stage problem and fallback badge are shown; skipped stage shows the skip text`、`draft 400 lists the errors instead of a timeline`、`record search 403 shows the no-read note`。
  - PreviewStages：`problem text renders; record preview without problem is unchanged`。
- [ ] **Step 2–4：** `bunx vitest run src/presentation/dynamic/approval-designer src/presentation/dynamic/approval --maxWorkers=2` 先紅後綠；`src/i18n`、`bun run typecheck`。
- [ ] **Step 5：commit** `feat(approval): 簽核流程試算`。

### Task 16：文件

**Files:**
- `docs/manual.md`：§21 開頭改寫（設計器已有畫面）；新增「21.x 設計簽核流程」：狀態機分頁的需簽核與條件都不符合時、可編輯狀態、鎖定標示與「儲存後重新計算」、建立順序（先建流程再打開需簽核）、簽核流程分頁（分組、啟停確認、排序只影響之後、被用過的只能停用）、對話框三段（含四段人數提示原文、關卡代碼預填與改代碼的警告、版本提示、試算看的是編輯中的內容）；§21.9 拿掉「簽核設計器」。文案以畫面實際文字為準。
- `docs/superpowers/specs/2026-11-10-approval-workflow-design.md`：§13.1 目標狀態下拉改成照 §20.4 第 3 條；§20.4 追加 a4a 裁決（U1–U4 結果；J3 啟停不產生新版本、J4 可編輯候選、J5／J6 試算草稿與只在設計器回報的流程鍵與問題、J7 草稿不被重抓洗掉、J21 名稱就緒前不誤報）。
- `docs/feature-checklist.md`：a4a 那一行勾掉並寫實際內容；手冊那一行改成「簽核設計器已補」；用腳本重算統計。
- `docs/todo.md`：事件設計器的「轉回草稿前改鎖定欄位」提示（a2d1 fix 新問題 1）、a2d7 N-M3 後端測試與 N-M4。
- `docs/roadmap.md`：a4a 列改「已上線」在合併後由主控改；本 Task 先寫「進行中：a4a5」。

- [ ] **Step 1：寫文件。**
- [ ] **Step 2：formwright-purpose-check**：不寫死上限（逐項對 Global Constraints）、不替模組寫特例（沒有 `moduleKey ===`）、空系統可用（沒有狀態機的模組只顯示提示）、權限不 fail-open（唯讀設計者沒有任何寫入入口，後端照樣擋）。
- [ ] **Step 3：commit** `docs(approval): a4a 手冊、設計寫回與功能清單`。

### Task 17：a4a 總收

- [ ] **Step 1：走查**（四寬度，formwright-demo-verify）：重跑 Task 7、10、14 的主要路徑做回歸；再加試算：選一筆設計案記錄與發起人試算已存流程、改條件不存檔再試一次結果跟著變、新流程沒存檔也能試、選一位沒有讀取權的設計者帳號看記錄搜尋的說明。分頁切換後量 `scrollWidth`；`rwd-check.js` 對設計頁回報 `overflow: false`。
- [ ] **Step 2：gate**：`bash scripts/unattended/gate.sh ../dynamic-fields-design.a4a`。
- [ ] **Step 3：交付說明**：本機測試、gate、走查各自宣稱；可寫死清單；pg0 補驗清單；未驗證項目（th、vi 母語、PostgreSQL）。
- [ ] **Step 4：commit**（只有走查後的修正才有）。上線交主控（formwright-ship）。

---

## pg0 合併後補驗

以 `docs/ai/testing.md` 的 PostgreSQL 雙跑補跑本段新增、標 `Category=Postgres` 的 WebAPI 測試：`GetFlows_Json_HasCurrentVersionUsed`、`GetWorkflow_Json_HasEditableFieldIds`、`DraftPreview_Json_HasFlowKeyAndProblem`、`DraftPreview_NonDesigner_Returns403`。不擋各段合併。

另補驗（a4a1 審查 S1）：PostgreSQL READ COMMITTED 下，流程存檔「目前版本是否已被用過」與送出單據並發時，定義一定寫入或明確失敗，不會回 200 卻沒存。

（a4a1 審查 S6，留到 a4a5 Task 17 收尾）兩個 handler 重複的錯誤訊息字串收成一份；「發起人自選」關卡沒有候選人時，試算是否該回問題代碼，交 a4a5 試算段一併判斷。

---

## 自我檢查紀錄

1. **§13.1 逐句對照**：轉移開關、`noMatch`、N 個流程連結、N 為 0 不能存 → Task 5；狀態可編輯開關（起始固定）、鎖定狀態與鎖定欄位、約束錯誤對到流程／轉移／狀態 → Task 6；分頁分組（入口轉移）、卡片六項、上移下移與桌機拖曳 → Task 8；基本段五項 → Task 12；關卡段卡片串、「＋」插入、來源參數、模式隱藏、可編輯欄位、逾期、找不到人時、上移下移刪除 → Task 9、13；試算 → Task 15；儲存失敗、版本提示、只影響之後、被用過沒有刪除 → Task 8、12；390 → Task 7、12、13。
2. **已拍板對照**：政策生效值與技術上限讀 API（Global Constraints）；不從名稱產生代碼（Task 11、13，U2 只決定預填與否）；人數提示與 a2d 裁決 9、判斷 17 一致（Global Constraints）；四寬度（各段收尾）；N-M1 併入（Task 9）；只驗 SQLite（pg0 補驗）；每段可獨立 fast-forward（子段表的界線說明）。
3. **後端缺口**：進行中實例數、鎖定欄位已在 main；新增四項都有 Task（1、2、3），沒有 migration。
4. **型別一致**：`CurrentVersionUsed`（Task 1）↔ `currentVersionUsed`（Task 5、12）；`EditableFieldIds`（Task 2）↔ `editableFieldIds`（Task 5、13）；`FlowKey`／`Problem`（Task 3）↔ `flowKey`／`problem`（Task 15）；`open-flows`（Task 5）↔ `focusTarget`（Task 8、12）；`ApproverLimits`（Task 9）↔ Task 12、13；`FlowDraft`（Task 11）↔ Task 12、13、15。
5. **比例**：程式碼區塊只有介面簽章；其餘是規則、測試名稱與斷言。
