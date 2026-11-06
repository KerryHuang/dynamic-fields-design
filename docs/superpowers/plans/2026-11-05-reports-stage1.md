# 報表第一段（查詢引擎與報表）Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 一般使用者能用五步驟精靈組報表、合併一個子模組、看表格與圖表、下鑽、匯出、分享給群組，數字一律照觀看者權限計算。

**Architecture:** 純計算放 Domain（`Reports/`，不碰資料庫，用手算預期值測試）；Application 的 `ReportRunner` 負責授權、載入記錄、組出列，再交給計算器。定義存 JSON，欄位以 ID 引用。前端照既有 queries／pages 分層。

**Tech Stack:** .NET 8、MediatR、EF Core SQLite、FluentValidation、xUnit＋FluentAssertions；Vue 3、Quasar、TanStack Query、vitest。新增 `echarts`（Apache-2.0，免費可商用、持續維護）畫圖。

**Spec:** [`docs/superpowers/specs/2026-11-05-reports-design.md`](../specs/2026-11-05-reports-design.md)。第二段計畫：[`2026-11-05-reports-stage2.md`](2026-11-05-reports-stage2.md)。

## Global Constraints

- 所有路由放在 `api/v3/dynamic/` 下（`EndpointRoutesTests` 會驗）。
- 不寫 SQLite 專屬語法，不用 `json_extract`；彙總在 C# 內以 decimal 計算。
- 每次讀記錄都帶 `DeleteFlag = false`；不可只查 `RecordIndex`（軟刪除不清索引）。
- 權限只用 `IDynamicAccessGuard`，不另寫一套。
- `Reports:MaxRows` 預設 50,000；下鑽最多 500 列。
- 全新安裝沒有任何報表。
- 文案走四語系 i18n（zh-TW、en-US、th、vi）；後端訊息照現有慣例寫繁中。
- 不可複製本機自助報表參考專案的文字、欄位名稱或設定檔；不得出現真實公司名稱（repo 是公開的）。
- commit 逐檔指名 add；trailer 用 `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`。

## Review Focus

每條都有測試釘在所屬 task：

1. 合併報表彙總主模組欄位時，結果帶 `dedupedNote`，畫面顯示說明（Task 2、Task 7）。
2. 條件或排序用到隱藏欄位時整張回 `restricted`，不是默默略過條件（Task 4）。
3. 別人分享的報表，對方權限較少時回 `restricted` 且訊息不洩漏隱藏欄位名稱（Task 4）。
4. 彙總欄位及間接引用它的公式，從欄位清單、帶出欄位、定義驗證都排除（Task 3、Task 6）。
5. 軟刪除的主記錄與子記錄都不計入（Task 4）。

---

### Task 1: 資料表與定義格式

**Files:**
- Create: `poc/backend/src/DynamicModules.Domain/Entities/Dynamic/Report.cs`、`.../ReportShare.cs`
- Create: `poc/backend/src/DynamicModules.Domain/Reports/ReportDefinition.cs`
- Create: `poc/backend/src/DynamicModules.Domain/Data/Configurations/ReportConfiguration.cs`
- Create: `poc/backend/src/DynamicModules.Domain/Repositories/Dynamic/IReportRepository.cs`
- Create: `poc/backend/src/DynamicModules.Persistence/Repositories/Dynamic/ReportRepository.cs`；Modify: `DependencyInjection.cs`
- Modify: 刪除群組的 repository 實作（`IAccountRepository.DeleteGroupAsync`）與 impact 查詢
- Migration：`dotnet ef migrations add Reports`
- Test: `poc/backend/tests/DynamicModules.DomainTests/Reports/ReportDefinitionTests.cs`、`poc/backend/tests/DynamicModules.WebAPITests/AdminGroupsApiTests.cs`

