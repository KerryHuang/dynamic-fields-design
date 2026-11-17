# 任務報表資料來源 kr1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 任務成為報表引擎的資料來源：報表定義帶 `dataset: tasks|taskEvents` 就能用既有的報表 API 預覽、存檔、執行、下鑽、匯出、放儀表板，數字照觀看者的任務可見範圍計算，看不到記錄時「所屬模組」「任務規則」是 `restricted`；同時新增三種一般報表也能用的量值能力：`countIf`、`median`、`sum`／`avg`／`median` 帶條件。驗收：用 API 可跑任務報表。

**Architecture:** 量值能力（kr1a）加在 `ReportCalculator`（記憶體路徑）與 `ReportQueryBuilder`（SQL 路徑，`countIf` 與帶條件的 `sum`／`avg` 用 `CASE WHEN`，`median` 退回記憶體路徑）。資料來源（kr1b）不開公開介面：`ReportRunner.RunAsync` 拆成「載入」與「共用尾段」，載入有兩個私有實作 `LoadModuleAsync`（現行步驟 1–6 原封搬移）與 `LoadTasksAsync`（新檔 `ReportRunner.Tasks.cs`），兩者回同一個 `SourceLoad`。任務資料來源以**虛擬模組**呈現：`TaskReportFields.Build` 產生不入庫的 `Module`（負數欄位 Id、選項是狀態、部門、模組、規則），所以 `ReportRules.Check`、條件正規化、標籤、排序、`Matches`、`ReportMasking` 都原封沿用；任務列由 `TaskReportSource` 用 kb1 的 `TaskVisibility`（EF LINQ 單一條件）載入，記錄能不能看用 `TaskAccess.EvaluateManyAsync`（與看板同一個判斷）。kr1c 補 `taskEvents`、下鑽固定欄與 `TaskIds`、匯出、資料來源目錄端點與 WebAPI 驗收。

**Tech Stack:** .NET 8、EF Core（SQLite；任務載入只用 EF LINQ）、Dapper（既有 SQL 路徑）、MediatR、xUnit＋FluentAssertions、ClosedXML（匯出驗證）。不新增相依套件、沒有 migration、沒有前端改動。

**Spec:**
- `docs/superpowers/specs/2026-11-13-task-board-reports-gantt-design.md`：§2 反目標、§3、§4.3、§4.4、§7 全部、§8.2（部門篩選屬 kr2，不做）、§9、§12 報表一條、§14、§15、§17.3 T11–T16、T23、§18、§19「報表」、§20 kr1。U8 已裁決不擴充報表分享（沒有 `ReportShareTargets` migration）。
- `docs/superpowers/specs/2026-11-05-reports-design.md` §6.1（每列代表什麼）、§6.3（去重）、§6.6（restricted）、§7（權限與分享）。
- `docs/superpowers/specs/2026-11-10-report-db-aggregation-design.md` §3（總原則：證明不了相同就退回、縮小只用精確條件）、§5.4（條件語意）、§9（精確整數加總）。
- `docs/superpowers/specs/2026-11-12-postgres-provider-design.md` §6.3、§6.4（雙跑集分類）。
- 樣板：`ReportRunner*.cs`、`ReportCalculator`、`ReportRules`、`ReportModuleLoader`、`GetReportCatalogHandler`、`TaskVisibilityQuery`、`TaskAccess.EvaluateManyAsync`、`TaskBoardService`（批次評估卡片）、測試 `ReportRunnerTests`／`ReportRunnerSqlTests`／`ReportPermissionMatrixTests`／`ReportSqlEquivalenceTests`／`TaskVisibilityEquivalenceTests`／`ReportsApiTests`。
- repo 規範：`AGENTS.md`、`.claude/rules/backend.md`、`.claude/rules/docs.md`、`docs/ai/testing.md`、`.claude/skills/formwright-purpose-check/SKILL.md`。

**路徑慣例：** 後端相對 `backend/`，文件相對 repo 根目錄。worktree `../formwright.kr1`、分支 `feature/kr1`，基準 `origin/main` 7fb2fda2；三個子單元依序在同一分支各自 commit，kr1c 完成後才走 `formwright-ship`。行號以 7fb2fda2 為準，動手前重新核對。

---

## 代為決定的事

### 使用者行為類（實作照推薦；使用者推翻時改對應 Task）

> 審查（`plan-review.md` R4）指出 B1、B3 與設計字面衝突。**B1、B3 已由使用者在 2026-11-17 確認（內容不變）**，kr1 總收時寫回設計 §7.3、§7.5、§17.2。B3 的 `taskEvents` 下鑽欄（審查 Y7）與 B4 欄名（審查 Y13）是主控裁決、標「待使用者確認」；被推翻時改對應 Task（Y7：Task 4、9；B4：Task 2；皆不影響 Task 1、3 的計算與 SQL）。

**B1（使用者裁決，2026-11-17）取消的任務：「是否逾期」為否、「延遲天數」空白、「完成狀況」是「未完成」。**
- 推薦：是否逾期＝狀態是待辦或進行中、且到期日早於報表基準日；延遲天數只算完成的任務（完成日減到期日）與待辦／進行中的任務（基準日減到期日），取消的任務空白；完成狀況照設計把取消算「未完成」。
- 理由：設計 §7.3 字面「未完成且到期日早於基準日」把取消也算進去，會讓報表的逾期件數比看板「逾期」欄（只列未完成的待辦、進行中）多，正好踩反目標「報表與看板對同一批任務計數不一致」。
- 選錯代價：照字面時，負載報表的「逾期件數」含已取消的任務，主管會追一件已經不用做的事。
- 例子：「回覆恆川機械樣品問題」到期 11-01、11-03 被取消，基準日 11-04 → 是否逾期「否」、延遲天數空白、完成狀況「未完成」；同一件若還在待辦 → 是否逾期「是」、延遲天數 3。

**B2 空值分組的名稱：負責人「未指派」、負責人部門「（無部門）」、建立者「系統」、所屬模組與任務規則「（無）」；下鑽與匯出的格子也顯示同一個字。**
- 推薦：照設計 §7.3、§8.1、§9 的字；設計沒寫的「所屬模組」空值（個人任務）也用「（無）」，與任務規則一致。分組、明細、下鑽、匯出四處都用這些字，條件「為空」照樣成立（建立者「為空」＝規則產生）。
- 理由：「（空白）」在任務報表裡沒有意義，使用者要知道那一組是「沒人接」還是「系統產生」。
- 選錯代價：只改分組不改明細時，同一個任務在分組叫「未指派」、在下鑽列是空格。
- 例子：依負責人分組的報表有一組「未指派」3 件；下鑽那一組，負責人欄顯示「未指派」；依任務規則分組時，個人任務落在「（無）」。

**B3（使用者裁決，2026-11-17）下鑽固定列六欄：標題、狀態、負責人、到期日、完成時間、所屬模組；不給記錄標題，每列附任務 Id。**
- 推薦：任務資料來源的下鑽一律用這六欄（不看報表自己的明細欄；`taskEvents` 因為同一任務的「建立」「完成」兩列內容會完全相同，六欄改成「事件種類、事件日期、標題、狀態、負責人、所屬模組」，Y7，`taskEvents` 這一組待使用者確認），「所屬模組」看不到記錄時是「（無權限）」；回應多帶 `taskIds`（與列對齊），kr2 畫面點列開任務詳細，伺服器照常再驗可見性。
- 理由：設計 §7.5 寫下鑽列「相關記錄（§4.3 的顯示）」，但 §7.3 規定報表不提供記錄標題（見「矛盾」1）；用「所屬模組」守住「報表永遠拿不到記錄內容」，記錄標題在任務詳細裡照既有權限顯示。
- 選錯代價：下鑽給記錄標題時，報表就成了另一條拿記錄標題的路，之後要多一套遮蔽測試；不給時，使用者要點進任務才看到是哪筆單據。
- 例子：manager01 下鑽「業務一／逾期」→ 3 列，其中一列「所屬模組」是「（無權限）」（記錄在他的資料範圍外），點進去看得到任務，相關記錄顯示「相關記錄（無權限查看）」。

**B4（主控裁決，待使用者確認；採審查 Y13）新量值的欄名帶出條件值。** `median` 是「{欄位} 中位數」；`countIf` 條件是單一值的 `eq` 時為「{條件欄位}＝{值} 筆數」；帶條件的 `sum`／`avg`／`median` 為「{欄位} 平均（{條件欄位}＝{值}）」；條件不是單一值的 `eq`（`in`、`gte`、多值、使用者欄位等）退回「{條件欄位} 符合條件筆數」與原欄名後加「（符合條件）」。
- 推薦：值用觀看者語系的選項標籤（Enum、Boolean、Text 可轉；轉不出來就退回後備字樣），四語系寫在 `ReportText`。實作的可帶值型別比這裡寫的多：Integer、Decimal、Currency、Percent、Date 也可以（值本身就是可讀的字面）；日期時間欄位沒有 `eq` 運算子所以不會出現；選項已被刪除的 Enum 值轉不出標籤，退回後備字樣。
- 理由：量值沒有自訂名稱欄位（見「矛盾」6）；條件欄位觀看者一定看得到（看不到時整張已被擋下），把值放進欄名就能分開同一欄位的多個 `countIf`，內建負載報表的三個 `countIf` 不必等 kr3 的自訂名稱。
- 選錯代價：不帶值時，同一欄位的兩個 `countIf` 欄名相同，使用者分不出哪欄是待辦、哪欄是進行中。
- 例子：`countIf` 條件「狀態＝待辦」→「狀態＝待辦 筆數」；`avg(延遲天數)` 帶條件「完成狀況＝逾期完成」→「延遲天數 平均（完成狀況＝逾期完成）」；條件是 `Amount gte 100` → 「訂單金額 符合條件筆數」。

### 技術類（主控決定，可推翻）

- **J1 資料來源介面＝兩個私有載入方法。** `LoadModuleAsync`、`LoadTasksAsync` 回同一個 `SourceLoad(ReportResult? Done, SourceRows? Rows)`；不開 `IReportDataSource` 公開介面，因為 `Bound`、`Side`、`TimedCell`、`RunContext` 都是 `ReportRunner` 的私有型別，開介面就得把它們全部公開。第三種資料來源出現時再抽。
- **J2 虛擬模組。** `TaskReportFields.Build(dataset, departments, modules, rules)` 回不入庫的 `Module`（`Id = 0`、`Key = ""`、一個區塊），讓既有的驗證、正規化、標籤、排序、遮蔽不必分支。標題用 `FieldType.Textarea`：`ReportFieldEligibility.CanGroup` 已排除長文字，就是 T12「標題不能分組」。
- **J3 選項 Id。** 狀態＝`(long)TaskItemStatus + 1`（待辦 1、進行中 2、完成 3、取消 4）；來源：個人 1、記錄上建立 2、規則產生 3；完成狀況：準時完成 1、逾期完成 2、無到期日完成 3、未完成 4；事件種類：建立 1、完成 2；負責人部門、所屬模組、任務規則用實體 Id。選項鍵一律 `opt_{id}`（同 `EnumHandler`）。
- **J4 欄位代碼。** -1 至 -15 照設計 §7.3；-16（層級）、-17（開始日）保留給 st1、gt1，kr1 不建；`taskEvents` 的事件種類 -18、事件日期 -19（設計沒給代碼）。
- **J5 兩個小擴充。** `Bound` 加 `Restrictable`（-10、-11 為 true，讓 `KeyRank`／`GroupLabel` 認得 `__restricted`、`__missing`）；`RunContext` 加 `BlankLabels`（B2）。模組來源一律 `false`／空字典，輸出不變。
- **J6 restricted 判斷。** 用 `TaskAccess.EvaluateManyAsync` 的 `TaskPermissions.Record.Accessible`，與看板卡片同一個判斷（設計 §4.3「一律用同一個判斷」）；規則已刪除時「任務規則」是 `missing`。
- **J7 縮小載入。** 資料庫端只用兩種「證明精確」的條件：「狀態」的 `eq`／`in`、主要日期欄位（到期日、建立時間、完成時間；`taskEvents` 是建立或完成任一落在區間）的區間；載入後照舊重套全部條件。
- **J8 量值條件的來源。** 主模組欄位的量值不能帶子模組欄位的條件（存檔 400「量值條件不能用子模組欄位」）：主欄位每個主記錄只算一次，帶子條件時要挑哪一列沒有定義。條件只准出現在 `ratio`、`countIf`、`sum`、`avg`、`median`（其他種類存檔時丟掉條件，見 J19）。
- **J19 既有定義的行為改變（Y3 查證；kr1a 審查 R1、Y1、Y2 修正）。** 存檔驗證原本不擋 `sum`／`avg`／`min`／`max`／`countMain` 帶 `Condition`，記憶體路徑與 SQL 路徑都忽略它；設計器（`StepGroups.vue` 只有 `ratio` 會帶條件）、內建應用程式套件、Demo 報表都不會產生這種定義，只有直接呼叫 API 才存得出來，所以「既有資料受影響」的範圍是零到極少數。處理：
  - `sum`／`avg`／`median` 先前存的條件**從 kr1a 起開始生效**（使用者存條件的本意就是只算符合的列，沿用現有存檔、不做遷移）。
  - `min`／`max`／`countMain`／`countChild` **不支援條件：存檔時正規化，直接丟掉 `Condition`，不回 400**（`ReportRules.DropIgnoredConditions`，在結構與欄位檢查之前做，被丟掉的條件不再驗證）。原本的 400 會讓舊定義另存（`CopyReport` 重驗整份）與設計器重存（`designer-state.ts` 原樣保留 `condition`，畫面上卻看不到可刪的地方）失敗。**已存在、還沒重存的舊定義**執行時照舊忽略條件：記憶體路徑只對 `sum`／`avg`／`median`（以及 `ratio`、`countIf`）套用條件，`min`／`max` 與 SQL 路徑一致。
  - 主模組欄位的量值帶子模組欄位的條件（J8 新存檔 400）：舊定義執行時同樣忽略條件，不會用「第一筆子記錄」決定整筆主記錄算不算。
  - **出貨前跑一條唯讀查詢**（`formwright-ship` 前，Demo 與正式資料庫各一次；SQLite 語法，PostgreSQL 改用 `jsonb_array_elements`），結果寫進交付說明。筆數不為 0 時列出報表 Id 給使用者，這些報表的數字不會變（本來就忽略條件），只是下次存檔時條件會被丟掉：

    ```sql
    SELECT r.Id, r.Name, json_extract(m.value, '$.id') AS MeasureId, json_extract(m.value, '$.kind') AS Kind,
           CASE WHEN json_extract(m.value, '$.kind') IN ('min', 'max', 'countMain', 'countChild')
                THEN 'condition-dropped-on-save' ELSE 'main-field-with-child-condition' END AS Why
    FROM Dynamic_Report r, json_each(r.DefinitionJson, '$.measures') m
    WHERE json_type(m.value, '$.condition') = 'object'
      AND (json_extract(m.value, '$.kind') NOT IN ('ratio', 'sum', 'avg', 'median', 'countIf')
        OR (json_extract(m.value, '$.field.source') = 'main' AND json_extract(m.value, '$.condition.field.source') = 'child'));
    ```
