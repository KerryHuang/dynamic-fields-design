# 動態模組 POC 第 3 階段：權限實作計畫

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 每個動態模組都受 ProgramCode 權限模型管控：動作軸、欄位隱藏或唯讀、資料範圍、選項範圍、金額遮罩。這些都由管理員在設計器裡設定，用身分切換就能看到差異。

**Architecture:**
- 權限資料表沿用 ProgramCode 權限模型的形狀。
- Application 層的 `PermissionResolver` 是純函式，把群組、個人、欄位、範圍、選項的設定列，合併成某位使用者對某個模組的 `EffectivePermissions`。
- 通用端點在 handler 內呼叫 `IDynamicAccessGuard` 取得有效權限，再據此：
  - 擋動作（403）。
  - 套資料範圍（範圍外的記錄回 404）。
  - 剔除隱藏欄位（回應、篩選、排序、檢視、schema）。
  - 寫入時忽略無權寫入的欄位。
- 設計器端點用 MediatR pipeline behavior 檢查 `DYN00000`。
- 前端改從 `GET /me` 與 schema 的 `permissions` 決定選單、路由與按鈕。

**Tech Stack:** 沿用第 1、2 階段（見 `docs/superpowers/plans/2026-11-03-phase1-foundation.md` 的 Tech Stack），不加新套件。

**Spec:** `docs/superpowers/specs/2026-11-03-dynamic-modules-design.md`。本階段對應：
- §13.2 第 3 項；
- §10 全部；
- §6.2 的權限表與 `FieldOptionScope`；
- §11.2 的 `/modules/{key}/permissions`、schema 與選單的權限語意；
- §11.4 第 1、2 步；
- §11.5 的資料範圍；
- §12.2；
- §15 風險 15、16。

**前置文件**：
- 第 1 階段裁決 `docs/superpowers/plans/2026-11-03-phase1-rulings.md`（R1–R31）。
- 第 2 階段裁決 `docs/superpowers/plans/2026-11-03-phase2-rulings.md`（R1–R12）。

兩份裁決都仍然有效。

## 本階段範圍

**做：**
- **權限表**：
  - 群組、成員。
  - 群組與個人的程式權限。
  - 欄位權限。
  - 資料範圍。
  - 選項範圍。
- **身分**：
  - 種子資料有 4 個群組。
  - 系統管理員放行。
  - 不認得的 `X-User` 一律回 401。
  - 匿名記錄改歸 `admin`（風險 16）。
- **動作軸**：
  - `Read`、`Add`、`Edit`、`Delete` 管控通用端點。
  - `Pricing` 控制 `currency` 欄位是否看得到。
  - `PrintExport`、`Approve` 只存不用；匯出在第 7 階段。
- **欄位層**：
  - `Hidden`：API 不回傳、不能寫、不能篩選排序、schema 不列。
  - `ReadOnly`：不能寫，schema 標示唯讀。
- **資料範圍**：`All`、`Own`、`Group`，套用在列表、明細、修改、刪除。
- **選項範圍**：沒權限的選項不能新選，既有值照常顯示。
- **設計器**：
  - 本身受 `DYN00000` 管控。
  - 編輯頁加「權限」分頁，設定群組權限、個人權限、資料範圍、欄位權限、選項範圍。
- **`Program.Active` 跟著模組狀態走**（風險 15）。
- **前端**：
  - 身分清單改由 API 提供。
  - 選單、路由、按鈕都依權限顯示。
  - 403 顯示「沒有權限」。

**不做（留給後續階段）：**
- 匯出（`PrintExport` 的用途）、反向關聯、light 查詢的範圍套用：在引入該功能的階段處理，套用同一個 `IDynamicAccessGuard`。
- 「規則唯讀」與設計器 `Config.ReadOnly` 的寫入強制：第 4 階段規則引擎一起處理。本階段只強制權限造成的唯讀。
- 有效權限快取（§11.6）：本階段每個請求都即時計算（我的判斷：POC 量小）。上線時用 `ICache`，在權限異動時清除。
- 群組與成員的維護畫面：用種子資料，不做 UI。
- `CloseOverride` 軸：spec 的表沒有欄位，保留。

## 已定決策（我的判斷，spec 沒說死的部分）

1. **系統管理員**：`AppUser.IsSystemAdmin`，種子資料只有 `admin` 為 true。
   - 放行：所有模組與 `DYN00000` 的所有動作，資料範圍 `All`。
   - 不受欄位權限與選項範圍限制，看得到金額。
   - 對應判定鏈裡的 MIS 放行。
2. **身分**：
   - 只有 `GET /api/v3/dynamic/users` 允許沒帶身分，因為身分切換器要先拿到清單。
   - 其他 `/api/v3/dynamic/*` 端點，`X-User` 缺少或不在 `Dynamic_AppUser` 裡，一律回 401：`{ "message": "請先選擇使用者" }`。
3. **合併規則**：
   - **動作軸**：個人權限 ∪ 所有所屬群組的權限，逐軸 OR（spec §10）。
   - **資料範圍**：
     - 只看「有給這個模組 `Read` 的群組」，各取其範圍，再取最寬的：`All` > `Group` > `Own`。
     - 群組沒有設範圍列，視為 `All`。
     - 只靠個人權限拿到 `Read`、沒有任何群組給 `Read` 時，範圍是 `Own`。理由是個人權限通常是單一例外，限縮比較安全。
   - **`Group` 範圍**：記錄擁有者和我共用**任一**群組，或者擁有者就是我。
   - **欄位權限**：
     - 看使用者所屬的**所有**群組，限制優先（spec §10）。
     - 任一群組設為 `Hidden` 就是隱藏；否則任一群組設為 `ReadOnly` 就是唯讀。
   - **`Pricing`**：沒有 `Pricing` 軸時，模組內所有 `currency` 欄位視同 `Hidden`。
   - **選項範圍**：
     - 選項有範圍列時，只有屬於列出群組的使用者能選。
     - 沒有範圍列時，所有人都能選（spec §6.2）。
     - 能不能選，併進 schema 的 `SchemaOption.Active`：`Active = 選項啟用 && 我能選`。寫入驗證的「可選集合」也取同一個交集。前端表單原本就把 `active=false` 的選項顯示成停用、保留既有值，所以不用改。列表篩選仍列出全部選項（第 2 階段 R9）。
4. **判定順序與狀態碼**：
   1. 身分無效 → 401。
   2. 模組不存在或未啟用 → 404（沿用）。
   3. ProgramCode 不在目錄或 `Program.Active = false` → 403。
   4. 缺少動作 → 403：`{ "message": "沒有權限執行此操作" }`。
   5. 依 id 存取的記錄不在資料範圍內 → 404，訊息同「記錄不存在」，不洩漏記錄存在。
5. **寫入**（§11.4 第 2 步）：
   - 隱藏與權限唯讀的欄位，輸入裡有也直接忽略，不算錯。
   - 修改時保留這些欄位的舊值；新增時這些欄位只套預設值。
   - 必填檢查跳過使用者無權寫入的欄位，否則沒有金額權限的人永遠存不了檔。
6. **回應剔除**：記錄的 `values` 不含隱藏欄位的鍵，包括列表、明細、新增與修改的回應。
7. **篩選、排序碰到隱藏欄位**：回 400，訊息與欄位不存在時相同（`欄位 {id} 不存在或未啟用`、`欄位 {key} 不存在或未開放排序`），不洩漏欄位存在。
8. **清單檢視**：
   - 讀檢視需要模組 `Read`。
   - 檢視裡引用了我看不到的欄位：從 `columns`／`filter`／`sort` 剔除，id 列進 `staleFieldIds`，沿用第 2 階段的剔除機制。
   - 個人檢視只要 `Read`。
   - 建立、修改、刪除**共用**檢視需要 `DYN00000` 的 `Edit`，因為共用檢視會影響所有人。
