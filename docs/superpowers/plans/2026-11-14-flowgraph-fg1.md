# 簽核流程圖 fg1：圖形定義地基 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 替流程圖（fg2–fg6）打地基，上線後使用者看到的簽核行為完全不變：流程定義多一種 `shape: graph`（節點與連線）的 Domain 型別、sequence → graph 純函式轉換、結構驗證 G1–G13 與警告 W1–W3、前後端共用的判斷案例檔；閉包、鎖定欄位、刪除與停用守衛、群組參照改讀兩種形狀；一次 migration `ApprovalGraph` 加齊流程圖要用的所有欄位；新政策 `approval.maxBranches`；後端允許流程列沒有已發布版本（設計 §10.8 列的程式假設）。API、套件、執行期三道都仍拒收 graph。

**Architecture:** Domain（`Domain/Approvals/`）放型別、轉換、參照讀取函式、範本語法、結構驗證（回「錯誤鍵＋規則＋代碼」，不帶訊息）；Application 的 `ApprovalDefinitionValidator` 以 partial 檔加 graph 分支（條件語法、簽核節點屬性、通知節點參數、政策上限、訊息對照），最後固定加一則 `shape` 錯誤擋住（fg2 刪這一行就開放）。資料層只加欄位、不改既有資料。不新增 DI 註冊、不新增端點、不碰前端程式（只加政策的四語 i18n）。

**Tech Stack:** .NET 8、EF Core 8.0.11（SQLite；新 WebAPI 測試標 `Postgres` trait，不寫 SQLite 專屬語法；pg0 未合併，PostgreSQL 標「未驗證」）、MediatR、xUnit＋FluentAssertions；前端只動 `src/i18n/*.json`，驗 vitest。不新增相依套件。

**Spec:**
- `docs/superpowers/specs/2026-11-13-approval-flowgraph-design.md`（下稱「流程圖設計」）：§6.2 節點型別、§6.3、§7（資料模型，尤其 §7.1–§7.8）、§9 G1–G14 與 W1–W3、§10.8（只有草稿的流程、程式假設清單）、§12.1–§12.2（收件人與範本語法，只取驗證）、§13 第 1、7 點、§14 Domain 測試、§15 政策與可寫死清單、§16 介面預留、§17 fg1 列、§19、§20。
- `docs/superpowers/specs/2026-11-10-approval-workflow-design.md`（下稱「簽核設計」）：§4.3、§4.4、§5.2、§6.1、§8.6、§9.2、§9.3。
- `docs/superpowers/specs/2026-11-11-policy-settings-design.md` §3.5（改嚴只管之後）、§4 政策目錄。
- a3 計畫「與流程圖的銜接」：`../.tmp/plans/20261114-approval-a3/plan.md` 788–795 行。
- 樣板：`ApprovalDefinitionValidator`（逐關檢查）、`SharedRuleCasesTests`（讀 `poc/shared/` 案例）、`SharedMasterSchemaTests`（migrate 到前一版再升級的 schema 測試）、`PolicyDefinitionTests` 的 `ApprovalOverdueReminderHours_FactoryRangeAndScope`、`ApprovalTestBase.SeedFlow`。
- repo 規範：`AGENTS.md`、`.claude/rules/backend.md`、`docs.md`、`.claude/skills/formwright-purpose-check/SKILL.md`、`docs/ai/testing.md`。

**路徑慣例：** 後端相對 `poc/backend/`，前端相對 `poc/frontend/`，文件與腳本相對 repo 根目錄。建議 worktree：`../dynamic-fields-design.fg1`，每個子段一個分支 `feature/fg1a`…`feature/fg1d`，各自從當時的 origin/main 開。行號以 `8a1425ee` 讀到的為準，Task 0 重新核對。

---

## 待確認的行為決定

設計沒寫死、答案反過來使用者操作會不同的決定。實作照「推薦」進行，全部標 **主控裁決（待使用者確認）**；推翻時改對應 Task。fg1 本身沒有流程圖畫面，下列多數在 fg3、fg5 才看得到，但規則在 fg1 寫進驗證與守衛，所以現在要定。

**U1 新政策「每個分支節點的分支數上限」fg1 就出現在政策頁與模組的「政策」分頁。** 主控裁決（待使用者確認）
- 推薦：fg1 就登記到政策目錄（設計 §17 把政策排在 fg1），政策頁與模組政策分頁自動多一項；說明文字只寫意思，不寫「上線後生效」這類時程字眼。
- 理由：政策頁依目錄自動列出，要藏起來得另做「隱藏政策」機制，只為了幾週的空窗不值得；設定值存著，流程圖上線時直接生效。
- 選錯代價：fg2 之前系統管理員會看到一項改了沒有任何效果的設定；改成延後登記，要把政策搬到 fg2 或 fg3，fg1 的 G13 只能先吃參數。
- 例子：系統管理員打開「系統管理 → 政策設定 → 簽核」，看到「每個分支節點的分支數上限：5（範圍 2 到 10）」；改成 3 後存檔成功，現有的關卡式流程完全不受影響。

**U2 「關卡數」政策的名稱延到 fg3 才改成「簽核節點數（關卡數）」。** 主控裁決（待使用者確認）
- 推薦：fg1 不改 `policies.items.approval.maxStages.name`（維持「每個流程的關卡數上限」），fg3 設計器上線、畫面開始出現「節點」時再改。
- 理由：fg1 時使用者看到的仍是關卡卡片，政策頁先出現「節點」會讓人找不到對應的東西。
- 選錯代價：fg1 就改名，fg3 之前政策頁與設計器用詞不一致；延後則 fg3 要記得改（已寫進「與後續段的交接」）。
- 例子：fg1 上線後政策頁仍顯示「每個流程的關卡數上限：10」；fg3 上線後同一項顯示「每個流程的簽核節點數（關卡數）上限：10」。

**U3 只寫在草稿裡的群組與欄位不受保護，可以刪除或停用；發布時才擋。** 主控裁決（待使用者確認）
- 推薦：刪除群組、刪除或停用欄位的守衛只看已發布版本與進行中實例用到的版本（現行範圍），不讀 `DraftJson`。
- 理由：U6 定義草稿「不驗證」，畫到一半的草稿本來就可能不合法；若草稿也擋，一份沒人再碰的草稿會永遠卡住主檔的整理。發布時 §9 全部重驗，不會留下壞掉的版本。
- 選錯代價：不保護時，設計者回來發布草稿才發現群組不見，要改完才能發布；保護時，系統管理員刪群組被一份草稿擋下，還得找出是誰的草稿。
- 例子：草稿裡的簽核節點「法務」用群組「法務部」；系統管理員刪除「法務部」→ 成功。設計者按「發布」→ 節點「法務」標紅「群組至少要有一個，而且都必須存在」。

**U4 匯出應用程式套件時，只有草稿的流程不匯出；已發布流程只匯出已發布版本，未發布的變更不帶。** 主控裁決（待使用者確認）
- 推薦：匯出照現行只讀目前版本（`ListCurrentByModuleAsync` 維持只含有版本的流程）。
- 理由：套件是給別的環境開啟後直接用的，未驗證的草稿進套件會在對方開啟時才失敗；草稿是設計中的工作，不是應用程式內容。
- 選錯代價：不匯出時，設計者以為整包帶走、到新環境才發現少一條流程；匯出時，套件格式要多一種「草稿」並在開啟時處理不合法的內容（fg6 範圍變大）。
- 例子：設計者把「大額採購會簽」只存成草稿、從應用程式頁匯出 → 套件的 `approvalFlows` 沒有這條；已發布的「一般採購」有未發布的變更 → 套件裡是已發布的那一版。

**U5 流程圖至少要有一個簽核節點。** 主控裁決（待使用者確認）
- 推薦：發布時圖上沒有任何簽核節點 → 錯誤（G13 下限，同第一版「關卡必須有 1 到 N 關」）；個別路線不經簽核（例如小額直接結束）照設計只給 W2 警告。
- 理由：流程掛在「需簽核」的轉移上，整張圖沒有任何簽核節點等於「需簽核的轉移不必任何人簽」，和第一版的規則不一致；只想知會不想簽，應該把轉移設成不需簽核。
- 選錯代價：要求時，「只發通知、不簽核」的流程畫不出來；不要求時，設計者可能誤把整條流程做成自動通過。
- 例子：設計者畫「開始 → 通知（人事）→ 結束」掛在「已核准 → 已取消」上，按發布 → 「流程圖至少要有一個簽核節點」。畫「開始 → 條件（金額）→ 小額：結束／其他：主管簽核 → 結束」→ 可以發布，條件節點旁出現黃色警告「預設路線不經任何簽核就結束」。

**U6 節點名稱：簽核、通知節點必填繁中；條件、分支、匯合節點選填，沒填時畫面用型別名稱。** 主控裁決（待使用者確認）
- 推薦：照上述；開始、結束照設計可省略。
- 理由：簽核節點的名稱出現在時間軸、簽核中心、進度摘要（「簽核中（財務、法務）」），通知節點的名稱是範本的 `{node}`，空白就沒得顯示；條件、分支、匯合只在圖上出現，圖示與型別文字已足夠辨識（設計 §7.1 範例的匯合節點也沒有名稱）。
- 選錯代價：全部必填時，設計者每放一個匯合都要命名；全部選填時，簽核中心會出現沒有名稱的待簽。
- 例子：設計者放了分支節點沒命名 → 可以發布，圖上顯示「分支」；簽核節點名稱留空 → 發布時該節點標紅「節點名稱必須有繁體中文」。

**U7 允許空分支：分支節點可以直接連到它的匯合節點。** 主控裁決（待使用者確認）
- 推薦：結構上合法；在「任一到齊」的並行區裡照 W1 警告（等同「送出就通過」）；同一對節點之間不能有兩條線（G1），所以一個分支最多一條空分支。
- 理由：設計器放分支節點時會自動帶出兩條空分支（設計 §10.3），設計者常先發布部分分支；U13 已定「可能不經簽核就到達匯合只警告不擋」，空分支是它的特例。
- 選錯代價：允許時，「全部到齊」的空分支沒有作用、圖上多一條線；禁止時，設計者要先刪掉空分支才能發布。
- 例子：「法務與財務」分支多拉一條線直接接到「全部到齊」匯合 → 可以發布，執行時只等法務與財務；同樣的圖改成「任一到齊」→ 發布時黃色警告「有分支不經簽核就能到達匯合，送出時會立即通過」。

**U8 通知範本：不認得的 `{…}` 佔位符是錯誤；不成對的單獨大括號照字面顯示。** 主控裁決（待使用者確認）
- 推薦：`{title}`、`{flow}`、`{initiator}`、`{node}`、`{f:<數字>}` 是佔位符；其他「左大括號＋不含大括號的文字＋右大括號」一律報「不認得的佔位符」；單獨的 `{` 或 `}` 照字面。不提供跳脫語法。
- 理由：打錯佔位符（`{titel}`）若照字面送出，收件人會看到一串程式碼；設計器有「插入欄位」選單，正常情況不需要手打。單獨的大括號（例如表情符號 `:-}`）不構成佔位符，沒有理由擋。
- 選錯代價：嚴格時，真的想在通知裡寫「{備註}」這種字面的人做不到；寬鬆時，打錯的佔位符會原樣寄出。
- 例子：內文「申請金額 {f:21} 元，請備查。」→ 合法；「請 {approver} 確認」→ 發布時「不認得的佔位符 {approver}」；「收到 :-}」→ 合法、照字面顯示。

