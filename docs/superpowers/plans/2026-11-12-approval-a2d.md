# 簽核 a2d：簽核鎖定、定義守衛、附件、簽核類政策 Implementation Plan

> **⛔ 阻塞中：等 pg0 合併。** roadmap 的順序是 O1 → un1 → pg0a → pg0，2026-11-12 四段都是「未開始」，main 上只有 pg0 的設計文件。pg0 合併前不開工，也不要只驗 SQLite 就往下做。開工第一步是 Task 0（rebase、重新核對行號與型別）。

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 記錄進入簽核鎖定狀態後，鎖定欄位（含計算欄位）與附件不能再被任何寫入路徑改掉，只有解鎖轉移上的事件可以收尾；有進行中實例時，設計器不能改會改寫記錄值的定義，也不能拆掉實例要走的轉移與狀態；有記錄停在鎖定狀態時，不能整批解鎖，也不能刪掉鎖定欄位的值；整批重算跳過已核准的記錄，並回報保留了幾筆；簽核的六類寫死政策（A3、A4、A10、A11、A12、A15）改讀 p1 的政策讀值服務，出廠值等於現值。

**Architecture:** 鎖定只有一個判斷來源：新的 `ApprovalLockService` 依模組（可帶候選狀態機）算出「鎖定狀態、鎖定欄位（含附件）、明細列是否一起鎖」。寫入端有兩道。第一道在 `UpdateRecordHandler`：鎖定記錄的計算欄位保留舊值。第二道是寫入後盾 `ApprovalBackstopStage`（Order 9）。設計器顯示與 `FrozenRecords` 也用同一個來源。附件上傳改走記錄寫入管線（`IRecordWriteRequest`），所以同一道後盾也擋上傳；收檔前的資格檢查另外擋簽核中、鎖定與已記帳。設計器端有兩個守衛，與既有的 `ClosedDataGuard`、`ImmutableDataGuard` 掛在同一組檢查點：`PendingApprovalGuard` 管進行中實例，`LockedDataGuard` 管停在鎖定狀態的記錄。政策一律經 `IPolicyReader` 讀，出廠值與技術上下限只寫在 `PolicyCatalog`。

**Tech Stack:** .NET 8、EF Core（SQLite＋PostgreSQL，兩者都要驗）、MediatR、xUnit＋FluentAssertions；Vue 3、Quasar 2、TanStack Query、vue-i18n、vitest、bun。不新增相依套件。

**Spec:**
- `docs/superpowers/specs/2026-11-10-approval-workflow-design.md`：§6.1、§8.5、§8.6、§9.2 末段、§9.3、§12、§13.1、§20.2、§20.4、§21。
- `docs/superpowers/specs/2026-11-11-policy-settings-design.md`：§3.1、§3.3、§3.5、§4 的 A3／A4／A10／A11／A12／A15、§9、§10。
- `docs/superpowers/specs/2026-11-12-postgres-provider-design.md`：§5.6、§6.3、§6.4、§15（pg0 合併後以 main 上的版本為準）。
- repo 規範：`AGENTS.md`、`.claude/rules/backend.md`、`.claude/rules/frontend.md`、`.claude/rules/docs.md`、`docs/ai/testing.md`。

**路徑慣例：** 後端相對 `poc/backend/`，前端相對 `poc/frontend/`，文件與腳本相對 repo 根目錄。worktree：`../dynamic-fields-design.a2d`（分支 `feature/a2d`）。本計畫引用的行號以 `67828aa4` 為準，Task 0 會重新核對。

---

## 修訂紀錄（2026-11-12）

依 `plan-review.md`（必修 7、建議修 12、nit 14）與使用者裁決 1–11 修訂。逐條處置與理由見同資料夾的 `revise-result.md`；下表只列落點。

| 來源 | 處置 | 落點 |
|---|---|---|
| M1 前置條件不成立 | 採用 | 頂端阻塞註記、Task 0、Task 5／6 的 WebAPI 案例標 `Category=Postgres` |
| M2 鎖定記錄的計算欄位會被重算 | 採用 | 新 Task 2（第一道）、Task 3 演算法（第二道只豁免靜默項目）、Task 8 端到端測試、判斷 26 |
| M3 明細鎖誤擋彙總重算 | 採用 | Task 3 明細規則與兩個測試 |
| M4 附件改走管線的連帶效果 | 採用 | Task 5（孤兒檔清理）、Task 6（已記帳、分錄）、判斷 27 |
| M5 裁決 1 沒展開 | 採用 | 登記表、判斷 15、Task 17、18、22、26 |
| M6 Task 10 漏檢查點 | 採用 | Task 11（刪區塊、改選項名稱） |
| M7 Task 7／8 介面切法 | 採用 | Task 8 一併改 `FormulaRecalc`、`RollupRecalc` 與三個呼叫端，Task 9 只做筆數與畫面 |
| S1–S12 | 全部採用 | 見各 Task；S11 落在 Task 26 |
| nit 14 條 | 13 條採用，1 條不採用（`backend.md` 過時，不在 a2d 範圍） | 見各 Task |
| 裁決 1（主管層數，模組可覆寫） | 展開 | 登記表、判斷 15、Task 17、18、22、26 |
| 裁決 2–5 | 展開 | Task 19（2）、20（3）、3（4）、5／7（5） |
| 裁決 6（審查 Q1：已記帳擋附件） | 新步驟 | Task 6 |
| 裁決 7（審查 Q2：解鎖事件放行） | 新步驟 | Task 3、判斷 25 |
| 裁決 8（審查 Q3：整批解鎖 409） | 新 Task | Task 14 |
| 裁決 9（審查 Q4：固定名單用技術上限 50） | 新步驟 | Task 19、判斷 17 |
| 裁決 10（審查 Q5：刪欄位或區塊 409） | 新 Task | Task 14 |
| 裁決 11（審查 Q6：A15 改名） | 新步驟 | Task 17、26 |
| 結構 | 六段改七段：原 a2d4 拆成 a2d4（進行中實例守衛）與 a2d5（鎖定資料守衛、閉包重檢），原 a2d5、a2d6 改名 a2d6、a2d7；Task 從 23 個增為 27 個（Task 0–26） | 子段表 |

---

## 前置條件（開工前確認，不符就停下回報）

1. `git fetch && git log --oneline origin/main | head -40` 看得到 pg0 已合併：`scripts/db/add-migration.sh` 存在、`docs/ai/testing.md` 有 `FORMWRIGHT_TEST_DB=postgres` 一節、測試專案有 `[Trait("Category", "Postgres")]`。看不到就停下回報。
2. 本機 PostgreSQL 照 pg0 的 `docs/ai/testing.md` 啟動，下面的雙跑指令（下稱「PG 雙跑」）能跑。指令以合併後的 `docs/ai/testing.md` 為準；和下面不同時，用 testing.md 的版本，並在 Task 0 改掉本計畫所有出現處：
   ```bash
   cd poc/backend && FORMWRIGHT_TEST_DB=postgres dotnet test tests/DynamicModules.ApplicationTests \
     --filter "Category=Postgres&Category!=SqliteOnly&Category!=PgDeferred&Category!=Benchmark"
   ```
3. `../dynamic-fields-design.lists` 如果還在跑 gate，本段的 gate 等它跑完再跑（兩邊都吃 CPU）；窄範圍測試可以先跑。

---

## 範圍確認

列出設計裡屬於 a2d 的每一項：哪些已在 main（附證據）、哪些這次做。證據的 commit 都在 `origin/main`（`67828aa4` 為止）。

### 已在 main，不重做

| 設計項目 | 證據 |
|---|---|
| 鎖定狀態與欄位上游閉包 | `4a66845b`；`Domain/Approvals/ApprovalTopology.cs`（`LockedStates`、`LockedFields`）、`FieldClosure.cs`（`Upstream`） |
| 設計器顯示鎖定狀態、可編輯狀態、鎖定欄位 | `WorkflowService/WorkflowApprovals.cs` 的 `LockedFieldIdsAsync`；測試 `ApprovalConstraintsTests.SaveWorkflow_ResponseListsLockedStates` |
| 入口一致性、閉包與可編輯欄位交集檢查（存流程、存狀態機時，§9.2） | `de94e670`；`ApprovalService/ApprovalConstraints.cs` |
| 流程增改刪、版本、「被用過」 | `5ca9712f` |
| §8.6 第 1 點：被流程引用的欄位不能刪除或停用；被引用的群組不能刪 | `a6d9b8b8`；`ApprovalDeleteGuardTests`；`DeleteGroupHandler.cs:20` |
| §8.5「簽核中」那一欄 | `093ad187`；`ApprovalBackstopStage.cs`、`ApprovalGate.cs` |
| 豁免以記錄為單位、執行期確認 `from` 不是鎖定狀態 | `de0d307e`、a2b 系列 |
| 簽核人臨時讀取權，含附件下載 | `66d08849`、`30b82b44` |
| 應用程式關閉時簽核動作回 409 | `ApprovalActionHandlers.cs:33` |
| §12 通知 | a2c；`ApprovalAlert.cs`（異常通知對象寫死全部系統管理員，A11 這次改） |
| 附件類政策 A20 | p1d；`PolicyCatalog.cs` 的 `attachment.*`（a2d 只用不改） |

### 不適用

| 設計項目 | 理由 |
|---|---|
| §8.5「匯入（更新既有列）」 | 見下方說明 |
| 鎖頭等畫面標示（§13.1） | 歸 a4b |

匯入目前只新增、不更新既有列（`ImportService` 沒有更新路徑）。將來加更新路徑時，它本來就是 `IRecordWriteRequest`，會自動經過本段的後盾。鎖頭標示屬於 a4b（前端簽核畫面）；本段只讓後端回 400 帶欄位鍵，既有表單已能顯示欄位錯誤。

### 這次做

| 設計項目 | 子段 |
|---|---|
| §8.5「鎖定狀態」一欄、§9.3 鎖定欄位與訊息；裁決 4、7 | a2d1 |
| §8.5 附件；裁決 5、6 | a2d2 |
| §8.6 第 4 點、§9.3 整批重算與保留筆數 | a2d3 |
| §8.6 第 2、3 點 | a2d4 |
| 裁決 8（整批解鎖）、裁決 10（刪欄位或區塊）、§9.2 末段閉包重檢 | a2d5 |
| 政策 A3、A4（裁決 1）、A12、A15（裁決 11）；裁決 2、3、9 | a2d6 |
| 政策 A10、A11；總收文件與設計寫回 | a2d7 |

---

## 子段切法與合併順序

七段依序做、依序合併。每段上線後 gate 能過、出廠值等於現值；沒人改設定時，行為不變，只多擋本段本來就要擋的寫入。各段改到相同的檔案（後盾、附件 handler、`UpdateFieldHandler`、`ApproverResolver`），所以不平行。

| 子段 | 內容 | Task | 估計行數（含測試） | 畫面 |
|---|---|---|---|---|
| a2d1 | 鎖定寫入：單一來源、計算欄位保留、後盾 | 1–4 | 約 850 | 無 |
| a2d2 | 附件：走寫入管線、孤兒檔清理、資格檢查 | 5–7 | 約 600 | 無 |
| a2d3 | 整批重算：`FrozenRecords` 擴大、保留筆數 | 8–10 | 約 600 | 有（欄位編輯對話框） |
| a2d4 | 進行中實例守衛：改寫值的定義、轉移與狀態 | 11–13 | 約 650 | 無 |
| a2d5 | 鎖定資料守衛、閉包重檢 | 14–16 | 約 650 | 無 |
| a2d6 | 數值與開關政策：A3、A4、A12、A15 | 17–22 | 約 1000 | 有（政策設定頁新分類） |
| a2d7 | 收件人政策 A10、A11；總收 | 23–26 | 約 850 | 有（收件人編輯器） |

另有開工前的 Task 0。合計 27 個 Task（0–26）。每段交給一個 subagent 做完，各段最後一個 Task 跑一次 gate。

---

## 計畫判斷（技術類，可推翻）

每條寫判斷、理由、選錯代價。

