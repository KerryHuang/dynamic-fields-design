# MCP 一般寫入 mcp2a Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 讓 AI client 用 `business.write` 範圍的權杖，經 `/mcp` 建立與修改記錄、建任務與改任務、加任務留言、送簽與撤回。內容：
- 範圍：`business.write` 可建立；個人頁建立權杖時寫入範圍預設不勾（使用者 2026-11-21 裁決）。
- 7 個一般寫入工具，全部重用畫面的同一條 MediatR request；結果（記錄值、歷程、事件事實、檢查、任務規則、通知）與畫面建立相同。
- 寫入稽核與業務寫入同一交易（設計 §8.1、T14、B12）。
- 冪等鍵：工具參數 `idempotencyKey` 接到 `IOperationContext.ClientKey`，`IdempotencyBehavior` 接手；任務與留言也納入。
- 寫入迴圈偵測（C3 使用者裁決）：同一權杖在時間窗內反覆寫同一筆記錄達 `ops.ruleLoopTriggersPerRecord` 次就擋下並開維運事件。
- 寫入端的「不提供給 AI」語意（設計 §5.5 留給 mcp2a 審）。

**使用者看得到的行為：**
- 個人「AI 存取權杖」頁的建立對話框多一個可勾的「新增與修改業務資料」範圍，預設不勾。
- 政策頁「維運」分類多一到兩項迴圈偵測政策（Q5）。
- 稽核頁出現 `mcp.createRecord` 等 7 個寫入動作。
- 用 AI 寫入的記錄，在畫面上和本人親手存的一樣（歷程記在權杖擁有者名下，Q7）。

**Architecture:**
- **沒有 migration**。冪等沿用 `Dynamic_Operation`；迴圈計數與每日名額都走稽核表既有的索引（`TargetKind, TargetId, OccurredUtc`、`TokenId, OccurredUtc`）。
- **工具**：`Application/Dynamic/McpService/Tools/Write/`，每個工具只依賴 `ISender`（沿用 `McpToolArchitectureTests`），只送既有 request，不另寫查詢與權限判斷。
- **寫入稽核**：新 behavior `McpWriteAuditBehavior` 放在 `McpConfirmationBehavior` 之後、`AuditBehavior` 之前（交易內、冪等之外）。執行器在呼叫寫入工具前先「上膛」本次的稽核內容；behavior 在最外層交易型請求成功後 `IAuditLog.Add` 並存檔，與業務同一交易；提交後標記「本次呼叫已提交」，執行器就不再寫第二列（判斷 3）。
- **冪等**：執行器在呼叫工具前把 `idempotencyKey` 設進 scoped 的 `IOperationContext.ClientKey`；`IdempotencyBehavior` 的判斷從 `IRecordWriteRequest` 放寬到新 marker `IIdempotentRequest`（`IRecordWriteRequest` 繼承它），任務與留言的 request 加上這個 marker（判斷 5）。
- **迴圈偵測**：執行器在範圍與速率之後、工具執行之前，對宣告了「迴圈目標」的工具查稽核表（判斷 8）。

**Tech Stack:** .NET 8、MediatR、EF Core（SQLite、PostgreSQL）、xUnit＋FluentAssertions、`ModelContextProtocol.AspNetCore` 2.4.0（mcp1a 已引入）；前端 Vue 3＋Quasar、vitest。

**Spec:** `docs/superpowers/specs/2026-11-11-mcp-server-design.md`，涵蓋：
- §5.2 範圍（`business.write`）、§5.5 已知限制第三點（寫入端 AiMasked）。
- §6.1、§6.2（一般寫入的 annotations、`idempotentHint`）、§6.5。
- §7.2 mcp2a 一般寫入。
- §8.1 寫入工具的稽核、§8.2（Q8：被擋、失敗、取消都計入每日名額，使用者 2026-11-21 裁決，mcp1a 已實作，本段沿用）。
- §12 mcp2a 列、§13.5「建立對話框」、§16 U12、§20 C3。
- 維運設計 `2026-11-11-ops-observability-design.md` §5.4（C3 範圍）、政策表 `ops.ruleLoopTriggersPerRecord`（出廠 30、5–1000）。

repo 規範：`AGENTS.md`、`.claude/rules/{backend,frontend,docs}.md`、`CONTRIBUTING.md`、`docs/ai/testing.md`。

**路徑慣例：** 同 mcp1a 計畫。後端相對 `backend/`；`Application/…` 是 `backend/src/DynamicModules.Application/…` 的縮寫，其餘專案同理。符號以 main `53c5c02d` 為準，不寫行號。

**修訂：**
- 2026-11-21 初版。

---

## 前置條件（開工前逐項確認，不符就停下回報）

1. **mcp1a、mcp1b 在 main**：`grep -n "BusinessWrite" backend/src/DynamicModules.Domain/Mcp/McpScopes.cs` 有結果，`ls frontend/src/presentation/mcp/CreateAiTokenDialog.vue` 存在。
2. **mcp2a 沒開工**：`grep -rn "McpWriteAuditBehavior\|IIdempotentRequest\|ruleLoopTriggersPerRecord" backend/src shared` 無結果。
3. **#74／#75（畫面通道遮罩）的狀態**：`gh issue view 74`、`gh issue view 75`，以及 `git log origin/main --oneline | grep -E "#74|#75"`。
   - 已合併：Task 5 直接以 main 為準。
   - 未合併：Task 1–4 照做；Task 5 開工前 rebase 到含 #74／#75 的 main，還沒合併就先做 Task 6–7，最後才做 Task 5（見「與 #74／#75 的衝突點」）。
4. **沒有別的段同時改 `shared/i18n/*/ui.json` 的 `policies.items.ops.*`、`audit.actions.mcp.*`、`aiTokens.*`**；有就先協調誰先合。
5. **PostgreSQL 測試環境可用**：`docker info` 成功，或設了 `FORMWRIGHT_TEST_PG`。
6. **plugin 更新**（AGENTS.md「票」）：`claude plugin marketplace update plugharbor && claude plugin update tickets@plugharbor --scope project`，再 `/reload-plugins`；失敗就照舊版跑，記進回報。
7. **worktree**：`../formwright-mcp2a`，分支 `feature/mcp2a`。

## 盤點：既有寫入路徑與重用方式

工具一律送下表的 request，不另寫 handler。「副作用」欄是畫面存檔時就會發生的事，MCP 走同一條，所以自動相同；對照測試逐項證明（Task 4、6、7）。

| 工具 | request（marker） | 畫面端點 | 副作用（誰做） |
|---|---|---|---|
| create_record | `CreateRecordRequest`（`IRecordWriteRequest`） | `POST modules/{key}/records` | handler：歷程（`RecordHistoryWriter.Created`）、自動編號、公式與規則（`RecordValueProcessor`）、索引、彙總、參照；`RecordEffectsBehavior`：整合守衛、事件與事實、後盾、檢查、任務同步、任務規則（`IWriteStage` Order 7–12） |
| update_record | `UpdateRecordRequest`（`IRecordWriteRequest`） | `PUT records/{id}` | 同上，歷程用 `history.Updated`；rowVersion 不符回 `ServiceResult.Stale()` |
| create_task | `CreateTaskRequest`／`CreateRecordTaskRequest`／`CreateSubtaskRequest`（`ITransactionalRequest`） | `POST tasks`、`POST records/{id}/tasks`、`POST tasks/{id}/subtasks` | `TaskWriter`：系統事件、參與者、通知（`FlushAsync`）；不跑 `IWriteStage` |
| update_task | `UpdateTaskRequest(Id, Patch)`（`ITransactionalRequest`） | `PATCH tasks/{id}` | 同上；沒有版本比對；可能回 `TaskHiddenAfterWrite` |
| add_task_comment | `AddTaskCommentRequest(TaskId, Body, Files: [])`（`ITransactionalRequest`） | `POST tasks/{id}/comments`（JSON） | `@提及` 解析成參與者、通知 |
| submit_approval | `PreviewRecordApprovalRequest`（唯讀）→ `SubmitApprovalRequest`（`IRecordWriteRequest`） | `POST records/{id}/approval/preview`、`…/submit` | 簽核引擎歷程、通知、換記錄 RowVersion；`IWriteStage` 照跑 |
| withdraw_approval | `WithdrawApprovalRequest(InstanceId, RowVersion, Comment)`（`IRecordWriteRequest`） | `POST approval-instances/{id}/withdraw` | 簽核歷程、`ApprovalWithdrawn` 通知 |

- 交易外的 `RuleFailureBehavior`（規則失敗計數）、`ApprovalAlertBehavior`（簽核警示）對 MCP 照常生效，不改。
- 寫入權限全在既有路徑：記錄是 `DynamicAccessGuard.AuthorizeAsync`／`AuthorizeDetailAsync`、`ModuleGate`、`LedgerGate`、`ApprovalGate`、`EventFactLock`；任務是 `TaskAccess.EvaluateAsync`；簽核是 `ApprovalSubmitEligibility`。工具層沒有權限捷徑。
- 畫面通道的隱藏與唯讀欄位：`RecordValueProcessor` 靜默忽略輸入、保留舊值、新增只套預設、不檢查必填；凍結欄位例外（看得到又送了不同值回 409）。MCP 通道的「不提供給 AI」欄位經 `ChannelPermissionRepository` 變成隱藏欄位，所以沿用這套語意（Q1）。
- 記錄留言不存在；要對記錄留言，先 create_task（帶 `recordId`）再 add_task_comment。
- 不在本段：刪除記錄、簽核同意與退回（mcp2b 高風險）；看板拖拉、甘特改日期、撤回留言、復原主管重派、匯入、轉換、沖銷。

## 子段切法（三個實作者交棒點）

| 批 | 內容 | Task | 手寫行數（估，含測試） | migration |
|---|---|---|---|---|
| mcp2a-1 | 範圍與建立對話框、寫入稽核同交易與錯誤對照、冪等鍵 | 1–3 | ≈ 1200（測試約 55%；另有約 40 行四語文案） | 無 |
| mcp2a-2 | 記錄工具與 REST 對照、寫入端「不提供給 AI」、任務與留言工具 | 4–6 | ≈ 1450（測試約 60%） | 無 |
| mcp2a-3 | 簽核工具、迴圈偵測與政策、文件、走查、gate、實機驗收 | 7–9 | ≈ 1150（測試約 55%） | 無 |

- 三批同一分支，每個 Task 各自 commit；mcp2a-3 收尾才跑 gate 並合併，**三批一起上線**（Q9）。
- mcp2a-1、mcp2a-2 收尾各跑一次本批的窄範圍 PostgreSQL 雙跑，不跑 gate。
- **交棒條件**：
  - **mcp2a-1 → mcp2a-2**：Task 1–3 驗收全過（含 PostgreSQL）。交棒說明寫：`IMcpWriteScope` 的形狀與「上膛」的時機；`IIdempotentRequest` 加在哪些 request；`McpToolOutcome` 新增的欄位；測試怎麼用 `McpTestKit` 建 `business.write` 權杖。
  - **mcp2a-2 → mcp2a-3**：Task 4–6 驗收全過。交棒說明寫：寫入工具的共用基底（`WriteTool`）與 `ignoredFields` 的算法；對照測試的造模方式；#74／#75 是否已合併、Task 5 以哪個 main SHA 為準。