---

## Global Constraints

- **既有行為不變**：sequence 定義序列化後的 JSON 與 fg1 前逐字相同（新屬性為 null 時不寫出）；所有既有測試不改斷言就通過（唯一例外：建構 `ApprovalLimitSet` 的三個套件測試要多帶一個參數，見 Task 3）。
- **graph 三道都拒收**（設計 §17 fg1「API 仍拒收 graph」）：存檔與試算（`ApprovalDefinitionValidator` 對 graph 一律加 `shape` 錯誤）、套件（V8 既有「格式 8 只支援 sequence」）、執行期（送出、預覽、重新比對選到 graph 定義回 409，Task 4）。fg2 拆除第一道與第三道。
- **業務政策不寫死**（鐵則 2）：新政策 `approval.maxBranches` 登記在 `PolicyCatalog`（出廠 5、技術範圍 2–10，只寫在這裡），同步 `poc/shared/policy-catalog.json`、四語 `policies.items.approval.maxBranches.{name,help}`、`PolicyDefinitionTests`；Application 經 `ApprovalPolicies.LimitsAsync` 讀，Domain 驗證器只吃參數。技術上限集中在 Domain 的 `ApprovalGraphLimits`（可寫死清單）。
- **改嚴只管之後**（政策設計 §3.5）：簽核節點數、分支數、每個簽核節點的人數、逾期時數、主管層數，超過上限而且比舊版大才擋；舊版是 sequence 時先用 §7.8 轉換再比（節點代號＝關卡代號）。
- **fail closed**：結構驗證讀不到輸入（例如節點代號是 null）一律算錯；只有草稿的流程即使資料庫裡 `Active = true` 也不參與比對（沒有版本就不在比對集合裡）。
- **migration**：一個工作段最多一個，只有 fg1a 加；只用 `dotnet ef` 產生，不手改；產生後檢查 `Dynamic_ApprovalStageRun` 的重建（`BranchKey` 放寬）有保留既有資料。期間不和其他會加 migration 的工作並行（目前只有 a3g 在跑，它不加 migration）。
- **訊息**：使用者看得到的訊息集中在 `ApprovalMessages.cs`（後端訊息目前只有中文，沿用）；Domain 不寫訊息文字。
- **i18n**：只加 `policies.items.approval.maxBranches`；四語鍵集合一致、單行 JSON；th、vi 不留中文；改前從當時的 main 重讀檔案。
- **後端測試**：新 ApplicationTests 測試類 `protected override bool ScopePerRequest => true;`；新 WebAPI 測試標 `[Trait("Category", "Postgres")]`。只跑窄範圍 filter，全套交 gate（GitHub Actions）；不和其他吃 CPU 的工作同時跑 `dotnet test`。本機只驗 SQLite；PostgreSQL 標「未驗證」，pg0 合併後隨 pg0 的 baseline 補驗（pg0 設計 §5.3：PostgreSQL baseline 從當時的模型產生，會含 fg1 的欄位）。
- **公開 repo**：文件與程式不寫公司名、商用 BPM 品牌名、個人絕對路徑、內網 IP；案例檔與測試資料全部虛構。
- **git**：逐檔 `git add`、commit 前 `git status`；不用 `--no-verify`、不 force push、不 merge commit；Conventional Commits，scope `approval`、`policy`、`docs`；不 push，由主控合併。

### 可寫死清單（fg1 新增）

| 項目 | 值 | 類別 |
|---|---|---|
| 結構規則 G1–G8、G11、G12 | 拒收 400 | 完整性（流程圖設計 §15） |
| 同一分支節點的各分支不能共用節點、只能在配對的匯合會合 | 拒收 | 完整性（§6.2 J1 的直接要求，J8） |
| 流程圖至少一個簽核節點 | 拒收（U5） | 完整性：需簽核轉移必須有人簽 |
| 簽核、通知、開始各一條出線；條件恰一條預設線 | 拒收 | 完整性（§6.2） |
| 節點與線代號 `^[a-z][a-z0-9-]{0,39}$` | 拒收 | 技術限制，與關卡代號同一個常數 |
| 節點總數 100、線總數 200、分支巢狀 5 層 | 拒收 | 技術限制（§15），`ApprovalGraphLimits` |
| 通知收件人設定 1–20 項；範本標題 200 字、內文 1000 字 | 拒收 | 技術限制（§12.1、§12.2） |
| 不認得的範本佔位符 | 拒收（U8） | 格式 |
| `BranchKey` 256 字、`NodeKind`／`CloseReason` 20 字、`InEdgeId` 40 字 | 欄位長度 | 技術限制 |
| `approval.maxBranches` 技術範圍 2–10 | `PolicyCatalog` 的 `Min`／`Max` | 技術限制 |
| 沒有已發布版本的流程不參與比對、快照、入口約束、試算、匯出；PUT 回 409 | 固定 | 完整性（§10.8、J11） |
| graph 定義在 fg2 之前一律拒收（存檔 400、執行期 409） | 暫時 | 完整性：引擎還不會跑 graph（J10） |

---

## Review Focus

測試沒有覆蓋、但最可能咬人的六個情境；每條的測試已加進負責的子段。

1. **sequence 定義的 JSON 悄悄變了。** `ApprovalDefinition` 加了 `nodes`、`edges`，若沒有 `WhenWritingNull`，每份既有定義、快照、匯出都多出 `"nodes":null`，套件往返與 `SameAs` 的語意會跟著動。→ fg1b `Sequence_SerializesExactlyAsBefore`、`SameAs_SequenceRoundTripUnchanged`。
2. **只有草稿的流程漏進比對或約束。** 新的「列出全部流程」若被誤用在比對、快照、入口約束，會讓草稿參與送出或讓「目標狀態沒有流程」的檢查失效。→ fg1a `UnpublishedFlow_ActiveInDb_NotMatched_Direct`、`UnpublishedFlow_NotCountedForNoFlowConstraint`、`UnpublishedFlow_NotInDraftPreviewFlowSet`。
3. **優先序撞到只有草稿的流程變 500。** 現行優先序檢查只看已發布流程，唯一索引 `(ModuleId, ToOptionId, Priority)` 卻包含草稿流程。→ fg1a `Create_PriorityTakenByUnpublishedFlow_400`。
4. **鎖定或閉包漏掉條件節點的欄位**（少鎖＝簽核中可改條件欄位＝少簽）。→ fg1b `Lock_Graph_ConditionEdgeField_Locked`、`Constraints_Graph_EditableFieldInConditionEdgeClosure_400`。
5. **並行區判定接受交錯或分支私下會合。** 兩個分支的區域交錯、分支在匯合以外的節點會合、區外連線進區內，演算法常見的漏洞。→ fg1c 案例 `G7 crossing regions`、`G7 branches merge without join`、`G7 edge into region`、`G7 edge out of region`。
6. **migration 重建 `Dynamic_ApprovalStageRun` 時遺失資料。** SQLite 放寬欄位長度會重建資料表。→ fg1a `ApprovalGraphMigration_KeepsExistingStageRunsAndFlows`。

---

## 範圍確認

### 已在 main（只接、不重做）

| 項目 | 證據 |
|---|---|
| 流程定義型別：`ApprovalDefinition(Shape, When, RejectTo, FallbackApprovers, Stages)`、`ApprovalStageDef`、`ApproverSource`；常數 `ApprovalShapes.Sequence` | `Domain/Approvals/ApprovalDefinition.cs` |
| 定義存法：`ApprovalFlowVersion.Definition` 以 `HasJsonRecordConversion`（`JsonSerializerDefaults.Web`，null 照寫出）存成 JSON；流程快照 `ApprovalInstance.FlowSetSnapshot` 也含完整定義 | `Domain/Data/Configurations/ApprovalConfigurations.cs`、`JsonConversions.cs` |
| 定義比對 `ApprovalDefinitionJson.SameAs`（預設選項序列化後比字串） | `Domain/Approvals/ApprovalDefinitionJson.cs` |
| 閉包與參照：`FieldClosure.Upstream`、`DirectFields`（流程條件、關卡條件、`userField`）、`ReferencedFields`（再加可編輯欄位）；鎖定 `ApprovalTopology.Lock`（各關可編輯欄位＋閉包＋附件） | `Domain/Approvals/FieldClosure.cs`、`ApprovalTopology.cs` |
| 讀 `Stages` 的位置：約束 `ApprovalConstraints.cs:62、103`、閉包重檢 `ApprovalClosureRecheck.cs:44`、群組參照 `ApprovalRepositories.cs:92-97`、刪除守衛 `DeletePlanner.cs:271`、停用守衛 `SetFieldStatusHandler.cs:55`；引擎、路由、預覽、查詢、套件、Demo 另有約 12 處（fg2、fg4、fg6 的範圍） | grep `\.Stages\b` |
| 結構驗證：`ApprovalDefinitionValidator.ValidateAsync(module, wf, d, previous)`，`d.Shape != sequence` → 鍵 `shape`；逐關檢查在 57–77 行；代號規則 `CodePattern` 在 Application | `ApprovalService/ApprovalDefinitionValidator.cs` |
| 套件 V8 擋非 sequence | `AppService/AppPackageValidator.V8.cs:72` |
| 關卡實例已預留 `NodeId`（40）、`BranchKey`（40），程式沒有任何地方寫入；`ApprovalStageStatus.Queued` 已存在（a3 加簽用） | `Entities/Dynamic/Approval.cs:80-81`、`ApprovalConfigurations.cs:82-83` |
| 流程列 `CurrentVersionId` 是 `long?`；清單 `ListCurrentByModuleAsync` 以內部 join 連版本表（沒有版本的流程不會出現） | `ApprovalRepositories.cs:20-29` |
| 假設「一定有目前版本」的 `!.Value`：送出快照 `ApprovalHandlers.cs:42`、草稿試算 `ApprovalHandlers.cs:206`、預覽 `ApprovalPreviewer.cs:75`、流程回應 `ApprovalFlowHandlers.cs:75`、更新 `ApprovalFlowHandlers.cs:130`；優先序重複檢查只看已發布流程 `ApprovalFlowHandlers.cs:53-55` | 同上 |
| 啟停沒有獨立端點：啟停、改名、排序前的屬性都走 `PUT approval-flows/{key}`（`ApprovalFlowInput` 含必填 `Definition`）；排序 `PUT approval-flows/order` 讀全部列 `ListByTargetTrackedAsync` | `ApprovalFlowsController.cs`、`ApprovalFlowHandlers.cs` |
| 政策：`approval.maxStages`（A3）、`maxApprovers`、`maxDueHours`、`maxManagerLevel` 等；`ApprovalLimitSet(MaxStages, MaxApprovers, MaxDueHours, MaxManagerLevel)` 由 `ApprovalPolicies.LimitsAsync` 組；政策設計 §4 已用到 A36（A35 被任務看板設計占用） | `Domain/Policies/PolicyCatalog.cs:96-114`、`ApprovalPolicies.cs`、政策設計 §4 |
| 前後端共用案例的作法：`poc/shared/rule-cases.json` 由 `DomainTests/Rules/SharedRuleCasesTests.cs` 往上找 repo 根目錄讀取 | 同左 |
| DbContext 在 `Domain/Data/DynamicDbContext.cs`（不是 Persistence）；migration 與 design-time factory 在 `Persistence/`；`dotnet-ef` 8.0.11 是本地工具；最新 migration `20261114094620_RemoveMockCustomer` | `.config/dotnet-tools.json`、`Persistence/Migrations/` |
| schema 測試作法：`SqliteTestDb.CreateAsync(migrate: false)` → `IMigrator.MigrateAsync("<前一版>")` → 塞舊資料 → 升到最新 | `ApplicationTests/Persistence/SharedMasterSchemaTests.cs` |
| `Notification` 只有種類、標題快照、連結 | `Entities/Dynamic/Notification.cs`、`TaskConfigurations.cs:122-137` |
| a3：`ApprovalPath`（直線版）、加簽 `queued`＋`AddedByUserId`；fg1 不改 | `Domain/Approvals/ApprovalPath.cs`，a3 計畫 788–795 行 |

