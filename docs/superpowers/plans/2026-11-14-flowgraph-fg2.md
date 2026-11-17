# 簽核流程圖 fg2：圖形執行引擎 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 讓 `shape: graph` 的流程真的能跑：送出後依圖推進（條件分路、並行分支、全部到齊／任一到齊匯合、分支取消）、同意後沿出線推進、每次同意的重新比對、退回發起人與退回上一關（同一分支段內）、撤回、終止、後加簽、轉交、改派在分支內的行為、送出前預覽與設計器試算的「預計路徑」、同一張單的動作以實例為單位序列化（任務層自動重試、實例層比對版本）；最後拆掉 fg1 的兩道擋門，API 接受 graph 存檔與試算。進行中與新送出的 sequence 實例行為完全不變。

**Architecture:** Domain（`Domain/Approvals/`）新增三個純函式檔：`ApprovalGraphRuntime`（建圖、執行期結構再驗與支援的節點型別、分支段代號、匯合進線所屬的分支、選線）、`ApprovalGraphPath`（分支段路徑、上一關、被取消的分支、撤回資格）、`ApprovalGraphRoute`（§8.7 路線比較），以及預覽用的 `ApprovalGraphPreview`（含共同必經後繼）。Application 的 `ApprovalGraphMode`（fg2a2）集中推進、重新比對、預覽三方共用的執行期輔助：graph 判斷、引擎支援的節點型別、條件求值轉接（J2、J27）。Application 新增 `ApprovalGraphEngine`（scoped，推進、匯合、取消）；依賴單向：`ApprovalEngine` 注入圖引擎並把單關步驟以方法參數傳給它，圖引擎不注入 `ApprovalEngine`（J22）。送出、同意、重來三個呼叫端改呼叫 `ApprovalEngine.AdvanceAsync`，它是唯一呼叫 `CompleteAsync` 的地方（J22）。動作 handler 依「定義是 graph、實例走流程（不是備援）」改走分支版本；結構再驗在 `LoadAsync` 共同做，各動作是否要求結構有效照 J7 的對照表。並發改在 `LoadAsync` 取得處理權，所有依目前狀態而定的檢查移到處理權之後（J16）。fg1 的 `ApprovalShapeGate` 在正式環境**一直擋到 fg2g2**：fg2a2 把它改成可由 DI 替換的服務，測試換成開啟版才跑得到引擎，正式環境的 graph 行為與 fg1 完全相同（J1）；fg2g2 刪類別、開關與驗證器的 `shape` 擋門。不加 migration（fg1a 已加齊欄位），不新增端點。

**Tech Stack:** .NET 8、EF Core 8.0.11（SQLite；PostgreSQL 待 pg0，相關測試標 `[Trait("Category", "Postgres")]`、標「未驗證」）、MediatR、xUnit＋FluentAssertions；前端在 fg2d（時間軸的取消原因、歷程的分支取消說明）與 fg2e（預覽的任一到齊註記）各改兩三個簽核元件與四語 i18n，驗 vitest。不新增相依套件。

**Spec:**
- `docs/superpowers/specs/2026-11-13-approval-flowgraph-design.md`（下稱「流程圖設計」）：§2 反目標、§6.2、§7.6（節點實例欄位語意）、§7.7、§8 全節（§8.1–§8.14）、§9 G8（引擎依賴的不變式）、§12.4（不擋流程）、§13 第 2、7 點、§14 Application 測試的並行、退回、撤回、重新比對、條件無法判斷、併發、等價性、草稿以外各項、§15（可寫死清單、B27）、§16 第一點（引擎不得引入範本概念）、§17 fg2 列、§19、§20、§20.1。fg1d 寫回的 §9「實作註記」、§19.1 U14–U21、§19.2 J19–J27（已在 main）。
- `docs/superpowers/specs/2026-11-10-approval-workflow-design.md`（下稱「簽核設計」）：§4.3、§4.4、§5.2、§5.4、§5.5、§6.5、§7.2–§7.8、§8.2–§8.4、§10.1、§12。
- `docs/superpowers/specs/2026-11-11-policy-settings-design.md` §10（B27）。
- fg1 計畫與結果：`../formwright/.tmp/plans/20261114-flowgraph-fg1/plan.md`、`fg1a-result.md`、`fg1b-result.md`、`fg1c-result.md`、`fg1c-fix1-result.md`、`fg1d-result.md`（與 fix1–fix3）。fg2 本身：`fg2a1-result.md`（fg2a1 的結果與偏離）。
- a3 計畫「與流程圖的銜接」：`../formwright/.tmp/plans/20261114-approval-a3/plan.md` 788–795 行。
- 樣板：`ApprovalEngine`（推進與 `StartAsync`）、`ApprovalPath`（有效路徑與退回截斷）、`ApprovalRouter.Recheck`、`ApprovalActionSupport.LoadAsync`、`ApprovalShapeGateTests`（「409 且什麼都沒動」的逐項比對寫法）、`InventoryConcurrencyTests`（檔案型 SQLite 的真並行）、`EventRuntimeTests.ConfigureServices`（測試換服務註冊）。
- repo 規範：`AGENTS.md`、`.claude/rules/backend.md`、`docs.md`、`.claude/skills/formwright-purpose-check/SKILL.md`、`docs/ai/testing.md`。

**路徑慣例：**（修訂 2：對齊 fg2a1 實作）Files 清單與共用檔表一律寫 repo 根目錄起算的完整路徑：後端 `poc/backend/src/…`、`poc/backend/tests/…`，前端 `poc/frontend/src/…`；文件與腳本相對 repo 根目錄。只寫檔名的簽核檔（例如 `ApprovalHandlers.cs`）指 `poc/backend/src/DynamicModules.Application/Dynamic/ApprovalService/`；`tests/.../Approvals/` 指 `poc/backend/tests/DynamicModules.ApplicationTests/Dynamic/Approvals/`。`dotnet` 指令在 `poc/backend/` 執行（方案檔 `DynamicModules.sln` 在那裡），所以驗收指令裡的 `tests/…`、`src/…` 相對 `poc/backend/`；`bunx`、`bun` 指令在 `poc/frontend/` 執行。每個子段一個分支 `feature/fg2a1`、`feature/fg2a2`、`feature/fg2b`…`feature/fg2f`、`feature/fg2g1`、`feature/fg2g2`，各自從當時的 origin/main 開。行號以 origin/main `916268f1`（已含 fg1d）讀到的為準，每段開工時重新核對。

---

## 待確認的行為決定

設計沒寫死、答案反過來使用者看到或操作會不同的決定。實作照「推薦」進行，全部標 **主控裁決（待使用者確認）**；推翻時改對應 Task。fg2 之後只有 API 寫得出 graph，下列多數在 fg3、fg4 上線後才會被一般使用者看到，但規則在 fg2 就寫進程式。寫回設計時編號接在 fg1d 之後（U22 起）。

**U1 fg2 透過 API 存的 graph 直接成為已發布版本；草稿與發布端點在 fg3。** 主控裁決（待使用者確認）
- 推薦：fg2 只把既有的 `POST approval-flows`、`PUT approval-flows/{key}`、`POST approval-flows/preview` 打開收 graph，存了就是目前版本（被用過就開新版本，照第一版）；`DraftJson` 的寫入、「儲存草稿」「發布」「捨棄草稿」端點、啟用沒有版本的流程回 400 都在 fg3。
- 理由：設計 §17 把「草稿與發布（依 U6）」放在 fg3，§13 第 2 點寫「fg2 起 API 接受 graph：沒有畫面前只能從 API 建立，用於測試」；草稿端點的形狀要和設計器一起定，fg2 先做會替 fg3 預設 API。
- 選錯代價：若草稿歸 fg2，fg2 多約 400 行（端點、樂觀鎖、權限、測試），而且沒有畫面可以驗；若照推薦，fg2 到 fg3 之間用 API 建的 graph 沒有「先存不驗證」的選項，每次存檔都要通過 §9 全部規則。
- 例子：測試人員 `POST /approval-flows` 送一份有分支與匯合的 graph → 201，回應的 `version = 1`；送一份缺預設線的 graph → 400，錯誤鍵 `definition.nodes.amount`，不會留下任何草稿。

**U2 通知節點在 fg5 之前：存檔拒收；執行期遇到含通知節點的定義一律停下（409＋異常通知），不假裝通過。** 主控裁決（待使用者確認）（修訂 1 推翻初版的「執行期直接通過」）
- 推薦：fg2g2 打開 API 時，定義裡有 `notify` 節點 → 400，鍵 `definition.nodes.{id}`「通知節點尚未開放」（fg5 刪這一則）。執行期由能力守門擋：`ApprovalGraphRuntime.Build` 收引擎支援的節點型別清單（fg2 起是開始、結束、簽核、條件、分支、匯合；fg5 加通知），定義含清單外的型別就和結構不合法一樣處理（J7）：送出 409＋異常通知、不建實例；已存在的實例，需要結構的動作（同意、加簽、退回上一關、重新比對）409＋異常通知，不需要結構的動作（撤回、終止、退回發起人、轉交、改派，見 U11）照常。
- 理由：通知的收件人解析、範本、逐人遮蔽都是 fg5。設計 §12.4「通知不擋流程」指的是通知**已支援**時，收件人或範本出問題不擋；不等於允許還不會處理的節點寫 `passed` 假裝完成。會碰到含通知節點的資料，只有一種實際情況：fg5 上線後回退到 fg2–fg4，這時靜默略過會讓設計者以為已經知會了人。
- 選錯代價：照推薦，fg5 回退到 fg2–fg4 期間，含通知節點的進行中單據停住（可撤回、終止），系統管理員每次有人按同意都會收到異常通知；改成直接通過，回退期間的通知全部靜默失效、歷程卻顯示流程正常走完。
- 例子：API 存「主管 → 通知（人事）→ 結束」→ 400「通知節點尚未開放」。fg5 上線後建了同一份流程、送出一張單，接著回退到 fg4：主管按同意 → 409「流程圖定義不完整，已停止並通知系統管理員」，系統管理員收到異常通知；發起人仍可撤回，簽核管理員仍可終止。

**U3 匯出應用程式套件時遇到 graph 流程：整份匯出失敗並列出流程名稱。** 主控裁決（待使用者確認）
- 推薦：沿用現有的匯出錯誤清單（整份 409，`AppMessages.ExportFlowError`），細節「使用流程圖格式，套件格式升版前不能匯出」；fg6 套件升版時拆掉。
- 理由：現在的匯出讀 `current.Stages`，graph 會變成沒有關卡的流程寫進套件，對方開啟時才被 V8 擋下；在匯出端就說清楚比較好。
- 選錯代價：照推薦，有任何 graph 流程的應用程式在 fg6 前都匯不出；改成略過 graph 流程，對方環境會少一條流程卻沒人知道。
- 例子：採購應用程式有一條 API 建的 graph 流程「大額會簽」，從應用程式頁按匯出 → 409「簽核流程「大額會簽」使用流程圖格式，套件格式升版前不能匯出」。

**U4 目前版本是 graph 的流程，不能存回 sequence。** 主控裁決（待使用者確認）；這是一條**產品限制**（永久禁止 graph → sequence），需要使用者確認後才算定案，不是從「只留一個設計器」自動推得
- 推薦：`PUT approval-flows/{key}` 的新定義是 sequence、而目前版本是 graph → 400，鍵 `definition.shape`「這條流程已是流程圖格式，不能改回關卡格式」。
- 理由：舊的流程對話框（fg3 才移除）存檔時一律組 `shape: 'sequence'`（`approval-flow-draft.ts:107`），使用者在舊對話框打開 API 建的 graph 流程、加一關存檔，整張圖會被換成一串關卡；設計 §2 反目標也只留一個設計器，之後沒有合法的「改回 sequence」需求。卡片上的啟停開關送回原定義（`ApprovalFlowsTab.vue:52-56`），不受影響。
- 選錯代價：照推薦，fg2 到 fg3 之間 graph 流程只能用 API 改定義；不擋的話，舊對話框可以靜默覆蓋流程圖（沒被用過的版本是原地覆寫，找不回來）。
- 例子：流程卡片「大額會簽」顯示「0 關」，打開編輯對話框加一關「主管」後按儲存 → 紅字「這條流程已是流程圖格式，不能改回關卡格式」；在卡片上切換啟用 → 成功。

**U5 簽核中心「待簽」列顯示自己任務所在的節點名稱。** 主控裁決（待使用者確認）
- 推薦：「待簽」列的關卡名稱改取任務所屬節點實例的名稱（sequence 的待簽任務一定在目前關卡，結果不變）；「已處理」「我送出的」「全部」照舊取實例的 `CurrentStageRunId`（graph 是 `Seq` 最小的進行中節點），fg4 再改成「簽核中（財務、法務）」。
- 理由：並行時財務的待簽列若顯示「法務」，財務會以為點錯單；只改「待簽」一處就沒有 sequence 差異。
- 選錯代價：不改的話，並行分支的待簽列名稱是另一條分支的節點；全部都改成多節點字串，要提前做 fg4 的 U9 文案。
- 例子：「法務與財務」同時進行，財務在簽核中心「待簽」看到「財務」，發起人在「我送出的」看到「法務」（Seq 較小的那個）。

**U6 單據頁既有的簽核時間軸只列簽核節點與加簽；被任一到齊取消的節點在 fg2 就顯示「其他分支已完成，不需處理」。** 主控裁決（待使用者確認）（修訂 1：取消原因的文字從 fg4 提前到 fg2d）
- 推薦：`GET records/{id}/approval` 的 `stages` 對 graph 實例只放 `NodeKind` 是 `approval` 的節點實例（含加簽），不放開始、條件、分支、匯合、結束、通知；每筆多帶 `nodeId`、`branchKey`、`closeReason`（為 null 時省略，sequence 的 JSON 不變）。前端時間軸（`ApprovalStageList.vue`）對 `closeReason = branchCancelled` 的列，狀態後面加一行「其他分支已完成，不需處理」，那一列的任務狀態照舊「已結束」；`instanceEnded` 不加字（狀態「已結束」已足夠）。歷程（`actions`）照列，`branch-cancelled` 的說明見 U8。設計 U5 的「簽核中心」部分：被取消的任務是 `closed`，第一版本來就不出現在「待簽」「已處理」（同或簽「已由他人處理」）；畫面還開著時按下動作，回 409「其他分支已完成，不需處理」（J17，四語）。
- 理由：「其他分支已完成，不需處理」是設計 U5 已確認的行為，fg2g2 一開放 API，用 API 建的 graph 流程送出的單就會出現在單據頁，不能只回 `closeReason` 資料、畫面顯示一般「已結束」。瞬間節點的畫法仍是 fg4 進度圖的事（§11.3 另回 `graph`）。
- 選錯代價：照推薦，fg4 之前時間軸看不出條件走了哪條線（只看得到哪些人簽過），fg2d 多改兩個前端元件與四語文案；延到 fg4，fg2g2 到 fg4 之間被取消的簽核人只看到「已結束」，不知道是自己不用簽還是單據出了事。
- 例子：送出「主管 → 金額（條件）→ 法務、財務任一到齊 → 結束」，主管核准後時間軸列「主管：已同意」「法務：簽核中」「財務：簽核中」，沒有「金額」「分支」列；財務先核准後，法務那一列變成「已結束 · 其他分支已完成，不需處理」。法務若在簽核中心還沒重新整理就按同意 → 紅字「其他分支已完成，不需處理」。

**U7 同一次推進裡被任一到齊取消的分支，負責人不會收到「待簽」通知。** 主控裁決（待使用者確認）
- 推薦：推進途中寫的「待簽」通知延到這次推進結束才寫，期間被取消（任務已關閉）的不寫。
- 理由：任一到齊的分支依定義順序展開，排在後面的分支若不經簽核就到達匯合，前面分支剛建的任務會在同一個交易裡被取消；照第一版「啟動就通知」，那些人會收到一則點開就說「不需處理」的通知（設計 U5 的精神是不要一次發好幾則「不用簽了」）。
- 選錯代價：照推薦，引擎多一個延後寫通知的收集點；不做的話，被取消者收到內容矛盾的通知。
- 例子：任一到齊的兩條分支：一「法務」、二「金額小於一萬直接到匯合」。送出一張五千元的單 → 法務沒有收到任何通知，單據直接往匯合後走，歷程有一筆「其他分支已自動取消」。

**U8 分支取消的歷程文字以「到達的那條分支最後一個簽核節點」命名，畫面依讀者語系組字。** 主控裁決（待使用者確認）（修訂 1：名稱取法不變；改正「comment 只供稽核」的假設）
- 推薦：`branch-cancelled` 的說明「分支「{名稱}」已完成，其他分支自動取消」，名稱依序取：到達匯合那條分支上最後一個已核准或略過的簽核節點名稱 → 那條分支第一條線的 `label` → 「（未命名分支）」。`DataJson` 帶 `forkId`、`joinId`、`arrived`（到達分支的段代號）、`cancelled`（被取消分支的段代號）與 `name`（上述名稱的 `I18nText` 原值，沒有就省略）。前端 `ApprovalActionLog.vue` 對 `branch-cancelled` 不顯示 `comment`（比照 `reroute`、`fallback`），改顯示 `approval.panel.branchCancelled`（`{name}` 依讀者語系取 `name`，沒有就用 `approval.panel.branchUnnamed`）；動作名稱 `approval.action.branch-cancelled`。四語都加。`comment` 仍寫固定繁中，只留給資料庫與稽核。
- 理由：分支本身沒有名稱屬性（§7.2），設計者認得的是節點名稱；空分支只有線，退而取線的標籤。現行前端會直接顯示新動作的 `comment`（`ApprovalActionLog.vue:63` 只排除 reroute、fallback），只加動作名稱的 i18n 鍵，英文、泰文、越南文讀者會看到繁中句子。
- 選錯代價：改用分支節點的名稱（例如「法務與財務」），看不出是哪一邊先完成；不改前端，三種語系的讀者看到繁中說明。
- 例子：「法務與財務」任一到齊，財務先核准 → 繁中讀者在歷程看到「其他分支已自動取消 · 系統」下一行「分支「財務」已完成，其他分支自動取消」；英文讀者看到 `Other branches cancelled` 與 `Branch "Finance" finished; the other branches were cancelled`（節點名稱取英文版，沒有英文版照 `resolveI18n` 的退回規則）。法務的待簽任務變成已結束。

**U9 送出前預覽遇到「看不到條件內容」的條件節點：標「依條件決定」，從它的共同必經後繼接著列；沒有共同必經後繼就停。** 主控裁決（待使用者確認）（修訂 1 推翻初版的「可達集合交集」算法）
- 推薦：記錄預覽（發起人）時，條件節點的任一出線條件引用發起人看不到的欄位 → 回一筆 `conditional` 的項目（代號是條件節點、名稱是節點名稱，沒有名稱由前端顯示「依條件決定」），不列它各出線上的節點，從它的**直接後必經節點**（immediate postdominator：從它出發的每一條路都一定會經過、而且最先遇到的節點）接著列。計算時把所有結束節點接到一個虛擬終點；後必經節點是虛擬終點，代表各條路在結束前沒有共同點，預覽到這筆 `conditional` 為止。共同必經後繼落在匯合節點時，照匯合的預覽規則（U10）繼續。設計器試算照常求值、全部列出。預計路徑（`path`）同樣不含被跳過的那段。
- 理由：設計 §8.9「它之後的路徑標依條件決定，不標選哪條」。初版用「各出線可達集合的交集」只代表**有路**可到，不代表**每條路**都經過：條件分到 A、B，A 可以再選「到總經理」或「直接結束」，B 一定經總經理，總經理在可達交集裡卻不是必經；初版會讓發起人以為一定要總經理簽。只停在條件節點，則會讓會合後一定經過的節點也不見。
- 選錯代價：照推薦，Domain 多一個後必經節點的計算（節點 100 個以內，用反向圖的迭代資料流即可）；用可達交集，預覽會多列不一定會簽的人，發起人照著預覽去催人。
- 例子：「主管 → 金額（引用發起人看不到的成本欄位）→ 大：財務、小：直接 → 總經理 → 結束」，發起人預覽看到「主管（王小明）」「依條件決定」「總經理（陳大同）」。改成「大：財務 → 總經理 → 結束；小：（另一個看不到的條件）→ 總經理或直接結束」，發起人預覽只看到「主管（王小明）」「依條件決定」，不列總經理。

**U10 送出前預覽遇到任一到齊的並行區：能確定送出當下就有分支到達，只列匯合之後；不能確定，就列出全部分支並標「任一條完成即可」。** 主控裁決（待使用者確認）（修訂 1 新增，審查要求定義 any 預覽）
- 推薦：預覽依定義順序逐條預估分支。某條分支在預覽裡**確定**不經人工就到達匯合（分支上的簽核節點都是略過條件不成立或整關為空而略過，而且沒有 U9 的「依條件決定」）→ 引擎送出當下就會通過這個匯合、取消其他分支（§8.3 末點），預覽不列任何分支的節點，從匯合之後接著列；設計器試算另列那條分支上被略過的節點（狀態 `skip`）。沒有確定立即到達的分支 → 每條分支的節點都列出，這些項目帶 `anyGroup = 匯合代號`，前端在這一組第一項上方顯示「以下分支任一條完成即可」（`approval.submit.stageAnyGroup`，四語）；誰先完成不預測，匯合之後的節點照常列。全部到齊的並行區照定義順序列出全部分支，不加註記。
- 理由：人工分支誰先簽完在送出前不可知，深度優先列出一條路徑會被讀成「一定這樣走」；立即到達的空分支則可以確定（同一份定義、同一組值，引擎的展開順序固定，J5），列出會被取消的人反而誤導。
- 選錯代價：照推薦，預覽回應多一個選用欄位、前端多一行註記；不加註記，發起人會以為法務、財務兩個人都要簽；把會被立即取消的分支也列出，發起人會去催一個系統不會通知的人。
- 例子：「主管 → 法務、財務任一到齊 → 結束」：發起人預覽看到「主管」「（以下分支任一條完成即可）法務」「財務」。把財務那條改成「金額小於一萬就略過財務」，送出五千元的單：預覽只看到「主管」，沒有法務、財務；設計器試算看到「主管」「財務：會跳過」。

**U11 結構不合法（或含目前不支援的節點）的 graph 實例：撤回、終止、退回發起人、轉交、改派照常；同意、加簽、退回上一關回 409 並通知系統管理員。** 主控裁決（待使用者確認）（修訂 1 新增，審查要求明定結構再驗適用的動作）
- 推薦：動作載入實例時（`LoadAsync`）對 graph 實例做一次結構再驗（J7）。要讀圖才能做的動作（同意與加簽要推進、要重新比對路線；退回上一關要重啟並往下推進）結構不合法就 409＋異常通知、什麼都不寫；只關閉或換人的動作（撤回、終止、退回發起人、轉交、改派）不讀圖，照常做。
- 理由：結構壞掉的單據沒辦法往下走，若連撤回、終止都擋，這張單會永遠停在簽核中、記錄一直鎖著，只能改資料庫；這幾個動作只關閉執行線或換負責人，結果與圖的內容無關。同意要先重新比對、才可能重來到另一條 sequence 流程，路線比較本身就要讀圖，所以同意一律要求結構有效。
- 選錯代價：照推薦，壞掉的單據還能轉交給別人（轉過去的人同樣按不了同意）；全部擋，壞資料只能靠資料庫修；全部放行，同意會讀到空的圖而少簽。
- 例子：一張 graph 單據的綁定定義缺預設線（繞過驗證寫入）。財務按同意 → 409「流程圖定義不完整，已停止並通知系統管理員」，系統管理員收到異常通知；發起人按撤回 → 成功，記錄狀態不動；簽核管理員按終止 → 成功。

**U12 送出前預覽與設計器試算遇到結構不合法（或含目前不支援的節點）的流程圖：409「簽核流程「{名稱}」的流程圖定義不完整，請系統管理員檢查流程設定」，不通知系統管理員。** 主控裁決（待使用者確認）（修訂 2 新增，審查 M1 第 3 點）
- 推薦：預覽用專用訊息 `ApprovalGraphPreviewInvalid`（四語，fg2a2 建），句型比照既有的「簽核流程「{0}」的條件無法判斷，請系統管理員檢查流程設定」（`MessageText.cs:176`）；不登記異常通知。按下送出才回 `ApprovalGraphRuntimeInvalid`「流程圖定義不完整，已停止並通知系統管理員」並通知（U2、U11）。
- 理由：預覽不寫入、可能被反覆呼叫，比照送出登記通知會讓系統管理員被同一張還沒送出的單洗版；沿用送出的文字，畫面會說「已通知系統管理員」其實沒有通知；修訂 1 寫的既有 `RuntimeInvalid`「簽核設定與狀態機不一致」講的是狀態機，原因也不對。
- 選錯代價：照推薦，多一則四語訊息，系統管理員要等有人真的按送出才收到通知；改成預覽也通知，壞掉的流程每被預覽一次就多一則異常通知；改成沿用送出的文字，發起人以為管理員已經知道而不去聯絡。
- 例子：採購單的「大額會簽」流程缺預設線，發起人開送出前預覽 → 紅字「簽核流程「大額會簽」的流程圖定義不完整，請系統管理員檢查流程設定」，系統管理員沒有收到通知；發起人仍按送出 → 409「流程圖定義不完整，已停止並通知系統管理員」，系統管理員收到異常通知。設計器試算同一條流程 → 同一則預覽訊息。

