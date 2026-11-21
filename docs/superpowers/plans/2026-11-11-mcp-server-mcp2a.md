# MCP 一般寫入 mcp2a Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 讓 AI client 用 `business.write` 範圍的權杖，經 `/mcp` 建立與修改記錄、建任務與改任務、加任務留言、送簽與撤回。內容：
- 範圍：`business.write` 可建立；個人頁建立權杖時寫入範圍預設不勾（使用者 2026-11-21 裁決）。
- 7 個一般寫入工具，全部重用畫面的同一條 MediatR request；結果（記錄值、歷程、事件事實、檢查、任務規則、通知）與畫面建立相同。
- 寫入稽核與業務寫入同一交易（設計 §8.1、T14、B12）；稽核摘要不存寫入的值。
- 冪等鍵：工具參數 `idempotencyKey` 接到 `IOperationContext.ClientKey`，`IdempotencyBehavior` 接手；任務與留言也納入。
- 寫入迴圈偵測（C3 使用者裁決）：同一權杖在時間窗內反覆寫同一筆記錄達 `ops.ruleLoopTriggersPerRecord` 次就擋下並開維運事件。
- 寫入端的「不提供給 AI」語意（設計 §5.5 留給 mcp2a 審；使用者 2026-11-21 裁決：不能寫）。

**使用者看得到的行為：**
- 個人「AI 存取權杖」頁的建立對話框多一個可勾的「新增與修改業務資料」範圍，預設不勾。
- 政策頁「維運」分類多兩項迴圈偵測政策（D5）。
- 稽核頁出現 `mcp.createRecord` 等 7 個寫入動作；維運事件頁可能出現「AI 寫入迴圈」事件。
- 用 AI 寫入的記錄，在畫面上和本人親手存的一樣（歷程記在權杖擁有者名下，D7）。

**Architecture:**
- **沒有 migration**。冪等沿用 `Dynamic_Operation`；迴圈計數與每日名額都走稽核表既有的索引（`TargetKind, TargetId, OccurredUtc`、`TokenId, OccurredUtc`）。
- **工具**：`Application/Dynamic/McpService/Tools/Write/`，每個工具只依賴 `ISender`（沿用 `McpToolArchitectureTests`），只送既有 request、從白名單參數逐欄組請求，不另寫查詢與權限判斷。
- **寫入稽核**：新 behavior `McpWriteAuditBehavior` 放在 `McpConfirmationBehavior` 之後、`AuditBehavior` 之前（交易內、冪等之外）。執行器在迴圈檢查之後、緊接 `tool.RunAsync` 之前「上膛」本次的稽核內容；behavior 只接手最外層的 `IIdempotentRequest`，成功後 `IAuditLog.Add` 並存檔，與業務同一交易；提交後標記「本次呼叫已提交」，執行器就不再寫第二列。其他交易型請求（簽核警示、迴圈事件）一律放行（判斷 3）。
- **冪等**：執行器在呼叫工具前把 `idempotencyKey` 設進 scoped 的 `IOperationContext.ClientKey`；`IdempotencyBehavior` 的判斷從 `IRecordWriteRequest` 放寬到新 marker `IIdempotentRequest`（`IRecordWriteRequest` 繼承它），任務與留言的 request 加上這個 marker（判斷 5）。
- **迴圈偵測**：執行器在範圍與速率之後、上膛與工具執行之前，對宣告了「迴圈目標」的工具查稽核表；這個 key 已有冪等紀錄（重送）時不擋，讓它照常回放（判斷 8）。

**Tech Stack:** .NET 8、MediatR、EF Core（SQLite、PostgreSQL）、xUnit＋FluentAssertions、`ModelContextProtocol.AspNetCore` 2.4.0（mcp1a 已引入）；前端 Vue 3＋Quasar、vitest。

**Spec:** `docs/superpowers/specs/2026-11-11-mcp-server-design.md`，涵蓋：
- §5.2 範圍（`business.write`）、§5.5 已知限制第三點（寫入端 AiMasked）。
- §6.1、§6.2（一般寫入的 annotations、`idempotentHint`）、§6.5。
- §7.2 mcp2a 一般寫入。
- §8.1 寫入工具的稽核、§8.2（被擋、失敗、取消都計入每日名額，使用者 2026-11-21 裁決，mcp1a 已實作，本段沿用）。
- §12 mcp2a 列、§13.5「建立對話框」、§16 U12、§20 C3。
- 維運設計 `2026-11-11-ops-observability-design.md` §5.4（C3 範圍）、政策表 `ops.ruleLoopTriggersPerRecord`（出廠 30、5–1000）。

repo 規範：`AGENTS.md`、`.claude/rules/{backend,frontend,docs}.md`、`CONTRIBUTING.md`、`docs/ai/testing.md`。

**路徑慣例：** 同 mcp1a 計畫。後端相對 `backend/`；`Application/…` 是 `backend/src/DynamicModules.Application/…` 的縮寫，其餘專案同理。符號以 main `c9cc9f66` 為準（已含 #74／#75 的 `c084787a`），不寫行號。

**修訂：**
- 2026-11-21 初版（`c8550675`）。
- 2026-11-21 第二輪：收審查必修 1–10 與建議 1–10；寫進使用者裁決（D1、D7）與顧問拍板（D2–D6、D9）。主要變動：
  - 稽核摘要逐參數去值（判斷 15）；工具從白名單參數組請求、`SystemKeys` 一律 null（判斷 16）。
  - 冪等：附件留言（`Files` 非空）不做請求層冪等，避免序列化 `Stream`；回放時也拿掉 `undoToken`（判斷 5、11）。
  - `submit_approval` 只有 `previewOnly` 才預覽，送簽不先預覽，重送才回放得了（判斷 9）。
  - `update_record` 先送 `GetRecordRequest` 取模組代碼；前置讀取失敗不擋寫入與回放（判斷 7）。
  - 迴圈事件改走另開 scope 的交易型請求、在上膛之前；事件標題只放代碼與數字；有冪等紀錄時不擋（判斷 8）。
  - MCP 通道沒上膛的最外層寫入請求丟例外（判斷 3）。
  - Task 5 不等 #74／#75；子段從三批改四批。
  - 「會變動的既有測試」補齊（判斷 14）；S2、S5 修正。
- 2026-11-21 第三輪：收複審必修 N1–N3 與建議 1–7；分支 rebase 到 main `c9cc9f66`。主要變動：
  - 寫入、高風險、不存在工具的稽核摘要，未列的鍵只記型別與長度；`values`、`details` 不是物件也只記型別（判斷 15）。
  - `McpWriteAuditBehavior` 只接手 `IIdempotentRequest`，其他交易型請求（簽核警示、迴圈事件）放行，送簽失敗的警示照常寫（判斷 3）。
  - Task 5 補明細被遮欄位的案例；#74／#75 已合併 main，改成開工前 rebase、系統管理員期望值寫死。
  - `IIdempotentRequest` 移到 Task 2 建立；`previewOnly` 一律上膛、預覽原因由 `McpToolOutcome.AuditReason` 傳；執行器上膛前只驗 `idempotencyKey` 與迴圈目標；冪等雜湊加上通道（判斷 3、5、8、9）。
  - S6 收窄成只擋把參數反序列化成 request；迴圈事件寫明 `Category = Config`、`Code = mcp.writeLoop`；檢查訊息 `{value}` 寫進已知限制。

---

## 前置條件（開工前逐項確認，不符就停下回報）

1. **mcp1a、mcp1b 在 main**：`grep -n "BusinessWrite" backend/src/DynamicModules.Domain/Mcp/McpScopes.cs` 有結果，`ls frontend/src/presentation/mcp/CreateAiTokenDialog.vue` 存在。
2. **mcp2a 沒開工**：`grep -rn "McpWriteAuditBehavior\|IIdempotentRequest\|ruleLoopTriggersPerRecord" backend/src shared` 無結果。
3. **#74／#75（畫面通道遮罩）已合併 main**（`c084787a`，之後還有 `4c83c989`）：開工前 `git rebase origin/main`，`git branch --contains c084787a` 要列得出目前分支。Task 5 的期望值照合併後的行為寫死（見「與 #74／#75 的衝突點」）。
4. **沒有別的段同時改 `shared/i18n/*/ui.json` 的 `policies.items.ops.*`、`audit.actions.mcp.*`、`aiTokens.*`、`ops.title.*`**；有就先協調誰先合。
5. **PostgreSQL 測試環境可用**：`docker info` 成功，或設了 `FORMWRIGHT_TEST_PG`。
6. **plugin 更新**（AGENTS.md「票」）：`claude plugin marketplace update plugharbor && claude plugin update tickets@plugharbor --scope project`，再 `/reload-plugins`；失敗就照舊版跑，記進回報。
7. **worktree**：`../formwright-mcp2a`，分支 `feature/mcp2a`。

## 盤點：既有寫入路徑與重用方式

工具一律送下表的 request，不另寫 handler。「副作用」欄是畫面存檔時就會發生的事，MCP 走同一條，所以自動相同；對照測試逐項證明（Task 4、6、7）。

| 工具 | request（marker） | 畫面端點 | 副作用（誰做） |
|---|---|---|---|
| create_record | `CreateRecordRequest`（`IRecordWriteRequest`） | `POST modules/{key}/records` | handler：歷程（`RecordHistoryWriter.Created`）、自動編號、公式與規則（`RecordValueProcessor`）、索引、彙總、參照；`RecordEffectsBehavior`：整合守衛、事件與事實、後盾、檢查、任務同步、任務規則（`IWriteStage` Order 7–12） |
| update_record | `GetRecordRequest`（唯讀，取模組代碼）→ `UpdateRecordRequest`（`IRecordWriteRequest`） | `PUT records/{id}` | 同上，歷程用 `history.Updated`；rowVersion 不符回 `ServiceResult.Stale()` |
| create_task | `CreateTaskRequest`／`CreateRecordTaskRequest`／`CreateSubtaskRequest`（`ITransactionalRequest`） | `POST tasks`、`POST records/{id}/tasks`、`POST tasks/{id}/subtasks` | `TaskWriter`：系統事件、參與者、通知（`FlushAsync`）；不跑 `IWriteStage` |
| update_task | `UpdateTaskRequest(Id, Patch)`（`ITransactionalRequest`） | `PATCH tasks/{id}` | 同上；沒有版本比對；可能回 `TaskHiddenAfterWrite` |
| add_task_comment | `AddTaskCommentRequest(TaskId, Body, Files: [])`（`ITransactionalRequest`） | `POST tasks/{id}/comments`（JSON） | `@提及` 解析成參與者、通知 |
| submit_approval | `SubmitApprovalRequest`（`IRecordWriteRequest`）；`previewOnly` 時只送 `PreviewRecordApprovalRequest`（唯讀） | `POST records/{id}/approval/submit`、`…/preview` | 簽核引擎歷程、通知、換記錄 RowVersion；`IWriteStage` 照跑 |
| withdraw_approval | `WithdrawApprovalRequest(InstanceId, RowVersion, Comment)`（`IRecordWriteRequest`） | `POST approval-instances/{id}/withdraw` | 簽核歷程、`ApprovalWithdrawn` 通知 |

- 交易外的 `RuleFailureBehavior`（規則失敗計數）、`ApprovalAlertBehavior`（簽核警示）對 MCP 照常生效，不改。
- 寫入權限全在既有路徑：記錄是 `DynamicAccessGuard.AuthorizeAsync`／`AuthorizeDetailAsync`、`ModuleGate`、`LedgerGate`、`ApprovalGate`、`EventFactLock`；任務是 `TaskAccess.EvaluateAsync`；簽核是 `ApprovalSubmitEligibility`。工具層沒有權限捷徑。
- 畫面通道的隱藏與唯讀欄位：`RecordValueProcessor` 靜默忽略輸入、保留舊值、新增只套預設、不檢查必填；凍結欄位例外（看得到又送了不同值回 409）。MCP 通道的「不提供給 AI」欄位經 `ChannelPermissionRepository` 變成隱藏欄位，所以沿用這套語意（D1）。
- 記錄留言不存在；要對記錄留言，先 create_task（帶 `recordId`）再 add_task_comment。
- 不在本段：刪除記錄、簽核同意與退回（mcp2b 高風險）；看板拖拉、甘特改日期、撤回留言、復原主管重派、匯入、轉換、沖銷、標通知已讀（D6）。

## 子段切法（四個實作者交棒點）

| 批 | 內容 | Task | 手寫行數（估，含測試） | migration |
|---|---|---|---|---|
| mcp2a-1 | 範圍與建立對話框、冪等標記、寫入稽核同交易與摘要去值、錯誤對照、冪等鍵 | 1–3 | ≈ 1490（測試約 55%；另有約 40 行四語文案） | 無 |
| mcp2a-2 | 記錄工具與 REST 對照、寫入端「不提供給 AI」 | 4–5 | ≈ 1120（測試約 65%） | 無 |
| mcp2a-3 | 任務與留言工具、簽核工具 | 6–7 | ≈ 1110（測試約 60%） | 無 |
| mcp2a-4 | 迴圈偵測、政策與事件、文件、走查、gate、實機驗收 | 8–9 | ≈ 1010（測試約 50%；另有約 40 行四語文案） | 無 |

- 四批同一分支，每個 Task 各自 commit；mcp2a-4 收尾才跑 gate 並合併，**四批一起上線**（D9）。
- mcp2a-1 到 mcp2a-3 收尾各跑一次本批的窄範圍 PostgreSQL 雙跑，不跑 gate。
- 審查建議 8 的「補完必修後超過 1500 就拆」：原 mcp2a-2（Task 4–6）補完約 1610 行，因此把 Task 6 移到 mcp2a-3、Task 8–9 獨立成 mcp2a-4。
- **交棒條件**：
  - **mcp2a-1 → mcp2a-2**：Task 1–3 驗收全過（含 PostgreSQL）。交棒說明寫：`IMcpWriteScope` 的形狀與上膛時機；`McpToolOutcome.AuditReason` 的用法；`IIdempotentRequest` 加在哪些 request、`Files` 非空的放行；`McpAuditSummary` 的寫入參數規則；測試怎麼用 `McpTestKit` 建 `business.write` 權杖。
  - **mcp2a-2 → mcp2a-3**：Task 4–5 驗收全過。交棒說明寫：寫入工具的共用基底（`WriteTool`）、白名單組請求的寫法、`ignoredFields` 的算法；對照測試的造模方式；分支 rebase 到的 main SHA。
  - **mcp2a-3 → mcp2a-4**：Task 6–7 驗收全過。交棒說明寫：`undoToken` 的兩種形狀怎麼辨識；`submit_approval` 的 `previewOnly` 稽核列。