**Interfaces:**
- Produces:
  - `Report { long Id; string OwnerUserId; string Name; string DefinitionJson; string RowVersion; List<ReportShare> Shares }`，繼承 `AuditableEntity`。`ReportShare { long ReportId; long GroupId }`，複合主鍵。
  - `ReportDefinition`（`System.Text.Json` 序列化，camelCase）：
    ```csharp
    record ReportDefinition(int Version, string MainModuleKey, ReportChild? Child,
        List<ReportFieldRef> Columns, List<ReportGroup> Groups, List<ReportMeasure> Measures,
        List<ReportFilter> Filters, ReportFieldRef? PrimaryDateField, ReportDateRange? DateRange,
        List<ReportSort> Sort, string Viz);
    record ReportChild(string ModuleKey, long LinkFieldId);
    record ReportFieldRef(string Source /* main|child */, long FieldId, long? ViaFieldId);
    record ReportGroup(ReportFieldRef Field, string? Bucket /* day|week|month|quarter|year */);
    record ReportMeasure(string Id, string Kind /* sum|avg|min|max|countMain|countChild|ratio */, ReportFieldRef? Field, ReportFilter? Condition);
    record ReportFilter(ReportFieldRef Field, string Op, JsonElement? Value);
    record ReportDateRange(string Kind /* today|thisWeek|lastWeek|thisMonth|lastMonth|thisYear|lastYear|lastDays|lastWeeks|lastMonths */, int? N);
    record ReportSort(string Key, bool Desc);
    ```
    `Viz` 為 `table|indicator|line|bar|donut`。`Filters` 是 AND 清單，運算子沿用 `FilterOperator`；不做 any／巢狀（我的判斷，五步驟 UI 不需要，之後放寬不改既有定義）。`Version` 目前為 1。
  - `IReportRepository`：`GetAsync(long id, ct)`、`ListVisibleAsync(string userId, IReadOnlyCollection<long> groupIds, ct)`、`Add(Report)`、`Remove(Report)`。

- [ ] **Step 1: 寫失敗測試。** `ReportDefinition_RoundTrips_AllFields`：完整定義序列化再還原後相等。`DeleteGroup_RemovesReportShares_AndImpactCountsThem`：分享給群組的報表，刪除群組前的 impact 列出它，刪除後 `ReportShare` 清掉，報表本身還在。
- [ ] **Step 2: 跑測試確認失敗。** `dotnet test poc/backend --filter "ReportDefinition|DeleteGroup_RemovesReportShares"`，預期編譯失敗或 FAIL。
- [ ] **Step 3: 實作實體、設定、repository、migration，把 `ReportShare` 納入群組刪除與 impact。** `Group` 刪除現有涵蓋 5 張表，這是第 6 張。
- [ ] **Step 4: 跑測試確認通過。**
- [ ] **Step 5: Commit。** `feat(reports): 報表資料表與定義格式`

### Task 2: 計算器（去重、分組、總計）

**Files:**
- Create: `poc/backend/src/DynamicModules.Domain/Reports/ReportRow.cs`、`ReportCalculator.cs`
- Test: `poc/backend/tests/DynamicModules.DomainTests/Reports/ReportCalculatorTests.cs`

**Interfaces:**
- Consumes: Task 1 的 `ReportDefinition` 系列。
- Produces:
  ```csharp
  record ReportCell(string State /* ok|missing|restricted|empty */, decimal? Number, string? Text, DateOnly? Date, IReadOnlyList<string>? Multi);
  record ReportRow(long MainId, long? ChildId, IReadOnlyDictionary<string, ReportCell> Cells); // key 為 ReportCalculator.KeyOf(fieldRef)
  record ReportGroupResult(IReadOnlyList<string> GroupKeys, IReadOnlyList<string> GroupLabels, IReadOnlyDictionary<string, decimal?> Values);
  record ReportCalcResult(IReadOnlyList<ReportGroupResult> Groups, IReadOnlyDictionary<string, decimal?> Total, bool DedupedNote, int RowCount);
  static ReportCalcResult ReportCalculator.Calculate(IReadOnlyList<ReportRow> rows, IReadOnlyList<ReportGroup> groups, IReadOnlyList<ReportMeasure> measures, bool merged);
  static string ReportCalculator.KeyOf(ReportFieldRef f);
  ```