### 這次做

| 設計項目 | 子段 |
|---|---|
| §7.6 migration `ApprovalGraph`（含通知 `DataJson`）與實體屬性 | fg1a |
| §10.8 後端允許流程列沒有已發布版本（程式假設清單 fg1 的五項） | fg1a |
| §15 政策 `approval.maxBranches`（A37） | fg1a |
| 執行期 graph 守門（§2 反目標「fail closed」、§13 第 7 點的回退安全） | fg1a |
| §7.1–§7.4 Domain 型別；§16 欄位參照集中；§12.2 範本語法解析 | fg1b |
| §7.7 閉包、鎖定、刪除與停用守衛、群組參照讀 graph（G11 隨之成立） | fg1b |
| §7.8 sequence → graph 轉換 | fg1b |
| §9 G1–G8、G12、G13（Domain）＋共用案例檔 | fg1c |
| §9 W1–W3（Domain）；G6 條件語法、G9、G10、G13 讀政策（Application，仍拒收）；設計寫回、路線、清單 | fg1d |

### 不做

| 項目 | 理由 |
|---|---|
| 圖形執行引擎、節點實例 `passed`／`waiting` 狀態常數、`CloseReason` 值、`NodeKind` 寫入 | fg2（只有欄位在 fg1，J13） |
| API 接受 graph、草稿儲存與發布端點、`GET approval-flows/{key}` 回傳轉換後的 graph、`LayoutJson` 驗證 | fg2／fg3（設計 §10.7、§10.8；J11） |
| 前端即時驗證跑共用案例 | fg3（J6） |
| 通知節點的推進、收件人解析、逐人遮蔽、`DataJson` 內容 | fg5（fg1 只加欄位與驗證） |
| 套件格式升版、批次轉換、`app.schema.json` | fg6 |
| 政策「關卡數」改名 | fg3（U2） |
| 政策設計 §10 B27 改寫 | fg2（流程圖設計 §15 末段） |

---

## 子段切法與合併順序

| 子段 | 內容 | 估計行數（含測試，不含 EF 產生檔） | 畫面 | 依賴 |
|---|---|---|---|---|
| fg1a | migration `ApprovalGraph`＋實體欄位；只有草稿的流程（程式假設、回應可為空、PUT 409、優先序檢查）；執行期 graph 守門；政策 `approval.maxBranches` | 約 600（另有 EF 產生的 migration 約 150 行、Designer 與 snapshot 差異） | 政策頁多一項 | 無 |
| fg1b | Domain 型別、`ApprovalDefinitionRefs`、範本語法、轉換；閉包、鎖定、約束、守衛、群組參照改讀 graph | 約 800 | 無 | 無（與 fg1a 有共用檔，見下） |
| fg1c | Domain 結構驗證 G1–G8、G12、G13、技術上限；`poc/shared/approval-graph-cases.json` 與案例測試 | 約 800（其中案例 JSON 約 350） | 無 | fg1b |
| fg1d | W1–W3；Application 驗證器 graph 分支（G6 語法、G9、G10、G13 政策、訊息對照、`shape` 擋門）；設計寫回、路線、清單 | 約 750 | 無 | fg1a（政策）、fg1c |

合計約 2950 行，超過 800，所以切四段。每段合併後既有行為不變：

- fg1a：資料庫多出欄位（全部可為空）；流程清單 API 的 `version`、`definition` 型別改成可為空並多 `draft`（目前不會出現 null，前端不受影響）；政策頁多一項。
- fg1b：鎖定、閉包、守衛的結果對 sequence 定義完全相同（回歸測試）；graph 只存在於測試。
- fg1c：純新增 Domain 檔與案例檔，沒有呼叫端。
- fg1d：存檔與試算送 graph 仍回 400，只是錯誤更完整（附各節點的錯誤，再加 `shape`）。

**合併順序：** 預設 fg1a → fg1b → fg1c → fg1d。

- 想平行時可開兩線：線 A fg1a；線 B fg1b → fg1c；fg1d 等兩線都合併。fg1a 與 fg1b 的共用檔是 `ApprovalDefinition.cs`（fg1a 改檔尾的 `ApprovalLimitSet`、`FlowState`，fg1b 改檔首的 `ApprovalDefinition` 與新增常數）、`ApprovalRepositories.cs`（不同方法）、`IApprovalRepositories.cs`；後合併的那條 rebase 時手動解，不會有語意衝突。照使用者記憶「Formwright 長任務用 subagent、不開 pane」，平行時用兩個 worktree＋兩個 subagent；只開一個 worktree 就照預設順序。
- fg1c 只新增檔案，fg1b 合併後可以和 fg1a 平行（沒有共用檔）。

---

## 共用檔與 a3g

**fg1 會碰的共用檔：**

| 類別 | 檔案 | 子段 |
|---|---|---|
| DbContext 與 EF | `Domain/Data/Configurations/ApprovalConfigurations.cs`、`TaskConfigurations.cs`（`NotificationConfiguration`）、`Persistence/Migrations/<時間>_ApprovalGraph*.cs`、`Persistence/Migrations/DynamicDbContextModelSnapshot.cs`；`DynamicDbContext.cs` 不必改（沒有新表） | fg1a |
| 實體 | `Domain/Entities/Dynamic/Approval.cs`、`Notification.cs` | fg1a |
| 政策 | `Domain/Policies/PolicyCatalog.cs`、`poc/shared/policy-catalog.json`、`ApprovalPolicies.cs` | fg1a |
| i18n | `poc/frontend/src/i18n/{zh-TW,en-US,th,vi}.json`（只加 `policies.items.approval.maxBranches`） | fg1a |
| 共用案例 | 新檔 `poc/shared/approval-graph-cases.json` | fg1c、fg1d |
| 測試基底 | `ApplicationTests/Dynamic/Approvals/ApprovalTestBase.cs`（加 `SeedUnpublishedFlow`） | fg1a |
| 文件 | `docs/manual.md`（政策段 776、785 行附近）、政策設計 §4、流程圖設計 §15／§17／§19／§20、`docs/roadmap.md`（fg1 列）、`docs/feature-checklist.md`（fg1 行與統計行） | fg1a、fg1d |
| 後端 DI 註冊 | 不碰（新 Domain 類別是 static；Application 只改既有類別，`ApprovalDefinitionValidator` 以 partial 檔擴充） | — |
| 前端路由、選單 | 不碰 | — |
| `poc/backend/apps/*.app.json` | 不碰（套件格式 fg6 才改；新政策沒有套件覆寫） | — |

**與 a3g 的關係：** a3g 目前在 `../dynamic-fields-design.a3`（分支 `feature/a3g`）改 `DemoService/DemoApprovalSteps.cs`、`Scenarios/InventoryScenario.cs`、`Demo/InventoryPoApprovalScenarioTests.cs`，之後會改 `docs/demo.md`、`docs/manual.md` §21、簽核設計 §20.4、`docs/feature-checklist.md`、`docs/roadmap.md`。

- **程式沒有共用檔**：fg1 不碰 Demo 服務與 Demo 測試。`DemoApprovalSteps` 讀 `def.Stages`，fg1 保持 `Stages` 非 null（J2），a3g 不受影響。
- **文件有共用檔**：`docs/manual.md`（a3g 改 §21，fg1a 改政策段，不同區段）、`docs/roadmap.md`（不同列）、`docs/feature-checklist.md`（不同行，但統計行一定衝突）。後合併的一方 rebase 後重跑 `feature_checklist.py docs/feature-checklist.md --write`，不手改統計行。
- fg1 不改簽核設計（流程圖設計 §20.1 的修正排在 fg 上線時），所以和 a3g 的簽核設計 §20.4 寫回不衝突。
- a3g 不加 migration，fg1a 的 migration 不必等它。

---

## 計畫判斷（技術類，主控決定，可推翻）