- 分界理由：
  - mcp2a-1 是機制，審查重點是稽核與業務同交易、提交後不寫第二列、冪等鍵不跨使用者、錯誤代碼對照。
  - mcp2a-2 是記錄與任務工具，審查重點是 REST／MCP 結果相同、被遮欄位寫不進也問不出存在。
  - mcp2a-3 是簽核與迴圈，審查重點是送簽預覽與 rowVersion、迴圈計數不被重送灌水、政策單一來源。

---

## 計畫期間的判斷（規劃者決定，可推翻）

1. 〔技術〕**工具清單 7 個，範圍都是 `business.write`、級別 `McpRisk.Write`**（設計 §7.2、U12）。`mark_notifications_read` 不在本段（Q6）。稽核動作 camelCase：`mcp.createRecord`、`mcp.updateRecord`、`mcp.createTask`、`mcp.updateTask`、`mcp.addTaskComment`、`mcp.submitApproval`、`mcp.withdrawApproval`，在 `AuditActions` 登記為 `W`。

2. 〔技術〕**工具參數（JSON Schema 固定英文，放 `McpTexts`）**：
   - `create_record`：`moduleKey`（必填）、`values`（物件，鍵是 describe_module 的欄位 key，值的格式同 REST）、`details`（物件，鍵是明細模組 key，值是列陣列，每列只有 `values`）、`hostKey`（擴充模組才要）、`idempotencyKey`（必填）。
   - `update_record`：`recordId`、`rowVersion`（必填，query_records／get_record 給的）、`values`、`details`（每列 `id`＋`rowVersion`＋`values` 是修改、沒 `id` 是新增；`_delete` 不收，Q3）、`idempotencyKey`。
   - `create_task`：`title`（必填，≤ `TaskEditParser.TitleMax`）、`description`、`assigneeUserId`、`dueDate`、`startDate`、`recordId`（記錄任務）、`parentTaskId`（子任務）、`idempotencyKey`。`recordId` 與 `parentTaskId` 同時帶回 `invalid_arguments`；依參數選三個 request 之一。
   - `update_task`：`taskId`、`status`（`todo`／`inProgress`／`done`／`cancelled`，與 `TaskWriter.StatusText` 同步，同 mcp1a 判斷「schema 列舉與 handler 合法值同步」）、`assigneeUserId`、`dueDate`、`startDate`、`cascade`（`complete`／`cancel`）、`idempotencyKey`。帶了才放進 `Patch`，帶 `null` 代表清除（同 REST）。標題與描述不開放（設計 §7.2 只列狀態、負責人、到期日；`startDate`、`cascade` 是到期日與完成的配套）。
   - `add_task_comment`：`taskId`、`body`（1–`TaskCommentRules.BodyMax` 字）、`idempotencyKey`；`Files` 固定空清單（設計：不含附件上傳）。
   - `submit_approval`：`recordId`、`to`（目標狀態選項 id，從 get_record_approval 的 `submitTargets` 取）、`rowVersion`（記錄的）、`picks`（關卡代號 → userId 陣列）、`comment`、`previewOnly`（預設 false）、`idempotencyKey`。
   - `withdraw_approval`：`instanceId`、`rowVersion`（實例的，從 get_record_approval 的 `current.rowVersion` 取）、`comment`、`idempotencyKey`。
   - 長度與上限沿用既有 request 的常數（`TaskEditParser.TitleMax`、`TaskCommentRules.BodyMax`），不在 schema 另寫一份數字以外的規則；schema 的 `maxLength` 由常數插值產生，同一個數字只有一個來源。

3. 〔技術〕**寫入稽核與業務同交易：`McpWriteAuditBehavior`**（設計 §8.1、T14、B12；設計原寫「透過 `IWriteStage`」，但任務與留言不跑 `IWriteStage`，見「與設計的矛盾」第 2 點）。
   - 新 scoped 服務 `IMcpWriteScope`（`McpService/Writes/`）：`Arm(long call, McpWriteAudit audit)`、`IsCommitted(long call)`。`McpWriteAudit` 帶動作代碼、工具名、參數雜湊、摘要。
   - 執行器在寫入工具 `RunAsync` 之前 `Arm`；工具送出的**第一個最外層**交易型請求（`AfterCommitActions.TransactionDepth == 1`）由 behavior 接手：成功時 `audit.Add(Success, TargetKind／TargetId 由 McpWriteTargets 算)`、`uow.CommitAsync`、`afterCommit.Add` 提交標記；失敗照常回傳（交易已回滾，執行器照 mcp1a 結束路徑以 `WriteNowAsync` 寫失敗列）。
   - 同一次呼叫的第二個最外層交易型請求 → 丟 `InvalidOperationException`（fail closed；工具一次只能送一個寫入請求，submit_approval 的預覽不是交易型）。
   - 請求同時是 `IAuditedRequest` 或 `IMcpConfirmedRequest` → 丟例外（一次呼叫只能一列；架構測試另外掃）。
   - 沒有上膛、或不是 MCP 通道 → 直接放行（畫面行為不變）。
   - `McpWriteTargets`（純函式，依 request 型別）：`CreateRecordRequest` → `record`／回應的 id；`UpdateRecordRequest` → `record`／`Id`；三種建任務 → `task`／回應的 id；`UpdateTaskRequest`、`AddTaskCommentRequest` → `task`／`Id`、`TaskId`；`SubmitApprovalRequest` → `record`／`RecordId`；`WithdrawApprovalRequest` → `approvalInstance`／`InstanceId`。回應可能是型別物件，也可能是冪等回放的 `JsonElement`，兩種都要讀得到 `id`。
   - 摘要多記 `op`（`IOperationContext.OperationId`，可串到事件事實），回放時 `ReasonCode = "replayed"`（判斷 8 依此排除）。
   - 執行器的「已提交」判斷改成 `confirmations.IsCommitted(call) || writes.IsCommitted(call)`，提交後格式化失敗或放不下，沿用 mcp1a 的「已執行、結果省略」（`McpDataOmitted`），不回錯誤、不寫第二列。

4. 〔技術〕**handler 失敗的錯誤代碼對照補 `conflict`**（設計 §6.5 只列 stale；mcp1a 的 `McpToolInvoker.Mapped` 把所有 `IsConflict` 都對成 `stale`，唯讀工具沒差，寫入工具會誤導模型去重讀）：
   - `Conflict` 且 `Code == "stale"` → `stale`（同現在）。
   - 其他 `Conflict`（檢查失敗、`subtasksOpen`、`AlreadyPending`、`CannotWithdraw`、凍結、模組關閉、host 重複）→ 新代碼 `conflict`，訊息照 handler（依語系），`fields` 帶 handler 的 `code` 與 `params`（例如 `subtasksOpen` 的 `count`），模型能據此帶 `cascade` 重試。
   - `Unprocessable` 且訊息是 `MessageText.IdempotencyKeyReused` → 新代碼 `idempotency_key_reused`（不改 REST 的 422 回應）。
   - `Forbidden` 維持一律「沒有權限」、不透露存在（`SupervisorOnlyAssignee` 的細訊息因此不給 AI，已知限制）。

5. 〔技術〕**冪等鍵**（設計 §7.2「`IdempotencyBehavior` 自動接手」）：
   - 七個工具的 `idempotencyKey` 都**必填**，格式 `OperationContext.KeyPattern`（`^[A-Za-z0-9_-]{8,64}$`），不符回 `invalid_arguments`。理由：AI client 逾時重送是常態；選填的話模型多半不帶，重送就重複建記錄與留言。必填讓 annotations 能一律標 `idempotentHint: true`（判斷 10）。
   - 執行器在工具 `RunAsync` 前把它設進 scoped `IOperationContext.ClientKey`（stateless 模式下一次 `tools/call` 一個 DI scope，不會串到別次呼叫）；工具結束後讀 `Replayed` 放進 `meta.replayed`。
   - `IdempotencyBehavior` 的判斷從 `IRecordWriteRequest` 改成新 marker `IIdempotentRequest : ITransactionalRequest`；`IRecordWriteRequest` 改繼承 `IIdempotentRequest`；`CreateTaskRequest`、`CreateRecordTaskRequest`、`CreateSubtaskRequest`、`UpdateTaskRequest`、`AddTaskCommentRequest` 加上 `IIdempotentRequest`。`RecordEffectsBehavior` 仍只看 `IRecordWriteRequest`，任務照舊不跑 `IWriteStage`。
   - 畫面不受影響：前端只對記錄、簽核、匯入、轉換、沖銷帶 `Idempotency-Key`（`frontend/src/services/api/dynamic-api.ts` 的 `idem(...)`），任務端點不帶；帶了才冪等。
   - 儲存沿用 `Dynamic_Operation`（主鍵 `UserId, Key`），保留天數沿用政策 `maintenance.idempotencyRetentionDays`（A26，出廠 7 天），不另設。key 以使用者隔離、不以權杖隔離：同一人的兩把權杖用同一個 key 送同樣內容會回放，送不同內容回 `idempotency_key_reused`。
   - 並行重送：沿用交易開頭的全庫寫入鎖（SQLite `WriteGate`＋`BEGIN IMMEDIATE`；PostgreSQL `WriteGate.ForKey`＋advisory lock），後到者等前者提交後查到紀錄回放。PostgreSQL 與 SQLite 都要測（Task 3）。
   - 回放時 `ServiceResult.Data` 是 `JsonElement`，不是型別物件：**寫入工具不得把 `r.Data` 轉型**，一律把 `Data` 當不透明物件放進 `data`；需要 id 時用 `JsonSerializer.SerializeToElement(r.Data)` 讀。對照測試驗「第一次的 data 與回放的 data 序列化後相同」（`IdempotencyBehavior.Json` 與 `McpResultFormatter.Json` 若有差異，以回放的 JSON 為準，把工具的輸出也先經同一份選項正規化）。

6. 〔技術〕**範圍與建立對話框**：
   - `McpScopes.Issuable` 加 `BusinessWrite`。
   - 預設勾選改成後端單一來源：`McpScopes.DefaultChecked = [BusinessRead]`（唯讀範圍；寫入範圍預設不勾，使用者 2026-11-21 裁決），`McpInfoResponse` 加 `DefaultScopes`，前端 `CreateAiTokenDialog` 開啟時 `scopes = [...info.defaultScopes]`。不在前端用「代碼以 `.read` 結尾」判斷（鐵則 2：預設值單一來源、前端從 API 取）。
   - `aiTokens.scopes.business.write` 四語改成具體說法：「新增與修改業務資料：建立與修改記錄、任務與留言、送簽與撤回」（en-US、th、vi 同義）。
   - 既有權杖不受影響：只有 `business.read` 的權杖看不到寫入工具；要寫入得重建權杖（設計 §5.2「新範圍要重新建權杖」）。

7. 〔技術〕**`ignoredFields`**：`create_record`、`update_record` 的 `meta.ignoredFields` 列出「這次送了、但依 describe_module 的 schema 不可寫」的欄位 key（不存在的 key、被遮、隱藏、唯讀、計算欄位），**不分原因**、依字母排序。
   - 算法：工具先送 `GetModuleSchemaRequest`（唯讀，不在交易內），可寫＝schema 裡出現、`config.readOnly` 為否、不是公式／彙總／自動編號；不在可寫集合的 key 進 `ignoredFields`。值照原樣送給 `CreateRecordRequest`，由 `RecordValueProcessor` 決定實際忽略（伺服器仍是唯一判斷，工具的清單只是提示）。
   - 規則唯讀、狀態機唯讀在 schema 看不出來，那些欄位不會列在 `ignoredFields`（已知限制）；結果的 `data` 是存檔後的記錄，模型可比對。
   - 不分原因是為了不透露被遮欄位存在：送一個猜的 key `salary`，不論它存在與否都只回「被忽略」（Q1）。