規則（spec §6.3）：`Source == "main"` 的量值（含 `ViaFieldId`）在每個分組內以 `MainId` 去重；`child` 的量值逐列算。`Total` 獨立重算，不是把各組相加。多選欄位每個選項一組。空值不計入加總與平均；`missing`、`restricted` 各自成一組。`DedupedNote` 為 true 的條件：`merged` 且有主模組欄位的 `sum` 或 `avg`。

- [ ] **Step 1: 寫失敗測試，資料與預期值都手算。**
  ```csharp
  // 訂單 A 金額 100、3 筆出貨（數量 1,2,3）；訂單 B 金額 50、無出貨（左合併保留一列）
  Sum_MainField_MergedThreeChildren_CountsOnce        // 不分組：金額 150，出貨數量 6
  Avg_MainField_Merged_DedupesByMain                   // 平均金額 75
  GroupByChildField_ParentInTwoGroups_TotalIsNotSum    // A 出貨到倉 X、Y：X=100、Y=100、Total=100
  CountMain_And_CountChild                             // countMain=2、countChild=3
  Ratio_MainCondition_DedupesByMain                    // 金額>=100 的訂單占比 0.5
  Ratio_ChildCondition_CountsChildRows                 // 數量>=2 的出貨占比 2/3
  EmptyValues_NotCounted_InSumOrAvg
  MultiSelect_GroupsPerOption_DedupesInEachGroup
  MissingAndRestricted_FormOwnGroups_NotMixed
  DedupedNote_TrueOnlyWhenMergedAndMainSumOrAvg        // 未合併 false；合併但只 countChild false
  Decimal_NoFloatDrift                                 // 0.1 + 0.2 == 0.3m
  ```
- [ ] **Step 2: 跑測試確認失敗。** `dotnet test poc/backend/tests/DynamicModules.DomainTests --filter ReportCalculator`
- [ ] **Step 3: 實作 `ReportCalculator`。** 純函式，不依賴資料庫。
- [ ] **Step 4: 跑測試確認通過。**
- [ ] **Step 5: Commit。** `feat(reports): 計算器與去重規則`

### Task 3: 日期區間與欄位資格

**Files:**
- Create: `poc/backend/src/DynamicModules.Domain/Reports/ReportDates.cs`、`ReportFieldEligibility.cs`
- Test: `poc/backend/tests/DynamicModules.DomainTests/Reports/ReportDatesTests.cs`、`ReportFieldEligibilityTests.cs`

**Interfaces:**
- Produces:
  - `static (DateOnly From, DateOnly ToInclusive) ReportDates.Resolve(ReportDateRange r, DateOnly basis)`
  - `static string ReportDates.BucketKey(DateOnly d, string bucket)`：`2026-11-05`、`2026-W45`（ISO 週）、`2026-11`、`2026-Q4`、`2026`。
  - `static bool ReportDates.Contains((DateOnly, DateOnly) range, ReportCell cell)`：比對日期值，日期時間以台北牆上時間的日期部分比較，所以隔天 00:00 不屬於前一天。
  - `static IReadOnlySet<long> ReportFieldEligibility.ExcludedFieldIds(Module m)`：彙總欄位，加上經 `FormulaGraph.RefsOf` 直接或間接引用彙總欄位的公式（依 `FormulaGraph.EvaluationOrder` 走）。
  - `static bool CanGroup(Field f)`（Textarea 否）、`CanSum(Field f)`（Integer、Decimal、Currency、Percent、數值結果公式）、`CanMinMax(Field f)`（CanSum 或 Date、DateTime、日期結果公式）。

