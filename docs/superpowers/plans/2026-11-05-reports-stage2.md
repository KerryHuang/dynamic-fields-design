# 報表第二段（儀表板）Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**前提：** [第一段](2026-11-05-reports-stage1.md)已合併到 main。開工前先對照第一段實際留下的介面（`ReportRunner`、`ReportRunOptions`、`ReportResultView`），名稱不符時以程式為準並記裁決。

**Goal:** 主管登入就看到自己的儀表板；管理員能指派給群組；使用者能拖放調整並自動另存個人版。

**Architecture:** 版面與卡片選項存 JSON，三種來源（指派版、個人版、自己的）共用同一份版面格式。卡片查詢走「儀表板＋卡片」API，先驗這個人看得到儀表板，再交給第一段的 `ReportRunner`，以觀看者權限計算。

**Tech Stack:** 第一段全部，加 `grid-layout-plus`（MIT，Vue 3 原生的拖放與調整大小版面，免費可商用、持續維護）。

**Spec:** [`docs/superpowers/specs/2026-11-05-reports-design.md`](../specs/2026-11-05-reports-design.md) §4、§6.4、§7、§8、§9、§10。

## Global Constraints

第一段的 Global Constraints 全部適用，另加：

- 只有 `AppUser.IsSystemAdmin`（`ISystemAdminRequest`）能建立、更新指派版與指派給群組。
- 區間優先順序：卡片自己的區間 > 儀表板暫時篩選 > 報表的區間；不取交集。
- 暫時的日期篩選不存檔，也不觸發個人版。
- 一張卡片失敗不影響其他卡片。
- 寬度小於 600px：清單收合、卡片單欄、不可拖放。

## Review Focus

1. 主管拖動指派版後，其他人畫面不變，且管理員更新時個人版不被覆蓋（Task 2、Task 5）。
2. 不能拿任意報表 ID 透過卡片 API 繞過定義可見性（Task 3）。
3. 取消分享只影響靠分享放上的卡片，指派版卡片照常（Task 3）。
4. 有自己區間的卡片不被儀表板篩選改掉，沒有主要日期欄位的卡片標示不受影響（Task 3、Task 6）。
5. 刪除群組清掉指派，報表被任何儀表板引用時不能刪（Task 1、Task 2）。

---

### Task 1: 資料表與版面格式

**Files:**
- Create: `poc/backend/src/DynamicModules.Domain/Entities/Dynamic/{Dashboard,DashboardAssignment,DashboardPersonalVersion}.cs`、`Domain/Reports/DashboardLayout.cs`、對應設定與 repository；migration `Dashboards`
- Modify: 群組刪除與 impact、`DeleteReport` 的引用檢查
- Test: `poc/backend/tests/DynamicModules.DomainTests/Reports/DashboardLayoutTests.cs`、`poc/backend/tests/DynamicModules.WebAPITests/AdminGroupsApiTests.cs`、`ReportsApiTests.cs`

**Interfaces:**
- Produces:
  - `Dashboard { long Id; string? OwnerUserId /* null=指派版 */; string Name; string LayoutJson; int Version; int Sequence }`
  - `DashboardAssignment { long DashboardId; long GroupId }`
  - `DashboardPersonalVersion { long DashboardId; string UserId; string LayoutJson; int BaseVersion }`
  - `DashboardLayout(List<DashboardCard> Cards)`；`DashboardCard(string Id, long ReportId, string? Title, string Viz, int X, int Y, int W, int H, CardOptions Options)`；`CardOptions(ReportDateRange? Range, string? SortKey, bool SortDesc, int? TopN, bool Cumulative, int? ComparePeriods, long? LinkDashboardId)`

- [ ] **Step 1: 寫失敗測試。** `Layout_RoundTrips`；`DeleteGroup_RemovesAssignments_ImpactListsThem`；`DeleteReport_ReferencedByOthersDashboard_409_ListsReferences`；`DeleteReport_ReferencedByAssignedDashboard_409`；`DeleteReport_OnlyOwnDashboardRefs_Allowed_RemovesCards`。
- [ ] **Step 2: 跑測試確認失敗。**
- [ ] **Step 3: 實作。**
- [ ] **Step 4: 跑測試確認通過。**
- [ ] **Step 5: Commit。** `feat(dashboards): 資料表與版面格式`

### Task 2: 儀表板 API 與個人版

**Files:**
- Create: `poc/backend/src/DynamicModules.Application/Dynamic/DashboardService/`（Commands、Queries、Data）、`poc/backend/src/DynamicModules.WebAPI/Controllers/v3/Dynamic/DashboardsController.cs`
- Test: `poc/backend/tests/DynamicModules.WebAPITests/DashboardsApiTests.cs`

