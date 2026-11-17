# 簽核 a4b：送簽畫面與簽核中心 Implementation Plan

> **⏳ 等 a2d 合併。** 排程是 o1a → a2d → **a4b** → a6 → a4a → a5 → a3（使用者 2026-11-12 裁決）。a2d 七段全部合併到 origin/main 之前不開工；開工第一步是 Task 0（rebase、核對 a2d 實際產出的型別與鍵名）。

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 一般使用者在記錄詳情頁看得到簽核狀態，能送出簽核（預覽流程、自選簽核人、意見）、同意、退回、撤回；簽核人帶可編輯欄位同意；核准後的鎖定欄位在表單上顯示鎖頭；沒有模組讀取權的簽核人也打得開那一筆；主選單有「簽核」中心（待我簽核、我已處理、我送出的、全部）；鈴鐺看得懂簽核通知。做完就能接 a6 的 CRM 設計案 Demo（設計 §17.7）。

**Architecture:** 後端只補四個查詢層缺口（記錄回應帶鎖定欄位與臨時讀取旗標、schema 的臨時讀取路徑、簽核檢視補可編輯欄位與標題快照、簽核中心加狀態篩選），沒有 migration、不改任何寫入規則。前端照既有分層：`domain/dynamic/approval-types.ts`（型別與純函式）→ `services/api/dynamic-api.ts`（端點）→ `application/dynamic/queries/approval-queries.ts`（TanStack Query）→ `presentation/dynamic/approval/*`（對話框、時間軸、動作列、鎖頭）與 `pages/approvals/ApprovalCenterPage.vue`。所有動作帶 `Idempotency-Key`（每次開對話框一把）。上限一律讀 API：意見字數讀 `GET policies/effective` 的 `approval.maxCommentLength`（a2d6），自選人數讀預覽回應的 `min`／`max`。轉交、加簽、代理、終止、改派不在 a4b，動作列留擴充點給 a3。

**Tech Stack:** .NET 8、EF Core（本段只驗 SQLite，PostgreSQL 等 pg0 合併後補驗）、MediatR、xUnit＋FluentAssertions；Vue 3、Quasar 2、TanStack Query、vee-validate＋zod、vue-i18n、vitest、bun。不新增相依套件。

**Spec:**
- `docs/superpowers/specs/2026-11-10-approval-workflow-design.md`：§7（動作）、§9（鎖定與可編輯欄位）、§10（權限）、§12（通知）、§13.2–§13.4（畫面）、§15（API）、§17.5 前端、§17.7（設計案劇本）、§18、§20.4。
- a2d 計畫 `../formwright/.tmp/plans/20261112-approval-a2d/plan.md`（檔尾使用者裁決優先）：Task 1 `ApprovalLockService`、Task 3 鎖定 400 鍵、Task 6 附件 409、Task 17 `approval.maxCommentLength`。
- repo 規範：`AGENTS.md`、`.claude/rules/frontend.md`、`.claude/rules/backend.md`、`.claude/rules/docs.md`、`docs/ai/testing.md`。

**路徑慣例：** 後端相對 `poc/backend/`，前端相對 `poc/frontend/`，文件與腳本相對 repo 根目錄。worktree：`../dynamic-fields-design.a4b`（分支 `feature/a4b`，Task 0 依 workspace:worktree 建立）。本計畫引用的行號以 origin/main `18168a47` 為準，Task 0 會重新核對。

---

## 前置條件（開工前確認，不符就停下回報）

1. `git fetch && git log --oneline origin/main | head -60` 看得到 a2d 七段（a2d1–a2d7）都已合併；`git grep -n "class ApprovalLockService" origin/main -- poc/backend/src`、`git grep -n "approval.maxCommentLength" origin/main -- poc/shared/policy-catalog.json` 都有結果。
2. 沒有其他 worktree 正在改 `poc/frontend/src/i18n/*.json`（四語檔是單行 JSON，平行改必衝突）。有的話等它合併，開工時從當時的 origin/main 重讀。
3. gate 一次只跑一個；其他 worktree 在跑 gate 時，本段只跑窄範圍測試。

---

## 範圍確認

### 設計裡屬於 a4b 的項目，與後端是否就緒

證據都在 origin/main `18168a47`（a2d 的項目標「a2d」）。「就緒」指端點與欄位已存在、前端直接接。

| 設計項目 | a4b 做什麼 | 後端 | 證據 |
|---|---|---|---|
| §13.2 狀態區「送出簽核：已核准」 | 詳情頁標題列依可送簽目標各出一顆鈕 | 就緒 | `ApprovalReader.SubmitTargetsAsync`（`ApprovalQueryHandlers.cs`）經 `GET records/{id}/approval` 的 `submitTargets` 回傳；`RecordApprovalSummary.CanSubmit` 在 `GET records/{id}` |
| §13.2 送出對話框：流程名稱、關卡時間軸 | 對話框開啟時呼叫預覽 | 就緒 | `POST records/{id}/approval/preview`（`ApprovalsController.Preview`）→ `ApprovalPreviewResponse(Outcome, FlowName, Stages, Picks)` |
| §13.2 自選簽核人 | 每個 `pick` 關卡一個多選，候選與人數來自預覽 | 就緒 | `PickRequirement(StageCode, Name, Min, Max, Candidates)`；送出驗證 `SubmitApprovalHandler.PickErrors`，400 鍵 `picks.{code}` |
| §13.2 意見、送出 | 意見選填，字數上限讀政策 | 就緒（上限 a2d6） | `SubmitBody(To, RecordRowVersion, Picks, Comment)`；回應 `SubmitApprovalResponse(Outcome=submitted/approved/direct)`；`approval.maxCommentLength`（a2d Task 17） |
| §13.2 `direct` 時的說明 | 預覽 `outcome=direct` 顯示「不需簽核，會直接轉為 X」 | 就緒 | `ApprovalPreviewOutcomes.Direct`；`blocked` 時送出回 400，鍵為狀態欄位 |
| §13.2 手機全螢幕、送出鈕固定底部 | 前端 | — | — |
| §13.3 標題列徽章 | 「簽核中（第 2 關／財務）」「已核准」「已退回」等 | 就緒 | `DynamicRecordResponse.Approval`（`RecordResponses.cs:19`）＝`RecordApprovalSummary(Status, StageName, StageSeq, CanSubmit)` |
| §13.3 簽核區塊時間軸 | 關卡、任務負責人、動作時間、意見；先前的實例收合 | 就緒 | `GET records/{id}/approval` → `RecordApprovalResponse(Current, Previous, SubmitTargets)`，`ApprovalInstanceView`／`ApprovalStageView`／`ApprovalTaskView`／`ApprovalActionView`；可見範圍 `GetRecordApprovalHandler`（§10.2） |
| §13.3 同意、退回（發起人／上一關） | 動作列與對話框；退回意見必填 | 就緒 | `POST approval-tasks/{id}/approve`、`/reject`（`To=initiator|previous`）；`ApprovalTaskView.CanAct`；`ApprovalActionResponse(Outcome, InstanceId, InstanceRowVersion, NewFlowName)` |
| §13.3 同意回 `rerouted` 的提示 | 顯示「你的同意未計入」並重讀 | 就緒 | `ApproveTaskHandler`：`outcome=rerouted`、`NewFlowName`（備援時為 null） |
| §13.3 撤回（不能撤回時停用並說明） | 發起人才看得到 | 就緒 | `POST approval-instances/{id}/withdraw`；`ApprovalInstanceView.CanWithdraw` |
| §13.3 可編輯欄位「這一關可修改」 | 同意時可改（位置見待決 U1） | **缺**：檢視沒說這一關能改哪些欄位 | `ApprovalStageView` 沒有欄位清單；同意端點已收 `values`（`ApproveBody.Values`） |
| §13.3 鎖定欄位與附件的鎖頭與說明 | 表單與詳情顯示鎖頭、欄位唯讀 | **缺**：執行期 API 沒有鎖定欄位 | a2d1 的 `ApprovalLockService` 只給寫入後盾與設計器（`GET modules/{key}/workflow` 的 `LockedFieldIds`，要設計器權限）；a2d 計畫「鎖頭等畫面標示交給 a4b」 |
| §13.3 手機動作列固定底部、最多兩鈕、其餘「更多」 | 前端；「更多」在 a4b 是空的擴充點 | — | — |
| §9.1 簽核中唯讀 | 簽核中隱藏編輯、刪除；編輯頁顯示唯讀說明 | 就緒 | `ApprovalGate.RejectAsync`（PUT、DELETE 409） |
| §5.3 需簽核轉移不能用一般存檔 | 編輯頁的狀態下拉不列需簽核的目標 | 就緒 | schema `workflow.transitions[].approval`（`ModuleSchemaBuilder.WorkflowOf`）；PUT 400「這個狀態需要送出簽核」 |
| §10.1 簽核人臨時讀取（打開那一筆） | 詳情頁在沒有模組讀取權時仍能顯示該筆 | **缺**：schema 沒有臨時讀取路徑 | `GET records/{id}` 已有（`GetRecordHandler.cs:31`）；`GET modules/{key}/schema` 只認 Read（`GetModuleSchemaHandler`），簽核人會拿到 403，整頁顯示無權限 |
| §10.1 臨時讀取包含列印預覽 | 見待決 U3 | **缺** | `ApprovalReadGrant` 只用在 `GetRecordHandler`、附件下載、`GetRecordApprovalHandler` |
| §10.2 發起人與處理過的人在失去讀取權後只看歷程與標題快照 | 詳情頁退回「只看簽核歷程」畫面 | **部分缺**：`ApprovalInstanceView` 沒有標題快照 | `GetRecordApprovalHandler` 已允許；`ApprovalInstance.TitleSnapshot` 存在但沒放進檢視 |
| §13.4 簽核中心分頁 todo／done／submitted／all | 新頁 `/approvals`，主選單一項 | 就緒 | `GET approval-tasks?box=`（`GetApprovalTasksHandler`）；`all` 沒有任何 Approve 軸回 403 |
| §13.4 每列欄位 | 標題快照、模組、目前關卡、發起人、送出時間、逾期、應用程式已關閉 | 就緒 | `ApprovalInboxItem` |
| §13.4 依模組、時間篩選 | 前端 | 就緒 | `module`、`from`、`to` 查詢參數 |
| §13.4 依狀態篩選 | 前端 | **缺** | `GetApprovalTasksRequest` 沒有狀態參數 |
| §13.4 手機卡片、篩選收進抽屜 | 前端（照 `UsersPage.vue` 的表格／卡片切換） | — | — |
| §13.4 清單頁系統篩選「簽核中」 | 見待決 U2 | **缺** | 記錄清單查詢沒有簽核條件 |
| §12 通知 | 鈴鐺顯示簽核通知的種類文字；點了進記錄 | 就緒（前端缺文字） | `NotificationKinds.Approval*`（11 種）、`ApprovalNotifier` 連結 `/dynamic/{module}/{recordId}`；前端 `NOTIFICATION_KINDS`（`task-types.ts:119`）只有任務的 7 種 |
| §15 冪等 | 每個動作帶 `Idempotency-Key` | 就緒 | 所有動作是 `IRecordWriteRequest`；前端 `idem()`（`dynamic-api.ts:23`） |
| a2d 的 400／409 | 鎖定欄位 400（鍵 `f_{id}`）顯示在欄位下；附件 409 照既有通知 | 就緒（a2d） | a2d Task 3（`ApprovalMessages.FieldLocked`）、Task 6（`AttachmentLocked`、`AttachmentPosted`）；`FormPage.vue` 既有欄位錯誤顯示 |
| §17.5 前端單元測試 | 動作列依資格顯示、冪等 key | — | 見各 Task |
| §17.7 設計案 | a4b 提供送簽、同意、退回到「設計中」、時間軸、簽核中心、通知；模組、Demo 資料、劇本走查屬 a6 | — | §20.4 第 4 條（第 1 層主管、送簽限業務群組） |