9. **schema**：
   - 剔除隱藏欄位。剔除後沒有欄位的區塊不列。
   - 唯讀欄位回傳 `config.readOnly = true`。
   - 回應加上 `permissions` 物件。
   - ETag 改成 `"{key}-v{version}-{permHash}"`。`permHash` 是有效權限正規化字串的 SHA-256 前 8 個十六進位字元，換人或改權限就會失效，不必另外維護權限版本號。
   - 讀 schema 需要 `Read`。
10. **選單**：只列出我有 `Read` 的啟用模組（§11.2）。
11. **設計器（`DYN00000`）各端點需要的軸**：
    - `Read`：`GET /modules`、`GET /modules/{key}`、`GET /modules/{key}/permissions`、`GET /groups`。
    - `Add`：`POST /modules`、`POST /modules/{key}/blocks`、`POST /modules/{key}/fields`、`POST /fields/{id}/options`。
    - `Edit`：所有 `PUT`，以及 `activate`／`deactivate`。
12. **`Program.Active`**：
    - 建立模組時為 false（草稿），`activate` 改 true，`deactivate` 改 false，與 `Module.Status` 在同一個交易。
    - 啟動時 `SeedData` 依現有模組狀態同步一次。
13. **既有資料升級**（啟動時 `SeedData` 冪等處理）：
    - 建 4 個群組與成員。
    - `Dynamic_GroupProgramPrivilege` **完全沒有資料時**：對每個既有模組，給 `sales`、`sales-manager`、`floor` 三個群組 `Read`/`Add`/`Edit`/`Delete`/`PrintExport`/`Pricing`，讓第 2 階段的資料升級後照舊可用。
    - 之後新建的模組沒有任何授權，只有系統管理員看得到，屬於 fail-closed。
    - 匿名記錄：`OwnerUserId` 不在 `Dynamic_AppUser` 的記錄，改歸 `admin`。
14. **權限設定 API**：
    - `PUT /modules/{key}/permissions` 一次送該模組的全部設定，在一個交易內整批取代。
    - 不遞增 `Module.Version`：權限不是 metadata，schema 的 ETag 已經含 `permHash`。

## Global Constraints

第 1、2 階段計畫的 Global Constraints 全部沿用，另加以下幾條：

- **新表**用一個 EF migration `Phase3Permissions` 建立。用 `dotnet ef migrations add Phase3Permissions --project poc/backend/src/DynamicModules.Persistence --startup-project poc/backend/src/DynamicModules.Persistence` 產生，不手寫（第 2 階段 R3）。

  | 表 | 欄位 | 鍵 |
  |---|---|---|
  | `Dynamic_PermissionGroup` | `Id` long、`Key` nvarchar(40) 唯一、`NameI18n` JSON | `Id` |
  | `Dynamic_UserGroupMapping` | `GroupId`、`UserId` nvarchar(50) | `(GroupId, UserId)` |
  | `Dynamic_GroupProgramPrivilege` | `GroupId`、`ProgramCode` nvarchar(8)、`CanRead`、`CanAdd`、`CanEdit`、`CanDelete`、`CanPrint`、`CanAmt`、`CanApprove`（bool） | `(GroupId, ProgramCode)` |
  | `Dynamic_UserProgramPrivilege` | `UserId`、`ProgramCode`，同上 7 個 bool | `(UserId, ProgramCode)` |
  | `Dynamic_FieldPermission` | `FieldId`、`GroupId`、`Access` nvarchar(20)（`Hidden`／`ReadOnly`） | `(FieldId, GroupId)` |
  | `Dynamic_ModuleDataScope` | `ModuleId`、`GroupId`、`Scope` nvarchar(20)（`All`／`Own`／`Group`） | `(ModuleId, GroupId)` |
  | `Dynamic_FieldOptionScope` | `OptionId`、`GroupId` | `(OptionId, GroupId)` |

  - `Dynamic_AppUser` 加 `IsSystemAdmin` bool，預設 false。
  - 個人權限表 `UserProgramPrivilege` 是 spec §10「個人權限 ∪ 群組權限」需要、§6.2 表格漏列的表。
  - 權限表照 spec §6.2 的寫法，不加稽核欄位，屬於 Phase 1 R8 的目錄資料豁免。
- **軸名對照**：

  | API 名稱 | 欄位 |
  |---|---|
  | `read` | `CanRead` |
  | `add` | `CanAdd` |
  | `edit` | `CanEdit` |
  | `delete` | `CanDelete` |
  | `printExport` | `CanPrint` |
  | `pricing` | `CanAmt` |
  | `approve` | `CanApprove` |

  C# 列舉 `PermissionAction { Read, Add, Edit, Delete, PrintExport, Pricing, Approve }`。
- **種子群組**：

  | `Key` | 名稱（zh-TW） | 成員 |
  |---|---|---|
  | `mis` | MIS | `admin` |
  | `sales` | 業務 | `sales01`、`sales02` |
  | `sales-manager` | 業務主管 | `manager01` |
  | `floor` | 現場人員 | `floor01` |

- **錯誤訊息**（逐字使用）：
  - 401：`請先選擇使用者`
  - 403：`沒有權限執行此操作`
  - 權限設定 400：
    - `標題欄位不能設為隱藏`
    - `群組不存在`
    - `使用者不存在`
    - `欄位不屬於此模組`
    - `選項不屬於此模組`
    - `設定重複`
- **前端文字**（i18n 四語系；th/vi 用英文佔位，照第 2 階段 R7 的作法）：
  - `dynamic.forbidden`：`沒有權限使用此功能`
  - 權限分頁的標籤：見 Task 10、11。

## Review Focus

1. **必填欄位對使用者隱藏或唯讀**：沒有金額權限的業務新增記錄，「預估費用」是必填，存檔要成功，不能卡在看不到的欄位。→ Task 5 的 `Create_RequiredFieldHiddenForUser_Succeeds`。
2. **沒有 `Pricing` 的人從任何管道都拿不到金額**：
   - 涵蓋 schema、列表的 values、明細、新增或修改的回應、用金額篩選、用金額排序、套用含金額條件的共用檢視。
   - 測試：Task 4 的 `Paged_PricingDenied_CurrencyNeverExposed`，以及 Task 6 的 `Schema_PricingDenied_OmitsCurrency`、`GetViews_HiddenFieldStrippedAndStale`。
3. **唯讀欄位被送了不同的值**：存檔成功，但存的是舊值。→ Task 5 的 `Update_ReadOnlyFieldIgnored_KeepsStoredValue`。
4. **用 id 直接存取範圍外的記錄**：`GET`、`PUT`、`DELETE /records/{id}` 都回 404，不是 403，也不是成功。→ Task 4 的 `GetRecord_OutOfScope_Returns404`、Task 5 的 `UpdateAndDelete_OutOfScope_Return404`。
5. **一個人屬於多個設定衝突的群組**：
   - 一個群組 `Own`、另一個群組 `All`，結果是 `All`。
   - 一個群組隱藏某欄位、另一個沒設，結果是隱藏。
   - 個人權限給 `Delete`、群組沒給，結果可以刪除。

   → Task 2 的 `Resolve_*` 測試組。