- **J9 去重說明與累計。** `countIf` 與 `countMain` 同列進 `NeedsDedupedNote`（合併且條件在主模組、或依多選分組）；`median` 與 `avg` 同列；`countIf` 進卡片的 `AdditiveKinds`（可算累計占比、期間比較缺值當 0）。
- **J10 SQL 帶條件量值。** `COUNT(CASE WHEN {p} THEN {a}.ScaledValue END)`、`SUM(CASE WHEN {p} THEN {a}.ScaledValue / {Split} END)`、`SUM(CASE WHEN {p} THEN {a}.ScaledValue % {Split} END)`；`{Split}` 沿用 `ReportQueryBuilder` 現有的整數常數 `Split = 1_000_000_000`（`:30`，現行 `:182-183` 就是這樣用），**不寫 `1e9` 這類浮點常值**（SQLite、PostgreSQL 都會變成 REAL 除法，hi 帶小數、`GetInt64` 截斷，15 位數金額會和記憶體路徑對不上）。謂詞產生三次（各自別名與參數），不另外 join 旗標欄；`countIf` 同 `ratio` 的 `hit` 欄。效能見 Task 3 的基準案例（Y4），超出既有基準才改成先算一次旗標欄。**kr1a 審查 Y5：實測 10k 筆，不帶條件 avg 9 ms、帶條件 avg 23 ms（2.5 倍）、帶條件 avg＋sum＋countIf 41 ms（4.5 倍）、ratio 10 ms，成本可接受，不改成旗標欄；要到數百萬筆的規模再重測。**
- **J11 目錄端點。** 新增 `GET /api/v3/dynamic/reports/datasets`（`[{ dataset, name }]`）與 `GET /reports/datasets/{dataset}/catalog`（同 `ModuleCatalog` 形狀，只列可選欄位與觀看者的選項）；**不改** `GET /reports/catalog`（現在的設計器直接拿它當模組清單，多兩筆會被當成模組點進去壞掉），kr2 做設計器時再合併。理由見「範圍確認」。
- **J12 `TaskIds`。** `ReportResult` 末尾加 `IReadOnlyList<long>? TaskIds`（只有任務資料來源且有明細時有值；`WhenWritingNull` 不輸出，模組回應不變）；`CapDetail`、`DrillReportHandler`、`DashboardCardRunner` 三處截斷統一改呼叫 `ReportRules.TakeDetail(result, n)`，`TaskIds` 跟著截。
- **J13 定義 JSON。** `ReportDefinition` 末尾加 `[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Dataset = null`；模組報表重新存檔的 JSON 與現在逐字相同，`Version` 維持 1。
- **J14 所屬模組與任務規則的選項只列觀看者可讀的模組。** 一般使用者取 `TaskVisibility.Modules`，系統管理員取所有可讀的非明細模組；看得到記錄的任務一定在這些模組裡。
- **J15 Done 但 `CompletedTime` 為空（舊資料或直接寫庫）：** 完成時間、完成狀況、完成天數、延遲天數空白，`taskEvents` 不產生完成列。
- **J16 超過載入上限的訊息** 用 `ReportText.TooMany`（「資料太多，請縮小條件」，設計 §7.7），不用模組專用、叫人設可篩選的 `TooManyToLoad`。
- **J17 雙跑集。** 新的 EF LINQ 測試類（`TaskReportSourceTests`、`TaskReportRunnerTests`、`TaskReportsApiTests`）整類標 `[Trait("Category", "Postgres")]`；SQL 路徑的新案例放進 `ReportSqlEquivalenceTests`、不標（整類在 pg1 加入雙跑集，PostgreSQL 設計 §6.4）。pg0 還沒做，標了也只在 SQLite 跑，PostgreSQL 標「未驗證」。
- **J18 未知的 `dataset`** 存檔時 400；執行時（資料被直接改壞）回 `moduleDisabled` 狀態，fail closed。
- **J20 `TaskReportSource` 的快取（Y5）。** `CatalogAsync` 的快取鍵含 `UserId` 與 dataset（同 `TaskVisibilityService` 依使用者快取），並提供 `Reset()`，接到 `DemoStructure.ResetPerRequestState`（`DemoService/DemoStructure.cs:31`）。測試的 `Svc<ReportRunner>()` 一律從根 scope 取（`DesignerTestBase.cs:96`），在 `As(...)` 切身分或改政策後會共用快取，所以任務報表的測試一律照 `TaskVisibilityEquivalenceTests.cs:36-44` 用 `using var scope = NewScope()` 取 runner 與 source。
- **J21 任務報表欄位名稱與逾期定義單一來源（Y8）。** `IsOverdue` 放 Domain 的 `TaskReportValues`，既有 `TaskModels.Overdue`（`Application/Dynamic/TaskService/TaskModels.cs:45`）改成呼叫它，看板與報表只有一份「逾期」定義。
- **J22 欄位目錄的規則名稱揭露（Y12，接受）。** 規則清單現在只給設計器（`IDesignerRequest`），記錄頁只露可手動套用的規則；J14 讓任何有模組 Read 的人從 `datasets/tasks/catalog` 看到該模組全部規則名稱。風險低（規則名稱不是記錄內容），接受並列入設計 §14「安全」；不採「只列觀看者看得到的任務實際用到的規則」，因為會讓存檔的條件值正規化隨資料變動。
- **J23 存檔正規化（G8）。** 存檔時 `dataset: "module"` 正規化成 null，任務資料來源的 `mainModuleKey` null 正規化成 `""`，J13「JSON 逐字相同」才對明寫 `"module"` 的請求也成立，`ReportModuleLoader` 也不會收到 null key。
- **J24 J7 狀態縮小遇到不存在的選項值（`opt_9`）時略過、不丟例外；`in` 全部無效時 Statuses 為空集合（結果本來就是 0 筆）（G10）。**

### kr1b 實作時代為決定（2026-11-17，待使用者確認；推翻時改 Task 4、7）

- **K1 完成時間只在狀態是完成時有值。** 非完成但留有 `CompletedTime` 的資料（重開後未清、直接寫庫）在「完成時間」是空白，與完成狀況、完成天數一致；資料庫端縮小仍用 `CompletedTime`，只會多撈。
- **K2 狀態選項文字沿用看板的「待辦、進行中、已完成、已取消」**（前端 `status.*` 四語），不另造「完成」「取消」兩套字。
- **K3 規則與模組選項一律 Active。** 停用的規則仍可在條件裡選（它產生的任務還在）；部門照部門自己的 Active。
- **K4 J24 的「略過」解讀為：無效的選項值不進狀態集合。** `eq opt_9` 與 `in` 全部無效一樣是空集合（0 筆，與記憶體路徑結果相同），不丟例外。

---

## Global Constraints

- **業務政策不寫死**（鐵則 2）：kr1 沒有新的業務數字。載入上限沿用 `Reports:MaxLoadRows`、`Reports:MaxRows`（`ReportOptions`，可設定）；下鑽 500 筆、畫面明細 1000 筆沿用 `ReportRules.DrillLimit`／`DetailLimit` 單一來源（p2 的 A24 會改成政策，見「矛盾」3）；一週起始日讀 `calendar.weekStart`；基準日用 `ReportRunner.BasisDateAsync`。
- **fail closed**：看不到的任務不進列（載入時套 `TaskVisibility`，不是載入後丟）；記錄判斷不出來就當看不到（`restricted`）；`restricted`、`missing` 的格子對任何條件都不符合（含 `ne`、`notIn`）。
- **反目標**（設計 §2）是測試對象：任務報表不提供說明、留言、附件、記錄標題；條件「所屬模組＝X」不能推出看不到的記錄屬於哪個模組；匯出內容與畫面相同。
- **模組報表行為完全不變**：不帶 `dataset` 的定義、所有既有測試原樣通過；沒帶條件的 `sum`／`avg` 與現行結果相同；SQL 路徑只多出 `countIf`、帶條件量值兩種形狀。**唯一接受的行為改變是 J19**（舊 API 存的 `sum`／`avg` 帶條件開始生效，`min`／`max`／`countMain`／`countChild` 的條件存檔時丟掉，設計器不會產生這種定義）。
- **日期**：營運時區的日曆天；`CreatedTime`、`CompletedTime` 是 `IBusinessTime.Now` 寫入的台北牆上時間，取日期用 `DateOnly.FromDateTime`，不經主機時區；動到日期的測試另跑 `TZ=UTC`。
- **PostgreSQL 相容**：任務載入只用 EF LINQ，不寫 raw SQL；DateTime 參數 `Kind` 明確（`Unspecified` 牆上時間，與寫入一致）；不改 `ReportQueryBuilder` 的 SQLite 專屬片段（pg1 的事）。
- **i18n**：使用者看得到的新字串（欄位名、選項名、資料來源名、量值欄名、B2 空值名）放後端四語表（`ReportText` 與 `TaskReportFields` 的標籤表），泰文、越南文沿用「待母語者校對」；沒有前端 i18n 改動。
- **交易**：kr1 沒有新的寫入 request。
- **測試**：本機只跑相關類別（`dotnet test <專案> --filter <類名>`），完整套件交 GitHub Actions（`.github/workflows/gate.yml`）；不和其他吃 CPU 的工作同時跑；任務相關新測試類 `protected override bool ScopePerRequest => true;`（`TaskVisibilityService` 依請求快取）。
- **Demo 與公開 repo**：測試資料沿用既有虛構人名（`sales01`、`manager01`、`ops01`…）；不寫公司名、個人絕對路徑、內網 IP。
- **git**：逐檔 `git add`，commit 前 `git status`；不用 `--no-verify`、不 force push、不 merge commit；scope 用 `report`、`docs`。

### 可寫死清單（kr1 新增，併入設計 §14）

| 項目 | 值 | 類別 |
|---|---|---|
| 任務報表不提供說明、留言、附件、記錄標題欄位；下鑽也不給記錄標題 | — | 安全 |
| 看不到記錄時「所屬模組」「任務規則」一律 `restricted`，條件恆不符合 | — | 安全 |
| 任務資料來源的欄位代碼固定負數（-1～-15、-18、-19；-16、-17 保留） | — | 技術限制 |
| 任務資料來源只走記憶體路徑（`datasetMemoryOnly`）；`median` 在 SQL 路徑退回（`measureNotSupported:median`） | — | 技術限制 |
| 量值條件只准出現在 `ratio`、`countIf`、`sum`、`avg`、`median`；主模組欄位的量值不能帶子模組條件 | 400 | 資料完整性 |
| 任務資料來源不能合併子模組、不能帶出欄位、`mainModuleKey` 必須為空 | 400 | 資料完整性 |
| 任務下鑽固定六欄（B3；`taskEvents` 另一組六欄） | — | 安全（不給記錄內容） |
| 目錄端點列出可讀模組的全部規則名稱（J22） | — | 安全（接受的揭露） |
| 狀態、來源、完成狀況、事件種類的選項 Id（J3） | — | 技術限制（存檔的條件值靠它） |
| 任務報表的權限物件固定「Read＋PrintExport、範圍 All、無隱藏欄位」（`TaskReportSource.Permissions`；列的範圍另由任務可見條件決定，U9） | — | 安全（kr1b 新增） |
| B2 空值顯示字（未指派、（無部門）、系統、（無））四語寫在 `TaskReportFields.BlankLabels` | — | 技術限制（UI 文字，kr1b 新增） |