8. 〔技術〕**寫入迴圈偵測**（C3 使用者裁決；政策見 Q5）：
   - 對象：**記錄**。工具描述多一個 `LoopTargetArg`（`update_record` 是 `recordId`、`submit_approval` 是 `recordId`），其餘工具不偵測（建立沒有既有目標；任務與撤回的目標不是記錄，靠每分鐘速率擋）。
   - 判斷位置：執行器在範圍、速率之後、工具執行之前。計數＝稽核表中同一 `TokenId`、`TargetKind = record`、`TargetId`、動作在「寫入工具動作清單」內、`Result = Success`、`ReasonCode` 是 null（排除回放）、`OccurredUtc >= now − 時間窗` 的列數。達到 `ops.ruleLoopTriggersPerRecord` 就擋。
   - 新 repository 方法 `IAuditLogRepository.CountWritesForTargetSinceAsync(tokenId, targetKind, targetId, sinceUtc, actions)`，等值比較加時間範圍，走既有 `(TargetKind, TargetId, OccurredUtc)` 索引；時間由應用層算 UTC，不用資料庫日期函式。放在 `McpService/McpUsage.cs`（工具層不碰 repository，S2）。
   - 擋下時：回 `isError`、代碼 `write_loop`、訊息說明「同一筆記錄在 N 分鐘內已被這把權杖修改 M 次，已暫停這把權杖對它的寫入，請停下來請使用者確認」；稽核 `Denied`、`ReasonCode = write_loop`。被擋的呼叫不計入迴圈次數（只計成功），但照 Q8 計入每日名額。
   - 同時開一張維運事件（`IncidentRecorder`，`IncidentSource.Config`，指紋＝`mcp.writeLoop`＋權杖 id＋記錄 id，同指紋去重），收件人照 `ops.configIssueRecipients`（沒有規則修改者時就是系統管理員），訊息帶權杖名稱、擁有者、模組、記錄 id、次數與時間窗；寫事件失敗只記 Error log，不影響回給模型的錯誤（同 C3「計數寫在業務交易之外」的原則）。
   - 恢復：時間窗滑過、成功次數降到門檻以下就自動恢復，不另做「恢復」按鈕（Q4）。
   - 「或來源」：mcp2a 只有 MCP 這一種外部來源，所以只看權杖；整合來源上線時再擴充（已知限制）。

9. 〔技術〕**`submit_approval` 先預覽再送**（設計 §7.2「預覽是送簽的一部分，不是高風險確認」）：
   - 同一次呼叫內先送 `PreviewRecordApprovalRequest`，預覽失敗就回錯誤；`previewOnly: true` 就回預覽（`data.preview`，含要自選的關卡與候選人 `PickRequirement`），不寫入。
   - 預覽成功再送 `SubmitApprovalRequest`，回 `data = { preview, outcome, instanceId, recordRowVersion }`。
   - 自選關卡缺人時，handler 回 `Invalid["picks.{code}"]`，模型照 `previewOnly` 的候選人重送。
   - `previewOnly` 的呼叫沒有交易型請求，執行器照唯讀路徑在事後寫一列（`Success`，`ReasonCode = preview`，迴圈不計）。

10. 〔技術〕**annotations**（設計 §6.2）：`McpSdkAdapter.ListToolsAsync` 依級別給：唯讀 `readOnlyHint: true, destructiveHint: false`；一般寫入 `readOnlyHint: false, destructiveHint: false, idempotentHint: true`（判斷 5 的必填 key）；高風險 `destructiveHint: true`（mcp2b 用）。全部 `openWorldHint: false`。現在寫入級別的 `destructiveHint` 是 null，要改。

11. 〔技術〕**`TaskHiddenAfterWrite`**：主管把任務派給自己看不到的人時，`update_task` 回 `data = { taskId, hiddenAfterWrite: true }`，不給 `undoToken`（復原不是本段工具；undo token 只給畫面）。

12. 〔技術〕**每日名額與速率**：寫入工具和唯讀工具共用 mcp1a 的每分鐘、每日上限，不另設寫入專用政策。被擋、失敗、取消都計入每日名額（使用者 2026-11-21 裁決 Q8，mcp1a 已實作）。

13. 〔技術〕**訊息**：新增 `McpMessages.WriteLoop(count, minutes)`、`McpMessages.IdempotencyKeyRequired`（格式不符也用它）、`McpMessages.ConflictPrefix` 不需要（`conflict` 直接用 handler 訊息）。新訊息照 mcp1a 進 `mcp.json` 基準檔，四語同步。

14. 〔技術〕**既有測試的預期值變動只有這幾處**：
    - `McpScopesTests`：`Issuable` 多 `business.write`；新增 `DefaultChecked`。
    - `McpTokenHandlerTests`、`McpTokenApiTests`：`me/mcp` 的 `scopes` 多一項、多 `defaultScopes`；可建立 `business.write`。
    - `McpTransportTests`：annotations 的期望值（判斷 10）；`tools/list` 在 `business.write` 權杖下多 7 個工具。
    - `McpToolInvokerTests.Conflict_Stale`：改成只有 `code=stale` 的 Conflict 才是 `stale`，另加 `Conflict_Other`。
    - `AiTokensPage.vitest.spec.ts`：INFO 加 `business.write` 與 `defaultScopes`，斷言送出只有 `['business.read']`。
    - `WriteJournalAndIdempotencyTests`、`IdempotencyApiTests`：不改預期，只新增任務案例。
    - 其他既有測試若因此要改，停下回報。

## 待確認

### 行為類（會改變使用者看到或要做的事；規劃者照推薦寫進計畫，答案反過來時照「選錯代價」調整）

以下都是**規劃者暫定**，相依的 Task 開工前要先裁決。

- **Q1 標「不提供給 AI」的欄位，AI 能不能寫？**（以 #74／#75 合併後的權限解析為準）
  - 背景：讀取端已經把這些欄位當成隱藏欄位。寫入走同一份權限，所以照現在的程式，AI 送了也會被靜默忽略。設計 §5.5 要求 mcp2a 審這個語意。
  - 例子：「員工」模組的「身分證字號」標了不提供給 AI。使用者叫 AI「把王小明的身分證字號改成 A123456789」，AI 若猜到欄位 key 送出。
  - 推薦：**不能寫，當作隱藏欄位**。修改時保留舊值；新增時只套預設值；結果的 `meta.ignoredFields` 列出它，但和「不存在的欄位」「唯讀欄位」混在一起不分原因，AI 無法藉此確認欄位存在（判斷 7）。
  - 另一個選項：可寫不可讀（AI 能填、但讀不回來）。
  - 選錯代價：選推薦，敏感欄位一律要人在畫面上填，AI 幫忙建檔時這些欄位會空著；選「可寫不可讀」，要在權限解析另開一條「AI 可寫」的分支（碰 #74／#75 正在改的 `PermissionResolver`），而且 AI 可以用「寫入後看檢查有沒有擋」去試探值。
- **Q2 被遮欄位是必填、又沒有預設值時，AI 建立記錄要不要擋？**
  - 背景：畫面上看不到的必填欄位現在不檢查必填，記錄照樣存（`RecordValueProcessor`）。Q1 選推薦時，AI 建的記錄一定缺這個值。
  - 例子：「員工」的「身分證字號」是必填且不提供給 AI，AI 依使用者指示建一筆新員工。
  - 推薦：**照畫面做法不擋**，記錄存成功、該欄位空著；手冊寫明「有不提供給 AI 的必填欄位時，AI 建的記錄要有人補填」。
  - 另一個選項：MCP 建立時擋下，回「這個模組有 AI 不能填的必填欄位，請在畫面建立」。
  - 選錯代價：選推薦，資料會有缺必填值的記錄，要靠人補；選「擋下」，這類模組完全不能用 AI 建立，而且錯誤本身透露了「有 AI 看不到的必填欄位」。
- **Q3 `update_record` 能不能刪明細列（`_delete`）？**
  - 背景：畫面把刪明細列當成「修改記錄」的一部分；設計把「刪除記錄」列為高風險（mcp2b），但沒提明細列。
  - 例子：叫 AI「把這張訂單的第 3 行拿掉」。
  - 推薦：**mcp2a 不收 `_delete`**，帶了回 `invalid_arguments`；AI 只能新增與修改明細列，刪除請使用者在畫面做。mcp2b 再決定要不要做成高風險工具。
  - 選錯代價：選推薦，AI 改單時刪不了行，要人手動；選「照畫面可刪」，AI 一次一般寫入就能清掉整張單的明細，沒有兩段式確認。
- **Q4 偵測到寫入迴圈時怎麼處理？**
  - 背景：C3 裁決只說要偵測「同一權杖反覆寫同一筆」，沒說擋多久、通知誰（維運設計 §5.4 的「自動暫停規則」對 MCP 沒有規則可停）。
  - 例子：AI 卡在「改狀態 → 檢查擋下 → 再改」的迴圈，10 分鐘內改同一張單 30 次。
  - 推薦：**這把權杖對這一筆的寫入暫時擋下（回 `write_loop`），其他記錄與其他權杖照常；開一張維運事件通知系統管理員；時間窗滑過自動恢復**（判斷 8）。
  - 其他選項：自動撤銷權杖；只開事件不擋。
  - 選錯代價：選推薦，迴圈停在一筆、管理員收到通知，但同一把權杖換一筆照樣能寫；選「撤銷」，使用者要重建權杖、AI 整個斷線；選「不擋」，迴圈會一直寫到每日名額用完，連帶觸發事件與通知灌爆。
- **Q5 迴圈的「時間窗」用哪個政策？**
  - 背景：維運設計只登記了次數 `ops.ruleLoopTriggersPerRecord`（出廠 30、5–1000），沒有時間窗。業務門檻不能寫死（鐵則 2）。
  - 推薦：**新增政策 `ops.ruleLoopWindowMinutes`「寫入迴圈的計算時間窗（分鐘）」，出廠 10、範圍 1–1440**，和次數一起放在政策頁「維運」分類。10 分鐘 30 次：AI 迴圈通常幾秒一次，很快就到；人正常請 AI 逐項修改同一筆，10 分鐘內很少超過 30 次。
  - 另一個選項：沿用 `ops.rulePauseWindowMinutes`（規則連續失敗的時間窗，出廠 60）。
  - 選錯代價：選推薦，政策頁多一項；選沿用，改一個會連帶改另一個，而且 60 分鐘 30 次對逐項修改比較容易誤擋。
- **Q6 `mark_notifications_read` 要不要在 mcp2a 做？**
  - 背景：設計 §7.2 列了這個工具，但 mcp1 沒有「列出通知」的唯讀工具，AI 拿不到通知 id，只能「全部標已讀」。
  - 推薦：**不做**，開 backlog 票「列出通知與標已讀」，等有唯讀工具時一起做。
  - 理由：AI 把通知全部標成已讀，使用者回到畫面就看不到哪些沒處理。
  - 選錯代價：選推薦，AI 不能幫忙清通知；選「做」，多一個工具，且使用者的未讀提醒可能被 AI 清掉。