- 分界理由：
  - mcp2a-1 是機制，審查重點是稽核與業務同交易、提交後不寫第二列、摘要不存值、冪等鍵不跨使用者、錯誤代碼對照。
  - mcp2a-2 是記錄工具，審查重點是 REST／MCP 結果相同、白名單組請求、被遮欄位寫不進也問不出存在。
  - mcp2a-3 是任務與簽核，審查重點是通知與簽核歷程相同、回放不洩漏 `undoToken`、送簽重送能回放。
  - mcp2a-4 是迴圈與收尾，審查重點是迴圈計數不被重送與失敗灌水、被擋的呼叫恰一列 Denied、事件標題不帶使用者字串、政策單一來源。

---

## 決策（Q 改稱 D；計畫依此寫，答案反過來時照「選錯代價」調整）

D1、D7 是使用者 2026-11-21 裁決；D2–D6、D9 是**顧問拍板，待使用者確認**（審查逐題核對，沒有違反鐵則或安全）。D8 沿用使用者 2026-11-21 裁決（被擋、失敗、取消都計入每日名額），不再問。

- **D1 標「不提供給 AI」的欄位，AI 能不能寫？——不能，當成看不到（使用者裁決 2026-11-21）**
  - 做法：當作隱藏欄位。修改時保留舊值；新增時只套預設值；結果的 `meta.ignoredFields` 列出它，但和「不存在的欄位」「唯讀欄位」混在一起不分原因，AI 無法藉此確認欄位存在（判斷 7）。
  - 現有程式本來就是這個語意（`PermissionResolver` 併進 `HiddenFieldIds`，`RecordValueProcessor.Writable` 忽略），Task 5 主要是回歸測試。
  - 沒採用的選項「可寫不可讀」：要讓 describe_module 揭露欄位、`EffectivePermissions` 多一組 AI 只能寫的欄位、改必填與凍結判斷，估 400–600 行並碰安全關鍵共用檔；之後若要改，獨立成一批、等 #74／#75 合併、重新審查。
- **D2 被遮欄位是必填、又沒有預設值時，AI 建立記錄要不要擋？——不擋（顧問拍板，待使用者確認）**
  - 例子：「員工」的「身分證字號」是必填且不提供給 AI，AI 依指示建一筆新員工 → 記錄存成功、該欄位空著。
  - 理由：與畫面上「看不到的必填不檢查」同一套語意，驗證仍在後端；手冊寫明「有不提供給 AI 的必填欄位時，AI 建的記錄要有人補填」。
  - 選錯代價：資料會有缺必填值的記錄要靠人補；若改成擋下，這類模組完全不能用 AI 建立，而且錯誤本身透露「有 AI 看不到的必填欄位」。
- **D3 `update_record` 能不能刪明細列（`_delete`）？——不收（顧問拍板，待使用者確認）**
  - 例子：叫 AI「把這張訂單的第 3 行拿掉」→ `invalid_arguments`，請使用者在畫面刪。
  - 理由：刪除記錄是高風險（mcp2b），刪明細列同樣不可逆；mcp2b 再決定要不要做成高風險工具。
  - 選錯代價：AI 改單時刪不了行；若改成可刪，AI 一次一般寫入就能清掉整張單的明細，沒有兩段式確認。
- **D4 偵測到寫入迴圈時怎麼處理？——擋該權杖對該筆、開維運事件、時間窗滑過自動恢復（顧問拍板，待使用者確認）**
  - 例子：AI 卡在「把數量改成 10 → 讀回來覺得不對 → 再改成 10」的迴圈，每次都存檔成功，10 分鐘內改同一張單 30 次 → 第 31 次回 `write_loop`，系統管理員收到一則事件通知。
  - 只計成功的寫入：「改狀態 → 被檢查擋下 → 再改」這種失敗迴圈不會觸發，靠每分鐘速率與每日名額擋（已知限制）。
  - 自動恢復的取捨：時間窗一滑動就放行一次，迴圈會以「門檻／時間窗」的速度繼續寫，事件只累加次數、不再通知，直到每日名額用完（鋸齒效應，寫進已知限制與手冊）。
  - 選錯代價：其他記錄與其他權杖照常；若改成自動撤銷權杖，使用者要重建、AI 整個斷線；若只開事件不擋，迴圈會寫到每日名額用完，連帶觸發事件與通知灌爆。
- **D5 迴圈的時間窗用哪個政策？——新政策 `ops.ruleLoopWindowMinutes`，出廠 10、範圍 1–1440（顧問拍板，待使用者確認）**
  - 理由：維運設計只登記了次數；鐵則 2 不准寫死；沿用 `ops.rulePauseWindowMinutes` 會讓改一個連帶改另一個，而且 60 分鐘 30 次對逐項修改容易誤擋。10 分鐘 30 次：AI 迴圈通常幾秒一次，很快就到；人正常請 AI 逐項修改同一筆，10 分鐘內很少超過 30 次。
  - 逐列改明細容易碰到門檻：工具描述與手冊寫明「明細請在同一次 update_record 一起送」。
- **D6 `mark_notifications_read` 要不要在 mcp2a 做？——不做，票 #82（顧問拍板，待使用者確認）**
  - 理由：mcp1 沒有列出通知的唯讀工具，AI 只能「全部標已讀」，使用者回到畫面就看不到哪些沒處理。等有唯讀工具時一起做。
- **D7 記錄歷程要不要標「經 AI」？——這段先不標，後續票 #81（使用者裁決 2026-11-21）**
  - 要查是不是 AI 改的，到稽核頁用「來源管道＝MCP」查。手冊寫明。
  - `RecordHistory` 沒有通道欄、也沒有 OperationId，要標一定得加 migration（票 #81 處理）；本段的「沒有 migration」因此成立。
- **D9 四個子段一起上線？——一起上線（顧問拍板，待使用者確認）**
  - 理由：中途上線會有「能建 `business.write` 權杖、但工具還沒齊」的狀態；`Issuable` 在 Task 1 打開，但只在分支上。

### 技術類（規劃者決定，審查可推翻）

- 判斷 3：寫入稽核用新 behavior，不用 `IWriteStage`；沒上膛的最外層寫入請求在 MCP 通道丟例外。
- 判斷 4：新錯誤代碼 `conflict`、`idempotency_key_reused`、`write_loop`。
- 判斷 5：`idempotencyKey` 必填；`IIdempotentRequest` 把任務與留言納入冪等；附件留言放行。
- 判斷 6：預設勾選由後端 `DefaultScopes` 提供。
- 判斷 7：`ignoredFields` 從 schema 推算、不分原因、只涵蓋表頭；`update_record` 先讀記錄取模組代碼。
- 判斷 8：迴圈只看記錄、只計成功且非回放的列、從稽核表計數、有冪等紀錄時不擋。
- 判斷 9：`submit_approval` 只有 `previewOnly` 才預覽。
- 判斷 2：`update_task` 不開放標題與描述；`create_task` 一個工具涵蓋三種建法。
- 判斷 15、16：摘要去值、白名單組請求。

---

## 計畫期間的判斷（規劃者決定，可推翻）

1. 〔技術〕**工具清單 7 個，範圍都是 `business.write`、級別 `McpRisk.Write`**（設計 §7.2、U12）。`mark_notifications_read` 不在本段（D6）。稽核動作 camelCase：`mcp.createRecord`、`mcp.updateRecord`、`mcp.createTask`、`mcp.updateTask`、`mcp.addTaskComment`、`mcp.submitApproval`、`mcp.withdrawApproval`，在 `AuditActions` 登記為 `W`。

2. 〔技術〕**工具參數（JSON Schema 固定英文，放 `McpTexts`）**：
   - `create_record`：`moduleKey`（必填）、`values`（物件，鍵是 describe_module 的欄位 key，值的格式同 REST）、`details`（物件，鍵是明細模組 key，值是列陣列，每列只有 `values`）、`hostKey`（擴充模組才要）、`idempotencyKey`（必填）。
   - `update_record`：`recordId`、`rowVersion`（必填，query_records／get_record 給的）、`values`、`details`（每列 `id`＋`rowVersion`＋`values` 是修改、沒 `id` 是新增；`_delete` 不收，D3）、`idempotencyKey`。描述寫明「明細請在同一次呼叫一起送」（D5）。
   - `create_task`：`title`（必填，≤ `TaskEditParser.TitleMax`）、`description`、`assigneeUserId`、`dueDate`、`startDate`、`recordId`（記錄任務）、`parentTaskId`（子任務）、`idempotencyKey`。`recordId` 與 `parentTaskId` 同時帶回 `invalid_arguments`；依參數選三個 request 之一。
   - `update_task`：`taskId`、`status`（`todo`／`inProgress`／`done`／`cancelled`，與 `TaskWriter.StatusText` 同步，同 mcp1a「schema 列舉與 handler 合法值同步」）、`assigneeUserId`、`dueDate`、`startDate`、`cascade`（`complete`／`cancel`）、`idempotencyKey`。帶了才放進 `Patch`，帶 `null` 代表清除（同 REST）。標題與描述不開放（設計 §7.2 只列狀態、負責人、到期日；`startDate`、`cascade` 是到期日與完成的配套）；`Patch` 由工具逐鍵組出，不轉送原始參數（判斷 16）。
   - `add_task_comment`：`taskId`、`body`（1–`TaskCommentRules.BodyMax` 字）、`idempotencyKey`；`Files` 固定空清單（設計：不含附件上傳）。
   - `submit_approval`：`recordId`、`to`（目標狀態選項 id，從 get_record_approval 的 `submitTargets` 取）、`rowVersion`（記錄的）、`picks`（關卡代號 → userId 陣列）、`comment`、`previewOnly`（預設 false）、`idempotencyKey`。
   - `withdraw_approval`：`instanceId`、`rowVersion`（實例的，從 get_record_approval 的 `current.rowVersion` 取）、`comment`、`idempotencyKey`。描述寫明「遇到 `conflict` 先用 get_record_approval 重讀」：實例版本過期時 handler 回的是 `conflict`（`StateChanged`／`StageHandled`），不是 `stale`。
   - 長度與上限沿用既有 request 的常數（`TaskEditParser.TitleMax`、`TaskCommentRules.BodyMax`）；schema 的 `maxLength` 由常數插值產生，同一個數字只有一個來源。

3. 〔技術〕**寫入稽核與業務同交易：`McpWriteAuditBehavior`**（設計 §8.1、T14、B12；設計原寫「透過 `IWriteStage`」，但任務與留言不跑 `IWriteStage`，見「與設計的矛盾」第 2 點）。
   - 新 marker `IIdempotentRequest : ITransactionalRequest`（`Application/Common/`，Task 2 建立；`IRecordWriteRequest` 同時改成繼承它）。本判斷的「寫入請求」一律指 `IIdempotentRequest`；任務與留言的 request 在 Task 3 加上它。
   - 新 scoped 服務 `IMcpWriteScope`（`McpService/Writes/`）：`Arm(long call, McpWriteAudit audit)`、`IsCommitted(long call)`、`IsArmed`。`McpWriteAudit` 帶動作代碼、`TargetLabel`（固定是工具名稱；權杖頁「最近呼叫」讀這一欄，見 `McpTokenItem`）、參數雜湊、摘要（判斷 15）。
   - **上膛時機**：範圍、速率、執行器自己的參數驗證（只有 `idempotencyKey` 的格式與 `LoopTargetArg` 的值，其他參數由工具自己驗）、迴圈檢查與迴圈事件都做完之後，緊接 `tool.RunAsync` 之前；寫入級別的工具一律上膛，包括 `previewOnly` 的送簽（執行器在 `RunAsync` 之前不認得 `previewOnly`）。執行器的參數驗證失敗就回 `invalid_arguments`、不上膛。迴圈事件的請求一定在未上膛狀態送出，而且它不是 `IIdempotentRequest`，不會被記成本次的寫入（判斷 8）。
   - 只接手**最外層的 `IIdempotentRequest`**（`AfterCommitActions.TransactionDepth == 1`）：成功時 `audit.Add(Success, TargetKind／TargetId 由 McpWriteTargets 算, TargetLabel)`、`uow.CommitAsync`、`afterCommit.Add` 提交標記；失敗照常回傳（交易已回滾，執行器照 mcp1a 結束路徑以 `WriteNowAsync` 寫失敗列）。
   - **其他交易型請求一律放行**（複審 N2）：上膛期間出現的最外層非 `IIdempotentRequest` 交易型請求（`ApprovalAlertBehavior` 在送簽失敗後同 scope 另送的 `WriteApprovalAlertRequest`、迴圈事件、`TouchMcpTokenRequest`）不寫列、不算次數、不標已提交。否則送簽碰到執行期流程錯誤時，警示請求會被當成第二個寫入而丟例外，`ApprovalAlertBehavior` 吞掉例外只記 log，系統管理員就收不到警示，和畫面不同。
   - 同一次呼叫的第二個最外層 `IIdempotentRequest` → 丟 `InvalidOperationException`（fail closed；工具一次只能送一個寫入請求）。「第二個」只算 `IIdempotentRequest`。
   - 沒有提交（handler 失敗、或 `previewOnly` 根本沒送寫入請求）時，執行器照 mcp1a 結束路徑在事後寫一列；工具可以在 `McpToolOutcome.AuditReason` 帶原因（`previewOnly` 帶 `preview`），執行器把它寫進 `ReasonCode`。現在成功列的 `ReasonCode` 固定是 null，這個欄位是新加的管道。
   - 請求同時是 `IAuditedRequest` 或 `IMcpConfirmedRequest` → 丟例外（一次呼叫只能一列；架構測試另外掃）。
   - **沒上膛也要擋**（審查建議 1）：MCP 通道上，沒上膛就出現的最外層 `IIdempotentRequest`（含所有 `IRecordWriteRequest`）→ 丟 `InvalidOperationException`。唯讀工具哪天誤送寫入請求也會被擋，不只靠範圍檢查與 S5。`CreateMcpConfirmationRequest`、迴圈事件請求、`TouchMcpTokenRequest`、`WriteApprovalAlertRequest` 不是 `IIdempotentRequest`，不受影響；畫面通道一律放行（行為不變）。Task 2 建好 marker 時只有 `IRecordWriteRequest` 繼承它，Task 3 再把任務與留言納入，規則本身不必改。
   - `McpWriteTargets`（純函式，依 request 型別）：`CreateRecordRequest` → `record`／回應的 id；`UpdateRecordRequest` → `record`／`Id`；三種建任務 → `task`／回應的 id；`UpdateTaskRequest`、`AddTaskCommentRequest` → `task`／`Id`、`TaskId`；`SubmitApprovalRequest` → `record`／`RecordId`；`WithdrawApprovalRequest` → `approvalInstance`／`InstanceId`。回應可能是型別物件，也可能是冪等回放的 `JsonElement`，兩種都要讀得到 `id`。
   - 摘要多記 `op`（`IOperationContext.OperationId`，可串到事件事實；記錄歷程沒有 OperationId，見「與設計的矛盾」第 9 點），回放時 `ReasonCode = "replayed"`（判斷 8 依此排除）。
   - 執行器的「已提交」判斷改成 `confirmations.IsCommitted(call) || writes.IsCommitted(call)`，提交後格式化失敗或放不下，沿用 mcp1a 的「已執行、結果省略」（`McpDataOmitted`），不回錯誤、不寫第二列。