### 不在 a4b

| 項目 | 歸屬 | 理由 |
|---|---|---|
| §13.1 設計器（狀態機分頁的需簽核開關、可編輯狀態、鎖定狀態與鎖定欄位標示、簽核流程分頁、試算） | a4a | roadmap a4a。a2d 計畫寫「鎖頭等畫面標示（§13.1）歸 a4b」：§13.1 的設計器部分歸 a4a，記錄表單上的鎖頭（§13.3）由 a4b 做 |
| 轉交、同意並加簽 | a3 | 沒有端點（`ApprovalsController` 只有 approve、reject、withdraw）；a4b 在動作列留擴充點（Task 11） |
| §13.5 個人代理設定、簽核中心頂端「由某某代理」 | a3 | `/me/approval-delegate` 不存在；使用者派工說明「代理按鈕不在 a4b」。功能清單 a4b 那一行的「個人代理設定」在 Task 19 改到 a3 |
| 終止、改派（簽核管理員） | a3（見待決 U4） | 沒有端點；`ApprovalInstanceView.CanCancel` 已回傳但 a4b 不用。roadmap a3 列目前沒寫這兩項 |
| 逾期提醒通知 | a3 | roadmap a3；簽核中心的逾期標示用既有 `Overdue` 欄位，a4b 做 |

### 後端缺口與建議歸屬

| 缺口 | 建議 | 落點 |
|---|---|---|
| 記錄回應沒有鎖定欄位 | a4b 自己補（查詢層，用 a2d1 的 `ApprovalLockService`） | Task 1 |
| 記錄回應沒說是不是臨時讀取 | a4b 自己補 | Task 1 |
| schema 沒有臨時讀取路徑 | a4b 自己補 | Task 2 |
| 檢視沒有這一關的可編輯欄位、沒有標題快照 | a4b 自己補 | Task 3 |
| 簽核中心沒有狀態篩選 | a4b 自己補 | Task 3 |
| 臨時讀取的列印預覽 | 建議 a3（待決 U3） | Task 15 先隱藏列印鈕 |
| 清單頁「簽核中」系統篩選 | 建議移出 a4b（待決 U2） | — |
| 終止、改派端點 | 建議 a3（待決 U4） | — |

a2d 已涵蓋、a4b 只接不改：鎖定欄位的寫入擋（400 鍵 `f_{id}`）、附件上傳 409、意見字數政策、定義守衛 409（設計器畫面，a4b 不碰）。

---

## 子段切法與合併順序

五段依序做、依序合併：每段都改四語 i18n（單行 JSON），平行必衝突；後段用前段的型別與元件。每段交給一個 subagent，最後一個 Task 跑一次 gate。

| 子段 | 內容 | Task | 估計行數（含測試） | 畫面 |
|---|---|---|---|---|
| a4b1 | 後端補齊：鎖定欄位、臨時讀取旗標、schema 臨時讀取、檢視補欄位、狀態篩選 | 1–4 | 約 550 | 無 |
| a4b2 | 前端地基與送出：型別、API、查詢；狀態機判斷排除需簽核轉移、設計器保留簽核設定；徽章、送出鈕、簽核中唯讀；送出對話框 | 5–9 | 約 900 | 有 |
| a4b3 | 簽核區塊與動作：時間軸、先前的簽核；同意、退回、撤回；手機底部動作列；a3 擴充點 | 10–12 | 約 900 | 有 |
| a4b4 | 可編輯欄位、鎖頭、臨時讀取：同意帶值；鎖定欄位鎖頭；簽核人臨時讀取的詳情頁、只看歷程 | 13–16 | 約 750 | 有 |
| a4b5 | 簽核中心與通知、總收：`/approvals` 頁、主選單、通知種類文字、手冊與設計寫回 | 17–20 | 約 850 | 有 |

另有開工前的 Task 0。合計 21 個 Task（0–20）。**沒有 migration。**

每段合併後既有行為不變的界線：a4b1 只多回應欄位與一個選用查詢參數；a4b2 起才出現新畫面。沒有任何流程的模組，畫面與現在完全相同（`record.approval` 為 null 時不顯示徽章、不發簽核請求）。

---

## 計畫判斷（技術類，可推翻）

每條：判斷／理由／選錯代價。

- **J1 沒有 migration。** 四個後端缺口都是查詢層。代價：無。實作中若發現非加不可，停下回報。
- **J2 鎖定欄位放在記錄回應 `approvalLock`，不放 schema。** 鎖不鎖看記錄目前的狀態，記錄回應本來就逐筆算；放 schema 要把流程定義併進 ETag（流程存檔不會 `TouchVersion`，`ApprovalFlowHandlers` 沒呼叫），容易讀到舊的。形狀：`{ fieldKeys: string[], detailRowsLocked: bool } | null`，只有記錄在簽核鎖定狀態時有值；`fieldKeys` 只列讀者看得到的欄位（`p.CanSee`）。代價：清單頁看不到鎖頭（清單本來就不能編輯）。
- **J3 臨時讀取旗標 `approvalGrant: bool` 放在記錄回應。** `GetRecordHandler` 已經知道 `granted`；前端據此隱藏關聯、任務、歷程、連動、轉換、列印、編輯、刪除，不必逐一等 403。代價：多一個欄位。
- **J4 schema 的臨時讀取用選用參數 `GET modules/{key}/schema?recordId={id}`。** 只有一般 Read 被拒時才看 `recordId`：記錄要存在且屬於這個模組（否則和沒有權限一樣回 403，不洩漏存在性；主控裁決 2026-11-12），`ApprovalReadGrant.ForRecordAsync` 放行才回 schema，明細權限用 `ForDetailAsync`，可做的轉移傳空清單；其餘維持原本的 403。有 Read 時忽略 `recordId`，回應與 ETag 和不帶參數相同。前端只在一般 schema 回 403 時才帶 `recordId` 重問（另一個 query key），不影響其他頁的 schema 快取。理由：沿用 a2c 的放行函式，不另寫權限。代價：簽核人的詳情頁多一個請求。
- **J5 `ApprovalStageView.EditableFieldKeys`**：只有「關卡 `active`、讀者在這一關有 `CanAct` 的任務、而且 `ApprovalRuntimeCheck.VerifyEditable(wf, inst.FromOptionId)` 通過」時才有值，其餘是空陣列；扣掉對讀者隱藏的欄位（沿用 handler 已算好的 `hidden`）。理由：只給能動作的人，不擴大揭露；鎖定狀態出發的簽核（作廢）在執行期本來就拒絕可編輯欄位（§8.5），畫面不該提供。代價：`ApprovalViews.Instance` 多兩個參數。
- **J6 `ApprovalInstanceView.TitleSnapshot`** 直接帶實例上的標題快照（§10.2「只看歷程與標題快照」）。不經欄位遮蔽：快照本來就給發起人與處理過的人看（§4.3）。
- **J7 簽核中心的狀態篩選 `status`** 只收 `ApprovalInstanceStatus` 的六個值，其他回 400（鍵 `status`，新訊息 `ApprovalStatusInvalid` 四語）；過濾在既有的記憶體分頁前做。代價：無。
- **J8 前端「狀態下拉」與「能不能推進」排除需簽核轉移。** `selectableStates`、`canAdvance`（`domain/dynamic/workflow.ts`）只看 `approval` 不為 true 的轉移；需簽核的目標只能從詳情頁的送出鈕走。理由：PUT 一定回 400（§5.3），列出來只會讓人存檔失敗。編輯頁狀態欄位下方加一行說明「要改成 X，請回詳情頁按『送出簽核』」（只在有需簽核目標時）。代價：無。
- **J9 設計器狀態機分頁存檔時保留 `approval` 與 `editable`。** 目前 `WorkflowDraft`／`buildWorkflow`（`workflow.ts:36`、`:71`）只帶 `from`、`to`、`groupIds` 與 `readOnly`，在設計器按一次儲存就會把轉移的需簽核設定與可編輯狀態清掉。a4b 只讓草稿原樣帶回這兩個屬性，不加任何編輯 UI（UI 歸 a4a）。理由：a6 的 Demo 在 a4a 之前上線，Demo 管理員一存狀態機就會讓設計案不必簽核。代價：`Workflow` 型別多兩個選用屬性。
- **J10 送簽目標取 `GET records/{id}/approval` 的 `submitTargets`，按鈕文字用狀態欄位選項的標籤。** 只在 `record.approval?.canSubmit` 為 true 時才查；`record.approval` 為 null 時整個簽核區塊不出現、也不發請求。
- **J11 冪等 key：每次開啟對話框產生一把**；網路錯誤或 5xx 重送沿用；成功或 422 換新（同 `DetailPage.vue:38` 的做法）。
- **J12 動作用實例的 `rowVersion`（`ApprovalInstanceView.RowVersion`）**；成功後失效 `['record', id]`、`['record-approval', id]`、`['record-history', id]`、`['approval-inbox']`、`['notifications']`。409 依訊息：`stale` 或「請重新整理」→ 提示並附「重新載入」；其他 409（已由他人處理、已在簽核中、找不到簽核人）照後端訊息提示並重讀簽核區塊。
- **J13 同意的 `rerouted`**：`newFlowName` 有值顯示「單據內容已變，流程改為「{flow}」重新開始，你的同意未計入」；null（改由備援）顯示「單據內容已變，改由備援簽核人重新簽核，你的同意未計入」；兩者都重讀時間軸，舊實例落在「先前的簽核」。
- **J14 退回對話框**：「退回發起人」「退回上一關」二選一；目前關卡之前沒有已同意的關卡時，「退回上一關」停用並說明「前面沒有已同意的關卡」（判斷同 `RejectTaskHandler`：`Seq` 較小且 `approved` 的關卡）。意見必填，前端先擋，後端 400 鍵 `comment` 顯示在欄位下。
- **J15 撤回**：只對發起人顯示；`canWithdraw` 為 false 時停用並說明「目前關卡已有人處理，不能撤回」。原因用固定文案，不另向後端要（`CanWithdraw` 為 false 的情況只有這一種，加上應用程式關閉，關閉時整區已顯示唯讀橫幅）。
- **J16 手機（`$q.screen.lt.sm`）動作列固定底部**：`q-page-sticky`，最多兩顆（同意、退回），其餘放 `RowActionMenu`（⋮）；a4b 沒有其他動作時不顯示 ⋮。頁面底部留出動作列高度的 padding，最後一段內容不被蓋住。
- **J17 a3 擴充點**：`approval-actions.ts` 匯出 `approvalTaskActions: ApprovalExtraAction[]`（a4b 為空陣列）與型別 `ApprovalExtraAction { key, label(t), visible(ctx), run(ctx) }`；動作列把它們放進「更多」。任務 `reason` 的顯示文字表先列全五種（resolved、delegated、transferred、added、fallback），a4b 實際只會出現 resolved 與 fallback。
- **J18 簽核中心「全部」分頁**：進頁時以 `box=all&pagesSize=1` 試探一次，403 就不顯示分頁（不重試）。理由：Approve 軸是逐模組的，前端沒有現成判斷；後端已回 403。代價：多一個小請求。
- **J19 簽核中心照 `MyTasksPage.vue` 的寫法把分頁與篩選放 URL query**（`tab`、`module`、`status`、`from`、`to`），重新整理或從通知回來狀態不丟；分頁碼不進 URL。桌機用 `q-table`＋`row-clickable`＋`tabindex="0"`＋Enter／Space（同 `UsersPage.vue:152`），手機 `grid` 卡片＋`card-clickable`。模組篩選選項用既有 `useMenu()`。
- **J20 「只看簽核歷程」畫面**：詳情頁的記錄請求回 403 或 404、而 `GET records/{id}/approval` 成功時，顯示標題快照、說明「你目前沒有這筆記錄的讀取權，只顯示你相關的簽核歷程」與時間軸（唯讀、沒有動作）；兩者都失敗才照原本顯示無權限或找不到。理由：§10.2 發起人永久可看；從「我已處理」點進來的人任務結束後就沒有臨時讀取權。
- **J21 臨時讀取時隱藏的區塊**：`PrintMenu`、`RecordConversions`、`RecordLedger`、`RecordTasks`、`RecordEffects`、`RelatedTabs`、`RecordHistory`、編輯、刪除；頂端加說明「你因為簽核任務，暫時可以查看這一筆」。§10.1 不放寬清單、匯出、反向關聯；任務與欄位歷程不在 §10.1 清單內，一併隱藏。
- **J22 鎖頭**：`DynamicForm` 對 `approvalLock.fieldKeys` 內的欄位設唯讀，欄位上方加 `lock` 圖示（`aria-label` 為說明文字）與 caption「已經過簽核，要修改請先退回可編輯的狀態」（§13.3 原文）；`detailRowsLocked` 時明細表格唯讀；附件欄位在清單內就唯讀。`DynamicDetail` 在標籤旁加同一個圖示。顏色用語意 token（`text-muted`），不寫死色碼。
- **J23 送出對話框的 `blocked`**：預覽回 `blocked` 時顯示「沒有符合條件的簽核流程，請聯絡管理員」並停用送出（後端送出也回 400）。`conditional` 關卡顯示「依條件決定」，不顯示會不會跳過（§5.4）；`pick` 關卡顯示「由你選擇」並出現自選欄位；有 `usesFallback` 顯示「備援」標記；時間軸下方註明「實際簽核人依當時的組織決定」（§6.3）。
- **J24 送出成功後留在詳情頁**：`submitted` 提示「已送出簽核」；`approved`（全部關卡都被跳過）提示「所有關卡都不需簽核，已直接核准」；`direct` 提示「已直接轉為 {狀態}」；三者都重讀記錄與簽核區塊。409「已在簽核中」關閉對話框並重讀。
- **J25 編輯頁遇到簽核中的記錄**：顯示「簽核中，記錄唯讀；要修改請先撤回」與返回鈕，不渲染表單（存檔一定 409）。
- **J26 通知種類**：前端 `NOTIFICATION_KINDS` 補齊後端 `NotificationKinds` 的 11 種簽核通知（含 a3 才會寫的 transferred、addSigned、overdue），一次給四語文字，a3 不必再改 i18n。
- **J27 走查用的流程用 API 在本機 Demo 臨時建立**（Task 0 寫好腳本放計畫資料夾，不進 repo）：設計器在 a4a、套件格式在 a5，這時沒有畫面能建流程；Demo 正式資料由 a6 產生。