---

## 檔案結構

只列本階段新增或修改的檔案。後端路徑省略 `poc/backend/src/DynamicModules.` 前綴。

```
Domain/
  Entities/Dynamic/{PermissionGroup,UserGroupMapping,GroupProgramPrivilege,UserProgramPrivilege,
                    FieldPermission,ModuleDataScope,FieldOptionScope}.cs
  Entities/Dynamic/{FieldAccess,DataScope}.cs        列舉
  Entities/Dynamic/AppUser.cs                        + IsSystemAdmin
  Data/Configurations/*Configuration.cs              新表各一個
  Repositories/Dynamic/IPermissionRepository.cs      讀寫權限列
  Repositories/Dynamic/RecordQuery.cs                + OwnerUserIds

Application/
  Common/Security/{ICurrentUser,PermissionAction,EffectivePermissions,PermissionResolver,
                   IDynamicAccessGuard,DynamicAccessGuard,IDesignerRequest,DesignerAccessBehavior}.cs
  Common/ServiceResult.cs                            + Forbidden
  Dynamic/RecordService/**                           套用範圍、欄位、動作
  Dynamic/SchemaService/**                           schema 與選單套用權限
  Dynamic/ViewService/**                             檢視套用權限
  Dynamic/ModuleService/**                           Program.Active；設計器請求標 IDesignerRequest
  Dynamic/FieldService/**                            設計器請求標 IDesignerRequest
  Dynamic/PermissionService/**                       權限設定、群組、使用者、me

Persistence/
  Repositories/Dynamic/PermissionRepository.cs
  Dynamic/Query/DynamicQueryBuilder.cs               + OwnerUserId IN
  Seed/SeedData.cs                                   群組、升級授權、匿名記錄、Program 同步
  Migrations/<時間戳>_Phase3Permissions.cs

WebAPI/
  Security/{HeaderCurrentUser,IdentityMiddleware}.cs 401
  Controllers/ServiceResultExtensions.cs             + 403
  Controllers/v3/Dynamic/PermissionsController.cs

poc/frontend/src/
  domain/dynamic/types.ts                            + ModulePermissions、Me、權限設定型別
  services/api/dynamic-api.ts                        + getMe、getUsers、getGroups、get/putPermissions
  application/dynamic/queries/permissions-queries.ts
  stores/use-identity-store.ts                       使用者清單改由 API
  layouts/MainLayout.vue                             設計器選單依權限、身分清單
  router/routes.ts                                   首頁導向
  pages/HomePage.vue                                 依權限導向
  pages/dynamic/{ListPage,DetailPage,FormPage}.vue   按鈕依權限、403
  pages/system/dynamic-modules/*.vue                 403、權限分頁
  presentation/dynamic/permissions/{PrivilegeEditor,FieldPermissionEditor}.vue
```

---

### Task 1：權限表、種子群組與資料升級

**Files:**
- Create：
  - `Domain/Entities/Dynamic/` 下 7 個實體與 `FieldAccess`、`DataScope` 列舉。
  - `Domain/Data/Configurations/` 下對應的 configuration。
  - migration `Phase3Permissions`。
- Modify：
  - `Domain/Entities/Dynamic/AppUser.cs`、`Domain/Data/Configurations/AppUserConfiguration.cs`、`Domain/Data/DynamicDbContext.cs`。
  - `Persistence/Seed/SeedData.cs`。
  - `Application/Dynamic/ModuleService/Commands/{CreateModule,ActivateModule,DeactivateModule}/*Handler.cs`：`Program.Active` 跟著模組狀態，見決策 12。
- Test：`ApplicationTests/Persistence/SchemaTests.cs`、`ApplicationTests/Persistence/SeedDataTests.cs`（新）、`ApplicationTests/Dynamic/ModuleServiceTests.cs`

**Interfaces:**
- Produces：
  - 實體：
    - `PermissionGroup { long Id; string Key; I18nText NameI18n; }`
    - `UserGroupMapping { long GroupId; string UserId; }`
    - `GroupProgramPrivilege { long GroupId; string ProgramCode; bool CanRead, CanAdd, CanEdit, CanDelete, CanPrint, CanAmt, CanApprove; }`
    - `UserProgramPrivilege { string UserId; string ProgramCode; 同上 7 個 bool }`
    - `FieldPermission { long FieldId; long GroupId; FieldAccess Access; }`
    - `ModuleDataScope { long ModuleId; long GroupId; DataScope Scope; }`
    - `FieldOptionScope { long OptionId; long GroupId; }`
  - 列舉：`enum FieldAccess { Hidden, ReadOnly }`、`enum DataScope { Own, Group, All }`。宣告順序就是由窄到寬，Task 2 用來比較。資料庫以字串存。
  - `AppUser.IsSystemAdmin`。
  - `SeedData.EnsureSeededAsync` 依決策 13 冪等處理：
    - 建群組與成員；
    - 第一次升級時授權；
    - 匿名記錄改歸 `admin`；
    - 同步 `Program.Active`；
    - 種子使用者 `admin` 的 `IsSystemAdmin = true`。

- [ ] **Step 1：寫失敗的測試**
  - `Schema_HasPermissionTables`：7 張表與複合主鍵都存在；`Dynamic_AppUser` 有 `IsSystemAdmin`。
  - `Seed_CreatesGroupsAndMembers`：4 個群組，成員照 Global Constraints 的表。
  - `Seed_IsIdempotent`：跑兩次，筆數不變。
  - `Seed_FirstUpgrade_GrantsExistingModules`：先建一個模組、清空權限表，再 seed，`sales`、`sales-manager`、`floor` 各有一列，`CanRead`/`CanAdd`/`CanEdit`/`CanDelete`/`CanPrint`/`CanAmt` 為 true、`CanApprove` 為 false。之後再建新模組並 seed，新模組沒有授權列。
  - `Seed_ReassignsUnknownOwnersToAdmin`：`OwnerUserId = "anonymous"` 的記錄變成 `admin`。
  - `Seed_SyncsProgramActiveWithModuleStatus`
  - `CreateModule_ProgramInactiveUntilActivated`：建立後 `Program.Active == false`，activate 後 true，deactivate 後 false。
- [ ] **Step 2：執行** `dotnet test poc/backend --filter "SchemaTests|SeedDataTests|ModuleServiceTests"`。預期新測試 FAIL。
- [ ] **Step 3：實作。** migration 用 Global Constraints 的指令產生。
- [ ] **Step 4：執行同一個指令**，預期 PASS；再跑 `dotnet test poc/backend` 全部通過。
- [ ] **Step 5：Commit** `feat: 權限表、種子群組與資料升級`

### Task 2：有效權限的合併（`PermissionResolver`）

**Files:**
- Create：
  - `Application/Common/Security/PermissionAction.cs`
  - `Application/Common/Security/EffectivePermissions.cs`
  - `Application/Common/Security/PermissionResolver.cs`
  - `Domain/Repositories/Dynamic/IPermissionRepository.cs`
  - `Persistence/Repositories/Dynamic/PermissionRepository.cs`
- Modify：`Persistence/DependencyInjection.cs`
- Test：`ApplicationTests/Common/PermissionResolverTests.cs`（純函式，不用資料庫）、`ApplicationTests/Persistence/PermissionRepositoryTests.cs`