**U13 流程圖單據的晚到動作：先說單據或分支發生了什麼，再說版本不符或「已由他人處理」。** 主控裁決（待使用者確認）（修訂 2 新增，審查 M3；J17 是實作）
- 推薦：graph 實例上的動作在取得處理權（任務層）或比對版本（實例層）之後，依 reload 後的狀態選訊息，優先序：① 實例已退回 → 409「單據已退回」；② 自己的任務因任一到齊被取消 → 409「其他分支已完成，不需處理」；③ 實例層動作（撤回、終止）版本不符、實例仍在簽核中 → 409「單據已有變動，請重新整理」；④ 其他（已核准、已撤回、已終止、任務已由他人處理、一人一票）→ 409「這一關已由他人處理」（同第一版）。sequence 實例完全照第一版：實例不在簽核中一律「這一關已由他人處理」，版本不符照舊「簽核狀態已更新，請重新整理後再試」或「這一關已由他人處理」。
- 理由：graph 的任務層動作不比對畫面版本（J16），沿用第一版「先看實例狀態、再換版本」的順序，晚到的人一律看到「已由他人處理」，分不出是整張單被退回、還是自己這條分支不用簽了；實例層動作在單據被退回時版本一定已經換過，若先講版本，發起人只看到「請重新整理」，重新整理後才發現單據已退回。
- 選錯代價：照推薦，`LoadAsync` 在 graph 模式多一次 reload 與一個共用的判斷函式，sequence 與 graph 對同一種晚到情境回的文字不同；照第一版，graph 的晚到者看到的原因與實際不符，簽核中心的設計 U5「其他分支已完成，不需處理」也落不了地。
- 例子：「法務與財務」全部到齊，法務退回發起人後，財務在還沒重新整理的畫面按同意 → 紅字「單據已退回」；發起人開著舊畫面按撤回 → 「單據已退回」，不是「單據已有變動，請重新整理」。改成任一到齊，財務先核准後法務按同意 → 「其他分支已完成，不需處理」。同樣的退回情境發生在 sequence 單據上，晚到的同意照舊是「這一關已由他人處理」。

---

## Global Constraints

- **既有行為不變**：sequence 實例（含進行中的舊單與新送出的 sequence 流程）走第一版程式，歷程、關卡實例、任務、通知、回應 JSON 逐筆相同；所有既有測試不改斷言就通過。`ApprovalShapeGateTests` 在 fg2a1–fg2g1 期間**一條都不改**（正式環境的擋門照舊），fg2g2 刪檔、案例改寫搬到各 graph 測試類。
- **graph 判斷只有一個**：`ApprovalGraphMode.Applies(inst, def)` = 定義 `Shape == graph` 而且 `inst.RouteKind == flow`（J2）。備援實例即使綁的是 graph 流程，仍走第一版（單一關或簽）。
- **正式環境的 graph 執行入口一直關到 fg2g2**（J1）：fg2a2 把 `ApprovalShapeGate` 改成 DI 註冊的服務，預設 `Open = false`，四個呼叫點照舊回 409；測試以 `ConfigureServices` 換成開啟版才跑得到引擎。開關不讀設定檔、沒有環境變數，正式環境改不動。API 存檔與試算到 fg2g2 才收 graph（驗證器的 `shape` 擋門）。所以 fg2a1–fg2g1 任何一段上線或回退到其中任何一段，graph 定義與 graph 實例的行為都和 fg1 一樣：一律 409、什麼都不寫。
- **回退下限**：資料庫一旦有 graph 版本（fg2g2 之後），能**執行** graph 的最低版本是 fg2g2；回退到 fg1a–fg2g1，graph 實例全部 409 凍結（不少簽、不寫入，但撤回、終止也做不了），重新升級即恢復；回退到 fg1a 之前不支援（設計 §13 第 7 點）。fg2g2 的部署回報要寫明這三句。
- **結構不合法一律擋**：送出、預覽、重來與 `LoadAsync` 對 graph 定義跑執行期結構再驗與能力守門（J7）；不合法時，要讀圖的入口與動作回 409 `GraphRuntimeInvalid` 並登記異常通知，不推進；預覽回 409 `GraphPreviewInvalid`、不登記（U12）；不讀圖的動作照 U11 放行。條件節點無法判斷回 409 `ConditionNodeUndecidable` 並登記異常通知，不走預設線（§8.8）。每個入口回哪則訊息見 J7 對照表。
- **唯一完成者**：`ApprovalEngine.AdvanceAsync` 是送出、同意、重來三條路徑上唯一呼叫 `CompleteAsync` 的地方；圖引擎走到結束只回報「已到結束」，呼叫端不再自己呼叫 `CompleteAsync`（J22）。
- **業務政策不寫死**：fg2 不新增政策。加簽人數與關卡數、自簽、逾期、意見字數照既有政策；新增的常數只有技術限制（處理權重試次數與退避，§15），列在可寫死清單，集中在 Domain `ApprovalGraphLimits`。
- **不加 migration**：fg1a 的 `ApprovalGraph` 已加齊 `NodeKind`、`InEdgeId`、`CloseReason`、`BranchKey`（256）。fg2 任何子段都不跑 `dotnet ef migrations add`；需要新欄位就停下來回報。
- **訊息**：使用者看得到的新訊息走 `Application/Common/MessageText.cs` 的四語系表（簽核訊息已搬進去），`ApprovalMessages.cs` 只放對照。新歷程動作的 `comment` 寫固定繁中、只給資料庫與稽核；**前端不顯示它**，`ApprovalActionLog.vue` 要明確排除並依 `DataJson` 組字（現行前端只排除 reroute、fallback，會直接顯示其他動作的 comment，U8）。Domain 不寫訊息文字。
- **i18n（前端）**：fg2d 加 `approval.action.branch-cancelled`、`approval.panel.branchCancelled`、`approval.panel.branchUnnamed`、`approval.stageCloseReason.branchCancelled`；fg2e 加 `approval.submit.stageAnyGroup`。四語鍵集合一致、單行 JSON；th、vi 不留中文；改前從當時的 main 重讀檔案。
- **後端測試**：新 ApplicationTests 測試類 `protected override bool ScopePerRequest => true;`，跑 graph 的測試類覆寫 `ConfigureServices` 呼叫 `ApprovalGraphTestKit.OpenGate(services)`；新 WebAPI 測試標 `[Trait("Category", "Postgres")]`。只跑窄範圍 filter，全套交 gate（GitHub Actions）。重的測試一律經測試鎖：先 `TL=<unattended skill>/scripts/test-lock.sh`，驗收指令寫成 `sh $TL dotnet test ...`。不和其他吃 CPU 的工作同時跑。本機只驗 SQLite；PostgreSQL 標「未驗證」：現在的 `TestApp` 寫死 SQLite 連線，`Postgres` trait 只是標記，pg0 要補上 provider 切換才算真的跑過。
- **執行期共用輔助集中一次**（修訂 2，審查 M1）：`ApprovalGraphMode` 的 `Applies`、`SupportedKinds`、`Eval`，以及結構不合法的兩則訊息 `ApprovalGraphRuntimeInvalid`、`ApprovalGraphPreviewInvalid`，都在 **fg2a2** 建好定案；fg2b、fg2c、fg2e 只引用、不改、不另寫一份。
- **測試輔助集中一次**：graph 的測試建構函式、開關替換寫在新檔 `poc/backend/tests/DynamicModules.ApplicationTests/Dynamic/Approvals/ApprovalGraphTestKit.cs`；`ApprovalTestBase.cs` 只改一次（加 `SeedGraphFlow`）。兩者都在 **fg2a2** 建好，fg2b、fg2e 都只依賴 fg2a2、不依賴彼此。之後各段的輔助方法寫在自己的測試檔，誰都不再改這兩個檔。WebAPI 的 `TestApp.cs` 只在 fg2f 加一個服務替換掛點。
- **公開 repo**：文件與程式不寫公司名、商用 BPM 品牌名、個人絕對路徑、內網 IP；測試資料全部虛構。
- **git**：逐檔 `git add`、commit 前 `git status`；不用 `--no-verify`、不 force push、不 merge commit；Conventional Commits，scope `approval`、`docs`；不 push，由主控合併。

### 可寫死清單（fg2 新增）

| 項目 | 值 | 類別 |
|---|---|---|
| graph 執行入口開關 | 正式環境固定關閉到 fg2g2，只有測試可換 | 守衛（回退安全，J1） |
| 執行期結構再驗不過、含不支援的節點型別 | 要讀圖的動作 409＋異常通知；撤回、終止、退回發起人、轉交、改派照常 | 完整性（§8.8、J7、U11） |
| 引擎支援的節點型別（`ApprovalGraphMode.SupportedKinds`） | 開始、結束、簽核、條件、分支、匯合（fg5 加通知） | 完整性：能力守門（U2） |
| 預覽遇到結構不合法的流程圖 | 409 `GraphPreviewInvalid`，不登記異常通知 | 完整性（U12） |
| graph 晚到動作的訊息優先序 | 已退回 → 分支已完成 → 版本不符 → 已由他人處理 | 完整性（U13、J17） |
| 條件節點無法判斷 | 409＋異常通知，不走預設線 | 完整性（§8.8、§15） |
| 簽核節點略過條件無法判斷 | 視為成立、不略過 | 完整性（第一版） |
| 分支出線推進順序 | 定義中的順序 | 技術（J6 設計、本計畫 J5） |
| 任一到齊時取消其他分支；任何分支退回發起人整張退回 | 固定 | 使用者決定 |
| 走到結束時還有其他進行中的執行線 | 409＋異常通知 | 完整性（G8 的防線） |
| 處理權條件更新的重試 | 5 次、退避 20、40、80、160 ms | 技術限制（§15「5 次」） |
| 撤回、終止比對畫面的實例版本 | 不重試 | 完整性（§8.13） |
| `BranchKey` 格式：外到內出線代號以 `/` 串接，根段空字串 | 固定 | 技術（§7.6） |
| graph 流程不能存回 sequence | 400（U4） | 完整性：只留一個設計器 |
| 通知節點在 fg5 前拒收 | 存檔 400、執行期 409＋異常通知（U2） | 暫時，fg5 拆 |
| 匯出遇到 graph 流程 | 409（U3） | 暫時，fg6 拆 |

---

## Review Focus

測試沒覆蓋就最可能咬人的十三個情境；每條的測試已放進負責的子段。

1. **退回上一關沿用第一版「關掉整個實例的 open 任務與排隊加簽」。** `RejectTaskHandler.Reject` 呼叫 `CloseOpen(tasks)`、`CloseQueued(runs)` 都以整個實例為範圍；分支內退回上一關會把另一條分支的待簽一起關掉。→ fg2c `ReturnPrevious_InBranch_OtherBranchTaskStaysOpen`。
2. **退回發起人只關目前關卡。** 第一版只把被退回的那一關改 `closed`，另一條分支的 `active` 節點實例留著。→ fg2c `RejectToInitiator_InBranch_AllLinesClosed_InstanceEnded`（被退回的那一列也要 `CloseReason = instanceEnded`）。
3. **同流程同版本的 graph 沒有路線比較。** `Recheck` 在同流程同版本時只比 `Stages`，graph 的 `Stages` 是空的，條件節點改選出線也不重簽（少簽）；換了流程、版本或切到備援仍會判定 Changed（`ApprovalRouter.cs:57-67`），缺的只有 graph 內部的路線比較。→ fg2c `Recheck_ConditionEdgeChanged_Rerouted`。
4. **任一到齊在推進當下就到達。** 先展開的分支已建任務、已寫通知，同一交易裡又被取消。→ fg2b `AnyJoin_LaterBranchArrivesImmediately_EarlierTasksClosed_NoPendingNotice`。
5. **重試後拿到 EF 追蹤中的舊實體。** 現在沒有自動重試，這是 fg2f 加重試後才會出現的機制問題：handler 在取得處理權之前已用追蹤查詢讀過任務與實例（`ApprovalRepositories.cs:121、153`），條件更新只同步 RowVersion（同檔 136），重試成功後 `ListTasksAsync` 回傳同一個已追蹤物件，`Status` 還是舊的。→ fg2f `Claim_RetryAfterConcurrentCommit_SeesFreshTaskStatus`。
6. **異常通知跟著主交易回滾而遺失。** `ApprovalAlert` 只記實例 id，送出時建的實例被回滾後 `WriteApprovalAlertHandler` 找不到實例、回 NotFound、不寫 log。graph 的條件無法判斷最常發生在送出當下。→ fg2b `Submit_ConditionUndecidable_409_AlertWrittenAfterRollback`。
7. **備援實例誤走圖引擎。** 重新比對沒有流程符合時建的備援實例沿用舊的綁定版本（graph），若只看 `Shape` 就分派，單一關的備援會被當成流程圖。→ fg2c `FallbackInstanceOfGraphFlow_UsesSequencePath`。
8. **加簽的 `Seq` 在並行時撞號或順序和第一版不同。** 第一版把加簽插在 `run.Seq + 1` 並把後面的排隊關卡順延，前提是目前關卡之後只有排隊關卡；並行時其他分支的節點實例也在後面。→ fg2d `AddSign_Nested_InBranch_OrderSameAsSequence_SeqMonotonic`。
9. **完成動作執行兩次。** 現行送出（`ApprovalHandlers.cs:83-90`）、同意（`ApprovalActionHandlers.cs:189-195`）、重來（`ApprovalEngine.cs:198-205`）都在「下一關是 null」時自己呼叫 `CompleteAsync`；圖引擎若在結束節點也呼叫，會寫兩筆 `complete`、兩則核准通知，第二次轉移失敗還會整筆回滾。→ fg2b `Submit_GraphEnd_CompleteOnce`、`Approve_GraphEnd_CompleteOnce`、`Reroute_ToGraphAllSkipped_CompleteOnce`。
10. **空分支到達匯合時認錯分支。** 合法的 `fork → join` 直連，進線起點是分支節點，它的 `BranchKey` 是外層段 P，實際到達的分支卻是 `Child(P, 這條線)`；用「進線起點最近一列的 BranchKey」推，會把空分支當成外層、全部到齊永遠等不到、任一到齊取消錯對象。→ fg2a1 `ArmOf_DirectForkToJoin_IsThatEdge`、fg2b `Submit_EmptyBranchAllJoin_ArrivalCountsAsThatBranch`。
11. **加簽總數的檢查在處理權之外。** `AddSignTaskHandler` 在載入實例之前算加簽筆數（`ApprovalActionHandlers.cs:261-264`）。上限 5、目前 4 筆，兩條分支同時加簽都看到 4，就算之後序列化也會各加一筆變 6。→ fg2f `AddSign_TwoBranchesAtLimitMinusOne_SecondGets400`。
12. **預覽把「有路可到」當「一定經過」。** 可達集合的交集會列出不是每條路都經過的節點。→ fg2e `Walk_HiddenCondition_ReachableButNotPostdominator_NotListed`。
13. **晚到動作的訊息被處理權之前的舊檢查攔下。**（修訂 2，審查 M3）`LoadAsync` 在換 RowVersion 之前就判斷 `inst.Status != Pending` 回 `StageHandled`（`ApprovalActionHandlers.cs:50`），四個 handler 也各自 `me.Status != Open → StageHandled`（同檔 114、289、400、468）；只在處理權之後換訊息，晚到的人永遠走不到「單據已退回」「其他分支已完成」。→ fg2f `LateApprove_InstanceRejectedByOtherBranch_409InstanceReturned`、`LateApprove_BranchCancelled_409BranchCompleted`、`Sequence_LateApprove_AfterReject_StillStageHandled`。

---

## 範圍確認

### 已在 main 核對（origin/main `916268f1`，已含 fg1d；只接、不重做）

| 項目 | 證據 |
|---|---|
| graph 型別：`ApprovalDefinition.Nodes`／`Edges`（null 不寫出）、`Stages` 非 null（graph 為空陣列）、`ApprovalNodeDef`（`AsStage()`）、`ApprovalEdgeDef`、`ApprovalNodeKinds`、`ApprovalShapes.Graph` | `Domain/Approvals/ApprovalDefinition.cs`（fg1b） |
| 參照集中：`ApprovalDefinitionRefs.Steps`（關卡或簽核節點 `AsStage()`）、`Conditions`、`NotifyFieldRefs`、`GroupRefs`；閉包、鎖定、約束、守衛、群組參照已改讀 | `ApprovalDefinitionRefs.cs:12`；fg1b 結果檔 |
| 轉換 `ApprovalGraphConversion.FromSequence`（已是 graph 原樣回傳；節點屬性與來源共用參照） | `ApprovalGraphConversion.cs:11` |
| 結構驗證 `ApprovalGraphValidator.Check(d, GraphPolicyLimits, previous)` 回 `GraphCheck(Errors, Warnings)`；建圖 `ApprovalGraphModel`（`Out`、`In`、`Reach`、`IsAcyclic`，依定義順序） | `ApprovalGraphValidator.cs:29`、`ApprovalGraphModel.cs`（fg1c） |
| 技術上限 `ApprovalGraphLimits`（節點 100、線 200、巢狀 5、通知相關） | `ApprovalGraphLimits.cs` |
| 節點實例欄位 `NodeId`、`BranchKey`（256）、`NodeKind`、`InEdgeId`、`CloseReason`；`Seq` 索引不是唯一索引 | `Entities/Dynamic/Approval.cs:86-90`、`ApprovalConfigurations.cs:83-88`、migration `20261114115459_ApprovalGraph` |
| 節點實例狀態只有 `queued`、`active`、`approved`、`skipped`、`returned`、`closed`（沒有 `passed`、`waiting`、`CloseReason` 常數） | `Approval.cs:143-146`（J13 延到 fg2） |
| 政策 `approval.maxBranches`（A37）；`ApprovalLimitSet.MaxBranches` | `PolicyCatalog.cs:100`、`ApprovalDefinition.cs:111` |
| 只有草稿的流程：`ListCurrentByModuleAsync` 只含已發布、`FlowState.VersionId`、`ListByModuleAsync`；PUT 對沒有版本的流程 409 | `ApprovalRepositories.cs`、`ApprovalFlowHandlers.cs:124`（fg1a） |
| 執行期擋門 `ApprovalShapeGate.Reject` 四個呼叫點：送出 `ApprovalHandlers.cs:54`、預覽 `ApprovalPreviewer.cs:85`、全部動作 `ApprovalActionHandlers.cs:53`（`LoadAsync`，在換 RowVersion 之前）、同意後重新比對 `ApprovalActionHandlers.cs:135` | grep `ApprovalShapeGate` |
| 存檔與試算擋門：`ApprovalDefinitionValidator.Graph.cs` 末尾固定加 `x.Err("shape", ApprovalMessages.GraphNotAccepted)`（fg1d，已合併） | `ApprovalDefinitionValidator.Graph.cs:65` |
| 讀 `def.Stages` 的執行期位置：推進 `ApprovalEngine.cs:55-57`、退回上一關重啟 `:244`、自選與使用者欄位檢查 `ApprovalHandlers.cs:102、129`、同意的可編輯欄位 `ApprovalActionHandlers.cs:120`、重新比對 `ApprovalRouter.cs:57、64`、預覽 `ApprovalPreviewer.cs:110`、檢視可編輯鍵 `ApprovalQueryHandlers.cs:355`、`ApprovalPath.NextStageIndex`；套件 `AppApprovalFlowCodec.cs:150`、`AppPackageValidator.V8.cs`（fg6） | grep `\.Stages\b` |
| 讀 `CurrentStageRunId` 當「唯一目前關卡」：撤回 `ApprovalActionHandlers.cs:360`、終止 `:430`、重新比對 `ApprovalRouter.cs:63`、記錄摘要 `ApprovalQueryHandlers.cs:56`、簽核中心 `:251、269`、檢視撤回資格 `:339`、後盾 `ApprovalBackstopStage.cs:258`；寫入 `ApprovalEngine.cs:75、120、151、276` 與三個結束動作 | grep `CurrentStageRunId` |
| 動作載入：`LoadAsync` 先 `TryBumpRowVersionAsync(id, 請求版本, 新值)`（`ExecuteUpdate` 條件更新，`ApprovalRepositories.cs:136`），失敗回 409；`RowVersion` 是並行權杖 | `ApprovalActionHandlers.cs:40-60`、`ApprovalConfigurations.cs:51` |
| 交易：每個 `ITransactionalRequest` 由 `TransactionBehavior` 包一個交易；SQLite 由 `WriteGate` 排隊後 `BEGIN IMMEDIATE`，同一個資料庫的寫入請求整筆序列化 | `TransactionBehavior.cs`、`EfTransactionManager.cs:20-24` |
| 異常通知：`ApprovalAlert.Raise(instanceId, reason)`，主交易回滾後由 `ApprovalAlertBehavior` 另開交易寫；handler 以實例 id 讀實例，找不到回 NotFound | `ApprovalAlert.cs:15-35`、`ApprovalAlertBehavior.cs` |
| 退回、撤回、終止的關閉範圍：`CloseOpen(tasks)`、`CloseQueued(runs)` 以整個實例為範圍；退回發起人只把目前關卡改 `closed` | `ApprovalActionHandlers.cs:75-88、319、337-345` |
| 加簽：`queued`＋`StageCode = null`＋`AddedByUserId`＋`AddSignUserIds`；插在 `run.Seq + 1 + 同關先前加簽數`，後面的排隊關卡順延；`ActivateNextAsync` 先啟動排隊加簽 | `ApprovalActionHandlers.cs:154-167`、`ApprovalEngine.cs:46-48` |
| 後端訊息四語系表 `MessageText`（有 `MessageTextTests`）；簽核訊息已搬入（`ApprovalStageHandled`、`ApprovalStateChanged`、`ApprovalCannotWithdraw`、`ApprovalNoPreviousStage`、`ApprovalRuntimeInvalid`、`ApprovalFlowUndecidable`） | `Common/MessageText.cs:48、176、218-243` |
| 匯出錯誤：`AppApprovalFlowCodec.ToPackage` 收集細節，整份 409 `AppMessages.ExportFlowError` | `AppApprovalFlowCodec.cs:137-158`、`AppService/Data/AppResponses.cs:107` |
| 前端舊對話框存檔一律組 `shape: 'sequence'`；卡片啟停送回原定義 | `src/domain/dynamic/approval-flow-draft.ts:107`、`approval-designer/ApprovalFlowsTab.vue:52-56` |
| 前端歷程動作文字 `approval.action.<代號>`，缺鍵時由 `labelOf` 退回；`comment` 除了 reroute、fallback 一律直接顯示 | `approval/ApprovalActionLog.vue:54、63` |
| 前端時間軸：狀態文字 `approval.stageStatus.<狀態>`（`closed` = 「已結束」），沒有任何取消原因的顯示 | `approval/ApprovalStageList.vue:25`、`i18n/zh-TW.json` |
| 預覽元件依 `status` 顯示 `conditional`、`pick`、`skip` 的說明；`PreviewStage` 沒有分組欄位 | `approval/PreviewStages.vue:16-20`、`ApprovalModels.cs:105` |
| 完成的呼叫端：送出、同意、重來各自在 `ActivateNextAsync` 回 null 後呼叫 `CompleteAsync` | `ApprovalHandlers.cs:83-90`、`ApprovalActionHandlers.cs:189-195`、`ApprovalEngine.cs:198-205` |
| 排隊加簽的分派：`ActivateNextAsync` 先從**整個實例**挑 `Seq` 最小的 queued | `ApprovalEngine.cs:45-48` |
| 載入前的狀態檢查：加簽關卡數在 `LoadAsync` 之前算（以歷程 `add-sign` 筆數）；實例 pending 的判斷在換 RowVersion 之前 | `ApprovalActionHandlers.cs:50、261-264` |
| 處理權之後、各 handler 自己的任務狀態檢查：同意、退回、轉交、改派都是 `me.Status != Open → StageHandled`；撤回、終止沒有任務 | `ApprovalActionHandlers.cs:114、289、400、468` |
| `Loaded` 是 positional record，有 6 處 5 元解構 | `ApprovalActionHandlers.cs:35`；解構在 112、287、359、398、427、466 |
| 執行期再驗失敗的出口 `ApprovalEngine.RuntimeInvalid(inst, reason)` 固定回 `ApprovalMessages.RuntimeInvalid`（「簽核設定與狀態機不一致…」） | `ApprovalEngine.cs:265-269`、`MessageText.cs:242` |
| 條件求值：`ApprovalRouter.Match`（解析失敗 → Undecidable）、`Holds`（解析失敗視為成立）都是 `ConditionParser.Parse(…, ConditionUsage.Rule, userIds)` 加 `ConditionEvaluator.Evaluate` | `ApprovalRouter.cs:22-45` |
| 通知列帶 `SourceType = "approval"`、`SourceId = inst.Id`；`Link` 是記錄層級（`/dynamic/{模組}/{記錄}`），`withLink = false` 時為空字串 | `ApprovalNotifier.cs:25` |
| fg2a1 已實作（未合併時以分支為準）：`ApprovalGraphRuntime`、`ApprovalGraphPath`、狀態與原因常數、`ApprovalPath.Effective(runs, includePassed)` | `feature/fg2a1` 的 `19d1a7de`、`c95c86d8`；`fg2a1-result.md` |
| 交易失敗即回滾：`TransactionBehavior` 對 `IsSuccess = false` 的回應 `RollbackAsync`（所以處理權之後回 400，換掉的 RowVersion 也一起回滾） | `Common/Behaviors/TransactionBehavior.cs:20-24` |
| ApplicationTests 的 `DbContext` 是單一共用實例（`AddSingleton(Db.Context)`），各 scope 共用同一條連線與交易 | `DesignerTestBase.cs` `InitializeAsync` |
| WebAPI `TestApp` 寫死 SQLite（記憶體或檔案）；`ConfigureWebHost` 內有 `ConfigureServices` 但沒有給測試類的替換掛點 | `WebAPITests/TestApp.cs:42-56、131-160` |
| 警示寫入：`ApprovalAlertBehavior` 只 catch 例外，不看 `WriteApprovalAlertRequest` 回的失敗結果 | `ApprovalAlertBehavior.cs:24-31` |
| 測試掛點：`DesignerTestBase.ConfigureServices`（可換服務註冊）；WebAPI `TestApp(fileDatabase: true)` 是檔案型 SQLite（真的 WriteGate）；`TestApp.Services` 可開 scope 直接寫資料庫 | `DesignerTestBase.cs:57`、`InventoryConcurrencyTests.cs` |
| DI：簽核服務全在 `Application/DependencyInjection.cs:139-161` 以 `AddScoped` 註冊 | 同左 |