---

## 使用者裁決（不再重開）

- 排程 o1a → a2d → a4b → a6 → a4a → a5 → a3；a4b 在 a2d 之後合併；pg0 未合併前只驗 SQLite，新增 WebAPI 測試照 a2d 慣例標 `[Trait("Category", "Postgres")]`，pg0 合併後補驗（a2d 裁決 16）。
- a3 的轉交、加簽、代理按鈕不在 a4b，只留擴充點（派工說明）。
- 有畫面的改動走查 390／820／1366／1920 四種寬度，不得橫向捲動（AGENTS.md）。
- 列表一致性已上線：可點列用 `.row-clickable`、手機 ⋮ 用 `RowActionMenu.vue`、鍵盤可操作。
- 待決 U1–U4 的推薦已照寫進對應 Task；裁決不同時改對應 Task（見檔尾「待決」）。

---

## Global Constraints

- **鐵則**：前端不寫 `moduleKey === '...'` 分支、不存上限常數；意見字數讀 `useEffectivePolicies(moduleKey)` 的 `approval.maxCommentLength`（讀不到時不設 `maxlength`，由後端 400 把關），自選人數讀預覽的 `min`／`max`。
- **i18n**：新句子放 `approval.*`（另加 `menu.approvals`、`notifications.kind.approval*`），zh-TW、en-US、th、vi 四語鍵集合一致、維持單行 JSON；含 `{`、`}`、`@`、`|` 用字面量寫法（`locales.vitest.spec.ts` 會編譯）。英文不得留中文。改 i18n 前先從當時的 origin/main 重讀檔案。
- **文案原文**（四語照同義翻譯）：
  - 鎖頭說明：「已經過簽核，要修改請先退回可編輯的狀態」。
  - 送出鈕：「送出簽核：{state}」。
  - `direct`：「這張單不需簽核，會直接轉為{state}」。
  - 重來：見 J13。
  - 可編輯欄位標示：「這一關可修改」。
- **RWD**：390、820、1366、1920 四種寬度不橫捲（`scripts/unattended/rwd-check.js`）；可捲動容器裡的 `column` 加 `no-wrap`；手機對話框 `maximized`，主要按鈕固定底部。
- **無障礙**：輸入框都有 `label` 或 `aria-label`；只有圖示的按鈕有 `aria-label`；可點的列與卡片 `tabindex="0"`，Enter、Space 可開。
- **顏色**：一律語意 token 或 Quasar 品牌色名（`positive`、`negative`、`warning`、`muted`），`no-hardcoded-colors.vitest.spec.ts` 要過。
- **後端**：使用者看得到的新訊息進 `MessageText` 四語表；新測試類 `protected override bool ScopePerRequest => true;`；不寫 SQLite 專屬語法；新 WebAPI 測試標 `[Trait("Category", "Postgres")]`。
- **測試**：只跑窄範圍；前端 vitest 一律 `--maxWorkers=2`；改完跑 `bun run typecheck`；改 i18n 另跑 `src/i18n`。gate 一次一個，期間不跑其他測試。
- **git**：逐檔 `git add`，commit 前 `git status`；不用 `--no-verify`、不 force push、不 merge commit；Conventional Commits，scope `approval`；不 push，由主控決定。公開 repo 不寫公司字串、個人絕對路徑、內網 IP；走查腳本不進 repo。
- **不碰** `../formwright/.tmp/plans/20261112-approval-a6/`（另一個 agent 在寫）。

---

## Review Focus

1. **沒有模組讀取權的簽核人**：從簽核中心點進記錄，畫面要出現記錄內容與動作，不是無權限頁；同意後臨時讀取權收回，同一頁要變成「只看簽核歷程」而不是錯誤。→ Task 2 `Schema_NoRead_WithOpenTask_ReturnsGrantSchema`、Task 15 `grant user sees record and actions, related panels hidden`、`after approving, record 404 falls back to history only`。
2. **設計器存檔不清掉簽核設定**：在狀態機分頁原樣按儲存，送出的本文仍帶 `approval` 與 `editable`。→ Task 6 `buildWorkflow keeps approval and editable`、`WorkflowTab save round-trips approval`。
3. **編輯頁不提供需簽核的狀態**：狀態下拉不列「已核准」；只能從詳情頁送出。→ Task 6 `selectableStates excludes approval targets`、`canAdvance ignores approval transitions`。
4. **冪等與重送**：網路錯誤後再按一次用同一把 key；成功後再開對話框換新 key。→ Task 8 `retry after network error reuses the key; success replaces it`、Task 11 同名測試。
5. **重來的同意**：`rerouted` 要說「你的同意未計入」，時間軸換成新實例。→ Task 11 `rerouted outcome shows not-counted message and refetches`。
6. **可編輯欄位只送改過的鍵**：沒改的不送，對簽核人隱藏的不出現。→ Task 3 `RecordApproval_EditableFieldHiddenForActor_NotListed`、Task 13 `only changed editable values are sent`。
7. **上限不寫死**：意見字數來自政策；自選人數來自預覽。→ Task 8 `comment counter uses policy max`、`pick count follows preview min and max`。
8. **手機底部動作列不蓋內容**：390 寬滑到底，最後一個區塊完整可見。→ Task 12 走查。

---

## Task 0：開工前 rebase 並重新核對（a2d 合併後第一件事）

**Files:** 只改本計畫與同資料夾的走查腳本，不改程式碼。

- [ ] **Step 1：確認前置條件 1–3。** 不符就停下回報。
- [ ] **Step 2：建 worktree。** 照 workspace:worktree：`git -C . worktree add ../dynamic-fields-design.a4b -b feature/a4b origin/main`；`git status --short` 乾淨。
- [ ] **Step 3：核對 a2d 實際產出**，有出入就先改本計畫再開工：
  - `ApprovalLockService.ForModuleAsync(Module, Workflow?, CancellationToken)` 與 `ApprovalLock(LockedStateIds, FieldIds, DetailRowsLocked)`、`IsLockedState(long?)` 的實際簽章（a2d Task 1）。
  - `PolicyCatalog` 的 `approval.maxCommentLength` 出現在 `GET policies/effective`（a2d 判斷 21 只排除收件人類）；前端 `EffectivePolicies` 的讀法。
  - 鎖定欄位 400 的鍵與訊息鍵（a2d Task 3）、附件 409 訊息（Task 6）。
  - 本計畫引用的檔案與行號：`GetRecordHandler.cs`、`RecordResponses.cs`、`GetModuleSchemaHandler.cs`、`SchemaController.cs`、`ApprovalQueryHandlers.cs`、`ApprovalModels.cs`、`ApprovalRequests.cs`、`ApprovalsController.cs`、`MessageText.cs`；前端 `DetailPage.vue`、`FormPage.vue`、`DynamicForm.vue`、`DynamicDetail.vue`、`workflow.ts`、`types.ts`、`dynamic-api.ts`、`task-types.ts`、`MainLayout.vue`、`routes.ts`。
- [ ] **Step 4：寫走查腳本** `../formwright/.tmp/plans/20261112-approval-a4b/walkthrough-setup.sh`（不進 repo）：對 `scripts/unattended/local-demo.sh` 起的本機 Demo（5180），以系統管理員登入後：
  1. 選一個 Demo 已開啟、有狀態機的模組（優先 CRM 報價單：`draft → pending` 設為需簽核、`noMatch=block`；`pending` 會成為鎖定狀態，`approved`、`rejected` 延伸鎖定，`draft` 是起始狀態不受影響）。用 `GET modules/{key}/workflow` 取回目前狀態機，只改那一條轉移，`PUT` 回去。
  2. `POST approval-flows` 建一個流程：目標狀態 `pending`、一關、來源群組（Demo 裡有成員、且不含送出者的群組，用 `GET groups` 確認）、或簽；另建一個含 `initiatorPick` 關卡的變體（優先序較後、`when` 用金額欄位區分）供送出對話框走查。
  3. 印出送出者與簽核人的帳號，供走查切換登入。
  DTO 形狀以 `ApprovalFlowsController`、`ApprovalFlowRequests.cs`、`ApprovalFlowModels.cs` 為準。跑一次確認能用，失敗就修腳本，不改程式碼。