**Interfaces:**
- Consumes：Task 1 的實體。
- Produces：
  - `PermissionInputs` 是 resolver 的輸入，一次讀齊：
    ```csharp
    public sealed record PermissionInputs(
        AppUser User, IReadOnlyList<long> GroupIds,
        IReadOnlyList<GroupProgramPrivilege> GroupPrivileges, UserProgramPrivilege? UserPrivilege,
        IReadOnlyList<ModuleDataScope> Scopes, IReadOnlyList<FieldPermission> FieldPermissions,
        IReadOnlyList<FieldOptionScope> OptionScopes, IReadOnlySet<string> GroupMateUserIds);
    ```
    - `GroupPrivileges`、`Scopes`、`FieldPermissions` 只含該模組、該使用者所屬群組的列。
    - `OptionScopes` 是該模組**所有**選項的範圍列，不限群組，因為要判斷「有列但我不在」。
    - `GroupMateUserIds` 是和我共用任一群組的使用者，含我自己。
  - `IPermissionRepository.LoadInputsAsync(string userId, Module module, CancellationToken ct) → Task<PermissionInputs?>`：使用者不存在時回 null。
  - `EffectivePermissions`：
    ```csharp
    public sealed record EffectivePermissions(
        IReadOnlySet<PermissionAction> Actions, DataScope Scope,
        IReadOnlySet<string>? OwnerUserIds,          // null = 不限（All）
        IReadOnlySet<long> HiddenFieldIds, IReadOnlySet<long> ReadOnlyFieldIds,
        IReadOnlySet<long> UnselectableOptionIds, string Hash)
    {
        public bool Can(PermissionAction a) => Actions.Contains(a);
        public bool CanSee(Field f) => !HiddenFieldIds.Contains(f.Id);
        public bool CanWrite(Field f) => CanSee(f) && !ReadOnlyFieldIds.Contains(f.Id);
    }
    ```
  - `PermissionResolver.Resolve(Module module, PermissionInputs inputs) → EffectivePermissions`。照「已定決策」第 1、3 條：
    - `OwnerUserIds`：`Own` 時是 `{我}`，`Group` 時是 `GroupMateUserIds`，`All` 時是 null。
    - `HiddenFieldIds` 已經含沒有 `Pricing` 時的所有 `currency` 欄位。
    - `Hash` 是決策 9 的 8 碼十六進位。
  - `PermissionResolver.ResolveProgram(string programCode, PermissionInputs inputs) → IReadOnlySet<PermissionAction>`：設計器只需要動作軸。

- [ ] **Step 1：寫失敗的測試**（`PermissionResolverTests`，手刻 `PermissionInputs`）
  - `Resolve_ActionsAreOrOfGroupsAndPersonal`：群組 A 給 `Read`、群組 B 給 `Add`、個人給 `Delete`，三軸都有，`Edit` 沒有。
  - `Resolve_ScopeIsWidestAmongReadGroups`：A（`Read`、`Own`）加 B（`Read`、`All`）得到 `All`，`OwnerUserIds` 是 null。
  - `Resolve_ScopeIgnoresGroupsWithoutRead`：A（`Read`、`Own`）加 B（沒給 `Read`、`All`）得到 `Own`。
  - `Resolve_MissingScopeRowMeansAll`
  - `Resolve_PersonalReadOnly_ScopeIsOwn`
  - `Resolve_GroupScope_OwnersAreGroupMates`
  - `Resolve_FieldHiddenWinsOverNothing`：A 隱藏欄位 5、B 沒設，5 是隱藏。
  - `Resolve_HiddenWinsOverReadOnly`：A 對 5 唯讀、B 對 5 隱藏，5 只在 Hidden、不在 ReadOnly。
  - `Resolve_NoPricing_HidesCurrencyFields`
  - `Resolve_OptionScopedToOtherGroup_IsUnselectable`：選項 9 只給群組 C，我不在 C，9 不可選。選項 10 沒有範圍列，可以選。
  - `Resolve_SystemAdmin_BypassesEverything`：沒有任何權限列也有全部 7 軸、`All`、三個集合都是空的。
  - `Resolve_HashChangesWhenPermissionsChange`：同輸入同 hash；拿掉一軸，hash 不同。
  - `PermissionRepositoryTests.LoadInputs_FiltersToModuleAndUserGroups`：SQLite in-memory，只載入該模組與我所屬群組的列；`OptionScopes` 含其他群組的列。
- [ ] **Step 2：執行** `dotnet test poc/backend --filter "PermissionResolverTests|PermissionRepositoryTests"`。預期 FAIL。
- [ ] **Step 3：實作。** Hash 的正規化字串，由排序後的各集合以固定分隔符串接，再算 SHA-256。
- [ ] **Step 4：執行同一個指令**，預期 PASS；再跑全部測試。
- [ ] **Step 5：Commit** `feat: 有效權限合併`

### Task 3：身分 401、存取守門與設計器管控

**Files:**
- Create：
  - `WebAPI/Security/IdentityMiddleware.cs`
  - `Application/Common/Security/{IDynamicAccessGuard,DynamicAccessGuard,IDesignerRequest,DesignerAccessBehavior}.cs`
- Modify：
  - `Application/Common/Security/ICurrentUser.cs`：`IKnownUsers` 不變。
  - `WebAPI/Security/HeaderCurrentUser.cs`：拿掉 `Anonymous` 後備值；無效時由 middleware 先擋。
  - `WebAPI/Program.cs`
  - `Application/Common/ServiceResult.cs`、`WebAPI/Controllers/ServiceResultExtensions.cs`
  - `Application/DependencyInjection.cs`：註冊 behavior，放在 Validation 之前。
  - `ModuleService`、`FieldService` 所有 Request：實作 `IDesignerRequest`，各自宣告需要的軸，見決策 11。
- Test：`WebAPITests/SecurityTests.cs`（新）、`ApplicationTests/Common/DesignerAccessBehaviorTests.cs`（新）、`ApplicationTests/Common/ServiceResultTests.cs`

**Interfaces:**
- Consumes：Task 2 的 `IPermissionRepository`、`PermissionResolver`。
- Produces：
  - `ServiceResult.Forbidden()`：`IsForbidden = true`，WebAPI 映射成 403 `{ message: "沒有權限執行此操作" }`。
  - `IDesignerRequest { PermissionAction RequiredDesignerAction { get; } }`；`DesignerAccessBehavior<TRequest, TResponse>`：
    - 只處理 `TResponse` 是 `ServiceResult` 的請求，無權時回 `Forbidden()`。
    - 回傳 List 的查詢（例如 `GetModules`）改成回 `ServiceResult`，讓 behavior 能一致回 403。
  - `IDynamicAccessGuard`：
    ```csharp
    Task<AccessResult> AuthorizeAsync(Module module, PermissionAction action, CancellationToken ct);
    Task<IReadOnlySet<PermissionAction>> DesignerActionsAsync(CancellationToken ct);
    ```
    - `sealed record AccessResult(EffectivePermissions? Permissions, ServiceResult? Denied)`：通過時 `Permissions` 有值；否則 `Denied` 是 `Forbidden()`。
    - 先查 `DynamicProgram`：不存在或 `Active = false` → `Forbidden()`；再看 `Permissions.Can(action)`。
  - `IdentityMiddleware`：
    - 路徑以 `/api/v3/dynamic` 開頭、不是 `GET /api/v3/dynamic/users`、而且 `X-User` 無效時，回 401 `{ message: "請先選擇使用者" }`。
    - `/api/health` 與 swagger 不受影響。