**fg1d 已合併**（main `916268f1` 含 `56b41952`…`d5ae6594`）：W1–W3、`ApprovalDefinitionValidator.Graph.cs`（G6 語法、G9、G10、訊息對照、`shape` 擋門）、`ApprovalMessages.Graph(code, …)`、設計寫回 U14–U21 與 J19–J27。API 仍拒收 graph（擋門在 `Graph.cs:65`）；設計 §19 最後的編號是 U21、J27，本計畫寫回時從 U22、J28 起。

### 這次做

| 設計項目 | 子段 |
|---|---|
| 節點實例狀態 `passed`、`waiting`，`CloseReason`，歷程 `branch-cancelled`；分支段代號、匯合進線所屬分支、能力守門（`Build` 收支援型別）、選線、上一關、取消分支、撤回資格（Domain 純函式） | fg2a1（已實作） |
| §8.7 路線比較（Domain）；graph 執行入口開關（正式環境關閉）；`ApprovalGraphMode`（graph 判斷、支援的節點型別、條件求值轉接）；結構不合法的兩則訊息；測試輔助 `SeedGraphFlow`、`ApprovalGraphTestKit` | fg2a2 |
| §8.1–§8.3 推進、條件、並行、兩種匯合、分支取消；§8.8 條件無法判斷；§8.9 自選全部要選；§8.12 通知節點（能力守門擋，U2）；唯一完成者；送出入口；同段同主節點的排隊加簽分派 | fg2b |
| §8.2 同意後推進；§8.7 重新比對與重來；§8.4 退回發起人、退回上一關；§8.5 撤回；§8.6 終止；載入時的結構再驗與動作對照（U11） | fg2c |
| §8.6 後加簽、轉交、改派、代理在分支內；檢視（時間軸、撤回與退回資格、可編輯鍵）與簽核中心（U5、U6）；取消原因與分支取消說明的畫面與四語（設計 U5、U8） | fg2d |
| §8.9 預覽與設計器試算的預計路徑（U9 共同必經後繼、U10 任一到齊） | fg2e |
| §8.13 以實例序列化、任務層自動重試、實例層比對版本、處理權內的狀態檢查、晚到動作的訊息；兩種資料庫的並發測試（PostgreSQL 待 pg0） | fg2f |
| 等價性測試（§14，正規化關卡對應、任務、席位、代理、歷程、通知） | fg2g1 |
| API 接受 graph 存檔與試算；刪 `ApprovalShapeGate` 與開關；U2–U4；政策設計 B27；設計與簽核設計寫回、路線、功能清單 | fg2g2 |

### 不做

| 項目 | 理由 |
|---|---|
| 草稿、發布、捨棄草稿端點；`DraftJson` 寫入；啟用沒有版本的流程回 400 | fg3（U1） |
| `GET approval-flows/{key}` 對 sequence 回傳轉換後的 graph；`LayoutJson` 驗證；發布回應帶 W1–W3 | fg3（設計 §10.7、§10.8） |
| 記錄簽核回應加 `graph`、舊實例轉直線圖、「簽核中（財務、法務）」摘要 | fg4 |
| 通知節點的推進、收件人解析、範本、逐人遮蔽、`DataJson`（fg2 只做能力守門） | fg5（U2） |
| 套件格式升版、匯入舊格式轉換、批次轉換、手冊章節 | fg6 |
| 節點實例層 `RowVersion` | 設計 J18 已取消 |

---

## 子段切法與合併順序

| 子段 | 內容 | 估計行數（含測試） | 正式環境開放給 graph 的入口 | 依賴 |
|---|---|---|---|---|
| fg2a1 | Domain 執行期地基：狀態與原因常數、`ApprovalGraphRuntime`（含匯合進線所屬分支 `ArmOf`、能力守門）、`ApprovalGraphPath`、`ApprovalPath.Effective` 加 `includePassed` | **已實作** +682 −4（`19d1a7de`、`c95c86d8`） | 無（沒有呼叫端） | fg1c（已在 main） |
| fg2a2 | `ApprovalGraphRoute`（Domain）；`ApprovalShapeGate` 改成 DI 服務（正式環境關閉）；`ApprovalGraphMode`（`Applies`、`SupportedKinds`、`Eval`）；`ApprovalGraphRuntimeInvalid`、`ApprovalGraphPreviewInvalid` 兩則四語訊息；`SeedGraphFlow`、`ApprovalGraphTestKit` | 約 610（修訂 2 吸收 M1，+110） | 無（擋門照舊） | fg2a1 |
| fg2b | `ApprovalGraphEngine`：從開始推進、各型別節點、匯合、取消、延後通知、同段同主節點的排隊加簽；`AdvanceAsync` 唯一完成者（送出、同意、重來三個呼叫端）；`RuntimeInvalid` 加訊息參數；送出入口；異常通知快照 | 約 760 | 無（測試開關下可送出） | fg2a2 |
| fg2c | 同意後推進、重新比對與重來、退回發起人、退回上一關、撤回、終止；`LoadAsync` 的結構再驗與動作對照（U11） | 約 790 | 無 | fg2b |
| fg2d | 加簽、轉交、改派、代理在分支內；檢視與簽核中心；時間軸取消原因、歷程分支取消說明的前端與四語 | 約 680 | 無 | fg2c |
| fg2e | 預覽與設計器試算的預計路徑（共同必經後繼、任一到齊）；預覽的任一到齊註記前端與四語 | 約 750 | 無 | fg2a2 |
| fg2f | 實例序列化、自動重試、處理權內的狀態檢查（含加簽總數）、實例層版本、晚到訊息與 `LateConflict`（J17、U13）、並發測試 | 約 780 | 無 | fg2d |
| fg2g1 | 等價性測試（純測試，擴充後的比較口徑） | 約 420 | 無 | fg2d |
| fg2g2 | API 收 graph、刪 `ApprovalShapeGate` 與開關、U2–U4、擋門測試改寫搬家、文件 | 約 560 | **全部**：存檔、試算、送出、預覽、所有動作 | fg2e、fg2f、fg2g1 |

合計約 6030 行（fg2a1 以實際行數計），切成九段（修訂 1 把初版的 fg2a 拆成 fg2a1、fg2a2，fg2g 拆成 fg2g1、fg2g2；初版各段代號與內容的對照見 `plan-rev1-notes.md`；修訂 2 的行數變動見 `plan-rev2-notes.md`）。fg1d 已在 main，各段不再等它。每段合併後既有行為不變：

- fg2a1：純新增 Domain 檔與常數，沒有呼叫端。
- fg2a2：擋門改成服務後，正式環境四個呼叫點的判斷與訊息完全相同（`ApprovalShapeGateTests` 不改就通過）；其餘是 Domain 新檔、`ApprovalGraphMode`（沒有正式呼叫端）、兩則還沒有人回的訊息與測試輔助。
- fg2b–fg2g1：sequence 實例程式路徑不變（回歸測試）；`AdvanceAsync` 對 sequence 是原本「`ActivateNextAsync` → 回 null 就 `CompleteAsync`」的同一串呼叫。正式環境的 graph 入口全部仍由擋門回 409，graph 只在開了測試開關的測試裡跑。
- fg2g2：一次打開全部 graph 入口；sequence 的存檔、送出、動作不變（fg2g1 的等價性測試證明轉換後的 graph 結果相同）。

**為什麼改成「最後一段一次打開」**（J1，修訂 1）：初版逐段拆擋門，論證是「每段只開放已經會正確處理 graph 的入口」。這只對「資料庫從沒有 graph」成立；擋門真正保護的是「資料庫已有 graph、程式被回退」（設計 §13 第 7 點）。例如回退到初版的 fg2c：同意已開放，但 graph 的排隊加簽仍走第一版「從整個實例挑最小 queued」，兩條分支各有加簽時會啟動到別條分支的加簽；並發語意也要到 fg2f 才完整。正式環境一直關到 fg2g2，回退到 fg2a1–fg2g1 任一段都與 fg1 相同（409、不寫入），就不必證明每一段的中間狀態對 graph 都正確。代價是 fg2g2 一次開放的面比較大，由 fg2g1 等價性與 fg2b–fg2f 各自的 graph 測試先行保護。

**合併順序：** 預設 fg2a1 → fg2a2 → fg2b → fg2c → fg2d → fg2e → fg2f → fg2g1 → fg2g2。fg2a1 已實作、等審查合併；fg2a2 在 fg2a1 合併後開；fg2b 起等本修訂經主控確認（fg1d 已合併，不再是條件）。

**可以平行的組合**（同檔不同段落也算共用，已排除）：
- fg2a1 與 fg2a2 之間必須依序（Route、`ApprovalGraphMode` 用 Runtime 的型別）。
- **fg2e ∥ fg2b、fg2c、fg2f、fg2g1 任一段**：fg2e 改 `ApprovalPreviewer.cs`、`ApprovalModels.cs`（預覽回應）、`PreviewStages.vue`、`approval-types.ts`、四語前端 i18n，新增 `ApprovalGraphPreview.cs` 與測試；只依賴 fg2a2 的開關、`ApprovalGraphMode`（`Applies`、`SupportedKinds`、`Eval`）、`ApprovalMessages.GraphPreviewInvalid` 與測試輔助（修訂 2，審查 M1：這些在 fg2a2 定案，fg2e 不碰 `ApprovalGraphEngine.cs`、`MessageText.cs`、`ApprovalMessages.cs`）。與 fg2b、fg2c、fg2f、fg2g1 沒有共用檔。**不能和 fg2d 平行**（共用 `ApprovalModels.cs`、`approval-types.ts`、四語 i18n）。
- **fg2f ∥ fg2g1**：fg2g1 只新增 `ApprovalGraphEquivalenceTests.cs`。後合併的一方要把對方的測試類加進自己的驗收 filter 再跑一次（fg2f 改了 graph 的處理權語意，等價性測試用正確的 rowVersion，預期不受影響）。
- fg2b → fg2c → fg2d → fg2f 必須依序：都改 `ApprovalActionHandlers.cs`，fg2b、fg2c 也都改 `ApprovalEngine.cs`。

照使用者記憶「Formwright 長任務用 subagent、不開 pane」，平行時用兩個 worktree＋兩個 subagent（fg2a1 實際在 `../dynamic-fields-design.fg1` 做；之後主線沿用它或另開 `dynamic-fields-design.fg2`，副線開 `dynamic-fields-design.fg2p`）：主線 fg2a1 → fg2a2 → fg2b → fg2c → fg2d → fg2f → fg2g2；副線在 fg2a2 合併後做 fg2e，在 fg2d 合併後做 fg2g1。只開一個 worktree 就照預設順序。fg2b 若實作後超過 900 行，在 Task 4 的「推進核心」與「唯一完成者、送出入口、異常通知」之間切成 fg2b1、fg2b2，各自可合併；fg2c 若超過 900 行，Task 5、Task 6 各自成段。

---

## 共用檔

| 類別 | 檔案 | 子段 |
|---|---|---|
| 後端 DI | `Application/DependencyInjection.cs`（`AddSingleton<ApprovalShapeGate>()`；`AddScoped<ApprovalGraphEngine>()`；刪開關註冊） | fg2a2、fg2b、fg2g2 |
| DbContext／migration | 不碰（欄位 fg1a 已加齊；`ApprovalConfigurations.cs` 不改） | — |
| 實體常數 | `poc/backend/src/DynamicModules.Domain/Entities/Dynamic/Approval.cs`（`ApprovalStageStatus.Passed`／`Waiting`、`ApprovalCloseReasons`、`ApprovalActions.BranchCancelled`） | fg2a1（已實作） |
| Domain 既有路徑函式 | `poc/backend/src/DynamicModules.Domain/Approvals/ApprovalPath.cs`（fg2a1 給 `Effective` 加選用參數 `includePassed = false`，sequence 不傳；之後改這個檔要保留預設值與 graph 段路徑的呼叫） | fg2a1（已實作）；fg2c、fg2d 若要改只能依序 |
| 後端訊息（i18n） | `poc/backend/src/DynamicModules.Application/Common/MessageText.cs` | fg2a2（`ApprovalGraphRuntimeInvalid`、`ApprovalGraphPreviewInvalid`）、fg2b、fg2f |
| 訊息對照 | `ApprovalMessages.cs` | fg2a2、fg2b、fg2f、fg2g2 |
| 前端 i18n | `poc/frontend/src/i18n/{zh-TW,en-US,th,vi}.json` | fg2d、fg2e |
| 前端簽核元件 | `poc/frontend/src/presentation/dynamic/approval/ApprovalStageList.vue`、`ApprovalActionLog.vue`（fg2d）；`PreviewStages.vue`（fg2e）；`poc/frontend/src/domain/dynamic/approval-types.ts`（fg2d、fg2e） | 見左 |
| 前端路由、選單 | 不碰 | — |
| `poc/backend/apps/*.app.json` | 不碰 | — |
| 簽核核心 | `ApprovalShapeGate.cs`（a2 改服務、g2 刪）、`ApprovalGraphMode.cs`（a2 建；之後只引用，fg5 改 `SupportedKinds`）、`ApprovalHandlers.cs`（a2、b）、`ApprovalPreviewer.cs`（a2、e）、`ApprovalActionHandlers.cs`（a2、b、c、d、f）、`ApprovalEngine.cs`（b、c）、`ApprovalRouter.cs`（c）、`ApprovalQueryHandlers.cs`（d）、`ApprovalModels.cs`（d、e）、`ApprovalGraphEngine.cs`（b 建；c 若要改只能依序段修改） | 見左 |
| 儲存庫 | `poc/backend/src/DynamicModules.Domain/Repositories/Dynamic/IApprovalRepositories.cs`、`poc/backend/src/DynamicModules.Persistence/Repositories/Dynamic/ApprovalRepositories.cs` | fg2f |
| 測試基底 | `poc/backend/tests/DynamicModules.ApplicationTests/Dynamic/Approvals/ApprovalTestBase.cs`（`SeedGraphFlow`）、新檔 `ApprovalGraphTestKit.cs`（含開關替換） | fg2a2（之後不改） |
| WebAPI 測試主機 | `poc/backend/tests/DynamicModules.WebAPITests/TestApp.cs`（加 `internal Action<IServiceCollection>? ExtraServices`，在 `ConfigureServices` 末尾呼叫） | fg2f |
| 擋門測試 | `poc/backend/tests/DynamicModules.ApplicationTests/Dynamic/Approvals/ApprovalShapeGateTests.cs` | fg2g2（刪；fg2a1–fg2g1 不改） |
| 文件 | 流程圖設計 §8、§13、§15、§17、§19、§20.1；簽核設計 §4.3、§8.4；政策設計 §10；`docs/roadmap.md`（fg2 列）；`docs/feature.md`（fg2 行與統計行）；`docs/todo.md` | fg2g2 |

**介面先固定**（平行開工前）：
- fg2a1（已實作，以實作為準、不改公開簽名）：`ApprovalGraphRuntime.Build(def, supportedKinds, out errors)`、`Start`、`Node`、`Out`、`Edge`、`JoinOf`、`ForkOf`、`ArmOf`、`Choose`、`Child`、`Within`；`ApprovalGraphPath.Segment`、`Previous`、`CancelledRoots`、`ArrivedArms`、`Live`、`CanWithdraw`；`ApprovalPath.Effective(runs, includePassed = false)`。之後要加方法（例如節點列舉）只增不改。
- fg2a2 合併時定案，fg2b、fg2c、fg2e 只用不改：`ApprovalShapeGate`（`Open`、`Reject`）；`ApprovalGraphMode.Applies`、`SupportedKinds`、`Eval`（J2、J27）；`ApprovalMessages.GraphRuntimeInvalid`、`GraphPreviewInvalid(flowName)` 與對應的 `MessageText` 鍵（修訂 2，審查 M1）；`ApprovalGraphRoute.Changed`；`ApprovalGraphTestKit` 的建構函式與 `OpenGate(services)`、`SeedGraphFlow` 的簽章。
- fg2b 合併時定案：`IApprovalStageSteps`（含 `RuntimeInvalid(inst, reason, message)`，修訂 2，審查 M2）、`AdvanceAsync`、`ApprovalGraphEngine` 的建構函式（J22）。

**與其他工作：** fg1d 已合併，不再有共用檔衝突。`docs/feature.md` 的統計行若與其他段衝突，後合併的一方 rebase 後重跑 `python3 ~/.claude/plugins/cache/plugharbor/lifecycle/2.6.0/scripts/feature_checklist.py docs/feature.md --write`，不手改統計行。

---

## 計畫判斷（技術類，主控決定，可推翻）

寫回設計時編號接在 fg1d 之後（J28 起）。修訂 1、修訂 2 改寫的條目分別標「（修訂 1）」「（修訂 2）」，理由與審查對應見 `plan-rev1-notes.md`、`plan-rev2-notes.md`。

- **J1 擋門在正式環境一直關到 fg2g2，測試用 DI 開關跑引擎。**（修訂 1，推翻初版的逐段拆）fg2a2 把 `ApprovalShapeGate` 從 static 改成服務：
  ```csharp
  /// <summary>graph 執行入口（流程圖設計 §13 第 7 點）。正式環境 Open 恆為 false：graph 一律 409，與 fg1 相同；
  /// 測試以 ApprovalGraphTestKit.OpenGate 換成開啟版才跑得到引擎。fg2g2 連同四個呼叫點一起刪除。</summary>
  public class ApprovalShapeGate
  {
      public virtual bool Open => false;
      public ServiceResult? Reject(FlowSnapshotEntry? entry) => Open ? null : /* 原本的判斷與訊息 */;
  }
  ```
  `AddSingleton<ApprovalShapeGate>()`；四個呼叫點（送出、預覽、`LoadAsync`、同意後的 `check.NewFlow`）改注入它，判斷與訊息不變。不讀設定檔、沒有環境變數，正式環境沒有辦法打開。fg2b–fg2g1 的 graph 測試在 `ConfigureServices` 換成 `Open = true` 的子類；`ApprovalShapeGateTests` 不換，所以一路證明正式環境的擋門還在。fg2g2 刪類別、四個呼叫點、DI 註冊、驗證器的 `shape` 擋門與 `ApprovalShapeGateTests.cs`（還有意義的案例照 J7 的對照表改寫搬家）。理由與回退下限見「子段切法」與 Global Constraints；不需要另做部署檢查，因為 fg2a1–fg2g1 對 graph 的行為就是 fg1 的行為。
- **J2 graph 模式的判斷與執行期共用輔助。**（修訂 2：補 `SupportedKinds`、`Eval`，改成獨立檔，審查 M1）新檔 `ApprovalGraphMode.cs`（Application，fg2a2 建；不與擋門同檔，fg2g2 刪擋門時不必搬家）：
  ```csharp
  /// <summary>graph 執行期的共用輔助（fg2a2 定案；推進、重新比對、預覽只引用，不另寫一份）。</summary>
  public static class ApprovalGraphMode
  {
      public static bool Applies(ApprovalInstance inst, ApprovalDefinition def) => def.Shape == ApprovalShapes.Graph && inst.RouteKind == ApprovalRouteKind.Flow;
      /// <summary>U2 能力守門：引擎支援的節點型別；fg5 加 notify。</summary>
      public static readonly IReadOnlySet<string> SupportedKinds;   // start、end、approval、condition、fork、join
      /// <summary>J27：條件求值轉接。</summary>
      public static Func<JsonElement, ConditionTruth> Eval(Module module, IReadOnlyDictionary<string, JsonElement?> values,
          FieldTypeRegistry registry, DateOnly today, IReadOnlySet<string> userIds);
  }
  ```
  分派、並發、檢視都只用 `Applies`；建圖一律 `ApprovalGraphRuntime.Build(def, ApprovalGraphMode.SupportedKinds, out errors)`。備援實例（§5.5 第 5 點，沿用舊綁定版本）只有一關或簽，照第一版。
- **J3 分支段代號。**（修訂 2：`Within` 對齊 fg2a1 實作，審查 S2）根段 `""`；分支節點所在段 `P` 的出線 `e` 開出的段 `Child(P, e) = P == "" ? e : $"{P}/{e}"`；「`k` 在段 `r` 之內」= `r == "" || k == r || k.StartsWith(r + "/", Ordinal)`（不能用單純前綴，`e1` 與 `e10`；根段涵蓋所有段。`CancelledRoots` 不會回根段，所以根段這一條只是邊界防呆，不影響行為）。代號最長 40、巢狀 5 層，256 字放得下。
- **J4 每個分支與匯合節點在一個實例裡最多執行一次；匯合的到達紀錄是每條分支一列。**（修訂 1：補到達分支的識別與兩條不變式）圖無環（G5），退回上一關只在同一分支段、且不跨過該段最後一次進入的分支或匯合節點（§8.4），所以推進不會第二次經過同一個分支節點（有測試）。執行線經進線 `e` 到達匯合 `j` 時：到達的分支 = `Child(外層段, g.ArmOf(j, e))`（J13）；寫一列 join 節點實例：`NodeId = j`、`BranchKey = 配對分支節點所在的段（外層）`、`InEdgeId = e`、狀態 `waiting`。兩條不變式（引擎內斷言，違反就 `RuntimeInvalid`）：
  1. **到達分支去重**：同一個匯合、同一個外層段，已有一列的 `ArmOf` 是同一條分支，就不再寫第二列。（修訂 2，審查 S1：改用合法的圖）同一分支會有兩條進線，例如分支內條件節點一條出線經簽核節點接匯合、另一條直接接匯合（fg2a1 測試的 `ConditionIntoJoin`；同一對節點之間的兩條線會被 G1 `edgeDuplicate` 擋下，不是合法的圖）。執行期一次只會走其中一條，退回上一關不會跨過匯合、重跑時也不會重複到達，所以去重只是防線；`ArrivedArms` 的去重（fg2a1 `ArrivedArms_TwoInEdgesSameArm_CountedOnce`）與這條斷言一起守。
  2. **匯合出線只推進一次**：同一個匯合、同一個外層段，已有 `passed` 的到達列就不再沿出線推進。
  `all`：到達列的 `ArmOf` 集合等於配對分支節點的全部出線 → 這個匯合的到達列全部改 `passed`，以外層段沿出線繼續；`any`：第一列直接 `passed`，取消其他分支後繼續。理由：fg4 的進度圖要每條走過的線都有 `InEdgeId`（§11.1），單一列只記得住一條進線。代價：與設計 §8.10「唯一一列 waiting」字面不同（列在矛盾第 2 點）。