1. **沒有 migration。** 鎖定、守衛、凍結都由既有資料算出；政策存在既有的 `Dynamic_SystemSetting` 與 p1 的模組覆寫表；裁決 2 選了「下一關啟動當下的設定」，不必在實例上存上限。實作中若發現非加不可，停下回報；屆時用 `scripts/db/add-migration.sh` 產兩個 provider 各一個同名 migration，由 `MigrationConsistencyTests` 把關，一段最多一個。
2. **鎖定的最終判斷放在寫入後盾，不在 `ApprovalGate`（PUT 前置）再寫一份。** §8.5 的每條路徑最後都經過後盾。代價：PUT 的 400 在交易內產生，多做一次寫入再回滾。第一道（判斷 26）只負責保留計算欄位的值，不判違規。
3. **鎖定與否看「請求開始前」的狀態**（合併後日誌的 `BeforeValues`）；新增的記錄不檢查。請求結束時的狀態只用來分辨「這次請求解鎖了」（判斷 25）。理由：§9.3「解鎖只能走一條已定義的轉移回到可編輯狀態」；裁決 4 要求同一次存檔轉回草稿又改金額要擋。代價：使用者要分兩次存。
4. **明細列看表頭「請求開始前」的狀態**。表頭在日誌裡時用合併後的 `BeforeValues`，否則以 `IRecordRepository.ListByIdsAsync` 讀。代價：表頭模組有需簽核轉移、而且這次有明細寫入時，多一次查詢。
5. **「明細列一起鎖」的判斷**：彙總欄位 `cfg.SourceFieldId == ModuleRelations.DetailFieldOf(來源模組)?.Id` 才算本單明細，來源模組以 `modules.GetByFieldIdAsync(cfg.SourceFieldId)` 讀（`RollupRecalc.cs:108` 的寫法）。理由：`RollupConfig` 沒有來源模組鍵；明細模組另有一個指回表頭的一般參照時，經那個參照做的彙總不算本單明細（審查 S2）。
6. **鎖定欄位一律加上本模組所有附件欄位**，寫在 `ApprovalTopology.Lock` 裡，所以設計器的 `LockedFieldIds` 也會列出附件欄位。理由：§9.3「附件在鎖定狀態一律鎖定」。代價：模組有附件欄位時，斷言 `LockedFieldIds` 完整集合的既有測試要跟著改。
7. **鎖定欄位違規回 400、鍵為欄位鍵 `f_{id}`**；簽核中違規照舊 409。同一筆同時符合兩者時，先判簽核中（409）。訊息放 `MessageText.ApprovalFieldLocked`（四語），經 `ApprovalMessages.FieldLocked(fieldLabel)` 取用。
8. **附件上傳的冪等**：`UploadAttachmentRequest.Content`（`Stream`）加 `[property: JsonIgnore]`，另加 `long Length`（取 `IFormFile.Length`，handler 不再驗，因為 store 逐位元組計數）；冪等雜湊因此是「記錄、欄位、檔名、長度」。理由：`IdempotencyBehavior.Hash` 會序列化整個請求，`Stream` 序列化會丟例外或讀掉內容。代價：同一把 Idempotency-Key 重送「同名同大小、內容不同」的檔案，會回放第一次的結果。前端上傳沒帶 key（`services/api/dynamic-api.ts:494`），只影響帶 key 的 API 客戶端；手冊與 API 說明寫明「重用 key 時只比對檔名與長度」。
9. **附件在鎖定狀態：上傳 409，以 PUT 刪除或替換 400**。上傳的資格檢查在收檔前就要擋，沒有欄位層級的 400 可回；PUT 是一般寫入，走判斷 7。簽核中兩者都是 409（既有）。已記帳時上傳 409（裁決 6），PUT 照既有的事實鎖後盾回 409。代價：狀態碼不同，手冊寫清楚；總收時把簽核設計 §8.5 表「附件刪除、替換｜鎖定」那一格改成 400。
10. **`FrozenRecords` 以整筆記錄為單位固定**（沿用既有粒度）。保留筆數＝**被修改的模組本身**被跳過的記錄數，不分事實、唯讀、簽核中或鎖定；連帶重算的父模組不加進來（審查 S8）。代價：改明細公式時，數字是明細列數，不是單據張數；說明文字寫「筆已固定的記錄」。
11. **保留筆數放在 `FieldResponse` 的選用屬性 `PreservedRecordCount`（`int?`）**，只有新增或修改欄位、而且觸發整批重算的存檔才有值（0 也給）。`SetFieldStatus` 重新啟用時沒有欄位對話框，不回這個數字。
12. **`PendingApprovalGuard` 的範圍＝`ClosedDataGuard.RejectFormulaRecalcAsync` 的範圍（本模組＋以它為來源的彙總所在的父模組），再加上「本模組是明細時的表頭模組」。** 理由：§8.6「改明細模組的公式，表頭有進行中實例時同樣回 409」。列出實例用 `ApprovalInstance.TitleSnapshot`，不讀記錄。
13. **轉移與狀態保護的判斷資料**：每個進行中實例的 `FromOptionId`、`ToOptionId`，加上它綁定版本（`FlowVersionId`；`direct` 路線沒有版本時略過）的 `RejectTo`。存狀態機時檢查：這些狀態仍在且啟用、狀態欄位沒換、`from → to` 仍存在且仍需簽核、有 `RejectTo` 時 `from → rejectTo` 仍存在。
14. **閉包重檢呼叫既有的 `ApprovalConstraints.Validate`，但只回報這次新出現的錯誤**：套用變更前算一次、套用後算一次，取差集（審查 S5）。位置在整批重算（`UpdateFieldHandler.cs:154`）之前；模組沒有需簽核轉移時不跑。另補 §9.2 的另一個漏洞：`lock` 帶值的目的欄位不能是任何版本的可編輯欄位。
15. **政策代碼與範圍**：見 Global Constraints 的登記表。A3 的「主管層數」與 A4 合併成一個政策 `approval.maxManagerLevel`，**範圍 Module（模組可覆寫）**，全系統出廠 5，技術上限 10 寫死（裁決 1）。覆寫可以比全系統寬（政策設計 §3.1）。
16. **數值政策「改嚴只管之後」**（政策設計 §3.5）：存流程時拿同一流程的目前版本比較。關卡以 `ApprovalStageDef.Code` 對照，備援簽核人（`FallbackApprovers`）拿舊版的備援比較。值「超過上限而且比舊版大」才擋（p1 的 `count > max && count > existingCount` 寫法）。新流程、新關卡沒有舊值，直接比上限。代價：舊版本關卡改代碼，會被當成新關卡。
17. **執行期只用技術上限的兩處**：主管鏈解析用 `PolicyCatalog.ApprovalMaxManagerLevel.Max`（10）；固定名單（來源 `users`，不論有沒有用到備援、備援是什麼）的人數用 `PolicyCatalog.ApprovalMaxApprovers.Max`（50，裁決 9）。關卡本身的來源（群組、主管、使用者欄位、發起人自選挑的人）照下一關啟動當下的 `approval.maxApprovers`（裁決 2）；**【主控 11-13 新規則】頂替的備援簽核人（流程的備援名單或政策預設備援，Task 24 起為 `DefaultFallback`）在執行時只受技術上限 50，不計入每關人數的當下上限**；自選關卡的人數範圍＝上限 min(存檔上限, 當下上限)、最少人數大於上限時用上限，`ApproverResolver.PickRange` 由送出檢查與預覽共用（超過回 400，待決 5），解析時另依當下上限檢查自選挑的人（超過回 409）。**【主控 11-13 修正】**原寫「且沒有用到備援或備援也是 users」會讓名單中有人停用、發起人在名單內時退回當下政策，與裁決 9 的保證不符，改為來源是 users 一律只受技術上限。理由：固定名單在存檔時已驗過，「只管之後」對它才真的成立。
18. **意見字數政策的技術上限是 1000**，因為資料庫欄位是 `HasMaxLength(1000)`（`ApprovalConfigurations.cs:118`），政策只能調低。期限時數上限 8760 同理。
19. **系統管理員的「略過轉移群組」集中到 `TransitionAccess.BypassGroupsAsync(PermissionInputs inputs, ct)`**（4 處：`WorkflowEnforcer.cs:93`，`ApprovalQueryHandlers.cs:40`，`ApprovalPreviewer.cs:52`，以及 `WorkflowEnforcer.cs:56`）。只看 `inputs.User.IsSystemAdmin` 與政策，不重讀 `ICurrentUser`。系統身分在 `WorkflowEnforcer.cs:90` 已先放行，不在這裡再寫一份，列進可寫死清單。
20. **新的政策型別 `Recipients`**：值是 `admins`、`group:<id>`、`user:<userId>` 的組合，逗號分隔、去重保留首次順序、1–20 項。Domain 的 `Normalize` 只檢查語法（`id` 是正整數；`userId` 符合 `CreateUserValidator.IdPattern`）。Application 的 PUT 檢查群組與使用者存在且啟用，400 列出不存在或已停用的項目（新待決 N3，預設照推薦）。代價：資料庫被手改成不存在的 id 時，解析時略過並記 Warning。
21. **收件人類政策不出現在生效值 API**（`GET policies/effective`）：前端執行畫面用不到，而且回應會帶出使用者帳號。
22. **刪群組時，若被收件人政策引用（全系統值或任何模組覆寫），回 409 並列出政策名稱。** 比對方式：先把值拆成項目再比 `group:<id>`，不用字串包含或 SQL `LIKE`（`group:1` 會誤中 `group:12`，也避開兩個 provider 的 collation 差異）。停用使用者不擋：解析時略過停用帳號。
23. **收件人解析為空時**：A11 改送全部啟用中的系統管理員並記 Warning，異常不能沒人知道；A10 照設計 §6 回 409，不偷偷改成系統管理員。
24. **新的政策分類 `approval`**；收件人政策也放這個分類。
25. **解鎖請求的事件放行（裁決 7）靠「輸入界線」**：`IWriteJournal` 加 `int InputSeqEnd` 與 `MarkInputEnd()`，`RecordEffectsBehavior` 在跑第一個 stage 前呼叫。`Seq ≤ InputSeqEnd` 的項目是這次請求的 handler 寫入（使用者輸入，也包括整合、匯入的直接寫入）；之後的是事件效果。請求開始前鎖定、結束時不鎖定的記錄，handler 寫入改到鎖定欄位照樣 400（裁決 4）；事件效果改到鎖定欄位放行。整合以系統身分直接寫入，算 handler 寫入（新待決 N1，預設照推薦）。理由：Seq 是日誌既有的流水號，不必辨認身分。代價：`IWriteJournal` 多一個成員，`WriteJournal.Clear()` 要一起歸零。
26. **第一道：鎖定記錄的計算欄位保留舊值**（審查 M2）。條件是記錄在請求開始前與這次寫入後都在鎖定狀態。`RecordValueProcessor.Process` 加選用參數 `IReadOnlySet<long>? keepComputed`：彙總值與 `ApplyFormulas` 都跳過這些欄位，讓依賴它們的公式用保留的值。`UpdateRecordHandler` 傳入 `lock.FieldIds` 中的 Formula 與 Rollup 欄位；事件與整合的更新經 `EffectWriter` 也走 `UpdateRecordHandler`，一起涵蓋。明細列在表頭鎖定、而且 `DetailRowsLocked` 時，由 `DetailWriter.PrepareAsync` 對既有列做同樣的事。解鎖的那次寫入不保留，照目前公式重算。第二道（後盾）只豁免靜默項目的計算欄位變動，非靜默項目改到鎖定的計算欄位一律 400。
27. **附件孤兒檔**：scoped 的 `AttachmentUploadTracker` 記下本請求寫進 store 的檔案 id。上傳控制器在 `Send` 回非成功或丟例外時，呼叫 `tracker.DeleteAll()`。handler 原本的 `catch` 保留。理由：後盾與檢查失敗回的是 `ServiceResult`，不是例外，handler 看不到；控制器是唯一同時看得到最終結果與 scope 的地方，比另寫一個要排序的 pipeline behavior 單純。成功提交後控制器呼叫 `tracker.Forget()` 清空清單（不刪檔），且 tracker 列入 `DemoStructure.ResetPerRequestState`，共用 scope 時後續失敗不會刪到前一次已提交的檔；`DeleteAll` 遇 IOException 只記 Warning，不蓋掉原結果。代價：Application 層直接 `Send` 的呼叫端（測試與 Demo 設計案產生器 `DesignCaseScenario.cs:180`）寫入後的階段失敗時會留檔，靠既有 `attachments/sweep` 清。
28. **整批解鎖與刪欄位守衛的記錄掃描**用既有的 `ListAllByModuleAsync`，在記憶體判斷狀態與值（同 `TitleRewriteGuard`），不加 repository 方法。理由：設計器操作頻率低，沿用既有寫法；不新增 SQL，兩個 provider 的行為一致。代價：大模組存狀態機時多一次全表讀取。
29. **原 a2d4 拆成兩段**：進行中實例守衛（a2d4）與鎖定資料守衛、閉包重檢（a2d5）。理由：合在一起約 1300 行，超過一個 subagent 能穩定收尾的量。代價：多一次 gate。
30. **已知限制，不在 a2d 修（新待決 N4，預設照推薦）**：鎖定記錄因為別的模組子記錄變動而重算彙總時（靜默、§8.5 允許），`RollupRecalc.RecomputeAsync` 會用目前的公式定義重算其他公式；若核准後改過公式，這次重算會套用新公式。這與 §20.2 已知的「彙總會變」同類，寫進 §20.2。

---

## 使用者裁決（不再重開）

- 業務政策要可設定；守衛、安全、技術限制可以寫死，但列清單（AGENTS.md 鐵則 2、政策設計 §10）。
- 收件人或備援對象改變只影響之後產生的通知與待簽（政策設計 §3.5）；改嚴只管之後的新增。
- a2d 的附件政策只用、不改（p1 已做）。
- 有畫面的改動走查 390／820／1366／1920 四種寬度。
- 2026-11-12 的裁決 1–11 見檔尾兩節，優先於本文。

---

## Global Constraints

- 鎖定訊息原文：「這個欄位已經過簽核，要修改請先退回可編輯的狀態」（§9.3）；英文 `This field has been approved. Return the record to an editable state before changing it.`；泰文、越南文照同義翻譯，不留中文。
- 附件訊息：鎖定「這筆記錄已經過簽核，附件不能再增減；要修改請先退回可編輯的狀態」；已記帳「已記帳的單據不能再增減附件」（裁決 6）。四語。
- 定義守衛（409）訊息要列出進行中的實例（`TitleSnapshot`，最多 5 筆，之後寫「等 N 筆」）。
- 使用者看得到的訊息集中在 `ApprovalMessages.cs`、`PolicyMessages.cs`、`MessageText.cs`；含上限數字的訊息一律帶參數，不寫死數字。
- 不寫 SQLite 專屬語法；新增或修改的 repository 方法，以及本計畫新增的 WebAPI 測試，都掛 `[Trait("Category", "Postgres")]` 進雙跑集（pg0 §6.4 的 WebAPI 清單沒有 `AttachmentApiTests`，所以這個檔案的新案例要個別標）。
- 新測試類一律 `protected override bool ScopePerRequest => true;`（`ApprovalLockService` 與 `PolicyReader` 都在 scope 內快取，共用根 scope 會讀到舊值）。
- 後端測試只跑 `--filter` 窄範圍；WebAPI 全套留給 gate。前端 vitest 一律 `--maxWorkers=2`。
- i18n 四語鍵集合一致、單行 JSON；`poc/shared/policy-catalog.json` 與 `PolicyCatalog` 一致（`PolicyCatalogFixtureTests` 把關），前端 `policy-i18n.vitest.spec.ts` 驗四語文案。
- commit 逐檔 `git add`，訊息用 Conventional Commits，scope `approval` 或 `policy`；不 push，由主控決定。
- 不碰 `../formwright/.tmp/plans/20261111-policy-settings-p2/`、`-p3/`。

### 政策登記表（a2d 新增，出廠值＝現值）

| 代碼 | 對應 | 型別 | 範圍 | 出廠 | 技術範圍 | 讀值處 |
|---|---|---|---|---|---|---|
| `approval.maxStages` | A3 | Integer | Module | 10 | 1–20 | 存流程 |
| `approval.maxApprovers` | A3 | Integer | Module | 20 | 1–50 | 存流程、解析簽核人、自選名單 |
| `approval.maxDueHours` | A3 | Integer | Module | 8760 | 1–8760 | 存流程 |
| `approval.maxCommentLength` | A3 | Integer | System | 1000 | 1–1000 | 簽核動作 |
| `approval.maxManagerLevel` | A3＋A4 | Integer | Module | 5 | 1–10 | 存流程 |
| `approval.allowSelfApproval` | A12 | Boolean | Module | false | — | 解析簽核人、自選名單、預覽 |
| `approval.adminBypassTransitionGroups` | A15 | Boolean | System | true | — | `TransitionAccess` |
| `approval.defaultFallbackApprovers` | A10 | Recipients | Module | admins | 1–20 項 | 解析備援 |
| `approval.alertRecipients` | A11 | Recipients | System | admins | 1–20 項 | 簽核異常通知 |

A15 的名稱是「系統管理員可以略過狀態轉移的群組限制」（裁決 11）。

### 可寫死清單（a2d 新增，總收寫進政策設計 §10）

**資料完整性**
- 鎖定規則本身：哪些狀態、哪些欄位鎖、附件全鎖、以「請求開始前的狀態」判斷、解鎖只放行事件效果。
- 定義守衛：有進行中實例時，拒絕改寫值的定義，並保護轉移與狀態。
- 有記錄停在鎖定狀態時，存狀態機不能把那些狀態變成不鎖定（裁決 8）。
- 有記錄停在鎖定狀態、而且欄位在鎖定集合中有值時，不能刪除欄位或區塊（裁決 10）。
- 已記帳的單據不能增減附件（裁決 6）。
- 被收件人政策引用的群組不能刪。

**安全**
- 系統身分略過轉移群組與簽核入口：平台不變式。
- 異常通知對象解析為空時，改送全部系統管理員。

**技術限制**
- 收件人政策 1–20 項。
- 意見字數 ≤ 1000（資料庫欄位長度）。
- 主管層數技術上限 10，執行期主管鏈也只用這個上限。
- 期限時數 ≤ 8760。
- 每關人數技術上限 50（整關合計，來源加頂替的備援、去重後）；固定名單的關卡與任何頂替的備援，執行期只用這個上限。

---

## Review Focus

1. **鎖定記錄上的跨模組彙總重算**：改了付款單，採購單（鎖定狀態）的「已付金額」彙總跟著變。預期放行（§8.5「允許」）。→ Task 3 `Locked_RollupFromOtherModule_Recalculated_Allowed`。
2. **系統身分與事件寫到鎖定欄位**：事件以系統身分改鎖定記錄的 Discount，記錄仍在鎖定狀態。預期整筆回滾 400。→ Task 3 `Locked_EventUpdatesLockedField_RollsBack400`。
3. **鎖定記錄改非鎖定欄位**：已確認的採購單改備註。預期 200，而且鎖定的計算欄位保留原值。→ Task 2 `Locked_StoredTotalDiffersFromFormula_PutMemo_TotalKept`、Task 3 `Locked_PutMemo_Succeeds`。
4. **政策調低後，原樣再存一次既有流程**。預期成功。→ Task 18 `SaveFlow_StagesOverLoweredLimit_Unchanged_Allowed`、`SaveFlow_FallbackUsersOverLoweredLimit_Unchanged_Allowed`。
5. **收件人政策引用的使用者後來被停用**。預期備援回 409；異常通知改送系統管理員並記 Warning。→ Task 24 `Fallback_OnlyDisabledUser_Rejects409`、`Alert_OnlyDisabledRecipient_FallsBackToAdmins`。
6. **改公式後再改備註**：核准後把 `NetTotal` 公式改掉（沒有進行中實例），已確認單保留舊值；之後改那張單的備註。預期 200，`NetTotal` 仍是舊值。→ Task 8 `Locked_FormulaChangedThenPutMemo_LockedTotalUnchanged`。
7. **解鎖轉移上的事件**：「已確認 → 草稿」轉移上的事件清空核定金額。預期 200；使用者同一次自己改金額仍 400。→ Task 3 `Locked_UnlockTransitionEventClearsLockedField_Succeeds`、`Locked_TransitionBackAndChangeLockedField_SameRequest_Returns400`。
8. **整批解鎖**：關掉「草稿 → 已確認」的需簽核，模組裡還有已確認的單。預期 409、列出筆數。→ Task 14 `SaveWorkflow_TurnOffApproval_RecordsInLockedState_Returns409WithCount`。

