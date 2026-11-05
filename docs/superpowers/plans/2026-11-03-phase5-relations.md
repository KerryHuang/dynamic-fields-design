# 動態模組 POC 第 5 階段：關聯（參照欄位、反向關聯、刪除行為、擴充模組） 實作計畫

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 設計者能加「參照」欄位指向另一個動態模組或既有實體（模具、客戶），被參照的記錄明細頁自動出現反向關聯分頁；刪除被參照的記錄時依欄位設定擋下、清空或連帶刪除；既有實體可以用擴充模組加自訂欄位。

**Architecture:**
- 新欄位型別 `reference`。值是目標的鍵：動態模組存 `Record.Id`（JSON number），既有實體存主鍵字串。一律寫進 `RecordIndex.RefKey`（字串），反向關聯、篩選、刪除檢查都查這個索引。
- 既有實體走 `IReferenceProvider` 登錄表（Application 介面、Persistence 實作）：提供輕量查詢與批次取標題。POC 用兩張 mock 表 `Mock_Mold`、`Mock_Customer` 代替外部系統的真實表。
- 讀取時批次解析參照，回應多一個 `references`，帶標題與狀態（正常、已失效、無權限檢視）。寫入時只在值有變動時檢查目標存在且在權限範圍內。
- 刪除走 `ReferenceDeletePlanner`：先在記憶體算出整棵連帶刪除樹，再一次套用；任何一處碰到 Restrict 就整筆不刪。
- 擴充模組是 `Module.Kind = Extension` 加 `HostEntityKey`，每筆記錄用 `Record.HostKey` 對到一筆既有資料，`(ModuleId, HostKey)` 唯一。

**Tech Stack:** 沿用第 1–4 階段（見 `docs/superpowers/plans/2026-11-03-phase1-foundation.md` 的 Tech Stack），不加新套件。

**Spec:** `docs/superpowers/specs/2026-11-03-dynamic-modules-design.md`。本階段對應：
- §13.2 第 5 項；
- §9 關聯（參照目標、反向關聯、刪除行為）；
- §6.2 的 `Module.Kind`、`HostEntityKey`、`Record.HostKey`；
- §7 的 `reference` 一列；
- §11.2 的 `records/light`、`records/{id}/related/{fieldId}/paged`；
- §11.3 的 `References`；
- §12 的 PopupSelect；
- §13 的 mock 既有實體與 Demo 2 的關聯部分；
- §15 第 7 條（既有實體刪除後顯示「已失效」）。

**前置文件**：第 1–4 階段裁決（`docs/superpowers/plans/2026-11-03-phase{1,2,3,4}-rulings.md`）全部仍然有效。

## 本階段範圍

**做：**
- **參照欄位**（`reference`）：目標是動態模組或既有實體；表單用 PopupSelect 挑選；列表、明細顯示標題與連結；列表可篩選；規則條件可用。
- **輕量查詢**：動態模組的 `records/light`、既有實體的 `entities/{key}/light`。
- **反向關聯分頁**：被參照記錄的明細頁，每個指向它的參照欄位一個分頁，可在分頁直接新增並自動帶入參照。
- **刪除行為**：`restrict`（預設）、`setNull`、`cascade`。
- **既有實體 mock**：模具（`design.mold`）、客戶（`sales.customer`）。
- **擴充模組**：替既有實體加自訂欄位，用 host 鍵讀寫。

**不做（留給後續階段）：**
- 表頭明細、彙總：第 6 階段。
- SetNull 寫歷程：第 7 階段做歷程時補上（spec §9 寫「清空並寫歷程」，本階段沒有歷程表）。
- 多值參照（一個欄位參照多筆）：spec 沒要求。
- 參照欄位排序：標題不在索引裡，排序要 join，POC 不做。
- 公式引用參照欄位的目標欄位（如 `{客戶}.{等級}`）：spec 沒要求。

## 已定決策（我的判斷，spec 沒說死的部分）

1. **欄位設定**：
   - `FieldConfig.RefTarget`：`{ kind: "module" | "entity", key }`。`module` 的 key 是 `Module.Key`，`entity` 的 key 是登錄的實體鍵（如 `design.mold`）。
   - `FieldConfig.OnDelete`：`restrict`（預設，null 視同 restrict）、`setNull`、`cascade`。只能用在 `kind = module`；既有實體的刪除不經過動態模組，沒有刪除行為可言。
   - `RefTarget` 建立後不能改，和型別一樣。`OnDelete` 可以改。
   - 目標模組必須存在、`Kind = Standalone`。允許指向自己的模組（例如「上層設備」）。目標模組不必已啟用。
   - 參照欄位不能設 `DefaultValue`、`MaxLength`、`Precision`、`Scale`、`Format`、`Formula`。`Required`、`ReadOnly`、`ShowInList`、`IsFilterable` 可以設；`IsSortable` 不行。
   - `setNull` 不能用在 `Required` 欄位：清空後的記錄會違反必填。
2. **值與索引**：
   - 模組目標的值是 JSON number（`Record.Id`）；實體目標是 JSON string。型別不符回 400 `參照的資料不存在或無權選取`。
   - `RefKey` 一律存字串（`Record.Id.ToString()` 或實體主鍵）。
   - 參照欄位**一律**建索引，不看 `IsFilterable`：反向關聯和刪除檢查都靠它。`RecordIndexer.ShouldIndex` 加這個條件。
3. **篩選與規則**：
   - 運算子：`eq`、`ne`、`in`、`notIn`、`isEmpty`、`isNotEmpty`。`filter-operators.json` 加 `"reference"` 鍵。
   - 條件值：模組目標用 number，實體目標用 string。`DynamicQueryBuilder.Column` 對 `reference` 回 `RefKey`，值轉字串比對。
   - 規則條件可以用參照欄位，同一組運算子。前端規則引擎以 `String(value)` 比對；`rule-cases.json` 加參照案例鎖定。
4. **寫入時的存在檢查**（只在新值和舊值不同時檢查；沒變動的舊值即使已失效也照存，見 Review Focus 4）：
   - 模組目標：目標模組啟用中、記錄存在且未刪除、目前使用者對目標模組有 `Read`、記錄在使用者的資料範圍內。
   - 實體目標：`IReferenceProvider.TitlesAsync` 查得到。
   - 不符一律回 400，鍵是欄位鍵，訊息 `參照的資料不存在或無權選取`。不區分「不存在」和「沒權限」，避免用來探測別人的記錄。
   - 實作：handler 在呼叫 `RecordValueProcessor` 前，先對每個有變動的參照欄位查出合法鍵集合，經 `FieldTypeContext.ValidRefKeys` 傳給 `ReferenceHandler.Normalize`。`ValidRefKeys = null` 代表不檢查（規則條件、篩選值正規化時用）。
5. **讀取時的參照資訊**：
   - `DynamicRecordResponse` 加 `References: Dictionary<string, ReferenceInfo>`，鍵是欄位鍵，只放有值、且使用者看得到的參照欄位。
   - `ReferenceInfo(string Key, string? Title, string State)`；`State` 是 `ok`、`missing`、`restricted`。
     - `missing`：目標記錄已刪除或不存在、既有實體查不到。
     - `restricted`：沒有目標模組的 `Read`、記錄不在資料範圍、目標的標題欄位對使用者隱藏，或目標模組未啟用。
     - 只有 `ok` 帶 `Title`，其他一律 null，不洩漏內容。
   - 單筆和分頁列表都帶，分頁一頁之內批次查（每個目標模組或實體一次查詢），不逐筆查。
   - 前端：`missing` 顯示「已失效」、`restricted` 顯示「無權限檢視」，都不給連結。
6. **輕量查詢 API**：
   - `GET /modules/{key}/records/light?keyword=&page=1&size=20` → `{ items: [{ key, title }], total }`。
     - 要目標模組的 `Read`，套資料範圍；模組未啟用回 404 `模組不存在或未啟用`；標題欄位對使用者隱藏回 403。
     - `keyword` 比對標題（`Record.Title` 包含），依 `Title` 排序；`size` 上限 50。
   - `GET /entities` → `[{ key, name }]`：設計器挑實體目標用，要設計器權限（`IsSystemAdmin`）。
   - `GET /entities/{entityKey}/light?keyword=&page=&size=` → 同上格式。實體不存在回 404 `既有實體不存在`。
   - mock 實體不做權限檢查（任何登入的使用者都能查）。§15 新增一條：上線後要依宿主實體的 `ProgramCode` 檢查。
   - schema 的 `SchemaField` 加 `Reference`：`{ kind, key, name, lightUrl }`。`name` 是目標模組或實體的名稱（zh-TW 字串或 I18nText，跟模組名稱同格式），`lightUrl` 是前端要呼叫的相對路徑（`/modules/{key}/records/light` 或 `/entities/{key}/light`）。