- **J5 分支展開順序固定，任一到齊通過後不再展開。** 分支節點寫 `passed` 後，依出線在定義中的順序一條一條推進到停住為止；每開下一條之前檢查配對的匯合是否已是任一到齊且已通過，是就不展開剩下的分支（不寫任何節點實例）。
- **J6 同一次推進的「待簽」通知延後到推進結束才寫。** `ApprovalEngine.StartAsync` 加選用參數 `List<PendingNotice>? defer`（經 `IApprovalStageSteps` 傳給圖引擎，J22）；圖引擎傳入收集清單，推進結束後只寫任務仍是 `open` 的那幾則（U7）。sequence 不傳，照舊立即寫。
- **J7 執行期結構再驗、能力守門與適用的動作。**（修訂 1：明定實作位置與動作對照；修訂 2：逐列寫訊息、對齊 fg2a1 實作，審查 M2、S7）`ApprovalGraphRuntime.Build(def, supportedKinds, out errors)` 呼叫 `ApprovalGraphValidator.Check(def, new GraphPolicyLimits(int.MaxValue, int.MaxValue), previous: null)`，有任何 Error、或有節點型別不在 `supportedKinds`（一律傳 `ApprovalGraphMode.SupportedKinds`，fg2 是開始、結束、簽核、條件、分支、匯合）就回 null 與錯誤清單。fg2a1 的實作細節：不支援的型別只在結構驗證**沒有任何錯誤**時才檢查（有結構錯誤時節點可能殘缺，只回結構錯誤）；問題的 `Rule` 填 `"runtime"`、代碼 `unsupportedKind`、鍵 `nodes.{id}`。政策上限不在執行期擋（改嚴只管之後）；警告不看。不快取（100 個節點以內，成本可忽略）。建圖的位置、失敗時的處理與訊息：

  | 入口或動作 | 建圖位置 | 不合法時 | 回給使用者的訊息 |
  |---|---|---|---|
  | 送出、重來到 graph 新實例 | 圖引擎 `StartAsync` 開頭 | `steps.RuntimeInvalid(inst, reason, ApprovalMessages.GraphRuntimeInvalid)` → 409＋異常通知，整筆回滾 | `GraphRuntimeInvalid`「流程圖定義不完整，已停止並通知系統管理員」 |
  | 記錄預覽、設計器試算 | `ApprovalPreviewer` | 409，不登記異常通知（不寫入，送出時才登記） | `GraphPreviewInvalid(流程名稱)`（U12） |
  | 同意、加簽（含其前的重新比對）、退回上一關 | `LoadAsync` 共同建一次，放進 `Loaded.Runtime` | handler 在任何寫入前 `engine.RuntimeInvalid(inst, reason, ApprovalMessages.GraphRuntimeInvalid)` → 409＋異常通知 | `GraphRuntimeInvalid` |
  | 撤回、終止、退回發起人、轉交、改派 | `LoadAsync` 一樣建，但 handler 不看 | 照常執行（U11）；這些動作只關閉執行線或換負責人，用到的 `CanWithdraw` 只讀節點實例與任務 | — |
  | 條件節點無法判斷（送出、同意後推進、重新比對） | — | `RuntimeInvalid(inst, reason, ApprovalMessages.ConditionNodeUndecidable(名稱))` → 409＋異常通知 | `ConditionNodeUndecidable`「條件「{0}」無法判斷，已停止並通知系統管理員」 |
  | 條件節點無法判斷（預覽） | — | 409，不登記 | `FlowConditionUndecidable(流程名稱)`（J19） |

  `reason`（只進異常通知標題）寫「流程圖結構不合法：」接 `GraphIssue` 的代碼與鍵（例如 `unsupportedKind nodes.n3`），供系統管理員查。`Loaded` 用 **init 屬性**加 `ApprovalGraphRuntime? Runtime { get; init; }` 與 `IReadOnlyList<GraphIssue> RuntimeErrors { get; init; } = []`，不加成 positional 參數（審查 S7：現有 6 處 5 元解構，`ApprovalActionHandlers.cs:112、287、359、398、427、466`，維持不動）；sequence 實例兩者都是 null／空。同意在重新比對**之前**就要求 `Runtime` 不是 null：即使重新比對可能選到另一條 sequence 流程，路線比較本身也要讀圖。
- **J8 條件節點無法判斷。** 依 `order` 由小到大求值：成立就選它；先遇到解析不了的（引用的欄位或選項已不在）就是「無法判斷」，即使後面某條成立也一樣（同第一版 `ApprovalRouter.Match`）；都不成立走預設線。無法判斷時 409 `ConditionNodeUndecidable(節點名稱)` 並登記異常通知（§8.8）。重新比對遇到同樣處理。
- **J9 瞬間節點不寫歷程。** 開始、條件、分支、匯合、結束只寫節點實例（`passed`／`waiting`），歷程只多 `skip`（沿用）與 `branch-cancelled`；走了哪條線看節點實例的 `InEdgeId`。（修訂 1）`branch-cancelled` 的 `comment` 是固定繁中、只給稽核，**畫面不顯示 comment**，依 `DataJson` 組字，前端改動在 fg2d（U8）。
- **J10 graph 的節點實例欄位。** 每一列都寫 `NodeId`、`NodeKind`、`BranchKey`、`InEdgeId`、`StageCode = NodeId`（不只簽核節點；維持「`StageCode` 為 null 就是加簽」這個第一版各處依賴的不變式）、`NameSnapshot = 節點名稱`、`Mode`：簽核節點照節點，其他填 `any`（欄位必填）。加簽列：`StageCode = null`、`NodeId = 主節點`、`NodeKind = approval`、`BranchKey` 同主節點、`InEdgeId = null`。`Seq` 一律取實例目前最大值加一。（修訂 1）**所有複製或重建節點實例的路徑都要帶齊 graph 欄位**：`RestartStageAsync` 的三個分支（備援、加簽、一般節點）、退回上一關時被退回的加簽重新排隊（`ApprovalActionHandlers.cs:308` 那段）、加簽巢狀重新編號；每條路徑各有一個斷言欄位齊全的測試。
- **J11 graph 的加簽順序。** 插入加簽時，把「同一 `BranchKey`、同一主節點」的排隊加簽依第一版的目標順序重新編號為「目前最大 `Seq` + 1、+ 2…」，所以 `Seq` 只增不減、順序與第一版相同；sequence 照舊。主節點或加簽完成後：同段同主節點有排隊加簽就啟動 `Seq` 最小的，否則沿主節點出線推進（a3 計畫 795 行）。（修訂 1）「同段同主節點」的分派在 **fg2b** 就寫進圖引擎，graph 實例從 fg2b 起不經過第一版「從整個實例挑最小 queued」那段（`ApprovalEngine.cs:45-48`）：`AdvanceAsync` 先依 J2 分派，graph 不進 `ActivateNextAsync`。fg2d 只負責寫入加簽列與重新編號。
- **J12 上一關（graph）。** 候選＝與目前節點實例同 `BranchKey`、`Seq` 比它小、而且比這個段最後一列「分支節點或匯合節點的節點實例」大；在候選上套第一版 `ApprovalPath.Effective` 的退回截斷規則（`passed` 與 `skipped` 一樣進路徑），取最近一個 `approved`。分支內第一個簽核節點：段內沒有更早的已核准列；匯合後第一個：最後一列是匯合，自然沒有候選。重啟沿用 `RestartStageAsync`，新列複製被退回目標的 `NodeId`、`NodeKind`、`BranchKey`、`InEdgeId`（J10）。
- **J13 到達的分支與被取消的分支。**（修訂 1，推翻初版「進線起點最近一列的 BranchKey」）`ApprovalGraphRuntime.ArmOf(joinId, inEdgeId)` 回「這條進線屬於配對分支節點的哪一條出線」，建圖時一次算好：進線起點就是配對的分支節點（`fork → join` 直連的空分支）→ 就是這條進線本身；否則是「從它的終點走得到這條進線起點」的那一條出線（G7 保證各分支不共用節點，所以唯一；巢狀的內層並行區整個屬於外層的一條分支）。`ApprovalGraphPath.CancelledRoots(runtime, runs)`：對每個已有 `passed` 到達列的任一到齊匯合，到達分支 = `Child(到達列的 BranchKey, ArmOf(匯合, 到達列的 InEdgeId))`；同一分支節點其他出線開出的段 `Child(外層, 其他出線)` 都是被取消的段。重新比對、撤回資格、檢視都以「`BranchKey` 在被取消的段之內」排除。
- **J14 `CloseReason` 只寫 graph 實例。** `branchCancelled`、`instanceEnded`；sequence 實例的結束動作照舊不寫（null），JSON 與資料不變。
- **J15 `CurrentStageRunId`。** graph 實例在每次動作結束前重算成「`Seq` 最小的 `active` 節點實例」（沒有就 null）；`StartAsync` 照舊先寫 `run.Id`，圖引擎推進結束時覆寫。
- **J16 並發：處理權與處理權之內的檢查。**（修訂 1：補「所有依目前狀態而定的檢查都移進處理權」）graph 模式下：任務層動作（同意、加簽、退回、轉交、改派）不比對請求的 `rowVersion`，以資料庫目前值做條件更新，失敗就重新讀取再試，最多 `ApprovalGraphLimits.MaxClaimAttempts = 5` 次、每次等 `ClaimBackoffMs × 2^(n-1)`（20 ms 起）；用完回 409 `ApprovalBusy`。實例層動作（撤回、終止）比對請求版本，不符回 409 `ApprovalInstanceChanged`「單據已有變動，請重新整理」，不重試。改派歸任務層（a3 計畫 791 行）。取得處理權之後：
  1. （修訂 2 定案，審查 S4）處理權之後，reload 這個實例在 ChangeTracker 中**所有**已追蹤的 `ApprovalInstance`、`ApprovalTask`、`ApprovalStageRun`（儲存庫 `ReloadTrackedAsync(instanceId)`：逐筆 `Entry(x).ReloadAsync()`；EF 不會用查詢結果覆蓋已追蹤物件），再載入節點實例與任務。不採「只 reload 處理權之前讀過的」或「處理權之前一律 `AsNoTracking`」：正式環境每個請求一個 `DbContext`，兩者等價；但 ApplicationTests 的 `DbContext` 是跨請求共用的單例（`DesignerTestBase.cs:68` `AddSingleton(Db.Context)`），前面請求載入過的任務與節點實例都還在追蹤中，只處理「這個請求讀過的」會在測試裡留下舊物件，測試與正式環境的行為不同。處理權發生在任何寫入之前，追蹤中的物件沒有未存的修改，reload 不會丟資料。
  2. 依 reload 後的值重新判斷：實例 `pending`、自己的任務 `open`、一人一票（`AlreadyApprovedInStage`）、加簽總數上限、轉交與改派的對象規則中依現有任務判斷的部分（Task 0 逐一列出）。
  3. `LoadAsync` 之前只准做與實例狀態無關的輸入檢查（意見字數、`to`、`mode`、人數上限、對象存在且啟用、不是自己）。加簽總數原本在 `AddSignTaskHandler` 載入前算（`ApprovalActionHandlers.cs:261-264`），保留那一段當快速失敗（sequence 的回應順序因此不變），**另在 `ApproveAsync` 取得處理權後、寫入前再算一次**，兩種形狀都做：sequence 的處理權比對請求版本，載入前後看到的是同一份資料，結果必然相同；graph 才是真正的防線。
  sequence 的處理權照舊。只改 `LoadAsync` 不算完成，Task 10 的測試逐項覆蓋第 2 點。`LoadAsync` **內部**、處理權之前那一道 pending 判斷怎麼處理見 J17。
- **J17 晚到動作的訊息（只適用 graph）。**（修訂 2 改寫，審查 M3；使用者看得到的優先序是 U13）
  1. 只在 `ApprovalGraphMode.Applies` 為真時套用。sequence 的 `LoadAsync`（`ApprovalActionHandlers.cs:50` 的 pending 判斷在換 RowVersion 之前）與各 handler 的 `me.Status != Open → StageHandled` 完全照第一版（`Sequence_LateApprove_AfterReject_StillStageHandled`）。
  2. graph 模式下，`LoadAsync` 在處理權之前**不**判斷實例 pending。順序：讀實例 → 授權 → 模組 → 定義 → 擋門（正式環境照舊 409）→ 處理權（任務層：讀目前版本、條件更新、重試；實例層：比對請求版本，J16）→ reload（J16 第 1 點）→ 實例不是 pending 就回 `LateConflict`。實例層比對失敗時也先 reload 實例再判斷：不是 pending → `LateConflict`；仍是 pending → `ApprovalInstanceChanged`。處理權成功後才發現晚到時，回 409 由 `TransactionBehavior` 整筆回滾，換掉的 RowVersion 一起還原。
  3. 所有「實例不是 pending、任務不是 open」的出口共用 `ApprovalActionSupport.LateConflict(ApprovalInstance inst, ApprovalTask? me, IReadOnlyList<ApprovalStageRun> runs)`：實例 `rejected` → `ApprovalInstanceReturned`「單據已退回」；`me` 不是 null、它所在的節點實例 `CloseReason = branchCancelled` → `ApprovalBranchCompleted`「其他分支已完成，不需處理」；其他 → `StageHandled`。呼叫點：`LoadAsync` 的 pending 判斷（graph），以及同意（`ApprovalActionHandlers.cs:114`）、退回（289）、轉交（400）、改派（468）的 `me.Status != Open`（graph 才換成 `LateConflict`，sequence 那幾行不變）。一人一票（`AlreadyApprovedInStage`）照舊回 `StageHandled`。
  這是設計 U5「簽核中心」那一半的落點（U6）。
- **J18 並發測試的分工。**（修訂 1：改正注入方式）SQLite 由 `WriteGate` 讓寫入請求整筆序列化，自動重試在 SQLite 不會自然發生，而且外層交易一開始就持有寫入鎖（`EfTransactionManager.cs:20`），**在失敗注入時從另一條連線真的寫資料會卡住**。所以：
  - 重試、用完、reload 的程式路徑用 ApplicationTests 的可控替身驗：`ConfigureServices` 換一個儲存庫裝飾器 `FlakyClaimRepository`，讓 `TryBumpRowVersionAsync` 前 N 次回 false；要模擬「別人剛提交」時，裝飾器在回 false 之前用**同一個** `DbContext` 的 `ExecuteUpdateAsync` 直接改資料列（ApplicationTests 的 `DbContext` 是共用單例，同一條連線、同一個交易，不開新 scope、不開新連線；`ExecuteUpdate` 繞過變更追蹤，已追蹤的物件因此真的變成舊值，正好重現 Review Focus 5；也因為共用單例，處理權後要 reload 追蹤中的全部相關物件，見 J16 第 1 點）。（修訂 2，審查 S5）要模擬「另一條分支剛加簽」時，因為加簽總數算的是 `add-sign` 歷程的**筆數**（`ApprovalActionHandlers.cs:263`），`ExecuteUpdate` 新增不了列：改用同一個 `DbContext` 的 `Database.ExecuteSqlAsync` 插入一筆 `add-sign` 歷程（不用 `Add`＋`SaveChanges`，免得新列進了追蹤、不像別的請求寫的）。
  - 結果正確性（兩條分支同時同意都成功、匯合只推進一次）用 WebAPI `TestApp(fileDatabase: true)` 真並行驗；在 SQLite 上它們被 `WriteGate` 排隊，驗的是「排隊後的結果正確」，不是「交錯提交」。
  - 真正的交錯提交與重試只有 PostgreSQL 驗得到：同一批 WebAPI 測試標 `Postgres` trait；現在 `TestApp` 寫死 SQLite，trait 只是標記，pg0 補上 provider 切換後才算跑過（未驗證）。
- **J19 預覽回應與任一到齊。**（修訂 1：補定義 any 預覽，改正「深度優先＝實際路徑」）`ApprovalPreviewResponse` 末尾加 `PreviewPath? Path = null`（`PreviewPath(IReadOnlyList<string> NodeIds, IReadOnlyList<string> EdgeIds)`，只有 graph 有，null 不寫出）；`PreviewStage` 末尾加 `string? AnyGroup = null`（任一到齊匯合的代號，null 不寫出，sequence 的 JSON 不變）。`Stages` 列預計會經過的簽核節點，**分支依定義順序逐條列出只是排列方式，不代表先後**；狀態沿用 `enter`／`skip`（只在設計器）／`pick`／`conditional`。匯合的預覽規則：
  - `all`：每條分支照列，之後接匯合後的節點。
  - `any`：依定義順序找第一條「確定立即到達」的分支（預估路徑上沒有會 `enter`、`pick`、`conditional` 的簽核節點，也沒有 U9 的依條件決定）。有 → 這個並行區的分支一條都不列（設計器試算列那條分支被略過的節點，狀態 `skip`），`Path` 只含那條分支的線，之後接匯合後的節點；這與引擎一致：引擎依定義順序展開（J5），排在它前面、需要人工的分支雖然會先建任務，但在同一交易內被取消、不發通知（U7）。沒有 → 每條分支的項目都帶 `AnyGroup`，`Path` 含全部分支，之後接匯合後的節點（U10）。
  - U9 的條件節點以一筆 `conditional` 表示，從共同必經後繼接著走；共同必經後繼是匯合時，被跳過的那段視為「這條分支不確定」（`any` 時就不算確定立即到達）。
  預覽遇到條件節點無法判斷回 409 `FlowConditionUndecidable(流程名稱)`（「…請系統管理員檢查流程設定」，不宣稱已通知；預覽不登記異常通知，不另開訊息）。預覽遇到結構不合法回 `GraphPreviewInvalid`（U12，fg2a2 建）。
- **J20 異常通知帶快照。** `ApprovalAlert.Raise(ApprovalInstance inst, string reason)` 記下實例 id、記錄 id、模組 id、標題快照；`WriteApprovalAlertHandler` 找不到實例（已隨主交易回滾）時用快照組通知與連結。（修訂 1）`ApprovalAlertBehavior` 另檢查 `WriteApprovalAlertRequest` 回的 `ServiceResult`，失敗時記 log（目前只 catch 例外，`ApprovalAlertBehavior.cs:24-31`）。sequence 的觸發條件不變，只是原本會遺失的警示現在送得出去。
- **J21 文件寫回的範圍。** fg2g2 改政策設計 §10 B27（§15 指定 fg2）與簽核設計 §4.3 狀態清單（補 `queued`、`passed`、`waiting`）、§8.4 併發加註（§8.13 的行為從 fg2 開始）；§20.1 其餘項目（§1.3、§2.2、§12、§16 指向、功能清單 `[-]` 行）照設計 §17 留給 fg6。
- **J22 引擎的依賴方向與唯一完成者。**（修訂 1：先定單向依賴，不留到實作時才發現 DI 循環；修訂 2：補齊建構函式、`RuntimeInvalid` 加訊息參數，審查 M2）
  - 依賴只有一個方向：`ApprovalEngine` 注入 `ApprovalGraphEngine`；`ApprovalGraphEngine` 的建構函式**不**注入 `ApprovalEngine`，完整清單是 `IApprovalInstanceRepository repo`、`ApproverResolver resolver`、`FieldTypeRegistry registry`、`IModuleRepository modules`、`IBusinessTime time`、`ApprovalNotifier notifier`、`IUnitOfWork uow`（寫完節點實例要 commit 才拿得到 `Id`，`branch-cancelled` 歷程的 `StageRunId`、`CurrentStageRunId` 都要用；同 `ApprovalEngine.SkipAsync` 的寫法），全部 scoped，不形成循環。它需要的單關步驟由 `ApprovalEngine` 以方法參數傳入：
    ```csharp
    /// <summary>ApprovalEngine 提供給圖引擎的單關步驟；以方法參數傳遞，不經 DI，避免 ApprovalEngine ↔ ApprovalGraphEngine 循環。</summary>
    public interface IApprovalStageSteps
    {
        Task<ApprovalStageRun> StartAsync(ApprovalInstance inst, ApprovalStageRun run, IReadOnlyList<SeatPlan> seats, int? dueHours,
            CancellationToken ct, string kind = NotificationKinds.ApprovalPending, List<PendingNotice>? defer = null);
        Task SkipAsync(ApprovalInstance inst, ApprovalStageRun run, string reason, CancellationToken ct);
        Task<(IReadOnlyList<SeatPlan> Seats, ServiceResult? Failure)> ResolveAddSignAsync(ApprovalInstance inst, Module module,
            ApprovalDefinition def, ApprovalStageRun addSign, IReadOnlyDictionary<string, JsonElement?> values, CancellationToken ct);
        ApprovalAction Log(ApprovalInstance inst, string action, string actor, long? runId = null, long? taskId = null, string? comment = null,
            List<string>? targets = null, string? data = null, string? onBehalfOf = null);
        /// <summary>登記異常通知並回 409；message 為 null 時回第一版的 ApprovalMessages.RuntimeInvalid（sequence 行為不變）。</summary>
        ServiceResult RuntimeInvalid(ApprovalInstance inst, string reason, MessageRef? message = null);
    }
    ```
    `ApprovalEngine` 實作這個介面（既有方法改成介面成員，`SkipAsync`、`ResolveAddSignAsync` 由 private 改 public，內容不變；`RuntimeInvalid` 加選用參數 `message`，現有 sequence 呼叫端不傳，回應不變）。（修訂 2）圖引擎與 graph 的 handler 要回圖專用訊息（`GraphRuntimeInvalid`、`ConditionNodeUndecidable`）都經這個參數，409 與登記警示仍在同一處，不讓圖引擎另外注入 `ApprovalAlert`（那樣 409 與警示會分兩處寫，容易只做一半）。handler 只呼叫 `ApprovalEngine`，不直接注入圖引擎。有一個測試從 DI 解析 `ApprovalEngine` 與每個動作 handler，證明沒有循環。
  - 唯一完成者：
    ```csharp
    /// <summary>推進到停住為止；走到底就由這裡呼叫唯一一次 CompleteAsync。done：剛完成的關卡（送出、重來傳 null）。</summary>
    public async Task<(bool Completed, ServiceResult? Failure)> AdvanceAsync(ApprovalInstance inst, Module module,
        IReadOnlyDictionary<string, JsonElement?> values, ApprovalStageRun? done, CancellationToken ct)
    ```
    sequence（含備援）：`ActivateNextAsync` → 回 null 就 `CompleteAsync`，與原本三個呼叫端的寫法逐行相同，`done` 不用。graph：`done is null` → `graph.StartAsync(inst, module, values, this, ct)`，否則 `graph.AfterApprovedAsync(inst, module, done, values, this, ct)`；圖引擎回 `GraphAdvance.ReachedEnd` 才由 `AdvanceAsync` 呼叫 `CompleteAsync`。送出（`ApprovalHandlers.cs:83-90`）、同意（`ApprovalActionHandlers.cs:189-195`）、重來（`ApprovalEngine.cs:198-205`）三個呼叫端在 **fg2b** 一起改成呼叫 `AdvanceAsync`、刪掉自己的 `CompleteAsync`；`ActivateNextAsync` 改 private。`CompleteAsync` 同時改 private，只剩 `AdvanceAsync` 呼叫得到，由編譯器保證不會再有第二個完成者。
  - 引擎內不引入任何範本、槽位概念（§16 第一點）。
- **J23 等價性比較的口徑。**（修訂 1：擴充）同一組情境以 sequence 與 `FromSequence` 轉出的 graph 各跑一次。先把兩邊的關卡實例**正規化**：只取 `NodeKind` 為 null 或 `approval` 的列（含加簽），依 `Seq` 排序後編號 0、1、2…，建「`Id` → 正規化編號」對照；之後所有掛在關卡實例上的資料都改用正規化編號比較，不比 `Seq` 的數值、`Id`、時間。比較：
  - 關卡實例序列：`(StageCode, Status, SkipReason, Mode, AddedByUserId, AddSignUserIds)`；加簽的 `StageCode` 都是 null，所以靠正規化編號與 `AddedByUserId`、`AddSignUserIds` 鎖定是哪一個加簽。
  - 任務：依 `(正規化編號, Id 順序)` 排序後比 `(正規化編號, SeatKey, AssigneeUserId, OriginalUserId, Reason, Status)`，席位與代理的本人都在裡面。
  - 歷程：依 `Id` 排序後比 `(Action, 正規化編號或 null, 任務的正規化位置或 null, ActorUserId, OnBehalfOfUserId, TargetUserIds, DataJson)`；`DataJson` 中的 `flowVersionId` 等兩邊本來就不同的鍵先移除（Task 12 列出移除清單）。
  - 通知：（修訂 2 定義篩選，審查 S6）「本實例相關」＝`SourceType = "approval"` 而且 `SourceId` 屬於這個情境建出的實例（含重來建的新實例；兩欄由 `ApprovalNotifier.cs:25` 寫入，不用 `Link` 篩：`Link` 是記錄層級，`withLink = false` 時是空字串）。篩出的通知依 `Id` 排序後比 `(Kind, UserId, TitleSnapshot, Link)`，`Link` 中的記錄 id 先換成占位字（兩種形狀各用自己的記錄）；若差異只在同一個動作內的先後（J6 的延後寫入），改比同一動作內的多重集合，並在修訂紀錄寫明是哪些情境。
  - 實例與記錄：實例 `Status`、`RouteKind`、`CurrentStageRunId` 的正規化編號、`Picks`；記錄最後的狀態選項。