---

## Task 0：開工前 rebase 並重新核對（阻塞解除後第一件事）

**Files:** 只改本計畫（`../formwright/.tmp/plans/20261112-approval-a2d/plan.md`），不改程式碼。

- [ ] **Step 1：確認前置條件 1–3。** 不符就停下回報。
- [ ] **Step 2：rebase。** `git fetch && git rebase origin/main`（在 `feature/a2d`）；`git status --short` 乾淨。
- [ ] **Step 3：核對行號與型別。** pg0 會把 `SqliteTestDb` 改名成 `TestDb`、改測試基礎；O1 會改排程與通知，所以下列檔案逐一 `grep -n` 對照本計畫引用的行號、方法名與建構子，有出入就先改計畫再開工：
  - 後端：`UpdateRecordHandler.cs`、`RecordValueProcessor.cs`、`DetailWriter.cs`、`ApprovalBackstopStage.cs`、`WriteJournal.cs`、`RecordEffectsBehavior.cs`、`AttachmentHandlers.cs`、`FrozenRecords.cs`、`FormulaRecalc.cs`、`RollupRecalc.cs`、`UpdateFieldHandler.cs`、`AddFieldHandler.cs`、`SetFieldStatusHandler.cs`、`DeleteFieldHandler.cs`、`DeleteBlockHandler.cs`、`UpdateOptionHandler.cs`、`UpdateModuleHandler.cs`、`SaveWorkflowHandler.cs`、`DeleteWorkflowHandler.cs`、`ApproverResolver.cs`、`ApprovalEngine.cs`、`ApprovalHandlers.cs`、`ApprovalActionHandlers.cs`、`ApprovalModels.cs`、`ApprovalPreviewer.cs`、`ApprovalQueryHandlers.cs`、`WorkflowEnforcer.cs`、`ApprovalDefinitionValidator.cs`、`ApprovalFlowHandlers.cs`、`ApprovalAlert.cs`、`MessageText.cs`。
  - 測試基礎：`ApprovalTestBase.cs`、`DesignerTestBase.cs`（`ScopePerRequest`）、`ApproverResolverTests.cs:37`、`ApprovalRuntimeTextTests.cs:69 :92`。
  - 文件：`docs/feature-checklist.md` 的 a2d 列、`docs/manual.md` 的「系統管理：政策設定」位置。
- [ ] **Step 4：核對雙跑。** 把計畫裡「PG 雙跑」的指令換成合併後 `docs/ai/testing.md` 的寫法；確認 `ApprovalApiTests`、`IdempotencyApiTests` 是否已在 WebAPI 雙跑清單。不在清單的，本計畫新增案例照 Global Constraints 個別標 trait。
- [ ] **Step 5：拿掉頂端的阻塞註記**，在修訂紀錄加一行「Task 0 核對完成：<日期>、<改了哪些行號>」。這一步只改計畫檔，不 commit 進 repo。

---

## a2d1：鎖定寫入

### Task 1：鎖定計算的單一來源 `ApprovalLockService`

**Files:**
- Modify: `src/DynamicModules.Domain/Approvals/ApprovalTopology.cs`（新增 `Lock(...)` 回傳 `ApprovalLock`，加附件欄位；`LockedFields` 保留）
- Create: `src/DynamicModules.Application/Dynamic/ApprovalService/ApprovalLockService.cs`
- Modify: `src/DynamicModules.Application/Dynamic/WorkflowService/WorkflowApprovals.cs`（委派給 `ApprovalLockService`，傳入呼叫端的 `wf`）
- Modify: DI 註冊處（`grep -rn "WorkflowApprovals>" src` 找到同一處，scoped）
- Test: `tests/DynamicModules.DomainTests/Approvals/ApprovalTopologyTests.cs`、`tests/DynamicModules.ApplicationTests/Dynamic/Approvals/ApprovalLockServiceTests.cs`（新檔）

**Interfaces:**
- Produces（Domain）：
  ```csharp
  public sealed record ApprovalLock(IReadOnlySet<long> LockedStateIds, IReadOnlySet<long> FieldIds, bool DetailRowsLocked)
  { public static readonly ApprovalLock None; public bool IsLockedState(long? optionId); }
  public static ApprovalLock Lock(Module module, Workflow wf, IEnumerable<ApprovalDefinition> definitions, Func<Field, bool> isOwnDetailRollup);
  ```
  既有的 `LockedFields(module, wf, definitions)` 保留，改成 `Lock(module, wf, definitions, _ => true).FieldIds` 的捷徑；既有呼叫端不改。
- Produces（Application，scoped）：
  ```csharp
  public sealed class ApprovalLockService(IApprovalFlowRepository flows, IModuleRepository modules)
  { public Task<ApprovalLock> ForModuleAsync(Module module, Workflow? wf, CancellationToken ct); }
  ```
  `wf` 為 null 時用 `module.Workflow`，而且依模組 Id 快取；有傳 `wf`（候選或剛存的狀態機）時不快取。模組沒有狀態機或沒有需簽核轉移時回 `ApprovalLock.None`，不查資料庫。`isOwnDetailRollup` 照判斷 5。

- [ ] **Step 1：寫失敗的測試**

  DomainTests（`ApprovalTopologyTests`）：
  - `Lock_IncludesAttachmentFields`：模組有附件欄位 `Files`、流程條件引用 `Total` → `FieldIds` 含 `Total` 與 `Files`。
  - `Lock_OwnDetailRollupInClosure_DetailRowsLocked`：條件引用 `Total`，`Total` 是公式 `LineSum - Discount`，`LineSum` 是彙總且 `isOwnDetailRollup` 回 true → `DetailRowsLocked == true`。
  - `Lock_OtherModuleRollupInClosure_DetailRowsNotLocked`：同上，但 `isOwnDetailRollup` 回 false → false。
  - `Lock_StatusFieldExcluded`：條件引用狀態欄位 → `FieldIds` 不含狀態欄位。
  - `LockedFields_EqualsLockFieldIds`：同一組輸入，`LockedFields(...)` 與 `Lock(..., _ => true).FieldIds` 相同。

  ApplicationTests（`ApprovalLockServiceTests : ApprovalTestBase`，`ScopePerRequest => true`）：
  - `ForModule_NoApprovalTransition_ReturnsNone`：`Plain` 狀態機 → `ApprovalLock.None`。
  - `ForModule_Scenario3_LockedStatesAreConfirmedAndCancelled`：`LockedStateIds` 等於 {已確認, 已取消}，與 `ApprovalTopology.LockedStates(wf)` 相同。
  - `ForModule_DetailRollupFromLine_DetailRowsLocked`：`BuildPurchase` 的 `LineSum` 來源是明細 `Line`、條件引用 `NetTotal` → true；條件只引用 `Supplier` → false。
  - `ForModule_RollupViaOtherReferenceToHeader_NotOwnDetail`（S2）：明細模組另有一個指回表頭的一般參照，經那個參照做的彙總 → `DetailRowsLocked == false`。
  - `ForModule_CandidateWorkflow_UsesCandidate`（S1）：先以 `wf: null` 算一次（有快取），再傳入把「草稿 → 已確認」的需簽核關掉的候選 → `LockedStateIds` 為空。
  - `WorkflowResponse_LockedFieldIds_UnchangedForPurchase`：跑既有 `SaveWorkflow_ResponseListsLockedStates` 的情境，`LockedFieldIds` 與改動前相同。
  - `WorkflowResponse_LockedFieldIds_IncludesAttachmentField`（nit）：採購單 rig 加一個附件欄位 → `GET` 狀態機回應的 `LockedFieldIds` 含它。

- [ ] **Step 2：跑測試確認失敗**
  ```bash
  cd poc/backend && dotnet test tests/DynamicModules.DomainTests --filter "FullyQualifiedName~ApprovalTopologyTests"
  dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~ApprovalLockServiceTests"
  ```
  預期：編譯失敗（`ApprovalLock`、`ApprovalLockService` 不存在）。

- [ ] **Step 3：實作** `ApprovalTopology.Lock`、`ApprovalLockService.ForModuleAsync`。`WorkflowApprovals.LockedFieldIdsAsync(module, wf, ct)` 改成 `(await lockService.ForModuleAsync(module, wf, ct)).FieldIds`，把呼叫端（`SaveWorkflowHandler.cs:76`、`GetWorkflowHandler.cs:15`）手上的 `wf` 傳下去。

- [ ] **Step 4：跑測試確認通過**：同 Step 2，另跑 `--filter "FullyQualifiedName~ApprovalConstraintsTests"`。預期全部 PASS。

- [ ] **Step 5：commit** `feat(approval): 鎖定狀態、鎖定欄位與明細鎖的單一來源`。

### Task 2：第一道，鎖定記錄的計算欄位保留舊值

**Files:**
- Modify: `src/DynamicModules.Application/Dynamic/RecordService/RecordValueProcessor.cs`（`Process` 與 `ApplyFormulas` 加 `keepComputed`）
- Modify: `src/DynamicModules.Application/Dynamic/RecordService/Commands/UpdateRecord/UpdateRecordHandler.cs`（注入 `ApprovalLockService`，`:63`、`:66` 兩處 `Process` 傳入）
- Modify: `src/DynamicModules.Application/Dynamic/RecordService/DetailWriter.cs`（`PrepareAsync` 加 `keepComputedRows`）
- Test: `tests/DynamicModules.ApplicationTests/Dynamic/Approvals/ApprovalLockTests.cs`（新檔，Task 3 共用）

**Interfaces:**
- Consumes：Task 1 的 `ApprovalLockService.ForModuleAsync(module, null, ct)`。
- Produces：
  ```csharp
  RecordValueProcessor.Process(..., bool frozen = false, IReadOnlySet<long>? keepComputed = null);
  RecordValueProcessor.ApplyFormulas(Module, Dictionary<string, JsonElement?>, DateOnly, IReadOnlyDictionary<long, FieldLock>? skip = null, IReadOnlySet<long>? keep = null);
  DetailWriter.PrepareAsync(..., bool keepComputedRows = false);
  ```
  規則見判斷 26。`UpdateRecordHandler` 的條件：`lock.IsLockedState(wf.StateOf(previous))`，而且寫入值的狀態仍是鎖定狀態。狀態值由 `WorkflowEnforcer.SeedMissingState` 之後的 `input` 決定；沒帶狀態時，等於 `previous` 的狀態。

- [ ] **Step 1：寫失敗的測試**（`ApprovalLockTests : ApprovalTestBase`，`ScopePerRequest => true`。`ApprovedPo()` 是測試類內的 helper：`NewPo` → `Submit` → 簽核人同意，斷言 `StateOf(po) == 已確認`；流程條件引用 `NetTotal` 與 `Supplier`）
  - `Locked_StoredTotalDiffersFromFormula_PutMemo_TotalKept`：測試直接改資料庫，把已確認單的 `NetTotal` 改成和公式算出來不同的值，模擬核准後改過公式 → `Put(po, V(Memo: "x"))` → 200，`NetTotal` 仍是改過的值。
  - `Locked_EventUpdatesMemo_TotalKept`：同上的資料，`Plain` 事件以系統身分改 `Memo` → 200，`NetTotal` 不變（事件經 `EffectWriter` 走同一個 handler）。
  - `Locked_TransitionBackToDraft_FormulaRecomputed`：同上的資料，只把狀態改回草稿 → 200，`NetTotal` 等於目前公式的結果。
  - `Locked_PutHeaderWithUnchangedDetails_DetailComputedKept`：測試直接改一列明細的小計（公式欄位），表頭 PUT 原封不動帶回明細 → 200，該列小計與表頭 `LineSum` 都不變。
  - `Draft_PutMemo_FormulaRecomputed`：草稿記錄照目前公式重算（防回歸）。

- [ ] **Step 2：跑測試確認失敗**
  ```bash
  cd poc/backend && dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~ApprovalLockTests"
  ```
  預期：`_TotalKept`、`_DetailComputedKept` FAIL；`_FormulaRecomputed` 兩條 PASS。

- [ ] **Step 3：實作**。`keepComputed` 只收 Formula 與 Rollup 欄位；`Process` 在套用 `rollupValues` 與 `ApplyFormulas` 時跳過這些欄位，值留在 `previous`。若 `Process` 的 `values` 不是由 `previous` 起算，就在 `Process` 結束前以 `previous` 蓋回。`DetailWriter.PrepareAsync` 收到 `keepComputedRows` 時，對既有列（有 `change.Before`）做同樣的事，`plan.RollupValues` 用保留後的值計算。

- [ ] **Step 4：跑測試確認通過**：同 Step 2，另跑 `--filter "FullyQualifiedName~RecordValueProcessor|FullyQualifiedName~DetailWriter|FullyQualifiedName~Formula"` 防回歸。預期 PASS。

- [ ] **Step 5：commit** `feat(approval): 鎖定記錄的計算欄位在寫入時保留核准時的值`。

### Task 3：第二道，寫入後盾加鎖定檢查

**Files:**
- Modify: `src/DynamicModules.Application/Dynamic/ApprovalService/ApprovalBackstopStage.cs`
- Modify: `src/DynamicModules.Application/Common/WriteJournal.cs`（`InputSeqEnd`、`MarkInputEnd()`、`Clear()` 歸零）
- Modify: `src/DynamicModules.Application/Common/Behaviors/RecordEffectsBehavior.cs`（注入 `IWriteJournal`，跑 stage 前呼叫 `MarkInputEnd()`）
- Modify: `ApprovalMessages.cs`、`src/DynamicModules.Application/Common/MessageText.cs`
- Test: `ApprovalLockTests.cs`、`tests/DynamicModules.WebAPITests/Dynamic/ApprovalApiTests.cs`

**Interfaces:**
- Consumes：Task 1 的 `ApprovalLockService`、`ApprovalLock`。
- Produces：`ApprovalMessages.FieldLocked(string fieldLabel)` → `MessageRef`；`MessageText.ApprovalFieldLocked`（四語，`{0}` 為欄位名稱）；`IWriteJournal.InputSeqEnd`、`MarkInputEnd()`。

演算法放在既有「簽核中」迴圈之後、轉移檢查之前，`anyApproval` 短路照舊：

```
對合併後日誌的每筆 Modified 記錄 e（簽核中的已在前面處理，跳過）：
  lock = ForModuleAsync(e 的模組, null)
  若 lock.IsLockedState(請求開始前的狀態 = wf.StateOf(e.BeforeValues))：
    unlocking = !lock.IsLockedState(wf.StateOf(e.AfterValues))
    一般欄位（lock.FieldIds 扣掉 Formula、Rollup、狀態欄位；豁免 context.IsExempt 照舊）：
      unlocking 為 false：合併後 before → after 有變 → 400
      unlocking 為 true：只看 Seq ≤ InputSeqEnd 的項目合併後的變動 → 有變就 400（裁決 4）；事件效果的變動放行（裁決 7）
    計算欄位（lock.FieldIds 中的 Formula、Rollup）：
      unlocking 為 false：逐筆原始項目，非靜默項目的 before → after 改到它 → 400；靜默項目放行（§8.5 彙總、參照重算）
      unlocking 為 true：放行（解鎖後照目前公式重算）
    400 的鍵為第一個違規欄位的 f_{id}
    軟刪除照既有規則（§8.5「刪除：照既有規則」），不在這裡擋
對明細列（headersOf 有值的 e）：
  表頭模組 lock.DetailRowsLocked 為 true，而且表頭請求開始前在鎖定狀態
  表頭狀態：表頭在日誌裡用合併後的 BeforeValues／AfterValues，否則以 records.ListByIdsAsync 讀（前後相同）
  算違規的項目：非靜默，而且是 Added、軟刪除，或改到任何欄位（計算欄位也算，第一道已保留它們）
  表頭解鎖中（結束時不在鎖定狀態）：只算 Seq ≤ InputSeqEnd 的項目
  有違規 → 400，鍵為表頭上明細欄位的 f_{id}
```