---

## Review Focus

測試最可能沒覆蓋、但最可能咬人的五個情境；每條的測試已加進負責的 Task。

1. **拆 `RunAsync` 改到模組報表。** 搬移時漏掉一個早退（`moduleDisabled`、`restricted`、`tooMany`、SQL 逾時）、`Passes` 的子模組語意、或 `fallbackReasons` 的順序。→ Task 5 不寫新測試，以既有類別當安全網全數不改斷言通過：ApplicationTests 的 `ReportRunnerTests`、`ReportRunnerSqlTests`、`ReportPermissionMatrix`（含 `ReportPermissionMatrixTests` 與繼承來的 `ReportPermissionMatrixSqlTests`，filter 用不含尾字的 `ReportPermissionMatrix`）、`ReportSqlEquivalenceTests`、`DemoReportsTests`、`SafeDeleteTests`；WebAPITests 的 `ReportsApiTests`（含匯出的 `ForExport` 範圍，ApplicationTests 沒有對應測試）、`SharedMasterPermissionTests`、`Dashboard`（儀表板測試都在 WebAPITests）、`ClosedModuleTests`、`ReportCatalogApiTests`；另加 `ModuleDefinition_SerializesWithoutDataset`（J13）。兩個專案的通過數都要記下。
2. **用條件或分組推出看不到的記錄。** 「所屬模組」用 `eq`、`ne`、`in`、`notIn`、`isEmpty`、`isNotEmpty` 當篩選與 `countIf` 條件，依「所屬模組」分組與排序，下鑽 `__restricted` 那一組，匯出。→ Task 7 `ModuleCondition_RestrictedNeverMatches_EveryOperator`、`GroupByModule_RestrictedOwnGroup_SortedLast`；Task 7 `RestrictedRecord_RuleCell_IsRestricted_NotMissingNorOk`；Task 9 `Drill_RestrictedGroup_ModuleColumnNoPermission`、`Export_RestrictedCell_NoPermissionText`。
3. **報表的列與「看得到的任務」不一致。** 軟刪除記錄的任務、凍結模組、資料範圍 Own、只被 @ 的人、主管（A33 關閉時）、管理員。→ Task 6 `Load_EqualsVisibleIds_ForEveryPersona`（每個身分的列 Id 集合＝`ListVisibleIdsAsync`）；Task 7 `PermissionMatrix_CountsPerPersona`。
4. **日界與基準日。** 23:59 與 00:10 完成、主機在 UTC、Demo 凍結基準日時逾期與延遲天數用基準日而不是今天。→ Task 4 `TaskReportValuesTests.Completion_AcrossMidnight`（Theory）；Task 7 `Overdue_UsesBasisDate`；本機加跑 `TZ=UTC`。
5. **帶條件量值的空值與 SQL／記憶體一致。** 沒有任何符合的列（`sum`／`avg`／`median` 是 null、`countIf` 是 0）、符合但值為空、負數與 15 位數金額、條件欄位是會被截斷的文字。→ Task 1 `ConditionalSum_NoMatches_IsNull`、`CountIf_NoMatches_IsZero`；Task 3 `ConditionalMeasures_MatchMemoryPath`（含負數、極值、文字條件退回）。

---

## 範圍確認

### 已在 main（只接、不重做）

| 項目 | 證據 |
|---|---|
| 任務可見條件（管理員、參與者、部屬、可讀模組範圍），EF LINQ 單一條件 | `Domain/Tasks/TaskVisibility.cs`、`TaskVisibilityService.ForCurrentUserAsync`、`Persistence/.../TaskVisibilityQuery.Visible`、`ITaskRepository.ListVisibleIdsAsync` |
| 記錄能不能看（含軟刪除 stub、凍結），批次 | `TaskAccess.EvaluateManyAsync` → `TaskPermissions.Record.Accessible` |
| 組織快照、部門 | `OrgSnapshotLoader`、`IOrganizationRepository.ListAsync` |
| 報表引擎（驗證、執行、下鑽、匯出、儀表板、分享到群組、遮蔽） | `ReportService/*`、`DashboardCardRunner` |
| `ratio` 的條件謂詞（SQL 與記憶體） | `ReportCalculator.MeasureOne`、`ReportQueryBuilder.Predicate`、`ReportRunner.Matches` |
| 內建應用程式與 Demo 報表沒有帶條件的非 `ratio` 量值 | 已查：`backend/apps/*.app.json` 的 `condition` 全是規則條件；`DemoReports.cs` 量值條件一律 null |

### 這次做

| 設計項目 | 子單元 |
|---|---|
| §7.4 `countIf`、`median`、`sum`／`avg`／`median` 帶條件（T13、T23），SQL 路徑 | kr1a |
| §7.1 `dataset`（T11）、§7.3 `tasks` 欄位與衍生算法、§7.5 列範圍與 `restricted`（T14）、§7.6 基準日（T15）、§7.7 只走記憶體路徑與 `SqlOnly`（T16）、資料來源介面 | kr1b |
| §7.2 `taskEvents`、§7.5 下鑽與匯出（U9）、§12 目錄端點、WebAPI 驗收、文件寫回 | kr1c |

### 不做

| 項目 | 理由 |
|---|---|
| 設計器選系統資料來源、欄位目錄畫面、下鑽列任務的畫面 | kr2 |
| 報表頁「部門」暫時篩選、執行與下鑽收 `departmentIds`（§8.2） | kr2（使用者指定） |
| 內建五份報表、`ReportBuiltIn` migration、量值自訂名稱 | kr3（見「矛盾」6） |
| 報表分享擴充成使用者與部門 | U8 已裁決不擴充 |
| 「層級」-16、「開始日」-17 | st1、gt1 |
| 任務報表進應用程式套件 | 💡（§8.3）；`AppReportCodec.ToPackage` 遇到 `mainModuleKey` 為空本來就回錯誤、不匯出，不改 |
| `GET /reports/catalog` 合併系統資料來源 | kr2（J11） |

**`GET /reports/catalog` 的判斷：** 驗收「用 API 可跑任務報表」需要知道欄位代碼與選項鍵。代碼是固定的（寫在設計 §7.3），但「所屬模組」「任務規則」「負責人部門」的選項是實體 Id，而且依觀看者可讀的模組而不同，沒有端點就只能猜 Id；所以**欄位目錄要放 kr1**（`GET /reports/datasets/{dataset}/catalog`，約 40 行，重用 `GetReportCatalogHandler.ToField`）。把兩個資料來源併進既有的 `GET /reports/catalog` 則**不放 kr1**：那個端點回的是模組清單，現在的設計器拿它當主模組選單，多兩筆會被當成模組、點下去打 `catalog/tasks` 得到 404；合併要跟設計器一起改，屬 kr2。

### 前端影響（kr1 沒有畫面，但要知道）

- 用 API 建的任務報表會出現在「我的報表」，但用現在的設計器打開會壞（沒有主模組）。kr1 只有 API、Demo 不放任務報表，所以 Demo 上不會出現；kr2 接手。
- 模組報表裡出現 `countIf`／`median` 的定義只能經 API 建；現在的設計器顯示這兩種量值時欄名照後端回的 `columns[].label`，量值設定步驟不認得這兩種（kr2）。

---

## 子單元切法

| 子單元 | 內容 | Task | 估計行數（產品＋測試） | 依賴 | 建議派工 |
|---|---|---|---|---|---|
| kr1a 通用量值 | `countIf`、`median`、帶條件量值：計算、驗證、欄名、SQL 路徑 | 1–3 | 約 180＋260＝440 | 無 | sonnet-medium（升級理由：超過 300 行、動 SQL 產生器 → opus-high 審查） |
| kr1b 資料來源＋tasks＋權限 | `dataset`、虛擬模組與衍生值、拆 `RunAsync`、任務載入、`restricted`、驗證 | 4–7 | 約 650＋550＝1200 | kr1a（`countIf`／帶條件量值給測試用） | opus-high（升級理由：安全與權限、公開介面、超過 300 行） |
| kr1c 事件、下鑽、匯出、目錄 | `taskEvents`、下鑽固定欄與 `TaskIds`、匯出、目錄端點、WebAPI 驗收、文件 | 8–10 | 約 230＋380＝610 | kr1b | sonnet-medium（升級理由：公開 API → opus-high 審查） |

- 每個子單元結束：跑該單元列出的測試類別、`formwright-purpose-check` 一次（寫進交付說明）、本單元新增的寫死項目列進交付說明；推分支等 CI（`bash scripts/unattended/ci-wait.sh <sha>`）。
- 合併界線：kr1a 合併後模組報表多兩種量值與帶條件量值；kr1b 合併後 API 可跑 `tasks`；kr1c 合併後 `taskEvents`、下鑽、匯出、目錄齊全，roadmap kr1 標上線。三段都沒有 migration，任一段回退都安全。

### 共用檔

| 類別 | 這段會碰的 | 說明 |
|---|---|---|
| i18n json（`frontend/src/i18n/*.json`） | 無 | 字串都在後端四語表 |
| 前端路由與選單 | 無 | |
| 後端 DI（`Application/DependencyInjection.cs`） | 一行：`services.AddScoped<Dynamic.ReportService.TaskReportSource>();` | 加在 `ReportDefinitionValidator` 那行（約第 98 行）之後，不加在檔尾 |
| DbContext／EF migration | 無 | `dataset` 是定義 JSON 的選填欄位 |
| `backend/apps/*.app.json` | 無 | |
| 其他多人會碰的 | `ReportText.cs`（量值欄名、資料來源名）、`TaskTestBase.SeedTask`（加兩個選填參數）、`ReportsController.cs`（兩個端點）、`TaskRepositories.cs`／`ITaskRepositories.cs`（一個方法）、`docs/roadmap.md` 第 46 列、`docs/feature.md` 第 228 行、設計文件 §7、§14、§17、§18 | 目前沒有已知的平行段；有的話 `ReportText` 新鍵加在表尾、`ITaskRepository` 新方法加在 `ListVisibleIdsAsync` 之後 |

---

## kr1a 通用量值

### Task 1: 記憶體路徑的 `countIf`、`median`、帶條件量值

**Files:**
- Modify: `src/DynamicModules.Domain/Reports/ReportCalculator.cs`（`Calculate` 的 matcher 檢查、`MeasureOne`、`NeedsDedupedNote`）
- Modify: `src/DynamicModules.Domain/Reports/ReportDefinition.cs`（類別註解的 Kind 清單加 `countIf|median`）
- Test: `tests/DynamicModules.DomainTests/Reports/ReportCalculatorTests.cs`

**Interfaces:**
- Produces：`ReportMeasure.Kind` 新值 `countIf`（必有 `Condition`，無 `Field`）、`median`（必有 `Field`，可有 `Condition`）；`sum`、`avg`、`median` 的 `Condition` 不為 null 時只計符合的列。`Calculate` 在任何量值帶條件（含 `countIf`）時要求 `matches`，否則丟 `ArgumentException`。

- [ ] **Step 1: 寫失敗測試**（`ReportCalculatorTests` 現有 helper `TwoOrders`、`M`、`Gte`、`Calc`；新增 `Status`＝`new("main", 5, null)` 與 `Eq(field, text)`，測試用 matcher 擴充成同時支援 `gte` 與文字 `eq`）

```csharp
[Fact] public void CountIf_MainCondition_DedupesByMain()      // TwoOrders、Gte(Amount,100) → 1；Calc([], …) → 0（不是 null）
[Fact] public void CountIf_ChildCondition_CountsChildRows()   // Gte(Qty,2) → 2
[Fact] public void CountIf_WithoutMatcher_Throws()            // Calculate(..., matches: null) → ArgumentException
[Theory]
[InlineData(new[] { 1, 3, 2 }, 2.0)]
[InlineData(new[] { 1, 2, 3, 10 }, 2.5)]
public void Median_OddAndEven(int[] values, double expected)   // 每值一筆主記錄；結果 decimal 比對 (decimal)expected
[Fact] public void Median_EmptyCellsSkipped_NoneIsNull()       // 一格空、一格 7 → 7；全空 → null
[Fact] public void Median_MainField_Merged_DedupesByMain()     // TwoOrders 的 Amount：100 只算一次、50 → 75
[Fact] public void ConditionalAvg_SpecExample()                // 設計 §19：延遲 2、4、6（完成狀況 opt_2）與 0、0（opt_1）
                                                               // avg 帶 Eq(Status,"opt_2") → 4；不帶 → 2.4；sum 帶條件 → 12；median 帶條件 → 4
[Fact] public void ConditionalSum_NoMatches_IsNull()           // 條件全不符合 → sum、avg、median 皆 null
[Fact] public void ConditionalSum_MatchButEmptyValue_NotCounted() // 符合的一列值為空、另一列 5 → avg 5
```