- **Q7 記錄歷程要不要標「經 AI」？**
  - 背景：AI 的寫入在歷程上記在權杖擁有者名下，與本人在畫面上改的一樣；稽核表有權杖 id 與 operation id，但歷程沒有通道欄位。
  - 推薦：**mcp2a 不標**；要查是不是 AI 改的，到稽核頁用「來源管道＝MCP」查。手冊寫明。
  - 另一個選項：歷程加「經 AI 工具」標記（要 migration 加欄位，改歷程畫面）。
  - 選錯代價：選推薦，一般使用者在記錄頁分不出哪次是 AI 改的；選「標」，本段多一個 migration 與畫面改動（約 250 行），子段超過上限。
- **Q9 三個子段一起上線？**
  - 推薦：**一起上線**（同 mcp1a Q1）。中途上線會有「能建 `business.write` 權杖、但工具還沒齊」的狀態。
  - 選錯代價：一起上線，分支存活約三段工作時間；各自上線，Task 1 的範圍開放要延到最後一批才合。

（Q8 沿用使用者 2026-11-21 裁決：被擋、失敗、取消都計入每日名額，不再問。）

### 技術類（規劃者決定，審查可推翻）

- 判斷 3：寫入稽核用新 behavior，不用 `IWriteStage`。
- 判斷 4：新錯誤代碼 `conflict`、`idempotency_key_reused`、`write_loop`。
- 判斷 5：`idempotencyKey` 必填；`IIdempotentRequest` 把任務與留言納入冪等。
- 判斷 6：預設勾選由後端 `DefaultScopes` 提供。
- 判斷 7：`ignoredFields` 從 schema 推算、不分原因。
- 判斷 8：迴圈只看記錄、只計成功且非回放的列、從稽核表計數。
- 判斷 9：`submit_approval` 內含預覽與 `previewOnly`。
- 判斷 2：`update_task` 不開放標題與描述；`create_task` 一個工具涵蓋三種建法。

## 與設計的矛盾（規劃時發現；Task 9 回填設計文件）

1. **冪等「自動接手」只對記錄寫入成立**：設計 §7.2 寫「每個寫入工具接受 `idempotencyKey`…`IdempotencyBehavior` 自動接手」，但 behavior 只處理 `IRecordWriteRequest`；任務、留言不在內。本計畫加 `IIdempotentRequest`（判斷 5）。
2. **寫入稽核「透過既有的 `IWriteStage`」**：設計 §8.1；`IWriteStage` 只由 `RecordEffectsBehavior` 對 `IRecordWriteRequest` 執行，任務與留言不跑。改用 `McpWriteAuditBehavior`（判斷 3）。
3. **錯誤對照**：設計 §6.5 只有 `stale`；寫入會遇到許多非版本衝突的 Conflict，補 `conflict`（判斷 4）。
4. **`mark_notifications_read`**：沒有列出通知的唯讀工具，暫不做（Q6）。
5. **迴圈時間窗沒有政策**：維運設計只登記次數（Q5）。
6. **annotations**：mcp1a 的寫入級別 `destructiveHint` 是 null、沒有 `idempotentHint`（判斷 10）。
7. **`idempotentHint` 的條件**：設計寫「帶冪等鍵時」，但 annotations 是每個工具固定的；改成 key 必填、一律標 true（判斷 5、10）。
8. **設計 §5.5 已知限制第三點**：本段審完，寫入端語意照 Q1 回填。

## 與 #74／#75 的衝突點

#74（一般參照來源的彙總與停用公式帶出群組隱藏欄位）、#75（條件引用隱藏欄位的彙總不會遮）是畫面通道的**讀取**遮罩修補，在另一條線（分支 `fix/74-75-screen-mask`）進行中，尚未提交。已知它會改：

| 檔案 | #74／#75 的改動 | mcp2a 的處理 |
|---|---|---|
| `Application/Common/Security/PermissionResolver.cs` | `HideDependents` 去掉 `aiMasked` 參數，畫面通道也遮條件與停用公式 | **mcp2a 不改這個檔** |
| `Application/Common/Security/RollupSourceMasks.cs` | 移除 `inputs.AiMasked` 條件 | 不改 |
| `Application/Common/Security/DynamicAccessGuard.cs` | 使用者 2026-11-21 指示可能會改 | 不改 |
| `Application/Common/Security/ChannelPermissionRepository.cs` | 同上 | 不改 |
| `ApplicationTests/Dynamic/Mcp/McpAiExclusionTests.cs` | 修改期望值 | **不改**；寫入端案例另開 `McpAiWriteMaskTests` |
| `ApplicationTests/Dynamic/{ReportRollupMaskTests,RollupFormulaMaskTests}.cs` | 新檔 | 不碰 |

- mcp2a 的寫入端語意（Q1）完全沿用權限解析的輸出（`EffectivePermissions.HiddenFieldIds`），程式改動在工具層與 `RecordValueProcessor` 之外，所以**沒有程式檔衝突**。
- 衝突在**期望值**：Task 5 驗「被遮欄位寫入後，依賴它的公式、彙總在 MCP 回應裡看不到」，哪些依賴欄位被遮以 #74／#75 合併後的 `PermissionResolver` 為準。Task 5 開工前 rebase 到含 #74／#75 的 main，期望值照當時的行為寫；#74／#75 還沒合併就先做 Task 6–7。
- 若 Q1 改選「可寫不可讀」，就一定要改 `PermissionResolver`，必須等 #74／#75 合併後再開工，並重估 Task 5 的規模。

## Global Constraints

- **MCP 關閉、或權杖沒有 `business.write` 時，行為與 main 相同**；畫面通道的冪等與稽核行為不變（前端任務端點不帶 key）。既有測試的預期值只照判斷 14 改。
- **鐵則 2**：迴圈次數與時間窗走 `PolicyCatalog`；長度上限沿用既有 request 的常數；`McpLimits` 不新增業務數字。
- **鐵則 4**：空系統（`new TestApp(seedDemoPeople: false)`）下，`create_task`（個人任務）、`update_task`、`add_task_comment` 照常可用；記錄工具在沒有模組時回 `forbidden`（與畫面相同）。
- **鐵則 5**：沒有執行期 DDL；寫入權限全經既有 guard；稽核寫不進去就整筆回滾（成功路徑）或回 `audit_unavailable`（失敗路徑）。
- **PostgreSQL**：新的 Application 測試類別 `McpWriteAuditTests`、`McpIdempotencyTests`、`McpWriteLoopTests` 與 WebAPI 的 `McpWriteToolsApiTests`、`McpWriteParityTests`、`McpAiWriteMaskTests` 標 `Category=Postgres`；每次雙跑核對執行數與 SQLite 相同，交棒說明寫數字。查詢全走 EF、時間範圍應用層算 UTC。
- **安全**：權杖、確認碼明文不進稽核與 log；稽核摘要沿用 `McpAuditSummary`（拿掉欄位值，`values`、`body`、`comment` 只記鍵與長度）。
- **測試只跑窄範圍**；完整套件交 CI（mcp2a-3 收尾的 gate）。
- **git**：逐檔 `git add`；commit 前 `git status`；不用 `--no-verify`、不 force push、不 merge commit。
- **公開 repo**：不寫公司字串、個人絕對路徑、內網 IP；測試資料全部虛構。
- **本機走查**：`bash scripts/unattended/local-demo.sh <worktree 絕對路徑>`；實機驗收用 `DEMO_LOGIN=false`。
- 段收尾用 `formwright-purpose-check`；上線用 `formwright-ship`。

## 寫死清單（本段新增，逐條標性質；Task 9 寫進設計 §15）

| 項目 | 常數位置 | 性質 |
|---|---|---|
| 7 個寫入工具、範圍 `business.write`、級別一般寫入 | `McpToolCatalog` | 平台功能清單 |
| 可建立範圍加 `business.write`；預設勾選只有唯讀範圍 | `McpScopes.Issuable`、`McpScopes.DefaultChecked` | 平台功能清單（預設勾選依使用者 2026-11-21 裁決） |
| 一次工具呼叫只能送一個最外層寫入請求；寫入稽核與業務同交易 | `McpWriteAuditBehavior` | 安全、資料完整性 |
| 寫入工具的 `idempotencyKey` 必填，格式沿用 `OperationContext.KeyPattern` | `McpArgs` | 資料完整性（技術限制） |
| 被遮欄位寫入時與隱藏欄位同語意（Q1） | 既有 `RecordValueProcessor` | 安全 |
| `ignoredFields` 不分原因 | `WriteTool` | 安全 |
| `update_record` 不收明細列 `_delete`（Q3） | `UpdateRecordTool` | 安全（暫定，mcp2b 重審） |
| 迴圈只計成功且非回放的寫入；寫事件失敗不影響回應 | `McpToolInvoker`、`McpUsage` | 技術限制 |
| 工具參數的長度上限沿用既有 request 常數（`TaskEditParser.TitleMax`、`TaskCommentRules.BodyMax`） | `McpTexts`（插值） | 技術限制（單一來源） |
| 寫入工具 annotations：`destructiveHint` false、`idempotentHint` true | `McpSdkAdapter` | 技術限制 |

**業務政策**：`ops.ruleLoopTriggersPerRecord`（出廠 30、5–1000）、`ops.ruleLoopWindowMinutes`（Q5，出廠 10、1–1440），出廠值與上下限只在 `PolicyCatalog`。

## 檔案地圖

**共用檔（並行工作要協調；★＝本段會動）**

| 共用檔 | Task | 說明 |
|---|---|---|
| EF migration、DbContext 模型設定 | 不動 | 本段沒有 migration |
| ★ DI 註冊 | 2、3 | `Application/DependencyInjection.cs`：`McpWriteAuditBehavior` 登記在 `McpConfirmationBehavior` 之後、`AuditBehavior` 之前；scoped `IMcpWriteScope`；寫入工具的 `IMcpTool` 登記 |
| ★ 冪等 | 3 | `Application/Common/IRecordWriteRequest.cs`、新 `Common/IIdempotentRequest.cs`、`Common/Behaviors/IdempotencyBehavior.cs`、`Dynamic/TaskService/TaskRequests.cs`（5 個 request 加 marker） |
| ★ 政策目錄 | 8 | `Domain/Policies/PolicyCatalog.cs`、`shared/policy-catalog.json` |
| ★ 介面文字 | 1、2、8 | `shared/i18n/{zh-TW,en-US,th,vi}/ui.json`：`aiTokens.scopes.business.write`、`audit.actions.mcp.*`（7 個）、`policies.items.ops.ruleLoop*` |
| ★ 後端訊息 | 3、8 | `McpService/McpMessages.cs`、基準檔 `ApplicationTests/Common/ServerTextBaseline/mcp.json` |
| ★ 稽核動作目錄 | 2 | `Application/Common/Audit/AuditActions.cs` |
| ★ 稽核 repository | 8 | `Domain/Repositories/Dynamic/IAuditLogRepository.cs`、`Persistence/Repositories/Dynamic/AuditLogRepository.cs`（`CountWritesForTargetSinceAsync`） |
| ★ MCP 既有檔 | 1–8 | `Domain/Mcp/{McpScopes,McpToolCatalog}.cs`、`McpService/Tools/{McpToolInvoker,IMcpTool,McpArgs}.cs`、`McpService/{McpTexts,McpUsage}.cs`、`McpService/Tokens/McpTokenModels.cs`、`WebAPI/Mcp/McpSdkAdapter.cs` |
| ★ 前端 | 1 | `frontend/src/presentation/mcp/CreateAiTokenDialog.vue`、`frontend/src/domain/mcp/mcp-types.ts`、`frontend/src/pages/account/AiTokensPage.vitest.spec.ts` |
| ★ 手冊 | 9 | `docs/manual.md` §24 |
| 權限解析（`PermissionResolver`、`RollupSourceMasks`、`DynamicAccessGuard`、`ChannelPermissionRepository`） | 不動 | 見「與 #74／#75 的衝突點」 |
| `apps/*.app.json` | 不動 | 本段不碰套件 |