- [ ] **Step 1：寫失敗的測試**（`ApprovalLockTests`，沿用 Task 2 的 `ApprovedPo()`）
  - `Locked_PutLockedField_Returns400WithFieldKey`：`Put(po, V(Discount: 50))` → 400，`Errors` 含鍵 `f_{r.Discount}`，訊息含「這個欄位已經過簽核」。
  - `Locked_PutMemo_Succeeds`：`Put(po, V(Memo: "x"))` → 200。
  - `Locked_PutStatusTransitionBackToDraft_Succeeds`：只改狀態回草稿（狀態機有「已確認 → 草稿」）→ 200。
  - `Locked_TransitionBackAndChangeLockedField_SameRequest_Returns400`（裁決 4）：同一次 PUT 改狀態回草稿並改 `Discount` → 400。
  - `Locked_UnlockTransitionEventClearsLockedField_Succeeds`（裁決 7）：「已確認 → 草稿」轉移上的事件把 `Discount` 設成 0 → 200，狀態是草稿，`Discount == 0`。
  - `Locked_EventUpdatesLockedField_RollsBack400`：`Plain` 事件在 `Memo` 改變時以系統身分把 `Discount` 設成 0，記錄仍在已確認 → 400，`Memo`、`Discount` 都是原值。
  - `Backstop_NonSilentComputedChangeOnLocked_Returns400`：測試類內解析 `IWriteJournal` 與 `ApprovalBackstopStage`，加一筆已確認記錄 `NetTotal` 改變的非靜默項目後呼叫 `RunAsync` → 400；同一筆改成 `Silent` → null。若 rig 不能在 scope 內直接加日誌項目，就用建構子手動注入一個 `WriteJournal` 實例。
  - `Locked_AddDetailLine_WhenDetailRollupLocked_Returns400`：新增一列 `Line` → 400，鍵為明細欄位的 `f_{id}`。
  - `Locked_AddDetailLine_WhenConditionOnlySupplier_Succeeds`：條件只引用 `Supplier` → 新增明細 200。
  - `Locked_PutHeaderWithUnchangedDetails_Succeeds`（M3）：表頭 PUT 原封不動帶回全部明細 → 200。
  - `Locked_DetailLineRollupFromOtherModule_Recalculated_Allowed`（M3）：rig 沒有就在測試類內建「收貨明細」模組，參照採購明細，採購明細加彙總「已收數量」→ 新增一筆收貨明細 → 200，採購明細的「已收數量」更新。
  - `Locked_SetNullOnLockedReference_RollsBack400`：流程條件引用 `Supplier`（參照設 SetNull）；刪除 `Vendor` 記錄 → 400，Vendor 仍在。
  - `Locked_RollupFromOtherModule_Recalculated_Allowed`：`Extra` 模組（獨立模組，彙總 `ExtraSum` 到採購單）新增一筆 → 200，`ExtraSum` 更新。
  - `Locked_ApprovalEditableFieldOnOwnInstance_StillExempt`：a2b 的「簽核人同意帶可編輯欄位」情境照舊 200。
  - `PendingFromLocked_PutLockedField_Returns409`（nit）：已確認的單送出作廢簽核（從鎖定狀態出發的需簽核轉移，以 `Scenario3` 的作廢為準），PUT `Discount` → 409，不是 400（判斷 7）。

  WebAPITests（`ApprovalApiTests`，`[Trait("Category", "Postgres")]`）：
  - `Put_LockedField_Returns400ProblemWithFieldKey`：HTTP PUT 回 400，problem details 的 `errors` 有 `f_{id}`。

- [ ] **Step 2：跑測試確認失敗**
  ```bash
  cd poc/backend && dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~ApprovalLockTests"
  ```
  預期：400 類與 `Backstop_*` FAIL；`Locked_PutMemo_Succeeds`、`_Allowed`、`PendingFromLocked_*` PASS。

- [ ] **Step 3：實作** 照上方演算法；建構子加 `ApprovalLockService lockService, IRecordRepository records`。更新類別註解（移除「鎖定在 a2d 加」，寫明兩道與解鎖規則）。

- [ ] **Step 4：跑測試確認通過**
  ```bash
  cd poc/backend && dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~Approval|FullyQualifiedName~Event|FullyQualifiedName~FactLock"
  dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~ApprovalApiTests"
  ```
  預期：全部 PASS（`MarkInputEnd` 不影響既有事件與事實鎖）。

- [ ] **Step 5：commit** `feat(approval): 簽核鎖定狀態的鎖定欄位與明細寫入回 400，解鎖轉移的事件放行`。

### Task 4：a2d1 收尾（文件、PG 雙跑、gate）

**Files:** `docs/roadmap.md`（a2d 列改「進行中：a2d1 完成」）、`docs/manual.md`（簽核章節加「核准後哪些欄位不能改、要怎麼改」：文案用 Task 3 訊息原文；寫明要先轉回可編輯狀態存一次、再改；解鎖轉移上的事件照常執行）。

- [ ] **Step 1：PG 雙跑**：前置條件 2 的指令，另跑 WebAPITests 同 filter。預期 `Failed: 0`。
- [ ] **Step 2：gate**：`bash scripts/unattended/gate.sh ../dynamic-fields-design.a2d`。預期每步 `exit=0`、整支 exit 0。
- [ ] **Step 3：走查**：無畫面改動，不做四寬度；`scripts/unattended/local-demo.sh` 起 Demo，用 1366 寬開一張已核准的採購單改金額，確認表單欄位下出現 400 訊息（formwright-demo-verify）。
- [ ] **Step 4：commit** `docs(approval): a2d1 簽核鎖定寫入的手冊與進度`。

---

## a2d2：附件

### Task 5：上傳改走記錄寫入管線，失敗不留檔

**Files:**
- Modify: `src/DynamicModules.Application/Dynamic/AttachmentService/AttachmentRequests.cs`
- Modify: `src/DynamicModules.Application/Dynamic/AttachmentService/AttachmentHandlers.cs`（`UploadAttachmentHandler`）
- Create: `src/DynamicModules.Application/Dynamic/AttachmentService/AttachmentUploadTracker.cs`（scoped）
- Modify: 上傳控制器（`grep -rn "UploadAttachmentRequest(" src/DynamicModules.WebAPI`），傳入 `Length`、失敗時清檔
- Modify: DI 註冊處
- Test: `tests/DynamicModules.ApplicationTests/Dynamic/Approvals/ApprovalAttachmentTests.cs`（新檔）、`tests/DynamicModules.WebAPITests/Dynamic/AttachmentApiTests.cs`

**Interfaces:**
- Produces：
  ```csharp
  public sealed record UploadAttachmentRequest(long RecordId, long FieldId,
      [property: JsonIgnore] Stream Content, string? FileName, long Length) : IRequest<ServiceResult>, IRecordWriteRequest;
  public sealed class AttachmentUploadTracker(IAttachmentStore store)
  { public void Track(string id); public void DeleteAll(); }
  ```
  handler 在 `store.SaveAsync` 成功後立刻 `tracker.Track(id)`；控制器在 `Send` 回非成功或丟例外時呼叫 `tracker.DeleteAll()`（判斷 27）。改實作介面後，`RecordEffectsBehavior` 的各 stage（事件、檢查、任務規則、事實鎖、分錄、簽核後盾）照常跑（裁決 5）。

- [ ] **Step 1：寫失敗的測試**

  ApplicationTests（`ApprovalAttachmentTests`，`ScopePerRequest => true`，rig 比照 `ApprovalReadGrantTests` 帶附件欄位 `_files`）：
  - `Upload_IsRecordWriteRequest`：`typeof(UploadAttachmentRequest).Should().Implement<IRecordWriteRequest>()`。
  - `Upload_EventWritesPendingOtherRecord_Returns409`（S4）：記錄 A 的模組設「記錄更新時以系統身分改 B」的事件，B 簽核中 → 上傳到 A → 409，A 的附件欄位沒有新檔案。這條只有後盾擋得住。
  - `Upload_EditableRecord_StillSucceeds`：草稿記錄上傳 → 201，欄位值多一個檔案。
  - `Upload_TriggersRecordUpdatedTaskRule`（裁決 5）：模組設「記錄更新時」任務規則 → 上傳後產生一筆任務。

  WebAPITests（`AttachmentApiTests`，新案例都標 `[Trait("Category", "Postgres")]`）：
  - `Upload_WithIdempotencyKey_ReplayDoesNotStoreSecondFile`：同一把 key 上傳兩次同名同大小的檔案 → 兩次結果相同，附件欄位只有一個檔案，儲存目錄只多一個檔。
  - `Upload_WithIdempotencyKey_DifferentFileName_Returns422`：同一把 key 換檔名 → 422（`IdempotencyKeyReused`）。
  - `Upload_RejectedByBackstop_NoOrphanFile`（M4）：同 `Upload_EventWritesPendingOtherRecord_Returns409` 的情境走 HTTP → 409，儲存目錄的檔案數不變。

- [ ] **Step 2：跑測試確認失敗**
  ```bash
  cd poc/backend && dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~ApprovalAttachmentTests"
  dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~AttachmentApiTests"
  ```
  預期：`Upload_IsRecordWriteRequest`、`Upload_EventWritesPendingOtherRecord_Returns409`、`Upload_TriggersRecordUpdatedTaskRule`、`Upload_RejectedByBackstop_NoOrphanFile` FAIL。

- [ ] **Step 3：實作** 介面、`Length`、tracker 與控制器的清檔。

- [ ] **Step 4：跑測試確認通過**：同 Step 2，另跑 `--filter "FullyQualifiedName~Attachment|FullyQualifiedName~Idempotency"` 防回歸。預期 PASS。

- [ ] **Step 5：commit** `feat(approval): 附件上傳改走記錄寫入管線，失敗時清掉剛寫的檔案`。

### Task 6：上傳資格檢查加簽核中、鎖定與已記帳

**Files:**
- Modify: `AttachmentHandlers.cs` 的 `AttachmentUploadGate`（建構子加 `IApprovalInstanceRepository`、`ApprovalLockService`、`EventFactLock`、`LedgerGate`）
- Modify: `ApprovalMessages.cs`、`MessageText.cs`（`AttachmentLocked`、`AttachmentPosted`，四語）
- Test: `ApprovalAttachmentTests.cs`、`AttachmentApiTests.cs`

**Interfaces:**
- Consumes：Task 1 的 `ApprovalLockService`；既有 `IApprovalInstanceRepository.GetPendingByRecordAsync`、`EventFactLock.IsFrozenAsync`、`LedgerGate.IsLedgerAsync`。
- Produces：在既有 `wf.IsLocked(previous)` 之後依序判斷：有進行中實例 → 409 `ApprovalMessages.Pending`；`lock.IsLockedState(wf.StateOf(previous))` → 409 `ApprovalMessages.AttachmentLocked`；模組是分錄模組或記錄已有事實 → 409 `MessageText.AttachmentPosted`（裁決 6）。

- [ ] **Step 1：寫失敗的測試**
  - `CheckUpload_PendingRecord_Returns409`：`CheckAttachmentUploadRequest` 對簽核中記錄 → 409。
  - `CheckUpload_LockedState_Returns409`：已確認的記錄 → 409，訊息含「附件不能再增減」。
  - `CheckUpload_LockedStateBackToDraft_Succeeds`：轉回草稿後 → 200。
  - `CheckUpload_PostedRecord_Returns409`（裁決 6）：記錄已產生事件事實 → 409，訊息「已記帳的單據不能再增減附件」。
  - `CheckUpload_LedgerRecord_Returns409`（裁決 6）：分錄模組的記錄 → 409，同一訊息。
  - `Put_RemoveAttachment_LockedState_Returns400`：PUT 把附件欄位改成空陣列 → 400，鍵為附件欄位的 `f_{id}`（判斷 9）。
  - `Put_RemoveAttachment_Pending_Returns409`：簽核中 → 409（既有行為）。
  - WebAPITests（`Category=Postgres`）`Upload_LockedRecord_RejectedBeforeBodyRead`、`Upload_PostedRecord_RejectedBeforeBodyRead`：HTTP 上傳回 409，沿用 p1d 檢查「在讀主體前擋」的既有寫法。

- [ ] **Step 2：跑測試確認失敗**（同 Task 5 指令）。預期：`CheckUpload_*` 409 類與兩條 WebAPI FAIL。
- [ ] **Step 3：實作**。
- [ ] **Step 4：跑測試確認通過**：同 Task 5 Step 4。預期 PASS。
- [ ] **Step 5：commit** `feat(approval): 簽核中、鎖定與已記帳的單據不能上傳附件`。

### Task 7：a2d2 收尾

**Files:** `docs/manual.md`、`docs/roadmap.md`。手冊的附件段落補三件事：
- 簽核中、核准後、已記帳的單據不能增減附件。上傳會被擋，以修改單據的方式刪除附件時，核准後的單據回欄位錯誤。用使用者看得懂的說法寫。
- 上傳附件現在算一次「記錄更新」：會觸發記錄更新的事件、檢查與任務規則。請設計者檢查既有規則的條件（裁決 5）。
- API 重用 Idempotency-Key 時，只比對檔名與長度（判斷 8）。

- [ ] **Step 1：PG 雙跑**（ApplicationTests、WebAPITests）。預期 `Failed: 0`。
- [ ] **Step 2：gate**。預期整支 exit 0。
- [ ] **Step 3：走查**：無畫面改動，不做四寬度；在 Demo 上對已核准的單按上傳，確認出現 409 訊息、檔案沒有上傳。
- [ ] **Step 4：commit** `docs(approval): a2d2 附件鎖定的手冊與進度`。

---

## a2d3：整批重算

### Task 8：`FrozenRecordsLoader`，重算改用 loader

**Files:**
- Modify: `src/DynamicModules.Domain/Repositories/Dynamic/IApprovalRepositories.cs`、`src/DynamicModules.Persistence/Repositories/Dynamic/ApprovalRepositories.cs`
- Modify: `src/DynamicModules.Application/Dynamic/Rollups/FrozenRecords.cs`（建構子改、移除 static `LoadAsync`）
- Create: `src/DynamicModules.Application/Dynamic/Rollups/FrozenRecordsLoader.cs`（scoped，註冊在 `RollupRecalc` 同處）
- Modify: `src/DynamicModules.Application/Dynamic/FieldService/FormulaRecalc.cs`（參數 `facts` 換成 `FrozenRecordsLoader loader`）
- Modify: `src/DynamicModules.Application/Dynamic/Rollups/RollupRecalc.cs`（建構子注入 `FrozenRecordsLoader`，`RecalculateModuleAsync` 改用它；`IEventFactRepository facts` 仍留給 `RecomputeAsync`）
- Modify: `AddFieldHandler.cs:98`、`UpdateFieldHandler.cs:156`、`SetFieldStatusHandler.cs:83`（傳 loader，回傳值這個 Task 先不用）
- Test: `tests/DynamicModules.ApplicationTests/Dynamic/Approvals/ApprovalFrozenRecordsTests.cs`（新檔）、repository 測試（放既有 Approval repository 測試類，`[Trait("Category","Postgres")]`）