- **J24 檢視多帶的欄位。** `ApprovalStageView` 末尾加 `string? NodeId = null, string? BranchKey = null, string? CloseReason = null`（null 不寫出，sequence 的 JSON 不變）；graph 實例的 `stages` 只放 `NodeKind` 是 `approval` 的列（U6）。
- **J25 自選鍵。** graph 實例的 `Picks` 鍵是簽核節點代號（＝`StageCode`）；`PickErrors`、`UserFieldErrors`、`RouteCheck` 的 `Picks` 判斷都改讀 `ApprovalDefinitionRefs.Steps(def)`（§8.9 J8：定義中所有自選節點都要選）。
- **J26 預覽的共同必經後繼。**（修訂 1 新增，U9 的算法）`ApprovalGraphPreview.ResumeAfter(g, conditionId)`：在圖上加一個虛擬終點、把每個 `end` 接過去，以反向圖做後必經節點的迭代計算（節點依反向拓撲序，`pdom(n) = {n} ∪ ⋂ pdom(後繼)`），取條件節點的直接後必經節點；結果是虛擬終點就回 null（預覽到此為止）。整張圖算一次、預覽內共用；不放進 `ApprovalGraphRuntime`（執行期用不到）。
- **J27 條件求值轉接只有一個，兩種條件在呼叫端分流。**（修訂 2 新增，審查 M1 第 2 點、S3）`ApprovalGraphMode.Eval(module, values, registry, today, userIds)` 回 `Func<JsonElement, ConditionTruth>`：`ValueKind` 不是 Object → `Undecidable`；`ConditionParser.Parse(w, module, registry, ConditionUsage.Rule, userIds)` 有 `Error` → `Undecidable`；`Node` 為 null（空群組）→ `Holds`；其餘依 `ConditionEvaluator.Evaluate(node, values, registry, today)` 回 `Holds`／`Fails`。與 `ApprovalRouter.Match`、`Holds` 同一組呼叫（`ApprovalRouter.cs:22-45`），不新增解析規則。推進（fg2b）、重新比對的呼叫端（fg2c `Recheck`）、預覽（fg2e）都用它建 `eval`，不各寫一份。「無法判斷」兩種條件的意思不同，分流規則：
  - 條件節點的出線：交給 `ApprovalGraphRuntime.Choose`，`Undecidable` 就是無法判斷（J8，409）。
  - 簽核節點的略過條件：`eval(when) != Fails` 就算成立、不略過，與第一版 `ApprovalRouter.Holds`「解析不了視為成立」一致（寧可多簽一關）。推進端直接沿用 `ApprovalRouter.Holds`；`ApprovalGraphRoute.Changed` 只收一個 `eval`，在內部照這條判斷 `skipped(condition)` 的節點，文件註解寫明，並有 `SkippedNodeConditionUnparseable_CountsAsHolds_Changed` 守住。
  `ApprovalGraphModeTests.Eval_AgreesWithRouterHolds` 以同一組條件證明「`Eval != Fails`」與 `ApprovalRouter.Holds` 結果相同。

---

## Task 0：核對（不改程式）

- [x] fg1d 已合併（修訂 2 核對）：`shape` 擋門在 `ApprovalDefinitionValidator.Graph.cs:65` 的 `x.Err("shape", ApprovalMessages.GraphNotAccepted)`；設計 §19 最後的編號 U21、J27。各段開工時仍讀一次 `ApprovalMessages.cs` 的現況與驗證器對外契約（`GraphCheck`、錯誤鍵規則）。
- [x] fg2a1 的 worktree：`../dynamic-fields-design.fg1`，分支 `feature/fg2a1`，基準 `916268f1`。之後各段在 fg2a1 合併後從當時的 origin/main 開分支（同一個 worktree 或 `../dynamic-fields-design.fg2`）；平行時另開 `../dynamic-fields-design.fg2p`。
- [ ] 重讀並確認行號（修訂 1 審查已在 `916268f1` 核對過主要行號；每段開工時對當時的 main 再核一次）：`ApprovalEngine.cs`（41-77、102-124、135-209、215-287）、`ApprovalActionHandlers.cs`（40-60、75-88、103-199、238-268、278-346、353-382、386-416、421-446、454-490）、`ApprovalHandlers.cs`（41-93、99-139）、`ApprovalRouter.cs`（52-68）、`ApprovalPreviewer.cs`（70-146）、`ApprovalQueryHandlers.cs`（56、251-271、300-358）、`ApprovalAlert.cs`、`ApprovalAlertBehavior.cs`、`ApprovalShapeGate.cs`、前端 `ApprovalActionLog.vue`、`ApprovalStageList.vue`、`PreviewStages.vue`。
- [ ] 列出 `LoadAsync` 之前依實例或任務**目前狀態**做的所有檢查（已知：`AddSignTaskHandler` 的加簽關卡數；預期轉交、改派的對象規則有一部分讀現有任務），寫進修訂紀錄，作為 J16 第 2 點的完整清單。
- [x] 確認 `ApprovalGraphValidator.Check` 對「沒有任何節點」「`nodes` 為 null」回結構錯誤（fg2a1 核對：`Nodes == null` 回 `nodesMissing`、空清單回 G2／G13；`Build_NoNodes_Null` 兩種都覆蓋）。
- [ ] 確認 `ApprovalNotifier.NotifyAsync` 是寫通知列（同一交易），J6 的延後寫入只需收集參數。
- [ ] （fg2f）確認儲存庫能提供 `ReloadTrackedAsync(instanceId)`：從 `ChangeTracker.Entries<ApprovalInstance／ApprovalTask／ApprovalStageRun>()` 篩這個實例，逐筆 `ReloadAsync()`（做法已定案，J16 第 1 點）。
- [x] ApplicationTests 的 `DbContext` 是共用單例（`DesignerTestBase.cs:68` 的 `AddSingleton(Db.Context)`），J16 第 1 點與 J18 的注入都依賴它。
- [ ] 確認 `TestApp.Services` 可開 scope 取 `DynamicDbContext` 直接寫 graph 版本，以及 `ConfigureWebHost` 末尾可加 `ExtraServices` 掛點（fg2f）。
- [ ] 確認 `docs/feature.md` 的 fg2 行與 `feature_checklist.py` 路徑。
- [ ] 核對結果寫在本檔末「修訂紀錄」。

---

## fg2a1：Domain 執行期地基（已實作）

**狀態：** 已實作於 `feature/fg2a1`（`19d1a7de`、`c95c86d8`，基準 `916268f1`，6 檔 +682 −4，DomainTests 59 條全過、整個方案 build 0 警告），等審查與合併。以下 Task 1、2 照實作改寫；公開簽名以實作為準，後段不得改動。結果與紅燈證據見 `fg2a1-result.md`。

### fg2a1 實作與計畫的偏離（後段要知道）

1. **`ApprovalPath.Effective` 加選用參數 `includePassed = false`**（`poc/backend/src/DynamicModules.Domain/Approvals/ApprovalPath.cs`）：`ApprovalGraphPath.Segment` 傳 `true`，讓 `passed`（條件、分支、匯合）也進段內路徑、重用第一版的退回截斷；sequence 不傳，行為不變。計畫原本沒列這個檔，已補進 Task 2 的 Files 與共用檔表；fg2c、fg2d 若要改 `ApprovalPath.cs`，要保留這個參數與預設值。
2. **`Within(key, "")` 回 true**（根段涵蓋所有段）。J3 已改成與實作一致；`CancelledRoots` 不會產生根段，所以只是邊界防呆。
3. **能力守門只在結構驗證沒有任何錯誤時才檢查**：有結構錯誤時節點可能殘缺，只回結構錯誤；`unsupportedKind` 的 `GraphIssue.Rule` 是 `"runtime"`。J7 已寫入。fg2b 組異常通知的 `reason` 時，兩種錯誤都要照實列出。
4. **`ArrivedArms` 排除 `closed` 的到達列**：被取消分支（或巢狀內層被取消）留下的到達列不算已到達。fg2b 的 all 匯合判斷直接用它，不另外篩。

另外兩點實作備註，fg2b 起要照做：
- `CancelledRoots`、`ArrivedArms` 對圖內不存在的 `NodeId`、`InEdgeId` 會丟 `KeyNotFoundException`，是刻意的（不吞掉不變式違反）。呼叫端一律用**實例自己綁定的快照**建 `ApprovalGraphRuntime`（`ApprovalEngine.DefinitionOf(inst)`），不拿別的版本的圖去讀這個實例的節點實例；路線比較換版本時走第一版的 Changed，不呼叫這兩個函式。
- `Previous` 的「分支或匯合邊界」只看與目前列同 `BranchKey` 的 fork／join 列；巢狀內層段的 `Previous` 有 `Previous_InnerSegment_BarrierPerSegment` 一條。fg2c 的 `ReturnPrevious_*` 要再補一條巢狀內層匯合後的情境（見 Task 6）。

### Task 1：狀態常數與 `ApprovalGraphRuntime`（已實作）

**Files:**
- Modify: `poc/backend/src/DynamicModules.Domain/Entities/Dynamic/Approval.cs`（`ApprovalStageStatus` 加 `Passed = "passed"`、`Waiting = "waiting"`；新增 `ApprovalCloseReasons { BranchCancelled = "branchCancelled", InstanceEnded = "instanceEnded" }`；`ApprovalActions` 加 `BranchCancelled = "branch-cancelled"`，不加進 `TaskHandled`）
- Create: `poc/backend/src/DynamicModules.Domain/Approvals/ApprovalGraphRuntime.cs`
- Test: `poc/backend/tests/DynamicModules.DomainTests/Approvals/ApprovalGraphRuntimeTests.cs`（新）

```csharp
/// <summary>條件求值結果：成立、不成立、無法判斷（引用的欄位或選項已不在）。</summary>
public enum ConditionTruth { Holds, Fails, Undecidable }

/// <summary>執行期用的圖（流程圖設計 §8）：結構再驗通過、而且只含支援的節點型別才建得出來；出線依定義順序。</summary>
public sealed class ApprovalGraphRuntime
{
    /// <summary>J7：結構驗證有任何錯誤、或有節點型別不在 supportedKinds，就回 null 與錯誤；政策上限不在這裡擋。</summary>
    public static ApprovalGraphRuntime? Build(ApprovalDefinition d, IReadOnlySet<string> supportedKinds, out IReadOnlyList<GraphIssue> errors);
    public ApprovalNodeDef Start { get; }
    public ApprovalNodeDef Node(string id);
    public IReadOnlyList<ApprovalEdgeDef> Out(string nodeId);
    public ApprovalEdgeDef Edge(string edgeId);
    public ApprovalNodeDef JoinOf(string forkId);
    public ApprovalNodeDef ForkOf(string joinId);
    /// <summary>J13：匯合的這條進線屬於配對分支節點的哪一條出線（fork → join 直連就是這條線本身）。</summary>
    public string ArmOf(string joinId, string inEdgeId);
    /// <summary>J8：依 order 求值，第一條成立的；先遇到無法判斷就回 (null, true)；都不成立回預設線。</summary>
    public (ApprovalEdgeDef? Edge, bool Undecidable) Choose(string conditionId, Func<JsonElement, ConditionTruth> eval);
    /// <summary>J3。</summary>
    public static string Child(string parentKey, string edgeId);
    public static bool Within(string key, string root);
}
```

`GraphIssue` 沿用驗證器的型別；不支援的型別用新的問題代碼 `unsupportedKind`（鍵 `nodes.{id}`、`Rule = "runtime"`，只在結構沒有錯誤時檢查），只在執行期出現，不進共用案例檔。`Build` 的 `supportedKinds` 由呼叫端傳入；fg2a2 起一律傳 `ApprovalGraphMode.SupportedKinds`（J2），Domain 不放型別清單。

**測試：**
- `Build_DesignExample_Ok`（設計 §7.1 範例）、`Build_MissingDefaultEdge_NullWithErrors`、`Build_NoNodes_Null`、`Build_OverPolicyApprovalNodes_StillBuilds`（12 個簽核節點）。
- `Build_NotifyNodeNotSupported_NullWithUnsupportedKind`、`Build_NotifyNodeSupported_Builds`（傳入含 `notify` 的集合，證明 fg5 只要改集合）。
- `Out_KeepsDefinitionOrder`、`JoinOf_ForkOf_Paired`。
- `ArmOf_DirectForkToJoin_IsThatEdge`（空分支）、`ArmOf_ThroughApproval_IsForkOutEdge`、`ArmOf_ConditionTwoEdgesIntoJoin_SameArm`（圖 `ConditionIntoJoin`：分支內條件節點一條出線經簽核節點接匯合、另一條直接接匯合，兩條進線回同一條分支）、`ArmOf_NestedInnerRegion_BelongsToOuterArm`（外層匯合的進線來自內層匯合）。
- `Child_RootAndNested`（`("", "e3") → "e3"`、`("e3", "e9") → "e3/e9"`）、`Within_SegmentBoundary`（`"e10"` 不在 `"e1"` 之內、`"e1/e9"` 在）、`Within_RootCoversAll`（實作時新增）。
- `Choose_FirstHoldingByOrder`、`Choose_NoneHolds_Default`、`Choose_UndecidableBeforeHolding_Undecidable`、`Choose_HoldingBeforeUndecidable_ReturnsHolding`、`Choose_OrderNotArrayOrder`（陣列順序與 `order` 相反）。

**驗收：** `sh $TL dotnet test tests/DynamicModules.DomainTests --filter "FullyQualifiedName~ApprovalGraphRuntime"`；`dotnet build`（警告即錯誤）。

- [x] 寫失敗測試 → 實作 → 通過 → commit `feat(approval): 流程圖執行期的圖模型、分支識別與選線`（`19d1a7de`）

### Task 2：`ApprovalGraphPath`（已實作）

**Files:**
- Create: `poc/backend/src/DynamicModules.Domain/Approvals/ApprovalGraphPath.cs`
- Modify: `poc/backend/src/DynamicModules.Domain/Approvals/ApprovalPath.cs`（`Effective` 加選用參數 `includePassed = false`；修訂 2 補列，審查 S10）
- Test: `poc/backend/tests/DynamicModules.DomainTests/Approvals/ApprovalGraphPathTests.cs`（新）

```csharp
public static class ApprovalGraphPath
{
    /// <summary>段內有效路徑（Seq 升冪；approved、skipped、passed 進路徑；returned 依 ApprovalPath 的規則截斷）。</summary>
    public static IReadOnlyList<ApprovalStageRun> Segment(IReadOnlyList<ApprovalStageRun> runs, string branchKey);
    /// <summary>J12：同段、在該段最後一列分支或匯合節點實例之後、最近一個 approved；沒有回 null。</summary>
    public static ApprovalStageRun? Previous(IReadOnlyList<ApprovalStageRun> runs, ApprovalStageRun current);
    /// <summary>J13：被任一到齊取消的分支段根（到達分支以 ArmOf 識別）。</summary>
    public static IReadOnlySet<string> CancelledRoots(ApprovalGraphRuntime g, IReadOnlyList<ApprovalStageRun> runs);
    /// <summary>J4：這個匯合在外層段 outerKey 已到達的分支（ArmOf 去重後）。</summary>
    public static IReadOnlySet<string> ArrivedArms(ApprovalGraphRuntime g, IReadOnlyList<ApprovalStageRun> runs, string joinId, string outerKey);
    /// <summary>還在走的執行線：active、waiting、queued。</summary>
    public static IEnumerable<ApprovalStageRun> Live(IEnumerable<ApprovalStageRun> runs);
    /// <summary>§8.5：每個 active 的簽核節點（含加簽）都還沒有 approved 或 rejected 的任務。不讀圖（U11：結構壞掉也能撤回）。</summary>
    public static bool CanWithdraw(IReadOnlyList<ApprovalStageRun> runs, IReadOnlyList<ApprovalTask> tasks);
}
```

**測試**（節點實例以 Domain 物件手寫，不碰資料庫）：
- `Previous_SameBranch_LastApproved`、`Previous_FirstInBranch_Null`、`Previous_FirstAfterJoin_Null`、`Previous_OtherBranchApproved_NotCandidate`、`Previous_SkippedNotCounted`、`Previous_PassedConditionInBetween_StillFindsApproved`。
- `Previous_AfterReturnCut_SameAsApprovalPath`：同一串 sequence 形狀的列（`BranchKey = ""`），結果與 `ApprovalPath.Previous` 相同（三組資料，含退回後重來與加簽）。
- `Previous_AddSignInBranch_Counted`。
- `CancelledRoots_AnyJoinPassed_SiblingRoots`、`CancelledRoots_AllJoin_Empty`、`CancelledRoots_NestedRegionInsideCancelled_CoveredByWithin`、`CancelledRoots_EmptyBranchArrived_SiblingsCancelled`（到達列的 `InEdgeId` 起點是分支節點本身）。
- `ArrivedArms_TwoInEdgesSameArm_CountedOnce`、`ArrivedArms_OtherOuterKey_NotCounted`（巢狀時同一匯合在不同外層段各算各的）。
- `CanWithdraw_NoActionOnActives_True`、`CanWithdraw_OneBranchWaitingAtJoin_True`、`CanWithdraw_SeatApprovedInAllMode_False`、`CanWithdraw_PreviousNodeApproved_True`。
- 實作時新增：`Segment_OnlyThatBranch_CutsReturned`、`Segment_IncludesPassedConditionAndFork`、`Previous_InnerSegment_BarrierPerSegment`、`CancelledRoots_AnyJoinStillWaitingOrAbsent_Empty`、`CancelledRoots_InnerAnyJoin_ChildOfInnerOuterKey`、`ArrivedArms_ClosedArrivalRow_NotCounted`（偏離 4）、`Live_ActiveWaitingQueued`、`CanWithdraw_AddSignActiveWithApprovedTask_False`。既有 `ApprovalPathTests` 不改斷言通過（偏離 1）。

**驗收：** `sh $TL dotnet test tests/DynamicModules.DomainTests --filter "FullyQualifiedName~ApprovalGraphPath|FullyQualifiedName~ApprovalPath"`。

- [x] 寫失敗測試 → 實作 → 通過 → commit `feat(approval): 流程圖分支段路徑、上一關與撤回資格`（`c95c86d8`）
- [ ] fg2a1 審查、合併（主控）

---

## fg2a2：路線比較、執行入口開關與測試輔助

### Task 3：`ApprovalGraphRoute.Changed`（§8.7）

**Files:**
- Create: `poc/backend/src/DynamicModules.Domain/Approvals/ApprovalGraphRoute.cs`
- Test: `poc/backend/tests/DynamicModules.DomainTests/Approvals/ApprovalGraphRouteTests.cs`（新）

```csharp
public sealed record GraphRouteCheck(bool Changed, string? UndecidableNodeId);

public static class ApprovalGraphRoute
{
    /// <summary>
    /// 同一流程與版本時的路線比較（設計 §8.7 第 2–4 點）：各段有效路徑上 skipped(condition) 的簽核節點，略過條件重算變成立 → Changed；
    /// 有效路徑上 passed 的條件節點，重算選中的出線與當時不同 → Changed（當時的出線＝之後第一列 InEdgeId 屬於它出線的節點實例）；
    /// 被取消的分支段（CancelledRoots）不參與；條件節點無法判斷回 UndecidableNodeId。
    /// eval 只有一個（ApprovalGraphMode.Eval，J27），「無法判斷」依條件種類分流：條件節點的出線交給 Choose，Undecidable 就回 UndecidableNodeId；
    /// skipped(condition) 簽核節點的略過條件 eval != Fails 就算成立（同第一版 ApprovalRouter.Holds：解析不了視為成立）→ Changed。
    /// g 必須從實例自己綁定的快照建（CancelledRoots 對圖外的代號會丟例外）；換流程或版本由呼叫端走第一版判斷，不呼叫這裡。
    /// </summary>
    public static GraphRouteCheck Changed(ApprovalGraphRuntime g, IReadOnlyList<ApprovalStageRun> runs, Func<JsonElement, ConditionTruth> eval);
}
```

**測試：**
- `ConditionNowChoosesOtherEdge_Changed`、`ConditionSameEdge_NotChanged`、`SkippedNodeConditionNowHolds_Changed`、`ApprovedNodeConditionNowFails_NotChanged`（多簽不算少簽）。
- （修訂 2，審查 S3）`SkippedNodeConditionUnparseable_CountsAsHolds_Changed`（`skipped(condition)` 的節點，略過條件現在 `eval` 回 `Undecidable` → 視為成立 → Changed，`UndecidableNodeId` 為 null）、`ConditionNodeUndecidable_NotTreatedAsHolds`（同一個 `eval` 對條件節點的出線回 `Undecidable` → `UndecidableNodeId`，不是 Changed）。
- `CancelledBranchChanges_Ignored`（被取消分支上的條件節點改選、略過條件變成立都不算）。
- `EmptyBranchAnyPassed_CancelledBranchChange_Ignored`（空分支讓任一到齊通過後，被取消分支上的條件變化不觸發）。
- `NestedJoin_InnerConditionChanged_Changed`、`NestedJoin_InsideCancelledOuterBranch_Ignored`。
- `ReturnCut_UsesLatestConditionRun`（退回上一關後條件節點重跑過，只看最新一列）。
- `ChosenEdge_InferredFromJoinArrivalRow`（條件節點出線直接接匯合，下一列是外層段的到達列）。
- `ConditionUndecidable_ReportsNodeId`。

**驗收：** `sh $TL dotnet test tests/DynamicModules.DomainTests --filter "FullyQualifiedName~ApprovalGraph"`。

- [ ] 寫失敗測試 → 實作 → 通過 → commit `feat(approval): 流程圖每次同意的路線比較`

### Task 3b：執行入口開關、執行期共用輔助與測試輔助

（修訂 2，審查 M1：`SupportedKinds`、條件求值轉接、結構不合法的訊息從 fg2b 移到這裡定案，fg2e 才真的只依賴 fg2a2。）

**Files:**
- Modify: `poc/backend/src/DynamicModules.Application/Dynamic/ApprovalService/ApprovalShapeGate.cs`（static → 服務，J1）
- Create: `poc/backend/src/DynamicModules.Application/Dynamic/ApprovalService/ApprovalGraphMode.cs`（`Applies`、`SupportedKinds`、`Eval`，J2、J27；這一段沒有正式呼叫端）
- Modify: `ApprovalHandlers.cs`、`ApprovalPreviewer.cs`、`ApprovalActionHandlers.cs`（四個呼叫點改注入 `ApprovalShapeGate`，判斷與訊息不變）
- Modify: `poc/backend/src/DynamicModules.Application/Common/MessageText.cs`（四語：`ApprovalGraphRuntimeInvalid`「流程圖定義不完整，已停止並通知系統管理員」；`ApprovalGraphPreviewInvalid`「簽核流程「{0}」的流程圖定義不完整，請系統管理員檢查流程設定」，U12；th、vi 不留中文）
- Modify: `ApprovalMessages.cs`（`GraphRuntimeInvalid = MessageRef.Of(MessageText.ApprovalGraphRuntimeInvalid)`；`GraphPreviewInvalid(object flow) => MessageRef.Of(MessageText.ApprovalGraphPreviewInvalid, flow)`，比照 `FlowConditionUndecidable`）
- Modify: `poc/backend/src/DynamicModules.Application/DependencyInjection.cs`（`AddSingleton<ApprovalShapeGate>()`）
- Modify: `poc/backend/tests/DynamicModules.ApplicationTests/Dynamic/Approvals/ApprovalTestBase.cs`（`SeedGraphFlow(r, key, to, priority, ApprovalDefinition graph, bool active = true)`：同 `SeedFlow`，斷言 `Shape == graph`）
- Create: `poc/backend/tests/DynamicModules.ApplicationTests/Dynamic/Approvals/ApprovalGraphTestKit.cs`（`Start()`、`Approval(id, source, when, mode, editable)`、`Condition(id)`、`Fork(id)`、`Join(id, forkId, mode)`、`End()`、`Notify(id)`、`E(id, from, to, when, order, isDefault)`、`G(nodes, edges)`；`OpenGate(IServiceCollection)` 以 `OpenShapeGate : ApprovalShapeGate { Open => true }` 取代註冊）
- Test: `poc/backend/tests/DynamicModules.ApplicationTests/Dynamic/Approvals/ApprovalGraphGateTests.cs`（新）、`ApprovalGraphModeTests.cs`（新，用 `ApprovalTestBase` 的 `BuildPurchase` 建模組）