4. 〔技術〕**handler 失敗的錯誤代碼對照補 `conflict`**（設計 §6.5 只列 stale；mcp1a 的 `McpToolInvoker.Mapped` 把所有 `IsConflict` 都對成 `stale`，唯讀工具沒差，寫入工具會誤導模型去重讀）：
   - `Conflict` 且 `Code == "stale"` → `stale`（同現在）。
   - 其他 `Conflict`（檢查失敗、`subtasksOpen`、`AlreadyPending`、`CannotWithdraw`、`StateChanged`、`StageHandled`、凍結、模組關閉、host 重複）→ 新代碼 `conflict`，訊息照 handler（依語系），`fields` 帶 handler 的 `code` 與 `params`（例如 `subtasksOpen` 的 `count`），模型能據此帶 `cascade` 重試。
   - `Unprocessable` 且訊息是 `MessageText.IdempotencyKeyReused` → 新代碼 `idempotency_key_reused`（不改 REST 的 422 回應）。
   - `Forbidden` 維持一律「沒有權限」、不透露存在（`SupervisorOnlyAssignee` 的細訊息因此不給 AI，已知限制）。

5. 〔技術〕**冪等鍵**（設計 §7.2「`IdempotencyBehavior` 自動接手」）：
   - 七個工具的 `idempotencyKey` 都**必填**，格式 `OperationContext.KeyPattern`（`^[A-Za-z0-9_-]{8,64}$`），不符回 `invalid_arguments`。理由：AI client 逾時重送是常態；選填的話模型多半不帶，重送就重複建記錄與留言。必填讓 annotations 能一律標 `idempotentHint: true`（判斷 10）。
   - 執行器在每次 `InvokeAsync` 開頭把 `ClientKey`、`Replayed` 重設為 null／false，在工具 `RunAsync` 前設 `ClientKey`（stateless 模式下一次 `tools/call` 一個 DI scope）；工具結束後讀 `Replayed` 放進 `meta.replayed`。
   - `IdempotencyBehavior` 的判斷從 `IRecordWriteRequest` 改成 `IIdempotentRequest`（marker 在 Task 2 建立，見判斷 3）；`CreateTaskRequest`、`CreateRecordTaskRequest`、`CreateSubtaskRequest`、`UpdateTaskRequest`、`AddTaskCommentRequest` 加上 `IIdempotentRequest`。`RecordEffectsBehavior` 仍只看 `IRecordWriteRequest`，任務照舊不跑 `IWriteStage`。
   - **附件留言放行**（審查必修 2，二選一取第一種）：`AddTaskCommentRequest.Files` 非空時 `IdempotencyBehavior` 直接放行、不做請求層冪等。`IdempotencyKeyMiddleware` 對所有端點都讀標頭，雜湊用 `JsonSerializer.Serialize(request)`，序列化 `CommentFileUpload` 裡的 `Stream` 會丟例外變成 500。
     - 理由：MCP 不收附件，所以 MCP 的留言永遠有冪等；畫面的附件留言本來就不帶 key、沒有冪等。另一個做法（`[JsonIgnore]` 掉 `Stream`、雜湊只算檔名、型別與大小）會讓「同名同大小、內容不同」的兩次上傳被當成重送而回放，靜默丟掉第二份檔案，比不做冪等更糟；要做對得先讀完串流算內容雜湊，超出本段範圍。
     - 放行條件寫成 request 自己宣告（`IIdempotentRequest` 加預設屬性 `bool IdempotencyApplies => true`，`AddTaskCommentRequest` 覆寫成 `Files.Count == 0`），behavior 不認得具體型別。
     - `IdempotencyApiTests` 補「帶標頭的 multipart 留言照常成功、兩次都寫」。
   - 畫面不受影響：前端只對記錄、簽核、匯入、轉換、沖銷帶 `Idempotency-Key`（`frontend/src/services/api/dynamic-api.ts` 的 `idem(...)`），任務端點不帶；帶了才冪等。
   - **雜湊加上通道**（複審建議 7）：MCP 通道的請求雜湊在既有輸入後面加 `"\nmcp"`，畫面通道的雜湊算法不變（既有紀錄照常回放）。理由：同一個人在畫面送出的 key 若被 AI 以相同內容重送，回放出來的是畫面通道的結果，含「不提供給 AI」的欄位；加了通道之後改回 `idempotency_key_reused`。`IdempotencyBehavior` 多注入 `IRequestChannel`，一行改動。
   - 儲存沿用 `Dynamic_Operation`（主鍵 `UserId, Key`），保留天數沿用政策 `maintenance.idempotencyRetentionDays`（A26，出廠 7 天），不另設。key 以使用者隔離、不以權杖隔離：同一人的兩把權杖用同一個 key 送同樣內容會回放，送不同內容回 `idempotency_key_reused`。
   - 並行重送：沿用交易開頭的全庫寫入鎖（SQLite `WriteGate`＋`BEGIN IMMEDIATE`；PostgreSQL `WriteGate.ForKey`＋advisory lock），後到者等前者提交後查到紀錄回放。PostgreSQL 與 SQLite 都要測（Task 3）。
   - 回放時 `ServiceResult.Data` 是 `JsonElement`，不是型別物件：**寫入工具不得把 `r.Data` 轉型後才用**，一律把 `Data` 當不透明物件放進 `data`；需要 id 時用 `JsonSerializer.SerializeToElement(r.Data)` 讀。唯一的例外是 `update_task` 要拿掉 `undoToken`，兩種形狀都要辨識（判斷 11）。對照測試驗「第一次的 data 與回放的 data 序列化後相同」（`IdempotencyBehavior.Json` 與 `McpResultFormatter.Json` 若有差異，以回放的 JSON 為準，把工具的輸出也先經同一份選項正規化）。
   - 工具在寫入請求之前的前置讀取（`update_record` 的 `GetRecordRequest`、`GetModuleSchemaRequest`）失敗時不擋寫入，照樣送寫入請求，讓 handler 判斷、讓重送照常回放（判斷 7）。

6. 〔技術〕**範圍與建立對話框**：
   - `McpScopes.Issuable` 加 `BusinessWrite`。
   - 預設勾選改成後端單一來源：`McpScopes.DefaultChecked = [BusinessRead]`（唯讀範圍；寫入範圍預設不勾，使用者 2026-11-21 裁決），`McpInfoResponse` 加 `DefaultScopes`，前端 `CreateAiTokenDialog` 開啟時 `scopes = [...info.defaultScopes]`。不在前端用「代碼以 `.read` 結尾」判斷（鐵則 2：預設值單一來源、前端從 API 取）。
   - `aiTokens.scopes.business.write` 四語改成具體說法：「新增與修改業務資料：建立與修改記錄、任務與留言、送簽與撤回」（en-US、th、vi 同義）。
   - 既有權杖不受影響：只有 `business.read` 的權杖看不到寫入工具；要寫入得重建權杖（設計 §5.2）。

7. 〔技術〕**`ignoredFields`**：`create_record`、`update_record` 的 `meta.ignoredFields` 列出「這次送了、但依 describe_module 的 schema 不可寫」的**表頭**欄位 key（不存在的 key、被遮、隱藏、唯讀、計算欄位），**不分原因**、依字母排序。
   - 模組代碼：`create_record` 用參數的 `moduleKey`；`update_record` 先送 `GetRecordRequest(recordId)`（唯讀、在交易外）取記錄所屬模組代碼（審查必修 4，二選一取第一種）。理由：AI 不必多帶一個可能填錯的 `moduleKey`；多一次唯讀查詢的成本小；記錄看不到時 `GetRecordRequest` 與寫入回同樣的 `forbidden`／`not_found`，不會多透露東西。
   - 接著送 `GetModuleSchemaRequest`（唯讀，不在交易內），可寫＝schema 裡出現、`config.readOnly` 為否、不是公式／彙總／自動編號；不在可寫集合的 key 進 `ignoredFields`。值照原樣送給寫入請求，由 `RecordValueProcessor` 決定實際忽略（伺服器仍是唯一判斷，工具的清單只是提示）。
   - 任一前置讀取失敗：不算 `ignoredFields`（`meta.ignoredFields` 省略），照樣送寫入請求。寫入的權限結果由 handler 決定；重送時記錄若已被別人刪掉，冪等回放仍能拿到第一次的結果。
   - 明細列的欄位不計入（已知限制：只涵蓋表頭）。規則唯讀、狀態機唯讀在 schema 看不出來，也不會列（已知限制）；結果的 `data` 是存檔後的記錄，模型可比對。
   - 不分原因是為了不透露被遮欄位存在：送一個猜的 key `salary`，不論它存在與否都只回「被忽略」（D1）。

8. 〔技術〕**寫入迴圈偵測**（C3 使用者裁決；D4、D5）：
   - 對象：**記錄**。工具描述多一個 `LoopTargetArg`（`update_record`、`submit_approval` 都是 `recordId`），其餘工具不偵測（建立沒有既有目標；任務與撤回的目標不是記錄，靠每分鐘速率擋）。
   - 判斷位置：執行器在範圍、速率、執行器自己的參數驗證（`idempotencyKey` 格式、`LoopTargetArg` 是正整數）之後，**上膛之前**。
   - **重送不擋**（審查建議 4）：先問 `McpUsage.HasOperationAsync(userId, idempotencyKey)`（讀 `IOperationRepository.GetAsync`）；已有紀錄代表這是重送，跳過迴圈檢查，讓 `IdempotencyBehavior` 照常回放（或回 `idempotency_key_reused`）。否則重送「第 30 次成功的那筆」會拿到 `write_loop`，模型會以為那次沒寫進去。
   - 計數＝稽核表中同一 `TokenId`、`TargetKind = record`、`TargetId`、動作在「寫入工具動作清單」內、`Result = Success`、`ReasonCode` 是 null（排除回放與預覽）、`OccurredUtc >= now − ops.ruleLoopWindowMinutes` 的列數。達到 `ops.ruleLoopTriggersPerRecord` 就擋。
   - 新 repository 方法 `IAuditLogRepository.CountWritesForTargetSinceAsync(tokenId, targetKind, targetId, sinceUtc, actions)`，等值比較加時間範圍，走既有 `(TargetKind, TargetId, OccurredUtc)` 索引；時間由應用層算 UTC，不用資料庫日期函式。由 `McpService/McpUsage.cs` 呼叫（工具與執行器不碰 repository，S2）。
   - 擋下時：回 `isError`、代碼 `write_loop`、訊息「同一筆記錄在 N 分鐘內已被這把權杖修改 M 次，已暫停這把權杖對它的寫入，請停下來請使用者確認」；稽核恰一列 `Denied`、`ReasonCode = write_loop`、`TargetKind = record`、`TargetId`（審查建議 5，方便稽核頁篩選與事件對照）、`TargetLabel` 是工具名稱。被擋的呼叫不計入迴圈次數（只計成功），但計入每日名額（D8）。
   - **事件**（審查必修 5）：
     - `IncidentRecorder` 只能在交易型請求的 handler 裡用，執行器在交易外。照 `RuleFailureBehavior` 的寫法，用 `IServiceScopeFactory` 開新 scope，送新的交易型請求 `RecordMcpWriteLoopIncidentRequest`（`McpService/WriteLoop/`，不是 `IIdempotentRequest`、不上膛，所以不會被 `McpWriteAuditBehavior` 記成本次的寫入）。寫事件失敗只記 Error log，不影響回給模型的錯誤（同 C3「計數寫在業務交易之外」）。
     - 訊號寫明 `Source = IncidentSource.Config`、`Category = IncidentCategory.Config`、`Code = mcp.writeLoop`；收件人走哪條政策由 `Category` 決定（`OpsNotifier.NotifyAsync`），所以 `Category` 一定要設。指紋＝`mcp.writeLoop`＋權杖 id＋記錄 id，照 `IncidentRecorder` 既有規則去重（同指紋未結→次數＋1；已結→重開並標回歸）。
     - 標題鍵 `ops.title.mcpWriteLoop`，參數只放代碼與數字（O2-T22）：權杖 id、記錄 id、次數、時間窗分鐘數。權杖名稱與擁有者是使用者輸入的字串，不進標題；管理員從「AI 連線」頁用權杖 id 查。`IncidentTitles` 新增 `WriteLoop(long tokenId, long recordId, int count, int minutes)`。
     - 嚴重度 Warning；`IncidentRecorder.ShouldNotify` 對 Warning 只在新開、回歸、升級時通知，同指紋再發生只累加次數；可在事件頁手動結案。收件人照 `ops.configIssueRecipients`：`RuleEditor` 是 null，所以 `ruleEditor` 那一項解析成空；整份清單都解析為空時才改送全體系統管理員（出廠值另含全部系統管理員，所以出廠時就是系統管理員）。
   - 恢復：時間窗滑過、成功次數降到門檻以下就自動恢復，不另做「恢復」按鈕（D4；鋸齒效應見已知限制）。
   - 「或來源」：mcp2a 只有 MCP 這一種外部來源，所以只看權杖；整合來源上線時再擴充（已知限制）。

9. 〔技術〕**`submit_approval`：只有 `previewOnly` 才預覽**（審查必修 3；設計 §7.2 原寫「先走預覽再送」）：
   - `previewOnly: true`：只送 `PreviewRecordApprovalRequest`（唯讀、不開交易），回 `data.preview`（含要自選的關卡與候選人 `PickRequirement`），不寫入。執行器照判斷 3 已經上膛，但沒有寫入請求所以不會提交；工具回 `AuditReason = preview`，執行器事後寫一列（`Success`、`ReasonCode = preview`，迴圈不計）。
   - 沒帶 `previewOnly`：不先預覽，直接送 `SubmitApprovalRequest`，handler 自己驗待簽、版本與自選人。回 `data = { outcome, instanceId, recordRowVersion }`，不含 `preview`。
   - 理由：預覽看到已有簽核中的實例就回 `Conflict(AlreadyPending)`，直接轉移之後記錄狀態也變了；先預覽的話，第一次成功之後的同 key 重送會被預覽擋下，`IdempotencyBehavior` 根本沒機會回放，模型會以為送簽失敗。
   - 自選關卡缺人時，handler 回 `Invalid["picks.{code}"]`，模型先用 `previewOnly` 看候選人再重送。
   - Task 9 把設計 §7.2 改成「預覽是 `previewOnly` 的那一次呼叫」。