**Interfaces:**
- Produces：
  ```csharp
  public sealed record PendingInstanceRef(long InstanceId, long RecordId, long ModuleId, long FromOptionId, long ToOptionId, long? FlowVersionId, string TitleSnapshot);
  Task<List<PendingInstanceRef>> ListPendingByModulesAsync(IReadOnlyCollection<long> moduleIds, CancellationToken ct);   // IApprovalInstanceRepository，AsNoTracking
  public sealed class FrozenRecordsLoader(IEventFactRepository facts, IApprovalInstanceRepository instances, IRecordRepository records,
      IModuleRepository modules, ApprovalLockService locks)
  { public Task<FrozenRecords> LoadAsync(Module module, IReadOnlyCollection<Record> rows, CancellationToken ct); }
  public sealed class FrozenRecords(Module module, IReadOnlySet<long> withFacts, IReadOnlySet<long> frozenHeaderIds,
      IReadOnlySet<long> pendingIds, ApprovalLock approvalLock)
  ```
  `Contains(record)`：有事實、簽核中、`wf.IsLocked(values)`（唯讀）、`approvalLock.IsLockedState(wf.StateOf(values))`，或表頭在 `frozenHeaderIds`。直接用記錄值判斷，不預先算「鎖定記錄 Id 集合」（S9）。
  loader 預先載入四樣：本模組事實 Id（既有）、`ListPendingByModulesAsync([module.Id, 表頭模組 Id])` 的記錄 Id、本模組的 lock，以及明細模組時的 `frozenHeaderIds`。`frozenHeaderIds` 是三者的聯集：有事實的表頭、簽核中的表頭、以 `records.ListByIdsAsync(表頭模組, rows 裡出現的表頭 Id)` 讀出後判定在鎖定狀態的表頭。不讀整個表頭模組。
  `rows` 由呼叫端傳入：`FormulaRecalc` 與 `RecalculateModuleAsync` 本來就會 `ListAllByModuleAsync`，先讀再傳，不多讀一次。

- [ ] **Step 1：寫失敗的測試**（`ScopePerRequest => true`）
  - repository：`ListPendingByModules_ReturnsOnlyPendingOfGivenModules`：兩個模組各一筆 pending、一筆 approved，加第三個模組一筆 pending → 只回前兩個模組的 pending，欄位值（含 `TitleSnapshot`）正確。
  - `Loader_PendingRecord_Frozen`、`Loader_LockedStateRecord_Frozen`、`Loader_DraftRecord_NotFrozen`。
  - `Loader_DetailRowOfLockedHeader_Frozen`：明細模組 `Line`，表頭在已確認 → `Contains == true`；表頭在草稿 → false。
  - `Loader_DetailRowOfPendingHeader_Frozen`。
  - `Loader_ModuleWithoutApproval_SameAsFactsOnly`：`Plain` 模組 → 結果與改版前只看事實相同。
  - `UpdateFormula_NoPending_LockedRecordKeepsOldValue`：一筆已確認、一筆草稿；把 `NetTotal` 公式改成 `LineSum` → 已確認那筆是舊值，草稿那筆是新值。
  - `Locked_FormulaChangedThenPutMemo_LockedTotalUnchanged`（M2、Review Focus 6）：接上一條 → 已確認單 `Put(po, V(Memo: "x"))` → 200，`NetTotal` 仍是舊值。

- [ ] **Step 2：跑測試確認失敗**
  ```bash
  cd poc/backend && dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~ApprovalFrozenRecordsTests|FullyQualifiedName~ListPendingByModules"
  ```
  預期：編譯失敗。
- [ ] **Step 3：實作**。這個 Task 結束時，`FrozenRecords.LoadAsync` 已沒有呼叫端，一併刪除，`rg -n "FrozenRecords.LoadAsync" poc/backend/src` 無輸出。
- [ ] **Step 4：跑測試確認通過**：同 Step 2，另跑 `--filter "FullyQualifiedName~Formula|FullyQualifiedName~Rollup|FullyQualifiedName~FactLock"` 防回歸；再跑 PG 雙跑的同一 filter。預期兩邊 PASS。
- [ ] **Step 5：commit** `feat(approval): 整批重算固定簽核中與鎖定狀態的記錄`。

### Task 9：重算回報保留筆數，欄位對話框顯示

**Files:**
- Modify: `FormulaRecalc.cs`、`RollupRecalc.cs`（回傳筆數）
- Modify: `AddFieldHandler.cs`、`UpdateFieldHandler.cs`（放進回應）；`SetFieldStatusHandler.cs` 不回筆數（判斷 11）
- Modify: `src/DynamicModules.Application/Dynamic/ModuleService/Data/ModuleResponses.cs`（`FieldResponse`）
- Modify: 前端 `src/domain/dynamic/` 的 FieldResponse 型別、`src/presentation/dynamic/designer/FieldEditorDialog.vue`、`src/i18n/{zh-TW,en-US,th,vi}.json`
- Test: `ApprovalFrozenRecordsTests.cs`；前端 `src/presentation/dynamic/designer/FieldEditorDialog.vitest.spec.ts`（既有就加，沒有就新建）

**Interfaces:**
- Consumes：Task 8 的 `FrozenRecordsLoader`。
- Produces：
  - `FormulaRecalc.RecalculateAsync(...)` 回 `Task<int>`：本模組被跳過的記錄數；之後連帶的 `RecalculateForSourceModuleAsync` 不加進來。
  - `RollupRecalc.RecalculateModuleAsync` 回 `Task<int>`（該模組被跳過的記錄數）；`RecalculateForSourceModuleAsync` 不變。
  - `public record FieldResponse(..., List<OptionResponse> Options, int? PreservedRecordCount = null);`
  - 數字的定義見判斷 10：只算被修改的模組；`UpdateFieldHandler.cs:161-162` 的 `rederivedParents` 重算不加。
  - i18n 鍵 `designer.field.preservedRecords`：「有 {n} 筆已固定的記錄保留原值，沒有跟著重算」。

- [ ] **Step 1：寫失敗的測試**
  - `UpdateFormula_NoPending_LockedRecordKeepsOldValue_ReportsCount`：Task 8 同情境 → `PreservedRecordCount == 1`。
  - `UpdateDetailFormula_LockedHeader_DetailRowKeepsOldValue`：已確認單有 3 列明細，改明細的小計公式 → 明細小計與表頭 `LineSum` 都不變，`PreservedRecordCount == 3`（明細模組本身的筆數，不含表頭）。
  - `AddRollupField_LockedRecord_ReportsCount`：採購單新增彙總欄位 → `PreservedRecordCount == 1`。
  - `UpdateLabelOnly_PreservedRecordCountIsNull`：只改標籤 → null。
  - 前端 `shows preserved-record notice after save`：mock 存檔回應 `preservedRecordCount: 2` → 出現含「2」的提示；`null` 時不出現。

- [ ] **Step 2：跑測試確認失敗**
  ```bash
  cd poc/backend && dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~ApprovalFrozenRecordsTests"
  cd ../frontend && bunx vitest run src/presentation/dynamic/designer --maxWorkers=2
  ```
  預期：新測試 FAIL。
- [ ] **Step 3：實作**：handler 把數字放進 `field.ToResponse() with { PreservedRecordCount = n }`；前端存檔成功後用既有通知元件顯示。
- [ ] **Step 4：跑測試確認通過**：上列兩條指令＋`--filter "FullyQualifiedName~Formula|FullyQualifiedName~Rollup"`＋`bun run typecheck`。預期 PASS、typecheck 無錯。
- [ ] **Step 5：commit** `feat(approval): 存檔公式或彙總時回報保留原值的記錄數`。

### Task 10：a2d3 收尾（四寬度走查）

**Files:** `docs/manual.md`（設計器的公式與彙總段落補：已固定的記錄保留原值、提示的數字只算這個模組、明細公式算的是明細列數）、`docs/roadmap.md`。

- [ ] **Step 1：PG 雙跑**。預期 `Failed: 0`。
- [ ] **Step 2：gate**。預期整支 exit 0。
- [ ] **Step 3：四寬度走查**（formwright-demo-verify）：`local-demo.sh` 起 Demo，在 390、820、1366、1920 四種寬度打開採購單的欄位編輯對話框、改公式存檔。確認提示完整顯示、沒有橫向捲動（`scripts/unattended/rwd-check.js` 的 `overflow == false`）。
- [ ] **Step 4：commit** `docs(approval): a2d3 整批重算保留筆數的手冊與進度`。

---

## a2d4：進行中實例守衛

### Task 11：進行中實例存在時拒絕改寫值的定義

**Files:**
- Create: `src/DynamicModules.Application/Dynamic/ApprovalService/PendingApprovalGuard.cs`（scoped）
- Modify: `AddFieldHandler.cs:36`、`UpdateFieldHandler.cs`（`:53` 帶值、`:110-121` 公式與彙總、`:140` 小數位）、`SetFieldStatusHandler.cs:77`、`DeleteService/Commands/DeleteField/DeleteFieldHandler.cs:26`、`DeleteService/Commands/DeleteBlock/DeleteBlockHandler.cs:25`、`FieldService/Commands/UpdateOption/UpdateOptionHandler.cs:29`、`ModuleService/Commands/UpdateModule/UpdateModuleHandler.cs:28`。這七處是 `immutable.RejectAsync` 的全部呼叫處（M6），守衛緊接在它之後。
- Modify: `ApprovalMessages.cs`
- Test: `tests/DynamicModules.ApplicationTests/Dynamic/Approvals/PendingApprovalGuardTests.cs`（新檔，`ScopePerRequest => true`）

**Interfaces:**
- Consumes：Task 8 的 `ListPendingByModulesAsync`；`IModuleRepository.ListRollupsBySourceModuleAsync`（同 `ClosedDataGuard`）。
- Produces：
  ```csharp
  public sealed class PendingApprovalGuard(IApprovalInstanceRepository instances, IModuleRepository modules)
  { public Task<ServiceResult?> RejectValueRewriteAsync(Module module, CancellationToken ct); }   // 有進行中實例 → 409，否則 null
  ```
  範圍見判斷 12。訊息 `ApprovalMessages.DefinitionBlockedByPending(IReadOnlyList<string> titles, int total)`，`titles` 取 `TitleSnapshot` 前 5 筆。

觸發條件與各處既有 `immutable.RejectAsync` 的條件相同：新增公式或彙總欄位；公式、彙總設定改變；`Config.Fill` 改變；數字欄位小數位改變；刪除仍有值的欄位或區塊；停用或重新啟用公式、彙總欄位；換標題欄位或標題範本；改標題用到的選項名稱。

- [ ] **Step 1：寫失敗的測試**（各情境都先 `Submit` 一張採購單使其簽核中）
  - `UpdateFormula_WithPending_Returns409ListingRecord`：改 `NetTotal` 公式 → 409，訊息含該單的 `TitleSnapshot`。
  - `UpdateFormula_SixPending_ListsFiveAndCount`：6 張簽核中 → 訊息列 5 筆，並寫「等 6 筆」。
  - `AddFormulaField_WithPending_Returns409`。
  - `UpdateFill_WithPending_Returns409`：改 `Supplier` 的帶值對應。
  - `UpdateScale_WithPending_Returns409`：改 `Total` 小數位。
  - `DeleteFieldWithValues_WithPending_Returns409`；`DeleteEmptyField_WithPending_Succeeds`。
  - `DeleteBlockWithValues_WithPending_Returns409`（M6）。
  - `RenameOptionUsedInTitle_WithPending_Returns409`（M6）：標題範本用到的選項改名。
  - `ChangeTitleField_WithPending_Returns409`。
  - `UpdateDetailFormula_HeaderPending_Returns409`：改明細 `Line` 的公式，表頭簽核中 → 409（判斷 12）。
  - `UpdateExtraModuleFormula_ParentPending_Returns409`：改 `Extra` 模組公式（它彙總到採購單）→ 409。
  - `UpdateLabel_WithPending_Succeeds`、`AddPlainField_WithPending_Succeeds`。
  - `UpdateFormula_AfterApproved_NoPending_Succeeds`：實例已核准 → 200（交給 a2d3 的固定）。

- [ ] **Step 2：跑測試確認失敗**
  ```bash
  cd poc/backend && dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~PendingApprovalGuardTests"
  ```
  預期：409 類 FAIL。
- [ ] **Step 3：實作**：守衛放在既有 `immutable.RejectAsync` 之後、套用變更之前。
- [ ] **Step 4：跑測試確認通過**＋`--filter "FullyQualifiedName~FieldService|FullyQualifiedName~ClosedData|FullyQualifiedName~Immutable|FullyQualifiedName~DeleteBlock|FullyQualifiedName~UpdateOption|FullyQualifiedName~TitleRewrite"` 防回歸。預期 PASS。
- [ ] **Step 5：commit** `feat(approval): 有進行中實例時拒絕會改寫記錄值的定義變更`。

### Task 12：進行中實例用到的轉移與狀態受保護

**Files:**
- Modify: `PendingApprovalGuard.cs`（加方法）
- Modify: `SaveWorkflowHandler.cs`（`ApprovalErrorsAsync` 之前）、`DeleteWorkflowHandler.cs`、`UpdateOptionHandler.cs`（停用選項）
- Test: `PendingApprovalGuardTests.cs`

**Interfaces:**
- Produces：
  ```csharp
  public Task<ServiceResult?> RejectWorkflowChangeAsync(Module module, Workflow? candidate, CancellationToken ct);   // candidate null＝刪除狀態機
  public Task<ServiceResult?> RejectOptionDeactivateAsync(Module module, long optionId, CancellationToken ct);
  ```
  受保護集合見判斷 13。違反時回 409 `ApprovalMessages.WorkflowBlockedByPending(...)`，列出實例（同 Task 11 的格式）。

- [ ] **Step 1：寫失敗的測試**（`Scenario3` 送出一張單：草稿 → 已確認，簽核中）
  - `SaveWorkflow_RemovePendingTransition_Returns409`。
  - `SaveWorkflow_TurnOffApprovalOnPendingTransition_Returns409`。
  - `SaveWorkflow_RemoveRejectToTransition_Returns409`：流程 `RejectTo` 指向「已退回」，刪「草稿 → 已退回」→ 409。
  - `SaveWorkflow_RemovePendingStateOption_Returns409`。
  - `SaveWorkflow_ChangeStatusField_Returns409`。
  - `SaveWorkflow_UnrelatedTransitionAdded_Succeeds`。
  - `DeleteWorkflow_WithPending_Returns409`；`DeleteWorkflow_NoPending_Succeeds`。
  - `DeactivateOption_UsedByPending_Returns409`；`DeactivateOption_Unused_Succeeds`。
- [ ] **Step 2：跑測試確認失敗**（同 Task 11 指令）。預期新 409 類 FAIL。
- [ ] **Step 3：實作**。
- [ ] **Step 4：跑測試確認通過**＋`--filter "FullyQualifiedName~Workflow"` 防回歸。預期 PASS。
- [ ] **Step 5：commit** `feat(approval): 進行中實例用到的轉移與狀態不能刪改`。

### Task 13：a2d4 收尾

**Files:** `docs/manual.md`（設計器章節：有單據簽核中時哪些修改會被擋、要怎麼處理）、`docs/roadmap.md`。

- [ ] **Step 1：PG 雙跑**。預期 `Failed: 0`。
- [ ] **Step 2：gate**。預期整支 exit 0。
- [ ] **Step 3：走查**：前端沒有新元件，不做四寬度；1366 寬在 Demo 送出一張單後改公式，確認通知顯示 409 訊息與單據標題。
- [ ] **Step 4：commit** `docs(approval): a2d4 進行中實例守衛的手冊與進度`。

---

## a2d5：鎖定資料守衛、閉包重檢

### Task 14：整批解鎖與刪除鎖定欄位值的守衛（裁決 8、10）

**Files:**
- Create: `src/DynamicModules.Application/Dynamic/ApprovalService/LockedDataGuard.cs`（scoped）
- Modify: `SaveWorkflowHandler.cs`、`DeleteWorkflowHandler.cs`（排在 Task 12 的 `RejectWorkflowChangeAsync` 之後）
- Modify: `DeleteFieldHandler.cs`、`DeleteBlockHandler.cs`（排在 Task 11 的守衛之後，條件同 `plan.Check.Purge` 非空）
- Modify: `UpdateOptionHandler.cs`（停用選項）：先確認 `ApprovalTopology.LockedStates` 是否看選項啟用與否。會看的話，以停用後的狀態機呼叫 `RejectUnlockAsync`；不看的話，這一處不加，改寫一條測試證明停用不改變鎖定集合。
- Modify: `ApprovalMessages.cs`、`MessageText.cs`
- Test: `tests/DynamicModules.ApplicationTests/Dynamic/Approvals/LockedDataGuardTests.cs`（新檔，`ScopePerRequest => true`）