- **J1 一次 migration 放在第一段。** fg1a 加齊 §7.6 全部欄位：`Dynamic_ApprovalFlowVersion.LayoutJson`（text，可空）、`Dynamic_ApprovalFlow.DraftJson`（text，可空）、`DraftUpdatedBy`（50，可空）、`DraftUpdatedTime`（可空）、`Dynamic_ApprovalStageRun.NodeKind`（20）、`InEdgeId`（40）、`CloseReason`（20）、`BranchKey` 40 → 256、`Dynamic_Notification.DataJson`（text，可空）。JSON 欄位不設 `HasMaxLength`（大小上限 64 KB／256 KB 是 fg3 驗證器的技術限制，同 `Definition` 不設長度）。理由：設計 §17 要求一次加齊；放第一段讓之後三段都不碰 EF。代價：fg2 之前有幾個欄位永遠是 null。
- **J2 `Stages` 維持非 null；graph 的 `stages` 是空陣列。** 專案 `TreatWarningsAsErrors`，把 `Stages` 改成可為空要動 fg2 範圍的十幾處 sequence 程式。做法：positional record 重新宣告 `public List<ApprovalStageDef> Stages { get; init; } = Stages ?? [];`，反序列化缺 `stages` 時得到空清單；graph 寫出時是 `"stages": []`。新屬性 `Nodes`、`Edges` 預設 null，加 `[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]`，sequence 的 JSON 與 fg1 前逐字相同。設計 §7.1「graph 時沒有 stages」讀成「stages 必須為空」（G1 檢查）。
- **J3 節點用單一扁平 record，以 `Kind` 分派。** `ApprovalNodeDef(Id, Kind, Name?, When?, Approvers?, Mode?, EditableFields?, DueHours?, OnEmpty?, ForkId?, Recipients?, Template?)`，可選屬性都 `WhenWritingNull`；簽核節點以 `AsStage()` 轉成既有 `ApprovalStageDef`，驗證與（fg2 的）解析直接重用。JSON 上匯合節點的模式欄位名照設計是 `mode`，與簽核節點的 `mode` 共用同一個屬性 `Mode`（值域依 kind 不同：簽核 `all`／`any` 代表會簽或簽，匯合 `all`／`any` 代表全部到齊或任一到齊），不另開 `JoinMode`。理由：多型 JSON（`JsonDerivedType`）要求判別欄位排第一、還要每種節點一個型別，對驗證器與設計器沒有好處。
- **J4 欄位與群組參照集中在 `ApprovalDefinitionRefs`**（設計 §16 第二點）：`Steps(d)`（sequence 的關卡或 graph 的簽核節點，一律 `ApprovalStageDef`）、`Conditions(d)`（流程條件、各步驟條件、條件節點出線條件）、`NotifyFieldRefs(d)`、`GroupRefs(d)`（簽核人、候選、備援、通知收件人的群組）。`FieldClosure`、`ApprovalTopology.Lock`、`ApprovalConstraints`、`ApprovalClosureRecheck`、`ApprovalFlowRepository.References` 全部改讀它。
- **J5 轉換 `ApprovalGraphConversion.FromSequence`**：照設計 §7.8；輸入已是 graph 時原樣回傳（「可重複執行」）；開始、結束節點衝突時依序試 `start-0`、`start-1`…；線代號 `e1`… 依序；節點與線代號是兩個命名空間（錯誤鍵已用 `nodes.`、`edges.` 區分），所以關卡代號 `e1` 不影響線代號。
- **J6 Domain 驗證器只回「鍵、規則、代碼」。** `GraphIssue(Key, Rule, Code)`；訊息在 Application 的 `ApprovalMessages.Graph(code)` 對照。共用案例檔斷言 `(key, rule, code)` 的集合，與語言無關，前端 fg3 照同一份跑。fg1 只有後端跑案例檔（前端還沒有驗證器，不寫投機程式）。
- **J7 規則分層。** Domain：G1–G8、G12、G13（政策上限由參數傳入，技術上限讀 `ApprovalGraphLimits`）、W1–W3、G6 的「有沒有 `when`／`order`／`default`」。Application：G6 條件語法（`ConditionParser`，需要模組欄位）、G9（既有逐關檢查重用）、G10 參數（使用者啟用中、群組存在、主管層數、範本欄位存在）、G11（`ApprovalConstraints` 讀 J4 之後自動成立）、G14（照舊）。
- **J8 同一分支節點的各分支不得共用節點**（G7 的一部分）：設計 §6.2「並行分支的會合一律要用匯合節點」的直接要求，§9 G7 原文沒列；不加這條，兩條分支可以在中間某個簽核節點會合，執行時那個節點會被到達兩次。
- **J9 graph 驗證放在 `ApprovalDefinitionValidator.Graph.cs`（partial），不新增服務、不改 DI。** `ValidateAsync` 依 `Shape` 分派；graph 跑完整驗證後**固定再加** `shape` 錯誤 `ApprovalMessages.GraphNotAccepted`。fg2 只要刪這一行就開放。代價：fg1 期間 graph 的「全部合法」案例在 Application 測試裡斷言成「只剩 `shape` 一個錯誤」。
- **J10 執行期守門 `ApprovalShapeGate`。** 送出（`SubmitApprovalHandler`）、預覽（`ApprovalPreviewer`）、草稿試算、重新比對（`ApproveTaskHandler` 呼叫 `Recheck` 之後）選到的定義不是 sequence → 409 `ApprovalMessages.GraphNotSupported(flowName)`，整筆回滾。現在 API 寫不出 graph，這是為了 §13 第 7 點的回退：fg2 之後若退回 fg1 的程式，graph 流程回 409，而不是讀 `def.Stages` 得到空清單、直接核准（少簽）。fg2 刪除。
- **J11 只有草稿的流程。** 
  - 儲存庫：`ListCurrentByModuleAsync` 語意不變（只含有已發布版本，給比對、約束、快照、試算、匯出、閉包重檢），回傳的 `FlowState` 多帶 `VersionId`，呼叫端不再寫 `CurrentVersionId!.Value`；新增 `ListByModuleAsync(moduleId)` 回 `(ApprovalFlow Flow, ApprovalFlowVersion? Version)`，只給設計器清單與優先序檢查用。
  - 回應：`ApprovalFlowResponse.Version` → `int?`、`Definition` → `ApprovalDefinition?`，加 `JsonElement? Draft`（`DraftJson` 原樣解析；fg1 一律 null）。
  - `PUT approval-flows/{key}` 對沒有版本的流程回 409 `ApprovalMessages.FlowNotPublished`，不寫任何東西。理由：現行 PUT 同時承載啟停與定義（`Definition` 必填），對草稿流程做什麼要等 fg3 的草稿與發布端點一起定；先 409 已滿足「沒有版本時拒絕啟用」，又不替 fg3 預設 API 形狀。設計 §10.8 寫的是 400，這裡用 409，fg3 定端點時一併改（列在「與設計的出入」）。
  - 排序、刪除、查詢照常處理草稿流程。
- **J12 政策編號 A37。** `approval.maxBranches`：Module、Integer、出廠 5、`Min` 2、`Max` 10、`count`；`ApprovalLimitSet` 末尾加 `int MaxBranches`（不給預設值，避免第二份數字）。
- **J13 狀態與原因常數延到 fg2。** fg1 只加欄位；`passed`、`waiting`、`branchCancelled`、`instanceEnded` 與 `NodeKind` 的寫入都是引擎的事，現在加常數沒有呼叫端。
- **J14 範本語法 `ApprovalNotifyTemplate.Parse` 放 Domain、在 fg1b 就做。** `ReferencedFields` 要讀 `{f:<Id>}`（設計 §12.2：被範本引用的欄位不能刪除或停用），驗證（fg1d）也用同一個解析。規則見 U8。
- **J15 錯誤鍵。** 代號合法且不重複時用 `nodes.{id}`、`nodes.{id}.{屬性}`、`edges.{id}`、`edges.{id}.{屬性}`；代號不合法、是 null 或重複時用索引 `nodes[{i}].id`、`edges[{i}].id`（以代號定位不了）；整體結構用 `graph`。Application 外層照舊加 `definition.` 前綴。
- **J16 舊版是 sequence 時的「改嚴只管之後」**：先用 J5 轉成 graph，再以節點代號對應舊節點（＝關卡代號）。
- **J17 測試專案名稱照 repo 現況**：`DynamicModules.DomainTests`、`DynamicModules.ApplicationTests`、`DynamicModules.WebAPITests`（設計 §14 寫的 `DynamicModules.Domain.Tests` 是筆誤）。

---

## Task 0：核對（不改程式）

- [ ] 開 worktree：`git worktree add ../dynamic-fields-design.fg1 -b feature/fg1a origin/main`，記下基準 commit。注意：主控給的參考 worktree `dynamic-fields-design.s2d` 在計畫撰寫期間被切到 `chore/leak-denylist-path`（`31424516`），與 `8a1425ee` 只差 `.plugharbor/unattended.md`，原始碼相同。
- [ ] 重讀並確認行號：`ApprovalHandlers.cs`（41-42、197、206-207）、`ApprovalPreviewer.cs`（74-75）、`ApprovalFlowHandlers.cs`（44、53-55、75、130）、`ApprovalRepositories.cs`（20-29、92-97）、`ApprovalConstraints.cs`（62、103）、`ApprovalClosureRecheck.cs`（44）、`ApprovalDefinitionValidator.cs`（37、46-77）、`ApprovalActionHandlers.cs`（131 的 `Recheck`）。
- [ ] 確認 STJ 對 positional record 的 `[property: JsonIgnore(WhenWritingNull)]` 與「重新宣告屬性 `= Stages ?? []`」可以一起用（寫一個 Domain 測試先跑，J2）；確認 `I18nText` 的 converter 在 `Name` 為 null 時不出錯。
- [ ] 確認 `dotnet tool restore` 後 `dotnet ef migrations add` 的參數：`--project src/DynamicModules.Persistence --startup-project src/DynamicModules.Persistence`（`DesignTimeDbContextFactory` 在 Persistence）；先 `--dry-run` 不可用時就產生後檢查。
- [ ] grep 對 `ApprovalFlowResponse` 建構與欄位的斷言（`ApprovalFlowDesignerTests`、`ApprovalFlowsApiTests`、前端 `approval-flow-types.ts`），列進 Task 2。
- [ ] grep 對政策數量的斷言（`PolicyCatalogFixtureTests`、`PolicyApiTests`、前端 `policy-i18n.vitest.spec.ts`、手冊「九項」），列進 Task 3。
- [ ] 確認 a3g 是否已合併（`docs/roadmap.md` a3g 列）；已合併就從新的 main 開，未合併照「共用檔與 a3g」處理文件衝突。
- [ ] 核對結果寫在本檔末「修訂紀錄」。

---

## fg1a：資料欄位、只有草稿的流程、守門、分支數政策

### Task 1：migration `ApprovalGraph` 與實體欄位

**Files:**
- Modify: `src/DynamicModules.Domain/Entities/Dynamic/Approval.cs`（`ApprovalFlow.DraftJson`、`DraftUpdatedBy`、`DraftUpdatedTime`；`ApprovalFlowVersion.LayoutJson`；`ApprovalStageRun.NodeKind`、`InEdgeId`、`CloseReason`）
- Modify: `src/DynamicModules.Domain/Entities/Dynamic/Notification.cs`（`DataJson`）
- Modify: `src/DynamicModules.Domain/Data/Configurations/ApprovalConfigurations.cs`（`DraftUpdatedBy` 50、`NodeKind` 20、`InEdgeId` 40、`CloseReason` 20、`BranchKey` 256）
- Create（產生）: `src/DynamicModules.Persistence/Migrations/<時間>_ApprovalGraph.cs`、`.Designer.cs`；Modify（產生）: `DynamicDbContextModelSnapshot.cs`
- Test: `tests/DynamicModules.ApplicationTests/Persistence/ApprovalGraphSchemaTests.cs`（新）

**步驟：**
1. 先寫失敗的 schema 測試，再改實體與設定。
2. `cd poc/backend && dotnet tool restore && dotnet ef migrations add ApprovalGraph --project src/DynamicModules.Persistence --startup-project src/DynamicModules.Persistence`。
3. 讀產生的 `Up`：`Dynamic_ApprovalStageRun` 若被重建，確認 `INSERT INTO ... SELECT` 含全部既有欄位、外鍵與 `(InstanceId, Seq)` 索引都在；`Down` 能還原。不手改產生檔；不對就改實體設定後刪掉重產。

**測試（`ApprovalGraphSchemaTests`）：**
- `ApprovalGraphMigration_AddsColumns`：最新 schema 的 `pragma_table_info` 含 §7.6 全部新欄位，且全部可為空。
- `ApprovalGraphMigration_KeepsExistingStageRunsAndFlows`：`MigrateAsync("RemoveMockCustomer")` → 用原生 SQL 塞一筆流程、版本、實例、關卡實例（含 `NodeId`、`BranchKey` 為 null）與一則通知 → 升到最新 → 各列原值不變、新欄位為 null。
- `StageRun_BranchKey256_RoundTrips`：EF 寫入 256 字的 `BranchKey` 再讀回相同（SQLite 不檢查長度，這條防的是設定被改回 40 時 PostgreSQL 才會壞；pg0 後在 PostgreSQL 補驗）。

**驗收：** `dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~ApprovalGraphSchema|FullyQualifiedName~SharedMasterSchema"`；`dotnet build`（警告即錯誤）。