10. 〔技術〕**annotations**（設計 §6.2）：`McpSdkAdapter.ListToolsAsync` 依級別給：唯讀 `readOnlyHint: true, destructiveHint: false`；一般寫入 `readOnlyHint: false, destructiveHint: false, idempotentHint: true`（判斷 5 的必填 key）；高風險 `destructiveHint: true`（mcp2b 用）。全部 `openWorldHint: false`。現在寫入級別的 `destructiveHint` 是 null，要改。

11. 〔技術〕**`TaskHiddenAfterWrite` 拿掉 `undoToken`**（審查必修 7）：主管把任務派給自己看不到的人時，`update_task` 回 `data = { taskId, hiddenAfterWrite: true }`，不給 `undoToken`（復原不是本段工具；undo token 只給畫面）。
    - 第一次呼叫拿到型別物件 `TaskHiddenAfterWrite(Id, Visible, UndoToken)`；冪等回放拿到 `JsonElement` `{id, visible:false, undoToken:"…"}`。兩種形狀都要辨識並改寫成上面的結果，不能照「不透明」原則原樣交出去。
    - 辨識寫在 `TaskWriteTools` 裡的一個私有函式，兩種形狀共用。

12. 〔技術〕**每日名額與速率**：寫入工具和唯讀工具共用 mcp1a 的每分鐘、每日上限，不另設寫入專用政策。被擋、失敗、取消都計入每日名額（D8）。

13. 〔技術〕**訊息與文案**：新增 `McpMessages.WriteLoop(count, minutes)`、`McpMessages.IdempotencyKeyRequired`（格式不符也用它）；`conflict` 直接用 handler 訊息。新訊息照 mcp1a 進 `mcp.json` 基準檔，四語同步。事件標題 `ops.title.mcpWriteLoop` 四語。

14. 〔技術〕**既有測試的預期值變動只有這幾處**：
    - `DomainTests/Mcp/McpScopesTests.cs`：`Issuable` 多 `business.write`；新增 `DefaultChecked`。
    - `DomainTests/Mcp/McpToolCatalogTests.cs`：`Production_TwelveTools_IncludingWhoami` 改成 19 個（12 唯讀＋7 寫入）；`Scopes_WhoamiNull_OthersBusinessRead_AllRead` 改成「唯讀工具都是 `business.read`、寫入工具都是 `business.write` 與 `Write`」。
    - `WebAPITests/Dynamic/Mcp/McpToolsApiTests.cs`：`ToolsList_Lists12ProductionTools` 改成只比 `business.read` 範圍的工具（Task 2 到 Task 7 之間 `tools/list` 與 `Production` 不相等）；寫入工具的列表斷言在 `McpTransportTests`。
    - `McpTokenHandlerTests`、`McpTokenApiTests`：`me/mcp` 的 `scopes` 多一項、多 `defaultScopes`；可建立 `business.write`。
    - `McpTransportTests`：annotations 的期望值（判斷 10）；`business.write` 權杖的 `tools/list` 多寫入工具。
    - `McpToolInvokerTests.Conflict_Stale`：改成只有 `code=stale` 的 Conflict 才是 `stale`，另加 `Conflict_Other`。
    - `McpAuditSummaryTests`：既有案例不變，新增寫入參數案例（判斷 15）。
    - `IAuditLogRepository` 加方法後，三個測試假實作要補空實作：`WebAPITests/Ops/AuditApiTests.cs` 的 `ThrowingRepository`、`CountingPurge`，`ApplicationTests/Common/Audit/AuditLogTests.cs` 的 `ThrowingRepository`（Task 8）。
    - `AiTokensPage.vitest.spec.ts`：INFO 加 `business.write` 與 `defaultScopes`，斷言送出只有 `['business.read']`。
    - `WriteJournalAndIdempotencyTests`、`IdempotencyApiTests`：不改預期，只新增任務與 multipart 案例。
    - 其他既有測試若因此要改，停下回報。

15. 〔技術〕**稽核摘要逐參數去值**（審查必修 1；Global Constraints「安全」）：`McpAuditSummary.Args` 現在只特別處理 `filter`、`keyword`，其餘參數原樣保留，寫入工具上線後會把欄位值、留言內文，連 AI 送了但被忽略的「不提供給 AI」欄位值都存進稽核摘要。規則：
    - `values`：只留鍵（排序後的陣列）。
    - `details`：留明細模組 key、列數、每列的鍵（含 `id` 是否存在，不含值）。
    - `title`、`description`、`body`、`comment`：只記長度。
    - `picks`：留關卡代號與人數。
    - `patch` 不存在於參數（`update_task` 的欄位是逐個參數），`assigneeUserId` 保留（是 id、不是自由文字）；`dueDate`、`startDate` 只記「有／清除」。
    - `idempotencyKey`、各種 id、`rowVersion`、`status`、`cascade`、`to`、`previewOnly`、`moduleKey`、`hostKey`：保留。
    - **未列的鍵**（複審 N1）：`McpAuditSummary.Args` 多一個模式參數 `strict`。寫入工具、高風險工具、不存在的工具（`unknown_tool`）用 strict：上面沒列到的鍵（模型猜錯的 `fields`、`data`、`record`，或多帶的 `systemKeys`）只記 `{ type, length }`，不留原值。唯讀工具維持現狀，`McpAuditSummaryTests` 既有案例不改。伺服器不驗 `additionalProperties`（schema 只是給 client 的提示，`McpArgs` 不拒收不認得的鍵），所以這條是唯一的保護。
    - `values`、`details` 不是物件，或 `details` 的列、列的 `values` 不是物件時，只記型別，不留原文。
    - 上膛的 `McpWriteAudit` 與所有結束路徑的列（含 `unknown_tool`、`scope_denied`、`invalid_arguments`、`write_loop`）都用同一個 `McpAuditSummary.Args`（參數雜湊照舊算原始參數）。

16. 〔技術〕**白名單組請求**（審查必修 6）：工具只從判斷 2 列的參數逐欄組 request，不把參數 `Deserialize` 成 request、不把參數原樣塞進 `Patch`。
    - `CreateRecordRequest`、`UpdateRecordRequest` 頂層的 `SystemKeys` 沒有 `[JsonIgnore]`、不受唯讀限制，工具一律給 null；`DetailRowInput` 只組 `Id`、`Values`、`RowVersion`，`Delete` 固定 false（D3）。
    - `UpdateTaskRequest.Patch` 由工具逐鍵寫入判斷 2 列的鍵，`title`、`description` 不會出現。
    - `values`、明細列 `values` 轉成 `Dictionary<string, JsonElement?>` 的解析放在 `McpArgs`（`Tools/` 底下，不在 S6 的掃描範圍）。
    - S6 grep：`grep -rnE "SystemKeys|Deserialize<\w*Request" backend/src/DynamicModules.Application/Dynamic/McpService/Tools/Write` 沒有結果（只擋把參數反序列化成 request，不誤擋正常解析）。

## 與設計的矛盾（規劃時發現；Task 9 回填設計文件）

1. **冪等「自動接手」只對記錄寫入成立**：設計 §7.2；behavior 只處理 `IRecordWriteRequest`。本計畫加 `IIdempotentRequest`（判斷 5）。
2. **寫入稽核「透過既有的 `IWriteStage`」**：設計 §8.1；`IWriteStage` 只由 `RecordEffectsBehavior` 對 `IRecordWriteRequest` 執行。改用 `McpWriteAuditBehavior`（判斷 3）。
3. **錯誤對照**：設計 §6.5 只有 `stale`；補 `conflict`（判斷 4）。
4. **`mark_notifications_read`**：不做（D6，票 #82）。
5. **迴圈時間窗沒有政策**：維運設計只登記次數（D5）。
6. **annotations**：mcp1a 的寫入級別 `destructiveHint` 是 null、沒有 `idempotentHint`（判斷 10）。
7. **`idempotentHint` 的條件**：設計寫「帶冪等鍵時」，但 annotations 是每個工具固定的；改成 key 必填、一律標 true（判斷 5、10）。
8. **設計 §5.5 已知限制第三點**：本段審完，寫入端語意照 D1、D2 回填。
9. **設計 §8.1「operation id 可串到事件事實與記錄歷程」**：`RecordHistory` 沒有 OperationId（只有 `ChangedUserId`、`ChangedTime`），只串得到事件事實；改寫成「可串到事件事實」，記錄歷程的串接隨票 #81。
10. **設計 §7.2「submit_approval 先走預覽再送」**：先預覽會讓重送無法回放（判斷 9）；改成「預覽是 `previewOnly` 的那一次呼叫」。

## 與 #74／#75 的衝突點

#74（一般參照來源的彙總與停用公式帶出群組隱藏欄位）、#75（條件引用隱藏欄位的彙總不會遮）是畫面通道的**讀取**遮罩修補，**已合併 main**（`c084787a`，之後還有 `4c83c989`）。它改的檔：

| 檔案 | #74／#75 的改動 | mcp2a 的處理 |
|---|---|---|
| `Application/Common/Security/PermissionResolver.cs` | `HideDependents` 去掉 `aiMasked` 參數，畫面通道也走原本 MCP 才走的嚴格路徑 | **mcp2a 不改這個檔** |
| `Application/Common/Security/RollupSourceMasks.cs` | 移除 `inputs.AiMasked` 條件；入口加系統管理員短路（`IsSystemAdmin && !AiMasked`，只對畫面通道生效） | 不改 |
| `ApplicationTests/Dynamic/Mcp/McpAiExclusionTests.cs` | 畫面通道斷言的理由文字（MCP 斷言沒改） | **不改**；寫入端案例另開 `McpAiWriteMaskTests` |
| `ApplicationTests/Dynamic/{ReportRollupMaskTests,RollupFormulaMaskTests}.cs` | 新檔 | 不碰 |
| `DynamicAccessGuard.cs`、`ChannelPermissionRepository.cs` | 沒改 | 不改 |

- 開工前 `git rebase origin/main`；本段的改動都在其他檔，衝突只會在文件。
- MCP 通道的遮罩結果在 #74／#75 前後不變，D1（不能寫）完全沿用權限解析的輸出（`EffectivePermissions.HiddenFieldIds`），所以沒有程式檔衝突。
- 系統管理員的期望值寫死：短路只對畫面通道生效，**MCP 通道的系統管理員照樣被遮**（被遮欄位與依賴它的公式、彙總都看不到）；畫面通道的系統管理員看得到。
- S4 鎖住 `Common/Security/` 沒有 diff。

## Global Constraints

- **MCP 關閉、或權杖沒有 `business.write` 時，行為與 main 相同**；畫面通道的冪等與稽核行為不變（前端任務端點不帶 key；帶 key 的附件留言放行）。既有測試的預期值只照判斷 14 改。
- **鐵則 2**：迴圈次數與時間窗走 `PolicyCatalog`；長度上限沿用既有 request 的常數；`McpLimits` 不新增業務數字。
- **鐵則 4**：空系統（`new TestApp(seedDemoPeople: false)`）下，`create_task`（個人任務）、`update_task`、`add_task_comment` 照常可用；記錄工具在沒有模組時回 `forbidden`（與畫面相同）。
- **鐵則 5**：沒有執行期 DDL；寫入權限全經既有 guard；稽核寫不進去就整筆回滾（成功路徑）或回 `audit_unavailable`（失敗路徑）；MCP 通道沒上膛的寫入請求丟例外。
- **PostgreSQL**：新的 Application 測試類別 `McpWriteAuditTests`、`McpIdempotencyTests`、`McpWriteLoopTests`、`AuditLogWriteCountTests` 與 WebAPI 的 `McpWriteToolsApiTests`、`McpWriteParityTests`、`McpAiWriteMaskTests` 標 `Category=Postgres`；每次雙跑核對執行數與 SQLite 相同，交棒說明寫數字。查詢全走 EF、時間範圍應用層算 UTC。
- **安全**：權杖、確認碼明文不進稽核與 log；稽核摘要照判斷 15 去值；工具照判斷 16 組請求。
- **測試只跑窄範圍**；完整套件交 CI（mcp2a-4 收尾的 gate）。
- **git**：逐檔 `git add`；commit 前 `git status`；不用 `--no-verify`、不 force push、不 merge commit。
- **公開 repo**：不寫公司字串、個人絕對路徑、內網 IP；測試資料全部虛構。
- **本機走查**：`bash scripts/unattended/local-demo.sh <worktree 絕對路徑>`；實機驗收用 `DEMO_LOGIN=false`。
- 段收尾用 `formwright-purpose-check`；上線用 `formwright-ship`。

## 寫死清單（本段新增，逐條標性質；Task 9 寫進設計 §15）

| 項目 | 常數位置 | 性質 |
|---|---|---|
| 7 個寫入工具、範圍 `business.write`、級別一般寫入 | `McpToolCatalog` | 平台功能清單 |
| 可建立範圍加 `business.write`；預設勾選只有唯讀範圍 | `McpScopes.Issuable`、`McpScopes.DefaultChecked` | 平台功能清單（預設勾選依使用者 2026-11-21 裁決） |
| 一次工具呼叫只能送一個最外層 `IIdempotentRequest`；寫入稽核與業務同交易；MCP 通道沒上膛的 `IIdempotentRequest` 丟例外；其他交易型請求放行 | `McpWriteAuditBehavior` | 安全、資料完整性 |
| 稽核摘要不存寫入的值（判斷 15） | `McpAuditSummary` | 安全 |
| 工具只從白名單參數逐欄組請求，`SystemKeys` 一律 null | `Tools/Write/*` | 安全 |
| 寫入工具的 `idempotencyKey` 必填，格式沿用 `OperationContext.KeyPattern` | `McpArgs` | 資料完整性（技術限制） |
| MCP 通道的冪等雜湊加通道後綴 | `IdempotencyBehavior` | 安全 |
| 寫入、高風險、不存在工具的稽核摘要，未列的鍵只記型別與長度 | `McpAuditSummary` | 安全 |
| 附件留言（`Files` 非空）不做請求層冪等 | `AddTaskCommentRequest.IdempotencyApplies` | 技術限制（`Stream` 不能可靠雜湊） |
| 被遮欄位寫入時與隱藏欄位同語意（D1） | 既有 `RecordValueProcessor` | 安全 |
| `ignoredFields` 不分原因、只涵蓋表頭 | `WriteTool` | 安全 |
| `update_record` 不收明細列 `_delete`（D3） | `UpdateRecordTool` | 安全（mcp2b 重審） |
| `update_task` 結果不給 `undoToken`（含回放） | `TaskWriteTools` | 安全 |
| 迴圈只計成功且非回放的寫入；有冪等紀錄時不擋；寫事件失敗不影響回應 | `McpToolInvoker`、`McpUsage` | 技術限制 |
| 迴圈事件標題只放代碼與數字 | `IncidentTitles.WriteLoop` | 安全（O2-T22） |
| 工具參數的長度上限沿用既有 request 常數（`TaskEditParser.TitleMax`、`TaskCommentRules.BodyMax`） | `McpTexts`（插值） | 技術限制（單一來源） |
| 寫入工具 annotations：`destructiveHint` false、`idempotentHint` true | `McpSdkAdapter` | 技術限制 |