- [ ] **Step 1: 寫失敗測試。** 基準日 2026-11-05（週四）：`ThisWeek` 為 11-02～11-08（週一開始）、`LastWeek` 10-26～11-01、`ThisMonth` 11-01～11-30、`LastMonth` 10-01～10-31、`LastDays(7)` 10-30～11-05、`LastWeeks(8)` 含本週共 8 週、`LastMonths(12)` 含本月共 12 個月。`Year_Boundary`：基準日 2027-01-01 的上月是 2026-12。`DateTime_NextDayMidnight_NotInRange`。`Excluded_Rollup_And_IndirectFormula`：公式 A 引用公式 B、B 引用彙總欄位，三者都排除；不引用的公式不排除。
- [ ] **Step 2: 跑測試確認失敗。**
- [ ] **Step 3: 實作。**
- [ ] **Step 4: 跑測試確認通過。**
- [ ] **Step 5: Commit。** `feat(reports): 日期區間與欄位資格`

### Task 4: ReportRunner（授權、載入、組列）

**Files:**
- Create: `poc/backend/src/DynamicModules.Application/Dynamic/ReportService/ReportRunner.cs`、`Data/ReportResponses.cs`、`ReportOptions.cs`
- Modify: `poc/backend/src/DynamicModules.Domain/Repositories/Dynamic/IRecordRepository.cs`、`poc/backend/src/DynamicModules.Persistence/Repositories/Dynamic/RecordRepository.cs`
- Test: `poc/backend/tests/DynamicModules.ApplicationTests/Dynamic/ReportRunnerTests.cs`、`ReportPermissionMatrixTests.cs`；`.../Persistence/ReportBenchmarkTests.cs`（`Category=Benchmark`）

**Interfaces:**
- Consumes: Task 1–3；`IDynamicAccessGuard.AuthorizeAsync`、`AuthorizeDetailAsync`、`ReferenceResolver`、`IBusinessTime`。
- Produces:
  - `IRecordRepository.ListForReportAsync(long moduleId, IReadOnlyCollection<string>? ownerUserIds, int limit, CancellationToken ct) : Task<IReadOnlyList<Record>>`，`AsNoTracking`、未刪除、不分頁，最多 `limit` 筆。
  - `IRecordRepository.ListByIdsAsync(long moduleId, IReadOnlyCollection<long> ids, CancellationToken ct) : Task<IReadOnlyList<Record>>`，未刪除，供多對一帶欄位。
  - `ReportResult { string State; string? Message; string RowMeaning; IReadOnlyList<ReportColumn> Columns; IReadOnlyList<ReportGroupResult>? Groups; IReadOnlyDictionary<string, decimal?>? Total; IReadOnlyList<IReadOnlyList<object?>>? DetailRows; bool DedupedNote; DateOnly BasisDate; ReportAppliedRange? AppliedRange }`。`State` 為 `ok|empty|restricted|fieldMissing|moduleDisabled|tooMany`，HTTP 一律 200。
  - `ReportRunner.RunAsync(ReportDefinition d, ReportRunOptions o, CancellationToken ct) : Task<ReportResult>`；`ReportRunOptions(ReportDateRange? RangeOverride, IReadOnlyList<string>? DrillGroupKeys)`。第二段會加欄位。
  - `ReportRunner.BasisDateAsync(ct) : Task<DateOnly>`，此 task 回傳 `IBusinessTime.Today`，Task 9 再加 Demo 分支。

判斷順序：模組不存在或停用 → `moduleDisabled`；模組無 Read → `restricted`；定義引用的欄位已刪除或停用 → `fieldMissing`；任何位置（欄位、分組、量值、條件、排序、帶出、主要日期）用到看不到的欄位，或用到 `ExcludedFieldIds` → `restricted`。明細子模組用 `AuthorizeDetailAsync`；非明細子模組與參照目標模組各自 `AuthorizeAsync`。子模組不可讀 → `restricted`（使用者選了就是要看，不默默當作沒有子記錄）。參照目標模組不可讀 → 該欄位所有值 `restricted`。**帶出欄位的目標記錄不在範圍內 → 該列 `restricted`；已刪除 → `missing`。** 子記錄依子模組範圍過濾。主記錄或合併後列數超過 `MaxRows` → `tooMany`，不回部分結果。`restricted` 的 `Message` 只寫看得到的模組與欄位名，看不到的欄位寫「隱藏欄位」。

條件先套主模組與其帶出欄位，再過濾子記錄，子條件把子記錄全篩掉時該主記錄的列消失（spec §6.2）；沒有子記錄且沒有子條件時保留一列。