7. **反向關聯**：
   - `GET /records/{id}/related` → 分頁清單 `[{ fieldId, fieldKey, fieldLabel, moduleKey, moduleName, canAdd }]`。
     - 來源：所有啟用中模組裡、啟用中、`RefTarget` 指向本記錄所屬模組的參照欄位。
     - 使用者要有來源模組的 `Read`，且來源欄位對他看得到；否則不出現這個分頁。
     - `canAdd`：使用者有來源模組的 `Add`，且來源欄位可寫入。
     - 本記錄要先通過 `GET /records/{id}` 同樣的讀取檢查，不通過回 404 `記錄不存在`。
   - `GET /records/{id}/related/{fieldId}/paged?page=&size=` → 和 `records/paged` 同樣的分頁回應格式（含 `references`）。
     - 以 `RecordIndex WHERE FieldId = ? AND RefKey = ?` 找來源記錄，套來源模組的資料範圍，排除已刪除，依 `Id` 遞減。
     - `fieldId` 不在上面的分頁清單裡（不存在、不指向本模組、看不到）回 404 `關聯不存在`。
   - 前端「新增」導向 `/dynamic/{來源模組}/new?prefill={fieldKey}:{recordId}`；FormPage 讀 `prefill` 帶入初始值，只接受該模組 schema 裡的參照欄位。
8. **刪除行為**：
   - 刪除前找出所有「指向被刪記錄所屬模組」的啟用中參照欄位（跨所有模組，含未啟用模組——資料仍在），以及它們未刪除的來源記錄。
   - **Restrict**：整棵連帶刪除樹裡任何一處有 Restrict 來源，整筆刪除都不執行（全有或全無）。
     - 回 409，body `{ message: "還有其他資料參照這筆記錄，不能刪除", references: [...], total }`。
     - `references` 最多 10 筆：`{ moduleKey, moduleName, fieldLabel, recordId, title }`；使用者對來源記錄沒有讀取權時 `title` 為 null。
   - **SetNull**：從來源記錄的 `ValuesJson` 移除該欄位、重建該欄位索引、遞增 `RowVersion`、蓋修改稽核欄位。不寫歷程（本階段不做）。
   - **Cascade**：來源記錄連帶軟刪除，再遞迴處理它們的來源。
     - 深度上限 5（被刪記錄是第 0 層），第 6 層還有東西要處理就回 409 `連帶刪除超過 5 層，請先手動刪除下層資料`，什麼都不刪。
     - 用已造訪集合防循環與自我參照。
     - 連帶刪除不檢查使用者對來源模組的 `Delete` 權限：刪除權看的是發起刪除的那筆。§15 新增一條記錄這個取捨。
   - 停用的參照欄位不擋刪除、不出現在反向關聯。
   - 整個刪除在一個交易內。
9. **既有實體 mock**：
   - `Mock_Mold`：`MoldNo` nvarchar(20) PK、`Name` nvarchar(100)、`DeleteFlag` bool。
   - `Mock_Customer`：`CustomerNo` nvarchar(20) PK、`Name` nvarchar(100)、`DeleteFlag` bool。
   - 種子（`SeedData`，冪等）：
     - 模具：`M-0001` 外殼上蓋模、`M-0002` 外殼下蓋模、`M-0003` 按鍵模、`M-0004` 電池蓋模、`M-0005` 支架模。
     - 客戶：`C-001` 台灣精密、`C-002` 恆川機械、`C-003` 曜陽電子。
   - 標題格式：`{編號} {名稱}`，例如 `M-0001 外殼上蓋模`。`DeleteFlag = true` 的資料 light 查不到、`TitlesAsync` 不回（顯示為 missing）。
   - 實體名稱：`design.mold` → 模具；`sales.customer` → 客戶（I18nText，四語系）。
10. **擴充模組**：
    - `CreateModuleRequest` 加 `Kind`（`standalone` 預設、`extension`）與 `HostEntityKey`。
      - `extension` 沒給或給了未登錄的實體：400，鍵 `HostEntityKey`，訊息 `擴充模組必須指定既有實體`。
      - 同一個實體已經有擴充模組：409 `這個既有實體已經有擴充模組`。
      - `standalone` 給了 `HostEntityKey`：忽略。
      - `Kind`、`HostEntityKey` 建立後不能改（UpdateModule 不接受這兩欄）。
    - 擴充模組不建預設標題欄位，`TitleFieldId` 維持 null，不能設定（UpdateModule 給了回 400，鍵 `TitleFieldId`，訊息 `擴充模組的標題取自既有資料`）。記錄的 `Title` 存 host 標題的快照（新增時取）。
    - 新增記錄：`CreateRecordRequest` 加 `HostKey`。
      - 擴充模組沒給：400，鍵 `hostKey`，訊息 `擴充模組的記錄必須指定既有資料`。
      - host 查不到：400，鍵 `hostKey`，訊息 `既有資料不存在`。
      - 同一筆 host 已有未刪除的記錄：409 `這筆既有資料已經有擴充資料`。已刪除的不算，唯一索引用 filtered index（`DeleteFlag = 0`），刪除後可以重建。
      - standalone 模組給了 `hostKey`：忽略。
    - 修改不接受改 `HostKey`（請求不帶這欄）。
    - 回應加 `HostKey`（standalone 為 null）。
    - `GET /modules/{key}/records/by-host/{hostKey}`：回單筆記錄，權限同 `GET /records/{id}`；沒有回 404 `記錄不存在`。
    - 參照欄位不能指向擴充模組（`參照目標不正確`）：擴充資料要透過既有實體參照。
11. **其他限制**：
    - 公式不能引用參照欄位（沿用 `公式不正確：{原因}`，原因沿用第 4 階段 `FormulaParser` 的既有訊息 `欄位 {id} 的型別不能用在公式`，不新增）。
    - 參照欄位不能當記錄標題（沿用 `記錄標題欄位必須是啟用中的文字或自動編號欄位`）。
    - 刪除被參照的**欄位**（停用）不需要額外檢查。
12. **Migration `Phase5Relations`**（一個，Task 1 產生，不手寫）。
13. **權限的組合**：參照欄位本身的欄位權限（隱藏、唯讀）照第 3 階段走；看不到參照欄位就沒有 `references` 那一格，也看不到它造成的反向分頁。

## Global Constraints

第 1–4 階段計畫的 Global Constraints 全部沿用，另加以下幾條：

- **Migration**：`dotnet ef migrations add Phase5Relations --project poc/backend/src/DynamicModules.Persistence --startup-project poc/backend/src/DynamicModules.Persistence`，在 Task 1 產生，不手寫、不手改。

  | 表 | 變更 | 鍵／索引 |
  |---|---|---|
  | `Dynamic_Module` | 加 `Kind` nvarchar(20) 非 null 預設 `standalone`、`HostEntityKey` nvarchar(100) null | `HostEntityKey` 唯一（filtered：非 null） |
  | `Dynamic_Record` | 加 `HostKey` nvarchar(100) null | `(ModuleId, HostKey)` 唯一（filtered：`HostKey` 非 null 且 `DeleteFlag = 0`） |
  | `Mock_Mold` | 新表：`MoldNo`、`Name`、`DeleteFlag` | PK `MoldNo` |
  | `Mock_Customer` | 新表：`CustomerNo`、`Name`、`DeleteFlag` | PK `CustomerNo` |

  mock 表不加 `Dynamic_` 前綴：它們代表外部系統既有的表，不屬於動態模組。
- **列舉**：
  - `FieldType` 加 `Reference`，JSON 名稱 `reference`。
  - `ModuleKind { Standalone, Extension }`，JSON `standalone`、`extension`；DB 存字串。
  - `ReferenceOnDelete { Restrict, SetNull, Cascade }`，JSON `restrict`、`setNull`、`cascade`。
  - `ReferenceState` 字串常數：`ok`、`missing`、`restricted`。