**業務政策**：`ops.ruleLoopTriggersPerRecord`（出廠 30、5–1000）、`ops.ruleLoopWindowMinutes`（D5，出廠 10、1–1440），出廠值與上下限只在 `PolicyCatalog`。

## 檔案地圖

**共用檔（並行工作要協調；★＝本段會動）**

| 共用檔 | Task | 說明 |
|---|---|---|
| EF migration、DbContext 模型設定 | 不動 | 本段沒有 migration |
| ★ DI 註冊 | 2、3、8 | `Application/DependencyInjection.cs`：`McpWriteAuditBehavior` 登記在 `McpConfirmationBehavior` 之後、`AuditBehavior` 之前；scoped `IMcpWriteScope`；寫入工具的 `IMcpTool` 登記 |
| ★ 冪等 | 2、3 | Task 2：新 `Common/IIdempotentRequest.cs`、`Application/Common/IRecordWriteRequest.cs`；Task 3：`Common/Behaviors/IdempotencyBehavior.cs`（判斷改 marker、通道後綴、`IdempotencyApplies`）、`Dynamic/TaskService/TaskRequests.cs`（5 個 request 加 marker、留言覆寫 `IdempotencyApplies`） |
| ★ 政策目錄 | 8 | `Domain/Policies/PolicyCatalog.cs`、`shared/policy-catalog.json` |
| ★ 介面文字 | 1、2、8 | `shared/i18n/{zh-TW,en-US,th,vi}/ui.json`：`aiTokens.scopes.business.write`、`audit.actions.mcp.*`（7 個）、`policies.items.ops.ruleLoop*`、`ops.title.mcpWriteLoop` |
| ★ 後端訊息 | 3、8 | `McpService/McpMessages.cs`、基準檔 `ApplicationTests/Common/ServerTextBaseline/mcp.json` |
| ★ 稽核 | 2 | `Application/Common/Audit/AuditActions.cs`、`McpService/McpAuditSummary.cs` |
| ★ 稽核 repository | 8 | `Domain/Repositories/Dynamic/IAuditLogRepository.cs`、`Persistence/Repositories/Dynamic/AuditLogRepository.cs`（`CountWritesForTargetSinceAsync`）；測試假實作 `WebAPITests/Ops/AuditApiTests.cs`、`ApplicationTests/Common/Audit/AuditLogTests.cs` |
| ★ 維運事件 | 8 | `Dynamic/OpsService/Incidents/IncidentTitles.cs`（`WriteLoop`） |
| ★ MCP 既有檔 | 1–8 | `Domain/Mcp/{McpScopes,McpToolCatalog}.cs`、`McpService/Tools/{McpToolInvoker,IMcpTool,McpArgs}.cs`、`McpService/{McpTexts,McpUsage}.cs`、`McpService/Tokens/McpTokenModels.cs`、`WebAPI/Mcp/McpSdkAdapter.cs` |
| ★ 前端 | 1 | `frontend/src/presentation/mcp/CreateAiTokenDialog.vue`、`frontend/src/domain/mcp/mcp-types.ts`、`frontend/src/pages/account/AiTokensPage.vitest.spec.ts` |
| ★ 手冊 | 9 | `docs/manual.md` §24 |
| 權限解析（`PermissionResolver`、`RollupSourceMasks`、`DynamicAccessGuard`、`ChannelPermissionRepository`） | 不動 | 見「與 #74／#75 的衝突點」 |
| `apps/*.app.json` | 不動 | 本段不碰套件 |

**新檔（後端）**
- `Application/Common/IIdempotentRequest.cs`
- `Application/Dynamic/McpService/Writes/{IMcpWriteScope,McpWriteScope,McpWriteAuditBehavior,McpWriteTargets}.cs`
- `Application/Dynamic/McpService/Tools/Write/{WriteTool,RecordWriteTools,TaskWriteTools,ApprovalWriteTools}.cs`
- `Application/Dynamic/McpService/WriteLoop/RecordMcpWriteLoopIncident.cs`（request＋handler）

**新檔（測試）**
- `ApplicationTests/Dynamic/Mcp/{McpWriteAuditTests,McpIdempotencyTests,McpWriteLoopTests,McpWriteTargetsTests}.cs`
- `ApplicationTests/Persistence/AuditLogWriteCountTests.cs`
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

### Task 2: 寫入稽核同交易、摘要去值、錯誤對照、annotations

**Files:**
- Create: `Application/Common/IIdempotentRequest.cs`（判斷 3；`IdempotencyApplies` 預設屬性在 Task 3 加）、`McpService/Writes/{IMcpWriteScope,McpWriteScope,McpWriteAuditBehavior,McpWriteTargets}.cs`
- Modify: `Application/Common/IRecordWriteRequest.cs`（改繼承 `IIdempotentRequest`）、`Application/DependencyInjection.cs`、`McpService/Tools/McpToolInvoker.cs`（上膛、執行器自己的參數驗證、已提交判斷、`AuditReason` 寫進 `ReasonCode`、`Mapped` 的 `conflict` 與 `idempotency_key_reused`）、`McpService/Tools/IMcpTool.cs`（`McpErrorCodes` 加 `Conflict`、`IdempotencyKeyReused`、`WriteLoop`；`McpToolOutcome` 加 `AuditReason`）、`McpService/McpAuditSummary.cs`（判斷 15）、`Domain/Mcp/McpToolCatalog.cs`（7 個寫入工具的宣告，`LoopTargetArg`）、`Common/Audit/AuditActions.cs`（7 個 `W`）、`shared/i18n/*/ui.json`（`audit.actions.mcp.*`）、`WebAPI/Mcp/McpSdkAdapter.cs`
- Test: `ApplicationTests/Dynamic/Mcp/{McpWriteAuditTests,McpWriteTargetsTests,McpToolInvokerTests,McpAuditActionsTests,McpAuditSummaryTests,McpToolArchitectureTests}.cs`、`DomainTests/Mcp/McpToolCatalogTests.cs`、`WebAPITests/Dynamic/Mcp/{McpTransportTests,McpToolsApiTests}.cs`

- [ ] **Step 1: 寫失敗的測試**
  - `McpWriteAuditTests`（`Category=Postgres`；測試組件裡定義 `TestWriteTool` 與 `TouchRecordWriteRequest : IRecordWriteRequest`，可設定成功、回失敗、丟例外、送兩個請求）：
    - `Success_OneRow_InTransaction`：記錄改了；稽核表恰一列 `mcp.<測試工具>`、`Success`、`RiskLevel=Write`、`TargetKind=record`、`TargetLabel` 是工具名、`TokenId`；摘要有 `op`，且等於事件事實的 `OperationId`。
    - `AuditSaveFails_RollsBackBusiness`【雙跑】：`TestDb` 加一個對 `Dynamic_AuditLog` 新增丟例外的攔截器 → 記錄沒變；回 `audit_unavailable`（invoker 的失敗列也寫不進）。
    - `HandlerFails_InvokerWritesOneRow`：handler 回 `Invalid` → 記錄沒變、稽核恰一列 `Invalid`（由 invoker 以 `WriteNowAsync` 寫）。
    - `HandlerThrows_OneFailedRow`。
    - `TwoTopLevelWrites_Throws`：工具送兩個交易型請求 → 第二個丟 `InvalidOperationException`、第一個已提交的照記（一列 `Success`），回應不是錯誤但 `meta.dataOmitted` 是 `post_commit_error`。
    - `NestedTransactional_NotAudited`：handler 內巢狀的交易型請求不另寫列。
    - `UiChannel_NoArm_NoRow`：畫面通道送同一個請求 → 沒有任何稽核列（記錄寫入在畫面不記稽核，行為不變）。
    - `Mcp_UnarmedWrite_Throws`：MCP 通道、沒上膛，直接送 `TouchRecordWriteRequest` → `InvalidOperationException`、記錄沒變；同樣狀態送 `CreateMcpConfirmationRequest` → 照常成功。
    - `FailedWrite_ThenNonIdempotentTransactional_PassesThrough`（複審 N2）：上膛後工具先送 `TouchRecordWriteRequest` 並回失敗，再送一個一般的交易型測試請求（不是 `IIdempotentRequest`，模擬簽核警示）→ 第二個照常執行、資料寫進去；稽核恰一列 `Invalid`、沒有 `Success` 列；`IsCommitted` 是 false。
    - `Armed_NonIdempotentFirst_ThenWrite_OneSuccessRow`：上膛後先送一般交易型請求、再送 `TouchRecordWriteRequest` → 只有後者被接手，恰一列 `Success`。
    - `AuditReason_WrittenWhenNotCommitted`：工具不送寫入請求、回 `AuditReason = preview` → 一列 `Success`、`ReasonCode=preview`。
    - `ExecutorArgs_Invalid_NotArmed`：`idempotencyKey` 格式不符、或 `LoopTargetArg` 不是正整數 → `invalid_arguments`、工具沒被呼叫、`IsArmed` 是 false。
    - `Committed_ResultTooLarge_NotError_OneRow`、`Committed_CancelledAfterCommit_OneRow`（照 mcp1a 確認碼的同名案例）。
    - `AuditedRequest_Rejected`：上膛後送 `IAuditedRequest` → 丟例外、記錄沒變。
  - `McpWriteTargetsTests`：7 種 request 的 target；回應是型別物件與 `JsonElement` 都讀得到 id。
  - `McpAuditSummaryTests`（判斷 15，每個參數一個案例）：`values` 只剩排序後的鍵；`details` 只剩模組 key、列數、每列鍵與有無 `id`；`title`／`description`／`body`／`comment` 只剩長度；`picks` 只剩代碼與人數；`dueDate` 只剩有／清除；`idempotencyKey`、id、`rowVersion`、`status` 保留；整份摘要字串不含任何送出的值（用帶特殊字樣的值斷言）。複審 N1 的三個（都用帶特殊字樣的值斷言整份摘要找不到它）：
    - `Write_UnknownArg_ValueNotStored`：寫入工具多帶 `fields`、`systemKeys` → 只有 `{type, length}`。
    - `UnknownTool_ValuesNotStored`：呼叫 `create_records`（不存在）帶 `values` → `unknown_tool` 那一列的摘要找不到值。
    - `Values_NotObject_TypeOnly`：`values` 是字串、`details` 是陣列、明細列是數字 → 只記型別。
    - 唯讀工具的既有案例不變（未列的鍵照舊）。
  - `McpToolInvokerTests`：`Conflict_Stale`（`code=stale`）、`Conflict_Other`（`code=subtasksOpen` → `conflict`，`fields` 帶 `code` 與 `params.count`，訊息依 `X-Locale`）、`Unprocessable_IdempotencyReused`。
  - `McpAuditActionsTests`：7 個新動作在 `AuditActions`、四語都有標籤。
  - `McpToolCatalogTests`：照判斷 14 改。
  - `McpToolArchitectureTests`：寫入工具也只依賴 `ISender`；掃 Application 組件，`McpWriteTargets` 涵蓋的 request 型別都不是 `IAuditedRequest`、`IMcpConfirmedRequest`。
  - `McpTransportTests`：寫入工具 `readOnlyHint` false、`destructiveHint` false、`idempotentHint` true；唯讀工具 `destructiveHint` false；`business.read` 權杖看不到寫入工具、呼叫回 `scope_denied`。
  - `McpToolsApiTests.ToolsList_Lists12ProductionTools`：照判斷 14 改成只比唯讀範圍。
- [ ] **Step 2: 跑測試確認失敗**：`cd backend && dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~McpWriteAuditTests|FullyQualifiedName~McpWriteTargetsTests|FullyQualifiedName~McpToolInvokerTests|FullyQualifiedName~McpAuditActionsTests|FullyQualifiedName~McpAuditSummaryTests|FullyQualifiedName~McpToolArchitectureTests"`；`dotnet test tests/DynamicModules.DomainTests --filter "FullyQualifiedName~McpToolCatalogTests"`；`dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~McpTransportTests|FullyQualifiedName~McpToolsApiTests"`
- [ ] **Step 3: 實作**（判斷 1、3、4、10、15）。工具類別在 Task 4、6、7 才寫；`McpToolCatalog` 先宣告 7 個工具，`tools/list` 只列有實作的（`McpToolInvoker.ListFor` 依 DI 裡的 `IMcpTool`），所以寫入工具的數量斷言在 Task 7 收尾時才補（`business.write` 權杖的 `tools/list` 是 19 個）。
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；加跑 `AuditBehaviorTests`、`AuditCoverageTests`、`McpConfirmationTests`、`McpConfirmationContractTests`、`TransactionBehaviorTests`、`McpAuditApiTests`。PostgreSQL：`FORMWRIGHT_TEST_DB=postgres dotnet test tests/DynamicModules.ApplicationTests --filter "Category=Postgres&FullyQualifiedName~McpWriteAuditTests"`。
- [ ] **Step 5: Commit**：超過 700 行就拆兩個——`feat(mcp): 寫入工具的稽核與業務同交易`、`feat(mcp): 稽核摘要去值與衝突代碼對照`。

### Task 3: 冪等鍵

**Files:**
- Modify: `Common/IIdempotentRequest.cs`（加預設屬性 `IdempotencyApplies`）、`Common/Behaviors/IdempotencyBehavior.cs`（判斷改 marker、`IdempotencyApplies`、通道後綴）、`Dynamic/TaskService/TaskRequests.cs`、`McpService/Tools/{McpToolInvoker,McpArgs}.cs`、`McpService/McpMessages.cs`、`ServerTextBaseline/mcp.json`
- Test: `ApplicationTests/Dynamic/Mcp/McpIdempotencyTests.cs`（`Category=Postgres`）、`ApplicationTests/Dynamic/WriteJournalAndIdempotencyTests.cs`、`WebAPITests/IdempotencyApiTests.cs`（新增案例）