`NeedsDedupedNote` 的兩個既有 Theory（`ReportCalculatorTests.cs:173-178`、`:186-191`）不能只加 `InlineData`，本體要改：它們依 kind 決定 field／cond，`countIf` 現在會拿到 `cond=null`、`median` 拿到 `field=null`（前者 `NeedsDedupedNote` 得 false、後者 `MeasureOne` 丟「沒有欄位」）。選法：`countIf` → `field = null`、`cond = Gte(Amount, 1)`；`median` → `field = Amount`、`cond = null`；multiGroup 那個 Theory 的 `countIf` 同樣要 `cond = Gte(Amount, 1)`、不帶欄位。再各加資料列：

```csharp
[InlineData("countIf", false, true)]   // 合併、條件在主模組：主記錄只算一次
[InlineData("median", true, true)]     // 與 avg 同（J9）
// multiGroup 那個 Theory：
[InlineData("countIf", true)]
[InlineData("median", true)]
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `dotnet test tests/DynamicModules.DomainTests --filter "FullyQualifiedName~ReportCalculatorTests"`
Expected: 新案例 FAIL（`不支援的量值種類 countIf`、`median` 走到 `_ => throw`）。

- [ ] **Step 3: 實作**

`MeasureOne` 加 `countIf`：與 `ratio` 共用同一段「範圍＋命中數」，`countIf` 回命中數（範圍空時 0）。`sum`／`avg`／`median` 在取 `Scope(rows, field.Source)` 之後、取格子之前，`m.Condition` 不為 null 就只留 `matches(cond, row.Cells[KeyOf(cond.Field)])` 為真的列（列沒有該格視為不符合）。`median`：取 `picked` 排序，奇數取中間、偶數取中間兩數的和除以 2（decimal）。`Calculate` 開頭的 matcher 檢查改成「任何量值 `Kind is "ratio" or "countIf"` 或 `Condition` 不為 null」。`NeedsDedupedNote`：合併子句加 `m.Kind == "countIf" && m.Condition?.Field.Source == "main"`、`m.Kind is "sum" or "avg" or "median" && m.Field?.Source == "main"`；多選子句加 `countIf`、`median`。

- [ ] **Step 4: 跑測試確認通過**

Run: 同 Step 2。Expected: 全部 PASS（既有案例不改）。

- [ ] **Step 5: Commit**

```bash
git add src/DynamicModules.Domain/Reports/ReportCalculator.cs src/DynamicModules.Domain/Reports/ReportDefinition.cs tests/DynamicModules.DomainTests/Reports/ReportCalculatorTests.cs
git commit -m "feat(report): 量值新增 countIf、median 與帶條件的 sum、avg、median（記憶體路徑）"
```

### Task 2: 驗證、欄名、卡片與 SQL 路徑暫時退回

**Files:**
- Modify: `src/DynamicModules.Application/Dynamic/ReportService/Data/ReportRules.cs`（`Kinds`、`CheckShape`、`Check` 的量值欄位檢查）
- Modify: `src/DynamicModules.Application/Dynamic/ReportService/ReportRunner.cs`（`FieldWithChangedType`：`median` 同 `sum`／`avg`）
- Modify: `src/DynamicModules.Application/Dynamic/ReportService/ReportRunner.Output.cs`（`MeasureColumn`）
- Modify: `src/DynamicModules.Application/Dynamic/ReportService/ReportRunner.Card.cs`（`AdditiveKinds` 加 `countIf`）
- Modify: `src/DynamicModules.Application/Dynamic/ReportService/ReportRunner.Sql.cs`（`PlanAggregate` 暫時退回）
- Modify: `src/DynamicModules.Application/Dynamic/ReportService/Data/ReportText.cs`（`CountIf`、`CountIfEq`、`Median`、`Conditional`、`ConditionalEq` 五個鍵，四語）
- Test: `tests/DynamicModules.ApplicationTests/Dynamic/ReportRunnerTests.cs`、`tests/DynamicModules.ApplicationTests/Dynamic/ReportPermissionMatrixTests.cs`

**Interfaces:**
- Consumes：Task 1 的量值語意。
- Produces：存檔／預覽驗證規則（J8）；欄名（B4／Y13）：`median` → `ReportText.Median`「{0} 中位數」（ValueType 同 `avg`：`number` 或 `percent`）；`countIf` 條件是單一值的 `eq` 且值轉得成標籤時 → `ReportText.CountIfEq`「{0}＝{1} 筆數」，否則 `ReportText.CountIf`「{0} 符合條件筆數」（ValueType `number`）；`sum`／`avg`／`median` 帶條件時，條件是可轉標籤的單一值 `eq` → 原欄名加 `ReportText.ConditionalEq`「（{0}＝{1}）」，否則加 `ReportText.Conditional`「（符合條件）」。值轉標籤用 `FieldDisplay.Format`（Enum、Boolean、Text、Integer、Decimal、Currency、Percent、Date；使用者欄位、已刪除的選項等轉不出來就走後備字樣），整個邏輯集中在 `MeasureColumn` 旁一個小函式，B4 被推翻時只改這一處。SQL 路徑退回原因：`measureNotSupported:countIf`、`measureNotSupported:median`、`measureConditionNotSupported:<量值 Id>`（Task 3 拿掉第一與第三個）。

- [ ] **Step 1: 寫失敗測試**

`ReportRunnerTests`（會自動在 `ReportRunnerSqlTests` 再跑一次 SQL 版本）：

```csharp
[Fact] public async Task CountIf_And_ConditionalAvg_HandComputed()   // 不含 median，Task 3 之後 SQL 版本真的走 SQL
// Arrange()；order 四筆（sales01）：金額 10／OptDone、20／OptDone、30／OptOpen、空／OptDone
// Def(measures: [M("done","countIf",cond:F(MainF(Status),"eq",OptDone)), M("a","avg",MainF(Amount),F(MainF(Status),"eq",OptDone)), M("s","sum",MainF(Amount))])
// Total: done=3、a=15、s=60；Columns 欄名（B4／Y13）：「狀態＝已完成 筆數」「訂單金額 平均（狀態＝已完成）」「訂單金額 加總」
[Fact] public async Task Median_HandComputed()                        // 獨立一張報表（median 永遠退回記憶體路徑）：M("med","median",MainF(Amount)) → 20；欄名「訂單金額 中位數」
[Fact] public async Task MeasureColumnNames_FallbackWording()         // countIf 條件 Amount gte 100 → 「訂單金額 符合條件筆數」；avg 帶 Amount gte 100 → 「訂單金額 平均（符合條件）」；`in` 兩值同退回
[Fact] public async Task Save_InvalidConditionPlacement_400()
// 經 CreateReportRequest：min 帶條件、countMain 帶條件、countIf 沒條件、median 沒欄位、median 用文字欄位、
// 主模組欄位 sum 帶子模組條件（Shipments、ChildF(Warehouse)）→ 每個都 IsInvalid，訊息含「量值」
[Fact] public async Task Card_CountIf_CumulativePercent()
// RunCardAsync（Cumulative=true）依 Sales 分組、第一個量值 countIf → Cumulative 有值、最後一筆 100
```

`ReportPermissionMatrixTests.Positions` 加三筆，`HiddenAt` 對應：

```csharp
"countIfCondition" => Def(measures: [M("c", "countIf", cond: F(MainF(Secret), "eq", "猜"))]),
"avgCondition"     => Def(measures: [M("a", "avg", MainF(Amount), F(MainF(Secret), "eq", "猜"))]),
"medianField"      => Def(measures: [M("m", "median", MainF(SecretNum))]),
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~ReportRunnerTests|FullyQualifiedName~ReportRunnerSqlTests|FullyQualifiedName~ReportPermissionMatrix"`（`ReportPermissionMatrix` 同時比對 `ReportPermissionMatrixTests` 與繼承來的 `ReportPermissionMatrixSqlTests`；filter 寫完用 `dotnet test --list-tests --filter …` 確認兩個類別都在清單裡）
Expected: 新案例 FAIL（存檔被拒「量值種類不合法」；SQL 版本的 `MeasureValues` 把未知種類當 min/max）。

- [ ] **Step 3: 實作**

`ReportRules.Kinds` 加 `countIf`、`median`；`MeasureColumn` 欄名照 Produces；`CheckShape`：`countIf` 沒條件 →「量值 {Id} 需要條件」；`median` 沒欄位 →「量值 {Id} 需要欄位」；`Condition` 出現在 `min`、`max`、`countMain`、`countChild` → **不報錯，存檔前由 `DropIgnoredConditions` 丟掉**（J19）；`m.Field?.Source == "main" && m.Condition?.Field.Source == "child"` →「量值 {Id} 的條件不能用子模組欄位」。`Check`：`median` 與 `sum`／`avg` 同一個 `CanSum` 檢查。`PlanAggregate` 的量值迴圈前：`countIf`、`median` 加 `measureNotSupported:{kind}`；`sum`／`avg` 帶條件加 `measureConditionNotSupported:{m.Id}`。

- [ ] **Step 4: 跑測試確認通過**

Run: 同 Step 2。Expected: PASS；SQL 版本的新案例走記憶體路徑，退回原因不含環境問題（`ReportTestBase.Run` 的斷言）。

- [ ] **Step 5: Commit**

```bash
git add src/DynamicModules.Application/Dynamic/ReportService/Data/ReportRules.cs src/DynamicModules.Application/Dynamic/ReportService/ReportRunner.cs src/DynamicModules.Application/Dynamic/ReportService/ReportRunner.Output.cs src/DynamicModules.Application/Dynamic/ReportService/ReportRunner.Card.cs src/DynamicModules.Application/Dynamic/ReportService/ReportRunner.Sql.cs src/DynamicModules.Application/Dynamic/ReportService/Data/ReportText.cs tests/DynamicModules.ApplicationTests/Dynamic/ReportRunnerTests.cs tests/DynamicModules.ApplicationTests/Dynamic/ReportPermissionMatrixTests.cs
git commit -m "feat(report): countIf、median 與量值條件的存檔驗證、欄名與卡片累計"
```

### Task 3: SQL 路徑的 `countIf` 與帶條件 `sum`／`avg`

**Files:**
- Modify: `src/DynamicModules.Application/Dynamic/ReportService/ReportRunner.Sql.cs`（`PlanAggregate` 量值迴圈、`MeasureValues`）
- Modify: `src/DynamicModules.Persistence/Dynamic/Query/ReportQueryBuilder.cs`（`Aggregate` 的量值 switch）
- Modify: `src/DynamicModules.Persistence/Repositories/Dynamic/ReportQueryRepository.cs`（`ReadAsync` 讀 `countIf` 的 `hit`）
- Modify: `src/DynamicModules.Domain/Reports/ReportSqlPlan.cs`（`ReportSqlMeasure` 註解：Kind 加 `countIf`；sum／avg 的 `Condition` 意義）
- Test: `tests/DynamicModules.ApplicationTests/Dynamic/ReportSqlEquivalenceTests.cs`

**Interfaces:**
- Consumes：Task 2 的退回原因位置。
- Produces：`ReportSqlMeasure(Id, "countIf", null, cond)`；`ReportSqlMeasure(Id, "sum"|"avg", field, cond?)`；`median` 仍回 `measureNotSupported:median`（設計 §7.4 的原因碼）。

- [ ] **Step 1: 寫失敗測試**（`ReportSqlEquivalenceTests` 的 `Prepare`、`SeedOrders`、`Both`、`ShouldMatch`）

```csharp
[Fact] public async Task ConditionalMeasures_MatchMemoryPath()
// cases：countIf（Status eq OptDone、Amount gte 100、Done eq true、Due beforeToday）、
//        sum／avg 帶條件（Amount 帶 Status eq OptDone；Price 帶 Sales in [sales01]；Amount 帶 Amount lt 0 → 只算負數；Amount 帶 Amount gte 100 → 符合列含 999999999999.99，驗 J10 的整數 `{Split}` 加總與記憶體路徑逐位相同，R1）、
//        依 Sales 與 Due 週分桶分組各跑一次；每個 case ShouldMatch(memory, sql, name)
[Fact] public async Task Median_FallsBack_WithReason()
// Both(Def(measures:[M("m","median",MainF(Amount))]), second: Auto) → sql.Engine == "memory"、FallbackReasons 含 "measureNotSupported:median"、數字同記憶體
[Fact] public async Task ConditionalAvg_TruncatedTextCondition_FallsBack()
// 照 TruncatedIndexText_NeAndGroup_FallBack 的資料：avg(Amount) 帶 Note eq <超長文字> → 退回，原因同既有文字截斷
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~ReportSqlEquivalenceTests"`
Expected: `ConditionalMeasures_MatchMemoryPath` FAIL（SqlOnly 丟「無法走 SQL 路徑：measureNotSupported:countIf」）。

- [ ] **Step 3: 實作**

`PlanAggregate`：拿掉 Task 2 加的 `countIf` 與 `measureConditionNotSupported` 退回；`countIf` 同 `ratio` 呼叫 `planner.Condition(m.Condition!)`；`sum`／`avg` 帶條件時 `mf` 與 `cond` 都要規劃成功才加量值。`ReportQueryBuilder.Aggregate`：`countIf` 同 `ratio` 輸出 `SUM(CASE WHEN … THEN 1 ELSE 0 END) AS hit{j}`；`sum`／`avg` 的 `m.Condition` 不為 null 時三個欄改成 J10 的 `CASE WHEN` 形式（每個 `CASE` 各自呼叫一次 `Predicate`；除法與餘數一律用整數常數 `{Split}`，不寫 `1e9`）。`ReadAsync`：`case "ratio" or "countIf"`。`MeasureValues`：`"countIf" => raw.Hit`。