- [ ] **Step 1: 寫失敗測試。** 用 `DesignerTestBase` 建訂單、出貨、客戶三個模組：
  - `Run_MergedOrderShipment_DedupesAmount`：同 Task 2 的數字，走真實資料庫。
  - `Run_SoftDeleted_MainAndChild_NotCounted`
  - `Run_ViaReference_GroupsByCustomerRegion`；`Run_ViaReference_TargetDeleted_FormsMissingGroup`；`Run_ViaReference_TargetOutOfScope_FormsRestrictedGroup_WithoutValue`
  - `Run_ChildFilterRemovesAllChildren_DropsParentRow`；`Run_NoChildren_KeepsParentRow`
  - `Run_TooManyRows_ReturnsTooMany_NoPartialResult`（`MaxRows` 設 2）
  - `Run_FieldDeleted_ReturnsFieldMissing`；`Run_ModuleDisabled_ReturnsModuleDisabled`
  - `Run_DetailMerge_InheritsHeaderScope`
- [ ] **Step 2: 寫權限矩陣失敗測試 `ReportPermissionMatrixTests`。** 以 `[Theory]` 跑「隱藏欄位放在 欄位／分組／量值／條件／排序／帶出／主要日期／量值條件」八個位置，每個都回 `restricted`，且 `Message` 不含該欄位名稱。另測：`Scope_Own_vs_All_DifferentNumbers`（業務本人 vs 主管同一定義數字不同）、`Pricing_Missing_CurrencyField_Restricted`、`ChildModule_NoRead_Restricted`、`RollupField_Restricted`、`NoModuleRead_Restricted`。
- [ ] **Step 3: 跑測試確認失敗。** `dotnet test poc/backend/tests/DynamicModules.ApplicationTests --filter "ReportRunner|ReportPermissionMatrix"`
- [ ] **Step 4: 實作 repository 方法與 `ReportRunner`。** 值解析重用 `FieldDisplay` 與各型別 handler；使用者欄位顯示 `AppUser.DisplayName`，選項依目前語系顯示標籤、依選項順序排序。
- [ ] **Step 5: 跑測試確認通過。**
- [ ] **Step 6: 加效能基準。** 1 萬筆主記錄、每筆 3 列子記錄、含分組與去重，取 5 次中位數，斷言 < 1000 ms，結果記到 `docs/acceptance/reports.md`。`dotnet test --filter Category=Benchmark`。若未達標，停在這裡回報，不先做快取。
- [ ] **Step 7: Commit。** `feat(reports): ReportRunner 授權與組列`

### Task 5: 報表 API（CRUD、分享、執行、下鑽、匯出）

**Files:**
- Create: `poc/backend/src/DynamicModules.Application/Dynamic/ReportService/Commands/{CreateReport,UpdateReport,DeleteReport,CopyReport,SetReportShares}/`、`Queries/{GetReports,GetReport,RunReport,PreviewReport,DrillReport,ExportReport}/`、`Data/ReportRules.cs`
- Create: `poc/backend/src/DynamicModules.WebAPI/Controllers/v3/Dynamic/ReportsController.cs`
- Test: `poc/backend/tests/DynamicModules.WebAPITests/ReportsApiTests.cs`

**Interfaces:**
- Consumes: Task 1、Task 4；`XlsxWriter`、`FieldDisplay`。
- Produces（路由都在 `api/v3/dynamic` 下）：
  | 方法與路由 | 用途 |
  |---|---|
  | `GET reports` | 我的＋分享給我（系統管理員另含所有有分享的報表）；每筆含 `id, name, ownerName, isOwner, sharedGroupIds, mainModuleKey` |
  | `POST reports`、`PUT reports/{id}`、`DELETE reports/{id}` | 只有擁有者；系統管理員可刪有分享的 |
  | `GET reports/{id}` | 回定義（唯讀給被分享者） |
  | `POST reports/{id}/copy` | 另存一份，擁有者換成自己，不帶分享 |
  | `PUT reports/{id}/shares` | body `{groupIds}`，缺欄位回 400 |
  | `POST reports/{id}/run` | body `{range?}` |
  | `POST reports/preview` | body 為定義，未存檔試跑 |
  | `POST reports/{id}/drill` | body `{groupKeys, range?}`，最多 500 列，回 `truncated` |
  | `GET reports/{id}/export` | xlsx；任一涉及模組缺 PrintExport 回 403 |