**新檔（後端）**
- `Application/Common/IIdempotentRequest.cs`
- `Application/Dynamic/McpService/Writes/{IMcpWriteScope,McpWriteScope,McpWriteAuditBehavior,McpWriteTargets}.cs`
- `Application/Dynamic/McpService/Tools/Write/{WriteTool,RecordWriteTools,TaskWriteTools,ApprovalWriteTools}.cs`
- `Application/Dynamic/McpService/McpWriteLoopIncidents.cs`

**新檔（測試）**
- `ApplicationTests/Dynamic/Mcp/{McpWriteAuditTests,McpIdempotencyTests,McpWriteLoopTests,McpWriteTargetsTests}.cs`
- `WebAPITests/Dynamic/Mcp/{McpWriteToolsApiTests,McpWriteParityTests,McpAiWriteMaskTests}.cs`

---

# mcp2a-1 範圍、寫入稽核與冪等

### Task 1: `business.write` 可建立、預設不勾

**Files:**
- Modify: `Domain/Mcp/McpScopes.cs`（`Issuable`、`DefaultChecked`）、`McpService/Tokens/McpTokenModels.cs`（`McpInfoResponse.DefaultScopes`）、`McpService/Tokens/McpTokenHandlers.cs`
- Modify: `frontend/src/domain/mcp/mcp-types.ts`、`frontend/src/presentation/mcp/CreateAiTokenDialog.vue`、`shared/i18n/*/ui.json`（`aiTokens.scopes.business.write`）
- Test: `DomainTests/Mcp/McpScopesTests.cs`、`ApplicationTests/Dynamic/Mcp/McpTokenHandlerTests.cs`、`WebAPITests/Dynamic/Mcp/McpTokenApiTests.cs`、`frontend/src/pages/account/AiTokensPage.vitest.spec.ts`

- [ ] **Step 1: 寫失敗的測試**
  - `McpScopesTests`：`Issuable` 是 `[business.read, business.write]`；`DefaultChecked` 是 `[business.read]`，且是 `Issuable` 的子集。
  - `McpTokenHandlerTests`：`CreateToken_BusinessWrite_Allowed`；`Info_DefaultScopes_ReadOnly`；`design.read` 仍回 `TokenScopeInvalid`。
  - `McpTokenApiTests`：`GET me/mcp` 回 `scopes` 兩項、`defaultScopes` 一項。
  - vitest：INFO 的 `scopes` 是兩項、`defaultScopes` 是 `['business.read']`；開啟對話框只勾讀取（`data-testid="ai-scope-business.read"` 勾、`ai-scope-business.write` 不勾）；勾了寫入再送出 → body 的 `scopes` 是兩項；不動 → 只有 `['business.read']`。
- [ ] **Step 2: 跑測試確認失敗**：`cd backend && dotnet test tests/DynamicModules.DomainTests --filter "FullyQualifiedName~McpScopesTests"`；`dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~McpTokenHandlerTests"`；`dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~McpTokenApiTests"`；`cd frontend && bunx vitest run src/pages/account/AiTokensPage.vitest.spec.ts --maxWorkers=2`
- [ ] **Step 3: 實作**（判斷 6）。
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；加跑 `bun run typecheck`、前端 i18n 鍵一致的守衛測試。
- [ ] **Step 5: Commit**（`feat(mcp): business.write 範圍可建立，寫入範圍預設不勾`）

### Task 2: 寫入稽核同交易、錯誤對照、annotations

**Files:**
- Create: `McpService/Writes/{IMcpWriteScope,McpWriteScope,McpWriteAuditBehavior,McpWriteTargets}.cs`
- Modify: `Application/DependencyInjection.cs`、`McpService/Tools/McpToolInvoker.cs`（上膛、已提交判斷、`Mapped` 的 `conflict` 與 `idempotency_key_reused`）、`McpService/Tools/IMcpTool.cs`（`McpErrorCodes` 加 `Conflict`、`IdempotencyKeyReused`、`WriteLoop`）、`Domain/Mcp/McpToolCatalog.cs`（7 個寫入工具的宣告，`LoopTargetArg`）、`Common/Audit/AuditActions.cs`（7 個 `W`）、`shared/i18n/*/ui.json`（`audit.actions.mcp.*`）、`WebAPI/Mcp/McpSdkAdapter.cs`
- Test: `ApplicationTests/Dynamic/Mcp/{McpWriteAuditTests,McpWriteTargetsTests,McpToolInvokerTests,McpAuditActionsTests,McpToolArchitectureTests}.cs`、`WebAPITests/Dynamic/Mcp/McpTransportTests.cs`

- [ ] **Step 1: 寫失敗的測試**
  - `McpWriteAuditTests`（`Category=Postgres`；測試組件裡定義 `TestWriteTool` 與 `TouchRecordWriteRequest : IRecordWriteRequest`，可設定成功、回失敗、丟例外、送兩個請求）：
    - `Success_OneRow_InTransaction`：記錄改了；稽核表恰一列 `mcp.<測試工具>`、`Success`、`RiskLevel=Write`、`TargetKind=record`、`TokenId`；摘要有 `op`，且等於事件事實的 `OperationId`。
    - `AuditSaveFails_RollsBackBusiness`【雙跑】：`TestDb` 加一個對 `Dynamic_AuditLog` 新增丟例外的攔截器 → 記錄沒變；回 `audit_unavailable`（invoker 的失敗列也寫不進）。
    - `HandlerFails_InvokerWritesOneRow`：handler 回 `Invalid` → 記錄沒變、稽核恰一列 `Invalid`（由 invoker 以 `WriteNowAsync` 寫）。
    - `HandlerThrows_OneFailedRow`。
    - `TwoTopLevelWrites_Throws`：工具送兩個交易型請求 → 第二個丟 `InvalidOperationException`、第一個已提交的照記（一列 `Success`），回應不是錯誤但 `meta.dataOmitted` 是 `post_commit_error`。
    - `NestedTransactional_NotAudited`：handler 內巢狀的交易型請求不另寫列。
    - `UiChannel_NoArm_NoRow`：畫面通道送同一個請求 → 沒有任何稽核列（記錄寫入在畫面不記稽核，行為不變）。
    - `Committed_ResultTooLarge_NotError_OneRow`、`Committed_CancelledAfterCommit_OneRow`（照 mcp1a 確認碼的同名案例）。
    - `AuditedRequest_Rejected`：上膛後送 `IAuditedRequest` → 丟例外、記錄沒變。
  - `McpWriteTargetsTests`：7 種 request 的 target；回應是型別物件與 `JsonElement` 都讀得到 id。
  - `McpToolInvokerTests`：`Conflict_Stale`（`code=stale`）、`Conflict_Other`（`code=subtasksOpen` → `conflict`，`fields` 帶 `code` 與 `params.count`，訊息依 `X-Locale`）、`Unprocessable_IdempotencyReused`。
  - `McpAuditActionsTests`：7 個新動作在 `AuditActions`、四語都有標籤。
  - `McpToolArchitectureTests`：寫入工具也只依賴 `ISender`；掃 Application 組件，`McpWriteTargets` 涵蓋的 request 型別都不是 `IAuditedRequest`、`IMcpConfirmedRequest`。
  - `McpTransportTests`：`business.write` 權杖的 `tools/list` 多 7 個工具；寫入工具 `readOnlyHint` false、`destructiveHint` false、`idempotentHint` true；唯讀工具 `destructiveHint` false；`business.read` 權杖看不到寫入工具、呼叫回 `scope_denied`。
- [ ] **Step 2: 跑測試確認失敗**：`cd backend && dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~McpWriteAuditTests|FullyQualifiedName~McpWriteTargetsTests|FullyQualifiedName~McpToolInvokerTests|FullyQualifiedName~McpAuditActionsTests|FullyQualifiedName~McpToolArchitectureTests"`；`dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~McpTransportTests"`
- [ ] **Step 3: 實作**（判斷 1、3、4、10）。工具類別在 Task 4–7 才寫；這一步 `McpToolCatalog` 先宣告 7 個工具，`tools/list` 只列有實作的（`McpToolInvoker.ListFor` 依 DI 裡的 `IMcpTool`），所以 Task 2 的 `McpTransportTests` 先驗「宣告了 7 個」與 annotations 規則，工具數的斷言在 Task 7 收尾時改成 7。
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；加跑 `AuditBehaviorTests`、`AuditCoverageTests`、`McpConfirmationTests`、`TransactionBehaviorTests`。PostgreSQL：`FORMWRIGHT_TEST_DB=postgres dotnet test tests/DynamicModules.ApplicationTests --filter "Category=Postgres&FullyQualifiedName~McpWriteAuditTests"`。
- [ ] **Step 5: Commit**（`feat(mcp): 寫入工具的稽核與業務同交易、衝突代碼對照`）

### Task 3: 冪等鍵

**Files:**
- Create: `Application/Common/IIdempotentRequest.cs`
- Modify: `Common/IRecordWriteRequest.cs`、`Common/Behaviors/IdempotencyBehavior.cs`、`Dynamic/TaskService/TaskRequests.cs`、`McpService/Tools/{McpToolInvoker,McpArgs}.cs`、`McpService/McpMessages.cs`、`ServerTextBaseline/mcp.json`、四語 `ui.json`（若訊息走前端鍵；後端訊息照 `mcp.json`）
- Test: `ApplicationTests/Dynamic/Mcp/McpIdempotencyTests.cs`（`Category=Postgres`）、`ApplicationTests/Dynamic/WriteJournalAndIdempotencyTests.cs`（新增任務案例）