- **`FieldConfig` 新屬性**：
  - `RefTarget`：`ReferenceTarget?`，`record ReferenceTarget(string Kind, string Key)`，`Kind` 是 `module` 或 `entity`。
  - `OnDelete`：`ReferenceOnDelete?`。
- **錯誤訊息**（逐字使用）：
  - `參照目標不正確`（鍵 `Config.RefTarget`）
  - `參照目標建立後不能更改`（鍵 `Config.RefTarget`）
  - `刪除方式只能用在參照動態模組的欄位`（鍵 `Config.OnDelete`）
  - `刪除時清空不能用在必填欄位`（鍵 `Config.OnDelete`）
  - `參照欄位不能設定預設值、長度、小數位、格式或排序`（鍵 `Config`）
  - `參照的資料不存在或無權選取`（鍵為欄位鍵）
  - `還有其他資料參照這筆記錄，不能刪除`（409）
  - `連帶刪除超過 5 層，請先手動刪除下層資料`（409）
  - `既有實體不存在`（404）
  - `關聯不存在`（404）
  - `擴充模組必須指定既有實體`（鍵 `HostEntityKey`）
  - `這個既有實體已經有擴充模組`（409）
  - `擴充模組的標題取自既有資料`（鍵 `TitleFieldId`）
  - `擴充模組的記錄必須指定既有資料`（鍵 `hostKey`）
  - `既有資料不存在`（鍵 `hostKey`）
  - `這筆既有資料已經有擴充資料`（409）
- **409 帶額外資料**：`ServiceResult` 加 `ConflictWith(string message, object data)`，`ServiceResultExtensions` 在 `Data` 非 null 時回 `{ message, ...data }`（以 `references`、`total` 兩個屬性展開）。既有 `Conflict(message)` 行為不變。
- **共用檔**：`poc/shared/filter-operators.json` 加 `reference`；`poc/shared/rule-cases.json` 加參照案例。改語意先改共用檔。
- **前端文字**：
  - i18n 放 `designer.types.reference`、`designer.reference.*`、`designer.extension.*`、`dynamic.reference.*`（含 `missing` 已失效、`restricted` 無權限檢視、`pick` 選擇、`clear` 清除）、`dynamic.related.*`、`dynamic.deleteBlocked.*`。
  - 四個語系都要有；th／vi 用英文佔位（第 2 階段 R7）。字面大括號用 `{'{'}` 跳脫。

## Review Focus

1. **參照到自己看不到的記錄**：sales01 的資料範圍是 Own，看不到 sales02 的客戶記錄。
   - light 清單不列出它；手工組 POST 帶它的 id 回 400 `參照的資料不存在或無權選取`；別人存好的參照讓 sales01 看，顯示 `restricted`、沒有標題。
   - 測試：Task 3 的 `Create_RefOutOfScope_Rejected`，Task 4 的 `Get_RefTargetOutOfScope_Restricted`、`Light_OutOfScope_Excluded`。
2. **連帶刪除的鏈上某處是 Restrict**：A ←(cascade) B ←(restrict) C。刪 A 回 409，A、B 都沒被刪、C 不變。→ Task 6 的 `Delete_CascadeChainHitsRestrict_NothingChanged`。
3. **連帶刪除遇到循環或自我參照**：「上層設備」指向自己模組且 cascade，或 A、B 兩個模組互相 cascade。刪除會結束、每筆只刪一次，不是無窮遞迴或 500。→ Task 6 的 `Delete_CascadeCycle_Terminates`、`Delete_SelfReferenceCascade_DeletesChain`。
4. **既有資料被刪掉之後**：模具 M-0003 的 `DeleteFlag` 改成 true。引用它的記錄顯示「已失效」；使用者修改這筆記錄的其他欄位、沒動模具欄位，照樣存得進去；改成另一個失效的值則被擋。→ Task 4 的 `Get_EntityGone_Missing`，Task 3 的 `Update_StaleEntityRefUnchanged_Saves`、`Update_ChangedToGoneEntity_Rejected`。
5. **反向分頁不洩漏**：來源模組的參照欄位對 floor 群組隱藏時，floor01 看被參照記錄的明細，沒有這個分頁，直接打 `related/{fieldId}/paged` 回 404；來源記錄超出資料範圍的不列出。→ Task 5 的 `Related_FieldHidden_NoTab`、`RelatedPaged_FieldHidden_NotFound`、`RelatedPaged_OutOfScope_Excluded`。

---

## 檔案結構

只列本階段新增或修改的檔案。後端路徑省略 `poc/backend/src/DynamicModules.` 前綴。

```
poc/shared/
  filter-operators.json                    + reference
  rule-cases.json                          + 參照案例

Domain/
  Entities/Dynamic/FieldType.cs            + Reference
  Entities/Dynamic/{ModuleKind,ReferenceOnDelete}.cs
  Entities/Dynamic/Module.cs               + Kind、HostEntityKey
  Entities/Dynamic/Record.cs               + HostKey
  Entities/Mock/{MockMold,MockCustomer}.cs
  ValueObjects/FieldConfig.cs              + RefTarget、OnDelete；ReferenceTarget
  Data/DynamicDbContext.cs                 + MockMolds、MockCustomers
  Data/Configurations/{Module,Record}Configuration.cs
  Data/Configurations/MockConfigurations.cs
  FieldTypes/FieldTypeContext（IFieldTypeHandler.cs）   + ValidRefKeys
  FieldTypes/Handlers/ReferenceHandler.cs
  FieldTypes/FieldTypeCapabilities.cs

Application/
  Common/ServiceResult.cs                  + ConflictWith
  Dynamic/References/IReferenceProvider.cs、ReferenceProviderRegistry.cs
  Dynamic/References/ReferenceResolver.cs  讀取：批次解析 references
  Dynamic/References/ReferenceValidator.cs 寫入：合法鍵集合
  Dynamic/References/ReferenceDeletePlanner.cs
  Dynamic/EntityService/**                 GET /entities、/entities/{key}/light
  Dynamic/FieldService/Data/FieldRules.cs  參照設定驗證
  Dynamic/ModuleService/**                 Kind、HostEntityKey
  Dynamic/RecordService/RecordIndexer.cs   參照一律索引
  Dynamic/RecordService/RecordValueProcessor.cs   傳 ValidRefKeys
  Dynamic/RecordService/Commands/**        存在檢查、HostKey、刪除行為
  Dynamic/RecordService/Queries/**         references、light、by-host
  Dynamic/RelationService/**               反向關聯 tabs 與 paged
  Dynamic/SchemaService/**                 SchemaField.Reference

Persistence/
  References/{MockMoldProvider,MockCustomerProvider}.cs
  Repositories/Dynamic/RecordRepository.cs + 依 RefKey 查來源、依 HostKey 查
  Dynamic/Query/DynamicQueryBuilder.cs     reference → RefKey
  Seed/SeedData.cs                         mock 資料
  Migrations/<時間戳>_Phase5Relations.cs

WebAPI/Controllers/v3/Dynamic/{EntitiesController,RelationsController}.cs
WebAPI/Controllers/v3/Dynamic/RecordsController.cs   + light、by-host
WebAPI/Controllers/ServiceResultExtensions.cs

poc/frontend/src/
  domain/dynamic/types.ts                  + reference、ReferenceInfo、RelatedTab
  domain/dynamic/rule-engine.ts            參照比對
  components/framework/PopupSelect.vue     （新）
  presentation/dynamic/field-type-registry.ts
  presentation/dynamic/components/{DynamicField,DynamicDetail}.vue
  presentation/dynamic/designer/FieldEditorDialog.vue
  presentation/dynamic/related/{RelatedTabs,DeleteBlockedDialog}.vue（新）
  services/api/dynamic-api.ts              + light、entities、related、byHost
  application/dynamic/queries/relation-queries.ts（新）
  pages/dynamic/{FormPage,DetailPage,ListPage}.vue
  pages/system/dynamic-modules/ModuleListPage.vue    建立對話框 + Kind、既有實體
```

---

## 任務

### Task 1：資料表、mock 既有實體與實體 API

**Files:**
- Create：
  - `Domain/Entities/Dynamic/ModuleKind.cs`、`Domain/Entities/Mock/{MockMold,MockCustomer}.cs`
  - `Domain/Data/Configurations/MockConfigurations.cs`
  - `Application/Dynamic/References/{IReferenceProvider,ReferenceProviderRegistry}.cs`
  - `Application/Dynamic/EntityService/Queries/{ListEntities,GetEntityLight}/*`
  - `Persistence/References/{MockMoldProvider,MockCustomerProvider}.cs`
  - `WebAPI/Controllers/v3/Dynamic/EntitiesController.cs`