- [ ] **Step 5：** 在本計畫頂端拿掉「等 a2d 合併」註記，修訂紀錄加一行「Task 0 核對完成：<日期>、<改了哪些引用>」。只改計畫檔。

---

## a4b1：後端補齊

### Task 1：記錄回應帶鎖定欄位與臨時讀取旗標

**Files:**
- Modify: `src/DynamicModules.Application/Dynamic/ApprovalService/ApprovalModels.cs`（新增 `RecordApprovalLock`）
- Modify: `src/DynamicModules.Application/Dynamic/RecordService/Data/RecordResponses.cs`（`DynamicRecordResponse` 加兩個 init 屬性）
- Modify: `src/DynamicModules.Application/Dynamic/RecordService/Queries/GetRecord/GetRecordHandler.cs`（注入 `ApprovalLockService`）
- Test: `tests/DynamicModules.ApplicationTests/Dynamic/Approvals/ApprovalRecordViewTests.cs`（新檔，`: ApprovalTestBase`）、`tests/DynamicModules.WebAPITests/Dynamic/ApprovalApiTests.cs`

**Interfaces:**
- Produces：
  ```csharp
  public sealed record RecordApprovalLock(IReadOnlyList<string> FieldKeys, bool DetailRowsLocked);
  // DynamicRecordResponse
  public ApprovalService.RecordApprovalLock? ApprovalLock { get; init; }   // 只在記錄處於簽核鎖定狀態時有值（J2）
  public bool ApprovalGrant { get; init; }                                 // 這次讀取靠簽核臨時讀取權（J3）
  ```
- Consumes：a2d Task 1 的 `ApprovalLockService.ForModuleAsync(module, null, ct)`、`ApprovalLock.IsLockedState`。
- 規則：`module.Workflow` 為 null 或 `lock.IsLockedState(wf.StateOf(values))` 為 false → `ApprovalLock = null`。`FieldKeys` 依欄位排序、只取 `p.CanSee` 的欄位。只有 `GET records/{id}` 填，清單與寫入回應不填。

- [ ] **Step 1：寫失敗的測試**（`ApprovalRecordViewTests`，`ScopePerRequest => true`；`Scenario3` 的採購單，流程條件引用 `NetTotal` 與 `Supplier`，rig 加一個附件欄位）
  - `GetRecord_LockedState_ListsLockedFieldKeys`：核准到已確認 → `ApprovalLock.FieldKeys` 含 `f_{NetTotal}`、`f_{Supplier}`、附件欄位，不含 `Memo` 與狀態欄位；`DetailRowsLocked` 等於 `ApprovalLockService` 算出的值。
  - `GetRecord_Draft_ApprovalLockNull`：草稿 → null。
  - `GetRecord_Pending_ApprovalLockNull`：草稿送出、簽核中（狀態仍是草稿）→ null。
  - `GetRecord_LockedFieldHiddenForReader_NotListed`：`HideFor` 把 `Supplier` 對讀者隱藏 → 不在 `FieldKeys`。
  - `GetRecord_ViaApprovalGrant_FlagTrue`：`RevokeAll` 後以有 open 任務的簽核人讀 → 200，`ApprovalGrant == true`。
  - `GetRecord_WithRead_FlagFalse`。
  WebAPITests（`[Trait("Category", "Postgres")]`）：
  - `GetRecord_Json_HasApprovalLockAndGrant`：已確認的單 → JSON 有 `approvalLock.fieldKeys`、`approvalLock.detailRowsLocked`、`approvalGrant: false`（camelCase）。
- [ ] **Step 2：跑測試確認失敗**
  ```bash
  cd poc/backend && dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~ApprovalRecordViewTests"
  ```
  預期：編譯失敗（屬性不存在）。
- [ ] **Step 3：實作**。`GetRecordHandler` 在組回應的 `with` 裡加兩個屬性；`granted` 已有。
- [ ] **Step 4：跑測試確認通過**：同 Step 2，另跑 `--filter "FullyQualifiedName~ApprovalReadGrantTests|FullyQualifiedName~GetRecord"` 防回歸；`dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~ApprovalApiTests"`。預期 PASS。
- [ ] **Step 5：commit** `feat(approval): 記錄回應帶簽核鎖定欄位與臨時讀取旗標`。

### Task 2：schema 的臨時讀取路徑

**Files:**
- Modify: `src/DynamicModules.Application/Dynamic/SchemaService/Queries/GetModuleSchema/GetModuleSchemaRequest.cs`（加 `long? RecordId = null`；檔名以 `grep -rn "record GetModuleSchemaRequest" src` 為準）
- Modify: `GetModuleSchemaHandler.cs`（注入 `IRecordRepository`、`ApprovalReadGrant`）
- Modify: `src/DynamicModules.WebAPI/Controllers/v3/Dynamic/SchemaController.cs`（`GetSchema(string key, [FromQuery] long? recordId)`）
- Test: `tests/DynamicModules.ApplicationTests/Dynamic/Approvals/ApprovalGrantSchemaTests.cs`（新檔）、`tests/DynamicModules.WebAPITests/Dynamic/SchemaApiTests.cs`

**Interfaces:**
- Produces：`GetModuleSchemaRequest(string ModuleKey, long? RecordId = null)`。規則見 J4：一般 Read 通過 → 照舊（忽略 `RecordId`）；被拒且 `RecordId` 有值 → 記錄不存在或不屬於本模組 → `access.Denied!`（403，不洩漏存在性）；`grant.ForRecordAsync` 為 null → 原本的拒絕結果；否則用 grant 權限組 schema，明細 `child => grant.ForDetailAsync(module, child, ct)`，`permittedTransitions: []`。

- [ ] **Step 1：寫失敗的測試**（`ApprovalGrantSchemaTests : ApprovalTestBase`，`ScopePerRequest => true`）
  - `Schema_NoRead_WithOpenTask_ReturnsGrantSchema`：`RevokeAll` 後，簽核人帶 `RecordId` → 200；`Permissions.Read == true`、`Pricing == true`、`Add`／`Edit`／`Delete == false`；`Workflow.Transitions` 為空；明細欄位存在。
  - `Schema_NoRead_RecordWithoutTask_Returns403`：沒有任務的使用者帶 `RecordId` → 403。
  - `Schema_NoRead_RecordOfOtherModule_Returns403`：`RecordId` 屬於另一個模組 → 403；另有 `_TaskOnOtherModuleRecord_Returns403`、`_RecordNotExist_Returns403`、`Schema_AfterTaskApproved_Returns403`。
  - `Schema_NoRead_NoRecordId_Returns403`：不帶 → 403（行為不變）。
  - `Schema_GrantHiddenField_NotInBlocks`：`HideFor` 對簽核人隱藏 `Supplier` → grant schema 的區塊沒有它。
  - `Schema_WithRead_RecordIdIgnored`：有 Read 時帶與不帶 `RecordId` 的回應相同（比對序列化結果與 `PermHash`）。
  WebAPITests（`SchemaApiTests`，`[Trait("Category", "Postgres")]`）：
  - `GetSchema_GrantPath_Returns200_AndStaleETagGets403NotModifiedAfterTaskEnds`：同一位使用者，一般路徑 403、帶 `recordId` 200，ETag 與有 Read 的使用者不同。
- [ ] **Step 2：跑測試確認失敗**
  ```bash
  cd poc/backend && dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~ApprovalGrantSchemaTests"
  ```
  預期：`_ReturnsGrantSchema`、`_Returns403` FAIL。
- [ ] **Step 3：實作**。`grant.ForRecordAsync` 回的權限 `Hash` 已帶 `-ag`，ETag 自然不同，不另改 `SchemaETag`。
- [ ] **Step 4：跑測試確認通過**：同 Step 2，另跑 `--filter "FullyQualifiedName~SchemaServiceTests|FullyQualifiedName~SchemaTests"`、`dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~SchemaApiTests"`。預期 PASS。
- [ ] **Step 5：commit** `feat(approval): schema 支援簽核人臨時讀取那一筆`。

### Task 3：簽核檢視補可編輯欄位與標題快照，簽核中心加狀態篩選

**Files:**
- Modify: `ApprovalModels.cs`（`ApprovalStageView` 加 `IReadOnlyList<string> EditableFieldKeys`；`ApprovalInstanceView` 加 `string TitleSnapshot`）
- Modify: `ApprovalQueryHandlers.cs`（`ApprovalViews.Instance` 加參數；`GetApprovalTasksHandler` 篩狀態）
- Modify: `ApprovalRequests.cs`（`GetApprovalTasksRequest` 加 `string? Status`）、`ApprovalsController.cs`（`[FromQuery] string? status = null`）
- Modify: `ApprovalMessages.cs`、`src/DynamicModules.Application/Common/MessageText.cs`（`ApprovalStatusInvalid` 四語：「簽核狀態只能是 pending、approved、rejected、withdrawn、cancelled、rerouted」）
- Test: `tests/DynamicModules.ApplicationTests/Dynamic/Approvals/ApprovalQueryTests.cs`、`ApprovalApiTests.cs`

**Interfaces:**
- Produces：
  ```csharp
  public sealed record ApprovalStageView(long Id, int Seq, string? StageCode, I18nText Name, string Mode, string Status, string? SkipReason,
      DateTime? DueTime, IReadOnlyList<ApprovalTaskView> Tasks, IReadOnlyList<string> EditableFieldKeys);
  public sealed record ApprovalInstanceView(..., bool CanWithdraw, bool CanCancel, string TitleSnapshot);
  public static IReadOnlyList<string> EditableKeys(ApprovalDefinition? def, ApprovalStageRun run, bool canAct, bool editableAllowed, IReadOnlySet<long>? hidden);
  public record GetApprovalTasksRequest(string Box, string? ModuleKey, DateOnly? From, DateOnly? To, int CurrentPage, int PagesSize, string? Status = null);
  ```
  `EditableKeys` 規則見 J5：`def` 取 `ApprovalEngine.DefinitionOf(inst)`；`editableAllowed` 由 handler 以 `ApprovalRuntimeCheck.VerifyEditable(wf, inst.FromOptionId) is null` 算；`hidden` 為 null（讀不到權限）時回空。`all` 與其他呼叫 `Instance` 的地方同步改參數。