- [ ] 寫失敗測試 → 實作 → 產生 migration → 通過 → commit `feat(approval): migration ApprovalGraph，加流程圖要用的欄位`

### Task 2：只有草稿的流程（後端程式假設）

**Files:**
- Modify: `src/DynamicModules.Domain/Approvals/ApprovalDefinition.cs`（`FlowState(Flow, Definition, long VersionId)`）
- Modify: `src/DynamicModules.Domain/Repositories/Dynamic/IApprovalRepositories.cs`、`src/DynamicModules.Persistence/Repositories/Dynamic/ApprovalRepositories.cs`（`ListCurrentByModuleAsync` 帶 `VersionId`；新增 `ListByModuleAsync`）
- Modify: `src/DynamicModules.Application/Dynamic/ApprovalService/ApprovalHandlers.cs`（42、206 改用 `f.VersionId`）、`ApprovalPreviewer.cs`（75）、`ApprovalFlowHandlers.cs`（優先序檢查改讀 `ListByModuleAsync`；`ResponseAsync` 允許沒有版本；`UpdateApprovalFlowHandler` 對沒有版本回 409；`GetApprovalFlowsHandler` 清單改讀 `ListByModuleAsync`）、`ApprovalFlowModels.cs`（`ApprovalFlowResponse`）、`ApprovalMessages.cs`（`FlowNotPublished`）
- Modify: 其他 `new FlowState(` 呼叫端（Task 0 列出，目前 3 處）
- Modify: `tests/DynamicModules.ApplicationTests/Dynamic/Approvals/ApprovalTestBase.cs`（`SeedUnpublishedFlow(r, key, to, priority, string? draftJson = null, bool active = false)`：直接寫 `ApprovalFlow`，`CurrentVersionId = null`）
- Test: `tests/DynamicModules.ApplicationTests/Dynamic/Approvals/ApprovalUnpublishedFlowTests.cs`（新）、`tests/DynamicModules.ApplicationTests/Dynamic/AppApprovalFlowCodecTests.cs`、`tests/DynamicModules.WebAPITests/Dynamic/ApprovalFlowsApiTests.cs`

**介面：**

```csharp
public sealed record FlowState(ApprovalFlow Flow, ApprovalDefinition Definition, long VersionId);

/// <summary>模組的全部流程列（含沒有已發布版本的），依目標狀態、優先序排序；只給設計器清單與優先序檢查用，比對與約束一律用 ListCurrentByModuleAsync。</summary>
Task<List<(ApprovalFlow Flow, ApprovalFlowVersion? Version)>> ListByModuleAsync(long moduleId, CancellationToken ct);

public sealed record ApprovalFlowResponse(
    string Key, I18nText Name, string ModuleKey, long ToOptionId, int Priority, bool Active,
    int? Version, bool Used, int PendingCount, ApprovalDefinition? Definition, bool CurrentVersionUsed, JsonElement? Draft);
```

**規則：**
1. 送出、預覽、草稿試算、約束、閉包重檢、匯出照舊用 `ListCurrentByModuleAsync`（只含已發布）。
2. 新增與更新的「同一目標狀態優先序不能重複」改看 `ListByModuleAsync`（含草稿流程），回 400 鍵 `priority`，不再撞唯一索引變 500。
3. `UpdateApprovalFlowHandler`：`flow.CurrentVersionId is null` → 409 `FlowNotPublished`「這條流程還沒有發布的版本，請先發布」，在任何驗證之前。
4. `ResponseAsync`：沒有版本時 `Version`、`Definition` 為 null、`CurrentVersionUsed = false`；`Draft` 為 `DraftJson` 解析結果（null 就 null）。
5. 不新增任何能建立只有草稿流程的路徑（草稿端點是 fg3）。

**測試（`ApprovalUnpublishedFlowTests`，用 `BuildPurchase`＋`SeedUnpublishedFlow`）：**
- `List_IncludesUnpublishedFlow_VersionAndDefinitionNull`：清單有它、`Version == null`、`Definition == null`、`Used == false`、`PendingCount == 0`；有 `DraftJson` 時 `Draft` 原樣回來。
- `Get_UnpublishedFlow_200_NoCrash`。
- `Update_UnpublishedFlow_409_NothingWritten`：`Active = true` 或 `false` 都 409，流程列與版本表不變。
- `UnpublishedFlow_ActiveInDb_NotMatched_Direct`：目標狀態只有一條 `Active = true` 的草稿流程、轉移 `noMatch = direct` → 送出走直接轉移（等同沒有流程），沒有建實例。另一條：`noMatch = block` → 400「沒有符合的流程」。
- `UnpublishedFlow_NotCountedForNoFlowConstraint`：目標狀態只有草稿流程時存狀態機 → `states[{to}]` 的「沒有流程」錯誤照出。
- `UnpublishedFlow_NotInDraftPreviewFlowSet`：同目標狀態另有一條草稿流程，草稿試算結果與沒有它時相同。
- `Create_PriorityTakenByUnpublishedFlow_400`：新流程用草稿流程的優先序 → 400 鍵 `priority`。
- `Reorder_IncludesUnpublishedFlow`：排序端點的 `keys` 必須列出草稿流程，回應含它且不出錯。
- `Delete_UnpublishedFlow_Removed`。
- `Lock_IgnoresUnpublishedFlow`：草稿流程的 `DraftJson` 寫了可編輯欄位，鎖定欄位不含它（U3）。
- 回歸：`ApprovalFlowDesignerTests`、`ApprovalSubmitTests`、`ApprovalPreviewTests` 不改斷言通過。
- `AppApprovalFlowCodecTests.Export_SkipsUnpublishedFlow`（U4）：匯出的 `approvalFlows` 沒有草稿流程。
- WebAPI `ApprovalFlowsApiTests.Get_UnpublishedFlow_VersionNull`（`Postgres` trait）：JSON 的 `version`、`definition` 是 null、`draft` 存在。

**驗收：** `dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~ApprovalUnpublishedFlow|FullyQualifiedName~ApprovalFlowDesigner|FullyQualifiedName~ApprovalFlowDefinition|FullyQualifiedName~ApprovalSubmit|FullyQualifiedName~ApprovalPreview|FullyQualifiedName~ApprovalConstraints|FullyQualifiedName~AppApprovalFlowCodec"`；`dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~ApprovalFlowsApi"`。

- [ ] 寫失敗測試 → 實作 → 通過 → commit `feat(approval): 後端允許流程沒有已發布版本`

### Task 3：政策 `approval.maxBranches`（A37）

**Files:**
- Modify: `src/DynamicModules.Domain/Policies/PolicyCatalog.cs`（`ApprovalMaxBranches`，加進 `All`，放在 `ApprovalMaxStages` 之後）
- Modify: `src/DynamicModules.Domain/Approvals/ApprovalDefinition.cs`（`ApprovalLimitSet(..., int MaxBranches)`）
- Modify: `src/DynamicModules.Application/Dynamic/ApprovalService/ApprovalPolicies.cs`（`LimitsAsync` 讀它）
- Modify: `poc/shared/policy-catalog.json`（`codes`）
- Modify: `poc/frontend/src/i18n/{zh-TW,en-US,th,vi}.json`（`policies.items.approval.maxBranches.{name,help}`）
- Modify: `tests/DynamicModules.ApplicationTests/Dynamic/AppApprovalPackageTests.cs:523-526`（三處 `new ApprovalLimitSet(...)` 補第五個參數 5）
- Modify: `docs/manual.md`（776 行加「每個分支節點的分支數上限（出廠 5，範圍 2 到 10；流程圖用）」，776、785 行「九項」改「十項」並在 785 行清單加「分支數」）
- Modify: `docs/superpowers/specs/2026-11-11-policy-settings-design.md` §4（加 A37 列：「簽核：每個分支節點的分支數（流程圖設計 §15；`approval.maxBranches`，技術範圍 2–10，可依模組覆寫）」、出廠 5、範圍 M、段 fg1a）
- Test: `tests/DynamicModules.DomainTests/Policies/PolicyDefinitionTests.cs`

**文字（zh-TW）：** name「每個分支節點的分支數上限」；help「流程圖中，一個分支節點最多能拉出幾條並行分支。調低只影響之後新增或修改的流程；已存的流程照常可用，原樣再存也不會被擋。」en-US、th、vi 照意思翻，不留中文。

**測試：**
- `ApprovalMaxBranches_FactoryRangeAndScope`：出廠 "5"、`Module`、`Min` 2、`Max` 10、`count`；`Normalize("1")`、`Normalize("11")` 為 null；`All` 含它。
- 既有 `PolicyCatalogFixtureTests`（codes 與 json 一致）、`PolicyApiTests`、前端 `policy-i18n.vitest.spec.ts`、`locales.vitest.spec.ts` 照常通過。
- `ApprovalPolicyLimitsTests` 加 `Limits_MaxBranches_ModuleOverride`：模組覆寫 3 → `LimitsAsync` 回 3。

**驗收：** DomainTests `--filter "FullyQualifiedName~Policy"`；ApplicationTests `--filter "FullyQualifiedName~ApprovalPolicyLimits|FullyQualifiedName~AppApprovalPackage"`；WebAPI `--filter "FullyQualifiedName~PolicyApi"`；前端 `bunx vitest run src/i18n src/domain/dynamic/policy-i18n.vitest.spec.ts --maxWorkers=2`。

- [ ] 寫失敗測試 → 實作 → 通過 → commit `feat(policy): 新增每個分支節點的分支數上限政策（A37）`

### Task 4：執行期 graph 守門（J10）

**Files:**
- Create: `src/DynamicModules.Application/Dynamic/ApprovalService/ApprovalShapeGate.cs`
- Modify: `ApprovalHandlers.cs`（送出：比對後）、`ApprovalPreviewer.cs`（比對後）、`ApprovalActionHandlers.cs`（`Recheck` 得到 `NewFlow` 之後、改道之前）、`ApprovalMessages.cs`（`GraphNotSupported(string flowName)`「流程「{name}」使用流程圖格式，這個版本的系統還不能執行」）
- Test: `tests/DynamicModules.ApplicationTests/Dynamic/Approvals/ApprovalShapeGateTests.cs`（新）

```csharp
/// <summary>fg2 之前 graph 定義一律不執行（fail closed）；fg2 上線時刪除這個類別與三個呼叫點。</summary>
public static class ApprovalShapeGate
{
    public static ServiceResult? Reject(FlowSnapshotEntry? entry) =>
        entry is { Definition.Shape: not ApprovalShapes.Sequence } e ? ServiceResult.Conflict(ApprovalMessages.GraphNotSupported(e.Name.Resolve("zh-TW"))) : null;
}
```

**測試**（用 Db 直接把某條流程目前版本的 `Shape` 改成 `"graph"`、`Stages` 清空來造情境）：
- `Submit_MatchedGraphFlow_409_NothingWritten`：沒有實例、記錄 `RowVersion` 不變。
- `Preview_MatchedGraphFlow_409`。
- `Approve_RecheckSelectsGraphFlow_409_Rollback`：實例送出後，把快照裡另一條優先序較前的流程改成 graph 並讓它成立 → 同意回 409、任務仍 `open`。
- `Submit_GraphFlowInactive_NotBlocking`：停用中的 graph 流程不影響送出（比對不到它）。