**測試：**
- `Gate_DefaultRegistration_IsClosed`（從正式的 `AddApplication()` 解析，`Open == false`）。
- `Gate_Opened_RejectReturnsNull`（`OpenShapeGate.Reject(graph 快照)` 回 null；只驗開關本身。fg2a2 還沒有引擎，**不**在這一段用開啟的擋門跑送出，否則會讀到空的 `Stages` 而核准）。
- `KitBuilds_DesignExample_PassesValidator`（測試輔助組出的設計 §7.1 範例通過 `ApprovalGraphValidator.Check`）。
- `ApprovalGraphModeTests`：`Applies_GraphFlow_True`、`Applies_FallbackInstance_False`、`Applies_Sequence_False`；`SupportedKinds_SixKinds_NoNotify`、`Build_WithSupportedKinds_NotifyRejected`（`Build(設計 §7.1 範例, SupportedKinds, …)` 回 null、`unsupportedKind`）；`Eval_Holds`、`Eval_Fails`、`Eval_UnknownField_Undecidable`（引用已刪除的欄位）、`Eval_EmptyGroup_Holds`、`Eval_NotObject_Undecidable`、`Eval_AgreesWithRouterHolds`（同一組條件，`Eval != Fails` 與 `ApprovalRouter.Holds` 結果相同，J27）。
- `MessageTextTests` 照舊通過（兩個新鍵四語齊全、th、vi 沒有中文）。
- 回歸：`ApprovalShapeGateTests` **不改任何一行**通過；`ApprovalSubmitTests`、`ApprovalPreviewTests`、`ApprovalActionTests` 不改斷言通過。

**驗收：** `sh $TL dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~ApprovalGraphGate|FullyQualifiedName~ApprovalGraphMode|FullyQualifiedName~ApprovalShapeGate|FullyQualifiedName~ApprovalSubmit|FullyQualifiedName~ApprovalPreview|FullyQualifiedName~ApprovalAction|FullyQualifiedName~MessageText"`；`dotnet build`。

- [ ] 寫失敗測試 → 實作 → 通過 → commit `refactor(approval): 流程圖執行入口改為可替換的擋門服務並加入執行期共用輔助與測試輔助`
- [ ] fg2a2 審查、合併（主控）；合併後 `ApprovalShapeGate`、`ApprovalGraphMode`（`Applies`、`SupportedKinds`、`Eval`）、`GraphRuntimeInvalid`、`GraphPreviewInvalid`、測試輔助的簽章定案

---

## fg2b：推進引擎、唯一完成者與送出

### Task 4：`ApprovalGraphEngine`、`AdvanceAsync` 與送出入口

**Files:**
- Create: `poc/backend/src/DynamicModules.Application/Dynamic/ApprovalService/ApprovalGraphEngine.cs`（含 `PendingNotice`、`GraphAdvance`、`IApprovalStageSteps`；修訂 2：**不**含 `SupportedKinds`，改引用 `ApprovalGraphMode.SupportedKinds`）
- Modify: `ApprovalEngine.cs`（實作 `IApprovalStageSteps`；新增 `AdvanceAsync`（J22）；`ActivateNextAsync`、`CompleteAsync` 改 private；`StartAsync` 加 `defer`；`SkipAsync`、`ResolveAddSignAsync` 改 public；`RuntimeInvalid` 加選用參數 `MessageRef? message = null`（修訂 2，審查 M2）；`RerouteAsync` 的新實例改呼叫 `AdvanceAsync`；建構函式注入 `ApprovalGraphEngine`）
- Modify: `ApprovalHandlers.cs`（送出改呼叫 `AdvanceAsync`、刪自己的 `CompleteAsync`；`PickErrors`、`UserFieldErrors` 改讀 `ApprovalDefinitionRefs.Steps`）
- Modify: `ApprovalActionHandlers.cs`（`ApproveAsync` 關卡完成後改呼叫 `AdvanceAsync(inst, module, current, done: run, ct)`、刪自己的 `CompleteAsync`；只改這一段，graph 的其餘同意邏輯在 fg2c）
- Modify: `ApprovalAlert.cs`、`poc/backend/src/DynamicModules.Application/Common/Behaviors/ApprovalAlertBehavior.cs`（J20 快照；檢查寫入結果並記 log）
- Modify: `ApprovalMessages.cs`（`ConditionNodeUndecidable(name)`；`BranchCancelledNote(name)` 繁中字面，只給歷程 comment。`GraphRuntimeInvalid` 已在 fg2a2）
- Modify: `poc/backend/src/DynamicModules.Application/Common/MessageText.cs`（`ApprovalConditionNodeUndecidable`「條件「{0}」無法判斷，已停止並通知系統管理員」，四語。`ApprovalGraphRuntimeInvalid` 已在 fg2a2）
- Modify: `poc/backend/src/DynamicModules.Application/DependencyInjection.cs`（`AddScoped<ApprovalGraphEngine>()`）
- Test: `poc/backend/tests/DynamicModules.ApplicationTests/Dynamic/Approvals/ApprovalGraphSubmitTests.cs`（新，`ConfigureServices` 呼叫 `OpenGate`）

```csharp
public sealed record PendingNotice(string Kind, IReadOnlyList<long> TaskIds);
public enum GraphAdvance { Stopped, ReachedEnd }

/// <summary>流程圖執行引擎（流程圖設計 §8.1–§8.3、§8.8）：每一步在呼叫端的交易內；失敗由呼叫端回傳、整筆回滾。
/// 不注入 ApprovalEngine；單關步驟由 steps 參數傳入（J22）。走到結束只回 ReachedEnd，不呼叫 CompleteAsync。
/// 建圖用 ApprovalGraphMode.SupportedKinds、條件求值用 ApprovalGraphMode.Eval（J2、J27）；失敗一律經 steps.RuntimeInvalid(inst, reason, message)。</summary>
public sealed class ApprovalGraphEngine(IApprovalInstanceRepository repo, ApproverResolver resolver, FieldTypeRegistry registry,
    IModuleRepository modules, IBusinessTime time, ApprovalNotifier notifier, IUnitOfWork uow)
{
    /// <summary>送出或重來後從 start 推進。</summary>
    public Task<(GraphAdvance Result, ServiceResult? Failure)> StartAsync(ApprovalInstance inst, Module module,
        IReadOnlyDictionary<string, JsonElement?> values, IApprovalStageSteps steps, CancellationToken ct);
    /// <summary>簽核節點或其加簽完成後：同段同主節點的排隊加簽先啟動，否則沿主節點出線推進（J11）。</summary>
    public Task<(GraphAdvance Result, ServiceResult? Failure)> AfterApprovedAsync(ApprovalInstance inst, Module module, ApprovalStageRun done,
        IReadOnlyDictionary<string, JsonElement?> values, IApprovalStageSteps steps, CancellationToken ct);
}
```

**推進規則**（設計 §8.2，逐條對應測試）：
1. 每次推進先 `ApprovalGraphRuntime.Build(def, ApprovalGraphMode.SupportedKinds, …)`（J7）；null → `steps.RuntimeInvalid(inst, reason, ApprovalMessages.GraphRuntimeInvalid)`（409＋異常通知）。條件的 `eval` 由 `ApprovalGraphMode.Eval(module, values, registry, time.Today, userIds)` 建一次、整次推進共用（J27）。
2. `Advance(edge, branchKey)`：寫目標節點的一列（J10），依型別：
   - `approval`：略過條件 `ApprovalRouter.Holds` 不成立 → `skipped(condition)`、寫 `skip` 歷程、沿出線繼續；照第一版解析簽核人（`Picks` 鍵＝節點代號），`AllUnitsEmpty`＋`onEmpty = skip` → `skipped(empty)` 繼續；解析失敗 409；否則 `steps.StartAsync(..., defer)`，這條執行線停住。
   - `condition`：`Choose`（J8）；無法判斷 → `steps.RuntimeInvalid(inst, reason, ApprovalMessages.ConditionNodeUndecidable(節點名稱))`（409＋異常通知）；否則 `passed`、沿選中的線繼續。
   - `fork`：`passed`，依 J5 逐條出線以 `Child(branchKey, e.Id)` 推進。
   - `join`：依 J4 寫到達列（到達分支以 `ArmOf` 識別、同分支去重）；`all` 且 `ArrivedArms` 等於配對分支節點的全部出線 → 這個匯合的到達列全部 `passed`，以外層段沿出線繼續；`any` → 這一列直接 `passed`，取消其他分支（下一點）後繼續；同一匯合同一外層段已 `passed` 過就不再推進（J4 不變式 2，違反 → `RuntimeInvalid`）。
   - 取消：被取消段內的 `active`、`waiting`、`queued` 改 `closed`、`CloseReason = branchCancelled`、`CompletedTime`；這些列的 `open` 任務改 `closed`；寫一筆 `branch-cancelled` 歷程（系統身分，comment 依 U8 的繁中字面，`DataJson = { forkId, joinId, arrived, cancelled, name }`）；被取消者不另發通知（設計 U5）。
   - `end`：還有 `Live` 的列 → `RuntimeInvalid`（G8 的防線）；否則 `passed`，回 `ReachedEnd`。
3. 推進結束：寫延後的「待簽」通知，只寫任務仍 `open` 的（J6）；重算 `CurrentStageRunId`（J15）。
4. `AfterApprovedAsync`：同 `BranchKey`、同主節點（`NodeId`）有 `queued` 加簽 → `steps.ResolveAddSignAsync` 後 `steps.StartAsync(..., ApprovalAddSigned, defer)`；否則沿主節點出線推進。整個實例的其他 queued 一律不看（J11）。

**送出入口：** `SubmitApprovalHandler` 照舊建實例、寫 `submit` 歷程，呼叫 `engine.AdvanceAsync(inst, module, values, null, ct)`；`Completed` → outcome `approved`。擋門仍在（正式環境照舊 409），測試開關下才進得來。

**測試（`ApprovalGraphSubmitTests`，`BuildPurchase`＋`SeedGraphFlow`）：**
- `Submit_Linear_StartPassed_FirstApprovalActive`、`Submit_RunsCarryNodeFields`（`NodeKind`、`BranchKey = ""`、`InEdgeId`、`StageCode = NodeId`）。
- `Submit_Condition_ChoosesByOrder`、`Submit_Condition_DefaultWhenNoneHolds`、`Submit_ConditionDefaultToEnd_ApprovedImmediately_TransitionRuns`。
- `Submit_GraphEnd_CompleteOnce`（直接到結束：`complete` 歷程恰好一筆、發起人恰好一則 `approvalApproved`、轉移執行一次）。
- `Submit_SkipCondition_SkippedWithHistory`、`Submit_OnEmptySkip_SkippedEmpty`、`Submit_ResolveFailure_409_NothingWritten`。
- `Submit_Fork_BothBranchesActive_BranchKeysAreEdgeIds`、`Submit_NestedFork_BranchKeyPath`（`e3/e9`）、`Submit_ForkExpansionOrder_IsDefinitionOrder`（Seq 依出線順序）。
- `Submit_EmptyBranchAllJoin_ArrivalCountsAsThatBranch`（`fork → join` 直連的空分支：到達列 `InEdgeId` 是直連線，另一條分支簽完後匯合通過）、`Submit_BranchConditionDirectToJoin_ArrivalCountsAsThatArm`（修訂 2，審查 S1：改用合法的圖，同 fg2a1 的 `ConditionIntoJoin`：分支 1 的條件節點一條出線經簽核節點、預設線直接接匯合；條件選預設線時到達列的 `InEdgeId` 是直連那條、算在分支 1，all 匯合繼續等分支 2，分支 2 簽完後恰好推進一次。同一分支兩列到達的去重是防線，由 fg2a1 的 `ArrivedArms_TwoInEdgesSameArm_CountedOnce` 守）。
- `Submit_AnyJoin_FirstBranchArrivesImmediately_LaterBranchesNotStarted_HistoryBranchCancelled`、`Submit_AnyJoin_EmptyBranch_CancelsSiblingsNotItself`。
- `AnyJoin_LaterBranchArrivesImmediately_EarlierTasksClosed_NoPendingNotice`（U7）。
- `Submit_NestedAnyInsideAll_InnerCancelOnlyInnerSiblings`（巢狀取消只取消內層兄弟分支）。
- `Submit_ConditionUndecidable_409_AlertWrittenAfterRollback`（條件節點引用的欄位已刪除：409、訊息鍵 `ApprovalConditionNodeUndecidable`、沒有實例、系統管理員收到 `approvalAdminAlert`，J20）。
- `Submit_BrokenGraph_409_AlertWritten`（直接寫一份缺預設線的 graph；訊息鍵 `ApprovalGraphRuntimeInvalid`，不是 `ApprovalRuntimeInvalid`）。
- `Submit_NotifyNode_409_Unsupported_AlertWritten_NothingWritten`（U2 能力守門；訊息鍵 `ApprovalGraphRuntimeInvalid`）。
- `RuntimeInvalid_NoMessage_SequenceMessageUnchanged`（`RuntimeInvalid(inst, reason)` 不傳 `message` 時仍回 `ApprovalRuntimeInvalid`，M2）。
- `Submit_PickRequiredForEveryPickNode_EvenOffPath`（J25、設計 §8.9）。
- `Submit_CurrentStageRunId_IsMinSeqActive`。
- `Submit_AddSignQueuedInOtherBranch_NotActivated`（手寫資料：另一條分支有 queued 加簽，本分支節點完成時不啟動它，J11）。
- `Alert_SequenceInstanceRolledBack_StillNotifiesAdmins`（J20 對 sequence 的效果：送出時所有關卡跳過、轉移執行前再驗失敗）、`Alert_WriteFails_Logged`（替換 handler 回失敗結果，`TestLogSink` 或等價的 log 收集看得到）。
- `Engine_ResolvesFromDi_NoCycle`（從 DI 解析 `ApprovalEngine`、`ApprovalApprover`、`SubmitApprovalHandler`，J22）。
- 回歸：`ApprovalSubmitTests`、`ApprovalRuntimeCheckTests`、`ApprovalFollowUpTests`、`ApprovalActionTests`、`ApprovalRerouteTests`、`ApprovalShapeGateTests` 不改斷言通過（`AdvanceAsync` 對 sequence 是同一串呼叫）。

**驗收：** `sh $TL dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~ApprovalGraphSubmit|FullyQualifiedName~ApprovalGraphGate|FullyQualifiedName~ApprovalShapeGate|FullyQualifiedName~ApprovalSubmit|FullyQualifiedName~ApprovalRuntime|FullyQualifiedName~ApprovalFollowUp|FullyQualifiedName~ApprovalAction|FullyQualifiedName~ApprovalReroute|FullyQualifiedName~MessageText"`；`dotnet build`；`grep -n "CompleteAsync(" src/DynamicModules.Application` 只剩定義與 `AdvanceAsync` 內一處。

- [ ] 寫失敗測試 → 實作 → 通過 → commit `feat(approval): 流程圖執行引擎：送出後依圖推進、並行與匯合，完成改由單一入口`
- [ ] fg2b 審查、合併（主控）；合併後 `IApprovalStageSteps`（含 `RuntimeInvalid` 的 `message` 參數）、`AdvanceAsync`、`ApprovalGraphEngine` 建構函式的簽章定案

---

## fg2c：同意、重新比對、退回、撤回、終止

### Task 5：同意後推進、重新比對與載入時的結構再驗

**Files:**
- Modify: `ApprovalActionHandlers.cs`
  - `ApprovalActionSupport.LoadAsync`：對 graph 實例（J2）建 `Runtime`，放進 `Loaded.Runtime`／`RuntimeErrors`（J7）；擋門呼叫照舊（正式環境仍 409）。
  - `ApprovalActionSupport.Loaded`：`Runtime`、`RuntimeErrors` 用 init 屬性加（J7，審查 S7），6 處解構不動。
  - `ApprovalApprover.ApproveAsync`：graph 且 `Runtime is null` → 在任何寫入前 `engine.RuntimeInvalid(inst, reason, ApprovalMessages.GraphRuntimeInvalid)`；可編輯欄位改用 `Steps(def)` 依 `run.StageCode` 找；`addSign is not null` 而且 graph → 409 `GraphNotSupported`（到 fg2d）；完成判斷照舊，`AdvanceAsync(…, done: run, …)` 已在 fg2b 接好。
- Modify: `ApprovalRouter.cs`（`Recheck`：同流程同版本而且是 graph → `ApprovalGraphRoute.Changed(loaded.Runtime, runs, ApprovalGraphMode.Eval(…))`（`eval` 不另寫，J27），無法判斷 → 回 `RouteCheck` 新欄位 `UndecidableNode`；不同流程、版本、備援的判斷照舊；`Picks` 改讀 `Steps`）
- Modify: `ApprovalEngine.cs`（`RerouteAsync`：graph 舊實例連 `waiting` 一起關、寫 `CloseReason = instanceEnded`；新實例已由 fg2b 改走 `AdvanceAsync`）
- Test: `poc/backend/tests/DynamicModules.ApplicationTests/Dynamic/Approvals/ApprovalGraphApproveTests.cs`（新）

**規則：** 條件節點在重新比對時無法判斷 → `engine.RuntimeInvalid(inst, reason, ApprovalMessages.ConditionNodeUndecidable(節點名稱))`（409＋異常通知），這次同意不寫入（整筆回滾）。流程層 `when` 無法判斷照舊 `FlowConditionUndecidable`。

**測試（`ApprovalGraphApproveTests`）：**
- `Approve_Linear_AdvancesToNextNode`、`Approve_LastNode_EndPassed_InstanceApproved_TransitionRuns`、`Approve_GraphEnd_CompleteOnce`。
- `AllJoin_FirstBranchWaits_SecondCompletes_ContinuesOnce`、`AllJoin_ArrivalRowPerBranch_InEdgeIds`、`AllJoin_EmptyBranchPlusApprovalBranch_ContinuesAfterApproval`。
- `AnyJoin_FirstArrival_OthersClosedBranchCancelled_TasksClosed_HistoryWritten`、`AnyJoin_CancelledTaskLosesReadGrant`（`ApprovalReadGrant` 對被取消者回無權限）、`AnyJoin_CancelledAssignees_NoNotice`、`AnyJoin_NestedForkInCancelledBranch_AllClosed`。
- `Approve_AllMode_InBranch_WaitsEverySeat`、`SamePersonInTwoBranches_EachTaskSignedSeparately`。
- `Recheck_ConditionEdgeChanged_Rerouted_NewInstanceFromStart`、`Recheck_SkippedNodeNowHolds_Rerouted`、`Recheck_CancelledBranchChange_NotRerouted`、`Recheck_EmptyBranchAnyPassed_CancelledBranchChange_NotRerouted`、`Recheck_NestedJoinConditionChanged_Rerouted`、`Recheck_ConditionNodeUndecidable_409_AlertWritten_NothingWritten`（訊息鍵 `ApprovalConditionNodeUndecidable`）、`Recheck_SkippedNodeConditionUnparseable_Rerouted`（審查 S3 的整合版：略過條件引用的欄位已刪除 → 視為成立 → 重來）。
- `Reroute_GraphInstance_ClosesActiveAndWaiting_InstanceEnded`、`Reroute_ToGraphAllSkipped_CompleteOnce`。
- `FallbackInstanceOfGraphFlow_UsesSequencePath`（重新比對沒有流程符合 → 備援實例一關或簽，同意後核准，J2）。
- `Recheck_PicksRequiredInNewGraphFlow_NeedsResubmit`（J25）。
- `Approve_BrokenBoundGraph_409_Alert_NothingWritten`（綁定定義缺預設線，即使重新比對會選到另一條 sequence 流程也一樣，J7；訊息鍵 `ApprovalGraphRuntimeInvalid`）。
- `AddSign_OnGraphInstance_StillRejected409`。
- 回歸：`ApprovalShapeGateTests`（不改）、`ApprovalActionTests`、`ApprovalRerouteTests`、`ApprovalReadGrantTests` 不改斷言通過。

**驗收：** `sh $TL dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~ApprovalGraphApprove|FullyQualifiedName~ApprovalShapeGate|FullyQualifiedName~ApprovalAction|FullyQualifiedName~ApprovalReroute|FullyQualifiedName~ApprovalReadGrant"`。

- [ ] 寫失敗測試 → 實作 → 通過 → commit `feat(approval): 流程圖同意後推進與每次同意的重新比對`

### Task 6：退回、撤回、終止

**Files:**
- Modify: `ApprovalActionHandlers.cs`
  - `RejectTaskHandler`：graph 退回發起人 → 被退回的那列 `closed`＋`instanceEnded`、其他 `Live` 列全部 `closed`＋`instanceEnded`、全部 `open` 任務關閉，其餘照第一版（`rejectTo` 轉移、通知）；**不要求 `Runtime`**（U11）。graph 退回上一關 → 要求 `Runtime`（否則 `engine.RuntimeInvalid(inst, reason, ApprovalMessages.GraphRuntimeInvalid)`，409＋異常通知）；`ApprovalGraphPath.Previous`，沒有 → 400 `NoPreviousStage`；只關「目前這一列」的 `open` 任務與「同段同主節點」的排隊加簽，被退回的列 `returned`，重啟（J12），被退回的是加簽時照第一版重排一筆（`Seq` 取最大加一，帶齊 `NodeId`、`NodeKind`、`BranchKey`，J10）。
  - `WithdrawApprovalHandler`：graph 用 `ApprovalGraphPath.CanWithdraw`，不符 409 `CannotWithdraw`；`Live` 列全部 `closed`＋`instanceEnded`，`current` 寫歷程用 `CurrentStageRunId`；不要求 `Runtime`。
  - `CancelApprovalHandler`：graph 連 `waiting` 一起關，`instanceEnded`；不要求 `Runtime`。
- Modify: `ApprovalEngine.cs`（`RestartStageAsync`：定義節點改用 `Steps(def)` 找；三個分支（備援、加簽、一般節點）的新列都複製 `NodeId`、`NodeKind`、`BranchKey`、`InEdgeId`；重啟後 graph 重算 `CurrentStageRunId`）
- Test: `poc/backend/tests/DynamicModules.ApplicationTests/Dynamic/Approvals/ApprovalGraphActionTests.cs`（新）

**測試：**
- （修訂 2，補 fg2a1 備註）`ReturnPrevious_AfterInnerJoinInOuterBranch_400`（巢狀：外層分支內，內層匯合之後的第一個簽核節點退回上一關 → 400 `NoPreviousStage`；內層匯合列的 `BranchKey` 是外層分支段，`Previous` 以它為邊界）。
- `RejectToInitiator_InBranch_AllLinesClosed_InstanceEnded`（另一條分支的 active 列與任務都關閉；被退回的那一列也是 `CloseReason = instanceEnded`）、`RejectToInitiator_RejectToStateRuns`。
- `ReturnPrevious_InBranch_RestartsSameBranchNode`、`ReturnPrevious_InBranch_OtherBranchTaskStaysOpen`、`ReturnPrevious_FirstInBranch_400`、`ReturnPrevious_FirstAfterJoin_400`、`ReturnPrevious_RootSegment_SameAsSequence`、`ReturnPrevious_ThenApprove_ConditionReevaluated`（重啟的節點同意後，途中的條件節點用當下的值重跑）。
- `Restart_EveryPath_CarriesGraphFields`（一般節點、加簽目標、被退回加簽的重排，三列都斷言 `NodeId`、`NodeKind`、`BranchKey`）。
- `Withdraw_NoActionOnActives_Ok_AllLinesClosed`、`Withdraw_PreviousNodeInBranchApproved_StillOk`、`Withdraw_OneBranchWaitingAtJoin_Ok`、`Withdraw_AnyActiveHasApproval_409`、`Withdraw_CancelledBranchIgnored`。
- `Cancel_GraphInstance_AllLinesClosed_InstanceEnded`。
- U11 對照：`BrokenGraph_ReturnPrevious_409_Alert`（訊息鍵 `ApprovalGraphRuntimeInvalid`）、`BrokenGraph_Withdraw_Ok`、`BrokenGraph_Cancel_Ok`、`BrokenGraph_RejectToInitiator_Ok`。
- 回歸：`ApprovalShapeGateTests`（不改）、`ApprovalActionTests`、`ApprovalManagerActionTests`、`ApprovalBackstopTests` 不改斷言通過。

**驗收：** `sh $TL dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~ApprovalGraphAction|FullyQualifiedName~ApprovalShapeGate|FullyQualifiedName~ApprovalAction|FullyQualifiedName~ApprovalManagerAction|FullyQualifiedName~ApprovalBackstop"`。

- [ ] 寫失敗測試 → 實作 → 通過 → commit `feat(approval): 流程圖分支內退回、撤回與終止`
- [ ] fg2c 審查、合併（主控）

---

## fg2d：加簽、轉交、改派、檢視與取消原因的畫面

### Task 7：加簽、轉交、改派、代理在分支內

**Files:**
- Modify: `ApprovalActionHandlers.cs`（`ApprovalApprover` 加簽：graph 依 J10、J11 寫排隊列並重新編號；刪 Task 5 的加簽暫擋；轉交、改派 graph 要求的只有任務 `open`，不要求 `Runtime`（U11））
- Test: `poc/backend/tests/DynamicModules.ApplicationTests/Dynamic/Approvals/ApprovalGraphAddSignTests.cs`（新）

（初版這裡的「`ActivateNextAsync` 對 graph 不再走先啟動 queued」「`AfterApprovedAsync` 先啟動同段 queued」已提前到 fg2b，J11。）