- [ ] **Step 1: 寫失敗的測試**
  - `WriteJournalAndIdempotencyTests`：`Task_create_with_same_key_replays`（同 key 兩次 `CreateTaskRequest` → 一筆任務、第二次 `Replayed`）、`Task_comment_with_same_key_replays`、`Task_update_same_key_different_patch_is_unprocessable`、`Task_request_without_key_runs_every_time`（畫面行為不變）、`Task_comment_with_files_bypasses_idempotency`（帶 key、`Files` 非空 → 兩次都寫、不丟例外）。
  - `IdempotencyApiTests`：`Multipart_comment_with_key_succeeds_twice`（帶 `Idempotency-Key` 標頭送兩次 multipart 留言 → 兩次 201、兩則留言，不是 500）。
  - `McpIdempotencyTests`（用 `TestWriteTool`，送 `TouchRecordWriteRequest` 與 `AddTaskCommentRequest`）：
    - `SameKey_Replays_OneWrite_MetaReplayed`：第二次 `meta.replayed` true、資料只寫一次；兩次的 `data` 序列化後相同。
    - `SameKey_Replay_AuditRow_Replayed`：第二次也寫一列 `Success`、`ReasonCode=replayed`。
    - `SameKey_DifferentArgs_IdempotencyKeyReused`：`isError`、`idempotency_key_reused`、沒有寫入。
    - `Key_Missing_InvalidArguments`、`Key_BadFormat_InvalidArguments`（`fields.idempotencyKey`）。
    - `Key_IsolatedPerUser`：兩個使用者用同一個 key → 各寫一次。
    - `Key_SharedAcrossTokensOfSameUser`：同一人兩把權杖、同 key 同內容 → 第二把回放。
    - `Key_UiThenMcp_NotReplayed`：同一人先經畫面通道用 key K 送一個請求，再經 MCP 用 K 送同樣內容 → `idempotency_key_reused`，不回放畫面的結果（雜湊含通道）。
    - `UiHash_Unchanged`：畫面通道的雜湊與改動前相同（用固定請求比對已知雜湊值），既有紀錄照常回放。
    - `FailedFirst_RetryExecutes`：第一次 handler 回 `Invalid` → 沒有紀錄；同 key 修正後重送 → 執行。
    - `ConcurrentSameKey_OneWrite`【雙跑】：兩個 scope 同時送同 key → 恰一次寫入，另一個回放。
    - `Key_NotLeakingToNextCall`：同一個 DI scope 連續兩次呼叫，第二次沒帶 key（測試專用的選填工具）→ `ClientKey` 是 null。
- [ ] **Step 2: 跑測試確認失敗**：`cd backend && dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~McpIdempotencyTests|FullyQualifiedName~WriteJournalAndIdempotencyTests"`；`dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~IdempotencyApiTests"`
- [ ] **Step 3: 實作**（判斷 5）。
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；加跑 `EffectsRuntimeApiTests`、`TasksApiTests`、`TaskCommentAttachmentsApiTests`、`BuiltinServerTextTests`。PostgreSQL：`FORMWRIGHT_TEST_DB=postgres dotnet test tests/DynamicModules.ApplicationTests --filter "Category=Postgres&(FullyQualifiedName~McpIdempotencyTests|FullyQualifiedName~McpWriteAuditTests)"`。
- [ ] **Step 5: Commit**（`feat(mcp): 寫入工具的冪等鍵，任務與留言納入請求層冪等`）

**驗收（mcp2a-1 交棒）**：Task 1–3 的 Step 4 全過；PostgreSQL 執行數與 SQLite 相同，寫進交棒說明。

# mcp2a-2 記錄與遮罩

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
    - `Update_DataScope_NotFound`：資料範圍外的記錄 → REST 404、MCP `not_found`。
    - `Create_NoAddPermission_Forbidden`：REST 403、MCP `forbidden`。
  - `McpWriteToolsApiTests`：
    - `CreateRecord_Details`：明細兩列一起建；`details` 帶 `_delete` → `invalid_arguments`（D3）。
    - `UpdateRecord_DetailDelete_InvalidArguments`：修改時某一列帶 `_delete: true` → `invalid_arguments`，資料庫的明細列數不變。
    - `UpdateRecord_SystemKeysArg_Ignored`：參數多帶 `systemKeys`（與明細列裡的 `systemKeys`）並送一個唯讀欄位的值 → schema 驗證擋下多餘參數或忽略，唯讀欄位的值沒變。
    - `CreateRecord_IgnoredFields`：送一個唯讀欄位、一個不存在的 key → `meta.ignoredFields` 兩個都列、依字母排序；值沒寫進去。
    - `UpdateRecord_IgnoredFields_UsesRecordModule`：`update_record` 不帶 `moduleKey` 也算得出 `ignoredFields`。
    - `UpdateRecord_Replay_AfterRecordDeleted`：第一次修改成功後，別人在畫面刪掉這筆；同 key 重送 → 回放第一次的結果（`meta.replayed` true），不是 `not_found`。
    - `UpdateRecord_DetailRow_UpdateAndAdd`。
    - `CreateRecord_ExtensionModule_HostKeyRequired` → `invalid`（`fields.hostKey`）。
    - `CreateRecord_Audit`：稽核一列 `mcp.createRecord`、`TargetId` 是新記錄 id；摘要的 `args.values` 只有鍵沒有值（判斷 15）。
    - `CreateRecord_ModuleClosed_Conflict`。
    - `Prompt_Injection_Values`：值裡有「忽略之前指示」字樣 → 照存，回傳在 `data` 區、帶不可信標示。
- [ ] **Step 2: 跑測試確認失敗**：`cd backend && dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~McpWriteParityTests|FullyQualifiedName~McpWriteToolsApiTests"`
- [ ] **Step 3: 實作**（判斷 2、5、7、16）。`WriteTool` 基底提供：`ignoredFields` 計算（含前置讀取失敗時省略）、`Fail(r)`、把 `r.Data` 原樣放進 `data`、`ResultCount = 1`。
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；加跑 `McpToolsApiTests`、`McpParityTests`、`McpToolArchitectureTests`。
- [ ] **Step 5: Commit**：拆兩個——`feat(mcp): create_record、update_record`、`test(mcp): 記錄寫入的 REST／MCP 對照`。

### Task 5: 寫入端「不提供給 AI」

**開工條件**：D1 已裁決（不能寫）；#74／#75 已合併 main，開工前確認分支已 rebase 到含 `c084787a` 的 main。

**Files:**
- Modify: 預期只有 `McpService/Tools/Write/WriteTool.cs`（若 `ignoredFields` 要調整）；**不改**權限解析的四個檔與 `McpAiExclusionTests.cs`
- Test: `WebAPITests/Dynamic/Mcp/McpAiWriteMaskTests.cs`（`Category=Postgres`）

- [ ] **Step 1: 寫失敗的測試**（欄位標 `ExcludeFromAi` 的方式沿用 `McpToolsApiTests.AiExcluded_NotInMcp`；預設用一般使用者）
  - `Update_MaskedField_KeepsOldValue`：MCP 送被遮欄位的新值 → 資料庫的值不變；`meta.ignoredFields` 有它；歷程沒有它的變更。
  - `Create_MaskedField_DefaultOnly`：被遮欄位有預設值 → 存成預設值；沒有 → 空。
  - `Create_MaskedRequired_NotBlocked`（D2）：被遮欄位必填、沒預設 → 建立成功、該欄位空。
  - `IgnoredFields_NoExistenceLeak`：送被遮欄位的 key 與一個不存在的 key → 兩者在 `ignoredFields` 的呈現完全相同（同一個陣列、沒有原因欄）。
  - `MaskedValue_NotInAudit`：送了被遮欄位的值 → 稽核摘要沒有這個值（判斷 15）。
  - `Update_MaskedDetailField_KeepsOldValue`（複審 N3）：明細子模組的某個欄位標「不提供給 AI」，`update_record` 修改既有明細列時送它的新值 → 資料庫的值不變、歷程沒有這筆變更、`meta.ignoredFields` 不列它（`ignoredFields` 只涵蓋表頭，判斷 7）。
  - `Create_MaskedDetailField_DefaultOnly`（複審 N3）：`create_record` 新增明細列時送被遮明細欄位的值 → 有預設值就存預設值、沒有就空。
  - `Response_HidesMaskedAndDependents`：寫入後的回應 `data` 不含被遮欄位（表頭與明細列都不含）；引用它的公式、彙總一併不出現。
  - `Response_HidesMaskedAndDependents_SystemAdmin`：同上，用 MCP 通道的系統管理員 → 一樣看不到（`RollupSourceMasks` 的系統管理員短路是 `IsSystemAdmin && !AiMasked`，只對畫面通道生效）；同一位管理員經 REST 看得到。
  - `SameUser_Ui_CanWrite`：同一使用者經 REST 寫同一欄位 → 有寫進去（畫面通道不受影響）。
  - `Approval_Submit_MaskedTitleTemplate`：模組標題樣板含被遮欄位，MCP 送簽成功；get_record_approval 的標題快照照 mcp1a Q7 是空字串（送簽工具在 Task 7，這一條在 Task 7 完成後補跑，先標 `Skip` 並寫原因）。
- [ ] **Step 2: 跑測試確認失敗**：`cd backend && dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~McpAiWriteMaskTests"`。若一開始就全部通過（沿用既有語意本來就成立），在交棒說明寫明，仍保留測試當回歸。
- [ ] **Step 3: 實作**：只在測試失敗的地方補；需要改權限解析就停下回報（違反「與 #74／#75 的衝突點」的前提）。明細走 `DetailWriter` → `guard.AuthorizeDetailAsync`，`ChannelPermissionRepository` 對每個模組都標 `AiMasked`，預期不必改程式。
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；加跑 `McpAiExclusionTests`、`McpAiExclusionReportTests`、`ChannelPermissionDecorationTests`、`McpParityTests`。PostgreSQL：`FORMWRIGHT_TEST_DB=postgres dotnet test tests/DynamicModules.WebAPITests --filter "Category=Postgres&FullyQualifiedName~McpAiWriteMaskTests"`。
- [ ] **Step 5: Commit**（`test(mcp): 寫入端「不提供給 AI」欄位的語意`）

**驗收（mcp2a-2 交棒）**：Task 4–5 的 Step 4 全過；PostgreSQL 執行數與 SQLite 相同。

# mcp2a-3 任務與簽核

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
    - `UpdateTask_ParentDone_SubtasksOpen_Conflict_ThenCascade`：回 `conflict`、`fields.code=subtasksOpen`、`params.count`；帶 `cascade: complete` 重送成功（換一個 key）。
    - `UpdateTask_ClearDueDate_Null`。
    - `UpdateTask_StatusEnum_Invalid`。
    - `UpdateTask_TitleArg_NotApplied`：參數多帶 `title`、`description` → schema 擋下多餘參數（`additionalProperties: false`）或忽略，任務標題與描述不變。
    - `UpdateTask_SupervisorHidden`：主管派給看不到的人 → `data.hiddenAfterWrite` true、沒有 `undoToken`。
    - `UpdateTask_SupervisorHidden_Replay_NoUndoToken`：同 key 重送 → 回放結果同樣沒有 `undoToken`（文字內容與結構化內容都搜不到 token 字串）。
    - `UpdateTask_SupervisorOtherField_Forbidden`：只能改負責人的主管送了到期日 → `forbidden`（一律訊息）。
    - `AddComment_EmptyBody_Invalid`、`AddComment_TooLong_Invalid`（上限取 `TaskCommentRules.BodyMax`）。
    - `AddComment_InvisibleTask_NotFound`。
    - `EmptySystem`（鐵則 4）：`new TestApp(seedDemoPeople: false)` 的 admin → `create_task`（個人）、`update_task`、`add_task_comment` 成功。
- [ ] **Step 2: 跑測試確認失敗**：`cd backend && dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~McpWriteParityTests|FullyQualifiedName~McpWriteToolsApiTests"`
- [ ] **Step 3: 實作**（判斷 2、11、16）。
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；加跑 `TasksApiTests`、`SubtasksApiTests`。PostgreSQL：`FORMWRIGHT_TEST_DB=postgres dotnet test tests/DynamicModules.WebAPITests --filter "Category=Postgres&(FullyQualifiedName~McpWriteParityTests|FullyQualifiedName~McpWriteToolsApiTests)"`。
- [ ] **Step 5: Commit**（`feat(mcp): 任務與留言的寫入工具`）

### Task 7: submit_approval、withdraw_approval

**Files:**
- Create: `McpService/Tools/Write/ApprovalWriteTools.cs`
- Modify: `McpService/McpTexts.cs`、`Application/DependencyInjection.cs`、`WebAPITests/Dynamic/Mcp/McpTransportTests.cs`（`business.write` 權杖的 `tools/list` 是 19 個）、`McpAiWriteMaskTests.cs`（拿掉 `Approval_Submit_MaskedTitleTemplate` 的 `Skip`）
- Test: `WebAPITests/Dynamic/Mcp/{McpWriteToolsApiTests,McpWriteParityTests}.cs`

- [ ] **Step 1: 寫失敗的測試**（造資料沿用 `ApprovalApiTests` 的 `NewRecord`、`Submitted`）
  - `McpWriteParityTests`：
    - `Submit_SameAsRest`：兩邊送簽後的實例、關卡、簽核人、簽核歷程、通知、記錄新 RowVersion 的行為相同。
    - `Withdraw_SameAsRest`。
  - `McpWriteToolsApiTests`：
    - `Submit_PreviewOnly_NoWrite`：回 `data.preview`（含 `picks` 候選）、沒有實例；稽核一列 `ReasonCode=preview`。
    - `Submit_NoPreviewInResult`：沒帶 `previewOnly` → `data` 有 `outcome`、`instanceId`、`recordRowVersion`，沒有 `preview`。
    - `Submit_MissingPicks_Invalid_ThenSucceeds`：缺自選 → `invalid`（`fields["picks.{code}"]`）；`previewOnly` 看候選、補上重送成功（換一個 key）。
    - `Submit_StaleRecord`：記錄 rowVersion 舊 → `stale`。
    - `Submit_AlreadyPending_Conflict`：換一個 key 再送 → `conflict`。
    - `Submit_Direct_Outcome`：沒有符合流程且設定為直接轉移 → `outcome=direct`。
    - `Submit_IdempotentResend`：同 key 重送 → 一個實例、`meta.replayed` true（不是 `conflict`）。
    - `Submit_Direct_IdempotentResend`：直接轉移後同 key 重送 → 回放 `outcome=direct`。
    - `Withdraw_NotInitiator_Conflict`、`Withdraw_StageHandled_Conflict`。
    - `Withdraw_StaleInstanceVersion_Conflict`：用舊的實例 rowVersion → `conflict`（不是 `stale`，與工具描述一致）。
    - `Submit_RuntimeInvalid_AlertWritten_OneInvalidRow`（複審 N2）：流程設定讓引擎在送簽時觸發執行期錯誤（沿用 `ApprovalApiTests` 或 `ApprovalAlert` 既有測試的造法）→ MCP 回錯誤；系統管理員收到簽核警示（與畫面通道同一案例的結果相同）；稽核恰一列 `Invalid`、沒有 `Success` 列；Error log 沒有「第二個寫入請求」的例外。