**驗收：** ApplicationTests `--filter "FullyQualifiedName~ApprovalShapeGate|FullyQualifiedName~ApprovalSubmit|FullyQualifiedName~ApprovalReroute"`。

- [ ] 寫失敗測試 → 實作 → 通過 → commit `feat(approval): 流程圖格式在引擎上線前一律不執行`
- [ ] fg1a 審查、合併（主控）

---

## fg1b：Domain 型別、參照、轉換

### Task 5：Domain 型別

**Files:**
- Modify: `src/DynamicModules.Domain/Approvals/ApprovalDefinition.cs`（`ApprovalDefinition` 加 `Nodes`、`Edges`；`Stages` 重新宣告；`ApprovalShapes.Graph`；新增下列 record 與常數）
- Create: `src/DynamicModules.Domain/Approvals/ApprovalCodes.cs`（代號規則，`ApprovalDefinitionValidator.CodePattern` 改用它）
- Modify: `src/DynamicModules.Application/Dynamic/ApprovalService/ApprovalDefinitionValidator.cs`（只換 regex 來源）
- Test: `tests/DynamicModules.DomainTests/Approvals/ApprovalGraphJsonTests.cs`（新）、`ApprovalDefinitionJsonTests.cs`

```csharp
public sealed record ApprovalDefinition(
    string Shape, JsonElement? When, long? RejectTo, ApproverSource? FallbackApprovers, List<ApprovalStageDef> Stages,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] List<ApprovalNodeDef>? Nodes = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] List<ApprovalEdgeDef>? Edges = null)
{
    public List<ApprovalStageDef> Stages { get; init; } = Stages ?? [];
}

/// <summary>流程圖節點（流程圖設計 §7.2）；依 Kind 使用其中的屬性，其餘為 null。Mode：簽核節點是會簽或簽，匯合節點是全部到齊或任一到齊。</summary>
public sealed record ApprovalNodeDef(
    string Id, string Kind, I18nText? Name = null,
    JsonElement? When = null, ApproverSource? Approvers = null, string? Mode = null, List<long>? EditableFields = null,
    int? DueHours = null, string? OnEmpty = null, string? ForkId = null,
    List<NotifyRecipientDef>? Recipients = null, NotifyTemplateDef? Template = null)   // 可選屬性都加 WhenWritingNull
{
    /// <summary>簽核節點轉成第一版關卡，供驗證與解析重用；Code = Id。</summary>
    public ApprovalStageDef AsStage();
}

public sealed record ApprovalEdgeDef(string Id, string From, string To, I18nText? Label = null, JsonElement? When = null, int? Order = null, bool? Default = null);
public sealed record NotifyRecipientDef(string Kind, List<string>? UserIds = null, List<long>? GroupIds = null, int? Level = null);
public sealed record NotifyTemplateDef(I18nText Title, I18nText Body);

public static class ApprovalNodeKinds { public const string Start = "start", Approval = "approval", Condition = "condition", Fork = "fork", Join = "join", End = "end", Notify = "notify"; }
public static class ApprovalNotifyRecipientKinds { public const string Users = "users", Group = "group", Initiator = "initiator", InitiatorManager = "initiatorManager"; }
```

**測試：**
- `Sequence_SerializesExactlyAsBefore`：一份含條件、兩關、備援的 sequence 定義，以 `JsonSerializerDefaults.Web` 序列化，結果與寫死在測試裡的 fg1 前字串完全相同（先在 fg1 前的程式碼產生字串貼進測試）。
- `SameAs_SequenceRoundTripUnchanged`（既有測試類補一條）。
- `Graph_DesignExample_RoundTrips`：設計 §7.1 的範例 JSON 反序列化 → 10 個節點、10 條線、`Stages` 是空清單、匯合 `ForkId == "split"`、`Mode == "all"`；再序列化後反序列化與第一次 `SameAs`。
- `Graph_MissingStages_EmptyList`、`Sequence_NullStagesInJson_EmptyList`。
- `ApprovalNode_AsStage_CopiesApprovalAttributes`。

**驗收：** DomainTests `--filter "FullyQualifiedName~Approvals"`；ApplicationTests `--filter "FullyQualifiedName~ApprovalDefinitionValidator"`（regex 來源換了，行為不變）。

- [ ] 寫失敗測試 → 實作 → 通過 → commit `feat(approval): 流程定義加流程圖節點與連線型別`

### Task 6：參照集中、範本語法、閉包與鎖定讀 graph

**Files:**
- Create: `src/DynamicModules.Domain/Approvals/ApprovalDefinitionRefs.cs`
- Create: `src/DynamicModules.Domain/Approvals/ApprovalNotifyTemplate.cs`
- Modify: `src/DynamicModules.Domain/Approvals/FieldClosure.cs`（`DirectFields`、`ReferencedFields`）、`ApprovalTopology.cs`（`Lock` 的可編輯欄位）
- Modify: `src/DynamicModules.Application/Dynamic/ApprovalService/ApprovalConstraints.cs`（62、103）、`ApprovalClosureRecheck.cs`（44）
- Modify: `src/DynamicModules.Persistence/Repositories/Dynamic/ApprovalRepositories.cs`（`References` 改用 `GroupRefs`）
- Test: `tests/DynamicModules.DomainTests/Approvals/ApprovalDefinitionRefsTests.cs`（新）、`ApprovalNotifyTemplateTests.cs`（新）、`FieldClosureTests.cs`、`ApprovalTopologyTests.cs`；`tests/DynamicModules.ApplicationTests/Dynamic/Approvals/ApprovalConstraintsTests.cs`、`ApprovalDeleteGuardTests.cs`

```csharp
public static class ApprovalDefinitionRefs
{
    /// <summary>sequence 的關卡，或 graph 的簽核節點（AsStage）。</summary>
    public static IEnumerable<ApprovalStageDef> Steps(ApprovalDefinition d);
    /// <summary>流程啟動條件、各步驟的略過條件、條件節點出線的條件（graph）。</summary>
    public static IEnumerable<JsonElement> Conditions(ApprovalDefinition d);
    /// <summary>通知範本標題與內文（所有語系）引用的欄位 Id；不影響路線，不進閉包與鎖定。</summary>
    public static IEnumerable<long> NotifyFieldRefs(ApprovalDefinition d);
    /// <summary>簽核人與候選群組、備援、通知收件人（group）的群組 Id。</summary>
    public static IEnumerable<long> GroupRefs(ApprovalDefinition d);
}

public sealed record TemplatePart(string? Text, string? Placeholder, long? FieldId);
public static class ApprovalNotifyTemplate
{
    /// <summary>U8：{title}、{flow}、{initiator}、{node}、{f:數字} 是佔位符；其他 {不含大括號的文字} 回 Error = 那段文字；單獨的大括號照字面。</summary>
    public static (IReadOnlyList<TemplatePart> Parts, string? Error) Parse(string text);
    public static IEnumerable<long> FieldIds(string text);
}
```

`DirectFields(d)` = `Conditions(d)` 的條件欄位＋`Steps(d)` 的 `userField`；`ReferencedFields(d)` = `DirectFields`＋`Steps` 的可編輯欄位＋`NotifyFieldRefs`。

**測試：**
- `Refs_Sequence_SameAsBefore`：對既有測試用的 sequence 定義，`DirectFields`、`ReferencedFields`、`Lock` 的結果與改寫前相同（把改寫前的期望值寫死）。
- `DirectFields_Graph_IncludesConditionEdgesSkipConditionsAndUserField_NotTemplate`。
- `ReferencedFields_Graph_IncludesEditableAndTemplateFields`。
- `Lock_Graph_ConditionEdgeField_Locked`、`Lock_Graph_ApprovalNodeEditableFields_Locked`、`Lock_Graph_TemplateField_NotLocked`。
- `GroupRefs_Graph_IncludesNotifyRecipientGroups`。
- 範本：`Parse_KnownPlaceholders`、`Parse_UnknownPlaceholder_Error`、`Parse_LoneBraces_Literal`、`Parse_FieldIdNotNumber_Error`（`{f:abc}`）、`FieldIds_AllLocales`。
- Application：`Constraints_Graph_EditableFieldInConditionEdgeClosure_400`（G11：條件節點出線引用公式欄位，公式引用的欄位是另一個簽核節點的可編輯欄位 → `flows[{key}]` 錯誤，路徑訊息含公式名）、`Constraints_Graph_EditableFromLockedEntry_400`。
- `DeleteGuard_FieldInGraphTemplate_Blocked`、`DeleteGuard_FieldInGraphConditionEdge_Blocked`、`DeleteGroup_ReferencedByGraphNotifyRecipient_Blocked`（以 Db 寫入 graph 版本造情境）。

**驗收：** DomainTests `--filter "FullyQualifiedName~Approvals"`；ApplicationTests `--filter "FullyQualifiedName~ApprovalConstraints|FullyQualifiedName~ApprovalClosureRecheck|FullyQualifiedName~ApprovalDeleteGuard|FullyQualifiedName~ApprovalLock|FullyQualifiedName~DataGuardFieldPurge"`。

- [ ] 寫失敗測試 → 實作 → 通過 → commit `feat(approval): 閉包、鎖定與參照守衛讀流程圖定義`

### Task 7：sequence → graph 轉換

**Files:**
- Create: `src/DynamicModules.Domain/Approvals/ApprovalGraphConversion.cs`
- Test: `tests/DynamicModules.DomainTests/Approvals/ApprovalGraphConversionTests.cs`（新）

```csharp
public static class ApprovalGraphConversion
{
    /// <summary>流程圖設計 §7.8：start → 各關（依序，id = code，屬性原樣）→ end；線 e1…；頂層屬性保留；已是 graph 原樣回傳。</summary>
    public static ApprovalDefinition FromSequence(ApprovalDefinition d);
}
```

**測試：**
- `Convert_ThreeStages_LinearGraph`：節點 `start`、三關、`end`；線 `e1`–`e4` 依序；`Shape == "graph"`、`Stages` 空、`When`／`RejectTo`／`FallbackApprovers` 相同。
- `Convert_KeepsStageAttributesIncludingUnmatchedMarks`（`UnmatchedGroups`、`CandidatesUnmatched`、`when`、`editableFields`、`dueHours`、`onEmpty`）。
- `Convert_StageCodedStart_UsesStart0`、`Convert_Start0AlsoTaken_UsesStart1`、`Convert_StageCodedEnd_UsesEnd0`。
- `Convert_StageCodedE1_EdgeIdsUnaffected`（J5）。
- `Convert_Twice_SameResult`、`Convert_Graph_ReturnsSameInstance`。
- `Convert_RefsUnchanged`：轉換前後 `ReferencedFields`、`DirectFields`、`GroupRefs` 相同。

**驗收：** DomainTests `--filter "FullyQualifiedName~ApprovalGraphConversion"`。

- [ ] 寫失敗測試 → 實作 → 通過 → commit `feat(approval): sequence 流程轉成直線流程圖的純函式`
- [ ] fg1b 審查、合併（主控）

---

## fg1c：結構驗證與共用案例

### Task 8：Domain 結構驗證 G1–G8、G12、G13