**測試：**
- `AddSign_InBranch_QueuedSameBranchKey_ActivatesBeforeAdvancing`、`AddSign_OtherBranchUnaffected`、`AddSign_TwoBranchesEachQueued_EachActivatesOwn`。
- `AddSign_Nested_InBranch_OrderSameAsSequence_SeqMonotonic`（第一版巢狀加簽的順序案例搬來 graph 跑，順序相同、`Seq` 嚴格遞增）。
- `AddSign_LimitCountsAcrossBranches`（加簽關卡數政策以整個實例計）。
- `AddSign_RunCarriesGraphFields`（J10）、`ReturnPrevious_ToAddSignInBranch_RequeuedSameBranch`。
- `AddSign_BrokenGraph_409_Alert`（U11；訊息鍵 `ApprovalGraphRuntimeInvalid`）、`Transfer_BrokenGraph_Ok`、`Reassign_BrokenGraph_Ok`。
- `Transfer_InBranch_SameRunNewTask`、`Transfer_TargetHasTaskInOtherBranch_Allowed`、`Reassign_InBranch_OldAssigneeNotified`。
- `Delegate_InBranch_ApproveByDelegate_CountsAsHolder`、`Delegate_HolderAndDelegateInSameNode_OneVote`（graph 的一人一票照第一版看本人）。
- 回歸：`ApprovalAddSignTests`、`ApprovalTransferTests`、`ApprovalDelegateTests` 不改斷言通過。

**驗收：** `sh $TL dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~ApprovalGraphAddSign|FullyQualifiedName~ApprovalShapeGate|FullyQualifiedName~ApprovalAddSign|FullyQualifiedName~ApprovalTransfer|FullyQualifiedName~ApprovalDelegate"`。

- [ ] 寫失敗測試 → 實作 → 通過 → commit `feat(approval): 流程圖分支內的後加簽、轉交、改派與代理`

### Task 8：檢視、簽核中心與取消原因的畫面

**Files:**
- Modify: `ApprovalQueryHandlers.cs`（`ApprovalViews.Instance`：graph 的 `stages` 只放 `NodeKind = approval` 的列並帶 J24 欄位；`CanReturnPrevious` 用 `ApprovalGraphPath.Previous`，結構不合法時 false；`canWithdraw` 用 `CanWithdraw`；`EditableKeys` 改讀 `Steps(def)`；簽核中心「待簽」列名稱取任務所屬列，U5）
- Modify: `ApprovalModels.cs`（`ApprovalStageView` 加 J24 三個欄位）
- Modify: `poc/frontend/src/domain/dynamic/approval-types.ts`（`ApprovalStageView` 加 `nodeId?`、`branchKey?`、`closeReason?`）
- Modify: `poc/frontend/src/presentation/dynamic/approval/ApprovalStageList.vue`（`closeReason === 'branchCancelled'` 時 subtitle 加 `approval.stageCloseReason.branchCancelled`，U6）
- Modify: `poc/frontend/src/presentation/dynamic/approval/ApprovalActionLog.vue`（`branch-cancelled` 不顯示 comment，改顯示 `approval.panel.branchCancelled`，`{name}` 取 `data.name` 依語系解析、沒有用 `approval.panel.branchUnnamed`，U8）
- Modify: `poc/frontend/src/i18n/{zh-TW,en-US,th,vi}.json`（`approval.action.branch-cancelled`「其他分支已自動取消」、`approval.panel.branchCancelled`「分支「{name}」已完成，其他分支自動取消」、`approval.panel.branchUnnamed`「未命名分支」、`approval.stageCloseReason.branchCancelled`「其他分支已完成，不需處理」）
- Test: `poc/backend/tests/DynamicModules.ApplicationTests/Dynamic/Approvals/ApprovalGraphViewTests.cs`（新）；`ApprovalStageList.vitest.spec.ts`（新）、`ApprovalActionLog.vitest.spec.ts`（加案例）

**測試：**
- `View_Graph_ListsOnlyApprovalAndAddSignRuns`、`View_Graph_CancelledRunHasCloseReason`。
- `View_CanReturnPrevious_MatchesHandler`（同一情境，檢視為 true 的任務退回上一關成功、為 false 的回 400）。
- `View_CanWithdraw_MatchesHandler`、`View_EditableKeysFromApprovalNode`、`View_BranchCancelledAction_DataHasNameI18n`。
- `View_Sequence_JsonHasNoNodeFields`（序列化後不含 `nodeId`、`branchKey`、`closeReason`）。
- `Inbox_Todo_Graph_ShowsOwnNodeName`、`Inbox_Submitted_Graph_ShowsMinSeqActiveName`、`Inbox_Todo_CancelledTaskNotListed`、`Inbox_Sequence_Unchanged`。
- 前端：`StageList_BranchCancelled_ShowsNoNeedToHandle`、`StageList_InstanceEnded_NoExtraText`、`ActionLog_BranchCancelled_UsesLocaleNotComment`（en-US 下不出現繁中 comment）、`ActionLog_BranchCancelled_NoName_Unnamed`；`bunx vitest run src/i18n --maxWorkers=2`（四語鍵集合一致）。
- 回歸：`ApprovalQueryTests`、`ApprovalRecordViewTests` 不改斷言通過；前端既有 `ApprovalActionLog`、`ApprovalPanel` spec 不改斷言通過。

**驗收：** `sh $TL dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~ApprovalGraphView|FullyQualifiedName~ApprovalQuery|FullyQualifiedName~ApprovalRecordView"`；`bunx vitest run src/presentation/dynamic/approval src/i18n --maxWorkers=2`；`bun run typecheck`（或專案的 `vue-tsc` 指令）。畫面走查：graph 實例在正式環境開不到，四寬度走查併入 fg2g2（見 Task 13）。

- [ ] 寫失敗測試 → 實作 → 通過 → commit `feat(approval): 簽核檢視與簽核中心支援流程圖實例，顯示分支取消原因`
- [ ] fg2d 審查、合併（主控）

---

## fg2e：預覽與設計器試算的預計路徑

### Task 9：`ApprovalGraphPreview` 與預覽器

**Files:**
- Create: `poc/backend/src/DynamicModules.Domain/Approvals/ApprovalGraphPreview.cs`
- Modify: `ApprovalPreviewer.cs`（graph 分支；擋門呼叫照舊）
- Modify: `ApprovalModels.cs`（`PreviewPath`；`ApprovalPreviewResponse` 末尾加 `PreviewPath? Path = null`；`PreviewStage` 末尾加 `string? AnyGroup = null`）
- Modify: `poc/frontend/src/domain/dynamic/approval-types.ts`（`PreviewStage.anyGroup?`）、`poc/frontend/src/presentation/dynamic/approval/PreviewStages.vue`（同一 `anyGroup` 的第一項上方顯示 `approval.submit.stageAnyGroup`）、`poc/frontend/src/i18n/{zh-TW,en-US,th,vi}.json`（`approval.submit.stageAnyGroup`「以下分支任一條完成即可」）
- Test: `poc/backend/tests/DynamicModules.DomainTests/Approvals/ApprovalGraphPreviewTests.cs`（新）、`poc/backend/tests/DynamicModules.ApplicationTests/Dynamic/Approvals/ApprovalGraphPreviewerTests.cs`（新，`OpenGate`）、`PreviewStages.vitest.spec.ts`（加案例）

```csharp
public sealed record PreviewStep(string NodeId, bool Undecided, string? AnyGroup);

public static class ApprovalGraphPreview
{
    /// <summary>
    /// 預計路徑（設計 §8.9、J19）：從開始依 eval 走；hidden(條件) 為 true 的條件節點標 Undecided，不走它的出線，
    /// 從 ResumeAfter 接著走（U9、J26），null 就停；all 匯合依定義順序列全部分支；any 匯合依 U10：有確定立即到達的分支就不列分支，
    /// 否則全部分支帶 AnyGroup。回傳經過的節點（含略過條件不成立的簽核節點，由呼叫端判斷）與線；條件節點無法判斷回 UndecidableNodeId。
    /// </summary>
    public static (IReadOnlyList<PreviewStep> Steps, IReadOnlyList<string> EdgeIds, string? UndecidableNodeId) Walk(
        ApprovalGraphRuntime g, Func<JsonElement, ConditionTruth> eval, Func<JsonElement, bool> hidden, Func<ApprovalNodeDef, bool> passesWithoutHuman);
    /// <summary>J26：直接後必經節點（虛擬終點回 null）。</summary>
    public static string? ResumeAfter(ApprovalGraphRuntime g, string nodeId);
}
```

`passesWithoutHuman` 由預覽器提供：簽核節點在預覽裡會被略過（略過條件不成立或整關為空）回 true。

**規則：**
1. 簽核節點逐一套第一版的判斷（隱藏欄位 → `conditional`、略過條件不成立 → 設計器 `skip`／記錄預覽不列、自選 → `pick`、`AllUnitsEmpty` → 同略過、解析問題只在設計器填 `Problem`）。
2. `Picks` 列定義中所有自選簽核節點（不只路徑上的，J25）。
3. U9 的條件節點回一筆 `PreviewStage(Code: 條件節點代號, Name: 節點名稱（沒有就空 I18nText，前端顯示「依條件決定」）, Status: conditional)`；不新增後端訊息。
4. （修訂 2，審查 M1）建圖用 `ApprovalGraphRuntime.Build(def, ApprovalGraphMode.SupportedKinds, …)`、`eval` 用 `ApprovalGraphMode.Eval(…)`（都在 fg2a2，J2、J27），不引用 `ApprovalGraphEngine`。結構不合法（`Build` 回 null，含不支援的節點型別）→ 409 `ApprovalMessages.GraphPreviewInvalid(流程名稱)`（U12，fg2a2 建；不登記異常通知：預覽不寫入，送出時才登記）；條件節點無法判斷 → 409 `FlowConditionUndecidable(流程名稱)`（J19）。fg2e 不改 `MessageText.cs`、`ApprovalMessages.cs`。
5. sequence 照舊，`Path`、`AnyGroup` 為 null、不寫出。

**測試：**
- Domain：`Walk_Linear`、`Walk_ConditionChoosesEdge`、`Walk_AllJoin_BothBranches_DefinitionOrder`、`Walk_HiddenCondition_UndecidedAndResumeAtPostdominator`、`Walk_HiddenCondition_ReachableButNotPostdominator_NotListed`（審查的反例：A 可選到總經理或直接結束、B 經總經理結束 → 不列總經理）、`Walk_HiddenCondition_NoCommonSuccessor_Stops`、`Walk_HiddenConditionInsideBranch_ResumeAtJoin`、`Walk_AnyJoin_ImmediateBranch_NoBranchesListed`、`Walk_AnyJoin_ImmediateBranchLaterInOrder_StillNoBranchesListed`、`Walk_AnyJoin_NoImmediate_AllBranchesGrouped`、`Walk_AnyJoin_HiddenConditionInBranch_NotImmediate_Grouped`、`Walk_UndecidableCondition_Reported`；`ResumeAfter_DiamondMerge`、`ResumeAfter_MultipleEnds_Null`、`ResumeAfter_NestedRegions`。
- Application：`Preview_Graph_StagesFollowPredictedPath_PathReturned`、`Preview_Graph_HiddenConditionNode_ConditionalThenPostdominator`（U9 例子）、`Preview_Graph_AnyJoinGrouped`、`Preview_Graph_AnyJoinImmediate_OnlyAfterJoin`（U10 例子）、`Designer_Graph_AnyJoinImmediate_SkippedListed`、`Preview_Graph_PickListsAllPickNodes`、`Designer_Graph_SkippedNodesListed_ProblemsFilled`（設計器試算以已存的 graph 流程跑 `PreviewApprovalFlowHandler`）、`Preview_Graph_ConditionUndecidable_409`（訊息鍵 `ApprovalFlowUndecidable`、沒有異常通知）、`Preview_Graph_BrokenGraph_409_PreviewInvalid_NoAlert`（缺預設線：訊息鍵 `ApprovalGraphPreviewInvalid`、帶流程名稱、沒有 `approvalAdminAlert`，U12）、`Designer_Graph_BrokenGraph_409_PreviewInvalid`、`Preview_Graph_NotifyNode_409_NoAlert`（訊息鍵 `ApprovalGraphPreviewInvalid`）、`Preview_Sequence_ResponseUnchanged`（`Path`、`anyGroup` 不出現在 JSON）。
- 前端：`PreviewStages_AnyGroup_NoteOncePerGroup`；`bunx vitest run src/i18n --maxWorkers=2`。
- 回歸：`ApprovalPreviewTests`、`ApprovalShapeGateTests`（不改）不改斷言通過。

**驗收：** `sh $TL dotnet test tests/DynamicModules.DomainTests --filter "FullyQualifiedName~ApprovalGraphPreview"`；`sh $TL dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~ApprovalGraphPreviewer|FullyQualifiedName~ApprovalPreview|FullyQualifiedName~ApprovalShapeGate"`；`bunx vitest run src/presentation/dynamic/approval src/i18n --maxWorkers=2`。

- [ ] 寫失敗測試 → 實作 → 通過 → commit `feat(approval): 流程圖的送出前預覽與試算預計路徑`
- [ ] fg2e 審查、合併（主控）

---

## fg2f：以實例序列化的併發

### Task 10：處理權、處理權內的檢查、重試與晚到訊息

**Files:**
- Modify: `poc/backend/src/DynamicModules.Domain/Approvals/ApprovalGraphLimits.cs`（`MaxClaimAttempts = 5`、`ClaimBackoffMs = 20`）
- Modify: `poc/backend/src/DynamicModules.Domain/Repositories/Dynamic/IApprovalRepositories.cs`、`poc/backend/src/DynamicModules.Persistence/Repositories/Dynamic/ApprovalRepositories.cs`（`GetRowVersionAsync(instanceId)`：`AsNoTracking` 只讀版本；`ReloadTrackedAsync(instanceId)`：reload ChangeTracker 中這個實例的全部 `ApprovalInstance`、`ApprovalTask`、`ApprovalStageRun`，J16 第 1 點，修訂 2 定案）
- Modify: `ApprovalActionHandlers.cs`（`LoadAsync` 加 `ClaimScope Scope`（`Task`／`Instance`），graph 依 J16；各 handler 傳對應範圍；graph 模式下 pending 判斷移到處理權與 reload 之後（J17 第 2 點，修訂 2，審查 M3）；新增 `ApprovalActionSupport.LateConflict(inst, me, runs)`，graph 的 `LoadAsync` pending 判斷與同意、退回、轉交、改派的 `me.Status != Open`（114、289、400、468）改呼叫它（J17 第 3 點）；處理權後 reload，再判斷任務 open、一人一票；`ApproveAsync` 在處理權後、寫入前再算一次加簽總數（兩種形狀）；Task 0 列出的轉交、改派狀態檢查一併移到處理權後）
- Modify: `ApprovalMessages.cs`、`poc/backend/src/DynamicModules.Application/Common/MessageText.cs`（`ApprovalBusy`「單據正忙，請稍後再試」、`ApprovalInstanceChanged`「單據已有變動，請重新整理」、`ApprovalBranchCompleted`「其他分支已完成，不需處理」、`ApprovalInstanceReturned`「單據已退回」，四語）
- Modify: `poc/backend/tests/DynamicModules.WebAPITests/TestApp.cs`（`internal Action<IServiceCollection>? ExtraServices`，`ConfigureServices` 末尾呼叫；預設 null，既有測試不受影響）
- Test: `poc/backend/tests/DynamicModules.ApplicationTests/Dynamic/Approvals/ApprovalGraphConcurrencyTests.cs`（新，含 `FlakyClaimRepository`，J18）、`poc/backend/tests/DynamicModules.WebAPITests/Dynamic/ApprovalGraphConcurrencyApiTests.cs`（新，`TestApp(fileDatabase: true)` 並以 `ExtraServices` 開擋門，graph 版本由 `TestApp.Services` 開 scope 直接寫，`[Trait("Category", "Postgres")]`）

**規則：**
1. sequence：`LoadAsync` 完全照舊（pending 判斷在換 RowVersion 之前、請求版本條件更新、`StateChanged`／`StageHandled`），各 handler 的 `me.Status != Open → StageHandled` 不變；唯一新增的是處理權後再算加簽總數（結果必然與載入前相同，J16）。
2. graph＋任務層：處理權之前不判斷 pending；讀目前版本 → 條件更新；失敗等退避再讀再試，用完 409 `ApprovalBusy`；成功後 `ReloadTrackedAsync`，再載入節點實例與任務；之後依序判斷：實例 pending（不是 → `LateConflict`）、J16 第 2 點的每一項（任務不是 open → `LateConflict`）。
3. graph＋實例層：請求版本條件更新；失敗 → reload 實例，不是 pending → `LateConflict`，仍是 pending → 409 `ApprovalInstanceChanged`，不重試；成功後同第 2 點 reload 與判斷。
4. 處理權成功後才回晚到的 409：`TransactionBehavior` 整筆回滾，換掉的 RowVersion 一起還原。回應的 `InstanceRowVersion` 照舊回新值。

**測試（ApplicationTests，可控替身）：**
- `BranchApprove_StaleRequestRowVersion_StillSucceeds`（法務同意後，財務用舊版本同意 → 200）。
- `Claim_FailsTwice_RetriesAndSucceeds`、`Claim_FailsFiveTimes_409Busy_NothingWritten`。
- `Claim_RetryAfterConcurrentCommit_SeesFreshTaskStatus`（裝飾器在第一次回 false 前，以同一個 `DbContext` 的 `ExecuteUpdateAsync` 把或簽另一人的任務改 approved、自己的任務改 closed → 重試後 409 `StageHandled`，不是誤判成功）。
- `Claim_RetryAfterInstanceRejected_409InstanceReturned`（同法注入實例 `rejected`）。
- `Claim_RetryReloadsAllTrackedRuns`（修訂 2，審查 S4：先前的請求已把另一條分支的節點實例載入共用 `DbContext`，裝飾器以 `ExecuteUpdate` 把它改 `closed`；重試後 handler 看到的是新值）。
- `AddSign_TwoBranchesAtLimitMinusOne_SecondGets400`（上限 5、已有 4：第一個分支加簽成功；第二個請求在載入前看到 4，裝飾器讓它先失敗一次、期間以同一個 `DbContext` 的 `Database.ExecuteSqlAsync` 插入一筆 `add-sign` 歷程（修訂 2，審查 S5：總數算的是歷程筆數，`ExecuteUpdate` 加不了列）→ 處理權後重算為 5 → 400 `AddSignLimit`，不寫入）。
- `Transfer_StateCheckAfterClaim`（Task 0 列出的每一項各一個，處理權前後狀態不同時以處理權後為準）。
- 晚到訊息（J17、U13，修訂 2 補齊，審查 M3）：`LateApprove_BranchCancelled_409BranchCompleted`（任一到齊時財務先核准，法務依序再按同意：handler 的 `me.Status` 檢查走 `LateConflict`）、`LateApprove_InstanceRejectedByOtherBranch_409InstanceReturned`（全部到齊，法務退回發起人後財務按同意：`LoadAsync` 處理權前不再先回 `StageHandled`）、`LateTransfer_BranchCancelled_409BranchCompleted`、`LateReject_InstanceRejectedByOtherBranch_409InstanceReturned`、`LateWithdraw_InstanceRejected_StaleRowVersion_409InstanceReturned`（實例層：版本不符但實例已退回 → 「單據已退回」，不是 `InstanceChanged`）、`LateApprove_InstanceApproved_409StageHandled`（其他非 pending 狀態照舊）。
- `Sequence_LateApprove_AfterReject_StillStageHandled`、`Sequence_LateWithdraw_AfterReject_StillStageHandled`（J17 第 1 點：sequence 不套用）。
- `Withdraw_AfterBranchApproved_StaleRowVersion_409InstanceChanged`、`Withdraw_FreshRowVersion_Ok`、`Cancel_StaleRowVersion_409InstanceChanged`。
- `Sequence_StaleRowVersion_StillStateChanged`、`Sequence_AddSignAtLimit_Still400BeforeLoad`（第一版行為不變）。

**測試（WebAPI，真並行；SQLite 上由 WriteGate 排隊，驗排隊後的結果）：**
- `TwoBranchesApproveAtOnce_BothSucceed`（兩條分支同時同意都 200，沒有 409）。
- `TwoBranchesReachAllJoinAtOnce_AdvancesExactlyOnce`（匯合後的節點恰好一列、只有一組任務）。
- `OrSignTwoApproversAtOnce_OneSucceeds_Other409StageHandled`。
- `HolderAndDelegateApproveAtOnce_OneSucceeds_Other409`（代理與本人競爭）。
- `AnyJoinTwoBranchesAtOnce_OneArrival_OtherBranchCancelled`。
- `TwoBranchesAddSignAtOnce_TotalNeverExceedsLimit`。

**驗收：** `sh $TL dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~ApprovalGraphConcurrency|FullyQualifiedName~ApprovalAction|FullyQualifiedName~ApprovalAddSign|FullyQualifiedName~ApprovalTransfer|FullyQualifiedName~ApprovalManagerAction|FullyQualifiedName~MessageText"`（fg2g1 已合併時加 `|FullyQualifiedName~ApprovalGraphEquivalence`）；`sh $TL dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~ApprovalGraphConcurrencyApi|FullyQualifiedName~ApprovalApi"`。PostgreSQL：未驗證（`TestApp` 仍是 SQLite；pg0 補 provider 切換後跑同一批 `Postgres` trait 測試）。

- [ ] 寫失敗測試 → 實作 → 通過 → commit `feat(approval): 流程圖實例的動作以實例為單位序列化`
- [ ] fg2f 審查、合併（主控）

---

## fg2g1：等價性

### Task 12：等價性測試（§14）

**Files:**
- Test: `poc/backend/tests/DynamicModules.ApplicationTests/Dynamic/Approvals/ApprovalGraphEquivalenceTests.cs`（新；`OpenGate`；`[Theory]` 參數 `shape`：`sequence` 用 `SeedFlow`，`graph` 用 `SeedGraphFlow(FromSequence(d))`；比較口徑 J23，正規化與比較函式寫在同一檔內；通知以 `SourceType = "approval"`、`SourceId` 屬於本情境的實例篩選，含重來建的新實例，修訂 2，審查 S6）

**情境**（取自簽核設計 §17 的必測與既有測試的劇本）：
- `ThreeStages_MiddleSkipped_ApproveAll`、`AllStagesSkipped_ApprovedAtSubmit`、`OnEmptySkip`。
- `ReturnPrevious_ThenApprove`、`ReturnPrevious_ToAddSign_Requeued`、`RejectToInitiator_RejectToState`、`Withdraw_BeforeAnyAction`、`Cancel_ByManager`。
- `AddSign_Nested`、`AddSign_TwoSiblingsSameStage`（兩筆加簽 `StageCode` 都是 null，靠正規化編號區分）、`Transfer_ThenApprove`、`Reassign_ThenApprove`、`Delegated_ApproveByDelegate`、`Delegate_NotApplied_InitiatorIsDelegate`。
- `Reroute_SkipConditionReopened`、`Reroute_NoFlowMatches_Fallback`。
- `AllMode_TwoSeats`、`FallbackApprovers_Used`。
- `Overdue_ReminderNotice`（逾期提醒的通知相同；若需要時間推進，用測試的假時鐘）。

**驗收：** `sh $TL dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~ApprovalGraphEquivalence"`。

- [ ] 寫測試（這一條預期一開始就通過；逐案先把 graph 一側故意改壞一次確認會紅，比較口徑的每一欄至少有一案證明會抓到差異，再還原）→ commit `test(approval): sequence 與轉換後流程圖的等價性`
- [ ] fg2g1 審查、合併（主控）

---

## fg2g2：開放 API 與收尾

### Task 11：API 收 graph、刪擋門與開關、U2–U4