- [ ] **Step 1：寫失敗的測試**
  - `SecurityTests.MissingOrUnknownUser_Returns401`：`GET /menu` 不帶 header，以及帶 `nobody`，都回 401，body 是固定訊息。
  - `SecurityTests.UsersEndpointIsAnonymous`：此端點由 Task 7 建立，本任務先用 `[Fact(Skip=...)]` 佔位，Task 7 再打開。
  - `DesignerAccessBehaviorTests.NonAdminWithoutDesignerRead_Forbidden`：`sales01` 呼叫 `GetModule` 得到 Forbidden。
  - `DesignerAccessBehaviorTests.GroupGrantedDesignerEdit_Allows`：給 `sales` 群組 `DYN00000` 的 `Read`/`Edit`，`sales01` 可以 `UpdateModule`，但 `CreateModule`（`Add`）仍然 Forbidden。
  - `DesignerAccessBehaviorTests.Admin_AllowsAll`
  - `SecurityTests.DesignerEndpoint_NonAdmin_Returns403`：`sales01` 呼叫 `POST /modules` 回 403。
  - `ServiceResultTests.Forbidden_MapsTo403`
- [ ] **Step 2：執行** `dotnet test poc/backend --filter "SecurityTests|DesignerAccessBehaviorTests|ServiceResultTests"`。預期 FAIL。
- [ ] **Step 3：實作。**
  - 既有的 WebAPI 測試若沒帶 `X-User`，補上 `admin`。
  - 測試輔助的 HttpClient 預設帶 `X-User: admin`，比照現有 helper 的寫法。
- [ ] **Step 4：執行** `dotnet test poc/backend`，全部通過。
- [ ] **Step 5：Commit** `feat: 身分驗證、存取守門與設計器權限`

### Task 4：記錄的讀取套用權限（列表、明細）

**Files:**
- Modify：
  - `Domain/Repositories/Dynamic/RecordQuery.cs`：加 `IReadOnlySet<string>? OwnerUserIds`，null 表示不限。
  - `Persistence/Dynamic/Query/DynamicQueryBuilder.cs`：`OwnerUserIds` 不是 null 時，加 `r.OwnerUserId IN (@o0, @o1, …)`，空集合時加 `1 = 0`。
  - `Application/Dynamic/RecordService/Queries/{GetPagedRecords,GetRecord}/*Handler.cs`
  - `Application/Dynamic/RecordService/Data/RecordResponses.cs`：`ToResponse(this Record r, string moduleKey, EffectivePermissions p, Module module)`，剔除隱藏欄位的鍵。
- Create：`Domain/Conditions/ConditionFields.cs`：`static IEnumerable<Field> Collect(ConditionNode node)`。
- Test：`ApplicationTests/Dynamic/RecordPermissionTests.cs`（新）、`ApplicationTests/Persistence/DynamicQueryBuilderTests.cs`

**Interfaces:**
- Consumes：Task 3 的 `IDynamicAccessGuard`。
- Produces：
  - 所有回記錄的地方都用新的 `ToResponse` 多載。舊多載刪除，讓編譯器找出漏改的地方。
  - 列表：
    - 需要 `Read`，套用 `OwnerUserIds`。
    - 篩選解析成功後，`ConditionFields.Collect` 找到隱藏欄位，就回 400，鍵 `filter`，訊息 `欄位 {id} 不存在或未啟用`。
    - `ParseSorts` 把隱藏欄位當成不存在。
  - 明細：需要 `Read`；記錄的 `OwnerUserId` 不在 `OwnerUserIds` 時，回 404 `RecordMapper.RecordNotFound`。

- [ ] **Step 1：寫失敗的測試**
  - `Paged_OwnScope_ListsOnlyOwnRecords`：`sales01` 和 `sales02` 各建 2 筆，`sales` 設為 `Own`，`sales01` 列表只看到 2 筆。
  - `Paged_GroupScope_ListsGroupMatesRecords`：`floor01` 也建 1 筆，`sales` 設為 `Group` 時，`sales01` 看到 4 筆。
  - `Paged_NoRead_Forbidden`
  - `Paged_PricingDenied_CurrencyNeverExposed`：`sales` 拿掉 `Pricing`：
    - `values` 沒有 currency 鍵。
    - 用 currency 篩選回 400，鍵 `filter`，訊息 `欄位 {id} 不存在或未啟用`。
    - 用 currency 排序回 400，鍵 `sortBy`。
  - `Paged_HiddenFieldStrippedFromValues`
  - `GetRecord_OutOfScope_Returns404`
  - `GetRecord_StripsHiddenFields`
  - `DynamicQueryBuilderTests.Build_OwnerUserIds_AreParameters`：
    - SQL 含 `r.OwnerUserId IN (@o0, @o1)`，不含使用者 id 字串。
    - 空集合時 SQL 含 `1 = 0`。
- [ ] **Step 2：執行** `dotnet test poc/backend --filter "RecordPermissionTests|DynamicQueryBuilderTests"`。預期 FAIL。
- [ ] **Step 3：實作。**
- [ ] **Step 4：執行** `dotnet test poc/backend`，全部通過。
- [ ] **Step 5：Commit** `feat: 記錄列表與明細套用資料範圍與欄位權限`

### Task 5：記錄的寫入套用權限（新增、修改、刪除）

**Files:**
- Modify：
  - `Application/Dynamic/RecordService/RecordValueProcessor.cs`：`Process` 加參數 `EffectivePermissions permissions`。
  - `Application/Dynamic/RecordService/Commands/{CreateRecord,UpdateRecord,DeleteRecord}/*Handler.cs`
  - 呼叫 `Process` 的測試與 benchmark：傳系統管理員的權限。測試輔助加 `EffectivePermissionsFactory.AdminFor(Module)`，放在 ApplicationTests 的共用 helper。
- Test：`ApplicationTests/Dynamic/RecordPermissionTests.cs`

**Interfaces:**
- Consumes：Task 3 的 guard、Task 4 的 `ToResponse` 多載。
- Produces：
  - `RecordValueProcessor.Process(Module module, IReadOnlyDictionary<string, JsonElement?> input, IReadOnlyDictionary<string, JsonElement?>? previous, IReadOnlySet<string> userIds, FieldTypeRegistry registry, EffectivePermissions permissions)`。
  - 欄位 `!permissions.CanWrite(f)` 時：
    - 忽略輸入，舊值保留。
    - 新增時只套預設值。
    - 不做必填檢查。
  - `FieldTypeContext` 的「啟用選項」集合改成：啟用中，且不在 `UnselectableOptionIds` 裡。
  - 權限檢查：
    - 新增需要 `Add`，修改需要 `Edit`，刪除需要 `Delete`。
    - 修改、刪除的記錄不在範圍內時，回 404。
    - 判定順序照決策 4：先 404 模組，再 403 動作，再 404 範圍。

- [ ] **Step 1：寫失敗的測試**
  - `Create_NoAdd_Forbidden`；`Update_NoEdit_Forbidden`；`Delete_NoDelete_Forbidden`。
  - `Create_RequiredFieldHiddenForUser_Succeeds`：必填的 currency 欄位，對 `sales`（沒有 `Pricing`）隱藏。新增回 201，回應的 values 不含它；用 admin 讀，該欄位是空的，或者等於預設值。
  - `Create_HiddenFieldInInput_Ignored`：送了隱藏欄位的值，存的是空值。
  - `Update_ReadOnlyFieldIgnored_KeepsStoredValue`：admin 先存 100，唯讀的使用者送 999，回 200，admin 讀到的還是 100。
  - `UpdateAndDelete_OutOfScope_Return404`
  - `Create_UnselectableOption_Rejected`：選項限定給其他群組，選它回 400，鍵是欄位 key。
  - `Update_KeepsExistingUnselectableOption`：記錄原本就是那個選項，只改別的欄位，回 200。
  - `Delete_PersonalPrivilegeGrantsDelete`：群組沒有 `Delete`、個人有，刪除成功。