**Files:**
- Create: `src/DynamicModules.Domain/Approvals/ApprovalGraphLimits.cs`（技術上限：節點 100、線 200、巢狀 5、收件人設定 20、範本標題 200、內文 1000、解析後收件人 200）
- Create: `src/DynamicModules.Domain/Approvals/ApprovalGraphModel.cs`（由定義建圖：節點索引、出入線、可達、拓撲序、並行區；fg2 會重用）
- Create: `src/DynamicModules.Domain/Approvals/ApprovalGraphValidator.cs`
- Test: `tests/DynamicModules.DomainTests/Approvals/ApprovalGraphValidatorTests.cs`（新；放案例檔放不下的：技術上限、`previous` 比對）

```csharp
public sealed record GraphIssue(string Key, string Rule, string Code);
public sealed record GraphCheck(IReadOnlyList<GraphIssue> Errors, IReadOnlyList<GraphIssue> Warnings);
public sealed record GraphPolicyLimits(int MaxApprovalNodes, int MaxBranches);

public static class ApprovalGraphValidator
{
    /// <summary>previous：這條流程目前版本（sequence 先轉換，J16）；政策上限超過且比舊版大才擋。Warnings 在 fg1d 才填。</summary>
    public static GraphCheck Check(ApprovalDefinition d, GraphPolicyLimits limits, ApprovalDefinition? previous);
}
```

**規則與代碼**（鍵照 J15；同一處多個問題全部列出，Application 每個鍵取第一則）：

| 規則 | 代碼 |
|---|---|
| G1 | `idInvalid`、`idDuplicate`、`kindInvalid`、`stagesNotAllowed`（`graph`）、`nodesMissing`（`graph`）、`edgeEndpointMissing`、`selfLoop`、`edgeDuplicate` |
| G2 | `startCount`（`graph`）、`endMissing`（`graph`）、`inCount`、`outCount` |
| G3 | `unreachable` |
| G4 | `deadEnd` |
| G5 | `cycle`（`graph`） |
| G6 | `defaultCount`（節點）、`whenMissing`、`orderInvalid`、`orderDuplicate`、`defaultHasWhen`、`conditionAttrNotAllowed`（非條件節點出線帶 `when`／`order`／`default`） |
| G7 | `forkWithoutJoin`、`joinForkInvalid`（`forkId` 不存在、不是分支、兩個匯合指向同一分支）、`joinModeInvalid`、`edgeIntoRegion`、`edgeOutOfRegion`、`joinInEdgeOutside`、`branchesMerge`（J8）、`regionCross` |
| G8 | `endInRegion` |
| G12 | `editableOverlap`（後出現的那個簽核節點） |
| G13 | `approvalNodesNone`（U5）、`approvalNodesTooMany`、`branchesTooMany`（分支節點）、`nodesTooMany`、`edgesTooMany`、`nestingTooDeep`（最內層的分支節點） |

**並行區演算法（建議，實作者可換成支配樹，設計 §20）：** 對每個分支 F 與配對的匯合 J，區域 R(F) = 從 F 出發、不經過 J 能到的節點（不含 F、J）。檢查：R 內沒有結束節點（G8）；R 內節點的出線只到 R 或 J（`edgeOutOfRegion`）；R 內節點的入線只來自 R 或 F（`edgeIntoRegion`）；J 的入線只來自 R 或 F（`joinInEdgeOutside`）；F 的每條出線各自的分支集合（從那條線的終點出發、不經 J 能到的節點）兩兩不相交（`branchesMerge`）；兩個區域相交時，一個必須完整包含另一個的 F、J 與區域（否則 `regionCross`）。巢狀深度＝包含它的區域數＋1。G3、G4、G5 先做；有環或有不可達節點時跳過 G7、G8、G12（避免連帶錯誤）。

**測試（C#，案例檔外）：**
- `TooManyNodes_101_Error`、`TooManyEdges_201_Error`、`Nesting6_Error_Nesting5_Ok`。
- `ApprovalNodes_OverPolicy_ButNotMoreThanPrevious_Ok`、`ApprovalNodes_OverPolicy_MoreThanPrevious_Error`、`Branches_OverPolicy_SameForkSameCount_Ok`、`Branches_OverPolicy_NewFork_Error`。
- `PreviousSequence_ConvertedForComparison`（J16：舊版 12 關 sequence、上限 10，新 graph 12 個簽核節點 → 通過；13 個 → 錯）。

**驗收：** DomainTests `--filter "FullyQualifiedName~ApprovalGraphValidator"`。

- [ ] 寫失敗測試 → 實作 → 通過 → commit `feat(approval): 流程圖結構驗證 G1–G8、G12、G13`

### Task 9：共用案例檔

**Files:**
- Create: `poc/shared/approval-graph-cases.json`
- Create: `tests/DynamicModules.DomainTests/Approvals/SharedApprovalGraphCasesTests.cs`（照 `SharedRuleCasesTests` 往上找 repo 根目錄；每個案例一列 `MemberData`，顯示名稱用案例 `name`）

**格式：**

```json
{
  "limits": { "maxApprovalNodes": 10, "maxBranches": 5 },
  "cases": [
    {
      "name": "G3 節點無法從開始到達",
      "definition": { "shape": "graph", "stages": [],
        "nodes": [ {"id":"start","kind":"start"}, {"id":"a","kind":"approval"}, {"id":"x","kind":"approval"}, {"id":"end","kind":"end"} ],
        "edges": [ {"id":"e1","from":"start","to":"a"}, {"id":"e2","from":"a","to":"end"}, {"id":"e3","from":"x","to":"end"} ] },
      "previous": null,
      "limits": null,
      "errors": [ ["nodes.x", "G3", "unreachable"] ],
      "warnings": []
    }
  ]
}
```

案例裡的簽核節點只寫 `id`、`kind`（結構驗證不看簽核人；簽核人屬性由 Application 測試管）；條件只要有 `when` 物件即可。斷言 `errors`、`warnings` 與結果是同一個集合（不看順序）。

**最少案例**（每條規則至少一個通過、一個失敗）：設計 §7.1 範例（通過）、Task 7 轉換出的直線圖（通過）、互斥路徑在簽核節點會合（通過，J1）、巢狀兩層並行（通過）、空分支（通過，U7）、`stages` 非空（G1）、重複代號與不合法代號（G1 索引鍵）、自環與重複線（G1）、兩個開始／沒有結束／簽核兩條出線／匯合一條入線（G2）、不可達（G3）、死路（G4）、環（G5）、條件沒有預設線／兩條預設線／預設線有條件／非預設線缺條件／順序重複／簽核節點出線帶 `order`（G6）、分支沒有匯合／匯合指向非分支節點／匯合模式不合法／區外連進區內／區內連到區外／分支私下會合／交錯並行區（G7）、結束在並行區內（G8）、並行兩分支可編輯欄位重疊（G12 錯）、互斥兩路可編輯欄位重疊（通過）、沒有簽核節點（G13，U5）、簽核節點 11 個（G13）、分支 6 條（G13）。

**驗收：** DomainTests `--filter "FullyQualifiedName~SharedApprovalGraphCases|FullyQualifiedName~ApprovalGraphValidator"`。

- [ ] 寫失敗測試 → 實作 → 通過 → commit `test(approval): 流程圖結構驗證的前後端共用案例`
- [ ] fg1c 審查、合併（主控）

---

## fg1d：警告、Application 驗證、總收

### Task 10：警告 W1–W3

**Files:**
- Modify: `src/DynamicModules.Domain/Approvals/ApprovalGraphValidator.cs`（填 `Warnings`）
- Modify: `poc/shared/approval-graph-cases.json`（警告案例）

**規則：**
- W1 `anyJoinWithoutApproval`（匯合節點）：模式 `any` 的並行區裡，有一條分支存在不經任何簽核節點就到達匯合的路徑（空分支也算）。
- W2 `defaultToEndWithoutApproval`（條件節點）：預設線之後存在不經任何簽核節點就到達結束的路徑。
- W3 `skipConditionOnConditionBranch`（簽核節點）：有略過條件，而且從某個條件節點的出線出發、在下一個簽核節點或會合點之前就到達它。
- 有結構錯誤（G1–G5）時不算警告。

**案例：** W1 空分支任一到齊、W1 分支只有通知節點、全部到齊的空分支不警告、W2 小額免簽、W2 預設線先過簽核再結束不警告、W3 條件出線上的節點有略過條件、W3 直線上的略過條件不警告、設計 §7.1 範例（W1–W3 都不觸發；Task 0 核對範例實際結果，觸發就照實寫進案例並在修訂紀錄說明）。

**驗收：** DomainTests `--filter "FullyQualifiedName~SharedApprovalGraphCases"`。

- [ ] 寫失敗案例 → 實作 → 通過 → commit `feat(approval): 流程圖發布警告 W1–W3`

### Task 11：Application 驗證器的 graph 分支（仍拒收）

**Files:**
- Modify: `src/DynamicModules.Application/Dynamic/ApprovalService/ApprovalDefinitionValidator.cs`（依 `Shape` 分派；逐關檢查抽成私有方法 `StageErrors(stage, prefix, codeKey, before, ...)`，sequence 鍵完全不變）
- Create: `src/DynamicModules.Application/Dynamic/ApprovalService/ApprovalDefinitionValidator.Graph.cs`
- Modify: `ApprovalMessages.cs`（`GraphNotAccepted`「流程圖格式尚未開放，請繼續使用關卡設定」；`Graph(string code)` 對照 Task 8、Task 10 的代碼；`NodeNameRequired`；`NotifyRecipientsCount(max)`、`NotifyRecipientInvalid`、`TemplateTooLong(max)`、`TemplatePlaceholderUnknown(text)`、`TemplateFieldInvalid`）
- Test: `tests/DynamicModules.ApplicationTests/Dynamic/Approvals/ApprovalGraphDefinitionValidatorTests.cs`（新）

**規則：**
1. `Shape == sequence` → 現行邏輯（只換成呼叫 `StageErrors`）。其他非 `graph` → `ShapeInvalid`。
2. `graph`：
   - 共同：`when`、`rejectTo`、`fallbackApprovers` 照現行。
   - Domain：`ApprovalGraphValidator.Check(d, new(limits.MaxStages, limits.MaxBranches), previous is { Shape: sequence } p ? FromSequence(p) : previous)`，每個錯誤以 `ApprovalMessages.Graph(code)` 轉成訊息。
   - 簽核節點（G9）：`StageErrors(node.AsStage(), $"nodes.{id}", ...)`；代號錯誤已由 Domain 報，不重報；名稱必填（U6）；`before` 是 `previous` 中同代號的節點。
   - 條件節點、分支、匯合：名稱選填，有填就驗 `IsValid`（U6）。
   - 條件出線（G6 語法）：`edges.{id}.when` 用既有 `ConditionError`；出線的 `label` 有填就驗 `IsValid`。
   - 通知節點（G10）：名稱必填；收件人 1–`ApprovalGraphLimits.MaxRecipientItems` 項（`nodes.{id}.recipients`）；`users` 每位啟用中、`group` 群組存在且至少一個、`initiator` 無參數、`initiatorManager` 層數 1–`limits.MaxManagerLevel`（改嚴只管之後，比 `previous` 同節點）；範本標題、內文都要有繁中、每個語系不超過技術上限、`ApprovalNotifyTemplate.Parse` 無錯誤、`{f:Id}` 是本模組表頭啟用中的欄位（`nodes.{id}.template.title`／`.body`）。
   - 最後固定 `Err("shape", ApprovalMessages.GraphNotAccepted)`（J9）。