**Interfaces:**
- Consumes: Task 1。
- Produces:
  | 方法與路由 | 用途 |
  |---|---|
  | `GET dashboards` | `{ assigned: [...], mine: [...], sharedReports: [...] }`；assigned 依 `Sequence`，含 `adjusted`、`hasUpdate` |
  | `GET dashboards/{id}` | 回生效的版面（有個人版就回個人版） |
  | `POST dashboards`、`DELETE dashboards/{id}` | 自己的 |
  | `PUT dashboards/{id}/layout` | body `{name?, layout, version?}`。自己的直接存；指派版：非管理員寫入個人版（首次建立，`BaseVersion` 取目前版本） |
  | `POST dashboards/{id}/restore` | 刪除個人版 |
  | `POST admin/dashboards`、`PUT admin/dashboards/{id}`（版本加一，版本不符回 409）、`DELETE admin/dashboards/{id}`、`PUT admin/dashboards/{id}/assignments`、`PUT admin/dashboards/order` | 系統管理員 |

管理員在 `GET` 走自己的指派視角；編輯指派版本體要走 admin 路由。放上卡片時檢查這個人看得到該報表定義（自己的或分享給他的）。

- [ ] **Step 1: 寫失敗測試。** `AssignedVisibleToGroupMembers_NotOthers`（404）；`MultipleGroups_SeesAll_InSequence`；`EditAssigned_NonAdmin_CreatesPersonal_OthersUnaffected`；`AdminUpdate_BumpsVersion_PersonalKept_HasUpdateTrue`；`Restore_DeletesPersonal_ReturnsLatest`；`Unassign_HidesDashboard_PersonalKept_ReassignRestores`；`AdminStaleVersion_409`；`NonAdmin_AdminRoutes_403`；`CannotAddCardOfInvisibleReport_400`；`OwnDashboard_NotVisibleToOthers`。
- [ ] **Step 2: 跑測試確認失敗。**
- [ ] **Step 3: 實作。**
- [ ] **Step 4: 跑測試確認通過。**
- [ ] **Step 5: Commit。** `feat(dashboards): 儀表板 API 與個人版`

### Task 3: 卡片查詢與卡片選項計算

**Files:**
- Modify: `ReportService/ReportRunner.cs`、`Domain/Reports/ReportCalculator.cs`
- Create: `DashboardService/Queries/{RunCard,DrillCard}/`；Modify: `DashboardsController.cs`
- Test: `poc/backend/tests/DynamicModules.DomainTests/Reports/CardOptionsTests.cs`、`poc/backend/tests/DynamicModules.WebAPITests/DashboardCardsApiTests.cs`

**Interfaces:**
- Consumes: Task 2；第一段 `ReportRunner`。
- Produces:
  - `POST dashboards/{id}/cards/{cardId}/run` body `{filterRange?}`；`POST dashboards/{id}/cards/{cardId}/drill`。先驗這個人看得到儀表板、卡片在他生效的版面內。
  - `ReportRunOptions` 新增 `TopN`、`SortKey`、`SortDesc`、`Cumulative`、`ComparePeriods`。
  - `ReportResult` 新增 `Cumulative`（每組累計占比）、`Compare`（`{ previousAverage, deltaPercent }`）、`IgnoresDateFilter`。

規則（spec §4、§6.4）：前 N 名不併「其他」，`Total` 仍算全部；累計占比只限長條，依數值由大到小，分母是全部分組總和；期間比較一期的長度跟著卡片區間，要有主要日期欄位，每期都走相同的去重與權限。

- [ ] **Step 1: 寫失敗測試。** `TopN_TotalStillAll_NoOtherGroup`；`Cumulative_DenominatorIsAllGroups`；`Compare_ThisMonth_AveragesPreviousNMonths`；`Compare_LastDays30_UsesPrevious30DayWindows`；`Compare_NoPrimaryDate_Null`；`Range_Precedence_Card_Over_Filter_Over_Report`；`NoPrimaryDate_IgnoresFilter_FlagSet`；`RunCard_DashboardNotVisible_404`；`RunCard_CardNotInEffectiveLayout_404`；`AssignedCard_ReportNotShared_StillRuns`；`Unshared_MyDashboardCard_ReturnsNoLongerShared`；`AssignedCard_UsesViewerPermissions_DifferentNumbers`。
- [ ] **Step 2: 跑測試確認失敗。**
- [ ] **Step 3: 實作。**
- [ ] **Step 4: 跑測試確認通過。**
- [ ] **Step 5: Commit。** `feat(dashboards): 卡片查詢與選項計算`

### Task 4: 前端儀表板頁、拖放、首頁