看不到的報表一律 404。`ReportRules.Check` 在存檔時驗：名稱必填上限 100、`Version == 1`、模組與欄位存在且不是 `ExcludedFieldIds`、`Sum`／`Avg` 限 `CanSum`、分組限 `CanGroup`、主模組不是明細、`LinkFieldId` 是該子模組指回主模組的參照或明細欄位、`ViaFieldId` 是參照欄位且不再往下鏈、至少一個欄位或量值。**存檔者要看得到定義裡所有欄位**（`CanSee`），不能把無權看的欄位存進定義。Task 2 的 `DedupedNote` 原樣回傳。

- [ ] **Step 1: 寫失敗測試。** `Share_OtherGroupMember_CanRunReadOnly_CannotEdit`；`NotShared_Returns404`；`Copy_NewOwner_NoShares`；`SharedReport_ViewerHasLessPermission_SameDefinition_DifferentNumbers`；`SharedReport_ViewerLacksField_RestrictedState_NotError`；`Save_WithRollupField_400`；`Save_FieldViewerCannotSee_400`；`Save_ChainedVia_400`；`Save_DetailAsMain_400`；`SetShares_MissingGroupIds_400`；`Export_WithoutPrintExportOnChild_403`；`Export_NumbersMatchRun`；`Drill_RowsRespectScope_AndCapAt500`；`Delete_NonOwner_403`；`SystemAdmin_CanDeleteSharedReport`；`Deactivated_OwnerReport_StaysShared`；`Unauthenticated_401`。
- [ ] **Step 2: 跑測試確認失敗。** `dotnet test poc/backend/tests/DynamicModules.WebAPITests --filter ReportsApi`
- [ ] **Step 3: 實作。** 寫入類是 `ITransactionalRequest`，照 `ViewService` 分層。
- [ ] **Step 4: 跑測試確認通過。**
- [ ] **Step 5: Commit。** `feat(reports): 報表 API`

### Task 6: 設計器用的欄位目錄 API

**Files:**
- Create: `poc/backend/src/DynamicModules.Application/Dynamic/ReportService/Queries/GetReportCatalog/`；Modify: `ReportsController.cs`
- Test: `poc/backend/tests/DynamicModules.WebAPITests/ReportCatalogApiTests.cs`

**Interfaces:**
- Produces:
  - `GET reports/catalog`：`[{ key, name }]`，有 Read 且可當主模組（非明細、啟用）的模組。
  - `GET reports/catalog/{moduleKey}`：`{ fields: [CatalogField], via: [{ fieldId, label, targetKey, fields: [CatalogField] }], children: [{ moduleKey, name, linkFieldId, linkLabel, isDetail, fields, via }] }`。
  - `CatalogField { id, label, type, canGroup, canSum, canMinMax, isDate, options? }`。只列 `CanSee` 且不在 `ExcludedFieldIds` 的欄位。一般參照的目標模組不可讀時不列；實體目標只列標題。

- [ ] **Step 1: 寫失敗測試。** `Catalog_ExcludesHiddenAndRollupAndIndirectFormula`；`Catalog_ListsDetailAndBackReferenceChildren`；`Catalog_ModuleWithoutRead_NotListed`；`Catalog_DetailModule_NotAMainOption`；`Catalog_ViaTargetNoRead_NotListed`；`Catalog_EntityTarget_TitleOnly`。
- [ ] **Step 2: 跑測試確認失敗。**
- [ ] **Step 3: 實作。** 用 `ListIncomingReferenceFieldsAsync` 與 `ListDetailModulesAsync`。
- [ ] **Step 4: 跑測試確認通過。**
- [ ] **Step 5: Commit。** `feat(reports): 設計器欄位目錄`