- [ ] **Step 4: 跑測試確認通過**

Run: `dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~ReportSqlEquivalenceTests|FullyQualifiedName~ReportRunnerSqlTests"`
另跑 `dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~ReportBenchmarkTests"`，並在其中加一個「帶條件 avg」情境記錄耗時（Y4；超出既有基準時，改成子查詢先算一次旗標欄 `CASE WHEN p THEN 1 END AS c{j}`，外層三個聚合共用）。
Expected: PASS；`ReportRunnerSqlTests` 裡 Task 2 的 `CountIf_And_ConditionalAvg_HandComputed` 現在走 SQL（含 `median` 的 `Median_HandComputed` 仍走記憶體路徑，不在此預期內，Y1）。

- [ ] **Step 5: Commit**

```bash
git add src/DynamicModules.Application/Dynamic/ReportService/ReportRunner.Sql.cs src/DynamicModules.Persistence/Dynamic/Query/ReportQueryBuilder.cs src/DynamicModules.Persistence/Repositories/Dynamic/ReportQueryRepository.cs src/DynamicModules.Domain/Reports/ReportSqlPlan.cs tests/DynamicModules.ApplicationTests/Dynamic/ReportSqlEquivalenceTests.cs
git commit -m "feat(report): SQL 路徑支援 countIf 與帶條件的 sum、avg，median 退回記憶體路徑"
```

---

## kr1b 資料來源介面、`tasks` 與權限

### Task 4: `dataset`、虛擬模組與衍生值（Domain）

**Files:**
- Modify: `src/DynamicModules.Domain/Reports/ReportDefinition.cs`（J13 的 `Dataset`）
- Create: `src/DynamicModules.Domain/Reports/ReportDatasets.cs`
- Create: `src/DynamicModules.Domain/Reports/TaskReportFields.cs`
- Create: `src/DynamicModules.Domain/Reports/TaskReportValues.cs`
- Modify: `src/DynamicModules.Domain/Reports/ReportReferences.cs`（資料來源不是模組時回空集合）
- Modify: `src/DynamicModules.Application/Dynamic/TaskService/TaskModels.cs`（`Overdue` 改呼叫 `TaskReportValues.IsOverdue`，J21）
- Test: `tests/DynamicModules.DomainTests/Reports/TaskReportFieldsTests.cs`、`tests/DynamicModules.DomainTests/Reports/TaskReportValuesTests.cs`、`tests/DynamicModules.DomainTests/Reports/ReportDefinitionTests.cs`、`tests/DynamicModules.DomainTests/Reports/ReportReferencesTests.cs`

**Interfaces:**
- Produces：

```csharp
public static class ReportDatasets
{
    public const string Module = "module", Tasks = "tasks", TaskEvents = "taskEvents";
    public static bool IsModule(string? dataset);          // null 或 "module"
    public static bool IsTask(string? dataset);            // tasks 或 taskEvents
}

public static class TaskReportFields
{
    public const long Title = -1, Status = -2, Assignee = -3, AssigneeDepartment = -4, Creator = -5, DueDate = -6,
        CreatedTime = -7, CompletedTime = -8, Source = -9, Module = -10, Rule = -11, Overdue = -12, Completion = -13,
        CompletionDays = -14, DelayDays = -15, EventKind = -18, EventDate = -19;
    public static long StatusOption(TaskItemStatus s);                 // (long)s + 1
    public const long SourcePersonal = 1, SourceRecord = 2, SourceRule = 3;
    public const long CompletionOnTime = 1, CompletionLate = 2, CompletionNoDue = 3, CompletionNotDone = 4;
    public const long EventCreated = 1, EventCompleted = 2;
    public static IReadOnlySet<long> Selectable(string dataset);       // tasks：-1..-15；taskEvents：-18、-19、-2、-3、-4、-5、-9、-10、-11
    public static IReadOnlySet<long> Restrictable { get; }             // {-10, -11}
    public static IReadOnlyList<long> DrillColumns(string dataset);    // tasks：[-1, -2, -3, -6, -8, -10]（B3）；taskEvents：[-18, -19, -1, -2, -3, -10]（Y7）
    public static IReadOnlyDictionary<long, I18nText> BlankLabels { get; } // -3 未指派、-4（無部門）、-5 系統、-10（無）、-11（無）（B2）
    /// <summary>不入庫的模組：Id 0、Key ""、NameI18n 是資料來源名；欄位含 Selectable 與 DrillColumns(dataset) 的聯集，選項依傳入的實體。</summary>
    public static Module Build(string dataset, IReadOnlyList<Department> departments, IReadOnlyList<Module> modules, IReadOnlyList<TaskRule> rules);
}

public static class TaskReportValues
{
    public static long SourceOf(TaskItem t);                                    // RuleId → 規則；RecordId → 記錄；否則個人
    public static bool IsOverdue(TaskItem t, DateOnly basis);                   // IsOpen && DueDate < basis（B1）；`TaskModels.Overdue` 改呼叫它（J21）
    public static long? CompletionOf(TaskItem t);                               // Done 且 CompletedTime 為 null → null（J15）
    public static int? CompletionDays(TaskItem t);                              // 完成日 − 建立日（日曆天）；未完成 null
    public static int? DelayDays(TaskItem t, DateOnly basis);                   // B1；不小於 0；沒有到期日 null
}
```

欄位型別：-1 `Textarea`；-2、-4、-9、-10、-11、-13、-18 `Enum`；-3、-5 `User`；-6 `Date`；-7、-8、-19 `DateTime`；-12 `Boolean`；-14、-15 `Integer`。欄位與選項標籤四語（繁中照設計 §7.3；`NameI18n`：tasks「任務」、taskEvents「任務事件（建立或完成）」）。部門選項 `Active` 照部門；所屬模組選項用 `Module.NameI18n`；規則選項用 `TaskRule.NameI18n`。

- [ ] **Step 1: 寫失敗測試**

```csharp
// TaskReportValuesTests（每個案例只給需要的欄位；basis = 2026-11-04）
[Theory]
[InlineData("2026-11-04T23:59:00", "2026-11-04", 1L /*準時*/, 0)]
[InlineData("2026-11-05T00:10:00", "2026-11-04", 2L /*逾期*/, 1)]
public void Completion_AcrossMidnight(string completed, string due, long completion, int delay)
[Fact] public void Completion_NoDue_And_NotDone_And_DoneWithoutTime() // 無到期日完成 3；待辦 4；取消 4；Done＋CompletedTime null → null
[Fact] public void CompletionDays_SameDayIsZero_OpenIsNull()          // 建立 11-02 09:00、完成 11-02 18:00 → 0；建立 11-01、完成 11-04 → 3
[Fact] public void DelayDays_ByStatus()                               // 完成晚 2 天 → 2；完成提早 → 0；待辦到期 11-01 → 3；取消 → null；沒到期日 → null
[Fact] public void IsOverdue_OnlyOpenBeforeBasis()                    // 待辦 11-03 → true；到期 11-04 → false；取消 11-01 → false
[Fact] public void CompletionDays_And_DelayDays_DoneWithoutCompletedTime_AreNull() // J15、G12：Done 但 CompletedTime null → 完成天數、延遲天數都 null
[Fact] public void SourceOf_ThreeKinds()

// TaskReportFieldsTests
[Fact] public void Build_Tasks_FieldsNegativeAndTyped()               // 欄位 Id 集合＝-1..-15；-1 是 Textarea；-12 Boolean；全部 Active
[Fact] public void Build_TaskEvents_ContainsSelectableAndDrill()      // 含 -18、-19 與 DrillColumns("taskEvents")；不含 -12..-15
[Fact] public void Build_Options_FromEntities()                       // 兩個部門（一個停用 → Active=false）、兩個模組、一條規則 → 選項 Id 對應實體 Id；狀態、來源、完成狀況、事件種類的選項都明設 Active=true（`FieldOption.Active` 預設 false，G5）
[Fact] public void Title_CannotGroup()                                // ReportFieldEligibility.CanGroup(標題欄位) == false
[Fact] public void Labels_FourLocales()                               // 每個欄位與選項的 LabelI18n 在 zh-TW、en-US、th、vi 都非空

// ReportDefinitionTests
[Fact] public void ModuleDefinition_SerializesWithoutDataset()        // 既有定義 Parse 後 Serialize，JSON 不含 "dataset"
[Fact] public void TaskDefinition_RoundTripsDataset()

// ReportReferencesTests
[Fact] public void TaskDataset_ReferencesNothing()                    // dataset tasks、欄位 -3、條件 -10 → ModuleKeys 與 FieldIds 都空
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `dotnet test tests/DynamicModules.DomainTests --filter "FullyQualifiedName~TaskReport|FullyQualifiedName~ReportDefinitionTests|FullyQualifiedName~ReportReferencesTests"`
Expected: 編譯失敗（型別不存在）。

- [ ] **Step 3: 實作** 上列三個新檔與兩處修改；日期一律 `DateOnly.FromDateTime(t.CreatedTime)`／`CompletedTime`。

- [ ] **Step 4: 跑測試確認通過**（另跑一次 `TZ=UTC dotnet test …` 同一 filter）。`ReportReferences` 的使用者在 ApplicationTests，再跑 `dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~SafeDeleteTests|FullyQualifiedName~TaskBoard|FullyQualifiedName~TaskService"`（`SafeDeleteTests` 用 `ReportReferences`；後兩個確認 `TaskModels.Overdue` 改呼叫後看板計數不變，filter 以 `--list-tests` 確認比對得到）。Expected: PASS。

- [ ] **Step 5: Commit**

```bash
git add src/DynamicModules.Application/Dynamic/TaskService/TaskModels.cs src/DynamicModules.Domain/Reports/ReportDefinition.cs src/DynamicModules.Domain/Reports/ReportDatasets.cs src/DynamicModules.Domain/Reports/TaskReportFields.cs src/DynamicModules.Domain/Reports/TaskReportValues.cs src/DynamicModules.Domain/Reports/ReportReferences.cs tests/DynamicModules.DomainTests/Reports/TaskReportFieldsTests.cs tests/DynamicModules.DomainTests/Reports/TaskReportValuesTests.cs tests/DynamicModules.DomainTests/Reports/ReportDefinitionTests.cs tests/DynamicModules.DomainTests/Reports/ReportReferencesTests.cs
git commit -m "feat(report): 報表定義加 dataset，任務資料來源的欄位目錄與衍生值"
```

### Task 5: 拆 `RunAsync`：載入與共用尾段（模組行為不變）

**Files:**
- Modify: `src/DynamicModules.Application/Dynamic/ReportService/ReportRunner.cs`
- Modify: `src/DynamicModules.Application/Dynamic/ReportService/ReportRunner.Cells.cs`（抽 `ApplyBucket`）
- Modify: `src/DynamicModules.Application/Dynamic/ReportService/ReportRunner.Output.cs`（`GroupLabel`、`Display` 讀 `BlankLabels`；`KeyRank`／`GroupLabel` 讀 `MayBeUnavailable`）

**Interfaces:**
- Produces（全部 `private`，Task 7、8、9 使用）：

```csharp
private sealed record Bound(ReportFieldRef Ref, string Key, Side Source, Field Field, Field? Via, Side? Target, bool Restrictable = false)
{ public bool MayBeUnavailable => Via is not null || Field.Type == FieldType.Reference || Restrictable; }
private sealed record RunContext(string Locale, IReadOnlyDictionary<string, string> UserNames, Dictionary<(string, string), string> Titles,
    IReadOnlyDictionary<string, string> Buckets, IReadOnlyDictionary<string, string> BlankLabels);
private sealed record SourceRows(IReadOnlyDictionary<string, Bound> Bound, List<ReportRow> Rows, Side Main, Side? Child, Module RowModule,
    RunContext Ctx, DateOnly Basis, bool Frozen, ReportAppliedRange? Applied, List<string> FallbackReasons,
    IReadOnlyDictionary<long, long>? TaskIdOf);
private sealed record SourceLoad(ReportResult? Done, SourceRows? Rows);
private Task<SourceLoad> LoadModuleAsync(ReportDefinition d, ReportRunOptions o, DateOnly basis, bool frozen, string locale, CancellationToken ct);
private async Task<(ReportDateRange? Range, (DateOnly, DateOnly)? Resolved, ReportAppliedRange? Applied)> ResolveRangeAsync(ReportDefinition d, ReportRunOptions o, DateOnly basis, CancellationToken ct);
private async Task<IReadOnlyDictionary<string, string>> UserNamesAsync(IEnumerable<Bound> bound, CancellationToken ct);
private static ReportCell ApplyBucket(Bound b, ReportCell cell, RunContext ctx);
```

- [ ] **Step 1: 確認安全網是綠的**（這個 Task 不加新測試：行為不變，五個既有類別就是測試）