- Modify：`Module.cs`、`Record.cs`、`DynamicDbContext.cs`、`{Module,Record}Configuration.cs`、`Persistence/DependencyInjection.cs`、`Persistence/Seed/SeedData.cs`
- Test：`ApplicationTests/Dynamic/EntityServiceTests.cs`、`WebAPITests/Dynamic/EntitiesApiTests.cs`

**Interfaces:**
- Produces：
  - `Module.Kind : ModuleKind`（預設 `Standalone`）、`Module.HostEntityKey : string?`；`Record.HostKey : string?`。
  - `interface IReferenceProvider { string EntityKey { get; } I18nText Name { get; } Task<LightPage> LightAsync(string? keyword, int page, int size, CancellationToken ct); Task<IReadOnlyDictionary<string, string>> TitlesAsync(IReadOnlyCollection<string> keys, CancellationToken ct); }`——`TitlesAsync` 只回存在且未刪除的鍵。
  - `record LightItem(string Key, string Title)`、`record LightPage(IReadOnlyList<LightItem> Items, int Total)`（放 `Application/Dynamic/References/`，Task 4 的模組 light 也用）。
  - `ReferenceProviderRegistry`：`IReferenceProvider? Get(string entityKey)`、`IReadOnlyList<IReferenceProvider> All`。provider 依賴 DbContext，所以 registry 和 provider 都註冊為 scoped。
  - 路由：`GET /api/v3/dynamic/entities`、`GET /api/v3/dynamic/entities/{entityKey}/light`。

- [ ] **Step 1：寫失敗測試**
  - `EntityServiceTests`：
    - `Light_Mold_KeywordMatchesNoOrName`：keyword `上蓋` 只回 `M-0001 外殼上蓋模`；keyword `M-000` 回 5 筆，依 `MoldNo` 排序。
    - `Light_DeletedMold_Excluded`：把 `M-0003` 的 `DeleteFlag` 設 true，total 變 4。
    - `Titles_MixedKeys_OnlyExisting`：`["M-0001","M-9999","M-0003"(已刪)]` 只回 `M-0001`。
    - `Light_SizeCappedAt50`。
  - `EntitiesApiTests`：
    - `List_Admin_ReturnsTwoEntities`：`design.mold`／模具、`sales.customer`／客戶。
    - `List_NonAdmin_Forbidden`：sales01 回 403。
    - `Light_UnknownEntity_NotFound`：404，`message` 為 `既有實體不存在`。
    - `Light_AnyUser_Ok`：floor01 查 `design.mold` 回 200。
- [ ] **Step 2：跑測試確認失敗**：`cd poc/backend && dotnet test --filter "EntityService|EntitiesApi"`。
- [ ] **Step 3：實作**
  - 實體、設定、mock provider、seed（冪等：依主鍵存在與否新增）。標題 `$"{No} {Name}"`；keyword 比對 No 或 Name 包含。
  - 產生 migration（Global Constraints 的指令與表）；確認 `Dynamic_Record` 的 filtered unique index 有 `DeleteFlag = 0` 條件。
  - `ListEntities` 只給 `IsSystemAdmin`（沿用設計器的判斷方式）。
- [ ] **Step 4：跑測試確認通過**：同上，再跑 `dotnet test poc/backend` 全部。
- [ ] **Step 5：Commit**：`feat: mock 既有實體、擴充模組欄位與實體 API`

### Task 2：參照欄位型別與設計器驗證

**Files:**
- Create：`Domain/FieldTypes/Handlers/ReferenceHandler.cs`、`Domain/Entities/Dynamic/ReferenceOnDelete.cs`
- Modify：
  - `Domain/Entities/Dynamic/FieldType.cs`、`Domain/ValueObjects/FieldConfig.cs`
  - `Domain/FieldTypes/{IFieldTypeHandler,FieldTypeRegistry,FieldTypeCapabilities}.cs`
  - `Application/Dynamic/FieldService/Data/FieldRules.cs`、`Application/Dynamic/FieldService/Commands/UpdateField/UpdateFieldHandler.cs`
  - `Application/Dynamic/SchemaService/**`（`SchemaField.Reference`）
  - `Application/Dynamic/RecordService/RecordIndexer.cs`、`Persistence/Dynamic/Query/DynamicQueryBuilder.cs`
  - `poc/shared/filter-operators.json`、`poc/shared/rule-cases.json`
- Test：`DomainTests/FieldTypes/ReferenceHandlerTests.cs`、`ApplicationTests/Dynamic/FieldServiceTests.cs`、`SchemaServiceTests.cs`、`DomainTests/Rules/` 既有的 rule-cases 跑法

**Interfaces:**
- Consumes：Task 1 的 `ReferenceProviderRegistry`、`Module.Kind`。
- Produces：
  - `FieldConfig.RefTarget : ReferenceTarget?`、`FieldConfig.OnDelete : ReferenceOnDelete?`；`record ReferenceTarget(string Kind, string Key)`，常數 `ReferenceTarget.ModuleKind = "module"`、`EntityKind = "entity"`。
  - `FieldTypeContext` 加 `IReadOnlySet<string>? ValidRefKeys`（預設 null）。
  - `ReferenceHandler.Normalize`：null／JsonValueKind.Null → `Ok(null)`；模組目標要 number、實體目標要非空 string；`ValidRefKeys` 非 null 且不含 `RefKey` 字串、且值不等於 `PreviousValue` → 錯誤 `參照的資料不存在或無權選取`。
  - `ReferenceHandler.ToIndex` → 單一 `IndexValue(0, null, null, null, refKey)`。
  - `static string? ReferenceHandler.KeyOf(JsonElement value)`：number → `GetInt64().ToString()`、string → 本身，其他 null。Task 3–6 都用它。
  - `SchemaField` 加 `SchemaReference? Reference`；`record SchemaReference(string Kind, string Key, I18nText Name, string LightUrl)`。

- [ ] **Step 1：寫失敗測試**
  - `ReferenceHandlerTests`：
    - `Normalize_ModuleTarget_NumberOk`、`Normalize_ModuleTarget_StringRejected`、`Normalize_EntityTarget_NumberRejected`。
    - `Normalize_NotInValidKeys_Rejected`、`Normalize_ValidKeysNull_Skipped`、`Normalize_UnchangedFromPrevious_NotChecked`（`ValidRefKeys` 是空集合、值等於 `PreviousValue` → Ok）。
    - `ToIndex_PutsStringInRefKey`：`42` → `RefKey = "42"`；`"M-0001"` → `"M-0001"`。
  - `filter-operators.json` 加 `"reference": ["eq","ne","in","notIn","isEmpty","isNotEmpty"]`；既有對照測試會自動涵蓋。
  - `rule-cases.json` 加三個案例：`referenceEqNumber`（值 42、條件 `eq 42` 成立）、`referenceInEntity`（`"M-0001"` in `["M-0001","M-0002"]` 成立）、`referenceEmpty`（沒值時 `isEmpty` 成立、`eq` 不成立）。
  - `FieldServiceTests`：
    - `AddField_Reference_ModuleTarget_Created`、`AddField_Reference_EntityTarget_Created`。
    - `AddField_Reference_UnknownModule_Rejected`、`AddField_Reference_ExtensionModule_Rejected`、`AddField_Reference_UnknownEntity_Rejected`：都是 `Config.RefTarget`／`參照目標不正確`。
    - `AddField_Reference_SelfModule_Allowed`。
    - `AddField_Reference_OnDeleteOnEntity_Rejected`：`刪除方式只能用在參照動態模組的欄位`。
    - `AddField_Reference_SetNullRequired_Rejected`：`刪除時清空不能用在必填欄位`。
    - `AddField_Reference_WithDefault_Rejected`、`AddField_Reference_Sortable_Rejected`：`Config`／`參照欄位不能設定預設值、長度、小數位、格式或排序`。
    - `UpdateField_Reference_ChangeTarget_Rejected`：`參照目標建立後不能更改`；`UpdateField_Reference_ChangeOnDelete_Ok`。
    - `AddField_Formula_UsesReference_Rejected`。
    - `UpdateModule_TitleToReference_Rejected`。
  - `SchemaServiceTests`：`Schema_ReferenceField_HasReferenceInfo`：模組目標的 `lightUrl` 為 `/modules/{key}/records/light`，實體目標為 `/entities/design.mold/light`，`name` 是目標名稱。
  - `RecordIndexTests`：`Index_ReferenceNotFilterable_StillIndexed`。