- [ ] **Step 1：寫失敗的測試**（`ApprovalQueryTests` 加）
  - `RecordApproval_ActorOnEditableStage_ListsEditableFieldKeys`：第 2 關可編輯欄位 `Discount` → 第 2 關簽核人讀到的目前關卡 `EditableFieldKeys == ["f_{Discount}"]`。
  - `RecordApproval_NonActor_EditableFieldKeysEmpty`：發起人與有 Read 的旁觀者讀 → 空陣列。
  - `RecordApproval_EditableFieldHiddenForActor_NotListed`：可編輯欄位對該簽核人隱藏（測試以替身略過存檔驗證或直接寫版本）→ 不列。
  - `RecordApproval_FromLockedState_EditableFieldKeysEmpty`：作廢簽核（`from` 是鎖定狀態）的簽核人 → 空陣列。
  - `RecordApproval_InstanceHasTitleSnapshot`：`Current.TitleSnapshot` 等於送出時的標題。
  - `Inbox_StatusFilter_Rejected_OnlyRejectedInstances`：`submitted` 分頁帶 `Status = "rejected"` → 只列退回的實例，`TotalCount` 相符。
  - `Inbox_StatusInvalid_Returns400`：`Status = "x"` → 400，`Errors` 鍵 `status`。
  WebAPITests（`[Trait("Category", "Postgres")]`）：
  - `Inbox_StatusQuery_FiltersAndJsonHasEditableKeysShape`：HTTP `GET approval-tasks?box=submitted&status=pending` 只回 pending；`GET records/{id}/approval` 的 JSON 有 `editableFieldKeys` 與 `titleSnapshot`。
- [ ] **Step 2：跑測試確認失敗**
  ```bash
  cd poc/backend && dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~ApprovalQueryTests"
  ```
  預期：編譯失敗。
- [ ] **Step 3：實作**。狀態過濾併進既有 `instances = instances.Where(...)` 那一行；`Values` 集合放在 `ApprovalInstanceStatus` 旁（`ApprovalInboxBoxes.Values` 的寫法）。
- [ ] **Step 4：跑測試確認通過**：同 Step 2，另跑 `--filter "FullyQualifiedName~Approval"`（ApplicationTests 的簽核全部）與 `ApprovalRuntimeTextTests`（訊息四語）。預期 PASS。
- [ ] **Step 5：commit** `feat(approval): 簽核檢視補可編輯欄位與標題快照，簽核中心可依狀態篩選`。

### Task 4：a4b1 收尾

**Files:** `docs/roadmap.md`（a4b 列改「進行中：a4b1 完成」）。

- [ ] **Step 1：gate**：`bash scripts/unattended/gate.sh ../dynamic-fields-design.a4b`。預期每步 `exit=0`。
- [ ] **Step 2：走查**：沒有畫面改動，不做四寬度。本機 Demo 跑 `walkthrough-setup.sh` 後，以 curl 確認 `GET records/{id}` 的 `approvalLock`、`GET modules/{key}/schema?recordId=` 對簽核人回 200。
- [ ] **Step 3：pg0 補驗清單**：在本計畫檔尾「pg0 合併後補驗」列出本段新增的 WebAPI 測試名稱。
- [ ] **Step 4：commit** `docs(approval): a4b1 進度`。

---

## a4b2：前端地基與送出簽核

### Task 5：型別、API、查詢

**Files:**
- Create: `src/domain/dynamic/approval-types.ts`
- Modify: `src/domain/dynamic/types.ts`（`DynamicRecord` 加 `approval?`、`approvalLock?`、`approvalGrant?`；`SchemaWorkflow.transitions` 加 `approval?: boolean`）
- Modify: `src/services/api/dynamic-api.ts`
- Create: `src/application/dynamic/queries/approval-queries.ts`
- Test: `src/application/dynamic/queries/approval-queries.vitest.spec.ts`（新檔）

**Interfaces:**
- Produces（`approval-types.ts`，欄位名照後端 camelCase）：
  ```ts
  export type ApprovalInstanceStatus = 'pending' | 'approved' | 'rejected' | 'withdrawn' | 'cancelled' | 'rerouted';
  export interface RecordApprovalSummary { status: ApprovalInstanceStatus | 'none'; stageName: I18nText | null; stageSeq: number | null; canSubmit: boolean }
  export interface RecordApprovalLock { fieldKeys: string[]; detailRowsLocked: boolean }
  export interface ApprovalTaskView { id: number; assigneeUserId: string; assigneeName: string; originalUserId: string; reason: string; status: string; actedTime: string | null; canAct: boolean }
  export interface ApprovalStageView { id: number; seq: number; stageCode: string | null; name: I18nText; mode: 'all' | 'any'; status: string; skipReason: string | null; dueTime: string | null; tasks: ApprovalTaskView[]; editableFieldKeys: string[] }
  export interface ApprovalActionView { action: string; actorUserId: string; actorName: string; onBehalfOfUserId: string | null; targetUserIds: string[]; comment: string | null; data: unknown; createdTime: string }
  export interface ApprovalInstanceView { id: number; status: ApprovalInstanceStatus; routeKind: 'flow' | 'fallback'; flowName: I18nText | null; fromOptionId: number; toOptionId: number; initiatorUserId: string; submittedTime: string; completedTime: string | null; rowVersion: string; stages: ApprovalStageView[]; actions: ApprovalActionView[]; canWithdraw: boolean; canCancel: boolean; titleSnapshot: string }
  export interface RecordApproval { current: ApprovalInstanceView | null; previous: ApprovalInstanceView[]; submitTargets: number[] }
  export interface UserRef { userId: string; displayName: string }
  export interface PreviewStage { code: string | null; name: I18nText; status: 'enter' | 'skip' | 'conditional' | 'pick'; approvers: UserRef[]; usesFallback: boolean }
  export interface PickRequirement { stageCode: string; name: I18nText; min: number; max: number; candidates: UserRef[] }
  export interface ApprovalPreview { outcome: 'flow' | 'direct' | 'blocked'; flowName: I18nText | null; stages: PreviewStage[]; picks: PickRequirement[] }
  export interface SubmitApprovalResult { outcome: 'submitted' | 'approved' | 'direct'; instanceId: number | null; recordRowVersion: string }
  export interface ApprovalActionResult { outcome: 'advanced' | 'approved' | 'rerouted' | 'rejected' | 'returned' | 'withdrawn'; instanceId: number; instanceRowVersion: string; newFlowName?: I18nText | null }
  export type ApprovalBox = 'todo' | 'done' | 'submitted' | 'all';
  export interface ApprovalInboxItem { taskId: number; instanceId: number; recordId: number; moduleKey: string; moduleName: I18nText; titleSnapshot: string; stageName: I18nText | null; initiatorUserId: string; initiatorName: string; submittedTime: string; dueTime: string | null; overdue: boolean; moduleClosed: boolean; status: ApprovalInstanceStatus }
  export interface ApprovalInboxFilter { module?: string; status?: ApprovalInstanceStatus; from?: string; to?: string }
  ```
- Produces（`dynamicApi`）：`getRecordApproval(id)`、`previewApproval(id, to)`、`submitApproval(id, body, key)`、`approveTask(taskId, body, key)`、`rejectTask(taskId, body, key)`、`withdrawApproval(instanceId, body, key)`、`getApprovalInbox(box, filter, page, size)`；`getModuleSchema(key, recordId?)`（有 `recordId` 才帶 query）。
- Produces（queries）：`useRecordApproval(recordId: Ref<number>, enabled: Ref<boolean>)`（key `['record-approval', id]`）、`useApprovalPreview(recordId, to, enabled)`（`staleTime: 0`）、`useSubmitApproval()`、`useApproveTask()`、`useRejectTask()`、`useWithdrawApproval()`（成功後照 J12 失效）、`useApprovalInbox(box, filter, page)`（key `['approval-inbox', box, filter, page]`，`placeholderData: prev`）。

- [ ] **Step 1：寫失敗的測試**（`approval-queries.vitest.spec.ts`，用 `mockHttp`）
  - `submit posts to, recordRowVersion, picks, comment with Idempotency-Key`。
  - `approve posts rowVersion, comment and values to the task`。
  - `reject posts to=previous and comment`。
  - `withdraw posts to the instance`。
  - `action success invalidates record, record-approval, approval-inbox and notifications`。
  - `inbox passes box, module, status, from, to, currentPage, pagesSize`。
  - `getModuleSchema adds recordId only when given`。
- [ ] **Step 2：跑測試確認失敗**：`cd poc/frontend && bunx vitest run src/application/dynamic/queries/approval-queries.vitest.spec.ts --maxWorkers=2`。預期 FAIL。
- [ ] **Step 3：實作**。
- [ ] **Step 4：跑測試確認通過**：同 Step 2，另跑 `bun run typecheck`。預期 PASS。
- [ ] **Step 5：commit** `feat(approval): 前端簽核型別、API 與查詢`。

### Task 6：狀態機判斷排除需簽核轉移，設計器存檔保留簽核設定

**Files:**
- Modify: `src/domain/dynamic/workflow.ts`
- Modify: `src/domain/dynamic/types.ts`（`Workflow.states[]` 加 `editable?: boolean`；`transitions[]` 加 `approval?: { required: boolean; noMatch: 'block' | 'direct' } | null`）
- Test: `src/domain/dynamic/workflow.vitest.spec.ts`、`src/presentation/dynamic/workflow/WorkflowTab.vitest.spec.ts`

**Interfaces:**
- `selectableStates`、`canAdvance`：只看 `!t.approval` 的轉移（J8）。
- 新增 `approvalTargets(workflow, state): string[]`：從 `state` 出發、`approval === true` 的目標（給編輯頁的說明行用）。
- `WorkflowDraft.states[]` 帶 `editable?`、`transitions[]` 帶 `approval?`；`draftFromWorkflow`、`setStateIncluded`（開啟新狀態時 `editable` 不設）、`buildWorkflow` 原樣保留（J9）；不加任何 UI。

- [ ] **Step 1：寫失敗的測試**
  - `selectableStates excludes approval targets`：`draft → approved`（approval）與 `draft → cancelled` → 編輯模式只給 `draft`、`cancelled`。
  - `canAdvance ignores approval transitions`：只有需簽核轉移離開 → false。
  - `approvalTargets lists only approval transitions from the state`。
  - `buildWorkflow keeps approval and editable`：草稿來自帶 `approval`、`editable` 的狀態機 → 輸出相同。
  - `setStateIncluded off drops approval transitions touching the state`（既有行為延伸）。
  - WorkflowTab：`save round-trips approval`：mock `GET workflow` 回帶 `approval` 的轉移，什麼都不改按儲存 → `PUT` 本文的那條轉移仍有 `approval`。
- [ ] **Step 2：跑測試確認失敗**：`cd poc/frontend && bunx vitest run src/domain/dynamic/workflow.vitest.spec.ts src/presentation/dynamic/workflow --maxWorkers=2`。預期 FAIL。
- [ ] **Step 3：實作**。
- [ ] **Step 4：跑測試確認通過**：同 Step 2，另跑 `src/presentation/dynamic/components src/pages/dynamic`（`DynamicForm`、`DetailPage` 用到這兩個函式）與 `bun run typecheck`。預期 PASS。
- [ ] **Step 5：commit** `fix(approval): 編輯頁不提供需簽核的狀態，設計器存檔保留簽核設定`。

### Task 7：詳情頁徽章、送出鈕、簽核中唯讀

**Files:**
- Create: `src/presentation/dynamic/approval/approval-display.ts`（狀態 → i18n 鍵與顏色；徽章文字）
- Create: `src/presentation/dynamic/approval/ApprovalBadge.vue`
- Modify: `src/pages/dynamic/DetailPage.vue`、`src/pages/dynamic/FormPage.vue`、`src/presentation/dynamic/components/DynamicForm.vue`（狀態欄位下的需簽核說明行）
- Modify: i18n 四語（`approval.status.*`、`approval.badge.*`、`approval.submitTo`、`approval.pendingReadOnly`、`approval.useSubmitHint`）
- Test: `src/pages/dynamic/DetailPage.approval.vitest.spec.ts`（新檔，沿用 `DetailPage.vitest.spec.ts` 的掛載寫法）、`src/pages/dynamic/FormPage.vitest.spec.ts`、`approval-display.vitest.spec.ts`