- [ ] **Step 1: 寫失敗的測試**
  - `WriteJournalAndIdempotencyTests`：`Task_create_with_same_key_replays`（同 key 兩次 `CreateTaskRequest` → 一筆任務、第二次 `Replayed`）、`Task_comment_with_same_key_replays`、`Task_update_same_key_different_patch_is_unprocessable`、`Task_request_without_key_runs_every_time`（畫面行為不變）。
  - `McpIdempotencyTests`（用 `TestWriteTool`，送 `TouchRecordWriteRequest` 與 `AddTaskCommentRequest`）：
    - `SameKey_Replays_OneWrite_MetaReplayed`：第二次 `meta.replayed` true、資料只寫一次；兩次的 `data` 序列化後相同。
    - `SameKey_Replay_AuditRow_Replayed`：第二次也寫一列 `Success`、`ReasonCode=replayed`。
    - `SameKey_DifferentArgs_IdempotencyKeyReused`：`isError`、`idempotency_key_reused`、沒有寫入。
    - `Key_Missing_InvalidArguments`、`Key_BadFormat_InvalidArguments`（`fields.idempotencyKey`）。
    - `Key_IsolatedPerUser`：兩個使用者用同一個 key → 各寫一次。
    - `Key_SharedAcrossTokensOfSameUser`：同一人兩把權杖、同 key 同內容 → 第二把回放。
    - `FailedFirst_RetryExecutes`：第一次 handler 回 `Invalid` → 沒有紀錄；同 key 修正後重送 → 執行。
    - `ConcurrentSameKey_OneWrite`【雙跑】：兩個 scope 同時送同 key → 恰一次寫入，另一個回放。
    - `Key_NotLeakingToNextCall`：同一個 DI scope 連續兩次呼叫，第二次沒帶 key（測試專用的選填工具）→ `ClientKey` 是 null。
- [ ] **Step 2: 跑測試確認失敗**：`cd backend && dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~McpIdempotencyTests|FullyQualifiedName~WriteJournalAndIdempotencyTests"`
- [ ] **Step 3: 實作**（判斷 5）。執行器在每次 `InvokeAsync` 開頭把 `ClientKey`、`Replayed` 重設為 null／false。
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；加跑 `IdempotencyApiTests`、`EffectsRuntimeApiTests`、`TasksApiTests`、`BuiltinServerTextTests`。PostgreSQL：`FORMWRIGHT_TEST_DB=postgres dotnet test tests/DynamicModules.ApplicationTests --filter "Category=Postgres&(FullyQualifiedName~McpIdempotencyTests|FullyQualifiedName~McpWriteAuditTests)"`。
- [ ] **Step 5: Commit**（`feat(mcp): 寫入工具的冪等鍵，任務與留言納入請求層冪等`）

**驗收（mcp2a-1 交棒）**：Task 1–3 的 Step 4 全過；PostgreSQL 執行數與 SQLite 相同，寫進交棒說明。

# mcp2a-2 記錄、遮罩、任務

### Task 4: create_record、update_record 與 REST 對照

**Files:**
- Create: `McpService/Tools/Write/{WriteTool,RecordWriteTools}.cs`
- Modify: `McpService/McpTexts.cs`（schema 與描述）、`Application/DependencyInjection.cs`（工具登記）
- Test: `WebAPITests/Dynamic/Mcp/{McpWriteToolsApiTests,McpWriteParityTests}.cs`（`Category=Postgres`）

- [ ] **Step 1: 寫失敗的測試**
  - `McpWriteParityTests`（設計 §12 mcp2a 驗收；造模沿用 `EffectsRuntimeApiTests`、`HistoryApiTests`、`ApiTestKit` 的寫法：一個有自動編號、公式、事件效果、跨記錄檢查、任務規則與一個明細模組的虛構模組）。同一個虛構使用者，一邊用 REST（cookie）、一邊用 MCP（`business.write` 權杖），送同樣的值：
    - `Create_SameRecord`：兩筆記錄除 id、時間、rowVersion、自動編號流水號外，`values` 與明細列相同。
    - `Create_SameHistory`：兩邊歷程的列數、`operation`、欄位與新值相同，`changedUserId` 都是該使用者。
    - `Create_SameEventFacts`：兩邊各自 `OperationId` 下的事件事實（`EventKey`、`EffectCode`、筆數）相同；效果寫到的目標記錄變動量相同。
    - `Create_SameTaskRuleTasks`：任務規則產生的任務數與標題相同。
    - `Create_CheckFails_SameMessage_NoWrite`：違反跨記錄檢查 → REST 409 的 `message`／`code` 與 MCP `conflict` 的訊息與 `fields.code` 相同；兩邊都沒寫入、沒有事件事實。
    - `Update_Same`：修改後歷程、事件事實相同。
    - `Update_Stale`：兩邊用舊 rowVersion → REST 409 `stale`、MCP `stale`。
    - `Update_DataScope_NotFound`：資料範圍外的記錄 → REST 404、MCP `not_found`（同 REST，handler 回 NotFound）。
    - `Create_NoAddPermission_Forbidden`：REST 403、MCP `forbidden`。
  - `McpWriteToolsApiTests`：
    - `CreateRecord_Details`：明細兩列一起建；`details` 帶 `_delete` → `invalid_arguments`（Q3）。
    - `CreateRecord_IgnoredFields`：送一個唯讀欄位、一個不存在的 key → `meta.ignoredFields` 兩個都列、依字母排序；值沒寫進去。
    - `UpdateRecord_DetailRow_UpdateAndAdd`。
    - `CreateRecord_ExtensionModule_HostKeyRequired` → `invalid`（`fields.hostKey`）。
    - `CreateRecord_Audit`：稽核一列 `mcp.createRecord`、`TargetId` 是新記錄 id、摘要的 `args.values` 只有鍵沒有值。
    - `CreateRecord_ModuleClosed_Conflict`。
    - `Prompt_Injection_Values`：值裡有「忽略之前指示」字樣 → 照存，回傳在 `data` 區、帶不可信標示。
- [ ] **Step 2: 跑測試確認失敗**：`cd backend && dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~McpWriteParityTests|FullyQualifiedName~McpWriteToolsApiTests"`
- [ ] **Step 3: 實作**（判斷 2、5、7）。`WriteTool` 基底提供：`idempotencyKey` 讀取（實際設定在 invoker）、`ignoredFields` 計算、`Fail(r)`、把 `r.Data` 原樣放進 `data`、`ResultCount = 1`。
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；加跑 `McpToolsApiTests`、`McpParityTests`、`McpToolArchitectureTests`。
- [ ] **Step 5: Commit**（`feat(mcp): create_record、update_record 與 REST 對照`）

### Task 5: 寫入端「不提供給 AI」（以 #74／#75 合併後為準）

**開工前**：照前置條件 3 確認 #74／#75 已合併並 rebase；未合併就先做 Task 6、7，回來再做。Q1、Q2 要已裁決；答案不是推薦時停下重估。

**Files:**
- Modify: 預期只有 `McpService/Tools/Write/WriteTool.cs`（若 `ignoredFields` 要調整）；**不改**權限解析的四個檔與 `McpAiExclusionTests.cs`
- Test: `WebAPITests/Dynamic/Mcp/McpAiWriteMaskTests.cs`（`Category=Postgres`）

- [ ] **Step 1: 寫失敗的測試**（欄位標 `ExcludeFromAi` 的方式沿用 `McpToolsApiTests.AiExcluded_NotInMcp`）
  - `Update_MaskedField_KeepsOldValue`：MCP 送被遮欄位的新值 → 資料庫的值不變；`meta.ignoredFields` 有它；歷程沒有它的變更。
  - `Create_MaskedField_DefaultOnly`：被遮欄位有預設值 → 存成預設值；沒有 → 空。
  - `Create_MaskedRequired_NotBlocked`（Q2 推薦）：被遮欄位必填、沒預設 → 建立成功、該欄位空。
  - `IgnoredFields_NoExistenceLeak`：送被遮欄位的 key 與一個不存在的 key → 兩者在 `ignoredFields` 的呈現完全相同（同一個陣列、沒有原因欄）。
  - `Response_HidesMaskedAndDependents`：寫入後的回應 `data` 不含被遮欄位；引用它的公式、彙總照 #74／#75 合併後的 `PermissionResolver` 規則一併不出現（期望值以當時的 main 為準）。
  - `SameUser_Ui_CanWrite`：同一使用者經 REST 寫同一欄位 → 有寫進去（畫面通道不受影響）。
  - `Approval_Submit_MaskedTitleTemplate`：模組標題樣板含被遮欄位，MCP 送簽成功；get_record_approval 的標題快照照 Q7（mcp1a）是空字串。
- [ ] **Step 2: 跑測試確認失敗**：`cd backend && dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~McpAiWriteMaskTests"`。若一開始就全部通過（沿用既有語意本來就成立），在交棒說明寫明，仍保留測試當回歸。
- [ ] **Step 3: 實作**：只在測試失敗的地方補；需要改權限解析就停下回報（違反「與 #74／#75 的衝突點」的前提）。
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；加跑 `McpAiExclusionTests`、`McpAiExclusionReportTests`、`ChannelPermissionDecorationTests`、`McpParityTests`。PostgreSQL：`FORMWRIGHT_TEST_DB=postgres dotnet test tests/DynamicModules.WebAPITests --filter "Category=Postgres&FullyQualifiedName~McpAiWriteMaskTests"`。
- [ ] **Step 5: Commit**（`test(mcp): 寫入端「不提供給 AI」欄位的語意`）

### Task 6: create_task、update_task、add_task_comment

**Files:**
- Create: `McpService/Tools/Write/TaskWriteTools.cs`
- Modify: `McpService/McpTexts.cs`、`Application/DependencyInjection.cs`
- Test: `WebAPITests/Dynamic/Mcp/{McpWriteToolsApiTests,McpWriteParityTests}.cs`

- [ ] **Step 1: 寫失敗的測試**
  - `McpWriteParityTests`：
    - `CreateTask_SameAsRest`：個人任務、記錄任務、子任務三種，兩邊的任務欄位、參與者、系統事件、通知（收件人與種類）相同。
    - `UpdateTask_Status_SameSystemEvents`：改狀態、負責人、到期日 → 兩邊的系統事件與通知相同。
    - `AddComment_Mentions_SameNotifications`：內文有 `@使用者` → 參與者與通知相同。
  - `McpWriteToolsApiTests`：
    - `CreateTask_RecordAndParent_Both_Invalid`。
    - `CreateTask_RecordTasksDisabled_Conflict`。
    - `UpdateTask_ParentDone_SubtasksOpen_Conflict_ThenCascade`：回 `conflict`、`fields.code=subtasksOpen`、`params.count`；帶 `cascade: complete` 重送成功。
    - `UpdateTask_ClearDueDate_Null`。
    - `UpdateTask_StatusEnum_Invalid`。
    - `UpdateTask_SupervisorHidden`：主管派給看不到的人 → `data.hiddenAfterWrite` true、沒有 `undoToken`。
    - `UpdateTask_SupervisorOtherField_Forbidden`：只能改負責人的主管送了到期日 → `forbidden`（一律訊息）。
    - `AddComment_EmptyBody_Invalid`、`AddComment_TooLong_Invalid`（上限取 `TaskCommentRules.BodyMax`）。
    - `AddComment_InvisibleTask_NotFound`。
    - `EmptySystem`（鐵則 4）：`new TestApp(seedDemoPeople: false)` 的 admin → `create_task`（個人）、`update_task`、`add_task_comment` 成功。
- [ ] **Step 2: 跑測試確認失敗**：`cd backend && dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~McpWriteParityTests|FullyQualifiedName~McpWriteToolsApiTests"`
- [ ] **Step 3: 實作**（判斷 2、11）。
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；加跑 `TasksApiTests`、`SubtasksApiTests`。PostgreSQL：`FORMWRIGHT_TEST_DB=postgres dotnet test tests/DynamicModules.WebAPITests --filter "Category=Postgres&(FullyQualifiedName~McpWriteParityTests|FullyQualifiedName~McpWriteToolsApiTests)"`。
- [ ] **Step 5: Commit**（`feat(mcp): 任務與留言的寫入工具`）