- [ ] **Step 2：跑測試確認失敗**：`cd poc/backend && dotnet test --filter "ReferenceHandler|FieldService|SchemaService|RecordIndex|RuleCases|IndexAndOperator"`。
- [ ] **Step 3：實作**
  - `FieldRules.ValidateConfig` 需要模組 repository 與 registry 才能驗目標：改由 AddField／UpdateField handler 先查好目標是否合法（模組存在且為 Standalone，或實體已登錄），再呼叫新函式 `FieldRules.CheckReference(FieldConfig config, bool targetOk) → ServiceResult?`；不改 `ValidateConfig` 既有簽章。
  - `FieldTypeCapabilities`：`reference` 可篩選、不可排序。
  - `RecordIndexer.ShouldIndex`：加 `|| field.Type == FieldType.Reference`。
  - `DynamicQueryBuilder.Column`：`Reference → RefKey`；篩選值轉成 `KeyOf` 字串後比對。
  - `ConditionParser` 對參照欄位用 `ValidRefKeys = null` 正規化條件值。
- [ ] **Step 4：跑測試確認通過**：同上，再跑全部後端測試。
- [ ] **Step 5：Commit**：`feat: 參照欄位型別與設計器驗證`

### Task 3：寫入時的參照存在檢查

**Files:**
- Create：`Application/Dynamic/References/ReferenceValidator.cs`
- Modify：`Application/Dynamic/RecordService/RecordValueProcessor.cs`、`Commands/CreateRecord/*`、`Commands/UpdateRecord/*`、`Persistence/Repositories/Dynamic/RecordRepository.cs`（`IRecordRepository` 加查詢）
- Test：`ApplicationTests/Dynamic/RecordReferenceWriteTests.cs`

**Interfaces:**
- Consumes：Task 2 的 `FieldTypeContext.ValidRefKeys`、`ReferenceHandler.KeyOf`；`IDynamicAccessGuard`。
- Produces：
  - `ReferenceValidator.ValidKeysAsync(Module module, IReadOnlyDictionary<string, JsonElement?> input, IReadOnlyDictionary<string, JsonElement?>? previous, CancellationToken ct) → Task<IReadOnlyDictionary<long, IReadOnlySet<string>>>`：只對「有送值、值非 null、和 previous 不同」的參照欄位查；鍵是 `Field.Id`。
  - `RecordValueProcessor.Process` 多一個參數 `IReadOnlyDictionary<long, IReadOnlySet<string>>? validRefKeys`（放最後、預設 null），組 `FieldTypeContext` 時帶入；沒出現在字典裡的欄位 `ValidRefKeys = null`。
  - `IRecordRepository.ListVisibleIdsAsync(long moduleId, IReadOnlyCollection<long> ids, IReadOnlyCollection<long>? ownerUserIds, CancellationToken ct) → Task<IReadOnlySet<long>>`：未刪除、在範圍內的 id。Task 4 也用。

- [ ] **Step 1：寫失敗測試**（`RecordReferenceWriteTests`，用 Application 測試既有的 in-memory SQLite 夾具）
  - `Create_RefToExistingRecord_Ok`：回應值為 number。
  - `Create_RefToDeletedRecord_Rejected`、`Create_RefToNonexistent_Rejected`：400、欄位鍵、`參照的資料不存在或無權選取`。
  - `Create_RefOutOfScope_Rejected`：目標模組給 sales 群組 Own 範圍，sales01 參照 sales02 擁有的記錄 → 400 同訊息。
  - `Create_RefTargetNoRead_Rejected`、`Create_RefTargetInactive_Rejected`。
  - `Create_RefToEntity_Ok`、`Create_RefToUnknownEntityKey_Rejected`。
  - `Update_StaleEntityRefUnchanged_Saves`：記錄參照 `M-0003`，之後 `M-0003` 被刪；送同樣的 `M-0003` 加改其他欄位 → 200。
  - `Update_ChangedToGoneEntity_Rejected`：改成已刪的 `M-0004` → 400。
  - `Update_RefCleared_Ok`：送 null → 值與索引都移除。
- [ ] **Step 2：跑測試確認失敗**：`dotnet test poc/backend --filter RecordReferenceWrite`。
- [ ] **Step 3：實作**
  - 模組目標：`guard.AuthorizeAsync(targetModule, Read)`；被拒或模組未啟用 → 該欄位合法集合為空；否則 `ListVisibleIdsAsync`。實體目標：`provider.TitlesAsync(keys).Keys`。
  - Create／Update handler 先呼叫 `ValidKeysAsync`，再呼叫 `Process`。
- [ ] **Step 4：跑測試確認通過**：同上，再跑全部後端測試。
- [ ] **Step 5：Commit**：`feat: 寫入參照時檢查目標存在與權限`

### Task 4：讀取的參照資訊與模組輕量查詢

**Files:**
- Create：`Application/Dynamic/References/ReferenceResolver.cs`、`Application/Dynamic/RecordService/Queries/GetLightRecords/*`
- Modify：`RecordService/Data/RecordResponses.cs`（`DynamicRecordResponse` 加 `References`、`HostKey`）、`RecordMapper`、`Queries/GetRecord/*`、`Queries/GetPagedRecords/*`、Create／Update 回應、`RecordsController.cs`
- Test：`ApplicationTests/Dynamic/ReferenceReadTests.cs`、`WebAPITests/Dynamic/RecordsApiTests.cs`

**Interfaces:**
- Consumes：Task 3 的 `ListVisibleIdsAsync`；Task 1 的 `LightPage`、registry。
- Produces：
  - `record ReferenceInfo(string Key, string? Title, string State)`；`DynamicRecordResponse` 末端加 `IReadOnlyDictionary<string, ReferenceInfo> References`、`string? HostKey`。
  - `ReferenceResolver.ResolveAsync(Module module, EffectivePermissions perms, IReadOnlyList<Record> records, CancellationToken ct) → Task<IReadOnlyDictionary<long, Dictionary<string, ReferenceInfo>>>`（鍵是 `Record.Id`）：依目標分組，每個目標一次查詢。
  - 路由 `GET /modules/{key}/records/light?keyword=&page=&size=` → `LightPage`（JSON `{ items: [{ key, title }], total }`）。

- [ ] **Step 1：寫失敗測試**
  - `ReferenceReadTests`：
    - `Get_RefOk_HasTitle`：`State = "ok"`、`Title` 為目標記錄標題。
    - `Get_RefTargetDeleted_Missing`、`Get_EntityGone_Missing`：`Title = null`。
    - `Get_RefTargetOutOfScope_Restricted`、`Get_RefTargetTitleHidden_Restricted`、`Get_RefTargetNoRead_Restricted`。
    - `Get_RefFieldHidden_NoReferenceEntry`：參照欄位本身對使用者隱藏 → `References` 沒有這個鍵。
    - `Paged_MixedTargets_AllResolved`：一頁 20 筆分別參照 2 個模組與 1 個實體，每筆的 `references` 都正確。批次查詢（每個目標一次）是實作要求，由 task review 檢查程式碼，不寫查詢次數測試。
  - `RecordsApiTests`：
    - `Light_KeywordAndPaging`：3 筆記錄，keyword 只命中 1 筆；`size=2` 第 1 頁 2 筆、`total = 3`。
    - `Light_OutOfScope_Excluded`。
    - `Light_TitleHidden_Forbidden`：403。
    - `Light_InactiveModule_NotFound`：404 `模組不存在或未啟用`。
- [ ] **Step 2：跑測試確認失敗**：`dotnet test poc/backend --filter "ReferenceRead|RecordsApi"`。
- [ ] **Step 3：實作**：狀態判定順序：目標模組未啟用或無 `Read` 或標題欄位隱藏 → `restricted`；記錄不存在或已刪 → `missing`；不在範圍 → `restricted`；其餘 `ok`。實體：查得到 `ok`，否則 `missing`。
- [ ] **Step 4：跑測試確認通過**：同上，再跑全部後端測試。
- [ ] **Step 5：Commit**：`feat: 記錄回應帶參照資訊與模組輕量查詢`

### Task 5：反向關聯 API