**Files:**
- Modify: `ApprovalDefinitionValidator.Graph.cs`（刪 `x.Err("shape", ApprovalMessages.GraphNotAccepted)`；通知節點加暫時錯誤 `NotifyNodeNotYet`「通知節點尚未開放」，鍵 `nodes.{id}`，註明 fg5 刪，U2）
- Modify: `ApprovalFlowHandlers.cs`（`ApprovalFlowEditor.ValidateAsync`：目前版本是 graph、新定義是 sequence → 400 鍵 `definition.shape`，U4）
- Modify: `ApprovalMessages.cs`（刪 `GraphNotAccepted`、`GraphNotSupported`；加 `NotifyNodeNotYet`、`GraphCannotRevert`）
- Delete: `ApprovalShapeGate.cs`（`ApprovalGraphMode` 從 fg2a2 起就是獨立檔 `ApprovalGraphMode.cs`，不用搬，修訂 2）；刪四個呼叫點與 DI 註冊；`ApprovalGraphTestKit.OpenGate` 與各測試類的呼叫一併刪除
- Delete: `poc/backend/tests/DynamicModules.ApplicationTests/Dynamic/Approvals/ApprovalShapeGateTests.cs`、`ApprovalGraphGateTests.cs`；還有意義的案例依 J7 對照表改寫搬家：`Submit_MatchedGraphFlow_409_NothingWritten` → `ApprovalGraphSubmitTests.Submit_GraphWithoutNodes_409_GraphRuntimeInvalid_NothingWritten`；`BoundGraphInstance_RejectToInitiator_409_Untouched` → `ApprovalGraphActionTests.BrokenGraph_RejectToInitiator_Ok`（已在 Task 6）；`BoundGraphInstance_ApproveRecheckSelectsSequence_409_Untouched` → `ApprovalGraphApproveTests.Approve_BrokenBoundGraph_409_Alert_NothingWritten`（已在 Task 5）；`BoundGraphInstance_OtherActions_409_Untouched` → 加簽 409、轉交與改派成功（已在 Task 7）；`Approve_RecheckSelectsGraphFlow_409_Rollback` → `ApprovalGraphApproveTests.Approve_RecheckSelectsGraphFlow_ReroutesToGraphInstance`（新增）；`Preview_MatchedGraphFlow_409` → `ApprovalGraphPreviewerTests.Preview_GraphWithoutNodes_409_PreviewInvalid`（新增，U12）；`Submit_GraphFlowInactive_NotBlocking`、`Submit_GraphFlowConditionNotMet_NotBlocking` 失去意義，刪除並在修訂紀錄說明
- Modify: `poc/backend/src/DynamicModules.Application/Dynamic/AppService/AppApprovalFlowCodec.cs`、`poc/backend/src/DynamicModules.Application/Dynamic/AppService/Data/AppResponses.cs`（U3：graph 流程加匯出細節 `ExportFlowGraph`）
- Test: `poc/backend/tests/DynamicModules.ApplicationTests/Dynamic/Approvals/ApprovalGraphFlowApiTests.cs`（新，Application 層）、`poc/backend/tests/DynamicModules.WebAPITests/Dynamic/ApprovalFlowsApiTests.cs`、`poc/backend/tests/DynamicModules.WebAPITests/Dynamic/AppFormat8Tests.cs`、fg1d 的 `ApprovalGraphDefinitionValidatorTests`（「只剩 `shape` 錯誤」的斷言改成「沒有錯誤」）；`ApprovalGraphConcurrencyApiTests` 刪掉 `ExtraServices` 開擋門那行（`TestApp.ExtraServices` 掛點保留，pg0 可用）

**測試：**
- `Create_Graph_201_VersionOne`、`Update_Graph_UsedVersion_NewVersion`、`Update_GraphToSequence_400`（U4）、`Toggle_GraphFlow_SameDefinition_Ok`。
- `Create_GraphWithNotifyNode_400`（U2）。
- `DraftPreview_Graph_PathReturned`（`POST approval-flows/preview` 收 graph）。
- WebAPI：`Create_Graph_201_AndSubmitApprove_EndToEnd`、`Create_GraphInvalid_400_NodeKeys`（`definition.nodes.*`、`definition.edges.*`）、`AppExport_WithGraphFlow_409`（U3）、`RecordApproval_GraphInstance_StagesHaveCloseReason`（API 回應含 fg2d 的欄位）。
- 回歸：`ApprovalDefinitionValidatorTests`、`ApprovalFlowDefinitionTests`、`ApprovalFlowDesignerTests`、`AppApprovalFlowCodecTests` 不改斷言通過；所有 `ApprovalGraph*` 測試類在刪掉開關後不改斷言通過（證明它們不依賴開關以外的測試替身）。

**驗收：** `sh $TL dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~ApprovalGraph|FullyQualifiedName~ApprovalDefinitionValidator|FullyQualifiedName~ApprovalFlowDefinition|FullyQualifiedName~ApprovalFlowDesigner|FullyQualifiedName~AppApprovalFlowCodec"`；`sh $TL dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~ApprovalFlowsApi|FullyQualifiedName~AppFormat8|FullyQualifiedName~ApprovalGraphConcurrencyApi"`；`dotnet build`；grep `ApprovalShapeGate|GraphNotAccepted|GraphNotSupported|OpenGate` 在 `src/` 與 `tests/` 為零。

- [ ] 寫失敗測試 → 實作 → 通過 → commit `feat(approval): API 接受流程圖存檔與試算，開放流程圖執行`

### Task 13：文件總收與上線

**Files:**
- Modify: `docs/superpowers/specs/2026-11-13-approval-flowgraph-design.md`：§19.1 加 fg2 的 U1–U13（編號接 U22 起，標「主控裁決（待使用者確認）」；U4 註明是產品限制）；§19.2 加 J（接 J28 起，至少 J1、J2、J4、J7、J10、J11、J12、J13、J16、J17、J19、J20、J22、J23、J26、J27）；§8.3、§8.10 註明到達列一條分支一列、以 `ArmOf` 識別（J4、J13）；§8.9 註明共同必經後繼與任一到齊的預覽（U9、U10）；§8.13 註明重試常數與處理權內的檢查；§12.4 註明「未支援的節點型別不適用不擋流程」（U2）；§13 第 7 點補回退下限（Global Constraints）；§15 可寫死清單補上本計畫新增項；§17 fg2 列註明切成 fg2a1–fg2g2。
- Modify: `docs/superpowers/specs/2026-11-11-policy-settings-design.md` §10（B27 改寫為「流程形狀只能是 sequence 或 graph，graph 須符合結構規則」）。
- Modify: `docs/superpowers/specs/2026-11-10-approval-workflow-design.md` §4.3（關卡實例狀態補 `queued`、`passed`、`waiting`，`CloseReason`）、§8.4（加註 graph 實例的序列化、自動重試、撤回與終止仍比對版本，指向流程圖設計 §8.13）。
- Modify: `docs/roadmap.md`（fg2 列，已完成附日期與 commit）。
- Modify: `docs/feature.md`（「流程圖執行引擎（段 fg2）」改 `[x]` 附日期；跑 `python3 ~/.claude/plugins/cache/plugharbor/lifecycle/2.6.0/scripts/feature_checklist.py docs/feature.md --write` 與 `--check`，exit 0）。
- Modify: `docs/todo.md`（實作中發現、不在本段處理的事；沒有就不動）。

- [ ] 文件 → commit `docs(approval): fg2 收尾：流程圖設計寫回、B27、簽核設計加註、路線與功能清單`
- [ ] fg2g2 審查、合併（主控）；部署照 `formwright-ship`，部署回報寫明回退下限三句（Global Constraints）。
- [ ] 本機四寬度走查（fg2d、fg2e 改了簽核元件，fg2g2 起正式環境才看得到 graph）：用 API 建一條「主管 → 法務、財務任一到齊 → 結束」的流程，在 390、820、1366、1920 四種寬度看送出前預覽（任一到齊註記）、單據頁時間軸（財務核准後法務列的「其他分支已完成，不需處理」）、歷程的分支取消說明，切 en-US 再看一次；頁面不橫向捲動。Demo 冒煙另確認既有 sequence 簽核的送出、同意、退回、撤回正常。

---

## 驗收總表

| 項目 | 指令或操作 | 子段 |
|---|---|---|
| Domain 單元 | DomainTests `--filter FullyQualifiedName~ApprovalGraph|FullyQualifiedName~ApprovalPath` | fg2a1、fg2a2、fg2e |
| 正式環境擋門仍在 | `ApprovalShapeGateTests` 不改一行通過、`Gate_DefaultRegistration_IsClosed` | fg2a2–fg2g1 每段 |
| Application 整合 | 各 Task 的窄範圍 filter（經測試鎖） | fg2a2–fg2g2 |
| sequence 回歸 | 各 Task 列的既有測試類不改斷言 | fg2a2–fg2g2 |
| 唯一完成者 | `*_CompleteOnce` 三條；`CompleteAsync` 為 private | fg2b、fg2c |
| 等價性 | `ApprovalGraphEquivalenceTests` | fg2g1 |
| 並發（SQLite） | `ApprovalGraphConcurrencyTests`、`ApprovalGraphConcurrencyApiTests` | fg2f |
| 並發（PostgreSQL） | 未驗證；pg0 補 `TestApp` 的 provider 切換後跑 `Postgres` trait 的同一批測試 | fg2f、fg2g2 |
| WebAPI | `--filter FullyQualifiedName~ApprovalFlowsApi|FullyQualifiedName~ApprovalApi|FullyQualifiedName~AppFormat8|FullyQualifiedName~ApprovalGraphConcurrencyApi` | fg2f、fg2g2 |
| 後端訊息四語 | `MessageTextTests` | fg2a2、fg2b、fg2f |
| 前端元件與 i18n | `bunx vitest run src/presentation/dynamic/approval src/i18n --maxWorkers=2`；typecheck | fg2d、fg2e |
| build | `dotnet build`（警告即錯誤） | 每段 |
| 擋門清除 | grep `ApprovalShapeGate|GraphNotAccepted|GraphNotSupported|OpenGate` 在 `src/`、`tests/` 為零 | fg2g2 |
| 無業務名詞 | `--filter FullyQualifiedName~NoBusinessWords` | 每段 |
| 合併前 gate | GitHub Actions，`bash scripts/unattended/ci-wait.sh <sha>` | 每段 |
| 外洩掃描 | `bash scripts/unattended/leak-scan.sh` | 每段推分支前 |
| 宗旨檢查 | `formwright-purpose-check` 逐項（下節），結果寫交付說明 | 每段 |
| 本機四寬度走查 | 390、820、1366、1920：送出前預覽、單據頁時間軸、歷程（含 en-US） | fg2g2（fg2d、fg2e 的畫面在正式環境要到 fg2g2 才看得到） |
| 部署回報 | 寫明回退下限三句 | fg2g2 |

本機測試、build、gate、commit、合併、上線各自回報，沒跑到的標「未驗證」。

---

## 宗旨檢查（formwright-purpose-check，計畫階段）

1. **機制還是政策？** 通過。推進、匯合、取消、序列化是簽核機制；重試次數與退避是技術限制（已列清單）；任一到齊取消其他分支、整張退回是使用者決定；沒有新增任何業務數字。
2. **寫死掃描：** 實作後跑 skill 裡的 diff grep。預期命中 `ApprovalGraphLimits.MaxClaimAttempts`、`ClaimBackoffMs`（技術限制）、`ApprovalGraphMode.SupportedKinds`（能力守門）、擋門開關（守衛，fg2g2 刪）與 U2、U3、U4 的暫時或完整性規則（已列清單）；不應命中任何人數、天數、金額。
3. **單一來源：** 通過。graph 判斷只有 `ApprovalGraphMode.Applies`；完成只有 `AdvanceAsync`；選線只有 `ApprovalGraphRuntime.Choose`（推進、重新比對、預覽共用）；到達分支只有 `ArmOf`（推進、取消、路線比較共用）；上一關只有 `ApprovalGraphPath.Previous`（handler 與檢視共用）；撤回資格只有 `CanWithdraw`；結構再驗只有 `Build`，型別清單只有 `ApprovalGraphMode.SupportedKinds`（送出、預覽、`LoadAsync` 共用）；條件求值只有 `ApprovalGraphMode.Eval`（推進、重新比對、預覽共用，J27）；晚到訊息只有 `LateConflict`（J17）。
4. **兩個應用程式：** 通過。並行會簽、分級路線是請假、採購、合約、製程變更共用的機制（流程圖設計 §5）。
5. **空系統：** 通過。引擎只依賴狀態機、組織、群組；不需要任何應用程式。
6. **套件只用定義組成：** 通過。fg2 不改套件格式；graph 流程匯出先擋（U3），fg6 升版。
7. **ERP 對照：** 通過，流程圖設計 §5 已有；fg2 不新增主檔或單據。
8. **fail closed、驗證不只做在前端、沒有執行期 DDL：** 通過（修訂 1 補上審查指出的兩處）。正式環境的 graph 入口到 fg2g2 才開，回退到 fg2a1–fg2g1 任一段都與 fg1 相同；不支援的節點型別由能力守門 409，不再假裝通過（U2）；結構與條件無法判斷都擋並通知，不讀圖的動作照 U11 放行；API 存檔走後端完整驗證；不加 migration。

---

## 與後續段的交接

- **fg3**：草稿、發布、捨棄草稿端點與 `DraftJson`（U1）；發布回應帶 W1–W3；`GET approval-flows/{key}` 回傳轉換後的 graph；設計器試算疊圖用 `ApprovalPreviewResponse.Path` 與 `PreviewStage.AnyGroup`（J19）；移除舊對話框後，U4 的「不能改回 sequence」保留當防線；前端 `ApprovalFlow` 型別加 graph。
- **fg4**：進度圖讀節點實例的 `NodeId`、`NodeKind`、`BranchKey`、`InEdgeId`、`CloseReason`；匯合的每條進線各有一列到達紀錄，到達分支用 `ArmOf` 判斷（J4、J13）；加簽列的主節點是 `NodeId`（J10）；檢視的 `stages` 只含簽核節點（U6），`graph` 另回全部節點實例；簽核摘要與簽核中心「我送出的」「全部」改成 U9 文案（U5）。時間軸的「其他分支已完成，不需處理」已在 fg2d 做好，進度圖沿用同一個 i18n 鍵。
- **fg5**：`ApprovalGraphMode.SupportedKinds` 加 `notify`、刪 U2 的存檔暫時錯誤；引擎加 `notify` 分支（解析收件人、套範本、寫通知與 `DataJson`、寫 `notify` 歷程）；通知同樣走 J6 的延後寫入。fg5 的部署回報要寫明：回退到 fg2g2–fg4 時，含通知節點的進行中單據會停住（U2）。
- **fg6**：刪 U3 的匯出擋門，套件格式升版；批次轉換用 `FromSequence`；設計 §20.1 其餘文件項（J21）。
- **pg0**：`TestApp` 加上 PostgreSQL provider 切換；跑 fg2f、fg2g2 的 `Postgres` trait 測試；`TryBumpRowVersionAsync` 在 PostgreSQL 的 READ COMMITTED 下會等前一筆提交再回 0 列，重試邏輯依賴這個行為，pg0 驗收時要確認。

---

## 與設計文件矛盾或需要讀成別的意思之處

1. **草稿端點的歸屬**：§17 fg2「API 接受 graph 存檔」與 fg3「草稿與發布」都沒寫後端草稿端點在哪段；依 §13 第 2 點與 §10.8 的工作項分配讀成 fg3（U1）。
2. **匯合的「唯一一列 waiting」**：§8.3、§8.10 寫一列 waiting 記下已到達的分支；本計畫每條到達的分支一列（J4），因為 §11.1 進度圖要每條走過的線都有 `InEdgeId`，而節點實例沒有欄位存「已到達的分支清單」。語意相同：到齊判斷看這個匯合的到達列、以 `ArmOf` 去重。（修訂 2，審查 S8）§8.10 另說「退回上一關讓分支段重跑時，該分支已到達的紀錄撤回」：依 §8.4 與 J12，分支到達匯合後這條分支就沒有進行中的節點可以退回（匯合後第一個節點也沒有上一關），這種情況不會發生；實作**不寫**撤回到達紀錄的程式，`ReturnPrevious_FirstAfterJoin_400` 與 `ReturnPrevious_AfterInnerJoinInOuterBranch_400` 守住前提。
3. **B27 與簽核設計的寫回時點**：§15 末段寫 B27 由 fg2 改寫，§17 fg6 又列「政策文件 B27 與簽核設計的對應段落」；本計畫 fg2g2 改 B27 與簽核設計 §4.3、§8.4（行為從 fg2 開始），其他 §20.1 項目留 fg6（J21）。
4. **通知節點**：§17 fg2 的「推進」包含所有節點型別，fg5 又列通知節點的「推進」；本計畫 fg2 不推進通知節點：存檔拒收、執行期由能力守門 409（U2）。§12.4「不擋流程」讀成「已支援的通知出問題時不擋」，不涵蓋未支援的節點型別。
5. **任務層動作的範圍**：§8.13 列「同意、退回、轉交、後加簽」，沒有改派；依 a3 計畫 791 行把改派歸任務層（J16）。
6. **`StageCode` 寫節點代號**：§7.6 只說簽核節點；本計畫所有 graph 節點實例都寫，保留「`StageCode` 為 null 就是加簽」的既有不變式（J10）。
7. **預覽的「依條件決定」與任一到齊**：§8.9 只說之後的路徑標依條件決定；本計畫從共同必經後繼接著列（U9、J26），任一到齊依 U10 呈現。
8. **分支取消的名稱**：§8.3 的「分支「{名稱}」」沒定義分支名稱來源（U8）。
9. **設計 U5 的畫面文字**：「簽核中心與詳情頁對已關閉任務顯示」——詳情頁在 fg2d 的時間軸顯示；簽核中心沿用第一版「已關閉的任務不列」，畫面過期時按動作回 409「其他分支已完成，不需處理」（U6、J17）。初版把這段延到 fg4，修訂 1 撤回。
10. **併發測試兩種資料庫都跑**：§14 要求從 fg2 起就有；pg0 未合併，`TestApp` 寫死 SQLite，PostgreSQL 版只標 trait、未驗證。
11. **§13 第 7 點的回退範圍**：設計只寫「回退只支援到 fg2 為止」；fg2 切成九段後，本計畫明定能執行 graph 的回退下限是 fg2g2，fg1a–fg2g1 對 graph 一律 409（J1）。

## 讀程式碼時發現的問題（不在設計文件裡）

1. **退回的關閉範圍是整個實例**：`RejectTaskHandler.Reject` 呼叫 `CloseOpen(tasks)`、`CloseQueued(runs)`，退回發起人時只把目前關卡改 `closed`；直線流程沒問題，並行時會關錯或漏關（Review Focus 1、2，fg2c 修）。目前受擋門保護，不是已在正式環境發生的 bug。
2. **同流程同版本的 graph 沒有路線比較**：`ApprovalRouter.Recheck` 在同流程同版本時只比 `Stages`，graph 會判定「沒變」；換流程、版本或切備援仍會判定 Changed（`ApprovalRouter.cs:57-67`）。現在由擋門擋住，fg2c 修。
3. **異常通知會遺失**：`ApprovalAlert` 只記實例 id，送出時建的實例隨主交易回滾後，`WriteApprovalAlertHandler` 回 NotFound；`ApprovalAlertBehavior` 只 catch 例外、不看失敗結果，所以也沒有 log。sequence 理論上也會發生（送出時所有關卡跳過而轉移再驗失敗），只是少見（J20，fg2b 修）。
4. **EF 追蹤與重試**：`GetTaskAsync`、`GetAsync` 是追蹤查詢，handler 在取得處理權之前就讀過；條件更新只同步 RowVersion。現在沒有自動重試，所以還不會出事；fg2f 加了重試後，重試成功時 `ListTasksAsync` 回傳同一個已追蹤物件、`Status` 不會更新（Review Focus 5，fg2f 一併修）。
5. **SQLite 的寫入整筆序列化**：`WriteGate`＋`BEGIN IMMEDIATE` 讓同一資料庫的寫入請求不會交錯，自動重試的程式路徑在 SQLite 測不到；外層交易持有寫入鎖時，從另一條連線寫資料會卡住，所以只能用同一個 `DbContext` 的 `ExecuteUpdate` 注入（J18）。
6. **舊對話框存 graph 流程會改成 sequence**：`buildFlowInput` 寫死 `shape: 'sequence'`（U4，fg2g2 擋）。
7. **匯出讀 `current.Stages`**：graph 流程會匯出成沒有關卡的流程（U3，fg2g2 擋）。
8. **`StageCode` 為 null 被當成加簽**：`RestartStageAsync`、`ApprovalPath.NextStageIndex`、檢視都這樣判斷；graph 的瞬間節點若不寫 `StageCode` 會被誤判（J10）。
9. **`StartAsync` 立即寫通知、把 `CurrentStageRunId` 設成剛啟動的那一關**：並行時兩者都不對（J6、J15）。
10. **加簽的 `Seq` 插入假設目前關卡之後只有排隊關卡**：並行時其他分支的節點實例也在後面（J11）。
11. **`MessageText` 的檔頭說「其餘訊息仍是呼叫端的中文字面值」**，但簽核訊息已多數搬入；新訊息照搬入的做法寫四語。
12. **完成由三個呼叫端各自負責**：送出、同意、重來都在 `ActivateNextAsync` 回 null 後自己呼叫 `CompleteAsync`；加入第二種推進方式就容易重複完成（Review Focus 9，J22，fg2b 修）。
13. **排隊加簽以整個實例挑最小 `Seq`**：`ActivateNextAsync` 第 45-48 行，並行時會啟動到別條分支的加簽（J11，fg2b 起 graph 不經過這段）。
14. **加簽總數在處理權之外檢查**：`AddSignTaskHandler` 第 261-264 行；sequence 有實例版本比對所以安全，graph 不比對版本後會超過上限（Review Focus 11，J16，fg2f 修）。
15. **前端直接顯示新歷程動作的 comment**：`ApprovalActionLog.vue:63` 只排除 reroute、fallback（U8，fg2d 修）。

## 修訂紀錄

- 2026-11-14：初版（計畫 subagent，基準 origin/main `9fec963f`；fg1d 未合併，依 worktree `dynamic-fields-design.fg1` 現況推定擋門位置與 U、J 編號）。Task 0 核對結果寫在這裡。
- 2026-11-14：修訂 1（依跨家族審查 `plan-review.md` 第 6 節 10 項必修；逐項處理見 `plan-rev1-notes.md`）。主要變動：擋門在正式環境關到 fg2g2、改成 DI 開關（J1）；`AdvanceAsync` 唯一完成者與單向依賴（J22）；`ArmOf` 識別到達分支與兩條不變式（J4、J13）；`LoadAsync` 結構再驗與動作對照（J7、U11）；處理權內的狀態檢查與加簽總數（J16）；SQLite 注入改用同一 `DbContext` 的 `ExecuteUpdate`（J18）；U9 改共同必經後繼（J26）、新增 U10 任一到齊預覽；U6、U8 的取消原因畫面與四語提前到 fg2d；U2 改為能力守門；J23 等價性口徑擴充；切段改為 fg2a1、fg2a2、fg2b–fg2f、fg2g1、fg2g2 九段。行號基準仍是 origin/main `9fec963f`。
- 2026-11-15：修訂 2（依 `plan-rev1-review.md` 新必修 M1–M3、建議 S1–S10，並對齊已實作的 fg2a1；逐項處理見 `plan-rev2-notes.md`）。主要變動：`ApprovalGraphMode` 改成 fg2a2 的獨立檔，`SupportedKinds`、條件求值轉接 `Eval`（新 J27）、`ApprovalGraphRuntimeInvalid`／`ApprovalGraphPreviewInvalid` 兩則訊息都在 fg2a2 定案，fg2e 只依賴 fg2a2（M1）；`RuntimeInvalid` 加選用 `message` 參數、圖引擎建構函式補 `IUnitOfWork`、J7 對照表逐列寫訊息並要求測試斷言訊息鍵（M2）；J17 改寫為只適用 graph、`LoadAsync` 的 pending 判斷移到處理權之後、共用 `LateConflict`（M3）；新增待確認 U12（預覽遇壞圖的訊息、不通知）、U13（晚到動作的訊息優先序）；採納 S1–S10（S6 改用 `SourceType`／`SourceId` 篩通知）。對齊 fg2a1：路徑改成 `poc/backend/src/…`，Task 2 補 `ApprovalPath.cs`，四處偏離寫進 fg2a1 段，J3、J4、J7 改成與實作一致。基準改為 origin/main `916268f1`（已含 fg1d），fg2b、fg2g2 不再依賴 fg1d。fg2a2 約 500 → 610 行，總計約 6030 行。

## 修訂 2 點檢建議（plan-rev2-review.md，主控 11-15 全部採納；各段開工時照做）
- S1（fg2f）：graph 判定要先取 def，但 sequence 實例維持「先判 pending、再取 def」的原順序，避免「已結束且快照找不到版本」從 StageHandled 變成 FlowVersionMissing；骨架見點檢檔。
- S2（fg2f）：改派（ApprovalActionHandlers.cs:466）的解構可以改成保留 runs，供 LateConflict 使用；不違反「6 處解構不動」的原則。
- S3（fg2f）：LateApprove_InstanceApproved_409StageHandled 寫明情境；補 LateApprove_AnyJoinReachedEnd_409BranchCompleted（U13 ② 從 LoadAsync 出口回應）。
- S4（fg2b）：J7 表補一列：引擎內部不變式違反（走到結束還有進行中的執行線）→ RuntimeInvalid 帶 GraphRuntimeInvalid 訊息。
- S5（fg2f）：正式環境已結束的 graph 實例，晚到動作從 StageHandled 變成擋門的 GraphNotSupported，屬預期差異（仍 409、不寫入）；正式環境在 fg2g2 前不會有 graph 實例。
- S6（排程）：fg2d 開工前 fg2e 要已合併。
- S7（措辭）：L203 行號 35 → 34；條件求值除了 Eval，推進的略過條件沿用 ApprovalRouter.Holds。