**驗收（mcp2a-2 交棒）**：Task 4–6 的 Step 4 全過；PostgreSQL 執行數與 SQLite 相同。

# mcp2a-3 簽核、迴圈與收尾

### Task 7: submit_approval、withdraw_approval

**Files:**
- Create: `McpService/Tools/Write/ApprovalWriteTools.cs`
- Modify: `McpService/McpTexts.cs`、`Application/DependencyInjection.cs`、`WebAPITests/Dynamic/Mcp/McpTransportTests.cs`（工具數改成 19）
- Test: `WebAPITests/Dynamic/Mcp/{McpWriteToolsApiTests,McpWriteParityTests}.cs`

- [ ] **Step 1: 寫失敗的測試**（造資料沿用 `ApprovalApiTests` 的 `NewRecord`、`Submitted`）
  - `McpWriteParityTests`：
    - `Submit_SameAsRest`：兩邊送簽後的實例、關卡、簽核人、簽核歷程、通知、記錄新 RowVersion 的行為相同。
    - `Withdraw_SameAsRest`。
  - `McpWriteToolsApiTests`：
    - `Submit_PreviewOnly_NoWrite`：回 `data.preview`（含 `picks` 候選）、沒有實例；稽核一列 `ReasonCode=preview`。
    - `Submit_MissingPicks_Invalid_ThenSucceeds`：缺自選 → `invalid`（`fields["picks.{code}"]`）；照候選補上重送成功。
    - `Submit_StaleRecord`：記錄 rowVersion 舊 → `stale`。
    - `Submit_AlreadyPending_Conflict`。
    - `Submit_Direct_Outcome`：沒有符合流程且設定為直接轉移 → `outcome=direct`。
    - `Withdraw_NotInitiator_Conflict`、`Withdraw_StageHandled_Conflict`。
    - `Submit_IdempotentResend`：同 key 重送 → 一個實例、`meta.replayed` true。
- [ ] **Step 2: 跑測試確認失敗**：`cd backend && dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~McpWriteParityTests|FullyQualifiedName~McpWriteToolsApiTests|FullyQualifiedName~McpTransportTests"`
- [ ] **Step 3: 實作**（判斷 9）。
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；加跑 `ApprovalApiTests`。
- [ ] **Step 5: Commit**（`feat(mcp): 送簽與撤回工具`）

### Task 8: 寫入迴圈偵測與政策

**Q4、Q5 要已裁決。**

**Files:**
- Create: `McpService/McpWriteLoopIncidents.cs`
- Modify: `Domain/Policies/PolicyCatalog.cs`、`shared/policy-catalog.json`、四語 `ui.json`（`policies.items.ops.ruleLoopTriggersPerRecord`、`ruleLoopWindowMinutes`）、`Domain/Repositories/Dynamic/IAuditLogRepository.cs`、`Persistence/Repositories/Dynamic/AuditLogRepository.cs`、`McpService/{McpUsage,McpMessages,McpPolicies}.cs`、`McpService/Tools/McpToolInvoker.cs`、`ServerTextBaseline/mcp.json`
- Test: `ApplicationTests/Dynamic/Mcp/McpWriteLoopTests.cs`（`Category=Postgres`）、`ApplicationTests/Persistence/AuditLogRepositoryTests.cs`（或既有的稽核 repository 測試類別）、`DomainTests/Policies/PolicyCatalogFixtureTests.cs`（跑，不改）

- [ ] **Step 1: 寫失敗的測試**
  - repository：`CountWritesForTargetSinceAsync` 只算指定權杖、目標、動作、時間之後、`Success` 且 `ReasonCode` 為 null 的列【雙跑】。
  - `McpWriteLoopTests`（`ruleLoopTriggersPerRecord` 設 5、`ruleLoopWindowMinutes` 設 10，用 `TimeProvider` 控時間）：
    - `FifthWrite_Allowed_SixthBlocked`：同權杖改同一筆 5 次成功，第 6 次 `write_loop`、記錄沒變；稽核 `Denied`、`ReasonCode=write_loop`。
    - `OtherRecord_NotBlocked`、`OtherToken_NotBlocked`。
    - `Replays_NotCounted`：同 key 回放 10 次 → 不觸發。
    - `Failures_NotCounted`：`stale` 失敗 10 次 → 不觸發。
    - `Blocked_NotCounted_RecoversAfterWindow`：被擋之後時間前進到最早那筆滑出時間窗 → 下一次成功。
    - `SubmitApproval_Counts`：`submit_approval` 與 `update_record` 對同一筆合併計數。
    - `CreateRecord_NotChecked`。
    - `Incident_Opened_Deduped`：第一次被擋開一張事件（`Source=Config`、帶權杖與記錄），再擋一次同一張事件次數加一，不開第二張；收件人照 `ops.configIssueRecipients`。
    - `IncidentWriteFails_StillWriteLoop`：事件寫入丟例外 → 回應仍是 `write_loop`、Error log 一筆。
    - `PolicyChange_TakesEffectNextCall`。
    - `BlockedCall_CountsTowardDailyQuota`（Q8）。
  - `PolicyCatalogFixtureTests`：兩項新政策在 catalog、json、四語都一致。
- [ ] **Step 2: 跑測試確認失敗**：`cd backend && dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~McpWriteLoopTests|FullyQualifiedName~AuditLog"`；`dotnet test tests/DynamicModules.DomainTests --filter "FullyQualifiedName~PolicyCatalogFixtureTests"`
- [ ] **Step 3: 實作**（判斷 8、13）。政策描述的文案寫白話：「同一把 AI 權杖在時間窗內修改同一筆記錄幾次，就暫停它對這筆的寫入」。
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；加跑前端政策守衛測試、`McpToolInvokerTests`、`RulePause` 相關測試。PostgreSQL：`FORMWRIGHT_TEST_DB=postgres dotnet test tests/DynamicModules.ApplicationTests --filter "Category=Postgres&(FullyQualifiedName~McpWriteLoopTests|FullyQualifiedName~AuditLog)"`。
- [ ] **Step 5: Commit**（`feat(mcp): 寫入迴圈偵測與政策`）

### Task 9: 文件、走查、gate、實機驗收

**Files:**
- Modify: `docs/superpowers/specs/2026-11-11-mcp-server-design.md`：
  - §5.5 已知限制第三點：照 Q1、Q2 的裁決寫寫入端語意。
  - §6.2：寫入工具 `idempotentHint` 的條件改成「key 必填、一律標」。
  - §6.5：表格加 `conflict`、`idempotency_key_reused`、`write_loop`。
  - §7.2 mcp2a：工具參數照判斷 2；`mark_notifications_read` 照 Q6；冪等照判斷 5（任務與留言也納入）。
  - §8.1：寫入稽核改成 `McpWriteAuditBehavior`（判斷 3）。
  - §12 mcp2a 列：加「mcp2a 計畫」連結。
  - §14：加 `ops.ruleLoopTriggersPerRecord`、`ops.ruleLoopWindowMinutes`（沿用其他文件的政策區）。
  - §15：本計畫的寫死清單。
  - §20 C3：補「mcp2a 已實作，處理方式見 Q4」。
  - Q 若被使用者推翻，照裁決改，並標明使用者裁決或規劃者暫定。
- Modify: `docs/superpowers/specs/2026-11-11-ops-observability-design.md` 政策表：`ops.ruleLoopTriggersPerRecord` 段別標已實作；加時間窗那一列（Q5）。
- Modify: `docs/manual.md` §24：寫入範圍與預設不勾、7 個工具能做什麼、不提供給 AI 的欄位在寫入時被忽略（Q1、Q2）、迴圈暫停與恢復（Q4）、要查 AI 改了什麼到稽核頁（Q7）、為什麼建立權杖時要謹慎勾寫入。
- Modify: `docs/roadmap.md` mcp2a 列：本段改「進行中」，合併後由主控補 SHA；`docs/feature.md` 的 mcp2a 項目照裁決更新（`mark_notifications_read` 若不做，寫進說明），跑 `feature_checklist.py docs/feature.md --write`，找不到腳本就回報「統計行待重算」。

- [ ] **Step 1: 整段驗收 grep**（S1–S5）全符合。
- [ ] **Step 2: 文件回填**（上列）。
- [ ] **Step 3: 開票**：Q6 若不做，用 `tickets:ticket-create` 開 backlog 票「列出通知與標已讀的 MCP 工具」；Q7 若不標，開票「記錄歷程標示經 AI 寫入」。票號寫進設計與交付說明。
- [ ] **Step 4: 受影響畫面走查**：用 `formwright-demo-verify` 在本機 Demo（9180）以 390、820、1366、1920 四種寬度看：個人「AI 存取權杖」頁的建立對話框（兩個範圍勾選框、預設狀態、四語標籤不溢出）、政策頁「維運」分類的兩項新政策、稽核頁含 `mcp.*` 寫入動作的列表與篩選。不得橫向捲動。
- [ ] **Step 5: 實機驗收**（下一節）逐項做，紀錄放專案 `.tmp/<日期>-mcp2a/`。
- [ ] **Step 6: gate**：推分支前外洩掃描；`bash scripts/unattended/ci-wait.sh <sha>`（含 PostgreSQL 兩步），`exit=0`。
- [ ] **Step 7: `formwright-purpose-check`**。重點：迴圈政策單一來源；工具通用、不依賴應用程式；空系統可用；寫入權限全在既有 guard；稽核 fail closed；沒有執行期 DDL。
- [ ] **Step 8: Commit**（`docs(mcp): mcp2a 完成，設計、手冊與功能清單回填`），之後照 `formwright-ship` fast-forward 推 main。

**驗收**：S1–S5 全符合；gate `exit=0`；實機驗收 1–9 全部有紀錄；四種寬度走查無橫向捲動。Demo 資料：**種子不改**（Demo 站開著 `Demo:LoginEnabled`，MCP 依設計整個停用）；交付說明寫「Demo 站無可示範（設計 §5.1），本機以實機驗收示範成功路徑與被擋、`stale`、迴圈暫停」。

---

## 整段驗收

設計 §12 mcp2a 列逐項：

| # | 項目 | Task | 驗證 |
|---|---|---|---|
| 1 | `business.write` 可建立、寫入範圍預設不勾 | 1 | `McpScopesTests`、`McpTokenApiTests`、`AiTokensPage.vitest.spec.ts` |
| 2 | 寫入稽核與業務同交易、提交後不寫第二列、稽核寫不進就回滾 | 2 | `McpWriteAuditTests` |
| 3 | 錯誤代碼（`stale`、`conflict`、`idempotency_key_reused`）與 annotations | 2、3 | `McpToolInvokerTests`、`McpTransportTests` |
| 4 | 重送同一冪等鍵不重複寫（記錄、任務、留言、送簽；並行；跨權杖；回放結果相同） | 3、7 | `McpIdempotencyTests`、`WriteJournalAndIdempotencyTests`、`McpWriteToolsApiTests.Submit_IdempotentResend` |
| 5 | MCP 建立／修改的記錄與畫面建立的完全相同（值、明細、歷程、事件事實、檢查、任務規則） | 4 | `McpWriteParityTests.Create_*`、`Update_*` |
| 6 | 任務、留言、送簽、撤回與畫面相同（系統事件、參與者、通知、簽核歷程） | 6、7 | `McpWriteParityTests` |
| 7 | 寫入端「不提供給 AI」 | 5 | `McpAiWriteMaskTests` |
| 8 | 寫入迴圈偵測（C3） | 8 | `McpWriteLoopTests` |
| 9 | 政策單一來源 | 8 | `PolicyCatalogFixtureTests`、前端政策守衛 |
| 10 | 空系統 | 6 | `McpWriteToolsApiTests.EmptySystem` |
| 11 | 架構：工具只依賴 `ISender`、寫入請求不是稽核或確認請求 | 2 | `McpToolArchitectureTests` |
| 12 | 實機連線寫入（Claude Code） | 9 | 「實機驗收」 |
| 13 | 受影響畫面四種寬度 | 9 | Task 9 Step 4 |