**Interfaces:**
- `badgeText(summary, locale, t)`：`pending` →「簽核中（第 {seq} 關／{stage}）」；`approved`「已核准」；`rejected`「已退回」；`withdrawn`「已撤回」；`cancelled`「已終止」；`rerouted`「流程已重來，請重新送出」；`none` → null（不顯示）。顏色：pending `warning`、approved `positive`、rejected `negative`、其他 `muted`。
- DetailPage：`record.approval` 不為 null 才啟用 `useRecordApproval`；`canSubmit` 時每個 `submitTargets` 一顆鈕「送出簽核：{狀態標籤}」，按了開 Task 8 的對話框（本 Task 先放 emit／ref，對話框在 Task 8 接上）；`pending` 時隱藏編輯、刪除並顯示橫幅 `approval.pendingReadOnly`。`canEdit` 沿用 `canAdvance`（Task 6 已排除需簽核轉移）。
- FormPage：`record.approval?.status === 'pending'` → 顯示 `approval.pendingReadOnly` 與返回鈕，不渲染表單（J25）。
- DynamicForm：`approvalTargets` 非空時，狀態欄位下方 caption `approval.useSubmitHint`（列出目標狀態名稱）。

- [ ] **Step 1：寫失敗的測試**
  - `shows pending badge with stage seq and name`。
  - `no approval summary: no badge and no approval request`（`getRecordApproval` 沒被呼叫）。
  - `hides edit and delete while pending and shows read-only banner`。
  - `one submit button per submit target, labelled with the state name`。
  - `approved summary shows positive badge`。
  - FormPage：`pending record shows read-only notice without the form`。
  - DynamicForm：`status field hint names the approval targets`。
- [ ] **Step 2：跑測試確認失敗**：`cd poc/frontend && bunx vitest run src/pages/dynamic src/presentation/dynamic/approval src/presentation/dynamic/components --maxWorkers=2`。預期 FAIL。
- [ ] **Step 3：實作**。
- [ ] **Step 4：跑測試確認通過**：同 Step 2，另跑 `src/i18n`、`bun run typecheck`。預期 PASS。
- [ ] **Step 5：commit** `feat(approval): 詳情頁簽核徽章、送出鈕與簽核中唯讀`。

### Task 8：送出對話框

**Files:**
- Create: `src/presentation/dynamic/approval/SubmitApprovalDialog.vue`
- Create: `src/presentation/dynamic/approval/PreviewStages.vue`（直向時間軸，`q-timeline` 或 `q-list`，手機單欄）
- Modify: `DetailPage.vue`（接上對話框）、i18n 四語（`approval.submit.*`）
- Test: `src/presentation/dynamic/approval/SubmitApprovalDialog.vitest.spec.ts`

**Interfaces:**
- Props：`{ recordId: number; moduleKey: string; to: number; toLabel: string; recordRowVersion: string; modelValue: boolean }`；emit `update:modelValue`、`submitted(outcome)`。
- 行為：照 J23、J24；冪等照 J11；意見 `q-input type=textarea`，`maxlength`／`counter` 來自 `approval.maxCommentLength`；自選每個 `PickRequirement` 一個 `q-select multiple use-chips`（label 為關卡名稱，hint「選 {min} 到 {max} 位」），人數不在範圍內時送出鈕停用；400 鍵 `picks.{code}` 顯示在對應選單、`comment` 顯示在意見、其他鍵與狀態欄位鍵顯示在對話框頂端紅字。`$q.screen.lt.sm` 時 `maximized`，按鈕列 `q-card-actions` 固定底部。

- [ ] **Step 1：寫失敗的測試**
  - `renders flow name and stage approvers from preview`。
  - `conditional stage shows 依條件決定 and no skip wording`。
  - `pick count follows preview min and max before submit is enabled`。
  - `direct outcome shows no-approval notice with the target state`。
  - `blocked outcome disables submit`。
  - `submit body carries picks keyed by stage code and the record rowVersion`。
  - `400 picks error shows under that pick select`。
  - `retry after network error reuses the key; success replaces it`。
  - `comment counter uses policy max`；`no policy value: no maxlength`。
  - `409 already pending closes the dialog and refetches`。
- [ ] **Step 2：跑測試確認失敗**：`cd poc/frontend && bunx vitest run src/presentation/dynamic/approval --maxWorkers=2`。預期 FAIL。
- [ ] **Step 3：實作**。
- [ ] **Step 4：跑測試確認通過**：同 Step 2，另跑 `src/pages/dynamic src/i18n`、`bun run typecheck`。預期 PASS。
- [ ] **Step 5：commit** `feat(approval): 送出簽核對話框（預覽、自選簽核人、意見）`。

### Task 9：a4b2 收尾

**Files:** `docs/roadmap.md`。

- [ ] **Step 1：走查**（formwright-demo-verify；本機 Demo＋`walkthrough-setup.sh`）：390、820、1366、1920 四種寬度各走一次「草稿詳情頁 → 送出簽核鈕 → 對話框（一般流程、含自選的流程）→ 送出 → 徽章變簽核中、編輯鈕消失」，`rwd-check.js` 對詳情頁與編輯頁回報 `overflow: false`；390 寬對話框全螢幕、送出鈕在底部可見。編輯頁的狀態下拉沒有需簽核的目標。設計器狀態機分頁原樣存檔後，`GET workflow` 仍有 `approval`。
- [ ] **Step 2：gate**：`bash scripts/unattended/gate.sh ../dynamic-fields-design.a4b`。預期全部 `exit=0`。
- [ ] **Step 3：commit** `docs(approval): a4b2 進度`。

---

## a4b3：簽核區塊與動作

### Task 10：簽核時間軸區塊

**Files:**
- Create: `src/presentation/dynamic/approval/ApprovalPanel.vue`（目前實例＋「先前的簽核」`q-expansion-item` 預設收合）
- Create: `src/presentation/dynamic/approval/ApprovalStageList.vue`（每關：狀態圖示與文字、名稱、會簽／或簽、任務負責人與狀態、動作時間、期限與逾期、備援標記）
- Create: `src/presentation/dynamic/approval/ApprovalActionLog.vue`（動作、人、時間、意見；`content-changed` 列出欄位名稱與前後值；`reroute` 顯示意見文字）
- Modify: `approval-display.ts`（關卡狀態、任務狀態、任務 reason、動作名稱的 i18n 鍵）、`DetailPage.vue`（`record.approval` 不為 null 時掛 `ApprovalPanel`，位置在 `DynamicDetail` 之前）、i18n 四語（`approval.panel.*`、`approval.stageStatus.*`、`approval.taskStatus.*`、`approval.reason.*`、`approval.action.*`）
- Test: `src/presentation/dynamic/approval/ApprovalPanel.vitest.spec.ts`

**Interfaces:**
- Props：`{ approval: RecordApproval; schema: ModuleSchema | null; readonly?: boolean }`（`schema` 為 null 時 `content-changed` 只顯示欄位代碼對不上的就略過；只看歷程模式傳 null、`readonly`）。
- `content-changed` 的 `data` 形狀 `[{ fieldId, before, after }]`，欄位名稱與格式用 schema 欄位與 `fieldTypeRegistry[type].format`；schema 找不到的欄位不顯示（後端已依讀者遮蔽）。
- 時間軸容器在手機單欄；長名稱 `overflow-wrap: anywhere`。

- [ ] **Step 1：寫失敗的測試**
  - `lists stages in seq order with status labels`。
  - `skipped stage shows 已跳過`。
  - `fallback task shows 備援 badge`。
  - `content-changed entry shows field label with before and after`。
  - `reroute entry shows its note`。
  - `previous instances are collapsed under 先前的簽核`。
  - `overdue active stage shows overdue mark`。
- [ ] **Step 2：跑測試確認失敗**：`cd poc/frontend && bunx vitest run src/presentation/dynamic/approval --maxWorkers=2`。預期 FAIL。
- [ ] **Step 3：實作**。
- [ ] **Step 4：跑測試確認通過**：同 Step 2，另跑 `src/pages/dynamic src/i18n`、`bun run typecheck`。預期 PASS。
- [ ] **Step 5：commit** `feat(approval): 記錄詳情的簽核時間軸`。

### Task 11：同意、退回、撤回

**Files:**
- Create: `src/presentation/dynamic/approval/approval-actions.ts`（純函式與 a3 擴充點）
- Create: `src/presentation/dynamic/approval/ApprovalActionBar.vue`（桌機在區塊標題列；手機 `q-page-sticky` 底部）
- Create: `src/presentation/dynamic/approval/ApproveDialog.vue`、`RejectDialog.vue`、`WithdrawDialog.vue`
- Modify: `ApprovalPanel.vue`、`DetailPage.vue`（手機底部留白）、i18n 四語（`approval.actions.*`、`approval.result.*`）
- Test: `src/presentation/dynamic/approval/approval-actions.vitest.spec.ts`、`ApprovalActionBar.vitest.spec.ts`

**Interfaces:**
- Produces（`approval-actions.ts`）：
  ```ts
  export function myOpenTask(instance: ApprovalInstanceView): ApprovalTaskView | undefined;   // 目前關卡第一個 canAct 的任務
  export function canReturnPrevious(instance: ApprovalInstanceView): boolean;                  // J14
  export function currentStage(instance: ApprovalInstanceView): ApprovalStageView | undefined; // status === 'active'
  export interface ApprovalActionContext { instance: ApprovalInstanceView; task: ApprovalTaskView; recordId: number; refresh: () => void }
  export interface ApprovalExtraAction { key: string; label: (t: (k: string) => string) => string; visible: (c: ApprovalActionContext) => boolean; run: (c: ApprovalActionContext) => void }
  export const approvalTaskActions: ApprovalExtraAction[] = [];   // a3 在這裡加轉交、同意並加簽
  ```
- 行為：照 J12–J17；同意對話框只有意見（可編輯欄位在 Task 13 加）；每個對話框開啟時產生冪等 key（J11）。結果提示：`advanced`「已同意」、`approved`「已核准」、`returned`「已退回上一關」、`rejected`「已退回發起人」、`withdrawn`「已撤回」、`rerouted` 見 J13。

- [ ] **Step 1：寫失敗的測試**
  - `approver sees approve and reject; others see none`（`canAct` 決定，不看身分）。
  - `initiator sees withdraw; disabled with reason when the current stage has acted`。
  - `reject requires a comment before sending`。
  - `return to previous disabled when no earlier approved stage`。
  - `approve sends the task id, instance rowVersion and an Idempotency-Key`。
  - `retry after network error reuses the key; success replaces it`。
  - `rerouted outcome shows not-counted message with the new flow name and refetches`；`rerouted without flow name shows the fallback wording`。
  - `409 state changed offers reload`；`409 stage handled shows server message and refetches`。
  - `mobile width shows a bottom bar with approve and reject only`；`extra actions appear in the more menu`（測試裡往 `approvalTaskActions` 塞一個再清掉）。
- [ ] **Step 2：跑測試確認失敗**：`cd poc/frontend && bunx vitest run src/presentation/dynamic/approval --maxWorkers=2`。預期 FAIL。
- [ ] **Step 3：實作**。
- [ ] **Step 4：跑測試確認通過**：同 Step 2，另跑 `src/pages/dynamic src/i18n`、`bun run typecheck`。預期 PASS。
- [ ] **Step 5：commit** `feat(approval): 同意、退回、撤回與手機底部動作列`。