**Interfaces:**
- Consumes：Task 1 的 `ApprovalLockService.ForModuleAsync(module, wf, ct)`。
- Produces：
  ```csharp
  public sealed class LockedDataGuard(ApprovalLockService locks, IRecordRepository records, IModuleRepository modules)
  {
      public Task<ServiceResult?> RejectUnlockAsync(Module module, Workflow? candidate, CancellationToken ct);           // 裁決 8
      public Task<ServiceResult?> RejectPurgeAsync(Module module, IReadOnlyCollection<Field> fields, CancellationToken ct); // 裁決 10
  }
  ```
  - `RejectUnlockAsync`：`unlocked = 舊 lock.LockedStateIds − 新 lock.LockedStateIds`（舊的用 `module.Workflow`，新的用 `candidate`，`candidate` 為 null 時新集合為空）。以舊狀態機的 `StateOf` 數出停在 `unlocked` 的未刪除記錄（判斷 28）；大於 0 → 409 `ApprovalMessages.UnlockBlockedByRecords(count)`：「有 {0} 筆記錄停在會被解除鎖定的狀態；請先把它們轉回可編輯的狀態，或保留需簽核設定」。
  - `RejectPurgeAsync`：`fields ∩ lock.FieldIds` 非空，而且停在鎖定狀態的記錄中，有任何一筆在這些欄位有值（空陣列、null 不算）→ 409 `ApprovalMessages.PurgeBlockedByLocked(fieldLabel, count)`。本模組是明細、表頭的 lock 為 `DetailRowsLocked`、而且被刪欄位在任何一筆鎖定表頭的明細列上有值時，同樣 409（新待決 N2，預設照推薦）。

- [ ] **Step 1：寫失敗的測試**（`Scenario3`，先核准一張單使其停在已確認，沒有進行中實例）
  - `SaveWorkflow_TurnOffApproval_RecordsInLockedState_Returns409WithCount`（Review Focus 8）：關掉「草稿 → 已確認」的需簽核 → 409，訊息含「1 筆」。
  - `SaveWorkflow_MarkLockedStateEditable_Returns409`：把「已確認」標成可編輯 → 409。
  - `SaveWorkflow_ChangeStatusField_RecordsLocked_Returns409`。
  - `SaveWorkflow_TurnOffApproval_NoRecordsInLockedState_Succeeds`：先把那張單轉回草稿 → 200。
  - `SaveWorkflow_AddApprovalTransition_Succeeds`：鎖定集合變大 → 200。
  - `DeleteWorkflow_RecordsInLockedState_Returns409`。
  - `DeleteAttachmentField_LockedRecordHasFile_Returns409`（審查 Q5 的例子）：已確認單的附件欄位有檔 → 刪附件欄位 409。
  - `DeleteLockedField_LockedRecordsEmpty_Succeeds`：鎖定欄位在鎖定記錄上都沒值 → 200。
  - `DeleteUnlockedField_WithLockedRecords_Succeeds`：刪 `Memo`（不在鎖定集合）→ 200。
  - `DeleteBlock_ContainsLockedFieldWithValue_Returns409`。
  - `DeleteDetailField_HeaderLockedDetailRowsLocked_Returns409`（N2）。
  - `DeactivateOption_LockedStateWithRecords`：依 Files 的判準，寫成 409，或寫成「鎖定集合不變」。

- [ ] **Step 2：跑測試確認失敗**：`--filter "FullyQualifiedName~LockedDataGuardTests"`。預期 409 類 FAIL。
- [ ] **Step 3：實作**。
- [ ] **Step 4：跑測試確認通過**＋`--filter "FullyQualifiedName~Workflow|FullyQualifiedName~DeleteField|FullyQualifiedName~DeleteBlock|FullyQualifiedName~PendingApprovalGuard"`。預期 PASS。
- [ ] **Step 5：commit** `feat(approval): 有記錄停在鎖定狀態時，不能整批解鎖或刪除鎖定欄位的值`。

### Task 15：改公式、彙總、帶值後重檢閉包交集

**Files:**
- Create: `src/DynamicModules.Application/Dynamic/ApprovalService/ApprovalClosureRecheck.cs`（scoped）
- Modify: `UpdateFieldHandler.cs`（變更前取基準、套用後在 `:154` 整批重算之前比對）、`SetFieldStatusHandler.cs`（重新啟用）、`AddFieldHandler.cs`（`lock` 帶值目的檢查）
- Modify: `ApprovalMessages.cs`
- Test: `tests/DynamicModules.ApplicationTests/Dynamic/Approvals/ApprovalConstraintsTests.cs`

**Interfaces:**
- Consumes：既有 `ApprovalConstraints.Validate(Module, Workflow, IReadOnlyList<FlowState>)`、`IApprovalFlowRepository.ListCurrentByModuleAsync`，以及 `WorkflowApprovals` 列出全模組所有流程與版本定義的既有讀法。
- Produces：
  ```csharp
  public sealed class ApprovalClosureRecheck(IApprovalFlowRepository flows)
  {
      public Task<IReadOnlyDictionary<string, string[]>> BaselineAsync(Module module, CancellationToken ct);   // 變更前
      public Task<ServiceResult?> RejectNewAsync(Module module, IReadOnlyDictionary<string, string[]> baseline, CancellationToken ct);   // 只回差集，400
      public Task<ServiceResult?> RejectLockFillIntoEditableAsync(Module module, Field field, CancellationToken ct);   // 400，鍵 config.fill
  }
  ```
  模組沒有需簽核轉移時三者都直接回空或 null（判斷 14）。`RejectLockFillIntoEditableAsync`：`field.Config.Fill` 為 `FillConfig.Lock` 時，`Map.To ∩ 任何版本的 editableFields` 非空 → 400 `ApprovalMessages.FillTargetEditable(欄位名稱)`。

- [ ] **Step 1：寫失敗的測試**（流程條件引用 `NetTotal`，某關 `editableFields` 含一個數字欄位，以 rig 實際欄位為準，例如 `ApprovedAmount`；沒有進行中實例）
  - `UpdateFormula_PullsEditableFieldIntoClosure_Returns400`：`NetTotal` 公式改成引用那個可編輯欄位 → 400，錯誤列出路徑。
  - `UpdateFill_MapsIntoConditionField_FromEditableReference_Returns400`：可編輯欄位是參照欄位，新增帶值把它對應到 `Discount` → 400。
  - `ReactivateFormulaField_CreatingIntersection_Returns400`。
  - `UpdateFormula_NoIntersection_Succeeds`。
  - `UpdateFormula_ModuleWithoutApproval_SkipsCheck`：`Plain` 模組 → 200。
  - `UpdateFormula_PreexistingWorkflowViolation_NotReported`（S5）：測試直接寫入一個既有違規（例如 `transitions[...]` 類）→ 改一個無關公式 → 200。
  - `UpdateFormula_Intersection_NoRecalcPerformed`（S5）：回 400 時，模組內記錄的 `RowVersion` 都沒變。
  - `AddRefField_LockFillIntoEditableField_Returns400`、`UpdateFill_LockModeIntoEditableField_Returns400`（S5）。
- [ ] **Step 2：跑測試確認失敗**：`--filter "FullyQualifiedName~ApprovalConstraintsTests"`。預期新測試 FAIL。
- [ ] **Step 3：實作**（判斷 14）。
- [ ] **Step 4：跑測試確認通過**＋`--filter "FullyQualifiedName~FieldService"`。預期 PASS。
- [ ] **Step 5：commit** `feat(approval): 改公式或帶值使可編輯欄位落進條件閉包時回 400`。

### Task 16：a2d5 收尾

**Files:** `docs/manual.md`（設計器章節：有已核准的單據停在鎖定狀態時，哪些狀態機與欄位修改會被擋、要怎麼處理）、`docs/roadmap.md`。

- [ ] **Step 1：PG 雙跑**。預期 `Failed: 0`。
- [ ] **Step 2：gate**。預期整支 exit 0。
- [ ] **Step 3：走查**：前端沒有新元件，不做四寬度；1366 寬在 Demo 有已核准單的模組關掉需簽核，確認通知顯示 409 與筆數。
- [ ] **Step 4：commit** `docs(approval): a2d5 鎖定資料守衛與閉包重檢的手冊與進度`。

---

## a2d6：數值與開關政策（A3、A4、A12、A15）

### Task 17：登記政策、fixture、四語文案

**Files:**
- Modify: `src/DynamicModules.Domain/Policies/PolicyCatalog.cs`（登記表前 7 項，加進 `All`）
- Modify: `poc/shared/policy-catalog.json`（codes、categories 加 `approval`；範圍不在 fixture 裡，不用改）
- Modify: `poc/frontend/src/i18n/{zh-TW,en-US,th,vi}.json`（`policies.categories.approval`、`policies.items.<code>.name/help`）
- Test: `tests/DynamicModules.DomainTests/Policies/PolicyCatalogFixtureTests.cs`、`PolicyDefinitionTests.cs`；前端 `src/domain/dynamic/policy-i18n.vitest.spec.ts`

**Interfaces:**
- Produces：`PolicyCatalog.ApprovalMaxStages`、`ApprovalMaxApprovers`、`ApprovalMaxDueHours`、`ApprovalMaxCommentLength`、`ApprovalMaxManagerLevel`（**Module**，裁決 1）、`ApprovalAllowSelfApproval`、`ApprovalAdminBypassTransitionGroups`。代碼、範圍、出廠、上下限照登記表。
- 文案：
  - `approval.maxManagerLevel` 的 help 寫明「可依模組覆寫；調低只影響之後新增或修改的流程」。
  - 其他數值項的 help 寫明「調低只影響之後新增或修改的流程」。
  - `approval.maxApprovers` 另寫「固定指定的使用者名單在執行時只受技術上限 50 限制」（裁決 9）。
  - `approval.adminBypassTransitionGroups` 的 name 是「系統管理員可以略過狀態轉移的群組限制」，help 寫明「只管狀態轉移的群組限制；系統管理員的其他權限不受這一項影響」（裁決 11）。

- [ ] **Step 1：寫失敗的測試**：`PolicyDefinitionTests` 加四條：
  - `ApprovalPolicies_FactoryEqualsLegacyConstants`：出廠值依序為 10、20、8760、1000、5、false、true。
  - `ApprovalMaxCommentLength_Above1000_Invalid`：`Normalize("1001") == null`。
  - `ApprovalMaxManagerLevel_IsModuleScope_Max10`。
  - `ApprovalMaxApprovers_TechnicalMax50`。

  `PolicyCatalogFixtureTests` 不改；fixture 更新前它會 FAIL。前端 `policy-i18n` 加一條斷言：`approval.adminBypassTransitionGroups` 的 zh-TW name 等於裁決 11 的原文。
- [ ] **Step 2：跑測試確認失敗**：`cd poc/backend && dotnet test tests/DynamicModules.DomainTests --filter "FullyQualifiedName~Policy"`。預期 FAIL。
- [ ] **Step 3：實作** 登記、fixture、四語文案。
- [ ] **Step 4：跑測試確認通過**：同上，另跑 `cd poc/frontend && bunx vitest run src/domain/dynamic/policy-i18n.vitest.spec.ts src/i18n --maxWorkers=2`。預期 PASS。
- [ ] **Step 5：commit** `feat(policy): 登記簽核類政策`。

### Task 18：存流程時的上限改讀政策，只擋這次增加的

**Files:**
- Modify: `src/DynamicModules.Application/Dynamic/ApprovalService/ApprovalDefinitionValidator.cs`（`ValidateAsync(module, wf, d, ct)` 多一個 `ApprovalDefinition? previous`；注入 `ApprovalPolicies`；`:44 :63 :67 :72 :79` 與 `fallbackApprovers` 的 `SourceError`）
- Create: `src/DynamicModules.Application/Dynamic/ApprovalService/ApprovalPolicies.cs`（scoped）
- Modify: `ApprovalFlowHandlers.cs`（`ApprovalFlowEditor.ValidateAsync` 用 `self?.CurrentVersionId` 讀出目前版本定義，傳入 `previous`）
- Modify: `ApprovalMessages.cs`（`StagesCount`、`UsersCount`、`ManagerLevel`、`DueHours`、`PickRange` 改成帶數字的方法）
- Modify: `src/DynamicModules.Domain/Approvals/ApprovalDefinition.cs`（刪 `ApprovalLimits` 的 `MaxStages`、`MaxManagerLevel`、`MaxDueHours`；`MaxApprovers`、`MaxCommentLength` 留到 Task 19 刪）
- Modify: `src/DynamicModules.Domain/Organization/ManagerChain.cs`（`MaxLevel` 改成 `PolicyCatalog.ApprovalMaxManagerLevel.Max`）
- Test: `tests/DynamicModules.ApplicationTests/Dynamic/Approvals/ApprovalPolicyLimitsTests.cs`（新檔，`ScopePerRequest => true`）、DomainTests 的 `ManagerChain` 測試類

**Interfaces:**
- Produces：
  ```csharp
  public sealed record ApprovalLimitSet(int MaxStages, int MaxApprovers, int MaxDueHours, int MaxManagerLevel);   // Domain/Approvals
  public sealed class ApprovalPolicies(IPolicyReader policies)
  { public Task<ApprovalLimitSet> LimitsAsync(long moduleId, CancellationToken ct); }   // 四項都帶 moduleId 讀（maxManagerLevel 也是模組範圍）
  ```
  比較規則見判斷 16（關卡以 `Code` 對照，備援拿舊版 `FallbackApprovers` 比）。

- [ ] **Step 1：寫失敗的測試**
  - `SaveFlow_FactoryLimits_SameAsBefore`：11 關 → 400，訊息含「10」。
  - `SaveFlow_ModuleOverrideStages3_NewFlowWith4_Returns400`：訊息含「3」。
  - `SaveFlow_StagesOverLoweredLimit_Unchanged_Allowed`（Review Focus 4）：既有 5 關流程，覆寫調成 3，只改名稱再存 → 200。
  - `SaveFlow_StagesOverLoweredLimit_AddOne_Returns400`：加到 6 關 → 400。
  - `SaveFlow_ApproversPerStage_ExistingStageUnchanged_Allowed`、`SaveFlow_ApproversPerStage_Increased_Returns400`（以關卡 `Code` 對照）。
  - `SaveFlow_FallbackUsersOverLoweredLimit_Unchanged_Allowed`（S6）：備援 15 位使用者，上限調成 10，只改名稱再存 → 200；備援加到 16 位 → 400。
  - `SaveFlow_DueHoursRaisedOverLimit_Returns400`。
  - `SaveFlow_ManagerLevelModuleOverride3_Level4_Returns400`（M5）。
  - `SaveFlow_ManagerLevelOtherModule_UsesSystem5_Succeeds`（M5）：採購單覆寫 3，另一模組新關卡 5 層 → 200。
  - `SaveFlow_ManagerLevelOverrideWiderThanSystem_Succeeds`（M5）：覆寫 8、系統 5、新關卡 7 層 → 200。
  - `SaveFlow_ManagerLevelSystemLowered2_Level3_Returns400`：系統值 2、沒有覆寫 → 400，訊息含「2」。
  - `ManagerChain_ResolvesUpToTechnicalMax`（DomainTests）：層數 6 能解析。
- [ ] **Step 2：跑測試確認失敗**：`--filter "FullyQualifiedName~ApprovalPolicyLimitsTests"`（ApplicationTests）、`--filter "FullyQualifiedName~ManagerChain"`（DomainTests）。預期 FAIL。
- [ ] **Step 3：實作**；`ApprovalMessages` 的常數字串改方法，數字由參數帶入。
- [ ] **Step 4：跑測試確認通過**＋`--filter "FullyQualifiedName~Approval"`。預期 PASS。
- [ ] **Step 5：commit** `feat(policy): 簽核流程的關卡數、期限、主管層數上限改讀政策`。