- [ ] **Step 2：執行** `dotnet test poc/backend --filter RecordPermissionTests`。預期新測試 FAIL。
- [ ] **Step 3：實作。**
- [ ] **Step 4：執行** `dotnet test poc/backend`，全部通過；`dotnet test poc/backend --filter Category=Benchmark` 能編譯並通過。
- [ ] **Step 5：Commit** `feat: 記錄寫入套用動作、範圍與欄位權限`

### Task 6：schema、選單、清單檢視套用權限

**Files:**
- Modify：
  - `Application/Dynamic/SchemaService/Queries/{GetModuleSchema,GetMenu}/*Handler.cs`
  - `Application/Dynamic/SchemaService/Data/SchemaResponses.cs`
  - `WebAPI/Controllers/v3/Dynamic/SchemaController.cs`（ETag）
  - `Application/Dynamic/ViewService/**`：讀檢視時的剔除、共用檢視的 `DYN00000` `Edit` 檢查。
  - 共用檢視的檢查：`CreateView`、`UpdateView`、`DeleteView` 在 `shared` 為 true，或原本就是共用檢視時，呼叫 `IDynamicAccessGuard.DesignerActionsAsync`，沒有 `Edit` 回 `Forbidden()`。
- Test：`ApplicationTests/Dynamic/SchemaServiceTests.cs`、`ApplicationTests/Dynamic/ViewServiceTests.cs`、`WebAPITests/SchemaEndpointTests.cs`（沿用既有的 schema 端點測試檔；沒有就新建）

**Interfaces:**
- Consumes：Task 3 的 guard。
- Produces：
  - `ModuleSchemaResponse` 最後加一個參數 `SchemaPermissions Permissions`；
    `record SchemaPermissions(bool Read, bool Add, bool Edit, bool Delete, bool PrintExport, bool Pricing)`。
  - schema：
    - 剔除 `!CanSee` 的欄位。
    - 唯讀欄位：`Config` 複製一份，把 `ReadOnly` 設為 true，不改到 metadata 本身。
    - `SchemaOption.Active` 照決策 3 取交集。
  - `SchemaETag.For(string key, int version, string permHash) → "\"{key}-v{version}-{permHash}\""`，舊的兩參數版本刪除。
  - 選單：只列有 `Read` 的模組，系統管理員看全部。
  - `GetViews`：
    - 需要 `Read`，否則回 Forbidden。
    - 剔除邏輯把「我看不到的欄位」和「停用、不可篩選、不可排序」一視同仁，id 進 `staleFieldIds`。

- [ ] **Step 1：寫失敗的測試**
  - `Schema_PricingDenied_OmitsCurrency`
  - `Schema_HiddenField_Omitted_ReadOnlyFlagged`
  - `Schema_UnselectableOption_IsInactive`
  - `Schema_CarriesPermissions`：`sales01` 有 `Read`/`Add`、沒有 `Delete`，`permissions` 符合。
  - `Schema_NoRead_Forbidden`
  - `SchemaEndpoint_ETagDiffersByUser`：同一模組，admin 與 `sales01` 的 ETag 不同；帶自己的 ETag 回 304，帶別人的 ETag 回 200。
  - `Menu_ListsOnlyReadableModules`
  - `GetViews_HiddenFieldStrippedAndStale`：共用檢視的篩選用了 currency，`sales01`（沒有 `Pricing`）讀到的 filter 沒有那個條件，`staleFieldIds` 含它；admin 讀到完整內容。
  - `CreateSharedView_WithoutDesignerEdit_Forbidden`；`CreatePersonalView_WithReadOnly_Succeeds`。
- [ ] **Step 2：執行** `dotnet test poc/backend --filter "SchemaServiceTests|ViewServiceTests|SchemaEndpointTests"`。預期新測試 FAIL。
- [ ] **Step 3：實作。**
- [ ] **Step 4：執行** `dotnet test poc/backend`，全部通過。
- [ ] **Step 5：Commit** `feat: schema、選單與清單檢視套用權限`

### Task 7：權限設定、群組、使用者與 me 的 API

**Files:**
- Create：
  - `Application/Dynamic/PermissionService/{Queries/GetModulePermissions,Commands/SaveModulePermissions,Queries/GetGroups,Queries/GetUsers,Queries/GetMe}/*`
  - `Application/Dynamic/PermissionService/Data/PermissionResponses.cs`
  - `WebAPI/Controllers/v3/Dynamic/PermissionsController.cs`
- Modify：`IPermissionRepository`／`PermissionRepository`，加整批取代與列表方法。
- Test：`ApplicationTests/Dynamic/PermissionServiceTests.cs`（新）、`WebAPITests/SecurityTests.cs`：打開 Task 3 佔位的 `UsersEndpointIsAnonymous`。

**Interfaces:**
- Consumes：Task 1、2、3。
- Produces（JSON camelCase）：
  - `GET /users`，不需身分 → `[{ userId, displayName }]`，依 `userId` 排序。
  - `GET /me` → `{ userId, displayName, isSystemAdmin, groups: [{ id, key, name }], designer: { read, add, edit, delete } }`。
  - `GET /groups`，需要 `DYN00000` 的 `Read` → `[{ id, key, name, members: [userId] }]`。
  - `GET /modules/{key}/permissions`，需要 `DYN00000` 的 `Read`：

    ```json
    {
      "groups":   [{ "groupId": 2, "privileges": { "read": true, "add": true, "edit": true, "delete": false,
                                                  "printExport": false, "pricing": false, "approve": false },
                     "scope": "own" }],
      "users":    [{ "userId": "sales01", "privileges": { ... } }],
      "fields":   [{ "fieldId": 12, "groupId": 2, "access": "hidden" }],
      "options":  [{ "optionId": 7, "groupIds": [3] }]
    }
    ```

    - `groups` 列出**所有**群組；沒有授權列的群組，privileges 全是 false，`scope` 是 null，代表 `All`。
    - `users` 只列有個人權限列的使用者。
    - `fields`、`options` 只列例外。
  - `PUT /modules/{key}/permissions`：
    - 需要 `DYN00000` 的 `Edit`，body 與 GET 同形狀。
    - 在一個交易內刪除該模組的全部權限列，再寫入：
      - 群組與個人的 `ProgramCode` 列。
      - 該模組欄位的 `FieldPermission`。
      - 該模組的 `ModuleDataScope`。
      - 該模組選項的 `FieldOptionScope`。
    - privileges 全是 false 的群組或個人，不寫列。
    - `scope` 為 null 時，不寫範圍列。
    - 成功回 200，body 是新的 GET 結果。
  - **驗證**：400，鍵是 JSON 路徑，例如 `fields[0].access`；訊息用 Global Constraints 的逐字文字。
    - 標題欄位不能是 `hidden`。
    - 群組、使用者、欄位、選項必須存在，而且屬於此模組。
    - 同一個（欄位, 群組）、（選項）、（群組）、（使用者）不可重複。