### Task 12：a4b3 收尾

**Files:** `docs/roadmap.md`。

- [ ] **Step 1：走查**（四種寬度）：簽核人登入 → 詳情頁看到時間軸與同意、退回 → 同意（一關流程直接核准、徽章變已核准）；另一張退回發起人（意見必填）；發起人撤回一張；兩人流程試退回上一關。390 寬底部動作列不蓋住最後一個區塊、沒有橫捲；1920 寬時間軸不被拉得過寬（`max-width` 照既有卡片）。
- [ ] **Step 2：gate**。預期全部 `exit=0`。
- [ ] **Step 3：commit** `docs(approval): a4b3 進度`。

---

## a4b4：可編輯欄位、鎖頭、臨時讀取

### Task 13：同意時修改可編輯欄位（待決 U1 推薦：在同意對話框裡）

**Files:**
- Create: `src/presentation/dynamic/approval/ApprovalEditableFields.vue`（`useForm`＋`DynamicField`，zod 用 `buildZodSchema` 對只含這些欄位的子 schema）
- Modify: `ApproveDialog.vue`、`approval-actions.ts`（`changedValues(keys, initial, current)`）、i18n 四語（`approval.editable.*`）
- Test: `src/presentation/dynamic/approval/ApproveDialog.vitest.spec.ts`

**Interfaces:**
- `ApproveDialog` 多收 `schema`、`record`；`currentStage.editableFieldKeys` 非空時顯示「這一關可修改」區段，欄位預填記錄目前值。
- 送出 `values` 只含改過的鍵（`changedValues`）；400 鍵 `f_{id}` 顯示在該欄位下，其他鍵進頂端紅字。

- [ ] **Step 1：寫失敗的測試**
  - `shows editable fields prefilled with record values under 這一關可修改`。
  - `only changed editable values are sent`。
  - `no editable keys: no editable section and values omitted`。
  - `400 on an editable field shows under that field`。
  - `editable key missing from schema is skipped`（欄位對讀者隱藏時 schema 沒有它）。
- [ ] **Step 2：跑測試確認失敗**：`cd poc/frontend && bunx vitest run src/presentation/dynamic/approval --maxWorkers=2`。預期 FAIL。
- [ ] **Step 3：實作**。
- [ ] **Step 4：跑測試確認通過**：同 Step 2，另跑 `src/presentation/dynamic/build-zod-schema.vitest.spec.ts`、`bun run typecheck`。預期 PASS。
- [ ] **Step 5：commit** `feat(approval): 簽核人同意時修改這一關的可編輯欄位`。

### Task 14：鎖定欄位的鎖頭與說明

**Files:**
- Modify: `src/presentation/dynamic/components/DynamicForm.vue`（`fieldReadonly` 加 `approvalLocked`；欄位上方鎖頭與 caption；`DetailGrid` 的 `readonly` 加 `detailRowsLocked`）
- Modify: `src/presentation/dynamic/components/DynamicDetail.vue`（標籤旁鎖頭）
- Modify: i18n 四語（`approval.lockedHint`）
- Test: `src/presentation/dynamic/components/DynamicForm.approval.vitest.spec.ts`（新檔）、`DynamicDetail` 既有 spec 或新檔

**Interfaces:**
- 鎖定集合 `approvalLockedKeys = new Set(record?.approvalLock?.fieldKeys ?? [])`，只在 `mode === 'edit'`。照 J22。狀態欄位永遠不在清單內（後端排除），解鎖轉移照常可選。

- [ ] **Step 1：寫失敗的測試**
  - `locked field is readonly with a lock icon and the hint`。
  - `field outside the lock list stays editable`。
  - `detail grid is readonly when detailRowsLocked`。
  - `attachment field in the lock list is readonly`。
  - `status field stays selectable on a locked record`。
  - DynamicDetail：`locked field label shows a lock icon with aria-label`。
- [ ] **Step 2：跑測試確認失敗**：`cd poc/frontend && bunx vitest run src/presentation/dynamic/components --maxWorkers=2`。預期 FAIL。
- [ ] **Step 3：實作**。
- [ ] **Step 4：跑測試確認通過**：同 Step 2，另跑 `src/presentation/dynamic/details src/pages/dynamic src/css/themes/no-hardcoded-colors.vitest.spec.ts`、`bun run typecheck`。預期 PASS。
- [ ] **Step 5：commit** `feat(approval): 核准後的鎖定欄位在表單與詳情顯示鎖頭`。

### Task 15：臨時讀取的詳情頁與只看歷程

**Files:**
- Modify: `src/application/dynamic/queries/schema-queries.ts`（`useRecordScopedSchema(key, recordId, enabled)`，key `['schema', key, 'record', recordId]`，不重試 4xx）
- Modify: `src/pages/dynamic/DetailPage.vue`
- Create: `src/presentation/dynamic/approval/ApprovalHistoryOnly.vue`
- Modify: i18n 四語（`approval.grantBanner`、`approval.historyOnly.*`）
- Test: `src/pages/dynamic/DetailPage.grant.vitest.spec.ts`（新檔）

**Interfaces:**
- DetailPage：一般 schema 回 403 時啟用 `useRecordScopedSchema`；`forbidden` 改成「兩個 schema 都 403 而且沒有歷程可看」。`record.approvalGrant` 時照 J21 隱藏區塊並顯示 `approval.grantBanner`。記錄 403／404 而簽核查詢 200 時顯示 `ApprovalHistoryOnly`（J20）：`titleSnapshot`（取 `current ?? previous[0]`）、說明、`ApprovalPanel readonly :schema="null"`。
- 簽核查詢在這種情況要照常發：`useRecordApproval` 的 `enabled` 改成「`record.approval` 不為 null，或記錄請求回 403／404」。

- [ ] **Step 1：寫失敗的測試**
  - `grant user sees record and actions, related panels hidden`（一般 schema 403、帶 recordId 的 schema 200、記錄 `approvalGrant: true`；`related`、`recordHistory`、`getRecordTasks` 都沒被呼叫；列印、編輯、刪除不出現）。
  - `after approving, record 404 falls back to history only`。
  - `record 403 and approval 404 shows the forbidden notice`（行為不變）。
  - `history only shows the title snapshot and no actions`。
  - `user with read does not request the record-scoped schema`。
- [ ] **Step 2：跑測試確認失敗**：`cd poc/frontend && bunx vitest run src/pages/dynamic --maxWorkers=2`。預期 FAIL。
- [ ] **Step 3：實作**。
- [ ] **Step 4：跑測試確認通過**：同 Step 2，另跑 `src/application/dynamic/queries src/presentation/dynamic/approval src/i18n`、`bun run typecheck`。預期 PASS。
- [ ] **Step 5：commit** `feat(approval): 簽核人沒有模組讀取權也能開那一筆，任務結束後只看歷程`。

### Task 16：a4b4 收尾

**Files:** `docs/roadmap.md`。

- [ ] **Step 1：走查**（四種寬度）：
  - 鎖頭：核准後的記錄（`walkthrough-setup.sh` 的流程核准一張）→ 編輯頁鎖定欄位唯讀、有鎖頭與說明，改備註可存；詳情頁標籤旁有鎖頭。
  - 臨時讀取：系統管理員在權限頁把簽核人群組對該模組的讀取關掉 → 簽核人從通知點進記錄看得到內容與動作，看不到關聯、歷程、任務、列印 → 同意後同一頁變成「只看簽核歷程」。
  - 可編輯欄位：`walkthrough-setup.sh` 另建一個帶可編輯欄位的流程（腳本補一個變體），同意對話框出現「這一關可修改」。
- [ ] **Step 2：gate**。預期全部 `exit=0`。
- [ ] **Step 3：commit** `docs(approval): a4b4 進度`。

---

## a4b5：簽核中心、通知與總收

### Task 17：簽核中心頁

**Files:**
- Create: `src/pages/approvals/ApprovalCenterPage.vue`
- Create: `src/presentation/approvals/ApprovalInboxFilters.vue`（模組、狀態、送出日期起訖；桌機一列、手機收進「篩選」對話框）
- Modify: `src/router/routes.ts`（`{ path: 'approvals', component: ... }`）、`src/layouts/MainLayout.vue`（「我的任務」下方一項「簽核」，`data-testid="approvals-menu"`）、i18n 四語（`menu.approvals`、`approval.center.*`）
- Test: `src/pages/approvals/ApprovalCenterPage.vitest.spec.ts`（新檔）、`src/router/routes.vitest.spec.ts`、`src/layouts/MainLayout.vitest.spec.ts`

**Interfaces:**
- 照 J18、J19。欄位：標題快照、模組名稱、目前關卡（`stageName`，非 pending 顯示狀態文字）、發起人、送出時間、期限（逾期加 `negative` 標記「逾期」）、`moduleClosed` 加 `muted` 標記「應用程式已關閉」。`todo` 分頁不顯示狀態篩選。每頁 20 筆、`q-pagination`。點列或 Enter／Space → `/dynamic/{moduleKey}/{recordId}`。空狀態「目前沒有待簽核的單據」等分頁各一句。

- [ ] **Step 1：寫失敗的測試**
  - `defaults to the todo tab and lists title, module, stage, initiator and submitted time`。
  - `overdue row shows the overdue badge`；`closed module row shows the closed badge`。
  - `row click and Enter navigate to the record detail`。
  - `all tab hidden when the probe returns 403`；`all tab shown when the probe succeeds`。
  - `status filter is sent and kept in the URL; todo tab has no status filter`。
  - `mobile width renders focusable cards and a filter button`。
  - routes：`/approvals resolves to the approval center`；MainLayout：`menu has the approvals entry`。
- [ ] **Step 2：跑測試確認失敗**：`cd poc/frontend && bunx vitest run src/pages/approvals src/router src/layouts --maxWorkers=2`。預期 FAIL。
- [ ] **Step 3：實作**。
- [ ] **Step 4：跑測試確認通過**：同 Step 2，另跑 `src/i18n`、`bun run typecheck`。預期 PASS。
- [ ] **Step 5：commit** `feat(approval): 簽核中心頁與主選單`。

### Task 18：通知種類文字

**Files:**
- Modify: `src/domain/dynamic/task-types.ts`（`NOTIFICATION_KINDS` 補 11 種簽核通知，J26）
- Modify: i18n 四語（`notifications.kind.approval*`，例：`approvalPending`「待你簽核：」、`approvalApproved`「已核准：」、`approvalRejected`「已退回：」、`approvalReturned`「退回上一關：」、`approvalWithdrawn`「已撤回：」、`approvalCancelled`「已終止：」、`approvalRerouted`「流程已重來：」、`approvalTransferred`「轉交給你：」、`approvalAddSigned`「加簽給你：」、`approvalOverdue`「簽核逾期：」、`approvalAdminAlert`「簽核異常：」）
- Test: `src/presentation/notifications/NotificationBell.vitest.spec.ts`

- [ ] **Step 1：寫失敗的測試**
  - `approval kinds show their label before the title`。
  - `every NOTIFICATION_KINDS entry has a label in all four locales`。
  - `approval notification link opens the record detail`（`/dynamic/m/5`）。