Run（兩個專案各一行，通過數都記下）：
`dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~ReportRunnerTests|FullyQualifiedName~ReportRunnerSqlTests|FullyQualifiedName~ReportPermissionMatrix|FullyQualifiedName~ReportSqlEquivalenceTests|FullyQualifiedName~DemoReportsTests|FullyQualifiedName~SafeDeleteTests"`
`dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~ReportsApiTests|FullyQualifiedName~SharedMasterPermissionTests|FullyQualifiedName~Dashboard|FullyQualifiedName~ClosedModuleTests|FullyQualifiedName~ReportCatalogApiTests"`
（`ReportPermissionMatrix` 同時涵蓋 `ReportPermissionMatrixTests` 與繼承來的 `ReportPermissionMatrixSqlTests`；`Dashboard` 涵蓋 `DashboardCardsApiTests`、`DashboardReviewFixesApiTests`、`AdminDashboardPreviewApiTests`。filter 寫好後用 `dotnet test --list-tests --filter …` 確認這些類別都在清單裡；匯出的 `ForExport` 資料範圍在 ApplicationTests 沒有測試，靠 WebAPITests 的 `ReportsApiTests`、`SharedMasterPermissionTests`。）
Expected: PASS，記下兩個專案的通過數。

- [ ] **Step 2: 搬移**

現行 `RunAsync` 第 87–251 行（步驟 1 到合併後的 `MaxRows` 檢查）原封搬進 `LoadModuleAsync`，每個 `return Fail(...)`／SQL 結果改成 `return new SourceLoad(結果, null)`，結尾回 `new SourceLoad(null, new SourceRows(..., RowModule: childSide?.Module ?? main, TaskIdOf: null))`；第 178–187 行抽成 `ResolveRangeAsync`、第 188–190 行抽成 `UserNamesAsync`，`LoadModuleAsync` 呼叫它們。`RunAsync` 只留：卡片排序覆寫、基準日、`Fail` 輔助、`var load = ReportDatasets.IsModule(d.Dataset) ? await LoadModuleAsync(...) : Fail(ModuleDisabledState, …)`（Task 7 換成 `LoadTasksAsync`）、第 253–281 行的尾段改讀 `load.Rows`。`BuildCellsAsync` 的分桶兩行改呼叫 `ApplyBucket`。`GroupLabel` 空鍵改 `ctx.BlankLabels.GetValueOrDefault(b.Key) ?? Blank`；`Display` 的 `Empty` 改回 `ctx.BlankLabels.GetValueOrDefault(b.Key)`（模組來源字典為空 → 仍是 null）。

- [ ] **Step 3: 跑安全網**

Run: 同 Step 1（兩個專案）。Expected: 通過數與 Step 1 相同，沒有任何斷言被改。

- [ ] **Step 4: Commit**

```bash
git add src/DynamicModules.Application/Dynamic/ReportService/ReportRunner.cs src/DynamicModules.Application/Dynamic/ReportService/ReportRunner.Cells.cs src/DynamicModules.Application/Dynamic/ReportService/ReportRunner.Output.cs
git commit -m "refactor(report): 報表執行拆成資料來源載入與共用尾段，模組報表行為不變"
```

### Task 6: 任務載入、虛擬模組服務與驗證接線

**Files:**
- Modify: `src/DynamicModules.Domain/Repositories/Dynamic/ITaskRepositories.cs`（`TaskReportNarrowing`、`ListForReportAsync`）
- Modify: `src/DynamicModules.Persistence/Repositories/Dynamic/TaskRepositories.cs`
- Create: `src/DynamicModules.Application/Dynamic/ReportService/TaskReportSource.cs`
- Modify: `src/DynamicModules.Application/DependencyInjection.cs`（一行）
- Modify: `src/DynamicModules.Application/Dynamic/ReportService/ReportModuleLoader.cs`（資料來源分支；建構子（`:18`）加 `TaskReportSource` 相依，已確認沒有手動 `new ReportModuleLoader(`／`new ReportRunner(`，只走 DI、不會循環相依，G2）
- Modify: `src/DynamicModules.Application/Dynamic/DemoService/DemoStructure.cs`（`ResetPerRequestState` 呼叫 `TaskReportSource.Reset()`，J20）
- Modify: `src/DynamicModules.Application/Dynamic/ReportService/Data/ReportRules.cs`（`CheckShape` 資料來源規則、`Check` 可選欄位）
- Test: `tests/DynamicModules.ApplicationTests/Dynamic/Tasks/TaskReportSourceTests.cs`、`tests/DynamicModules.ApplicationTests/Dynamic/Tasks/TaskTestBase.cs`（`SeedTask` 加 `DateTime? created = null, DateTime? completed = null`，預設值行為不變）

**Interfaces:**
- Consumes：Task 4 的 `TaskReportFields`、`ReportDatasets`；kb1 的 `TaskVisibilityService`、`TaskAccess`、`TaskVisibilityQuery`。
- Produces：

```csharp
// ITaskRepositories.cs
/// <param name="DateField">due|created|completed|createdOrCompleted；null 不縮小日期。From、ToInclusive 是營運日期，含頭含尾。</param>
public sealed record TaskReportNarrowing(IReadOnlyCollection<TaskItemStatus>? Statuses, string? DateField, DateOnly? From, DateOnly? ToInclusive);
/// <summary>可見條件＋縮小條件，不追蹤、含參與者，依 Id；最多 take 筆。</summary>
Task<List<TaskItem>> ListForReportAsync(TaskVisibility visibility, TaskReportNarrowing narrowing, int take, CancellationToken ct);

// TaskReportSource.cs
public sealed record TaskReportItem(TaskItem Task, bool RecordAccessible, long? AssigneeDepartmentId);
public sealed class TaskReportSource(ITaskRepository tasks, ITaskRuleRepository rules, IOrganizationRepository org,
    IModuleRepository modules, TaskVisibilityService visibility, TaskAccess access)
{
    /// <summary>不帶隱藏欄位、動作含 Read 與 PrintExport、範圍 All 的權限（U9：任務報表沒有模組的匯出權限可查）。</summary>
    public static EffectivePermissions Permissions { get; }
    public Task<Module> CatalogAsync(string dataset, CancellationToken ct);     // J14 的選項；快取鍵含 UserId 與 dataset（J20）
    public void Reset();                                                         // 清快取；接到 DemoStructure.ResetPerRequestState
    public Task<IReadOnlyList<TaskReportItem>> LoadAsync(TaskReportNarrowing narrowing, int take, CancellationToken ct);
}
```

`ReportModuleLoader.LoadAsync`：`ReportDatasets.IsTask(d.Dataset)` 時直接回 `(new ReportModuleSet(new ReportSide(await taskReports.CatalogAsync(d.Dataset!, ct), TaskReportSource.Permissions), null, empty), null)`，不看 `action`、不看 `lenient`（驗證、遮蔽、`CanExport`、匯出都走這裡）。`CheckShape` 資料來源規則：`dataset` 只能是 `module`、`tasks`、`taskEvents`；任務資料來源時 `mainModuleKey` 必須空白（取代「主模組必填」）、`child` 必須為 null、所有引用 `ViaFieldId` 為 null、`Source` 為 `main`、`countChild` 不能用。`Check`：任務資料來源時，`AllRefs` 的欄位代碼不在 `TaskReportFields.Selectable(dataset)` →「欄位 {id} 不能用於這個資料來源」。

- [ ] **Step 1: 寫失敗測試**（`TaskReportSourceTests : TaskTestBase`，整類 `[Trait("Category", "Postgres")]`、`ScopePerRequest => true`；組織用 `DemoPeople.EnsureOrganizationAsync`，同 `TaskVisibilityEquivalenceTests`）

```csharp
[Theory]
[InlineData("admin")] [InlineData("sales01")] [InlineData("sales02")] [InlineData("manager01")] [InlineData("ops01")] [InlineData("floor01")]
public async Task Load_EqualsVisibleIds_ForEveryPersona(string user)
// 資料：個人任務、記錄任務（Own 範圍內外各一）、軟刪除記錄的任務、凍結模組的任務、只被 @ 的任務、部屬的未指派任務
// LoadAsync(無縮小) 的 Task.Id 集合 == ListVisibleIdsAsync(ForCurrentUserAsync) 的集合；
// RecordAccessible == EvaluateManyAsync(...)[i].Record?.Accessible == true
[Fact] public async Task Load_SupervisorPolicyOff_DropsSubordinateTasks()    // task.supervisorScope=false → manager01 少了部屬的任務（用 `using var scope = NewScope()` 取 source，J20；改政策後同一 scope 會讀到舊值）
[Fact] public async Task Catalog_CacheIsPerUser_AndReset()                  // 同一 source 先後以 sales01、admin 取目錄選項不同；Reset() 後重新讀取（J20）
[Fact] public async Task Load_QueryCount_DoesNotGrowWithTaskCount()          // 照 Evaluate_QueryCount_DoesNotGrowWithTaskCount：10 筆與 40 筆查詢次數相同
[Fact] public async Task Validate_ModuleDatasetNormalized_Http()             // 請求明寫 dataset "module" → 存檔 JSON 與不帶 dataset 逐字相同；任務資料來源 mainModuleKey null → ""（J23）
[Fact] public async Task Load_Narrowing_UnknownStatusOption_Ignored() // J24：opt_9 略過不丟例外；`in` 全部無效 → 0 筆
[Fact] public async Task Load_Narrowing_StatusAndDates()                     // Statuses=[Done]、completed 11-01..11-03 → 只回區間內完成的；createdOrCompleted 取聯集；邊界日含頭含尾
[Fact] public async Task Catalog_OptionsFollowViewer()                       // sales01 的所屬模組選項只有可讀模組；admin 有全部非明細模組；部門含停用部門
[Fact] public async Task Validate_TaskDefinitionShape_400()
// 經 ReportDefinitionValidator.ValidateAsync（執行要到 Task 7 才接上）：tasks＋mainModuleKey "order"、tasks＋child、tasks＋via、
// dataset "foo"、taskEvents 用 -12、tasks 依 -1 分組 → Failure 是 400
[Fact] public async Task Validate_ValidTaskDefinition_Normalized()           // 條件 -2 in ["opt_1","opt_2"]、-3 eq "sales01" 正規化通過；-10 eq 不可讀模組的 opt → 400
```

- [ ] **Step 2: 跑測試確認失敗**

（資料用的 `SeedTask` 新增的 `created`／`completed` 用 `DateTimeKind.Unspecified`：既有 `SeedTime` 是 `Kind=Utc`（`TaskTestBase.cs:17`），Npgsql 寫 `timestamp without time zone` 會丟例外；新測試的基準時間也用 Unspecified，G6。）

Run: `dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~TaskReportSourceTests"`
Expected: 編譯失敗（`TaskReportSource`、`ListForReportAsync` 不存在）。

- [ ] **Step 3: 實作**

`ListForReportAsync`：`TaskVisibilityQuery.Visible(db, v).Include(t => t.Participants)`，`Statuses` 不為 null 時 `Where(t => statuses.Contains(t.Status))`；日期用 `from.ToDateTime(TimeOnly.MinValue)` 與 `to.AddDays(1).ToDateTime(TimeOnly.MinValue)`（`Kind` Unspecified）做半開區間；`due` 用 `DateOnly` 比較；`OrderBy(t => t.Id).Take(take)`。`TaskReportSource.LoadAsync`：取 `visibility.ForCurrentUserAsync`、呼叫 repository、`access.EvaluateManyAsync(items)` 取 `Record?.Accessible == true`、`org.ListUserDepartmentsAsync`（`IOrganizationRepository.cs:20`）查負責人部門（不用 `OrgSnapshotLoader`，它會多載入全部部門與使用者，G3）；`EvaluateManyAsync` 判為 `CanSee=false` 的任務再丟掉（縱深防禦，st1 的子任務可見性進來後兩邊若分歧也 fail closed，G11）。`CatalogAsync`：可讀模組（J14）、這些模組的規則（逐模組 `rules.ListByModuleAsync`，查詢數隨可讀模組數成長，接受；要固定次數再加 `ListByModulesAsync(ids)`，不在 kr1 做，G4）、`org.ListAsync()`，交給 `TaskReportFields.Build`。存檔時照 J23 正規化 `dataset`／`mainModuleKey`（寫在 `ReportRules.Check` 的正規化段）。

- [ ] **Step 4: 跑測試確認通過**（另跑 `TZ=UTC`）。Expected: PASS。

- [ ] **Step 5: Commit**

```bash
git add src/DynamicModules.Application/Dynamic/DemoService/DemoStructure.cs src/DynamicModules.Domain/Repositories/Dynamic/ITaskRepositories.cs src/DynamicModules.Persistence/Repositories/Dynamic/TaskRepositories.cs src/DynamicModules.Application/Dynamic/ReportService/TaskReportSource.cs src/DynamicModules.Application/DependencyInjection.cs src/DynamicModules.Application/Dynamic/ReportService/ReportModuleLoader.cs src/DynamicModules.Application/Dynamic/ReportService/Data/ReportRules.cs tests/DynamicModules.ApplicationTests/Dynamic/Tasks/TaskReportSourceTests.cs tests/DynamicModules.ApplicationTests/Dynamic/Tasks/TaskTestBase.cs
git commit -m "feat(report): 任務資料來源的載入、欄位目錄與定義驗證"
```

### Task 7: `tasks` 資料來源的執行