### Task 7: 前端報表清單與結果頁

**Files:**
- Create: `poc/frontend/src/application/dynamic/queries/reports-queries.ts`、`poc/frontend/src/domain/dynamic/report-types.ts`
- Create: `poc/frontend/src/pages/reports/ReportListPage.vue`、`ReportViewPage.vue`
- Create: `poc/frontend/src/presentation/reports/ReportResultView.vue`、`ReportChart.vue`、`DrillDialog.vue`、`ShareDialog.vue`
- Modify: `router/routes.ts`、`layouts/MainLayout.vue`、`services/api/dynamic-api.ts`、四語系 JSON、`poc/frontend/package.json` 與 `bun.lock`
- Test: 同目錄 `*.vitest.spec.ts`

**Interfaces:**
- Consumes: Task 5。
- Produces: 路由 `/reports`、`/reports/new`、`/reports/:id(\d+)`、`/reports/:id(\d+)/edit`；`ReportResultView` props `{ result: ReportResult; viz: string }`，事件 `drill(groupKeys)`。第二段的卡片直接重用它。選單新增「報表」區，`data-testid="reports-menu"`，所有登入者可見。

`ReportResultView` 依 `state` 顯示 spec §9 的文字；`ok` 時畫表格或圖表。`dedupedNote` 為 true 時固定顯示「主模組的金額等欄位每筆只算一次，明細列相加不等於這個數字」，下鑽視窗也顯示。`ReportChart` 包 `echarts`，只引入 bar、line、pie，測試時 mock。`AppliedRange` 與 `RowMeaning` 顯示在結果上方。

- [ ] **Step 1: 寫失敗測試。** `shows dedupe note when dedupedNote`；每個 `state` 一個顯示測試，`restricted` 顯示後端 message；`shared report hides edit and shows save as copy`；`export disabled when forbidden`（403 顯示訊息）；`share dialog sends selected groups`；`menu shows reports`；`drill truncated shows notice`。
- [ ] **Step 2: 跑測試確認失敗。** `cd poc/frontend && bunx vitest run src/presentation/reports src/pages/reports`
- [ ] **Step 3: 安裝 `echarts`（選兩週前以上發布的確切版本），實作。**
- [ ] **Step 4: 跑 vitest 與 `bunx vue-tsc --noEmit`。**
- [ ] **Step 5: Commit。** `feat(frontend): 報表清單與結果頁`

### Task 8: 五步驟設計器

**Files:**
- Create: `poc/frontend/src/pages/reports/ReportDesignerPage.vue`、`poc/frontend/src/presentation/reports/designer/{StepSource,StepFields,StepFilters,StepGroups,StepPreview}.vue`、`designer-state.ts`
- Modify: `reports-queries.ts`、四語系 JSON
- Test: `designer-state.vitest.spec.ts`、`ReportDesignerPage.vitest.spec.ts`

**Interfaces:**
- Consumes: Task 5 的 preview、Task 6 的 catalog、Task 7 的 `ReportResultView`。
- Produces: `designer-state.ts`：`toDefinition(state): ReportDefinition`、`fromDefinition(def, catalog): state`、`canAdvance(state, step): boolean`。

五步：選資料、選欄位、設條件、分組與彙總、預覽與儲存（spec §4）。第 1 步選了子模組才顯示合併欄位；第 2 步把欄位分成主模組、帶出、子模組三群；第 3 步的運算子取自 `poc/shared/filter-operators.json`，含相對日期與主要日期欄位；第 4 步依 `canSum`、`canMinMax`、`canGroup` 限制選項；**第 4 步選了會去重的量值時就顯示去重說明**。第 5 步打 `reports/preview`，存檔前能切五種呈現。