**Files:**
- Create: `poc/frontend/src/application/dynamic/queries/dashboards-queries.ts`、`poc/frontend/src/pages/dashboards/DashboardPage.vue`、`poc/frontend/src/presentation/dashboards/{DashboardSideList,DashboardGrid,DashboardCardView,CardOptionsDialog,AddCardDialog}.vue`
- Modify: `router/routes.ts`、`layouts/MainLayout.vue`、`pages/HomePage.vue`、`services/api/dynamic-api.ts`、四語系 JSON、`package.json`、`bun.lock`
- Test: 同目錄 `*.vitest.spec.ts`、`pages/HomePage.vitest.spec.ts`

**Interfaces:**
- Consumes: Task 2、3；第一段 `ReportResultView`。
- Produces: 路由 `/dashboards/:id(\d+)?`；選單 `data-testid="dashboards-menu"`；localStorage 鍵 `dashboard.last.<userId>`，讀寫都包 try/catch。

左側清單分「指派給我」「我的」「分享給我」的報表（點報表開 `/reports/:id`）；個人版標「已調整」，`hasUpdate` 時顯示「儀表板已更新」與「還原成最新版」。`DashboardCardView` 各自查詢、各自顯示 spec §9 的狀態，標示實際套用的區間與「不受日期篩選影響」。首頁：有儀表板就開上次的，否則第一張；沒有就維持現有導向。

- [ ] **Step 1: 寫失敗測試。** `one failing card does not affect others`；`drag saves layout and marks adjusted`；`temporary date filter does not save`；`has update shows restore`；`restore confirms then reloads`；`card shows applied range and ignores-filter note`；`under 600px single column no drag`；`home opens last dashboard`、`home falls back to first`、`home keeps redirect when none`；`last dashboard key is per user`；`localStorage throws still renders`；`link hidden when target not visible`。
- [ ] **Step 2: 跑測試確認失敗。**
- [ ] **Step 3: 安裝 `grid-layout-plus`（選兩週前以上發布的確切版本），實作。**
- [ ] **Step 4: 跑 vitest 與 vue-tsc。**
- [ ] **Step 5: Commit。** `feat(frontend): 儀表板頁與拖放`

### Task 5: 系統管理的指派畫面

**Files:**
- Create: `poc/frontend/src/pages/system/DashboardsAdminPage.vue`；Modify: `router/routes.ts`、`layouts/MainLayout.vue`、`dashboards-queries.ts`、四語系 JSON
- Test: `DashboardsAdminPage.vitest.spec.ts`

**Interfaces:**
- Consumes: Task 2 的 admin 路由；Task 4 的 `DashboardGrid`。
- Produces: 路由 `/system/dashboards`；選單 `data-testid="dashboards-admin-menu"`，只給 `me.isSystemAdmin`。

新增、改名、刪除指派版，編輯版面（重用 `DashboardGrid`），勾選群組，上下移調整順序。

- [ ] **Step 1: 寫失敗測試。** `non admin no menu`；`assign sends group ids`；`reorder sends order`；`stale version shows conflict message`；`delete confirms`。
- [ ] **Step 2: 跑測試確認失敗。**
- [ ] **Step 3: 實作。**
- [ ] **Step 4: 跑 vitest 與 vue-tsc。**
- [ ] **Step 5: Commit。** `feat(frontend): 儀表板指派管理`

### Task 6: Demo 指派版、文件、走查

**Files:**
- Modify: `DemoStructure.cs`、`DemoPreflight.cs`、`README.md`、`docs/manual.md`、`docs/demo.md`、`docs/acceptance/reports.md`、`docs/superpowers/plans/2026-11-05-reports-rulings.md`
- Test: `poc/backend/tests/DynamicModules.ApplicationTests/Dynamic/Demo/DemoDashboardsTests.cs`

- [ ] **Step 1: 寫失敗測試。** `Generate_CreatesAssignedDashboards_ForSalesEquipmentQa`（業務主管、設備、品保各一張）；`AllCards_ForAssignedUser_NotErrored`；`FloorUser_SeesRestrictedCard_NotBrokenDashboard`；`FreshInstall_NoDashboards`。
- [ ] **Step 2: 跑測試確認失敗。**
- [ ] **Step 3: 實作，並更新手冊與 Demo 腳本。** 手冊含個人版、指派、日期優先順序。
- [ ] **Step 4: 實機走查（chrome-devtools，Demo 模式全新資料庫）。** 業務一與業務主管看同一張儀表板數字不同；現場人員看到無權限卡片其他卡片正常；拖放後重新載入版面不變；管理員更新後個人版提示、還原；手機寬度。沒走到的標「未實機驗證」。
- [ ] **Step 5: 跑完整測試。** `dotnet test poc/backend`；`cd poc/frontend && bunx vitest run && bunx vue-tsc --noEmit && bunx quasar build`；掃新增行沒有公司相關字串（清單不進 repo）。
- [ ] **Step 6: Commit。** `feat(dashboards): Demo 指派版與文件`