**Files:**
- Create：`Application/Dynamic/RelationService/Queries/{GetRelatedTabs,GetRelatedPaged}/*`、`WebAPI/Controllers/v3/Dynamic/RelationsController.cs`
- Modify：`IRecordRepository`／`RecordRepository`（依 RefKey 查來源）、`IModuleRepository`／`ModuleRepository`（找指向某模組的參照欄位）
- Test：`ApplicationTests/Dynamic/RelationServiceTests.cs`、`WebAPITests/Dynamic/RelationsApiTests.cs`

**Interfaces:**
- Consumes：Task 4 的 `ReferenceResolver` 與分頁回應組裝。
- Produces：
  - `IModuleRepository.ListIncomingReferenceFieldsAsync(string targetModuleKey, CancellationToken ct) → Task<IReadOnlyList<(Module Source, Field Field)>>`：所有模組（含未啟用）的啟用中參照欄位；呼叫端自行過濾來源模組是否啟用。Task 6 也用（不過濾未啟用）。
  - `IRecordRepository.QueryByRefAsync(long fieldId, string refKey, IReadOnlyCollection<long>? ownerUserIds, int page, int size, CancellationToken ct) → Task<(IReadOnlyList<Record> Items, int Total)>`
  - `record RelatedTab(long FieldId, string FieldKey, I18nText FieldLabel, string ModuleKey, I18nText ModuleName, bool CanAdd)`
  - 路由：`GET /records/{id}/related`、`GET /records/{id}/related/{fieldId}/paged?page=&size=`。

- [ ] **Step 1：寫失敗測試**
  - `RelationServiceTests`：
    - `Related_TwoIncomingFields_TwoTabs`：維修紀錄、保養紀錄都指向申請 → 兩個分頁，`canAdd` 正確。
    - `Related_SourceModuleInactive_NoTab`、`Related_SourceFieldInactive_NoTab`。
    - `Related_FieldHidden_NoTab`：floor 群組看不到來源欄位。
    - `Related_NoSourceRead_NoTab`。
    - `Related_RecordNotReadable_NotFound`：本記錄超出範圍 → 404 `記錄不存在`。
    - `RelatedPaged_ListsSources_DescById`、`RelatedPaged_ExcludesDeleted`。
    - `RelatedPaged_OutOfScope_Excluded`：floor01 範圍 Own，只看到自己建的來源記錄。
    - `RelatedPaged_FieldHidden_NotFound`、`RelatedPaged_FieldNotTargetingModule_NotFound`：404 `關聯不存在`。
  - `RelationsApiTests`：`Related_Endpoints_Roundtrip`：兩個端點 200，分頁回應含 `references`。
- [ ] **Step 2：跑測試確認失敗**：`dotnet test poc/backend --filter "RelationService|RelationsApi"`。
- [ ] **Step 3：實作**：tabs 先讀本記錄（沿用 GetRecord 的檢查），再對每個候選欄位跑 `guard.AuthorizeAsync(source, Read)` 與 `CanSee(field.Id)`；paged 用同一套判斷找出欄位，失敗一律 404 `關聯不存在`。
- [ ] **Step 4：跑測試確認通過**：同上，再跑全部後端測試。
- [ ] **Step 5：Commit**：`feat: 反向關聯分頁 API`

### Task 6：刪除行為（Restrict、SetNull、Cascade）

**Files:**
- Create：`Application/Dynamic/References/ReferenceDeletePlanner.cs`
- Modify：`Application/Common/ServiceResult.cs`（`ConflictWith`）、`WebAPI/Controllers/ServiceResultExtensions.cs`、`RecordService/Commands/DeleteRecord/DeleteRecordHandler.cs`
- Test：`ApplicationTests/Dynamic/ReferenceDeleteTests.cs`、`WebAPITests/ServiceResultMappingTests.cs`

**Interfaces:**
- Consumes：Task 5 的 `ListIncomingReferenceFieldsAsync`；`RecordIndexer` 重建單一欄位索引（`ReplaceFieldIndexAsync`）。
- Produces：
  - `ReferenceDeletePlanner.PlanAsync(Record root, Module rootModule, CancellationToken ct) → Task<DeletePlan>`
  - `record DeletePlan(IReadOnlyList<Record> ToDelete, IReadOnlyList<(Record Record, Field Field)> ToClear, IReadOnlyList<BlockingRef> Blocking, int BlockingTotal, bool DepthExceeded)`
  - `record BlockingRef(string ModuleKey, I18nText ModuleName, I18nText FieldLabel, long RecordId, string? Title)`——`Title` 由 handler 依使用者權限決定填或 null。
  - `ServiceResult.ConflictWith(string message, object data)`；409 body `{ message, references, total }`。

- [ ] **Step 1：寫失敗測試**（`ReferenceDeleteTests`）
  - `Delete_NoReferences_Deletes`（回歸）。
  - `Delete_Restrict_Blocked409`：409、`message` 為 `還有其他資料參照這筆記錄，不能刪除`、`references[0]` 帶 `moduleKey`、`recordId`、`title`、`total = 1`；被刪記錄 `DeleteFlag` 仍 false。
  - `Delete_Restrict_SourceUnreadable_TitleNull`。
  - `Delete_Restrict_MoreThan10_ListCapped`：12 筆來源 → `references` 10 筆、`total = 12`。
  - `Delete_RestrictSourceDeleted_NotBlocking`：來源已軟刪 → 可刪。
  - `Delete_RestrictFieldInactive_NotBlocking`。
  - `Delete_SetNull_ClearsValueAndIndex`：來源的值與 `RecordIndex` 那列都消失、`RowVersion` 變了。
  - `Delete_Cascade_DeletesSources`。
  - `Delete_CascadeChainHitsRestrict_NothingChanged`：A ←cascade B ←restrict C；刪 A → 409；A、B 未刪，C 不變。
  - `Delete_CascadeCycle_Terminates`：模組 X、Y 互相 cascade 參照、資料成環 → 刪除成功、每筆只刪一次。
  - `Delete_SelfReferenceCascade_DeletesChain`：「上層設備」cascade，3 層鏈 → 全刪。
  - `Delete_CascadeDepthOver5_Rejected`：7 筆的 cascade 鏈 → 409 `連帶刪除超過 5 層，請先手動刪除下層資料`，全部未刪。
  - `Delete_CascadeIgnoresSourceDeletePermission`：使用者對來源模組沒有 `Delete`，照樣連帶刪除。
  - `ServiceResultMappingTests`：`ConflictWith_MapsTo409WithData`。
- [ ] **Step 2：跑測試確認失敗**：`dotnet test poc/backend --filter "ReferenceDelete|ServiceResultMapping"`。
- [ ] **Step 3：實作**
  - 規劃演算法（BFS，記憶體內，最後才寫入）：

    ```
    queue = [(root, depth 0)]; visited = {root.Id}
    while queue:
      (rec, d) = pop
      for (srcModule, field) in incoming(rec.ModuleKey):     // 每個模組鍵快取一次
        sources = 未刪除、RefKey = rec.Id 的來源記錄（不套範圍）
        sources -= visited 中已排入刪除的
        switch field.OnDelete ?? Restrict:
          Restrict → Blocking += sources
          SetNull  → ToClear += sources（若同一筆之後也被刪，套用時跳過清空）
          Cascade  → for s in sources not in visited:
                       if d + 1 > 5: DepthExceeded = true; break all
                       visited += s; ToDelete += s; queue += (s, d + 1)
    ```

  - Handler：權限與 RowVersion 檢查不變；`Blocking` 非空 → `ConflictWith`（取前 10 筆，標題用 Task 4 的可見性判斷）；`DepthExceeded` → `Conflict`；否則在一個交易內套用：根與 `ToDelete` 軟刪、`ToClear` 移值重建索引、全部遞增 `RowVersion` 並蓋稽核欄位。
- [ ] **Step 4：跑測試確認通過**：同上，再跑全部後端測試。
- [ ] **Step 5：Commit**：`feat: 被參照記錄的刪除行為`

### Task 7：擴充模組

**Files:**
- Modify：
  - `ModuleService/Commands/CreateModule/*`（`Kind`、`HostEntityKey`）、`UpdateModule/*`（擋 `TitleFieldId`）、模組回應加 `Kind`、`HostEntityKey`
  - `RecordService/Commands/CreateRecord/*`（`HostKey`）、`Queries/GetRecordByHost/*`（新）
  - `IRecordRepository`／`RecordRepository`（`GetByHostAsync`）
  - `RecordsController.cs`（`CreateRecordBody` 加 `hostKey`、`by-host` 路由）