- [ ] **Step 2：跑測試確認失敗**：`cd poc/frontend && bunx vitest run src/presentation/notifications --maxWorkers=2`。預期 FAIL。
- [ ] **Step 3：實作**。
- [ ] **Step 4：跑測試確認通過**：同 Step 2，另跑 `src/i18n`。預期 PASS。
- [ ] **Step 5：commit** `feat(approval): 鈴鐺顯示簽核通知的種類`。

### Task 19：文件

**Files:**
- `docs/manual.md`：補簽核章節（送出簽核、預覽與自選簽核人、同意與可編輯欄位、退回兩種、撤回條件、簽核中唯讀、核准後的鎖頭、簽核中心四個分頁、沒有讀取權的簽核人看得到什麼）；文案以畫面實際文字為準。功能清單的「手冊補章節」那一行把「簽核待前端完成後補」改為已補簽核執行畫面（設計器待 a4a）。
- `docs/superpowers/specs/2026-11-10-approval-workflow-design.md`：§20.4 追加 a4b 的裁決（U1–U4 的結果；J2 鎖定欄位走記錄回應、J4 schema 臨時讀取、J20 只看歷程、J21 臨時讀取隱藏的區塊、J9 設計器保留簽核設定）；U1 選對話框時，§13.3「可編輯欄位在表單中解鎖」改寫成「在同意對話框中修改」。
- `docs/feature-checklist.md`：a4b 那一行勾掉並把描述改成實際內容（移除「個人代理設定」）；a3 那一行補「個人代理設定」，U4 照推薦時再補「終止、改派」；U2、U3 照推薦時各加一行 📋。照 `.claude/rules/docs.md` 用腳本重算統計。
- `docs/roadmap.md`：a4b 列改「已上線」（合併後）、a3 列補項目（同上）。

- [ ] **Step 1：寫文件**。
- [ ] **Step 2：formwright-purpose-check**：對照鐵則（不寫死上限、不替模組寫特例、空系統可用、權限不 fail-open）逐項確認。
- [ ] **Step 3：commit** `docs(approval): a4b 手冊、功能清單與設計寫回`。

### Task 20：a4b 總收

- [ ] **Step 1：走查**（四種寬度，formwright-demo-verify）：簽核中心四個分頁（`all` 用系統管理員）、篩選（手機對話框）、點列進詳情、鈴鐺點簽核通知進詳情；`rwd-check.js` 對 `/approvals`、詳情頁、編輯頁回報 `overflow: false`。再跑一次 a4b2–a4b4 的主要路徑做回歸。
- [ ] **Step 2：gate**：`bash scripts/unattended/gate.sh ../dynamic-fields-design.a4b`。預期全部 `exit=0`。
- [ ] **Step 3：交付說明**：本機測試、gate、走查各自宣稱；pg0 合併後補驗清單（下節）；給 a6 的接口摘要（送簽鈕條件、`walkthrough-setup.sh` 的做法、通知連結）。
- [ ] **Step 4：commit**（只有走查後的修正才有）。上線流程交主控（formwright-ship）。

---

## pg0 合併後補驗

pg0 合併後，以 `docs/ai/testing.md` 的 PostgreSQL 雙跑指令補跑本段新增、標 `Category=Postgres` 的 WebAPI 測試：`GetRecord_Json_HasApprovalLockAndGrant`、`GetSchema_GrantPath_Returns200_AndStaleETagGets403NotModifiedAfterTaskEnds`、`Inbox_StatusQuery_FiltersAndJsonHasEditableKeysShape`。不擋 a4b 各段合併（a2d 裁決 16）。

---

## 待決

### 使用者行為類（本計畫先照推薦寫，裁決不同時改對應 Task）

**U1. 簽核人要改的欄位，在哪裡改？**
- 例子：費用報支第 2 關財務可以改「核定金額」。設計 §13.3 寫「可編輯欄位在表單中解鎖並標示『這一關可修改』」，但詳情頁本身是唯讀檢視，要改得另開編輯畫面。
- 推薦：**在「同意」對話框裡改**：對話框列出這一關能改的欄位（預填目前值）、意見，按同意一次送出。理由：設計 §9.2 規定可編輯欄位「只能隨同意送出，不另開暫存」，放在同意對話框剛好一個動作一次送出；在主表單解鎖會讓人以為按「儲存」就生效，但簽核中的存檔一定被擋。代價：欄位多時對話框較長（手機全螢幕）。
- 替代：詳情頁那幾欄就地變成可編輯，按「同意」才送出；要改既有詳情元件，工作量約多一倍。
- 落點：Task 13、Task 19 的設計寫回。

**U2. 模組清單頁的系統篩選「簽核中」，a4b 要做嗎？**
- 例子：採購單清單上方多一個篩選「簽核中」，只列有進行中簽核的單據。設計 §13.4 寫「隨 4e 做」。
- 推薦：**移出 a4b，另立小段（暫稱 a4c），排在 pg0 合併之後**。理由：Demo 的設計案不需要；它要改記錄清單的查詢（子查詢實例表），正好是 pg0 要雙資料庫驗證的地方，現在做等於做兩次；簽核中心「我送出的」「全部」已能找到簽核中的單。代價：清單頁暫時篩不出簽核中的單。
- 替代：a4b 加一段做（後端查詢條件＋篩選面板），約 400 行，只驗 SQLite。
- 落點：Task 19 的功能清單。

**U3. 沒有模組讀取權的簽核人，能不能列印預覽那一筆？**
- 例子：財務沒有費用報支的讀取權，靠簽核任務打開那一筆。設計 §10.1 說臨時讀取包含列印預覽，但後端的列印端點還沒有臨時讀取路徑。
- 推薦：**a4b 先隱藏列印鈕，後端補在 a3**（a3 做代理時本來就要改臨時讀取權）。理由：Demo 的簽核人（業務主管）本來就有讀取權，列印不影響 Demo；a4b 維持只補查詢層。代價：a3 之前，沒有讀取權的簽核人不能列印。
- 替代：a4b1 一併補列印的臨時讀取（約 150 行含測試）。
- 落點：Task 15（隱藏）、Task 19。

**U4. 簽核管理員的「終止」「改派」排在哪一段？**
- 例子：主管離職，他手上的簽核卡住，管理員要把任務改派給別人，或直接終止請發起人重送。設計 §7.8 有這兩個動作，但目前沒有端點，roadmap a3 那一列也沒寫。
- 推薦：**排進 a3**，和轉交（改派的同一套邏輯）一起做；a4b 動作列留擴充點。理由：改派就是管理員版的轉交，拆開做會寫兩次。代價：a3 之前卡住的單只能由系統管理員改組織或備援名單，或請簽核人退回。
- 替代：a4b 加一段做終止（約 300 行，後端新端點＋按鈕），改派仍在 a3。
- 落點：Task 11（擴充點）、Task 19 的 roadmap 與功能清單。

### 技術類（直接決定）

- 沒有 migration（J1）。
- 鎖定欄位放記錄回應，不放 schema（J2）；只列讀者看得到的欄位。
- 臨時讀取旗標放記錄回應（J3）；schema 用選用 `recordId` 走臨時讀取，只在一般 Read 被拒時生效（J4）。
- 可編輯欄位清單只給能動作的人，扣掉隱藏欄位與鎖定狀態出發的簽核（J5）。
- 實例檢視帶標題快照（J6）；簽核中心狀態篩選只收六個值（J7）。
- 編輯頁的狀態下拉與「能不能推進」排除需簽核轉移（J8）。
- 設計器狀態機存檔保留 `approval`、`editable`，不加 UI（J9）。⚠️ 這是現有缺陷：目前在設計器存一次狀態機就會清掉需簽核設定。
- 送簽目標從記錄的簽核查詢取（J10）；冪等 key 每開一次對話框一把（J11）；動作用實例 rowVersion、成功後失效五組查詢（J12）。
- 重來、退回、撤回的文案與停用條件（J13–J15）。
- 手機底部動作列與 a3 擴充點（J16、J17）。
- 簽核中心「全部」分頁以試探請求決定顯示（J18）；篩選放 URL（J19）。
- 只看歷程的退回畫面（J20）；臨時讀取時隱藏的區塊（J21）；鎖頭呈現（J22）。
- 送出對話框的 `blocked`、`conditional`、`pick` 呈現與送出後行為（J23、J24）；編輯頁遇簽核中不渲染表單（J25）。
- 通知種類一次補齊 11 種（J26）；走查流程用 API 腳本臨時建立、不進 repo（J27）。
- 五段依序合併（i18n 單行 JSON 不平行）。

---

## 自我檢查紀錄

1. **設計涵蓋**：§13.2 → Task 7、8；§13.3 → Task 7（徽章）、10（時間軸）、11（動作、手機）、13（可編輯欄位）、14（鎖頭）；§13.4 → Task 3（狀態篩選）、17；§13.5 → a3（範圍確認）；§10.1 → Task 1、2、15；§10.2 → Task 3、15；§12 → Task 18；§15 冪等 → Task 8、11；§17.5 前端 → 各 Task 測試；§17.7 → a4b 提供的畫面（範圍確認），劇本屬 a6。
2. **後端證據**：每個「就緒」都附了端點或型別；「缺」的五項都有 Task 或待決。
3. **步驟掃描**：每個 Task 有 Files、Interfaces（純文件 Task 除外）、測試名稱與斷言、指令與預期、commit。
4. **型別一致**：`RecordApprovalLock`（Task 1）↔ `approvalLock`（Task 5、14）；`approvalGrant`（Task 1）↔ Task 15；`EditableFieldKeys`（Task 3）↔ `editableFieldKeys`（Task 5、13）；`TitleSnapshot`（Task 3）↔ Task 15；`status` 查詢（Task 3）↔ `ApprovalInboxFilter.status`（Task 5、17）；`getModuleSchema(key, recordId?)`（Task 5）↔ Task 2、15；`approvalTaskActions`（Task 11）留給 a3。
5. **Review Focus**：八項都對到負責 Task 的測試或走查。
6. **比例**：程式碼區塊只有介面簽章，其餘是測試名稱、斷言與判斷。

## 使用者裁決（2026-11-12）

- U1：可編輯欄位在「同意」對話框裡改（照推薦）。
- U2：清單頁「簽核中」篩選移出 a4b，另立 a4c 排在 pg0 之後（照推薦）。
- U3：沒有讀取權的簽核人，a4b 先隱藏列印鈕，後端臨時讀取列印補在 a3（照推薦）。
- U4：終止、改派排進 a3，與轉交一起；a4b 留擴充點（照推薦）。

## 排程變更（使用者 2026-11-12，優先於 Task 0「等 a2d 七段合併」）

順序改為 a2d1 → **a4b** → a6 → a2d2–a2d7 → a4a → a5 → a3。a4b 只等 a2d1 合併（`ApprovalLockService`、`ApprovalFieldLocked` 訊息、`IWriteJournal` 輸入界線已在 a2d1）。Task 0 改為：rebase 到含 a2d1 的 main，核對 a2d1 實際介面（見 ../formwright/.tmp/plans/20261112-approval-a2d/a2d1-result.md）；凡依賴 a2d2–a2d7 才有的東西（附件鎖定 409、定義守衛、政策值、收件人政策），a4b 只接既有錯誤碼的通用顯示，不做專屬 UI，並列進交付說明。鎖定違規錯誤鍵：表頭 `f_{id}`、明細 `details.{明細模組key}`；訊息帶欄位名稱後綴。