```bash
# S1 沒有 migration
git diff --name-only main...HEAD -- 'backend/src/*/Migrations/*'   # 無結果
# S2 工具不碰 DbContext 與 repository
grep -rn "DynamicDbContext\|Repository" backend/src/DynamicModules.Application/Dynamic/McpService/Tools backend/src/DynamicModules.WebAPI/Mcp   # 無結果
# S3 迴圈門檻不寫死
grep -rn "ruleLoop" backend/src --include='*.cs' | grep -v "PolicyCatalog\|McpPolicies"   # 只有讀政策的地方，沒有數字
# S4 權限解析沒動（#74／#75 的檔）
git diff --name-only main...HEAD -- backend/src/DynamicModules.Application/Common/Security/   # 無結果
# S5 寫入工具只送既有 request
grep -rn "new .*Request(" backend/src/DynamicModules.Application/Dynamic/McpService/Tools/Write | grep -v "CreateRecordRequest\|UpdateRecordRequest\|GetModuleSchemaRequest\|CreateTaskRequest\|CreateRecordTaskRequest\|CreateSubtaskRequest\|UpdateTaskRequest\|AddTaskCommentRequest\|PreviewRecordApprovalRequest\|SubmitApprovalRequest\|WithdrawApprovalRequest"   # 無結果
```

## 實機驗收（Claude Code 連本機）

1. **啟動**：`DEMO_LOGIN=false bash scripts/unattended/local-demo.sh <worktree 絕對路徑>`（背景執行，等 `READY`）；MCP 在 5180。
2. **準備**：照 mcp1a「實機驗收」第 2 步建一個非示範的虛構帳號、開 `mcp.enabled`；在個人頁建權杖時確認寫入範圍預設不勾，勾上後建立（截圖）。
3. **建立**：Claude Code 問「在某模組新增一筆記錄，名稱為…」→ 畫面上看到這筆記錄，歷程記在該帳號名下；觸發的事件效果與畫面建立的一樣。
4. **修改**：「把剛才那筆的狀態改成…」→ 成功；再用畫面改一次同一筆，回 Claude Code 叫它用舊資料再改 → 回 `stale`，模型重讀後再改成功。
5. **任務與留言**：「替這筆記錄建一個任務指派給某人，明天到期，並留言 @某人」→ 被提及的人收到通知。
6. **送簽與撤回**：「把這筆送簽」→ 先看到預覽、送出成功；「撤回」→ 撤回成功。
7. **重送**：用 curl 對 `/mcp` 送兩次同一個 `create_record`（同 `idempotencyKey`）→ 只有一筆，第二次 `meta.replayed` true。
8. **迴圈**：把 `ops.ruleLoopTriggersPerRecord` 調到 5，用 curl 連續改同一筆 6 次 → 第 6 次 `write_loop`；維運事件頁看到一張事件；時間窗過後恢復。
9. **稽核**：admin 的稽核頁用「來源管道＝MCP」查到上述寫入動作，帶權杖 id；被擋的那次是「被拒」。

Codex 的寫入不另做（工具與 Claude Code 共用同一個 endpoint，mcp1a 已驗過 Codex 連線）。

## 預估規模

| 批 | Task | 新檔 | 改檔 | 手寫行數（估，含測試） |
|---|---|---|---|---|
| mcp2a-1 | 1 範圍與對話框 | 0 | 9 | ≈ 180（測試約 90；文案約 10） |
| | 2 寫入稽核、錯誤對照、annotations | 6（含 2 個測試檔） | 8 | ≈ 620（測試約 360） |
| | 3 冪等鍵 | 2（含 1 個測試檔） | 7 | ≈ 400（測試約 240） |
| | 小計 | 8 | 24 | ≈ 1200 |
| mcp2a-2 | 4 記錄工具與對照 | 4（含 2 個測試檔） | 2 | ≈ 700（測試約 430） |
| | 5 寫入端遮罩 | 1（測試檔） | 0–1 | ≈ 250（測試約 230） |
| | 6 任務與留言工具 | 1 | 2 | ≈ 500（測試約 300） |
| | 小計 | 6 | 5 | ≈ 1450 |
| mcp2a-3 | 7 簽核工具 | 1 | 3 | ≈ 420（測試約 250） |
| | 8 迴圈與政策 | 2（含 1 個測試檔） | 10 | ≈ 560（測試約 300；文案約 20） |
| | 9 文件與收尾 | 0 | 6 | ≈ 170 |
| | 小計 | 3 | 19 | ≈ 1150 |
| 合計 | | 約 17 | 約 48 | ≈ 3800，其中測試約 58% |

- 單一 Task 超過 700 行就拆 commit：Task 4 拆「工具」「對照測試」兩個。
- Q1 改選「可寫不可讀」或 Q7 改選「標」時，Task 5 或另一個新 Task 各多約 250 行，mcp2a-2 會超過 1500，要再切一批。

## 已知限制（mcp2a，交付時寫回設計）

- `ignoredFields` 只看 schema：規則唯讀與狀態機唯讀造成的忽略不會列出，要比對回傳的記錄。
- `forbidden` 一律同一句，主管只能改負責人這類細訊息不給 AI。
- `update_task` 沒有版本比對，兩邊同時改時後者蓋前者（與畫面相同）；標題與描述不開放。
- 迴圈偵測只看記錄、只看權杖；任務與撤回的反覆寫入只靠每分鐘速率；整合等其他來源上線時再擴充。
- 迴圈計數在交易外判斷，並行的兩次寫入可能讓次數多一次才擋（門檻型偵測可接受）。
- 冪等紀錄保留 `maintenance.idempotencyRetentionDays`（出廠 7 天），過了之後同 key 重送會再執行一次。
- 檢查規則若引用被遮欄位，AI 可以藉「改別的欄位後有沒有被擋」試探被遮欄位的值（畫面上看不到隱藏欄位的使用者也有同樣問題）。
- 被遮的必填欄位在 AI 建立時不檢查（Q2）。
- 歷程不標「經 AI」（Q7），要到稽核頁查。
- 留言不收附件；不做 `mark_notifications_read`（Q6）。

## 宗旨檢查（formwright-purpose-check）

1. **機制還是政策**：通過。工具、冪等、稽核、迴圈偵測是平台機制；迴圈的次數與時間窗是政策；寫死的只有安全、完整性、技術限制，已列清單。
2. **寫死掃描**：計畫階段不適用；Task 9 Step 7 對 diff 跑 skill 的 grep，預期命中 `McpToolCatalog`、`McpScopes`（平台功能清單，已列）。
3. **單一來源**：通過。迴圈門檻只在 `PolicyCatalog`；預設勾選只在 `McpScopes.DefaultChecked`、前端從 API 取；長度上限沿用既有 request 常數；寫入權限只在既有 guard。
4. **兩個應用程式**：通過。工具通用，靠 describe_module 的 schema；CRM 的客戶、進銷存的訂單都用同一組 create_record／update_record；對照測試用虛構模組。
5. **空系統**：通過。沒開任何應用程式時任務與留言工具照常可用（`EmptySystem`）。
6. **套件只用定義組成**：不適用。本段不碰套件。
7. **ERP 對照**：本段沒有新增主檔或單據。對照設計 §11：Odoo 的外部 API 以 API key 代替使用者執行 create／write，權限等同使用者；Formwright 同樣等同使用者，另多了範圍（寫入要另勾）、冪等鍵、逐筆稽核與迴圈偵測。
8. **權限 fail closed、驗證不只在前端、沒有執行期 DDL**：通過。寫入全經既有 guard 與後端驗證；稽核寫不進去就回滾；一次呼叫兩個寫入請求就丟例外；沒有 migration。
9. **不限產業**：通過。例子用記錄、任務、簽核，與產業無關。

## 自我檢查

1. **設計覆蓋**：§5.2 → 判斷 6、Task 1；§5.5 第三點 → Q1、Q2、Task 5；§6.2 → 判斷 10；§6.5 → 判斷 4；§7.2 mcp2a → 判斷 1、2、5、9、Q6；§8.1 → 判斷 3；§8.2 → 判斷 12；§12 mcp2a 列 → 「整段驗收」；§13.5 建立對話框 → Task 1；C3 → 判斷 8、Q4、Q5。
2. **使用者 2026-11-21 裁決**：寫入範圍預設不勾 → 判斷 6；被擋、失敗、取消計入每日名額 → 判斷 12；畫面通道也補遮罩（#74、#75）→ 「與 #74／#75 的衝突點」、Task 5 開工條件。
3. **型別一致**：`McpScopes.{Issuable,DefaultChecked}`、`McpInfoResponse.DefaultScopes`、`McpToolDescriptor.LoopTargetArg`、`IMcpWriteScope.{Arm,IsCommitted}`、`McpWriteAudit`、`McpWriteAuditBehavior`、`McpWriteTargets`、`IIdempotentRequest`、`McpErrorCodes.{Conflict,IdempotencyKeyReused,WriteLoop}`、`IAuditLogRepository.CountWritesForTargetSinceAsync`、`McpWriteLoopIncidents`、`PolicyCatalog.{OpsRuleLoopTriggersPerRecord,OpsRuleLoopWindowMinutes}`。
4. **前段踩過的坑**：
   - 回放的 `Data` 是 `JsonElement`，工具不轉型（判斷 5）。
   - 稽核只能一列：寫入、確認、`IAuditedRequest` 三種互斥（判斷 3）。
   - `WriteNowAsync` 不能在交易內呼叫：失敗列仍由執行器在交易外寫。
   - 新測試類別進 PostgreSQL 雙跑集；依可空欄位排序寫明 NULL 位置。
   - 新訊息進 `mcp.json` 基準檔；四語文案同步並跑前端守衛。
   - `local-demo.sh` 給絕對路徑；實機驗收關 Demo 登入。

## 使用者裁決（沿用，不再問）

- 2026-11-11、11-12：設計 §3、§20 C1–C7（C3：迴圈偵測延到 mcp2a，對象是同一權杖或來源反覆寫同一筆，政策 `ops.ruleLoopTriggersPerRecord`）；U12（送簽、撤回、任務與留言、建立與修改記錄是一般寫入，簽核同意與退回是高風險）。
- 2026-11-20：業務政策不寫死、權限 fail closed、驗證不只做在前端；超過上限的訊息帶上限數字；沒指定語系用預設語系；系統自記的時間照營運時區。
- 2026-11-21：建立權杖的範圍預設唯讀全勾、寫入範圍不勾；被擋、失敗、取消都計入每日名額（Q8）；「不提供給 AI」的連帶遮罩畫面通道也補（#74、#75，另一條線實作）。