- Test：`ApplicationTests/Dynamic/ExtensionModuleTests.cs`、`WebAPITests/Dynamic/ModulesApiTests.cs`

**Interfaces:**
- Consumes：Task 1 的 registry、`Module.Kind`、`Record.HostKey`；Task 4 的 `DynamicRecordResponse.HostKey`。
- Produces：
  - `CreateModuleRequest(string Key, I18nText Name, string? Icon, string? Section, ModuleKind Kind = ModuleKind.Standalone, string? HostEntityKey = null)`
  - `CreateRecordRequest(string ModuleKey, Dictionary<string, JsonElement?> Values, string? HostKey = null)`
  - `IRecordRepository.GetByHostAsync(long moduleId, string hostKey, CancellationToken ct) → Task<Record?>`（未刪除）
  - 路由 `GET /modules/{key}/records/by-host/{hostKey}`。
  - `ModuleSchemaResponse` 與模組詳情回應加 `Kind`、`HostEntityKey`（Task 9、10 的前端要用）。

- [ ] **Step 1：寫失敗測試**（`ExtensionModuleTests`）
  - `CreateModule_Extension_NoTitleField`：建立成功、`TitleFieldId = null`、仍有預設區塊。
  - `CreateModule_Extension_NoHost_Rejected`、`CreateModule_Extension_UnknownHost_Rejected`：`HostEntityKey`／`擴充模組必須指定既有實體`。
  - `CreateModule_Extension_Duplicate_Conflict`：409 `這個既有實體已經有擴充模組`。
  - `UpdateModule_Extension_SetTitle_Rejected`：`TitleFieldId`／`擴充模組的標題取自既有資料`。
  - `CreateRecord_Extension_TitleIsHostSnapshot`：`hostKey = "M-0001"` → `title = "M-0001 外殼上蓋模"`、`hostKey` 在回應裡。
  - `CreateRecord_Extension_NoHostKey_Rejected`、`CreateRecord_Extension_UnknownHost_Rejected`。
  - `CreateRecord_Extension_DuplicateHost_Conflict`：409 `這筆既有資料已經有擴充資料`。
  - `CreateRecord_Extension_AfterDelete_Allowed`：刪掉後同一 host 可再建。
  - `GetByHost_Found`、`GetByHost_Missing_NotFound`、`GetByHost_OutOfScope_NotFound`。
  - `Schema_ExtensionModule_HasHostEntity`：schema 的 `kind = "extension"`、`hostEntityKey = "design.mold"`。
  - `CreateRecord_Standalone_HostKeyIgnored`。
- [ ] **Step 2：跑測試確認失敗**：`dotnet test poc/backend --filter "ExtensionModule|ModulesApi"`。
- [ ] **Step 3：實作**：重複 host 先查 `GetByHostAsync`，再靠唯一索引兜底（捕捉 `DbUpdateException` 轉同一個 409）。
- [ ] **Step 4：跑測試確認通過**：同上，再跑全部後端測試。
- [ ] **Step 5：Commit**：`feat: 既有實體的擴充模組`

### Task 8：前端型別、API 與 PopupSelect

**Files:**
- Create：`components/framework/PopupSelect.vue`（+ `.vitest.spec.ts`）、`application/dynamic/queries/relation-queries.ts`
- Modify：`domain/dynamic/types.ts`、`domain/dynamic/rule-engine.ts`、`services/api/dynamic-api.ts`、`presentation/dynamic/field-type-registry.ts`（+ spec）、`presentation/dynamic/build-zod-schema.ts`、`presentation/dynamic/list/FilterPanel.vue`（篩選輸入）、i18n 四語系
- Test：`PopupSelect.vitest.spec.ts`、`field-type-registry.vitest.spec.ts`、`rule-engine` 既有的共用案例 spec、`dynamic-api` 若有 spec 則補

**Interfaces:**
- Consumes：後端 Task 2、4、5、7 的回應格式。
- Produces：
  - 型別：`FieldType` 加 `'reference'`；`SchemaReference { kind: 'module' | 'entity'; key: string; name: I18nText; lightUrl: string }`；`SchemaField.reference?: SchemaReference`；`ReferenceInfo { key: string; title: string | null; state: 'ok' | 'missing' | 'restricted' }`；`DynamicRecord.references: Record<string, ReferenceInfo>`、`hostKey: string | null`；`LightItem`、`LightPage`、`RelatedTab`；`DeleteBlocked { message; references: BlockingRef[]; total }`。
  - `dynamicApi.light(lightUrl, { keyword, page, size })`、`entities()`、`related(recordId)`、`relatedPaged(recordId, fieldId, page, size)`、`recordByHost(moduleKey, hostKey)`；`createRecord` 多帶 `hostKey`。
  - `PopupSelect` props：`modelValue: string | number | null`、`lightUrl: string`、`valueKind: 'number' | 'string'`、`displayTitle?: string | null`、`readonly?`、`label?`；emits `update:modelValue`。對話框內有關鍵字輸入（300ms debounce）、分頁清單、清除鈕；選中回傳 `valueKind === 'number' ? Number(key) : key`。
  - registry 的 `reference` 條目：表單元件 PopupSelect；`format` 用記錄的 `references`（無則顯示鍵）；zod：number 或非空字串；篩選輸入對 `eq`／`ne` 用 PopupSelect、`in`／`notIn` 用可多選版本（PopupSelect 加 `multiple` prop，值為陣列）。
  - `rule-engine.ts`：參照欄位比對以 `String(value)`；共用案例三個新案例通過。

- [ ] **Step 1：寫失敗測試**
  - `PopupSelect.vitest.spec.ts`：`opens dialog and lists light items`、`keyword triggers search after debounce`、`select emits number when valueKind is number`、`clear emits null`、`readonly does not open dialog`、`multiple emits array`。
  - `field-type-registry.vitest.spec.ts`：`reference zod accepts number for module target and string for entity target`、`reference filter operators match shared json`。
  - 規則引擎共用案例 spec 自動跑新案例。
- [ ] **Step 2：跑測試確認失敗**：`cd poc/frontend && bunx vitest run PopupSelect field-type-registry rule-engine`。
- [ ] **Step 3：實作**：PopupSelect 用 Quasar `q-dialog` + `q-list`，資料走 TanStack Query（key `['light', lightUrl, keyword, page]`）。
- [ ] **Step 4：跑測試確認通過**：`bunx vitest run && bunx vue-tsc --noEmit`。
- [ ] **Step 5：Commit**：`feat(frontend): 參照型別、輕量查詢 API 與 PopupSelect`

### Task 9：設計器：參照設定與擴充模組

**Files:**
- Modify：`presentation/dynamic/designer/FieldEditorDialog.vue`（+ spec）、`pages/system/dynamic-modules/ModuleListPage.vue`（+ spec 若有）、`pages/system/dynamic-modules/ModuleEditorPage.vue`（擴充模組隱藏標題欄位選擇、顯示宿主實體）、i18n
- Test：`FieldEditorDialog.vitest.spec.ts`、`ModuleListPage.vitest.spec.ts`（沒有就新建）

**Interfaces:**
- Consumes：Task 8 的 `dynamicApi.entities()`、模組清單 API（既有）。

- [ ] **Step 1：寫失敗測試**
  - `FieldEditorDialog`：
    - `reference type shows target kind, target select and onDelete`。
    - `target select lists standalone modules for module kind and entities for entity kind`（不列擴充模組）。
    - `onDelete hidden for entity target`。
    - `editing existing reference field disables target`。
    - `default value and sortable hidden for reference`。
    - `submits config.refTarget and config.onDelete`。
  - `ModuleListPage`：`create dialog with extension kind requires host entity`、`submits kind and hostEntityKey`。
- [ ] **Step 2：跑測試確認失敗**：`bunx vitest run FieldEditorDialog ModuleListPage`。
- [ ] **Step 3：實作**：刪除方式選項文字 `designer.reference.onDelete.{restrict,setNull,cascade}`，各附一行說明；後端錯誤沿用既有 `api-error.ts` 掛到對應欄位。
- [ ] **Step 4：跑測試確認通過**：`bunx vitest run && bunx vue-tsc --noEmit`。
- [ ] **Step 5：Commit**：`feat(frontend): 設計器參照設定與擴充模組`