- [ ] **Step 2: 跑測試確認失敗**：`cd backend && dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~McpWriteParityTests|FullyQualifiedName~McpWriteToolsApiTests|FullyQualifiedName~McpTransportTests|FullyQualifiedName~McpAiWriteMaskTests"`
- [ ] **Step 3: 實作**（判斷 9）。
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；加跑 `ApprovalApiTests`。PostgreSQL：`FORMWRIGHT_TEST_DB=postgres dotnet test tests/DynamicModules.WebAPITests --filter "Category=Postgres&FullyQualifiedName~McpWrite"`。
- [ ] **Step 5: Commit**（`feat(mcp): 送簽與撤回工具`）

**驗收（mcp2a-3 交棒）**：Task 6–7 的 Step 4 全過；PostgreSQL 執行數與 SQLite 相同。

# mcp2a-4 迴圈與收尾

### Task 8: 寫入迴圈偵測、政策與事件

**Files:**
- Create: `McpService/WriteLoop/RecordMcpWriteLoopIncident.cs`
- Modify: `Domain/Policies/PolicyCatalog.cs`、`shared/policy-catalog.json`、四語 `ui.json`（`policies.items.ops.ruleLoopTriggersPerRecord`、`ruleLoopWindowMinutes`、`ops.title.mcpWriteLoop`）、`Domain/Repositories/Dynamic/IAuditLogRepository.cs`、`Persistence/Repositories/Dynamic/AuditLogRepository.cs`、`Dynamic/OpsService/Incidents/IncidentTitles.cs`、`McpService/{McpUsage,McpMessages,McpPolicies,McpTexts}.cs`、`McpService/Tools/McpToolInvoker.cs`、`ServerTextBaseline/mcp.json`
- Modify（測試假實作，判斷 14）：`WebAPITests/Ops/AuditApiTests.cs`（`ThrowingRepository`、`CountingPurge`）、`ApplicationTests/Common/Audit/AuditLogTests.cs`（`ThrowingRepository`）
- Test: `ApplicationTests/Dynamic/Mcp/McpWriteLoopTests.cs`（`Category=Postgres`）、`ApplicationTests/Persistence/AuditLogWriteCountTests.cs`（`Category=Postgres`）、`DomainTests/Policies/PolicyCatalogFixtureTests.cs`（跑，不改）

- [ ] **Step 1: 寫失敗的測試**
  - `AuditLogWriteCountTests`：`CountWritesForTargetSinceAsync` 只算指定權杖、目標、動作、時間之後、`Success` 且 `ReasonCode` 為 null 的列【雙跑】。
  - `McpWriteLoopTests`（`ruleLoopTriggersPerRecord` 設 5、`ruleLoopWindowMinutes` 設 10，用 `TimeProvider` 控時間）：
    - `FifthWrite_Allowed_SixthBlocked`：同權杖改同一筆 5 次成功，第 6 次 `write_loop`、記錄沒變。
    - `Blocked_ExactlyOneDeniedRow_NoSuccessRow`：被擋的那次呼叫在稽核表恰一列：`Denied`、`ReasonCode=write_loop`、`TargetKind=record`、`TargetId`、`TargetLabel` 是工具名；沒有任何 `Success` 列；`IMcpWriteScope.IsCommitted` 是 false。
    - `OtherRecord_NotBlocked`、`OtherToken_NotBlocked`。
    - `Replays_NotCounted`：同 key 回放 10 次 → 不觸發。
    - `ResendAfterBlock_Replays`：第 5 次成功的那筆（key K5）在已到門檻時用 K5 重送 → 回放成功、`meta.replayed` true，不是 `write_loop`。
    - `Failures_NotCounted`：`stale` 失敗 10 次 → 不觸發。
    - `PreviewOnly_NotCounted`：`submit_approval` 的 `previewOnly` 10 次 → 不觸發。
    - `Blocked_NotCounted_RecoversAfterWindow`：被擋之後時間前進到最早那筆滑出時間窗 → 下一次成功。
    - `SubmitApproval_Counts`：`submit_approval` 與 `update_record` 對同一筆合併計數。
    - `CreateRecord_NotChecked`。
    - `Incident_Opened_Deduped`：第一次被擋開一張事件（`Source=Config`、嚴重度 Warning、標題鍵 `ops.title.mcpWriteLoop`、參數只有權杖 id、記錄 id、次數、分鐘數），再擋一次同一張事件次數加一、不開第二張、不再通知；收件人照 `ops.configIssueRecipients`。
    - `Incident_TitleParams_NoUserText`：權杖名稱設成特殊字樣 → 事件的標題參數與快照文字都不含它。
    - `IncidentWriteFails_StillWriteLoop`：事件請求丟例外 → 回應仍是 `write_loop`、被擋那列照寫、Error log 一筆。
    - `PolicyChange_TakesEffectNextCall`。
    - `BlockedCall_CountsTowardDailyQuota`（D8）。
  - `PolicyCatalogFixtureTests`：兩項新政策在 catalog、json、四語都一致。
- [ ] **Step 2: 跑測試確認失敗**：`cd backend && dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~McpWriteLoopTests|FullyQualifiedName~AuditLogWriteCountTests|FullyQualifiedName~AuditLogTests"`；`dotnet test tests/DynamicModules.DomainTests --filter "FullyQualifiedName~PolicyCatalogFixtureTests"`
- [ ] **Step 3: 實作**（判斷 8、13）。政策描述的文案寫白話：「同一把 AI 權杖在時間窗內修改同一筆記錄幾次，就暫停它對這筆的寫入」「計算上一項次數的時間窗（分鐘）」。
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；加跑 `AuditApiTests`、前端政策守衛測試、`McpToolInvokerTests`、`McpWriteAuditTests`、`RulePause` 相關測試。PostgreSQL：`FORMWRIGHT_TEST_DB=postgres dotnet test tests/DynamicModules.ApplicationTests --filter "Category=Postgres&(FullyQualifiedName~McpWriteLoopTests|FullyQualifiedName~AuditLogWriteCountTests)"`。
- [ ] **Step 5: Commit**：超過 700 行就拆兩個——`feat(mcp): 寫入迴圈偵測與政策`、`feat(mcp): 寫入迴圈的維運事件`。

### Task 9: 文件、走查、gate、實機驗收

**Files:**
- Modify: `docs/superpowers/specs/2026-11-11-mcp-server-design.md`：
  - §5.5 已知限制第三點：照 D1、D2 寫寫入端語意。
  - §6.2：寫入工具 `idempotentHint` 的條件改成「key 必填、一律標」。
  - §6.5：表格加 `conflict`、`idempotency_key_reused`、`write_loop`。
  - §7.2 mcp2a：工具參數照判斷 2；`submit_approval` 照判斷 9（預覽是 `previewOnly` 那一次呼叫）；`mark_notifications_read` 照 D6（票 #82）；冪等照判斷 5（任務與留言也納入、附件留言放行）。
  - §8.1：寫入稽核改成 `McpWriteAuditBehavior`（判斷 3）；摘要去值規則（判斷 15）；「operation id 可串到事件事實與記錄歷程」改成「可串到事件事實」，記錄歷程隨票 #81（與設計的矛盾第 9 點）。
  - §12 mcp2a 列：加「mcp2a 計畫」連結。
  - §14：加 `ops.ruleLoopTriggersPerRecord`、`ops.ruleLoopWindowMinutes`（沿用其他文件的政策區）。
  - §15：本計畫的寫死清單。
  - §20 C3：補「mcp2a 已實作，處理方式見計畫 D4」。
  - D 若被使用者推翻，照裁決改；D1、D7 標使用者裁決，其餘照當時狀態標「顧問拍板，待使用者確認」或使用者裁決。
- Modify: `docs/superpowers/specs/2026-11-11-ops-observability-design.md` 政策表：`ops.ruleLoopTriggersPerRecord` 段別標已實作；加時間窗那一列（D5）。
- Modify: `docs/manual.md` §24：
  - 寫入範圍與預設不勾、7 個工具能做什麼、為什麼建立權杖時要謹慎勾寫入。
  - 不提供給 AI 的欄位在寫入時被忽略；有這種必填欄位時 AI 建的記錄要有人補填（D1、D2）。
  - 明細請在同一次修改一起送，逐列修改容易碰到迴圈門檻（D5）。
  - 迴圈暫停與自動恢復，以及鋸齒效應：時間窗一滑動就放行一次，迴圈會以「門檻／時間窗」的速度繼續寫，事件只累加次數、不再通知，管理員看到事件要去「AI 連線」頁撤銷權杖（D4）。
  - 失敗的反覆嘗試不算迴圈，靠每分鐘速率與每日名額擋。
  - 要查 AI 改了什麼到稽核頁（D7）。
- Modify: `docs/roadmap.md` mcp2a 列：本段改「進行中」，合併後由主控補 SHA；`docs/feature.md` 的 mcp2a 項目照裁決更新（`mark_notifications_read` 不做，附票 #82），跑 `feature_checklist.py docs/feature.md --write`，找不到腳本就回報「統計行待重算」。

- [ ] **Step 1: 整段驗收 grep**（S1–S6）全符合。
- [ ] **Step 2: 文件回填**（上列）。
- [ ] **Step 3: 票**：#81（歷程標示經 AI）、#82（列出通知與標已讀）已開，票號寫進設計與交付說明，不另開。
- [ ] **Step 4: 受影響畫面走查**：用 `formwright-demo-verify` 在本機 Demo（9180）以 390、820、1366、1920 四種寬度看：個人「AI 存取權杖」頁的建立對話框（兩個範圍勾選框、預設狀態、四語標籤不溢出）、政策頁「維運」分類的兩項新政策、稽核頁含 `mcp.*` 寫入動作的列表與篩選、維運事件頁的「AI 寫入迴圈」事件。不得橫向捲動。
- [ ] **Step 5: 實機驗收**（下一節）逐項做，紀錄放專案 `.tmp/<日期>-mcp2a/`。
- [ ] **Step 6: gate**：推分支前外洩掃描；`bash scripts/unattended/ci-wait.sh <sha>`（含 PostgreSQL 兩步），`exit=0`。
- [ ] **Step 7: `formwright-purpose-check`**。重點：迴圈政策單一來源；工具通用、不依賴應用程式；空系統可用；寫入權限全在既有 guard；稽核 fail closed 且不存值；沒有執行期 DDL。
- [ ] **Step 8: Commit**（`docs(mcp): mcp2a 完成，設計、手冊與功能清單回填`），之後照 `formwright-ship` fast-forward 推 main。

**驗收**：S1–S6 全符合；gate `exit=0`；實機驗收 1–9 全部有紀錄；四種寬度走查無橫向捲動。Demo 資料：**種子不改**（Demo 站開著 `Demo:LoginEnabled`，MCP 依設計整個停用）；交付說明寫「Demo 站無可示範（設計 §5.1），本機以實機驗收示範成功路徑與被擋、`stale`、迴圈暫停」。

---

## 整段驗收

設計 §12 mcp2a 列逐項：

| # | 項目 | Task | 驗證 |
|---|---|---|---|
| 1 | `business.write` 可建立、寫入範圍預設不勾 | 1 | `McpScopesTests`、`McpTokenApiTests`、`AiTokensPage.vitest.spec.ts` |
| 2 | 寫入稽核與業務同交易、提交後不寫第二列、稽核寫不進就回滾、沒上膛的寫入丟例外、其他交易型請求放行（簽核警示照常） | 2、7 | `McpWriteAuditTests`、`Submit_RuntimeInvalid_AlertWritten_OneInvalidRow` |
| 3 | 稽核摘要不存寫入的值 | 2、5 | `McpAuditSummaryTests`、`McpWriteToolsApiTests.CreateRecord_Audit`、`McpAiWriteMaskTests.MaskedValue_NotInAudit` |
| 4 | 錯誤代碼（`stale`、`conflict`、`idempotency_key_reused`）與 annotations | 2、3 | `McpToolInvokerTests`、`McpTransportTests` |
| 5 | 重送同一冪等鍵不重複寫（記錄、任務、留言、送簽；並行；跨權杖；回放結果相同；附件留言不壞） | 3、4、7 | `McpIdempotencyTests`、`WriteJournalAndIdempotencyTests`、`IdempotencyApiTests`、`McpWriteToolsApiTests.*Resend*`、`*Replay*` |
| 6 | MCP 建立／修改的記錄與畫面建立的完全相同（值、明細、歷程、事件事實、檢查、任務規則） | 4 | `McpWriteParityTests.Create_*`、`Update_*` |
| 7 | 白名單組請求（`SystemKeys`、`_delete`、任務標題） | 4、6 | `UpdateRecord_SystemKeysArg_Ignored`、`UpdateRecord_DetailDelete_InvalidArguments`、`UpdateTask_TitleArg_NotApplied` |
| 8 | 任務、留言、送簽、撤回與畫面相同（系統事件、參與者、通知、簽核歷程）；回放不給 `undoToken` | 6、7 | `McpWriteParityTests`、`UpdateTask_SupervisorHidden_Replay_NoUndoToken` |
| 9 | 寫入端「不提供給 AI」 | 5 | `McpAiWriteMaskTests` |
| 10 | 寫入迴圈偵測（C3）與事件 | 8 | `McpWriteLoopTests` |
| 11 | 政策單一來源 | 8 | `PolicyCatalogFixtureTests`、前端政策守衛 |
| 12 | 空系統 | 6 | `McpWriteToolsApiTests.EmptySystem` |
| 13 | 架構：工具只依賴 `ISender`、寫入請求不是稽核或確認請求 | 2 | `McpToolArchitectureTests` |
| 14 | 實機連線寫入（Claude Code） | 9 | 「實機驗收」 |
| 15 | 受影響畫面四種寬度 | 9 | Task 9 Step 4 |