3. 警告不從 Application 回傳（fg3 的發布回應再加）。

**測試：**
- `Graph_DesignExample_OnlyShapeGateError`：設計 §7.1 範例換成 Demo 群組與欄位 → 錯誤只有 `shape`。
- `Graph_ApprovalNodeAttributes_KeyedByNodeId`：不合法的 `mode`、`dueHours`、群組 → `nodes.mgr.mode` 等鍵。
- `Graph_ConditionEdgeWhenInvalid_KeyedByEdgeId`：引用不存在欄位 → `edges.e3.when`。
- `Graph_NotifyRecipients_InactiveUserOrMissingGroup_400`、`Graph_NotifyTemplate_UnknownPlaceholder_400`、`Graph_NotifyTemplate_DetailOrInactiveField_400`、`Graph_NotifyTemplate_TitleTooLong_400`、`Graph_NotifyManagerLevel_OverPolicy_400`。
- `Graph_NameRules_ApprovalRequired_ForkOptional`（U6）。
- `Graph_StructuralErrors_MappedToMessages`：一個不可達節點 → `nodes.x` 的訊息是 `Graph("unreachable")` 的文字。
- `Graph_MaxBranchesFromModulePolicy`：模組覆寫 `approval.maxBranches = 2`，三條分支 → `nodes.split` 錯誤。
- `Graph_PreviousSequence_KeepOldApplies`（J16）。
- 回歸：`ApprovalDefinitionValidatorTests`、`ApprovalFlowDefinitionTests`、`ApprovalPolicyLimitsTests` 不改斷言通過。
- WebAPI `ApprovalFlowsApiTests.Create_Graph_400_ShapeGateAndNodeKeys`（`Postgres` trait）：`POST approval-flows` 送 graph → 400，錯誤鍵含 `definition.shape` 與 `definition.nodes.*`，沒有建立流程。

**驗收：** ApplicationTests `--filter "FullyQualifiedName~ApprovalGraphDefinitionValidator|FullyQualifiedName~ApprovalDefinitionValidator|FullyQualifiedName~ApprovalFlowDefinition|FullyQualifiedName~ApprovalPolicyLimits"`；WebAPI `--filter "FullyQualifiedName~ApprovalFlowsApi"`。

- [ ] 寫失敗測試 → 實作 → 通過 → commit `feat(approval): 流程圖定義的完整驗證（API 仍不收）`

### Task 12：文件總收

**Files:**
- Modify: `docs/superpowers/specs/2026-11-13-approval-flowgraph-design.md`：§19.1 加 fg1 的行為類裁決（使用者確認後的 U1–U8，編號接 U14 起）；§19.2 加 fg1 計畫的技術決定（J2、J6、J8、J9、J10、J11、J15、J16，編號接 J19 起）；§7.1 註明 graph 的 `stages` 為空陣列（J2）；§9 註明索引鍵與 `GraphIssue` 代碼、G7 加「分支不得共用節點」；§10.8 註明 fg1 對草稿流程的 PUT 回 409（J11）；§15 寫明政策編號 A37；§17 fg1 列註明切成 fg1a–fg1d。
- Modify: `docs/roadmap.md`（fg1 列改成 fg1a–fg1d 子列或狀態欄寫明，已上線附日期與 commit）
- Modify: `docs/feature-checklist.md`（「簽核流程圖地基（段 fg1）」改 `[x]` 附日期；跑 `feature_checklist.py docs/feature-checklist.md --write`）
- Modify: `docs/todo.md`（實作中發現、不在本段處理的事；沒有就不動）

- [ ] 文件 → commit `docs(approval): fg1 收尾：流程圖設計寫回、路線、功能清單`
- [ ] fg1d 審查、合併（主控）；合併後部署照 `formwright-ship`，Demo 冒煙只需確認既有簽核送出、同意、政策頁正常（fg1 沒有新畫面，不做四寬度走查；政策頁多一項的版面沿用既有元件）

---

## 驗收總表

| 項目 | 指令或操作 | 子段 |
|---|---|---|
| Domain 單元 | DomainTests `--filter FullyQualifiedName~Approvals`（含共用案例） | fg1b、fg1c、fg1d |
| 政策目錄一致 | DomainTests `--filter FullyQualifiedName~Policy` | fg1a |
| Application 整合 | 各 Task 的窄範圍 filter | 全部 |
| migration | `ApprovalGraphSchemaTests`；`dotnet build` 無警告 | fg1a |
| WebAPI | `--filter FullyQualifiedName~ApprovalFlowsApi|FullyQualifiedName~PolicyApi` | fg1a、fg1d |
| 前端 i18n | `bunx vitest run src/i18n src/domain/dynamic/policy-i18n.vitest.spec.ts --maxWorkers=2` | fg1a |
| 無業務名詞 | `--filter FullyQualifiedName~NoBusinessWords` | 每段 |
| 合併前 gate | GitHub Actions，`bash scripts/unattended/ci-wait.sh <sha>` | 每段 |
| 外洩掃描 | `bash scripts/unattended/leak-scan.sh` | 每段推分支前 |
| 宗旨檢查 | `formwright-purpose-check` 逐項（下節），結果寫交付說明 | 每段 |
| PostgreSQL | 未驗證；pg0 合併後隨 baseline 與雙跑集補驗 fg1a 的欄位與 `Postgres` trait 測試 | fg1a、fg1d |
| 本機四寬度走查 | 不適用（沒有新畫面；政策頁多一項由既有元件呈現）。若主控要求，走政策頁 390 寬 | — |

本機測試、build、gate、commit、合併、上線各自回報，沒跑到的標「未驗證」。

---

## 宗旨檢查（formwright-purpose-check，計畫階段）

1. **機制還是政策？** 通過。結構規則、技術上限、代號格式是完整性與技術限制（已列可寫死清單）；分支數、簽核節點數是政策（A37、A3），可依模組覆寫，出廠 5、10，系統管理員與模組設計者可改。
2. **寫死掃描：** 實作後跑 skill 裡的 diff grep。預期命中 `ApprovalGraphLimits` 的常數（技術限制，已列清單）；不應命中任何分支數或節點數的業務數字。
3. **單一來源：** 通過。分支數只在 `PolicyCatalog`；技術上限只在 `ApprovalGraphLimits`；代號 regex 由 Application 改讀 Domain 的 `ApprovalCodes`（fg1b Task 5）。前端 fg3 從政策 API 取值。
4. **兩個應用程式：** 通過。流程圖是核心簽核機制，請假、採購、合約、製程變更都用得上（流程圖設計 §5 ERP 對照）。
5. **空系統：** 通過。型別、驗證、轉換只依賴狀態機、組織、群組，沒有應用程式也能用；migration 不寫任何種子資料。
6. **套件只用定義組成：** 通過。fg1 不改套件格式；graph 進套件在 fg6，仍是定義資料。
7. **ERP 對照：** 通過，流程圖設計 §5 已有；fg1 不新增主檔或單據。
8. **fail closed、驗證不只做在前端、沒有執行期 DDL：** 通過。graph 在 fg2 前三道拒收；只有草稿的流程不參與比對；驗證全在後端；新欄位只走 EF migration。

---

## 與後續段的交接

- **fg2**：刪 `ApprovalShapeGate` 與三個呼叫點、刪 `ApprovalDefinitionValidator` 的 `shape` 擋門一行；加節點實例狀態與原因常數；`ApprovalGraphModel` 的並行區可直接算 `BranchKey`；政策設計 §10 B27 改寫。
- **fg3**：草稿與發布端點（寫 `DraftJson`、`DraftUpdatedBy`、`DraftUpdatedTime`，樂觀鎖 J15）、草稿流程的屬性更新（取代 fg1 的 PUT 409，屆時決定「啟用沒有版本的流程」回 400 的位置）、`GET approval-flows/{key}` 回傳轉換後的 graph、`LayoutJson` 驗證、前端驗證跑 `approval-graph-cases.json`、前端 `ApprovalFlow.version`／`definition` 改可為空、政策「關卡數」改名（U2）、發布回應帶 W1–W3。
- **fg5**：通知節點推進與 `Notification.DataJson` 寫入；驗證已在 fg1d 完成，fg5 只補執行期再驗。
- **fg6**：套件格式升版時，匯出端的 `AppApprovalFlowCodec`（目前讀 `current.Stages`）與 V8 要改讀兩種形狀；`ApprovalDefinitionRefs` 可直接用。

---

## 與設計文件矛盾或需要讀成別的意思之處

1. **graph 的 `stages`**：§7.1「graph 時沒有 stages」，本計畫存成空陣列（J2），因為 `Stages` 改可為空會波及 fg2 範圍的十幾處程式。
2. **G7 分支不得共用節點**：§9 G7 沒寫，但 §6.2 的「並行分支的會合一律要用匯合節點」需要它（J8）。
3. **G10 在 fg1 還是 fg5**：§17 fg1 列「G1–G13」、fg5 列「通知節點：驗證」。本計畫把 G10 的定義時驗證放 fg1d（範本解析 fg1b 就需要），fg5 的「驗證」讀成執行期再驗。
4. **沒有版本的流程啟用回 400**：§10.8 寫 400；fg1 沒有能建立草稿流程的端點，現行 PUT 又一定帶定義，本計畫先讓 PUT 對草稿流程一律 409（J11），400 的位置留給 fg3 定端點時一起決定。
5. **草稿端點的歸屬**：§17 沒寫草稿儲存與發布的後端端點在哪一段（fg2 的「API 接受 graph 存檔」或 fg3 的「草稿與發布」）。本計畫假設 fg3；fg2 計畫要確認。
6. **測試專案名稱**：§14 寫 `DynamicModules.Domain.Tests`，repo 是 `DynamicModules.DomainTests`（J17）。
7. **流程至少一個簽核節點、節點名稱、空分支、範本大括號**：設計沒寫，列為 U5–U8。

## 讀程式碼時發現的問題（不在設計文件裡）

1. **優先序檢查與唯一索引範圍不一致**：`ApprovalFlowEditor.ValidateAsync` 只看已發布流程，但唯一索引包含所有流程列；今天沒有草稿流程所以不會發生，fg1a Task 2 規則 2 修。
2. **`def.Stages` 對 graph 會是空清單**：引擎 `ActivateNextAsync` 的迴圈會直接走完、核准實例（少簽）。fg1 用 J10 的守門擋住，fg2 換成圖引擎。
3. **`JsonConversions` 的 Web 選項會寫出 null 屬性**：新增任何可選屬性都會改變既有 JSON；fg1b 用 `WhenWritingNull` 並加逐字比對測試。之後其他段在定義型別加屬性時也要照做。
4. **參考 worktree 的分支在計畫撰寫期間被另一個 session 切換**（`dynamic-fields-design.s2d` 現在是 `chore/leak-denylist-path`）：原始碼與 `8a1425ee` 相同，不影響本計畫，Task 0 從 origin/main 重開即可。

## 修訂紀錄

- 2026-11-14：初版（計畫 subagent，基準 `8a1425ee`）。Task 0 核對結果寫在這裡。