- [ ] **Step 1：寫失敗的測試**
  - `SavePermissions_RoundTrips`：PUT 再 GET 得到同樣內容；不屬於此模組的列不受影響。
  - `SavePermissions_ReplacesPreviousRows`：第二次 PUT 不含某個欄位權限，那一列被刪除。
  - `SavePermissions_TitleFieldHidden_Rejected`
  - `SavePermissions_ForeignFieldOrOption_Rejected`
  - `SavePermissions_Duplicates_Rejected`
  - `SavePermissions_TakesEffectImmediately`：PUT 把 `sales` 設為 `Own`，接著 `sales01` 的列表只剩自己的記錄。
  - `GetMe_ReturnsGroupsAndDesignerActions`
  - `SecurityTests.UsersEndpointIsAnonymous`：拿掉 Skip，不帶 `X-User` 回 200。
- [ ] **Step 2：執行** `dotnet test poc/backend --filter "PermissionServiceTests|SecurityTests"`。預期 FAIL。
- [ ] **Step 3：實作。**
- [ ] **Step 4：執行** `dotnet test poc/backend`，全部通過。
- [ ] **Step 5：Commit** `feat: 權限設定、群組、使用者與 me API`

### Task 8：前端身分與導覽依權限

**Files:**
- Modify：
  - `poc/frontend/src/domain/dynamic/types.ts`
  - `services/api/dynamic-api.ts`
  - `stores/use-identity-store.ts`：拿掉寫死的 `SEED_USERS`，清單改用 `GET /users`。localStorage 裡的 id 不在清單時，改用清單第一個。
  - `layouts/MainLayout.vue`：
    - 身分下拉的選項改由 `useUsers()` 提供。
    - 「動態模組設計」選單項只在 `me.designer.read` 時顯示。
  - `router/routes.ts`：`/` 改由 `HomePage` 導向。
- Create：
  - `application/dynamic/queries/permissions-queries.ts`：`useUsers()`、`useMe()`；`me` 的 queryKey 含 userId。
  - `pages/HomePage.vue`：
    - `me.designer.read` 為 true 時，導到 `/system/dynamic-modules`。
    - 否則導到選單的第一個模組。
    - 都沒有就顯示 `dynamic.forbidden`。
  - `components/framework/ForbiddenNotice.vue`：顯示 `dynamic.forbidden` 的一行文字元件，各頁共用。
- Modify：所有頁面在 API 回 403 時，顯示 `ForbiddenNotice`，不顯示 `loadFailed`：
  - `pages/dynamic/{ListPage,DetailPage,FormPage}.vue`
  - `pages/system/dynamic-modules/{ModuleListPage,ModuleEditorPage}.vue`
- Test：
  - `stores/use-identity-store.vitest.spec.ts`
  - `layouts/MainLayout.vitest.spec.ts`
  - `pages/HomePage.vitest.spec.ts`（新）
  - `pages/dynamic/ListPage.vitest.spec.ts`

**Interfaces:**
- Consumes：Task 7 的 `/users`、`/me`。
- Produces：
  - `Me { userId; displayName; isSystemAdmin; groups: { id; key; name: I18nText }[]; designer: { read; add; edit; delete } }`
  - `useMe(): UseQueryReturnType<Me>`
  - `ApiError.status === 403` 的判斷，各頁統一用 `isForbidden(err: unknown): boolean`，放在 `services/api/http.ts`。

- [ ] **Step 1：寫失敗的測試**
  - `identity store falls back to the first API user when the stored id is unknown`
  - `MainLayout hides the designer menu without designer read`
  - `HomePage redirects to the designer when allowed, else to the first menu module, else shows forbidden`
  - `ListPage shows forbidden notice on 403`
- [ ] **Step 2：執行** `cd poc/frontend && bunx vitest run`。預期新測試 FAIL。
- [ ] **Step 3：實作。**
- [ ] **Step 4：執行** `bunx vitest run && bunx vue-tsc --noEmit`，全部通過。
- [ ] **Step 5：Commit** `feat: 前端身分清單與導覽依權限`

### Task 9：前端列表、明細、表單依權限

**Files:**
- Modify：
  - `domain/dynamic/types.ts`：`ModuleSchema.permissions: SchemaPermissions`。
  - `pages/dynamic/ListPage.vue`：「新增」按鈕只在 `permissions.add` 時顯示。
  - `pages/dynamic/DetailPage.vue`：「編輯」只在 `edit` 時顯示，「刪除」只在 `delete` 時顯示。
  - `pages/dynamic/FormPage.vue`：
    - 新增模式缺 `add`、編輯模式缺 `edit` 時，顯示 `ForbiddenNotice`，不渲染表單。
    - 唯讀欄位與不可選的選項沿用既有機制：`config.readOnly`、`active=false`，不另寫。
  - `presentation/dynamic/list/ViewMenu.vue`：「另存為」對話框的「共用給所有人」勾選，只在 `me.designer.edit` 時顯示。
- Test：`pages/dynamic/ListPage.vitest.spec.ts`、`pages/dynamic/DetailPage.vitest.spec.ts`（新）、`pages/dynamic/FormPage.vitest.spec.ts`（新）、`presentation/dynamic/list/ViewMenu.vitest.spec.ts`

**Interfaces:**
- Consumes：Task 6 的 `permissions`、Task 8 的 `useMe`。

- [ ] **Step 1：寫失敗的測試**
  - `ListPage hides the new button without add`
  - `DetailPage hides edit and delete per permissions`
  - `FormPage shows forbidden in edit mode without edit`
  - `FormPage renders a read-only field as readonly`：schema 的 `config.readOnly` 為 true 時，輸入元件帶 readonly。
  - `ViewMenu hides the shared checkbox without designer edit`
- [ ] **Step 2：執行** `bunx vitest run`。預期新測試 FAIL。
- [ ] **Step 3：實作。**
- [ ] **Step 4：執行** `bunx vitest run && bunx vue-tsc --noEmit`，全部通過。
- [ ] **Step 5：Commit** `feat: 列表、明細、表單依權限顯示`

### Task 10：設計器權限分頁之一：群組、個人權限與資料範圍

**Files:**
- Create：
  - `presentation/dynamic/permissions/PrivilegeEditor.vue`
  - `application/dynamic/queries/permissions-queries.ts`：加 `useModulePermissions(key)`、`useGroups()`、`useSaveModulePermissions()`。
- Modify：
  - `pages/system/dynamic-modules/ModuleEditorPage.vue`：在既有內容外包一層 `q-tabs`，分成「欄位」（既有內容）與「權限」兩頁。
  - `domain/dynamic/types.ts`：`ModulePermissions`、`Privileges`、`DataScope` 型別，對齊 Task 7 的 JSON。
  - i18n 四語系。
- Test：`presentation/dynamic/permissions/PrivilegeEditor.vitest.spec.ts`