```bash
# S1 沒有 migration
git diff --name-only main...HEAD -- 'backend/src/*/Migrations/*'   # 無結果
# S2 寫入工具與執行器不碰 DbContext 與 repository（mcp1a 的 GetMcpWhoAmI.cs 本來就注入 repository，不在掃描範圍）
grep -rn "DynamicDbContext\|Repository" backend/src/DynamicModules.Application/Dynamic/McpService/Tools/Write backend/src/DynamicModules.Application/Dynamic/McpService/Tools/McpToolInvoker.cs   # 無結果
# S3 迴圈門檻不寫死
grep -rn "ruleLoop" backend/src --include='*.cs' | grep -v "PolicyCatalog\|McpPolicies"   # 只有讀政策的地方，沒有數字
# S4 權限解析沒動（#74／#75 的檔）
git diff --name-only main...HEAD -- backend/src/DynamicModules.Application/Common/Security/   # 無結果
# S5 寫入工具只送既有 request
grep -rn "new .*Request(" backend/src/DynamicModules.Application/Dynamic/McpService/Tools/Write | grep -v "CreateRecordRequest\|UpdateRecordRequest\|GetRecordRequest\|GetModuleSchemaRequest\|CreateTaskRequest\|CreateRecordTaskRequest\|CreateSubtaskRequest\|UpdateTaskRequest\|AddTaskCommentRequest\|PreviewRecordApprovalRequest\|SubmitApprovalRequest\|WithdrawApprovalRequest"   # 無結果
# S6 白名單組請求：不碰 SystemKeys、不把參數反序列化成 request（values 的解析在 McpArgs，不在掃描範圍）
grep -rnE "SystemKeys|Deserialize<\w*Request" backend/src/DynamicModules.Application/Dynamic/McpService/Tools/Write   # 無結果
```

## 實機驗收（Claude Code 連本機）

1. **啟動**：`DEMO_LOGIN=false bash scripts/unattended/local-demo.sh <worktree 絕對路徑>`（背景執行，等 `READY`）；MCP 在 5180。
2. **準備**：照 mcp1a「實機驗收」第 2 步建一個非示範的虛構帳號、開 `mcp.enabled`；在個人頁建權杖時確認寫入範圍預設不勾，勾上後建立（截圖）。
3. **建立**：Claude Code 問「在某模組新增一筆記錄，名稱為…」→ 畫面上看到這筆記錄，歷程記在該帳號名下；觸發的事件效果與畫面建立的一樣。
4. **修改**：「把剛才那筆的狀態改成…」→ 成功；再用畫面改一次同一筆，回 Claude Code 叫它用舊資料再改 → 回 `stale`，模型重讀後再改成功。
5. **任務與留言**：「替這筆記錄建一個任務指派給某人，明天到期，並留言 @某人」→ 被提及的人收到通知。
6. **送簽與撤回**：「先看看這筆送簽會經過哪些關卡」（`previewOnly`）→ 看到預覽；「送簽」→ 成功；「撤回」→ 成功。
7. **重送**：用 curl 對 `/mcp` 送兩次同一個 `create_record`（同 `idempotencyKey`）→ 只有一筆，第二次 `meta.replayed` true；稽核摘要只有欄位鍵、沒有值。
8. **迴圈**：把 `ops.ruleLoopTriggersPerRecord` 調到 5，用 curl 連續改同一筆 6 次（每次換 key）→ 第 6 次 `write_loop`；維運事件頁看到一張「AI 寫入迴圈」事件；用第 5 次的 key 重送 → 回放；時間窗過後恢復。
9. **稽核**：admin 的稽核頁用「來源管道＝MCP」查到上述寫入動作，帶權杖 id；被擋的那次是「被拒」、目標是那筆記錄。

Codex 的寫入不另做（工具與 Claude Code 共用同一個 endpoint，mcp1a 已驗過 Codex 連線）。

## 預估規模

| 批 | Task | 新檔 | 改檔 | 手寫行數（估，含測試） |
|---|---|---|---|---|
| mcp2a-1 | 1 範圍與對話框 | 0 | 9 | ≈ 180（測試約 90；文案約 10） |
| | 2 冪等標記、寫入稽核、摘要去值、錯誤對照、annotations | 7（含 2 個測試檔） | 13 | ≈ 830（測試約 500） |
| | 3 冪等鍵 | 1（測試檔） | 8 | ≈ 480（測試約 300） |
| | 小計 | 8 | 30 | ≈ 1490 |
| mcp2a-2 | 4 記錄工具與對照 | 4（含 2 個測試檔） | 2 | ≈ 790（測試約 490） |
| | 5 寫入端遮罩 | 1（測試檔） | 0–1 | ≈ 330（測試約 310） |
| | 小計 | 5 | 3 | ≈ 1120 |
| mcp2a-3 | 6 任務與留言工具 | 1 | 2 | ≈ 560（測試約 340） |
| | 7 簽核工具 | 1 | 4 | ≈ 550（測試約 360） |
| | 小計 | 2 | 6 | ≈ 1110 |
| mcp2a-4 | 8 迴圈、政策與事件 | 3（含 2 個測試檔） | 14 | ≈ 790（測試約 400；文案約 30） |
| | 9 文件與收尾 | 0 | 6 | ≈ 220 |
| | 小計 | 3 | 20 | ≈ 1010 |
| 合計 | | 約 18 | 約 59 | ≈ 4730，其中測試約 58% |

- 第二輪比初版多約 750 行：摘要去值（+140）、附件留言放行與測試（+60）、白名單與 `undoToken` 測試（+110）、`update_record` 前置讀取與回放（+90）、送簽重送（+90）、迴圈事件請求、標題與假實作（+230）、文件（+50）。
- 第三輪再多約 180 行：未列鍵與非物件的摘要規則（+50）、behavior 只接手 `IIdempotentRequest` 與 `AuditReason`（+60）、明細遮罩案例（+50）、冪等雜湊通道（+20）。mcp2a-1 約 1490，貼近上限；若 Task 2 實作超過 850 行，把 annotations 與 `McpTransportTests` 移到 Task 3 開頭。
- 單一 Task 超過 700 行就拆 commit：Task 2、4、8 照各自 Step 5。

## 已知限制（mcp2a，交付時寫回設計）

- `ignoredFields` 只涵蓋表頭、只看 schema：明細欄位、規則唯讀與狀態機唯讀造成的忽略不會列出，要比對回傳的記錄。
- `forbidden` 一律同一句，主管只能改負責人這類細訊息不給 AI。
- `update_task` 沒有版本比對，兩邊同時改時後者蓋前者（與畫面相同）；標題與描述不開放。
- 撤回時實例版本過期回 `conflict`（`StateChanged`／`StageHandled`），不是 `stale`；工具描述提醒先重讀。
- 迴圈偵測只看記錄、只看權杖；任務與撤回的反覆寫入只靠每分鐘速率；整合等其他來源上線時再擴充。
- 迴圈只計成功的寫入：失敗的反覆嘗試（例如一直被檢查擋下）不觸發，靠每分鐘速率與每日名額擋。
- 迴圈的鋸齒效應：時間窗一滑動就放行一次，迴圈會以「門檻／時間窗」的速度繼續寫，事件只累加次數、不再通知，直到每日名額用完（D4 自動恢復的取捨，手冊寫明）。
- 迴圈計數在交易外判斷，並行的兩次寫入可能讓次數多一次才擋（門檻型偵測可接受）。
- 冪等紀錄保留 `maintenance.idempotencyRetentionDays`（出廠 7 天），過了之後同 key 重送會再執行一次；附件留言沒有請求層冪等（MCP 不收附件，不受影響）。
- 檢查規則若引用被遮欄位，AI 可以藉「改別的欄位後有沒有被擋」試探被遮欄位的值；更直接的是，檢查訊息裡的 `{value}`、`{limit}` 會換成彙總值（`CheckEvaluator`），若彙總的正好是被遮欄位，AI 在 `conflict` 訊息裡直接拿到數字。畫面上看不到隱藏欄位的使用者也是同樣語意，本段不擋；主控另開票處理「MCP 通道上彙總欄位被遮時 `{value}` 顯示為『…』」。
- 被遮的必填欄位在 AI 建立時不檢查（D2）。
- 歷程不標「經 AI」（D7，票 #81），要到稽核頁查。
- 留言不收附件；不做 `mark_notifications_read`（D6，票 #82）。
- mcp2b 的伏筆：`McpWriteAuditBehavior` 遇到 `IMcpConfirmedRequest` 會丟例外。C7 允許管理員把刪除移出高風險清單、改成一般寫入，到時同一個請求可能要走「一般寫入」這條路；mcp2b 要決定是讓確認請求在「不要求確認」時改走寫入稽核，還是另做一個不帶確認的請求。

## 宗旨檢查（formwright-purpose-check）

1. **機制還是政策**：通過。工具、冪等、稽核、迴圈偵測是平台機制；迴圈的次數與時間窗是政策；寫死的只有安全、完整性、技術限制，已列清單。
2. **寫死掃描**：計畫階段不適用；Task 9 Step 7 對 diff 跑 skill 的 grep，預期命中 `McpToolCatalog`、`McpScopes`（平台功能清單，已列）。
3. **單一來源**：通過。迴圈門檻只在 `PolicyCatalog`；預設勾選只在 `McpScopes.DefaultChecked`、前端從 API 取；長度上限沿用既有 request 常數；寫入權限只在既有 guard。
4. **兩個應用程式**：通過。工具通用，靠 describe_module 的 schema；CRM 的客戶、進銷存的訂單都用同一組 create_record／update_record；對照測試用虛構模組。
5. **空系統**：通過。沒開任何應用程式時任務與留言工具照常可用（`EmptySystem`）。
6. **套件只用定義組成**：不適用。本段不碰套件。
7. **ERP 對照**：本段沒有新增主檔或單據。對照設計 §11：Odoo 的外部 API 以 API key 代替使用者執行 create／write，權限等同使用者；Formwright 同樣等同使用者，另多了範圍（寫入要另勾）、冪等鍵、逐筆稽核與迴圈偵測。
8. **權限 fail closed、驗證不只在前端、沒有執行期 DDL**：通過。寫入全經既有 guard 與後端驗證；稽核寫不進去就回滾；一次呼叫兩個寫入請求、或沒上膛的寫入請求就丟例外；沒有 migration。
9. **不限產業**：通過。例子用記錄、任務、簽核，與產業無關。

## 自我檢查

1. **設計覆蓋**：§5.2 → 判斷 6、Task 1；§5.5 第三點 → D1、D2、Task 5；§6.2 → 判斷 10；§6.5 → 判斷 4；§7.2 mcp2a → 判斷 1、2、5、9、D6；§8.1 → 判斷 3、15；§8.2 → 判斷 12；§12 mcp2a 列 → 「整段驗收」；§13.5 建立對話框 → Task 1；C3 → 判斷 8、D4、D5。
2. **使用者裁決**：寫入範圍預設不勾 → 判斷 6；被擋、失敗、取消計入每日名額 → 判斷 12；畫面通道也補遮罩（#74、#75）→ 「與 #74／#75 的衝突點」；D1 不能寫 → 判斷 7、Task 5；D7 不標 → 票 #81。
3. **審查第二輪與複審**：複審 N1 → 判斷 15、Task 2；N2 → 判斷 3、Task 2、Task 7；N3 → Task 5；複審建議 1 → 前置條件 3、衝突點、Task 5；2 → 判斷 3、Task 2 Files；3 → 判斷 3、9；4 → 判斷 16；5 → 已知限制；6 → 判斷 8；7 → 判斷 5、Task 3。第二輪：必修 1 → 判斷 15；必修 2 → 判斷 5；必修 3 → 判斷 9；必修 4 → 判斷 7；必修 5 → 判斷 3、8；必修 6 → 判斷 16、S6；必修 7 → 判斷 11；必修 8 → 判斷 14；必修 9 → S2；必修 10 → 前置條件 3、Task 5。建議 1 → 判斷 3；2、3 → D4、已知限制、手冊；4 → 判斷 8；5 → 判斷 8；6 → 判斷 7、已知限制；7 → 判斷 2、已知限制；8 → 子段切法；9 → 已知限制；10 → 衝突點表。
4. **型別一致**：`McpScopes.{Issuable,DefaultChecked}`、`McpInfoResponse.DefaultScopes`、`McpToolDescriptor.LoopTargetArg`、`IMcpWriteScope.{Arm,IsCommitted,IsArmed}`、`McpWriteAudit`、`McpWriteAuditBehavior`、`McpWriteTargets`、`IIdempotentRequest.IdempotencyApplies`、`McpToolOutcome.AuditReason`、`McpErrorCodes.{Conflict,IdempotencyKeyReused,WriteLoop}`、`IAuditLogRepository.CountWritesForTargetSinceAsync`、`McpUsage.HasOperationAsync`、`RecordMcpWriteLoopIncidentRequest`、`IncidentTitles.WriteLoop`、`PolicyCatalog.{OpsRuleLoopTriggersPerRecord,OpsRuleLoopWindowMinutes}`。
5. **前段踩過的坑**：
   - 回放的 `Data` 是 `JsonElement`，工具不轉型；`undoToken` 兩種形狀都要拿掉（判斷 5、11）。
   - 稽核只能一列：寫入、確認、`IAuditedRequest` 三種互斥；迴圈事件在上膛之前；behavior 只接手 `IIdempotentRequest`，簽核警示等交易型請求放行（判斷 3、8）。
   - `WriteNowAsync` 不能在交易內呼叫：失敗列仍由執行器在交易外寫；`IncidentRecorder` 只能在交易內，所以事件另開 scope 送交易型請求。
   - 新測試類別進 PostgreSQL 雙跑集；依可空欄位排序寫明 NULL 位置。
   - 新訊息進 `mcp.json` 基準檔；四語文案同步並跑前端守衛。
   - `local-demo.sh` 給絕對路徑；實機驗收關 Demo 登入。

## 使用者裁決（沿用，不再問）

- 2026-11-11、11-12：設計 §3、§20 C1–C7（C3：迴圈偵測延到 mcp2a，對象是同一權杖或來源反覆寫同一筆，政策 `ops.ruleLoopTriggersPerRecord`）；U12（送簽、撤回、任務與留言、建立與修改記錄是一般寫入，簽核同意與退回是高風險）。
- 2026-11-20：業務政策不寫死、權限 fail closed、驗證不只做在前端；超過上限的訊息帶上限數字；沒指定語系用預設語系；系統自記的時間照營運時區。
- 2026-11-21：建立權杖的範圍預設唯讀全勾、寫入範圍不勾；被擋、失敗、取消都計入每日名額（D8）；「不提供給 AI」的連帶遮罩畫面通道也補（#74、#75，另一條線實作）；D1 被遮欄位不能寫（當看不到）；D7 歷程這段先不標（票 #81）。