### Task 19：執行期的人數與意見字數改讀政策

**Files:**
- Modify: `ApproverResolver.cs`（`ResolveContext` 加 `int MaxApprovers`；`ContextAsync` 依 `module.Id` 讀政策；`:83 :96` 人數上限）
- Modify: `ApprovalHandlers.cs:115-116`（自選名單人數）、`ApprovalPreviewer.cs:114`
- Modify: `ApprovalModels.cs:24`（`ApprovalActionRules.CommentError(comment, required, int maxLength)`）；呼叫點 `ApprovalHandlers.cs:32`、`ApprovalActionHandlers.cs:69 :170 :232` 以 `IPolicyReader.IntAsync(PolicyCatalog.ApprovalMaxCommentLength, null, ct)` 讀值傳入
- Modify: `MessageText.cs`（`ApprovalTooManyApprovers`、`ApprovalPickCount`、`ApprovalCommentTooLong` 改帶參數）
- Modify: `ApprovalDefinition.cs`（刪 `ApprovalLimits` 剩下的兩個常數，類別一併刪除）
- Modify: 直接 `new ResolveContext(...)` 的測試：`ApproverResolverTests.cs:37`、`ApprovalRuntimeTextTests.cs:69 :92`。新參數不給預設值，漏一處就編不過，不會落回 fail-open 的出廠值（S7）。
- Test: `ApprovalPolicyLimitsTests.cs`

**Interfaces:**
- Consumes：Task 18 的 `ApprovalPolicies.LimitsAsync`。
- Produces：`ResolveContext(..., int MaxApprovers, ...)`，由 `ApproverResolver.ContextAsync` 填入；5 個產生點（`ApprovalHandlers.cs:54`、`ApprovalEngine.cs:61 :155 :185`、`ApprovalPreviewer.cs:91`）都經 `ContextAsync`，不各自讀。人數上限的選法見判斷 17：
  ```csharp
  var limit = OnlyStaticUsers(source, fallback, usedFallback) ? PolicyCatalog.ApprovalMaxApprovers.Max : ctx.MaxApprovers;
  ```

- [ ] **Step 1：寫失敗的測試**
  - `Resolve_GroupOverModuleMaxApprovers_UsesPolicy`：模組覆寫 `maxApprovers = 2`，群組 3 人 → 行為與現行「超過 20 人」相同，訊息含「2」。
  - `Resolve_StaticUsersOverLoweredLimit_UsesTechnicalMax`（裁決 9）：某關固定 15 人（上限 20 時存檔），覆寫調成 10 → 送出成功，該關 15 個任務。
  - `Resolve_LoweredAfterSubmit_NextStageUsesCurrent`（裁決 2）：送出後才把覆寫調成 5，第二關群組 8 人 → 第一關同意時回 409，訊息含「5」。
  - `Pick_OverPolicyLimit_Returns400`。
  - `Comment_OverSystemLimit_Returns400`：系統值 10，意見 11 字 → 400，訊息含「10」；10 字 → 200。四個動作（送出、同意、退回、撤回）各跑一次。
  - `Comment_FactoryLimit_1000`：1001 字 → 400。
- [ ] **Step 2：跑測試確認失敗**。預期 FAIL。
- [ ] **Step 3：實作**。
- [ ] **Step 4：跑測試確認通過**＋`rg -n "ApprovalLimits" poc/backend/src` 無輸出＋`--filter "FullyQualifiedName~Approval"`。預期 PASS。
- [ ] **Step 5：commit** `feat(policy): 簽核人數與意見字數改讀政策`。

### Task 20：A12 發起人能否簽自己的單

**Files:** `ApproverResolver.cs`（`ResolveContext` 加 `bool AllowSelfApproval`，`ContextAsync` 讀；`Clean()`、`:152-153`）、`ApprovalHandlers.cs:113`（自選名單不能含發起人）、`:131`（`UserFieldIsInitiator`）、`ApprovalPreviewer.cs:112`；Task 19 列的三個測試檔同步加參數；Test：`tests/DynamicModules.ApplicationTests/Dynamic/Approvals/ApprovalSelfApprovalTests.cs`（新檔，`ScopePerRequest => true`）。

**Interfaces:**
- Produces：`ResolveContext` 加 `bool AllowSelfApproval`（`ContextAsync` 依 `module.Id` 讀 `PolicyCatalog.ApprovalAllowSelfApproval`）。`false` 時行為與現行完全相同。

- [ ] **Step 1：寫失敗的測試**
  - `SelfApproval_Factory_InitiatorRemoved`：fin1 發起、關卡群組 FinanceGroup（fin1、fin2）→ 任務只給 fin2。
  - `SelfApproval_Allowed_InitiatorKeptAndMustAct`（裁決 3）：模組覆寫 true → 任務給 fin1、fin2；或簽時 fin1 要自己按同意才推進，沒有自動同意的歷程。
  - `SelfApproval_Allowed_PickIncludesInitiator_Succeeds`；`SelfApproval_Factory_PickIncludesInitiator_Returns400`。
  - `SelfApproval_Allowed_UserFieldIsInitiator_Succeeds`。
  - `SelfApproval_Allowed_ManagerSourceStillGoesUp`：主管來源 level 1 仍是發起人的主管。
  - `Preview_SelfApprovalAllowed_ListsInitiator`。
- [ ] **Step 2：跑測試確認失敗**：`--filter "FullyQualifiedName~ApprovalSelfApprovalTests"`。預期 `_Allowed_` 類 FAIL。
- [ ] **Step 3：實作**。
- [ ] **Step 4：跑測試確認通過**＋`--filter "FullyQualifiedName~Approval"`。預期 PASS。
- [ ] **Step 5：commit** `feat(policy): 發起人能否簽自己的單改讀政策`。

### Task 21：A15 系統管理員略過轉移群組限制

**Files:**
- Create: `src/DynamicModules.Application/Dynamic/WorkflowService/TransitionAccess.cs`（scoped）
- Modify: `WorkflowEnforcer.cs:56`、`:93`，`ApprovalQueryHandlers.cs:40`，`ApprovalPreviewer.cs:52`
- Test: `tests/DynamicModules.ApplicationTests/Dynamic/Workflow/TransitionAccessTests.cs`（新檔，`ScopePerRequest => true`；資料夾以既有 Workflow 測試位置為準）

**Interfaces:**
- Produces：
  ```csharp
  public sealed class TransitionAccess(IPolicyReader policies)
  { public Task<bool> BypassGroupsAsync(PermissionInputs inputs, CancellationToken ct); }   // inputs.User.IsSystemAdmin 且政策為 true
  ```
  系統身分在 `WorkflowEnforcer.cs:90` 已先放行，這裡不再處理（判斷 19）。

- [ ] **Step 1：寫失敗的測試**
  - `Admin_Factory_BypassesTransitionGroups`：系統管理員走限定 PurchaseLeadGroup 的轉移 → 200（現行）。
  - `Admin_PolicyOff_RespectsTransitionGroups`：系統值 false → 400，鍵為狀態欄位，訊息 `WorkflowNotPermitted`（nit）。
  - `Admin_PolicyOff_SubmitTargetsExcludeRestricted`：`SubmitTargetsAsync` 不列出限定群組的轉移。
  - `Admin_PolicyOff_PreviewRespectsGroups`。
  - `Admin_PolicyOff_StillHasModulePermissions`：同一位管理員仍能 PUT 一般欄位（證明只管轉移群組，對應裁決 11 的說明）。
  - `SystemIdentity_PolicyOff_StillBypasses`：事件推動的轉移照舊放行（防回歸）。
- [ ] **Step 2：跑測試確認失敗**：`--filter "FullyQualifiedName~TransitionAccessTests"`。預期 `PolicyOff` 類（除 `StillHasModulePermissions`）FAIL。
- [ ] **Step 3：實作**：4 處改呼叫 `BypassGroupsAsync(inputs, ct)`。
- [ ] **Step 4：跑測試確認通過**＋`--filter "FullyQualifiedName~Workflow|FullyQualifiedName~Approval"`。預期 PASS。
- [ ] **Step 5：commit** `feat(policy): 系統管理員略過轉移群組改讀政策`。

### Task 22：a2d6 收尾（政策頁四寬度）

**Files:**
- `docs/manual.md`「系統管理：政策設定」：補簽核分類七項。A15 用新名稱；寫明固定名單用技術上限 50、群組等動態來源照當下設定（裁決 9）。
- 模組設計「政策」分頁說明：補模組範圍的**五項**：關卡數、每關人數、期限時數、主管層數、自簽。
- `docs/roadmap.md`。

- [ ] **Step 1：PG 雙跑**。預期 `Failed: 0`。
- [ ] **Step 2：gate**。預期整支 exit 0。
- [ ] **Step 3：四寬度走查**：`/system/policies` 與採購單模組設計的「政策」分頁，390／820／1366／1920 各看一次。確認：
  - 簽核分類出現，模組分頁列出五項（含主管層數）。
  - A15 顯示新名稱；切英文看一次，四語文案不缺。
  - 在模組分頁覆寫 `approval.maxManagerLevel` 存檔成功。
  - `rwd-check.js` 的 `overflow == false`。
- [ ] **Step 4：commit** `docs(policy): a2d6 簽核類政策的手冊與進度`。

---

## a2d7：收件人政策（A10、A11）與總收

### Task 23：`Recipients` 型別、API 檢查、刪群組守衛

**Files:**
- Modify: `src/DynamicModules.Domain/Policies/PolicyDefinition.cs`（`PolicyKind.Recipients`、`Normalize`、`Clamp`；加 `static IReadOnlyList<string> RecipientItems(string normalized)` 拆項目）
- Modify: `PolicyCatalog.cs`（登記表後 2 項）、`poc/shared/policy-catalog.json`、四語 i18n
- Modify: `src/DynamicModules.Application/Dynamic/PolicyService/PolicyHandlers.cs`（PUT 檢查存在且啟用；`EffectivePolicies` 排除 Recipients）、`PolicyModels.cs`（`PolicyJson` 的 ToJson／FromJson：Recipients 當字串陣列；`HasRange` 含 Recipients）、`PolicyMessages.cs`
- Modify: `IPolicyReader`／`PolicyReader.cs`（`ListAsync` 接受 Recipients）
- Modify: `src/DynamicModules.Application/Dynamic/AccountService/Commands/DeleteGroup/DeleteGroupHandler.cs`
- Test: `PolicyDefinitionTests.cs`、`tests/DynamicModules.ApplicationTests/Dynamic/Policies/`（既有政策 handler 測試類）、DeleteGroup 既有測試類

**Interfaces:**
- Produces：
  - `PolicyKind.Recipients`；標準字串規則見判斷 20。
  - `PolicyCatalog.ApprovalDefaultFallbackApprovers`（Module，出廠 `admins`，1–20）、`ApprovalAlertRecipients`（System，出廠 `admins`，1–20）。
  - 刪群組檢查用既有的 `ListByPrefixAsync("policy.")` 與模組覆寫的列出方法，在 handler 內用 `RecipientItems` 拆項目比對（判斷 22）。若列出全部模組覆寫沒有現成方法，加 `Task<List<ModulePolicy>> ListByCodesAsync(IReadOnlyCollection<string> codes, CancellationToken ct)`，測試掛 `Category=Postgres`。

- [ ] **Step 1：寫失敗的測試**
  - Domain：
    - `Recipients_Normalize_DedupesKeepsOrder`：`"user:fin1,admins,user:fin1"` → `"user:fin1,admins"`。
    - `Recipients_Normalize_RejectsBadSyntax`：`group:abc`、`user:A B`、`role:x`、空字串 → null。
    - `Recipients_Normalize_Over20_Null`。
    - `RecipientItems_SplitsExactly`：`group:12` 拆出來不等於 `group:1`。
  - Application：
    - `PutRecipients_UnknownGroup_Returns400ListingItem`、`PutRecipients_UnknownUser_Returns400`。
    - `PutRecipients_DisabledUser_Returns400`（N3）。
    - `PutRecipients_Valid_Persists`。
  - `EffectivePolicies_ExcludesRecipients`：回應沒有 `approval.defaultFallbackApprovers`、`approval.alertRecipients`。
  - 刪群組：
    - `DeleteGroup_ReferencedBySystemPolicy_Returns409`、`DeleteGroup_ReferencedByModuleOverride_Returns409`。
    - `DeleteGroup_PrefixLookalike_Succeeds`：政策引用 `group:12` 時，刪群組 1 → 200。
    - `DeleteGroup_Unreferenced_Succeeds`。
- [ ] **Step 2：跑測試確認失敗**：`--filter "FullyQualifiedName~Policy|FullyQualifiedName~DeleteGroup"`（DomainTests、ApplicationTests）。預期 FAIL。
- [ ] **Step 3：實作**。
- [ ] **Step 4：跑測試確認通過**；PG 雙跑同 filter（若加了 repository 方法）。預期 PASS。
- [ ] **Step 5：commit** `feat(policy): 新增收件人型別政策與刪群組守衛`。

### Task 24：A10 備援對象、A11 異常通知對象改讀政策

**Files:**
- Create: `src/DynamicModules.Application/Dynamic/PolicyService/RecipientResolver.cs`（scoped）
- Modify: `ApproverResolver.cs`（`ContextAsync` 的 `ActiveAdmins` 改為 `DefaultFallback`，依 `module.Id` 讀政策並解析；`FallbackUsers`、`ResolveFallback`、`:39 :95 :158`）
- Modify: `ApprovalEngine.cs:155-156`（重新比對的備援實例，S7 更正行號）、`ApprovalAlert.cs:32-34`
- Modify: Task 19 列的三個測試檔（`ResolveContext` 參數改名）
- Test: `tests/DynamicModules.ApplicationTests/Dynamic/Approvals/ApprovalRecipientPolicyTests.cs`（新檔，`ScopePerRequest => true`）

**Interfaces:**
- Produces：
  ```csharp
  public sealed class RecipientResolver(IPermissionRepository permissions, ILogger<RecipientResolver> logger)
  { public Task<IReadOnlyList<string>> ResolveAsync(IReadOnlyList<string> items, CancellationToken ct); }   // 展開成啟用中使用者 Id，去重、排序；不存在的 id 略過並記 Warning
  ```
  `ResolveContext.DefaultFallback` 只在流程定義的 `FallbackApprovers` 為 null 時使用，屬於動態來源（判斷 17）。發起人排除照 A12（Task 20）。A11 解析為空時改送全部啟用中系統管理員並記 Warning（判斷 23）。

- [ ] **Step 1：寫失敗的測試**
  - `Fallback_Factory_AllAdmins`：現行行為。
  - `Fallback_ModuleOverrideGroup_UsesGroupMembers`：覆寫 `group:<FinanceGroup>` → 任務給 fin1、fin2。
  - `Fallback_FlowDefinitionFallback_TakesPrecedence`。
  - `Fallback_OnlyDisabledUser_Rejects409`（Review Focus 5）：先存 `user:fin1`，再停用 fin1。
  - `Fallback_ChangedAfterSubmit_AffectsOnlyLaterStages`：送出後才改政策，已建立的任務不變，下一關找不到人時用新值。
  - `Fallback_RematchFallbackInstance_UsesPolicy`：重新比對產生的備援實例（`ApprovalEngine.cs:155`）也讀政策。
  - `Alert_SystemRecipientsUser_NotifiesOnlyThatUser`。
  - `Alert_OnlyDisabledRecipient_FallsBackToAdmins`（Review Focus 5）：記一筆 Warning、全部系統管理員收到。
- [ ] **Step 2：跑測試確認失敗**：`--filter "FullyQualifiedName~ApprovalRecipientPolicyTests"`。預期 FAIL。
- [ ] **Step 3：實作**。
- [ ] **Step 4：跑測試確認通過**＋`--filter "FullyQualifiedName~Approval"`。預期 PASS。
- [ ] **Step 5：commit** `feat(policy): 簽核備援對象與異常通知對象改讀政策`。