**Interfaces:**
- Consumes：Task 7 的端點。
- Produces：
  - `PrivilegeEditor` props `{ modelValue: ModulePermissions; groups: Group[]; users: User[]; readonly: boolean }`，emit `update:modelValue`。
  - 畫面：
    - 群組表：一列一個群組，7 個軸各一個勾選，加上資料範圍下拉。選項是「全部」「同群組」「本人」，空值是預設，等於全部。
    - 個人權限表：可以新增或移除使用者列，每列 7 個勾選。
  - 「權限」分頁底部有一個「儲存權限」按鈕。
    - 送出的資料包含 Task 11 的欄位、選項設定。
    - 失敗時，依錯誤鍵標在對應的列。
    - 沒有 `me.designer.edit` 時整頁唯讀。
  - i18n 鍵放在 `designer.permissions.*`：

    | 鍵 | zh-TW |
    |---|---|
    | `designer.permissions.tab` | 權限 |
    | `designer.permissions.groups` | 群組權限 |
    | `designer.permissions.users` | 個人權限 |
    | `designer.permissions.addUser` | 新增使用者 |
    | `designer.permissions.scope` | 資料範圍 |
    | `designer.permissions.scopeAll` | 全部 |
    | `designer.permissions.scopeGroup` | 同群組 |
    | `designer.permissions.scopeOwn` | 本人 |
    | `designer.permissions.save` | 儲存權限 |
    | `designer.permissions.saved` | 權限已儲存 |
    | `designer.permissions.axes.read` | 讀取 |
    | `designer.permissions.axes.add` | 新增 |
    | `designer.permissions.axes.edit` | 修改 |
    | `designer.permissions.axes.delete` | 刪除 |
    | `designer.permissions.axes.printExport` | 列印匯出 |
    | `designer.permissions.axes.pricing` | 金額 |
    | `designer.permissions.axes.approve` | 核准 |

- [ ] **Step 1：寫失敗的測試**
  - `PrivilegeEditor toggles a group axis and emits the updated permissions`
  - `PrivilegeEditor sets scope own and clears it back to default (null)`
  - `PrivilegeEditor adds and removes a personal row`
  - `PrivilegeEditor is fully disabled when readonly`
  - `list-i18n` 的鍵完整性測試涵蓋 `designer.permissions`（沿用既有測試檔的作法，把新區塊加進檢查清單）。
- [ ] **Step 2：執行** `bunx vitest run`。預期新測試 FAIL。
- [ ] **Step 3：實作。**
- [ ] **Step 4：執行** `bunx vitest run && bunx vue-tsc --noEmit`，全部通過。
- [ ] **Step 5：Commit** `feat: 設計器權限分頁：群組、個人權限與資料範圍`

### Task 11：設計器權限分頁之二：欄位權限與選項範圍

**Files:**
- Create：`presentation/dynamic/permissions/FieldPermissionEditor.vue`
- Modify：`ModuleEditorPage.vue` 的「權限」分頁加入這個元件；i18n 四語系。
- Test：`presentation/dynamic/permissions/FieldPermissionEditor.vitest.spec.ts`

**Interfaces:**
- Consumes：Task 10 的分頁與儲存流程。
- Produces：
  - `FieldPermissionEditor` props `{ modelValue: ModulePermissions; fields: DesignerField[]; groups: Group[]; titleFieldId?: number; readonly: boolean }`。
  - **欄位權限矩陣**：
    - 列是啟用中的欄位，欄是群組。
    - 每格一個下拉：「預設」「唯讀」「隱藏」。「預設」不寫列。
    - 標題欄位不提供「隱藏」。
  - **選項範圍**：每個 `enum`／`multienum` 欄位的每個選項一列，用多選群組下拉。空值代表所有人可選。
  - **i18n 鍵**：

    | 鍵 | zh-TW |
    |---|---|
    | `designer.permissions.fields` | 欄位權限 |
    | `designer.permissions.access.default` | 預設 |
    | `designer.permissions.access.readOnly` | 唯讀 |
    | `designer.permissions.access.hidden` | 隱藏 |
    | `designer.permissions.options` | 選項範圍 |
    | `designer.permissions.optionsHint` | 未指定群組代表所有人可選 |
    | `designer.permissions.pricingHint` | 沒有「金額」權限的群組一律看不到金額欄位 |

- [ ] **Step 1：寫失敗的測試**
  - `FieldPermissionEditor sets a cell to hidden and back to default removes the row`
  - `FieldPermissionEditor does not offer hidden for the title field`
  - `FieldPermissionEditor scopes an option to two groups and clearing removes the row`
  - `FieldPermissionEditor lists only enum and multienum options`
- [ ] **Step 2：執行** `bunx vitest run`。預期新測試 FAIL。
- [ ] **Step 3：實作。**
- [ ] **Step 4：執行** `bunx vitest run && bunx vue-tsc --noEmit && bunx quasar build`，全部通過。
- [ ] **Step 5：Commit** `feat: 設計器權限分頁：欄位權限與選項範圍`

### Task 12：第 3 階段驗收

**Files:**
- Create：
  - `poc/backend/tests/DynamicModules.WebAPITests/Phase3AcceptanceTests.cs`
  - `docs/acceptance/phase3.md`、`docs/acceptance/phase3/*.png`
- Modify：
  - spec §15：風險 15、16 改寫成「第 3 階段已處理」並附做法，加一條「有效權限每請求即時計算，上線時要用 `ICache`」。
  - spec §6.2 的權限表：補 `UserProgramPrivilege`。
  - `README.md` 的文件表加上本計畫的列。

**Interfaces:**
- Consumes：所有前面的任務。

- [ ] **Step 1：寫驗收測試** `Phase3_Scenario`，用 API 走完以下流程，每一步都要斷言：
  1. admin 建模組 `repair-request`，欄位如下，然後啟用：
     - 申請人：`text`，標題。
     - 故障類別：`enum`，選項「模具損壞」「機台異常」「外包」。
     - 預估費用：`currency`，必填。
     - 說明：`textarea`。
  2. 用 `PUT /permissions` 設定：
     - `sales`：`Read`/`Add`/`Edit`，範圍 `Own`，沒有 `Pricing`。
     - `sales-manager`：`Read`/`Add`/`Edit`/`Delete`/`Pricing`，範圍 `All`。
     - `floor`：`Read`/`Add`，範圍 `Own`，「說明」唯讀。
     - 選項「外包」只給 `sales-manager`。
  3. `sales01` 建 2 筆、`sales02` 建 1 筆，都沒送預估費用，都回 201。
  4. `sales01` 的列表只有自己 2 筆，`values` 沒有預估費用；schema 沒有預估費用，`permissions.delete` 是 false。
  5. `sales01` 讀 `sales02` 的記錄，回 404；刪除自己的記錄，回 403。
  6. `sales01` 選「外包」，回 400；`manager01` 選「外包」，回 201。
  7. `manager01` 的列表有全部 4 筆，看得到預估費用，可以依預估費用排序。
  8. `floor01` 修改自己的記錄，送了「說明」，存的仍是舊值。
  9. 不帶 `X-User` 呼叫 `GET /menu`，回 401；`sales01` 呼叫 `POST /modules`，回 403。
- [ ] **Step 2：執行** `dotnet test poc/backend --filter Phase3_Scenario`，預期 PASS。
- [ ] **Step 3：執行全部自動測試**：
  - `dotnet test poc/backend`（不含 Benchmark）；
  - `cd poc/frontend && bunx vitest run && bunx vue-tsc --noEmit`；

  預期全部通過。
- [ ] **Step 4：實機走一遍**：執行 `poc/start.command`，在瀏覽器走 Step 1 的情境：
  - 設計器權限分頁：群組權限、資料範圍、欄位權限、選項範圍，儲存後重新整理仍在。
  - 切 `sales01`：選單沒有「動態模組設計」、列表只有自己的記錄、看不到金額欄、沒有刪除按鈕、「外包」選項停用。
  - 切 `manager01`：看得到全部記錄與金額。
  - 切 `floor01`：「說明」唯讀。
  - 在網址列直接輸入 `/system/dynamic-modules`，顯示「沒有權限使用此功能」。

  每個畫面截一張圖存到 `docs/acceptance/phase3/`，結果記在 `docs/acceptance/phase3.md`，逐項標「通過」或「失敗」。
- [ ] **Step 5：Commit** `test: 第 3 階段驗收`