**Files:**
- Create: `src/DynamicModules.Application/Dynamic/ReportService/ReportRunner.Tasks.cs`
- Modify: `src/DynamicModules.Application/Dynamic/ReportService/ReportRunner.cs`（建構子加 `TaskReportSource taskReports`；`RunAsync` 分派到 `LoadTasksAsync`；`SqlOnly` 的丟例外只在模組來源）
- Test: `tests/DynamicModules.ApplicationTests/Dynamic/Tasks/TaskReportRunnerTests.cs`、`tests/DynamicModules.ApplicationTests/Dynamic/Demo/DemoReportsTests.cs`

**Interfaces:**
- Consumes：Task 5 的 `SourceLoad`／`SourceRows`／`Bound.Restrictable`／`RunContext.BlankLabels`／`ResolveRangeAsync`／`UserNamesAsync`／`ApplyBucket`；Task 6 的 `TaskReportSource`、`TaskReportNarrowing`。
- Produces：`private Task<SourceLoad> LoadTasksAsync(ReportDefinition d, ReportRunOptions o, DateOnly basis, bool frozen, string locale, CancellationToken ct)`；`private ReportCell TaskCell(long code, TaskReportItem item, Module catalog, DateOnly basis)`（Task 8 擴充事件欄）；結果 `Engine = "memory"`、`FallbackReasons = ["datasetMemoryOnly"]`。

做法：`CatalogAsync` → `Side(catalog, TaskReportSource.Permissions)` → 用現有 `Bind` 綁 `AllRefs`（-10、-11 設 `Restrictable`）→ `UnusableMessage`、`FieldWithChangedType` 照跑 → `ResolveRangeAsync` → 縮小條件（J7）→ `LoadAsync(narrowing, MaxLoadRows + 1)`，超過回 `TooMany`（J16）→ 逐任務組格子（-7、-8 用 `TimedCell`；格子判斷順序寫死（R3；判斷錯了會把看不到的記錄的規則名稱或模組可讀性漏出去，`TaskCell` 註解保留這段）：-10 `ModuleId is null` → 空；`!RecordAccessible` → `RestrictedCell`；模組不在選項 → `MissingCell`；其餘 `opt_{ModuleId}`。-11 `RuleId is null` → 空；`!RecordAccessible` → `RestrictedCell`（先於「規則不在選項」，否則記錄看不到、規則屬於可讀模組時會變 ok、帶出規則名稱）；規則不在選項 → `MissingCell`；其餘 `opt_{RuleId}`。分桶走 `ApplyBucket`）→ 篩選與主要日期區間（同模組來源 `Passes("main")` 的語意）→ `MaxRows` 檢查 → `SourceRows(RowModule: catalog, TaskIdOf: 任務 Id 對任務 Id)`。

- [ ] **Step 1: 寫失敗測試**（`TaskReportRunnerTests : TaskTestBase`，`[Trait("Category", "Postgres")]`、`ScopePerRequest => true`；用 `ReportRunner.RunAsync` 直接跑定義，runner 與 `TaskReportSource` 一律在 `using var scope = NewScope()` 取（J20）；資料以 `SeedTask(created:, completed:)` 建，基準日 2026-11-04。`TaskTestBase` 沒有報表 helper，類別內自備（Y6）：`static ReportDefinition TaskDef(string dataset, IReadOnlyList<ReportMeasure> measures, IReadOnlyList<ReportGroup>? groups = null, IReadOnlyList<ReportFilter>? filters = null, ...)`、`static ReportFieldRef T(long code) => new("main", code, null)`、`static ReportMeasure M(string id, string kind, long? field = null, ReportFilter? cond = null)`、`static ReportFilter F(long code, string op, object? value)`，簽章仿 `ReportTestBase`（`ReportRunnerTests.cs:38-50`）。）

```csharp
[Fact] public async Task SpecExample_AvgDelayAndOnTimeRate()
// 設計 §19：sales01 負責，3 件逾期完成（延遲 2、4、6）、2 件準時；
// Def(dataset tasks, filters: 狀態 eq opt_3, groups: [-3], measures: countMain, countIf(完成狀況 eq opt_1), ratio(完成狀況 eq opt_1),
//     avg(-15, 完成狀況 eq opt_2), avg(-15))
// sales01 那組：5、2、0.4、4、2.4；RowMeaning「一筆任務」；Engine "memory"；FallbackReasons == ["datasetMemoryOnly"]
[Fact] public async Task WorkloadShape_GroupsByAssignee_WithUnassigned()
// 待辦、進行中混合、一件未指派；filters 狀態 in [opt_1,opt_2]；countIf 待辦、countIf 進行中、countIf(-12 eq true)
// 未指派那組 GroupLabels[0] == "未指派"；逾期只算待辦／進行中（B1：一件已取消且過期的不算）
[Fact] public async Task CompletionDays_Median()                   // 完成天數 1、3、8 → median 3、avg 4
[Fact] public async Task PermissionMatrix_CountsPerPersona()       // 每個身分 countMain == ListVisibleIdsAsync 筆數（admin、sales01、manager01、ops01、floor01）
[Fact] public async Task GroupByModule_RestrictedOwnGroup_SortedLast()
// manager01 對 tk2 無權限、但 tk2 有他部屬的任務 → 依 -10 分組：「（無權限）」一組排最後、「（無）」是個人任務；restricted 組不含模組名稱
[Theory]
[InlineData(-10, "eq")] [InlineData(-10, "ne")] [InlineData(-10, "in")] [InlineData(-10, "notIn")] [InlineData(-10, "isEmpty")] [InlineData(-10, "isNotEmpty")]
[InlineData(-11, "eq")] [InlineData(-11, "ne")] [InlineData(-11, "in")] [InlineData(-11, "notIn")] [InlineData(-11, "isEmpty")] [InlineData(-11, "isNotEmpty")]
public async Task ModuleOrRuleCondition_RestrictedNeverMatches_EveryOperator(long field, string op)
// 同上資料；條件放 filters 與 countIf 各一次：restricted 的任務在任何運算子下都不計入
[Fact] public async Task DeletedRule_IsMissingGroup()              // 規則刪除後依 -11 分組 → 「（已刪除）」（看得到記錄的任務）
[Fact] public async Task RestrictedRecord_RuleCell_IsRestricted_NotMissingNorOk()
// 記錄看不到的規則任務，兩種情形：規則已刪除、規則屬於觀看者可讀的模組 → 依 -11 分組都是「（無權限）」；
// JsonSerializer.Serialize(result) 不含規則名稱、不含 `opt_{ruleId}`；個人任務（ModuleId null）的 -10 是「（無）」而不是 restricted
[Fact] public async Task EmptySystem_PersonalTasksOnly()
// 不建任何模組、沒有部門：個人任務照樣能跑；依 -4 分組是「（無部門）」；-10、-11 的目錄選項為空，依 -10 分組只有「（無）」（purpose-check 第 5 項）
[Fact] public async Task Creator_RuleTask_IsSystem()               // CreatedBy null → 依 -5 分組標籤「系統」；條件 -5 isEmpty 命中
[Fact] public async Task DateRange_OnCompletedTime_AndWeekBuckets() // primary -8、range thisMonth；依 -8 週分桶分組，週鍵同 ReportDates.BucketKey
[Fact] public async Task SqlOnly_StillRunsInMemory()               // ReportOptions.Engine = SqlOnly → 不丟例外、Engine "memory"
[Fact] public async Task OverLoadLimit_TooMany()                   // ReportOptions.MaxLoadRows = 2、3 件可見 → State "tooMany"、Message 是「資料太多，請縮小條件」
[Fact] public async Task UnknownDataset_ModuleDisabled()           // 定義直接帶 dataset "foo"（繞過驗證）→ State "moduleDisabled"
[Fact] public async Task SavedReport_SharedToGroup_EachViewerOwnNumbers()
// admin 建 tasks 報表分享給業務群組；sales01、sales02 RunReportRequest 各自只算自己看得到的
[Fact] public async Task Card_TaskReport_OnDashboard()               // 任務報表放儀表板卡片（RunCardAsync）：tasks 依 -3 分組有數字；觀看者各算各的（Y10；設計 §2 目標 3）
```

`DemoReportsTests` 加：

```csharp
[Fact] public async Task TaskReport_OverdueUsesBasisDate()
// DemoTestHost；SetGeneratedOn(host, "2026-11-05")；到期 11-04 的待辦 → 是否逾期 true、延遲天數 1，與今天無關；BasisIsFrozen 依今天判斷
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~TaskReportRunnerTests|FullyQualifiedName~DemoReportsTests"`
Expected: 新案例 FAIL（`moduleDisabled`：Task 5 的暫時分派）。

- [ ] **Step 3: 實作** `ReportRunner.Tasks.cs` 與 `RunAsync` 分派（照上方做法）。

- [ ] **Step 4: 跑測試確認通過**，再跑一次 Task 5 的安全網（模組行為不變）與 `TZ=UTC`：

Run: `dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~TaskReport|FullyQualifiedName~ReportRunner|FullyQualifiedName~ReportPermissionMatrix|FullyQualifiedName~ReportSqlEquivalenceTests|FullyQualifiedName~DemoReportsTests|FullyQualifiedName~SafeDeleteTests"`，並跑 Task 5 Step 1 的 WebAPITests 那一行。
Expected: PASS。

- [ ] **Step 5: Commit**

```bash
git add src/DynamicModules.Application/Dynamic/ReportService/ReportRunner.Tasks.cs src/DynamicModules.Application/Dynamic/ReportService/ReportRunner.cs tests/DynamicModules.ApplicationTests/Dynamic/Tasks/TaskReportRunnerTests.cs tests/DynamicModules.ApplicationTests/Dynamic/Demo/DemoReportsTests.cs
git commit -m "feat(report): 任務資料來源可執行，看不到記錄的所屬模組與規則為無權限"
```

---

## kr1c 任務事件、下鑽、匯出、目錄

### Task 8: `taskEvents` 資料來源

**Files:**
- Modify: `src/DynamicModules.Application/Dynamic/ReportService/ReportRunner.Tasks.cs`
- Test: `tests/DynamicModules.ApplicationTests/Dynamic/Tasks/TaskReportRunnerTests.cs`

**Interfaces:**
- Consumes：Task 7 的 `LoadTasksAsync`、`TaskCell`。
- Produces：`taskEvents` 每個任務一列「建立」（-18＝opt_1、-19＝`CreatedTime`），狀態完成且 `CompletedTime` 不為 null 再一列「完成」（-19＝`CompletedTime`）；列的 `MainId` 是 `taskId * 2`（建立）與 `taskId * 2 + 1`（完成），`TaskIdOf` 對回任務 Id；其餘欄位取自該任務。縮小條件的日期欄位用 `createdOrCompleted`。`RowMeaning`「一筆任務事件（建立或完成）」。

- [ ] **Step 1: 寫失敗測試**

```csharp
[Fact] public async Task TaskEvents_OneTaskTwoRows()
// 一件 11-02 建立、11-03 完成；一件 11-03 建立未完成 → countMain 3；countIf(-18 eq opt_1) 2、countIf(-18 eq opt_2) 1
[Fact] public async Task TaskEvents_WeeklyTrend()
// primary -19、range lastWeeks 2；依 -19 week 分組；每週建立件數、完成件數與手算相同；跨週完成的任務兩列落在不同週
[Fact] public async Task TaskEvents_ReopenedKeepsLastCompletionOnly()   // 經 `TaskWriter`（重開會清掉 `CompletedTime`，`TaskWriter.cs:196`）或 `PATCH /tasks/{id}` 走完「完成→重開→再完成」，不用 `SeedTask`（它只能放一個 CompletedTime，等於沒測到，G7）：只有最後一次完成一列（設計 §15）
[Fact] public async Task Card_TaskEvents_ComparePeriods()               // RunCardAsync：taskEvents 依週分組、ComparePeriods=2、countIf 缺值當 0（Y10）
[Fact] public async Task TaskEvents_DoneWithoutCompletedTime_NoCompletionRow() // J15
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~TaskReportRunnerTests"`
Expected: 新案例 FAIL。

- [ ] **Step 3: 實作** 事件列展開與 -18、-19 的格子。

- [ ] **Step 4: 跑測試確認通過**（另跑 `TZ=UTC`）。Expected: PASS。

- [ ] **Step 5: Commit**

```bash
git add src/DynamicModules.Application/Dynamic/ReportService/ReportRunner.Tasks.cs tests/DynamicModules.ApplicationTests/Dynamic/Tasks/TaskReportRunnerTests.cs
git commit -m "feat(report): 任務事件資料來源，一列代表一次建立或完成"
```

### Task 9: 下鑽固定欄、`TaskIds` 與匯出

**Files:**
- Modify: `src/DynamicModules.Application/Dynamic/ReportService/Data/ReportResponses.cs`（`ReportResult` 末尾加 `IReadOnlyList<long>? TaskIds = null`）
- Modify: `src/DynamicModules.Application/Dynamic/ReportService/Data/ReportRules.cs`（`TakeDetail`；`CapDetail` 改用它）
- Modify: `src/DynamicModules.Application/Dynamic/ReportService/ReportRunner.cs`（尾段：任務來源下鑽用 `DrillColumns`、填 `TaskIds`）
- Modify: `src/DynamicModules.Application/Dynamic/ReportService/ReportRunner.Output.cs`（`DetailRows` 同時回排序後的列）
- Modify: `src/DynamicModules.Application/Dynamic/ReportService/ReportRunner.Tasks.cs`（下鑽時另綁 `DrillColumns`）
- Modify: `src/DynamicModules.Application/Dynamic/ReportService/Queries/DrillReport/DrillReportHandler.cs`、`src/DynamicModules.Application/Dynamic/DashboardService/DashboardCardRunner.cs`（截斷改呼叫 `TakeDetail`）
- Test: `tests/DynamicModules.ApplicationTests/Dynamic/Tasks/TaskReportRunnerTests.cs`