### Task 25：前端收件人編輯器

**Files:**
- Modify: `src/domain/dynamic/policy-types.ts`（`PolicyKind` 加 `'recipients'`；`policyValueText` 加選用參數 `names?: ReadonlyMap<string, string>`；`policyRangeText` 補 recipients「1–20 項」）
- Create: `src/presentation/policies/useRecipientNames.ts`（用 `useUsers`、`useGroups` 組出 `admins`／`group:<id>`／`user:<id>` → 顯示名稱）
- Modify: `src/presentation/policies/PolicyValueEditor.vue`（新增 `recipients` 分支：`q-select multiple use-chips`，選項＝「全部系統管理員」＋群組＋啟用中使用者，可搜尋，`label` 與 `aria-label` 都給）
- Modify: `src/pages/system/PoliciesPage.vue:63`、`src/presentation/policies/ModulePoliciesTab.vue:62`（傳 `names`）
- Modify: 四語 i18n（`policies.recipients.admins`、`policies.recipients.group`、`policies.recipients.user`、`policies.recipients.missing`＝「找不到或已停用」）
- Test: `src/presentation/policies/PolicyValueEditor.vitest.spec.ts`、`src/domain/dynamic/policy-types` 的既有 spec

**Interfaces:**
- Consumes：Task 23 的 API（Recipients 值是字串陣列）。
- Produces：`policyValueText(meta, v, t, names?)`；recipients 時把每項換成名稱，對不到的顯示 `policies.recipients.missing`。`GetUsersHandler` 只回啟用中使用者，所以停用帳號與不存在的帳號同樣顯示「找不到或已停用」，不另加 API（nit）。

- [ ] **Step 1：寫失敗的測試**
  - `renders recipients select with admins, groups and users`：mock 兩個群組、兩個使用者 → 選項 5 個，第一個是「全部系統管理員」。
  - `emits normalized items`：選 FinanceGroup → emit `['group:<id>']`。
  - `policyValueText shows names for recipients`：`['admins','user:fin1']` → 「全部系統管理員、<fin1 名稱>」。
  - `unknown recipient shows missing label`。
  - `policyRangeText shows item range for recipients`：→ 含「1」與「20」。
- [ ] **Step 2：跑測試確認失敗**：`cd poc/frontend && bunx vitest run src/presentation/policies src/domain/dynamic --maxWorkers=2`。預期 FAIL。
- [ ] **Step 3：實作**。
- [ ] **Step 4：跑測試確認通過**＋`bun run typecheck`。預期 PASS、無型別錯誤。
- [ ] **Step 5：commit** `feat(policy): 政策設定頁的收件人編輯器`。

### Task 26：a2d 總收（文件、設計寫回、四寬度、gate）

**Files:**
- `docs/roadmap.md`：a2d 列改「已完成」，附各子段 commit。
- `docs/feature-checklist.md` 的 a2d 列（Task 0 核對過的行號）：改 `[x]`，再跑 lifecycle 的 `feature_checklist.py docs/feature-checklist.md --write` 重算統計。
- `docs/manual.md`：政策設定章節補收件人政策（PUT 拒收不存在或已停用的帳號；停用後畫面顯示「找不到或已停用」）；簽核章節補「找不到簽核人時給誰」。
- `docs/superpowers/specs/2026-11-10-approval-workflow-design.md`：
  - §6.1 表：`level`（1 到 5）改成「1 到政策上限（`approval.maxManagerLevel`，可依模組覆寫，技術上限 10）」。§13.1 的「主管層級 1 到 5」同步改。
  - §8.5 表：「附件刪除、替換｜鎖定狀態的記錄」改成 400（判斷 9），「附件上傳」一列註明已記帳也是 409。
  - §8.6 末加兩點：整批解鎖守衛（裁決 8）與刪除鎖定欄位值守衛（裁決 10）。
  - §9.3 補：計算欄位兩道保留、解鎖轉移上的事件放行（裁決 7）。
  - §20.2 已知限制補兩條：(1) 簽核定義守衛與整批重算依賴全庫寫入序列化（SQLite `BEGIN IMMEDIATE`、pg0 的全庫 advisory lock），拿掉全庫鎖前要重新設計（S11）；(2) 判斷 30（N4）。
  - §20.4 補本計畫的使用者裁決 1–11 與技術判斷 3、8、9、10、12、13、25、26、27（標「主控暫定」）。
- `docs/superpowers/specs/2026-11-11-policy-settings-design.md`：
  - §4：A3、A4 兩列合併，範圍改成 M，主管層數註明「可依模組覆寫」。A15 名稱改為「系統管理員可以略過狀態轉移的群組限制」，說明只管轉移群組（裁決 11）。A3、A4、A10、A11、A12、A15 標已接上。
  - §10：補「可寫死清單（a2d 新增）」全部項目（見 Global Constraints）。
- `docs/superpowers/specs/2026-11-12-postgres-provider-design.md` §15 風險清單補一行「簽核定義守衛依賴全庫序列化」（S11）。

- [ ] **Step 1：PG 雙跑**（ApplicationTests、WebAPITests）。預期 `Failed: 0`。
- [ ] **Step 2：gate**。預期整支 exit 0。
- [ ] **Step 3：四寬度走查**：`/system/policies` 與採購單模組「政策」分頁的收件人編輯器，在 390／820／1366／1920 各操作一次（加一個群組、存檔、重整後顯示名稱）。確認 chips 換行不溢出（`rwd-check.js` 的 `overflow == false`），鍵盤可操作，輸入框有可讀名稱。模組分頁這時有簽核六項。
- [ ] **Step 4：formwright-purpose-check**：確認 a2d 的七段（鎖定寫入、附件、整批重算、進行中實例守衛、鎖定資料守衛、數值政策、收件人政策）與裁決 1–11 都對到設計條目。
- [ ] **Step 5：commit** `docs(approval): a2d 完成，裁決寫回設計與可寫死清單`。

---

## 待決

### 原待決 1–5：已裁決

原本的五題已由使用者在 2026-11-12 裁決，結果見檔尾「使用者裁決」第 1–5 條，其中第 1 條推翻原推薦，改成模組可覆寫。審查提出的 Q1–Q6 對應檔尾第 6–11 條。

### 新待決（使用者行為類，本計畫先照推薦寫，裁決不同時改對應 Task）

**N1. 整合以系統身分直接寫入時，同一次又解鎖又改鎖定欄位，要擋嗎？**
- 例子：外部系統呼叫整合，一次把採購單從「已確認」改回「草稿」，同時把金額從 1,000 改成 5,000。
- 推薦：**擋（400），與使用者自己存檔相同（裁決 4）**。只有解鎖轉移上的事件效果放行（裁決 7）。理由：裁決 7 放行的是設計者安排在轉移上的收尾動作；整合直接送來的值就是這次請求的輸入，放行等於讓整合繞過裁決 4。代價：整合要分兩次呼叫。
- 落點：判斷 25、Task 3。

**N2. 刪除明細模組的欄位，表頭停在鎖定狀態而且明細一起鎖時，要比照裁決 10 擋嗎？**
- 例子：採購單的條件引用「折扣後總額」，它由明細小計彙總而來，所以明細列跟著鎖。設計者刪掉明細的「規格說明」欄位，已核准採購單的明細規格說明會一起消失。
- 推薦：**擋（409）**，只在表頭的明細一起鎖時才擋。理由：§9.3「明細列跟著表頭一起固定」；裁決 10 的語意是核准過的內容不能消失。代價：設計者要先把那些單轉回可編輯狀態。
- 落點：Task 14。

**N3. 收件人政策儲存時引用已停用的帳號，要拒收還是存下？**
- 例子：管理員把異常通知對象設成 `user:fin1`，但 fin1 上週已停用。
- 推薦：**拒收（400），列出已停用的帳號**。理由：存進去只會在出事時才發現沒人收到；系統沒有「存檔警告」機制，拒收最直接。帳號存檔後才停用的情況，仍照判斷 23 處理。代價：要先恢復帳號才能設。
- 落點：判斷 20、Task 23。

**N4. 核准後改過公式，之後別的模組讓這張單的彙總重算時，公式套用新定義，要不要在 a2d 修？**
- 例子：採購單核准後，設計者把「折扣後總額」公式改成含稅。已核准的單由 a2d3 保留舊值。之後付款單新增一筆，採購單的「已付金額」彙總重算，這次重算會順便用新公式把「折扣後總額」也算成含稅。
- 推薦：**列為已知限制，a2d 不修**。理由：要修就得讓重算只動「依賴這個彙總」的公式，等於另寫一套相依求值；這個情況要同時具備「核准後改公式」與「別的模組的彙總流進同一張單」，而跨模組彙總會改變鎖定記錄，本來就是 §20.2 接受的限制。代價：極少數單據的鎖定金額會靜默變成新公式的結果，沒有歷程。
- 落點：判斷 30、Task 26。

### 技術類（直接決定）

- A15 只管「系統管理員略過轉移群組限制」；系統身分的略過寫死（判斷 19）。
- 鎖頭等畫面標示交給 a4b；本段只回 400 帶欄位鍵。
- 附件：上傳 409；以 PUT 刪除或替換，鎖定 400、已記帳 409（判斷 9）。
- 意見字數與期限時數的政策只能調低（技術上限 1000、8760）。
- 保留筆數只算被修改的模組（判斷 10）。
- 收件人類政策不出現在生效值 API（判斷 21）。
- 附件上傳冪等以「記錄、欄位、檔名、長度」雜湊（判斷 8）。
- 解鎖請求以日誌 Seq 的「輸入界線」分辨使用者輸入與事件效果（判斷 25）。
- 孤兒檔由控制器在失敗時清理（判斷 27）。
- 守衛掃描沿用 `ListAllByModuleAsync`（判斷 28）。
- 原 a2d4 拆成兩段（判斷 29）。
- 沒有 migration（判斷 1）。

---

## 自我檢查紀錄

1. **設計涵蓋**：
   - §8.5 鎖定欄逐列對到 Task 2–3（PUT、明細、事件、整合、SetNull／Cascade、彙總與公式放行）與 Task 5–6（附件）；匯入列為不適用並附理由。
   - §8.6：第 1、4 點已在 main；第 2 點 Task 12；第 3 點 Task 11；整批重算 Task 8–9；裁決 8、10 的延伸在 Task 14。
   - §9.2 末段 Task 15。
   - §9.3：鎖定欄位三類與附件 Task 1；計算欄位與訊息 Task 2–3；`FrozenRecords` 與明細 Task 8；保留筆數 Task 9。
   - 政策：A3、A4 Task 17–19；A12 Task 20；A15 Task 21；A10、A11 Task 23–24。
   - §12 通知已在 main，A11 只改收件對象。
2. **裁決對照**：
   - 裁決 1：登記表、判斷 15、Task 17、18、22、26。
   - 裁決 2：判斷 17、Task 19。
   - 裁決 3：Task 20。
   - 裁決 4：Task 3。
   - 裁決 5：Task 5、7。
   - 裁決 6：Task 6、7。
   - 裁決 7：判斷 25、Task 3。
   - 裁決 8：Task 14。
   - 裁決 9：判斷 17、Task 17、19、22。
   - 裁決 10：Task 14。
   - 裁決 11：Task 17、21、22、26。
3. **步驟掃描**：每個 Task 都有 Files、Interfaces、測試名稱與斷言、指令與預期結果、commit。依 rig 實際欄位調整的地方都已寫明判準：Task 3 的作廢簽核與收貨明細、Task 14 的選項停用、Task 15 的可編輯數字欄位、Task 21 的測試資料夾。
4. **型別一致**：
   - `ApprovalLock`（Task 1）→ Task 2、3、6、8、14 使用。
   - `ForModuleAsync(module, wf?, ct)`（Task 1）→ Task 14 傳候選。
   - `PendingInstanceRef`／`ListPendingByModulesAsync`（Task 8）→ Task 11、12。
   - `FrozenRecordsLoader`（Task 8）→ Task 9。
   - `IWriteJournal.InputSeqEnd`（Task 3）只在後盾讀。
   - `ApprovalLimitSet`／`ApprovalPolicies`（Task 18）→ Task 19。
   - `ResolveContext` 依序加 `MaxApprovers`（19）、`AllowSelfApproval`（20），再以 `DefaultFallback` 取代 `ActiveAdmins`（24）；三個測試檔每次同步。
   - `FieldResponse.PreservedRecordCount`（Task 9）前後端同名。
5. **Review Focus**：八項都已加進負責 Task 的測試。
6. **比例**：程式碼區塊只有介面簽章與一段演算法，其餘是測試名稱與斷言。

## 使用者裁決（2026-11-12，開工前以此為準）

1. 主管層數：A3 與 A4 合併成一個政策 `approval.maxManagerLevel`，**範圍 M（模組可覆寫）**，全系統出廠 5，技術上限寫死並列入可寫死清單。這推翻上方待決 1 的「只全系統」推薦；Task 14–16 照此實作，模組政策分頁要出現這一項並補模組覆寫的測試。
2. 每關人數上限：用下一關啟動當下的設定（照推薦，無 migration）。
3. 允許自簽時，發起人是簽核人要本人按同意（照推薦）。
4. 鎖定中同一次存檔轉回可編輯又改鎖定欄位：擋下 400（照推薦）。
5. 附件上傳觸發「記錄更新」事件、檢查、任務規則：接受，上線說明與手冊提醒設計者檢查條件（照推薦）。

## 使用者裁決（2026-11-12，審查 Q1–Q6）

6. 已記帳（事實鎖、分錄）的單據也不能增減附件，收檔前就回 409「已記帳的單據不能再增減附件」。
7. 解鎖轉移上的事件寫到鎖定欄位放行（最終狀態非鎖定、改動來自事件或系統身分）；使用者自己同次改鎖定欄位仍 400。
8. 存狀態機會讓有記錄停留的鎖定狀態變成不鎖定時 409，列出受影響筆數；列入可寫死清單（完整性守衛）。
9. 每關人數上限：固定名單（users）執行期只看技術上限 50；群組、主管、自選照當下設定。手冊寫明。
10. 有記錄在鎖定狀態、且欄位在鎖定集合中有值時，刪除欄位或區塊 409。
11. A15 範圍維持只管轉移群組限制，名稱與說明改為「系統管理員可以略過狀態轉移的群組限制」，政策設計 §4 一起改。

## 使用者裁決（2026-11-12，修訂後新待決 N1–N4）

12. N1：整合以系統身分直接寫入時，同次解鎖又改鎖定欄位視為本次輸入，回 400；只有設計者掛在轉移上的事件效果放行（判斷 25、Task 3）。
13. N2：表頭鎖定且明細一起鎖時，刪除明細欄位 409（Task 14）。
14. N3：收件人政策引用已停用帳號拒收 400，列出帳號（Task 23）。
15. N4：核准後改公式、之後跨模組彙總重算套用新公式，列為已知限制寫進設計 §20.2，a2d 不修（Task 26）。

## 使用者裁決（2026-11-12，排程變更，優先於頂端「阻塞中」與 Task 0 的 pg0 前提）

16. 簽核主線提前到 o1a 上線之後、pg0 之前（順序 o1a → a2d → a4b → a6 → a4a → a5 → a3）。a2d **不等 pg0**：Task 0 改成「rebase 到最新 main、重新核對行號與型別」，各段只驗 SQLite；計畫中的 PostgreSQL 雙跑步驟保留為「pg0 合併後補驗」清單，不擋各段合併。新增測試仍依 backend 規則避免 SQLite 專屬寫法。a2d 第一段同時改 docs/roadmap.md 的順序列與使用者裁決段。

## 排程變更（使用者 2026-11-12，第 17 條）

17. 合併順序改為 a2d1 → a4b → a6 → a2d2–a2d7 → a4a → a5 → a3。a2d1 單獨合併；a2d2 起在 a6 合併後再 rebase 開工。手冊 7.6 只寫已生效的保護。