### Task 10：表單、明細、列表的參照顯示與帶入

**Files:**
- Modify：`presentation/dynamic/components/{DynamicField,DynamicDetail}.vue`、`pages/dynamic/{FormPage,DetailPage,ListPage}.vue`（+ 既有 spec）、i18n
- Test：`DetailPage.vitest.spec.ts`、`FormPage.vitest.spec.ts`、`ListPage.vitest.spec.ts`

**Interfaces:**
- Consumes：Task 8 的型別、PopupSelect、registry。

- [ ] **Step 1：寫失敗測試**
  - `DetailPage`：`reference ok renders link to target record`（模組目標連到 `/dynamic/{key}/{id}`；實體目標只顯示文字）、`reference missing shows 已失效`、`reference restricted shows 無權限檢視`、`extension record shows host title`。
  - `ListPage`：`reference column shows title from references`。
  - `FormPage`：
    - `edit form passes reference title to PopupSelect`。
    - `prefill query sets reference initial value`：`?prefill=f_12:42` → 欄位 `f_12` 初值 42。
    - `prefill ignores non-reference or unknown fields`。
    - `extension module new form requires host pick`：顯示宿主實體的 PopupSelect（用 schema 的模組 `hostEntityKey` 組 `/entities/{key}/light`），沒選不能送出；送出帶 `hostKey`。
- [ ] **Step 2：跑測試確認失敗**：`bunx vitest run DetailPage FormPage ListPage`。
- [ ] **Step 3：實作**：宿主挑選用 schema 的 `kind`、`hostEntityKey`（Task 7 提供）；`prefill` 格式 `{fieldKey}:{value}`，模組目標轉 number。
- [ ] **Step 4：跑測試確認通過**：`bunx vitest run && bunx vue-tsc --noEmit`。
- [ ] **Step 5：Commit**：`feat(frontend): 表單與明細的參照顯示、帶入與擴充模組宿主`

### Task 11：反向關聯分頁與刪除被擋對話框

**Files:**
- Create：`presentation/dynamic/related/{RelatedTabs,DeleteBlockedDialog}.vue`（+ spec）
- Modify：`pages/dynamic/DetailPage.vue`（+ spec）、`pages/dynamic/ListPage.vue`（列表刪除也走同一個對話框）、i18n
- Test：`RelatedTabs.vitest.spec.ts`、`DeleteBlockedDialog.vitest.spec.ts`、`DetailPage.vitest.spec.ts`

**Interfaces:**
- Consumes：Task 8 的 `related`、`relatedPaged`、`DeleteBlocked`。

- [ ] **Step 1：寫失敗測試**
  - `RelatedTabs`：`renders one tab per related field with module and field label`、`tab lists source records with paging`、`add button navigates with prefill when canAdd`、`no add button when canAdd false`、`renders nothing when no tabs`。
  - `DeleteBlockedDialog`：`shows message, list and remaining count`（`total = 12`、列 10 筆時顯示「還有 2 筆」）、`null title shows 無權限檢視`、`item links to source record`。
  - `DetailPage`：`delete 409 with references opens blocked dialog`、`delete 409 depth message shows notify`（沒有 `references` 的 409 照既有方式顯示訊息）。
- [ ] **Step 2：跑測試確認失敗**：`bunx vitest run RelatedTabs DeleteBlockedDialog DetailPage`。
- [ ] **Step 3：實作**：分頁放在明細內容下方（`q-tabs` + 每個分頁一個精簡列表：標題、修改時間，點擊進明細）；新增鈕導向 `/dynamic/{moduleKey}/new?prefill={fieldKey}:{recordId}`。
- [ ] **Step 4：跑測試確認通過**：`bunx vitest run && bunx vue-tsc --noEmit && bunx quasar build`。
- [ ] **Step 5：Commit**：`feat(frontend): 反向關聯分頁與刪除被擋提示`

### Task 12：驗收

**Files:**
- Create：`poc/backend/tests/DynamicModules.WebAPITests/Phase5AcceptanceTests.cs`、`docs/acceptance/phase5.md`、`docs/acceptance/phase5/*.png`、`docs/superpowers/plans/2026-11-03-phase5-rulings.md`
- Modify：`README.md`（進度表第 5 列、文件表）、`docs/manual.md`（新增「關聯」章節：參照欄位、刪除方式、反向分頁、擴充模組）、spec §15（新增條目，見下）

- [ ] **Step 1：寫驗收測試** `Phase5_Scenario`（固定時鐘 2026-11-04），以 API 走完並逐步斷言：
  1. admin 建 `repair-req`（模具維修申請）：申請人（標題）、模具（`reference` → `entity:design.mold`，必填）、故障說明（`textarea`）；啟用。
  2. 建 `repair-log`（維修紀錄）：處理內容（標題）、申請單（`reference` → `module:repair-req`，`onDelete = restrict`）、工時（`decimal`）；啟用。
  3. 建擴充模組 `mold-ext`（`extension`、`design.mold`）：保養週期（`integer`）、上次保養日（`date`）；啟用。
  4. 申請 A 參照 `M-0001` → 201，`references.f_x` 為 `{ key: "M-0001", title: "M-0001 外殼上蓋模", state: "ok" }`。參照 `M-9999` → 400 `參照的資料不存在或無權選取`。
  5. 兩筆維修紀錄參照申請 A；`GET /records/{A}/related` 有一個分頁（維修紀錄），`paged` 回 2 筆。
  6. 刪除申請 A → 409 `還有其他資料參照這筆記錄，不能刪除`，`total = 2`；刪掉兩筆維修紀錄後，再刪申請 A → 200。
  7. `mold-ext` 為 `M-0001` 建擴充資料 → 201，`title = "M-0001 外殼上蓋模"`；再建一次 → 409 `這筆既有資料已經有擴充資料`；`GET /modules/mold-ext/records/by-host/M-0001` → 200，保養週期正確。
  8. 權限：`repair-log` 給 floor 群組 Read／Add、範圍 Own；floor01 建一筆維修紀錄參照申請 B，admin 也建一筆；floor01 看申請 B 的反向分頁只有 1 筆。
  9. 把 `Mock_Mold` 的 `M-0002` 設為已刪除（測試直接改 DB）；參照 `M-0002` 的申請 → `state = "missing"`、`title = null`；只改故障說明照樣 200。
- [ ] **Step 2：跑驗收與全部測試**：
  - `dotnet test poc/backend --filter Phase5_Scenario` → 通過。
  - `dotnet test poc/backend` → 全部通過，記下各專案數字。
  - `cd poc/frontend && bunx vitest run && bunx vue-tsc --noEmit && bunx quasar build` → 全部通過。
- [ ] **Step 3：實機走查**（`poc/start.command`，Chrome），逐項截圖到 `docs/acceptance/phase5/`：
  1. 設計器加參照欄位（模組目標與實體目標）、選刪除方式；擴充模組的建立對話框。
  2. 表單用 PopupSelect 搜尋並挑模具。
  3. 明細顯示模具標題；維修紀錄的申請單是連結，點了到申請明細。
  4. 申請明細的「維修紀錄」分頁，按新增後表單已帶好申請單。
  5. 刪除有維修紀錄的申請 → 對話框列出參照來源。
  6. 模具擴充資料：新增時挑模具、存檔後明細標題是模具名稱。
  7. 已失效的模具顯示「已失效」。
  8. floor01 看反向分頁只有自己的記錄。
- [ ] **Step 4：寫文件**
  - `docs/acceptance/phase5.md`：照 `phase4.md` 的格式（自動測試表、情境、走查表、逐項觀察、截圖表、已知限制、實作裁決）。
  - `2026-11-03-phase5-rulings.md`：執行中的裁決（R1…）。
  - spec §15 新增：
    - 第 28 條：mock 既有實體不檢查權限；上線後 light 與存在檢查要依宿主實體的 `ProgramCode` 套權限與資料範圍。
    - 第 29 條：連帶刪除不檢查使用者對來源模組的刪除權限，只看發起刪除的那筆；正式版要決定是否改成「無權刪除來源就視同 Restrict」。
    - 第 30 條：SetNull 本階段不寫歷程，第 7 階段補。
  - README 進度表加第 5 列、文件表加第 5 階段計畫與裁決；manual 加「關聯」一節。
- [ ] **Step 5：Commit**：驗收測試、文件、截圖逐檔 add，訊息 `docs: 第 5 階段驗收`。