**Interfaces:**
- Consumes：Task 7、8 的 `SourceRows.TaskIdOf`。
- Produces：`public static ReportResult ReportRules.TakeDetail(ReportResult r, int n)`（`DetailRows` 與 `TaskIds` 一起截到 n 列，其餘欄不動）；任務來源的下鑽結果 `Columns` 是 B3 六欄、`TaskIds[i]` 對應 `DetailRows[i]`；模組來源 `TaskIds` 為 null。

- [ ] **Step 1: 寫失敗測試**

```csharp
[Fact] public async Task Drill_FixedColumns_WithTaskIds()
// 依 -3 分組的報表下鑽 ["sales01"] → Columns 標籤依序 標題、狀態、負責人、到期日、完成時間、所屬模組；TaskIds 與該組任務 Id 集合相同、與列對齊（用標題對）
// taskEvents 下鑽（Y7）→ Columns 依序 事件種類、事件日期、標題、狀態、負責人、所屬模組；同一任務的建立列與完成列內容不同
[Fact] public async Task Drill_RestrictedGroup_ModuleColumnNoPermission()
// manager01 下鑽 -10 的 "__restricted" 組 → 每列所屬模組是「（無權限）」；DetailRows 不含任何記錄標題或模組名稱
[Fact] public async Task Drill_TaskEvents_MapsToTaskIds()           // 同一任務的建立與完成兩列都對回同一個 Id
[Fact] public async Task Drill_OverLimit_TruncatesTaskIdsToo()      // DrillReportRequest，501 列 → Truncated、DetailRows 與 TaskIds 都是 500；資料用 `AddRange` 一次寫入，不逐筆 `SeedTask`（每筆一次 SaveChanges，G13）
[Fact] public async Task Export_RestrictedCell_NoPermissionText()
// ExportReportRequest（明細欄 -1、-10）→ xlsx 用 ClosedXML 讀：看不到記錄的列「（無權限）」；sales01 沒有任何模組的 PrintExport 也能匯出（U9）
[Fact] public async Task Detail_TaskReport_CanExportTrue()          // GetReportRequest → CanExport true、Definition 條件值未被遮蔽
[Fact] public async Task ModuleDrill_TaskIdsNull()                  // 既有模組報表下鑽 → TaskIds null
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~TaskReportRunnerTests"`
Expected: 新案例 FAIL（下鑽欄位是分組欄而不是六欄；`TaskIds` 不存在）。

- [ ] **Step 3: 實作** `FieldColumnsFor` 在任務來源下鑽時回 `DrillColumns(dataset)`（`ReportFieldRef("main", code, null)`）；`DetailRows` 回 `(values, orderedRows)`，尾段在 `TaskIdOf` 不為 null 時以 `orderedRows` 的 `MainId` 換出 `TaskIds`；三處截斷改 `TakeDetail`。

- [ ] **Step 4: 跑測試確認通過**，加跑儀表板與報表 handler：

Run（儀表板測試都在 WebAPITests，ApplicationTests 的 `Dashboard` filter 比對不到）：
`dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~TaskReport|FullyQualifiedName~ReportRunner"`
`dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~Dashboard|FullyQualifiedName~ReportsApiTests"`
Expected: PASS（`DashboardCardRunner` 的截斷改 `TakeDetail` 要被這個 filter 實際跑到，`--list-tests` 確認）。

- [ ] **Step 5: Commit**

```bash
git add src/DynamicModules.Application/Dynamic/ReportService/Data/ReportResponses.cs src/DynamicModules.Application/Dynamic/ReportService/Data/ReportRules.cs src/DynamicModules.Application/Dynamic/ReportService/ReportRunner.cs src/DynamicModules.Application/Dynamic/ReportService/ReportRunner.Output.cs src/DynamicModules.Application/Dynamic/ReportService/ReportRunner.Tasks.cs src/DynamicModules.Application/Dynamic/ReportService/Queries/DrillReport/DrillReportHandler.cs src/DynamicModules.Application/Dynamic/DashboardService/DashboardCardRunner.cs tests/DynamicModules.ApplicationTests/Dynamic/Tasks/TaskReportRunnerTests.cs
git commit -m "feat(report): 任務報表下鑽固定列任務並附任務 Id，匯出無權限格子"
```

### Task 10: 資料來源目錄端點、WebAPI 驗收與文件寫回

**Files:**
- Create: `src/DynamicModules.Application/Dynamic/ReportService/Queries/GetReportCatalog/GetReportDatasetsRequest.cs`、`GetReportDatasetsHandler.cs`、`GetReportDatasetCatalogRequest.cs`、`GetReportDatasetCatalogHandler.cs`（慣例 request 與 handler 分檔，樣板 `GetReportCatalogRequest.cs`／`GetReportCatalogHandler.cs`，G9）
- Modify: `src/DynamicModules.Application/Dynamic/ReportService/Queries/GetReportCatalog/GetReportCatalogHandler.cs`（`ToField` 改 `internal static`）
- Modify: `src/DynamicModules.WebAPI/Controllers/v3/Dynamic/ReportsController.cs`（`GET datasets`、`GET datasets/{dataset}/catalog`）
- Test: `tests/DynamicModules.WebAPITests/Dynamic/TaskReportsApiTests.cs`
- Docs: `docs/superpowers/specs/2026-11-13-task-board-reports-gantt-design.md`（§7.3 代碼 -18、-19 與 B1／B2、§7.5 下鑽 B3、§7.4 欄名 B4、§12 目錄端點、§14 本計畫的可寫死清單、§17.3 加 J 系列中改變設計的幾條（J1、J2、J11、J12）、§18 本計畫的矛盾 1–6）、`docs/roadmap.md` 第 46 列、`docs/feature.md` 第 228 行（kr1 部分完成的說明，跑 `python3 ~/.claude/plugins/marketplaces/plugharbor/plugins/lifecycle/scripts/feature_checklist.py docs/feature.md --write`，路徑依 `.claude/rules/docs.md:9`，G1）；§15 已知限制加：分享給群組的報表若帶「-10＝觀看者不可讀的模組」，詳情會回 `opt_{moduleId}`（只露 Id、不露名稱，`ReportMasking` 對任務欄位一律視為看得到，G14）；§14「安全」加規則名稱揭露（J22）

**Interfaces:**
- Consumes：Task 6 的 `TaskReportSource.CatalogAsync`、`TaskReportFields.Selectable`。
- Produces：`GET /api/v3/dynamic/reports/datasets?locale=` → `[{ "dataset": "tasks", "name": "任務" }, { "dataset": "taskEvents", "name": "任務事件（建立或完成）" }]`；`GET /reports/datasets/{dataset}/catalog?locale=` → `ModuleCatalog(Fields, [], [])`，只列 `Selectable(dataset)`，順序照代碼由 -1 往下；未知 dataset → 404。

- [ ] **Step 1: 寫失敗測試**（`TaskReportsApiTests`，`[Trait("Category", "Postgres")]`，照 `ReportsApiTests` 的 `TestApp`／`ApiTestKit` 寫法；任務用既有任務 API 建，完成用 `PATCH /tasks/{id}`）

```csharp
[Fact] public async Task Datasets_ListAndCatalog()
// GET datasets → 兩筆；GET datasets/tasks/catalog → 15 個欄位、-1 canGroup=false、-2 options 4 個；
// GET datasets/taskEvents/catalog 不含 -12；GET datasets/foo/catalog → 404
[Fact] public async Task TaskReport_EndToEnd_Http()
// POST /reports（dataset tasks、groups -3、measures countMain＋countIf 狀態待辦）→ 201；POST /reports/{id}/run → state ok、engine memory；
// POST /reports/{id}/drill → taskIds 長度＝detailRows；GET /reports/{id}/export → 200 xlsx；
// POST /reports/preview 同定義 → 與 run 相同 total
// 點任務伺服器再驗（Y11）：拿 taskIds[i] 呼叫 GET /tasks/{id}，觀看者回 200；restricted 那列的 record.accessible=false 且回應不含模組代號與記錄 Id；換一個看不到該任務的使用者回 404
[Fact] public async Task TaskReport_OtherUser_SeesOwnNumbers_Http()   // 分享給群組後另一個使用者 run 的 total 只含他看得到的
[Fact] public async Task ModuleCatalog_Unchanged_Http()               // GET /reports/catalog 的回應不含 tasks（J11）
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~TaskReportsApiTests"`
Expected: `Datasets_ListAndCatalog` FAIL（404）；`TaskReport_EndToEnd_Http` 已可能通過（kr1b 的成果），照實記錄。

- [ ] **Step 3: 實作** handler 與兩個端點；寫回上列文件。

- [ ] **Step 4: 跑測試確認通過**

Run: `dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~TaskReportsApiTests|FullyQualifiedName~ReportsApiTests|FullyQualifiedName~ReportCatalogApiTests"`
Expected: PASS。推分支後等 CI 跑完整 gate；`formwright-purpose-check` 一次；外洩掃描交給 `formwright-ship`。

- [ ] **Step 5: Commit**

```bash
git add src/DynamicModules.Application/Dynamic/ReportService/Queries/GetReportCatalog/GetReportDatasetsRequest.cs src/DynamicModules.Application/Dynamic/ReportService/Queries/GetReportCatalog/GetReportDatasetsHandler.cs src/DynamicModules.Application/Dynamic/ReportService/Queries/GetReportCatalog/GetReportDatasetCatalogRequest.cs src/DynamicModules.Application/Dynamic/ReportService/Queries/GetReportCatalog/GetReportDatasetCatalogHandler.cs src/DynamicModules.Application/Dynamic/ReportService/Queries/GetReportCatalog/GetReportCatalogHandler.cs src/DynamicModules.WebAPI/Controllers/v3/Dynamic/ReportsController.cs tests/DynamicModules.WebAPITests/Dynamic/TaskReportsApiTests.cs
git commit -m "feat(report): 任務資料來源目錄端點與 API 驗收測試"
git add docs/superpowers/specs/2026-11-13-task-board-reports-gantt-design.md docs/roadmap.md docs/feature.md
git commit -m "docs: kr1 任務報表資料來源的裁決寫回設計、路線圖與功能清單"
```

---

## 矛盾（設計與程式、設計內部）

1. **下鑽的「相關記錄」與「不提供記錄標題」**：§7.5 說下鑽列「相關記錄（§4.3 的顯示）」，§7.3 說報表不提供記錄標題、「報表永遠拿不到記錄內容」。本計畫取後者，下鑽用「所屬模組」代替（B3），記錄標題留在任務詳細。
2. **PostgreSQL 雙跑**：§3、§7.7 寫「pg0 之後」「新測試放進雙跑集」，但 roadmap 的 pg0a、pg0 都「未開始」，沒有 PostgreSQL 測試環境；只能先標 `Category=Postgres`（J17），PostgreSQL 實際行為標「未驗證」，pg0c 時隨雙跑集一起跑。
3. **下鑽上限「沿用 A24」**：§7.5 寫筆數上限沿用 A24，但 A24 屬 p2（未開始），現在是 `ReportRules.DrillLimit = 500` 常數；kr1 沿用這個單一來源，p2 改成政策時任務下鑽自動跟上。
4. **`GET /reports/catalog` 多回系統資料來源**：§12 要它多回兩個資料來源，但它現在回的是模組清單、設計器直接當主模組選單；kr1 改開新端點、不動舊端點（J11），kr2 合併。
5. **「是否逾期」與取消**：§7.3「未完成且到期日早於基準日」加上「取消也是未完成」，字面上讓取消的任務算逾期，與看板「逾期」欄（只含待辦、進行中）不一致，碰到反目標；本計畫照 B1 只算待辦、進行中。
6. **量值沒有名稱**：§8.1 的內建報表量值有各自名稱（待辦件數、進行中件數…），但 `ReportMeasure` 沒有名稱欄位，同一欄位的兩個 `countIf` 欄名相同（B4 只能做到「狀態 符合條件筆數」）；kr3 要補量值自訂名稱（建議 `ReportMeasure.Label`，四語 `I18nText`），kr1 不做。
7. **事件欄位沒有代碼**：§7.1 說欄位用固定負數代碼，§7.3 的 `taskEvents` 事件種類、事件日期沒給；本計畫定 -18、-19，-16、-17 保留給 st1、gt1（J4）。
8. **§3 的既有事實已過時**：§3 寫 `TaskAccess.EvaluateManyAsync` 逐筆查 stub，kb1 已改成一次批次取 stub、模組權限依模組快取；kr1 直接用批次版本（J6），設計文件寫回時一併更正。§20 kr1 列的「若 U8 擴充報表分享也在這段」已因 U8 裁決不擴充而作廢。