- [ ] **Step 1: 寫失敗測試。** `toDefinition/fromDefinition round trip`；`cannot advance without main module`；`sum only offered for canSum fields`；`changing main module clears dependent fields`；`dedupe note appears at group step when merged and main sum`；`preview posts current definition`；`save posts and navigates`；`definition referencing removed field shows warning and blocks save until removed`。
- [ ] **Step 2: 跑測試確認失敗。**
- [ ] **Step 3: 實作。**
- [ ] **Step 4: 跑 vitest 與 vue-tsc。**
- [ ] **Step 5: Commit。** `feat(frontend): 五步驟報表設計器`

### Task 9: Demo 示範報表與資料基準日

**Files:**
- Modify: `poc/backend/src/DynamicModules.Application/Dynamic/DemoService/DemoStructure.cs`、`DemoPreflight.cs`、`Commands/GenerateDemoData/`
- Create: `poc/backend/src/DynamicModules.Domain/Entities/Dynamic/SystemSetting.cs` 與其設定、repository；migration `SystemSettings`
- Modify: `ReportService/ReportRunner.cs`（`BasisDateAsync`）
- Test: `poc/backend/tests/DynamicModules.ApplicationTests/Dynamic/Demo/DemoReportsTests.cs`

**Interfaces:**
- Consumes: Task 5 的 `CreateReportRequest`、`SetReportSharesRequest`。
- Produces: `SystemSetting { string Key; string Value }`；鍵 `demo.generatedOn` 值 `yyyy-MM-dd`。`BasisDateAsync`：`AuthSettings.DemoLoginEnabled` 且有此設定時回它，否則回 `IBusinessTime.Today`。`ReportResult.BasisDate` 帶此值，前端只在與今天不同時顯示「資料基準日 yyyy-MM-dd」。

每個情境至少 2 張報表，分享給該情境的主管群組，其中訂單情境一張合併明細且依客戶帶欄位分組。名稱與數字來自產生器資料。

- [ ] **Step 1: 寫失敗測試。** `Generate_CreatesReportsForEachScenario_AllStateOk`（以各情境主管身分執行全部 `ok` 且非 `empty`）；`BasisDate_DemoLoginOn_UsesGeneratedOn`；`BasisDate_DemoLoginOff_UsesToday`；`Preflight_ExistingDemoReport_Conflict`；`FreshInstall_NoReports`；`SalesOne_vs_SalesManager_SameReport_DifferentNumbers`。
- [ ] **Step 2: 跑測試確認失敗。**
- [ ] **Step 3: 實作。**
- [ ] **Step 4: 跑測試確認通過；本機以 `--generate-demo-data` 實跑一次，確認 exit 0。**
- [ ] **Step 5: Commit。** `feat(reports): Demo 示範報表與資料基準日`

### Task 10: 文件與實機走查

**Files:**
- Modify: `README.md`、`docs/manual.md`、`docs/demo.md`；Create: `docs/acceptance/reports.md`（若 Task 4 未建）、`docs/superpowers/plans/2026-11-05-reports-rulings.md`

- [ ] **Step 1: 手冊新增報表章節。** 寫明列的定義、合併與去重（含明細列加不等於總計的原因）、權限與「無權限」卡片、分享、匯出權限、5 萬列上限、**混用幣別要依幣別分組或篩選**、金額權限只管金額型別、彙總欄位不能用時改合併子模組。路線圖加：幣別綁定、結果快取。
- [ ] **Step 2: `docs/demo.md` 加走法。** 含業務一與業務主管看同一張報表數字不同、現場人員看到無權限。
- [ ] **Step 3: 實機走查（chrome-devtools，Demo 模式全新資料庫）。** 組一張「訂單合併明細、依客戶地區分組」的報表，對照手算；分享給群組；換人看數字不同；下鑽；匯出數字一致；切五種呈現；刪掉欄位後看提示。沒走到的標「未實機驗證」，Render 上的標「未驗證」。
- [ ] **Step 4: 跑完整測試。** `dotnet test poc/backend`；`cd poc/frontend && bunx vitest run && bunx vue-tsc --noEmit && bunx quasar build`。掃新增行沒有公司相關字串（清單不進 repo）。
- [ ] **Step 5: Commit。** `docs: 報表手冊與 Demo 腳本`
