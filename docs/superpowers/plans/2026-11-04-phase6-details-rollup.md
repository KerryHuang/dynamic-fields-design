# 動態模組 POC 第 6 階段：表頭與明細、彙總欄位 實作計畫

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 參照欄位可以設成「明細」：子模組變成父模組的明細，父記錄的新增、編輯頁直接逐列編輯子記錄，表頭與所有明細在同一個交易存檔；父模組可以加「彙總」欄位，依指向它的子記錄算加總、筆數、最小、最大，子記錄怎麼變動都會重算，彙總值也能被公式引用。

**Architecture:**
- 明細是參照欄位的屬性（`Config.Relation = detail`），不是新的模組種類。一個模組最多一個明細欄位；有明細欄位的模組就是「明細模組」，它的權限、資料範圍、Pricing 全部沿用表頭模組，對通用記錄 API 不可見，只能透過表頭記錄讀寫。
- 表頭存檔的請求多一個 `details`。handler 先逐列處理明細（正規化、公式、規則），再用記憶體裡的明細算出表頭的彙總，最後處理表頭（公式、規則），任何一處有錯就整張不存。
- 彙總欄位型別 `rollup`。計算本身是 Domain 裡的純函式 `RollupCalculator`，有兩條路會用到它：
  - 表頭存檔時：在記憶體裡用請求中的明細計算。
  - 一般參照的子記錄有任何寫入時：寫入後從資料庫重讀子記錄，由 `RollupRecalc` 重算受影響的父記錄。寫入包括新增、修改、刪除、連帶刪除、清空、改指向，以及子模組的公式重算。
- 彙總只支援一層：彙總的對象不能是彙總欄位，也不能是引用了彙總欄位的公式。重算因此不會往上連鎖。

**Tech Stack:** 沿用第 1–5 階段（見 `docs/superpowers/plans/2026-11-03-phase1-foundation.md` 的 Tech Stack），不加新套件。

**Spec:** `docs/superpowers/specs/2026-11-03-dynamic-modules-design.md`。本階段對應：
- §13.2 第 6 項。
- §9「表頭與明細」。
- §8「彙總」。
- §7 的 `rollup` 一列。
- §13.1 的 `Config.scope = "parent"`（項次在單內編號）。
- §3 目標 13、14。
- Demo 情境一的明細、彙總、公式、全單唯讀與 Pricing 部分。訂單範本本身是第 8 階段，本階段的驗收以 API 手動建出同樣的訂單模組。

**前置文件**：第 1–5 階段裁決（`docs/superpowers/plans/2026-11-03-phase{1,2,3,4,5}-rulings.md`）全部仍然有效。

**執行時的補充**（見 [`2026-11-04-phase6-rulings.md`](2026-11-04-phase6-rulings.md)）：Task 9 之後追加 Task 9b「下拉選單打字篩選」（R12）；稅額公式不除以 100，百分比欄位在公式裡已是小數（R19），下文的 `/ 100` 以裁決為準。

## 本階段範圍

**做：**
- **明細**：
  - 參照欄位的 `Relation` 可設為 `detail`。
  - 明細模組不出現在選單，通用記錄 API 看不到它。
  - 表頭的新增、編輯頁有逐列編輯的明細表格，明細頁唯讀顯示明細。
  - 表頭與明細一起存檔。
  - 刪除表頭時連帶刪除明細。
- **彙總欄位**（`rollup`）：
  - 函數有 `sum`、`count`、`min`、`max`，可以加條件。
  - 來源可以是明細，也可以是一般參照（使用者裁決）。
  - 結果可以被公式引用、可以篩選排序；結果是金額時受 Pricing 遮罩。
- **項次在表頭內編號**：明細模組的自動編號可設 `Scope = parent`，每張表頭從 1 開始。
- **設計器**：
  - 參照欄位的「明細」選項、自動編號的「在表頭內編號」、彙總欄位的設定。
  - 模組清單標示明細模組；權限頁對明細模組只開放欄位例外。

**不做（留給後續階段）：**
- 表單即時計算公式與彙總：前端沿用第 4 階段，不算公式，存檔後才顯示新值（已知限制，見下）。
- 彙總的歷程、表頭與明細的變更歷程：第 7 階段。
- 匯出、匯入明細：第 7 階段。
- 訂單範本：第 8 階段。
- 多層明細（明細的明細）、孫記錄彙總：spec 明定只支援一層。
- 依讀者權限計算的彙總值：彙總一律算全部未刪除的子記錄，不看讀者的資料範圍（使用者裁決接受推測風險，§15 新增一條）。

## 已定決策（我的判斷，spec 沒說死的部分）

1. **明細的設定（`FieldConfig.Relation`）**：
   - 值是 `lookup` 或 `detail`。null 視同 `lookup`。只能用在 `RefTarget.Kind = module`。
   - **建立後不能改**，和 `RefTarget` 一樣。
   - `detail` 欄位由伺服器強制 `Required = true`、`OnDelete = cascade`。前端送來的值不同時直接覆蓋，不算錯。
   - 以下情況不能設為明細，錯誤鍵 `Config.Relation`，訊息 `這個欄位不能設為明細：{原因}`。依序檢查，回報第一個成立的原因：
     - `只能參照動態模組`
     - `表頭模組不能是擴充模組或其他模組的明細`：目標是擴充模組、是明細模組，或是自己。
     - `這個模組已經有表頭`：本模組已有啟用或停用中的明細欄位。
     - `這個模組已經有資料`：有任何記錄，含已刪除的。
     - `這個模組有自己的明細，不能再當明細`：有別的模組以本模組為表頭。
     - `這個模組被其他欄位參照`：有任何參照欄位指向本模組。
     - `擴充模組不能當明細`。
   - 參照欄位的目標不能是明細模組：沿用 `參照目標不正確`。
   - 明細欄位不能停用：`明細的表頭欄位不能停用`（鍵 `Status`）。
2. **明細模組的存取**：
   - 判斷方式：`ModuleRelations.ParentOf(Module child)` 回傳它啟用中的明細欄位與表頭模組鍵。明細欄位停用不了，所以「有明細欄位」就是明細模組。
   - **權限沿用表頭**：
     - 動作軸、資料範圍、Pricing 都用表頭模組的 ProgramCode 與 `ModuleDataScope`。
     - 欄位例外（隱藏、唯讀）與選項範圍用明細模組自己的設定。
     - 實作是 `IDynamicAccessGuard.AuthorizeDetailAsync(Module parent, Module child, PermissionAction action, CancellationToken ct) → Task<AccessResult>`：用表頭的動作軸、範圍、Pricing，加上明細模組的欄位例外，算出明細模組的 `EffectivePermissions`。
   - **動作的對應**：
     - 新增表頭時一併新增明細，需要表頭的 `Add`。
     - 編輯表頭時新增、修改、刪除明細列，都只需要表頭的 `Edit`，不另外要 `Delete`，因為刪明細列屬於編輯這張單。
   - **擁有者**：明細記錄的 `OwnerUserId` 一律等於表頭的擁有者，新增時設定。表頭的擁有者不會變，所以資料範圍判斷不用另外處理。
   - **通用 API 看不到明細模組**：
     - `GET /menu` 不列出。
     - 以模組鍵為參數的端點回 404 `模組不存在或未啟用`：`modules/{key}/schema`、`records/paged`、`records/light`、`POST records`、`by-host`。
     - 以記錄 id 為參數的端點，記錄是明細列時回 404 `記錄不存在`：`GET/PUT/DELETE records/{id}`、`related`。
     - 設計器端點照常可用。
   - **權限設定**：`PUT /modules/{key}/permissions` 對明細模組只接受 `fields` 與 `options`。`groups` 或 `users` 非空時回 400，鍵 `groups`，訊息 `明細模組的權限沿用表頭模組`。
   - **啟用狀態**：明細模組也要啟用，才會出現在表頭的 schema 與存檔裡。啟用條件照舊，需要標題欄位。
   - **反向關聯分頁**：不列出明細欄位，明細改由表格呈現。
3. **表頭存檔的請求與回應**：
   - **請求**：`CreateRecordRequest`、`UpdateRecordRequest` 加 `Details: Dictionary<string, List<DetailRowInput>>?`，鍵是明細模組的 `Module.Key`。
     - 型別：`record DetailRowInput(long? Id, Dictionary<string, JsonElement?>? Values, string? RowVersion, bool Delete)`，JSON 名稱 `id`、`values`、`rowVersion`、`_delete`。
   - **語意**：
     - 沒給 `id` 的列是新增。
     - 有 `id` 而 `_delete` 為 false 的列是修改，`values` 只帶改動的欄位，沒帶的欄位保留舊值。
     - 有 `id` 而 `_delete` 為 true 的列是刪除，也就是軟刪除。
     - **請求沒列出的既有明細列一律不動**。前端只送新增、修改、刪除的列。
   - **列的版本與數量**：
     - 修改與刪除都要帶該列的 `rowVersion`。不符時整張回 409 `資料已被其他人修改，請重新載入`。
     - 每個明細模組，一張表頭最多 200 列未刪除的明細，以存檔後的結果計算。超過回 400，鍵 `details.{childKey}`，訊息 `明細最多 200 列`。
   - **表頭的版本**：PUT 一律檢查並換掉表頭的 `RowVersion`，即使只改了明細。表頭的 `Values` 可以是空物件。
   - **其他檢查**：
     - 明細列的表頭欄位值由伺服器填入表頭的 id，請求帶了也忽略。
     - `details` 的鍵不是這個表頭啟用中的明細模組時，回 400，鍵 `details.{childKey}`，訊息 `明細模組不存在或未啟用`。
     - `id` 不屬於這張表頭、不存在或已刪除時，回 400，鍵 `details.{childKey}[{i}]._record`，訊息 `明細不存在`。
   - **錯誤鍵**：明細列的欄位錯誤是 `details.{childKey}[{i}].{fieldKey}`，列層級錯誤是 `details.{childKey}[{i}]._record`。`i` 是該列在請求陣列裡的位置，從 0 起算。
   - **全單唯讀**：
     - 表頭被 `*` 唯讀規則鎖住時，整個請求回 `_record`：`此記錄目前不可修改`，包括只改明細的請求。
     - 明細列自己的 `*` 唯讀規則只鎖那一列，修改或刪除那一列時回該列的 `_record`。
   - **回應**：`DynamicRecordResponse` 加 `Details: IReadOnlyDictionary<string, IReadOnlyList<DynamicRecordResponse>>?`。
     - 有啟用中明細模組的表頭才帶這個屬性；依第 5 階段 R7，null 會省略。
     - 每個明細模組的列依 `Id` 遞增排序，每列照明細模組的權限過濾欄位，也帶 `references`。
     - 單筆讀取（`GET /records/{id}`）、新增與修改的回應都帶。分頁列表不帶。
4. **表頭存檔的處理順序**（在同一個交易內）：
   1. 權限、資料範圍、表頭 `RowVersion` 檢查，照第 5 階段。
   2. 檢查表頭是否被全單唯讀鎖住，鎖住就整個請求回 `_record`。
   3. 逐列處理明細：
      - 載入既有列、檢查 `rowVersion`。
      - 跑 `ReferenceValidator`、`RecordValueProcessor.Process`，用明細模組與 `AuthorizeDetailAsync` 的權限。這一步包含明細的公式與規則。
      - 錯誤加上 `details.{childKey}[{i}].` 前綴後收集起來。
   4. **算表頭的明細彙總**：對來源是明細欄位的彙總欄位，用「資料庫裡沒被這次請求修改或刪除的列」加上「這次新增與修改後的列」，呼叫 `RollupCalculator.Compute`。新增表頭時，資料庫裡沒有既有列。
   5. 處理表頭：`RecordValueProcessor.Process` 多一個參數 `rollupValues`，在公式計算之前寫進 values。表頭的公式與規則因此看得到新的彙總值。
   6. 任何一處有錯，就回 `Invalid`，帶上全部錯誤。
   7. **寫入表頭**：新增時取自動編號，`CommitAsync` 取得 Id。
   8. **寫入明細**：
      - 新增列：設定表頭欄位值、擁有者、自動編號（`Scope = parent` 用表頭內的序號，見決策 6）、標題、索引。
      - 修改列：寫回值、換 `RowVersion`、蓋修改稽核欄位、重建索引。
      - 刪除列：軟刪除。明細模組不能被參照，所以刪除列不需要第 5 階段的刪除規劃。
   9. `CommitAsync`。
   10. 呼叫 `RollupRecalc.AfterWriteAsync`，處理明細列或表頭身上的一般參照所造成的其他父記錄重算（決策 8）。
5. **刪除表頭**：
   - 明細欄位是 `cascade`，第 5 階段的刪除規劃本來就會連帶軟刪除明細，這裡不改。
   - 明細列身上的一般參照所造成的其他父記錄重算，走決策 8。
6. **項次在表頭內編號（`FieldConfig.Scope`）**：
   - 值是 `parent` 或 null。只能用在明細模組的 `autonumber` 欄位。其他情況回 400，鍵 `Config.Scope`，訊息 `只有明細模組的自動編號可以在表頭內編號`。
   - `Scope = parent` 的格式不能含日期記號，否則回 400，鍵 `Config.Format`，訊息 `在表頭內編號的格式不能含日期`。
   - 序號存在 `NumberSequence`，`Period = "p" + 表頭 Id`。因此要把 `Period` 加長到 20，在 migration `Phase6Details` 裡做。
   - 刪掉的項次不會重用，新列接在最大號之後。spec 的「在單內重新編號」解讀為「每張單從 1 起算」，不解讀為「刪除後重排」。
7. **彙總欄位（`FieldType.Rollup`，JSON `rollup`）**：
   - **設定**：`FieldConfig.Rollup: RollupConfig?`，型別 `record RollupConfig(long SourceFieldId, RollupFunction Function, long? AggregateFieldId, JsonElement? Condition)`。
     - `RollupFunction { Sum, Count, Min, Max }`，JSON 名稱 `sum`、`count`、`min`、`max`。
   - **來源欄位**：任一模組裡、啟用中、`RefTarget = module:<本模組>` 的參照欄位，明細或一般參照都可以，也可以是本模組自己的參照欄位，例如「上層設備」。
   - **彙總對象**：
     - 來源模組的啟用中欄位，型別是 `integer`、`decimal`、`currency`、`percent`、`date`、`formula`。
     - 不能是 `rollup`，也不能是直接或間接引用了 `rollup` 的公式。
     - `count` 不能給彙總對象，其他函數一定要給。
     - `sum` 不能用在日期，也不能用在日期結果的公式。
   - **條件**：§8 的條件語法，依來源模組的欄位解析，引用規則與 `ConditionParser` 相同。條件裡不能用 `rollup` 欄位，也不能用引用了 `rollup` 的公式。
   - **錯誤**：鍵 `Config.Rollup`，訊息 `彙總設定不正確：{原因}`。原因逐字如下：
     - `來源欄位必須是指向這個模組的參照欄位`
     - `彙總欄位必須是來源模組的數字或日期欄位`
     - `筆數不需要彙總欄位`
     - `必須指定彙總欄位`
     - `加總不能用在日期欄位`
     - `不能彙總另一個彙總欄位或引用它的公式`
     - `條件不正確：{ConditionParser 的訊息}`
   - **結果型別**由伺服器推得，寫回 `Config.ResultType` 與 `Config.Scale`，前端送來的值會被覆蓋：
     - `count` → `number`，scale 0。
     - 其他函數：彙總對象是 `currency`，或結果是 `currency` 的公式 → `currency`；是 `date`，或結果是日期的公式 → `date`；其餘 → `number`。scale 沿用彙總對象，沒有設定時用 2。
   - **空集合**：`sum` 與 `count` 得 0，`min` 與 `max` 得 null。null 時從 values 移除。
   - **計算範圍**：來源記錄未刪除、來源欄位值等於父記錄 id、符合條件。不看讀者權限。
   - **其他屬性**：
     - 和公式一樣是系統計算欄位，沿用 `系統計算欄位不能設定必填、預設值、唯讀或長度`。
     - 可以篩選、排序。索引與運算子比照 `formula`：日期結果索引 `DateValue`，其他索引 `NumberValue`。`filter-operators.json` 加 `rollup` 與 `rollup:date`。
     - 公式可以引用彙總欄位，型別依 `ResultType`。
     - 規則條件可以用彙總欄位。前端與公式一樣跳過，由伺服器在存檔時判斷。
     - **權限**：`ResultType = currency` 的彙總欄位，沒有表頭 Pricing 的使用者看不到，比照 currency 公式。引用它的公式也跟著隱藏，沿用第 3 階段「公式引用隱藏欄位就隱藏」。彙總對象在來源模組對使用者隱藏，**不影響**彙總欄位的可見性（已知限制，§15 新增一條）。
   - **停用限制**：被彙總設定引用的欄位不能停用，包括來源欄位、彙總對象、條件裡的欄位。鍵 `Status`，訊息 `此欄位被彙總欄位引用，不能停用`。
   - **重新計算全部父記錄**：彙總欄位新增、修改、重新啟用時，`RollupRecalc.RecalculateModuleAsync` 重算該模組所有記錄，比照 `FormulaRecalc`。
8. **一般參照的重算（`RollupRecalc`）**：
   - **什麼情況要重算**：任何寫入改變了「某個彙總欄位的來源模組」的記錄時，受影響的父記錄要重算。
     - 父記錄是寫入前與寫入後的來源欄位值，兩者都算，所以改指向時新舊兩個父記錄都會重算。
     - 寫入的路徑：
       - `CreateRecordHandler`、`UpdateRecordHandler`、`DeleteRecordHandler`。
       - 第 5 階段刪除規劃的連帶刪除與清空。
       - 表頭存檔裡的明細列，除了已經在決策 4 第 4 步算過的那張表頭。
       - `FormulaRecalc`：重算後，對以該模組為來源的所有彙總欄位，重算全部父記錄。
   - **不重算的父記錄**：這次請求已經刪除的父記錄，以及這次請求已經在記憶體裡算過的表頭。
   - **怎麼算**：
     - 寫入後先 `CommitAsync`，讓查詢看得到新資料，再從資料庫讀出子記錄，用 `RollupCalculator.Compute` 算。
     - 之後跑父記錄的公式（`RecordValueProcessor.ApplyFormulas`），重建索引。
     - **只有值真的改變時**才換父記錄的 `RowVersion`；不蓋修改稽核欄位，因為這是系統計算。
     - 不重跑父記錄的規則，比照 `FormulaRecalc`。
   - **一層的保證**：彙總對象與條件都不能引用彙總結果（決策 7），所以重算父記錄不會再觸發上一層。`RollupRecalc` 不遞迴。
   - **併發**：子記錄存檔後，父記錄的 `RowVersion` 會變，正在編輯父記錄的人存檔會收到 409。這是使用者裁決接受的取捨。
9. **schema**：
   - `ModuleSchemaResponse` 加 `Details: List<SchemaDetail>?`。
     - `SchemaDetail(string ModuleKey, I18nText Name, long ParentFieldId, string ParentFieldKey, string? TitleFieldKey, List<SchemaField> Fields, List<SchemaRule> Rules)`。
     - 只列啟用中的明細模組，依明細模組 `Key` 排序。
     - `Fields`：明細模組啟用中、使用者看得到、表頭欄位以外的欄位，依區塊順序、欄位順序攤平。
     - `Rules`：用第 4 階段同一套過濾邏輯，引用看不到的欄位的規則不送出。
   - ETag 要涵蓋明細模組的版本與權限。`SchemaETag.For` 的版本參數改成字串：表頭 `Version` 後面接每個明細模組的 `Version`，以 `.` 串接。權限雜湊也併入明細模組的權限。
   - `ModuleResponse`、`ModuleDetailResponse` 加 `DetailOf: string?`，值是表頭模組鍵。設計器用它標示明細模組。
   - 新端點 `GET /modules/{key}/rollup-sources`：設計器挑來源欄位用，需要設計器權限。
     - 回傳 `[{ fieldId, fieldLabel, moduleKey, moduleName, relation }]`。
     - 內容是所有指向本模組、啟用中的參照欄位，含停用模組。
10. **Migration `Phase6Details`**：只把 `Dynamic_NumberSequence.Period` 從 nvarchar(6) 改為 nvarchar(20)。在 Task 1 產生。新的設定屬性都存在 `Config` JSON 裡，不需要改表。
11. **已知限制**：前端不計算公式與彙總。逐列編輯明細時，金額、合計、稅額都要存檔後才會更新。spec Demo 一「改明細數量後，合計跟著變」以「存檔後」驗收。

## Global Constraints

第 1–5 階段計畫的 Global Constraints 全部沿用，另加以下幾條：

- **Migration**：`dotnet ef migrations add Phase6Details --project poc/backend/src/DynamicModules.Persistence --startup-project poc/backend/src/DynamicModules.Persistence`。在 Task 1 產生，不手寫、不手改。內容只有 `Dynamic_NumberSequence.Period` 改成 nvarchar(20)。
- **列舉**：
  - `FieldType` 加 `Rollup`，JSON 名稱 `rollup`。
  - `ReferenceRelation { Lookup, Detail }`，JSON 名稱 `lookup`、`detail`。
  - `RollupFunction { Sum, Count, Min, Max }`，JSON 名稱 `sum`、`count`、`min`、`max`。
- **`FieldConfig` 新屬性**：
  - `Relation`：`ReferenceRelation?`。
  - `Scope`：`string?`，只接受 `parent`。
  - `Rollup`：`RollupConfig?`。
- **錯誤訊息**（逐字使用）：
  - `這個欄位不能設為明細：{原因}`（鍵 `Config.Relation`），原因列在決策 1。
  - `明細設定建立後不能更改`（鍵 `Config.Relation`）
  - `明細的表頭欄位不能停用`（鍵 `Status`）
  - `明細模組的權限沿用表頭模組`（鍵 `groups`）
  - `明細模組不存在或未啟用`（鍵 `details.{childKey}`）
  - `明細最多 200 列`（鍵 `details.{childKey}`）
  - `明細不存在`（鍵 `details.{childKey}[{i}]._record`）
  - `只有明細模組的自動編號可以在表頭內編號`（鍵 `Config.Scope`）
  - `在表頭內編號的格式不能含日期`（鍵 `Config.Format`）
  - `彙總設定不正確：{原因}`（鍵 `Config.Rollup`），原因列在決策 7。
  - `此欄位被彙總欄位引用，不能停用`（鍵 `Status`）
  - 沿用的訊息：`參照目標不正確`、`系統計算欄位不能設定必填、預設值、唯讀或長度`、`此記錄目前不可修改`、`資料已被其他人修改，請重新載入`、`模組不存在或未啟用`、`記錄不存在`。
- **共用檔**：`poc/shared/filter-operators.json` 加 `rollup`、`rollup:date`，內容分別與 `formula`、`formula:date` 相同。
- **前端文字**：
  - i18n 放 `designer.types.rollup`、`designer.reference.relation*`、`designer.autonumber.scopeParent`、`designer.rollup.*`、`designer.detailOf`、`designer.permissions.detailInherited`、`dynamic.details.*`。
  - 四個語系都要有，th、vi 用英文佔位（第 2 階段 R7）。

## Review Focus

1. **明細某一列驗證失敗**：三列裡第二列數量不是數字。
   - 預期：回 400，鍵是 `details.{childKey}[1].{fieldKey}`；表頭、第一列、第三列都沒寫入；自動編號的序號沒有前進，交易回滾。
   - 測試：Task 4 的 `Create_DetailRowInvalid_NothingSaved`、`Update_DetailRowInvalid_NothingSaved`。
2. **一般參照的子記錄改指向**：維修紀錄從申請 A 改成申請 B。
   - 預期：A 與 B 的「總工時」都正確重算。
   - 測試：Task 5 的 `Update_ChildRefChanged_BothParentsRecalculated`。
3. **第 5 階段的連帶刪除與清空碰到彙總**：
   - 預期：
     - 連帶刪掉的子記錄、被清空參照的子記錄，它們原本的父記錄都會重算。
     - 父記錄本身被刪時不重算，不會對已刪除的記錄寫入。
   - 測試：Task 5 的 `Delete_CascadeChild_ParentRollupRecalculated`、`Delete_SetNullChild_ParentRollupRecalculated`、`Delete_ParentWithChildren_NoRecalcOnDeletedParent`。
4. **子記錄存檔與父記錄的版本**：
   - 預期：
     - 改到彙總值時，父記錄的 `RowVersion` 換掉，拿舊版本存父記錄回 409。
     - 子記錄只改了和彙總無關的欄位時，父記錄的 `RowVersion` 不變，不會無故 409。
   - 測試：Task 5 的 `Create_Child_ParentRowVersionChanges`、`Update_ChildIrrelevantField_ParentRowVersionUnchanged`。
5. **沒有 Pricing 的使用者**：
   - 預期：
     - 表頭 schema 沒有金額結果的彙總，也沒有引用它的公式。
     - 明細 schema 沒有單價與金額欄位。
     - 回應裡的表頭與明細都沒有這些值。
     - 這個使用者送了單價，伺服器忽略不存。
   - 測試：Task 3 的 `Schema_NoPricing_HidesCurrencyRollupAndDetailColumns`，Task 4 的 `Update_NoPricing_DetailCurrencyIgnored`。

---

## 檔案結構

只列本階段新增或修改的檔案。後端路徑省略 `poc/backend/src/DynamicModules.` 前綴。

```
poc/shared/filter-operators.json                    + rollup、rollup:date

Domain/
  Entities/Dynamic/FieldType.cs                     + Rollup
  Entities/Dynamic/{ReferenceRelation,RollupFunction}.cs
  ValueObjects/FieldConfig.cs                       + Relation、Scope、Rollup；RollupConfig
  Data/Configurations/NumberSequenceConfiguration.cs   Period 20
  FieldTypes/Handlers/RollupHandler.cs
  FieldTypes/FieldTypeRegistry.cs
  Formulas/FormulaParser.cs                         FormulaFieldTypes + Rollup
  Rollups/RollupCalculator.cs                       純函式
  Modules/ModuleRelations.cs                        明細判斷、彙總依賴

Application/
  Common/Security/{IDynamicAccessGuard,DynamicAccessGuard}.cs   + AuthorizeDetailAsync
  Dynamic/FieldService/Data/FieldRules.cs           明細、Scope、彙總設定驗證
  Dynamic/FieldService/Commands/{AddField,UpdateField,SetFieldStatus}/**
  Dynamic/FieldService/FormulaRecalc.cs             之後呼叫 RollupRecalc
  Dynamic/FieldService/Queries/GetRollupSources/**
  Dynamic/Rollups/RollupRecalc.cs
  Dynamic/RecordService/RecordValueProcessor.cs     + rollupValues
  Dynamic/RecordService/DetailWriter.cs             表頭存檔的明細處理
  Dynamic/RecordService/Commands/{CreateRecord,UpdateRecord,DeleteRecord}/**
  Dynamic/RecordService/Queries/**                  明細模組不可見、Details
  Dynamic/RecordService/Data/RecordResponses.cs     + Details
  Dynamic/SchemaService/**                          Details、ETag、選單排除
  Dynamic/ModuleService/Data/ModuleResponses.cs     + DetailOf
  Dynamic/PermissionService/Commands/SaveModulePermissions/**
  Dynamic/RelationService/**                        排除明細欄位、明細列 404

Persistence/
  Repositories/Dynamic/{ModuleRepository,RecordRepository}.cs   + 查詢
  Migrations/<時間戳>_Phase6Details.cs

WebAPI/Controllers/v3/Dynamic/{RecordsController,FieldsController}.cs

poc/frontend/src/
  domain/dynamic/types.ts                           + rollup、relation、scope、SchemaDetail、details
  presentation/dynamic/field-type-registry.ts       + rollup
  presentation/dynamic/rule-state.ts                rollup 視為計算欄位
  services/api/dynamic-api.ts                       + details、rollupSources
  application/dynamic/queries/records-queries.ts    SaveRecordInput + details
  components/framework/InlineEditableDataTable.vue  （新）
  presentation/dynamic/details/{detail-rows.ts,DetailGrid.vue,DetailTables.vue}（新）
  presentation/dynamic/components/{DynamicForm,DynamicDetail}.vue
  presentation/dynamic/designer/FieldEditorDialog.vue
  presentation/dynamic/permissions/PermissionsTab.vue（實際路徑以現有檔為準）
  pages/dynamic/FormPage.vue
  pages/system/dynamic-modules/{ModuleListPage,ModuleEditorPage}.vue
  i18n/{zh-TW,en-US,th,vi}.json
```

---

## 任務

### Task 1：型別、設定與 migration

**Files:**
- Create：`Domain/Entities/Dynamic/{ReferenceRelation,RollupFunction}.cs`、`Domain/FieldTypes/Handlers/RollupHandler.cs`
- Modify：`FieldType.cs`、`FieldConfig.cs`、`FieldTypeRegistry.cs`、`FieldTypeCapabilities.cs`（不需改時免改）、`FormulaParser.cs`（`FormulaFieldTypes`）、`NumberSequenceConfiguration.cs`、`poc/shared/filter-operators.json`
- Test：`DomainTests/FieldTypes/{FieldTypeRegistryTests,IndexAndOperatorTests}.cs`、`DomainTests/ValueObjects/FieldConfigJsonTests.cs`（沒有就新建）、`DomainTests/Formulas/FormulaParserTests.cs`、`ApplicationTests/Persistence/SchemaTests.cs`

**Interfaces:**
- Produces：
  - `FieldType.Rollup`
  - `ReferenceRelation`
  - `RollupFunction`
  - `record RollupConfig(long SourceFieldId, RollupFunction Function, long? AggregateFieldId, JsonElement? Condition)`
  - `FieldConfig.Relation`、`FieldConfig.Scope`、`FieldConfig.Rollup`
  - `RollupHandler`：`Normalize` 一律回 `Ok(null)`；`OperatorsFor`、`ToIndex` 比照 `FormulaHandler`，依 `ResultType`。

- [ ] **Step 1：寫失敗測試**
  - `FieldConfig_Rollup_RoundTripsJson`：`{"rollup":{"sourceFieldId":3,"function":"sum","aggregateFieldId":5,"condition":{...}},"relation":"detail","scope":"parent"}` 序列化回來相同。
  - `Registry_HasRollupHandler`。
  - `Rollup_Operators_FollowResultType`：`date` 用日期運算子，其他用數字運算子。
  - `Rollup_ToIndex_DateAndNumber`。
  - 共用檔測試會讀 `filter-operators.json` 的 `rollup` 與 `rollup:date`。在既有的 `IndexAndOperatorTests` 加對應案例。
  - `FormulaParser_RollupField_TypedByResultType`：`ResultType` 為 `currency` 時是 Number，為 `date` 時是 Date。
  - `NumberSequence_Period_Allows20Chars`（`SchemaTests`）：寫入 `Period = "p1234567890"` 可以存。
- [ ] **Step 2：跑測試確認失敗**：`dotnet test poc/backend --filter "FieldConfig|Registry|Rollup|FormulaParser|SchemaTests"`。
- [ ] **Step 3：實作**：照 Interfaces 加型別與屬性，改 `Period` 上限，產生 migration `Phase6Details`。先檢查產生的檔案只改了 `Period`。
- [ ] **Step 4：跑測試確認通過**：同上，再跑全部後端測試。
- [ ] **Step 5：Commit**：`feat: 彙總欄位型別、明細與表頭內編號設定`

### Task 2：彙總計算與設計器驗證

**Files:**
- Create：`Domain/Rollups/RollupCalculator.cs`、`Domain/Modules/ModuleRelations.cs`、`Application/Dynamic/FieldService/Queries/GetRollupSources/**`
- Modify：`FieldRules.cs`、`AddFieldHandler`、`UpdateFieldHandler`、`SetFieldStatusHandler`、`ModuleResponses.cs`、`ModuleMapper`、`IModuleRepository`、`ModuleRepository`、`FieldsController`（或放 `ModulesController`，依現有慣例）
- Test：`DomainTests/Rollups/RollupCalculatorTests.cs`、`ApplicationTests/Dynamic/DetailFieldRulesTests.cs`、`ApplicationTests/Dynamic/RollupFieldRulesTests.cs`、`WebAPITests/Dynamic/FieldsApiTests.cs`

**Interfaces:**
- Consumes：Task 1 的型別。
- Produces：
  - `RollupCalculator.Compute(Field rollup, Module source, IEnumerable<IReadOnlyDictionary<string, JsonElement?>> children, DateOnly today) → JsonElement?`：
    - `children` 是已經篩過來源欄位、未刪除的子記錄值。
    - 條件在函式裡用 `ConditionEvaluator` 判斷。
  - `ModuleRelations`（static）：
    - `DetailFieldOf(Module m) → Field?`：啟用中、`Relation = Detail` 的欄位。
    - `IsDetail(Module m) → bool`。
    - `DependsOnRollup(Module m, Field f) → bool`：欄位本身是 rollup，或是直接或間接引用 rollup 的公式。
    - `RollupRefsOf(Field rollup) → IReadOnlySet<long>`：來源欄位、彙總對象、條件裡引用的欄位。
  - `IModuleRepository`：
    - `ListDetailModulesAsync(string parentKey, ct) → IReadOnlyList<(Module Child, Field ParentField)>`
    - `ListRollupsBySourceModuleAsync(long sourceModuleId, ct) → IReadOnlyList<(Module Parent, Field Rollup, Field Source)>`
    - `GetParentOfDetailAsync(Module child, ct) → Module?`
  - `ModuleResponse.DetailOf`、`ModuleDetailResponse.DetailOf`。
  - `GET /modules/{key}/rollup-sources` → `[{ fieldId, fieldLabel, moduleKey, moduleName, relation }]`。

- [ ] **Step 1：寫失敗測試**
  - `RollupCalculatorTests`：
    - `Sum_Decimal`
    - `Count_IgnoresAggregate`
    - `Min_Max_Dates`：結果是 `yyyy-MM-dd` 字串。
    - `Sum_Empty_Zero`
    - `Max_Empty_Null`
    - `Condition_FiltersChildren`：例如只算「狀態 = 已完成」的。
    - `MissingValues_Skipped`：子記錄沒有這個值時，不計入 sum、min、max，但 count 照算。
    - `Sum_Currency_RoundsToScale`
  - `DetailFieldRulesTests`，每種「不能設為明細」的原因各一個：
    - `Detail_EntityTarget_Rejected`：只能參照動態模組。
    - `Detail_TargetIsExtension_Rejected`
    - `Detail_TargetIsDetail_Rejected`
    - `Detail_TargetIsSelf_Rejected`
    - `Detail_SecondDetailField_Rejected`：這個模組已經有表頭。
    - `Detail_ModuleHasRecords_Rejected`
    - `Detail_ModuleHasOwnDetails_Rejected`
    - `Detail_ModuleIsReferenced_Rejected`
    - `Detail_OnExtensionModule_Rejected`
    - `Detail_ForcesRequiredAndCascade`
    - `Detail_RelationLocked_OnUpdate`
    - `Detail_CannotDeactivate`
    - `Reference_ToDetailModule_Rejected`
    - `Scope_Parent_OnlyDetailAutonumber`
    - `Scope_Parent_FormatWithDate_Rejected`
  - `RollupFieldRulesTests`：
    - 每個 `彙總設定不正確：{原因}` 各一個。
    - `Rollup_ResultTypeDerived`：sum currency → `currency`；count → `number` 且 scale 0；max date → `date`；sum 結果為 currency 的公式 → `currency`。
    - `Rollup_ComputedFlags_Rejected`：設必填回 `系統計算欄位不能設定必填、預設值、唯讀或長度`。
    - `Rollup_OfFormulaReferencingRollup_Rejected`
    - `Rollup_ConditionOnRollup_Rejected`
    - `Deactivate_FieldUsedByRollup_Rejected`：來源欄位、彙總對象、條件欄位各測一次。
    - `Formula_CanReferenceRollup`
  - `FieldsApiTests`：
    - `RollupSources_ListsIncomingReferenceFields`
    - `ModuleDetail_DetailOf_Set`
- [ ] **Step 2：跑測試確認失敗**：`dotnet test poc/backend --filter "RollupCalculator|DetailFieldRules|RollupFieldRules|FieldsApi"`。
- [ ] **Step 3：實作**
  - 驗證放在 `FieldRules`，和 `CheckReference`、`CheckFormula` 同樣做成 handler 層的檢查。
  - 依賴資料庫的檢查（有無記錄、有無被參照）放在 handler，呼叫 repository。
  - 彙總欄位新增、修改、重新啟用之後的全量重算留給 Task 5，這裡先不接。
- [ ] **Step 4：跑測試確認通過**：同上，再跑全部後端測試。
- [ ] **Step 5：Commit**：`feat: 彙總計算與明細、彙總的設計器驗證`

### Task 3：明細模組的存取與 schema

**Files:**
- Modify：`IDynamicAccessGuard.cs`、`DynamicAccessGuard.cs`、`PermissionResolver`（如需）、`GetMenuHandler`、`GetModuleSchemaHandler`、`SchemaResponses.cs`、`SchemaETag`、`RecordService/Queries/**`（paged、light、by-host、GetRecord）、`CreateRecordHandler`、`UpdateRecordHandler`、`DeleteRecordHandler`（明細模組與明細列直接存取 → 404）、`RelationService/**`、`SaveModulePermissionsHandler`
- Test：`ApplicationTests/Dynamic/DetailAccessTests.cs`、`WebAPITests/Dynamic/SchemaApiTests.cs`

**Interfaces:**
- Consumes：Task 2 的 `ModuleRelations`、`ListDetailModulesAsync`、`GetParentOfDetailAsync`。
- Produces：
  - `IDynamicAccessGuard.AuthorizeDetailAsync(Module parent, Module child, PermissionAction action, CancellationToken ct) → Task<AccessResult>`
  - `ModuleSchemaResponse.Details`
  - `record SchemaDetail(string ModuleKey, I18nText Name, long ParentFieldId, string ParentFieldKey, string? TitleFieldKey, List<SchemaField> Fields, List<SchemaRule> Rules)`

- [ ] **Step 1：寫失敗測試**（`DetailAccessTests`、`SchemaApiTests`）
  - `Menu_ExcludesDetailModule`
  - `Schema_DetailModule_NotFound`
  - `Paged_DetailModule_NotFound`
  - `Light_DetailModule_NotFound`
  - `Create_DirectOnDetailModule_NotFound`
  - `GetRecord_DetailRow_NotFound`
  - `Update_DetailRow_NotFound`
  - `Delete_DetailRow_NotFound`
  - `Related_ExcludesDetailField`
  - `Schema_Parent_IncludesDetails`：`details[0]` 帶 `moduleKey`、`parentFieldKey`，`fields` 不含表頭欄位，`rules` 是明細模組的規則。
  - `Schema_Parent_InactiveDetail_Excluded`
  - `Schema_ETag_ChangesWhenDetailModuleChanges`：改明細模組的欄位，表頭 schema 的 ETag 跟著變。
  - `Schema_NoPricing_HidesCurrencyRollupAndDetailColumns`（Review Focus 5）：sales 群組沒有 Pricing，表頭的金額彙總、引用它的稅額公式、明細的單價與金額都不出現。
  - `Schema_DetailFieldHidden_ByDetailModuleFieldPermission`：明細模組的欄位例外有效。
  - `AuthorizeDetail_UsesParentPrivileges`：明細模組自己的 ProgramCode 沒有授權，表頭有，結果照表頭。
  - `SavePermissions_DetailModuleWithGroups_Rejected`：回 400，鍵 `groups`，訊息 `明細模組的權限沿用表頭模組`。
  - `SavePermissions_DetailModuleFieldsOnly_Ok`
- [ ] **Step 2：跑測試確認失敗**：`dotnet test poc/backend --filter "DetailAccess|SchemaApi"`。
- [ ] **Step 3：實作**
  - 通用端點在載入模組或記錄之後、判斷權限之前，先用 `ModuleRelations.IsDetail` 判斷，是明細就回 404。
  - schema 的明細欄位與規則，重用現有建 schema 欄位與過濾規則的程式碼。抽一個內部方法給表頭與明細共用，不要複製一份。
- [ ] **Step 4：跑測試確認通過**：同上，再跑全部後端測試。
- [ ] **Step 5：Commit**：`feat: 明細模組沿用表頭權限、schema 帶出明細`

### Task 4：表頭與明細一起存檔

**Files:**
- Create：`Application/Dynamic/RecordService/DetailWriter.cs`
- Modify：`CreateRecordRequest`、`UpdateRecordRequest`（+ `DetailRowInput`）、`CreateRecordHandler`、`UpdateRecordHandler`、`GetRecordHandler`、`RecordValueProcessor.cs`（`rollupValues`）、`RecordResponses.cs`（`Details`）、`INumberSequenceRepository` 的呼叫端（表頭內編號）、`IRecordRepository`／`RecordRepository`（依表頭列明細：`ListByRefAsync` 夠用就不加）
- Test：`ApplicationTests/Dynamic/DetailSaveTests.cs`、`WebAPITests/Dynamic/RecordsApiTests.cs`

**Interfaces:**
- Consumes：
  - Task 2 的 `RollupCalculator`、`ListDetailModulesAsync`。
  - Task 3 的 `AuthorizeDetailAsync`。
- Produces：
  - `record DetailRowInput(long? Id, Dictionary<string, JsonElement?>? Values, string? RowVersion, bool Delete)`，`Delete` 的 JSON 名稱是 `_delete`。
  - `CreateRecordRequest(..., Dictionary<string, List<DetailRowInput>>? Details = null)`、`UpdateRecordRequest(..., Dictionary<string, List<DetailRowInput>>? Details = null)`。
  - `RecordValueProcessor.Process(..., IReadOnlyDictionary<string, JsonElement?>? rollupValues = null)`：在 `ApplyFormulas` 之前寫入。
  - `DetailWriter`：
    - `PrepareAsync(Module parent, Record? parentRecord, EffectivePermissions parentPerms, PermissionAction action, IReadOnlyDictionary<string, List<DetailRowInput>>? details, ct) → Task<DetailPlan>`
    - `DetailPlan`：處理後的列、錯誤、明細彙總值 `IReadOnlyDictionary<string, JsonElement?>`。
    - `ApplyAsync(DetailPlan plan, Record parentRecord, ct)`：寫入明細列。
  - `DynamicRecordResponse.Details`。
  - 決策 8 的 `RollupRecalc.AfterWriteAsync` 由 Task 5 接上。這裡先留一個呼叫點，Task 5 補實作。

- [ ] **Step 1：寫失敗測試**（`DetailSaveTests`，訂單、訂單明細的最小版本：明細有數量 `decimal`、單價 `currency`、金額公式 `{數量} * {單價}`、項次 `autonumber` `{0}` 且 `Scope = parent`；表頭有 `sum` 金額的合計、`count` 筆數、稅額公式 `ROUND({合計} * 5 / 100, 0)`）
  - 新增：
    - `Create_WithDetails_SavesAllInOneRequest`：3 列；回應的 `details` 有 3 列，項次是 `1`、`2`、`3`，每列金額正確；表頭的合計、筆數、稅額正確。
    - `Create_DetailRowInvalid_NothingSaved`（Review Focus 1）：第 2 列數量是 `"abc"`；回 400，鍵 `details.{child}[1].{qtyKey}`；資料庫沒有表頭、沒有明細；表頭的自動編號序號沒前進。
  - 修改明細列：
    - `Update_ChangeRowQty_RollupUpdated`：只改第 2 列數量，表頭合計跟著變，表頭 `RowVersion` 換掉。
    - `Update_DeleteRow_RollupUpdated_NumberNotReused`：刪第 2 列後新增一列，新列項次是 `4`，筆數是 3。
    - `Update_AddRow_OtherRowsUntouched`：沒列出的列 `RowVersion` 不變。
    - `Update_DetailRowInvalid_NothingSaved`：表頭值與其他列都不變。
  - 版本與歸屬：
    - `Update_StaleRowVersion_Conflict`：任一列帶舊 `rowVersion`，回 409 `資料已被其他人修改，請重新載入`。
    - `Update_RowOfOtherParent_Rejected`：回 400，鍵 `details.{child}[0]._record`，訊息 `明細不存在`。
    - `Update_UnknownDetailModule_Rejected`：回 400，鍵 `details.foo`，訊息 `明細模組不存在或未啟用`。
    - `Update_Over200Rows_Rejected`
    - `Create_ParentFieldInRow_Ignored`：列裡帶了別張表頭的 id，存檔後指向這張表頭。
  - 唯讀規則：
    - `Update_HeaderReadOnlyAll_DetailChangeRejected`：表頭狀態是已結案，只改明細，回 `_record`：`此記錄目前不可修改`。
    - `Update_RowReadOnlyAll_RowRejected`：明細模組的 `*` 唯讀規則鎖住一列；改那列回 `details.{child}[0]._record`。
  - 權限：
    - `Update_NoPricing_DetailCurrencyIgnored`（Review Focus 5）：沒有 Pricing 的使用者送了單價，伺服器忽略不存，回應沒有單價、金額與合計。
    - `Create_DetailRowOwner_IsParentOwner`
    - `Update_NoEdit_Forbidden`：只有表頭 `Read` 的使用者改明細，回 403。
  - 讀取與刪除：
    - `Get_Parent_IncludesDetailsOrderedById`
    - `Delete_Parent_CascadesDetails`（回歸第 5 階段的連帶刪除）。
  - 公式引用彙總：
    - `Validation_OnRollup_SeesNewValue`：表頭的驗證規則「合計 > 0」在只有一列、金額 0 時擋下，表示規則看到的是這次算出的彙總。
  - `RecordsApiTests`：
    - `PostRecord_DetailsJson_Shape`：`_delete` 的 JSON 名稱正確。
    - `ErrorKeys_DetailFormat`
- [ ] **Step 2：跑測試確認失敗**：`dotnet test poc/backend --filter "DetailSave|RecordsApi"`。
- [ ] **Step 3：實作**：照決策 4 的順序做。
  - 表頭內編號的 `Period` 是 `$"p{parentRecord.Id}"`。
  - 新增表頭時要先 `CommitAsync` 取得表頭 Id，再寫明細。錯誤一定要在寫入之前就收集完，交易回滾由 `TransactionBehavior` 處理。
- [ ] **Step 4：跑測試確認通過**：同上，再跑全部後端測試。
- [ ] **Step 5：Commit**：`feat: 表頭與明細在同一個交易存檔`

### Task 5：一般參照的彙總重算

**Files:**
- Create：`Application/Dynamic/Rollups/RollupRecalc.cs`
- Modify：`CreateRecordHandler`、`UpdateRecordHandler`、`DeleteRecordHandler`、`DetailWriter`（呼叫點）、`FormulaRecalc.cs`、`AddFieldHandler`、`UpdateFieldHandler`、`SetFieldStatusHandler`（彙總欄位的全量重算）
- Test：`ApplicationTests/Dynamic/RollupRecalcTests.cs`

**Interfaces:**
- Consumes：
  - Task 2 的 `RollupCalculator`、`ListRollupsBySourceModuleAsync`。
  - Task 4 的呼叫點。
- Produces：
  - `RollupRecalc.CollectParents(Module source, IReadOnlyDictionary<string, JsonElement?>? before, IReadOnlyDictionary<string, JsonElement?>? after, IReadOnlyList<(Module Parent, Field Rollup, Field Source)> rollups) → IReadOnlySet<(long ParentModuleId, long ParentRecordId)>`
  - `RollupRecalc.RecalculateAsync(IEnumerable<(long ParentModuleId, long ParentRecordId)> parents, IReadOnlySet<long> skipRecordIds, DateOnly today, ct)`
  - `RollupRecalc.RecalculateModuleAsync(Module parent, DateOnly today, ct)`：重算一個模組的全部記錄。
  - `RollupRecalc.RecalculateForSourceModuleAsync(Module source, DateOnly today, ct)`：供 `FormulaRecalc` 呼叫。
  - `RollupRecalc.AfterWriteAsync(Module source, IEnumerable<(IReadOnlyDictionary<string, JsonElement?>? Before, IReadOnlyDictionary<string, JsonElement?>? After)> changes, IReadOnlySet<long> skipRecordIds, ct)`：各 handler 統一呼叫這個。內部先 `CommitAsync`，再收集父記錄、重算。

- [ ] **Step 1：寫失敗測試**（`RollupRecalcTests`，以維修申請與維修紀錄為例：申請有「紀錄筆數」`count` 與「總工時」`sum(工時)`）
  - `Create_Child_ParentRollupUpdated`
  - `Create_Child_ParentRowVersionChanges`（Review Focus 4）：之後拿舊 `RowVersion` 存申請，回 409。
  - `Update_ChildHours_ParentSumUpdated`
  - `Update_ChildIrrelevantField_ParentRowVersionUnchanged`（Review Focus 4）
  - `Update_ChildRefChanged_BothParentsRecalculated`（Review Focus 2）
  - `Delete_Child_ParentRollupUpdated`
  - `Delete_CascadeChild_ParentRollupRecalculated`（Review Focus 3）：子記錄因為它指向的另一筆記錄被刪而連帶刪除，它的彙總父記錄要重算。
  - `Delete_SetNullChild_ParentRollupRecalculated`（Review Focus 3）
  - `Delete_ParentWithChildren_NoRecalcOnDeletedParent`（Review Focus 3）：父記錄本身被刪，它的 `ValuesJson` 不再被改寫。
  - `ParentFormula_ReRunAfterRecalc`：申請的公式「平均工時 = 總工時 / 紀錄筆數」跟著更新。
  - `Rollup_IgnoresReaderScope`：floor01 只看得到自己的紀錄，但申請的筆數算全部。
  - `ChildFormulaChanged_ParentsRecalculated`：改維修紀錄的公式（彙總對象是公式），全部申請重算。
  - `AddRollupField_BackfillsExistingParents`
  - `ReactivateRollupField_Backfills`
  - `DetailRowLookup_RecalculatesOtherParent`：訂單明細有一般參照指向「產品」，產品有 `sum(數量)` 的彙總；存訂單後，產品的彙總更新。
  - `SelfReference_CountChildren`：「上層設備」`count`。
- [ ] **Step 2：跑測試確認失敗**：`dotnet test poc/backend --filter RollupRecalc`。
- [ ] **Step 3：實作**
  - 刪除 handler 的 `changes`：根記錄、`ToDelete` 用 `(Before, null)`；`ToClear` 用 `(Before, 清空後)`。
  - `skipRecordIds` 放被刪除的記錄 id，以及表頭存檔時已經在記憶體算過的表頭 id。
  - 重算時先比較新舊值，有變才寫。
- [ ] **Step 4：跑測試確認通過**：同上，再跑全部後端測試。
- [ ] **Step 5：Commit**：`feat: 子記錄變動時重算彙總欄位`

### Task 6：前端型別、API 與彙總顯示

**Files:**
- Modify：`domain/dynamic/types.ts`、`presentation/dynamic/field-type-registry.ts`（+ spec）、`presentation/dynamic/rule-state.ts`（+ spec）、`services/api/dynamic-api.ts`、`services/api/http.ts`（如需）、`application/dynamic/queries/{records-queries,modules-queries}.ts`、i18n
- Test：`field-type-registry.vitest.spec.ts`、`rule-state.vitest.spec.ts`（沒有就新建）、`dynamic-api.vitest.spec.ts`（沒有就在既有 api spec 加）

**Interfaces:**
- Produces：
  - 型別：
    - `FieldType` 加 `'rollup'`。
    - `FieldConfig` 加 `relation?: 'lookup' | 'detail'`、`scope?: 'parent'`、`rollup?: { sourceFieldId: number; function: 'sum' | 'count' | 'min' | 'max'; aggregateFieldId?: number; condition?: ConditionNode }`。
    - `SchemaDetail { moduleKey; name; parentFieldId; parentFieldKey; titleFieldKey: string | null; fields: SchemaField[]; rules: SchemaRule[] }`。
    - `ModuleSchema.details?: SchemaDetail[]`。
    - `DynamicRecord.details?: Record<string, DynamicRecord[]>`。
    - `ModuleSummary`、`ModuleDetail` 加 `detailOf?: string`。
    - `DetailRowPayload = { id?: number; values?: Record<string, unknown>; rowVersion?: string; _delete?: true }`。
    - `RollupSource { fieldId; fieldLabel; moduleKey; moduleName; relation }`。
  - API：
    - `createRecord(key, values, hostKey?, details?: Record<string, DetailRowPayload[]>)`。
    - `updateRecord(id, values, rowVersion, details?)`。
    - `rollupSources(moduleKey)`。
  - `SaveRecordInput` 加 `details?`。
  - `useDesignerMutation` 成功後改成清掉全部 `['schema']`，因為改明細模組會影響表頭的 schema。

- [ ] **Step 1：寫失敗測試**
  - `rollup is read-only and formats by resultType`：currency 用千分位，date 原樣顯示。
  - `rollup operators follow resultType`：讀共用檔的 `rollup:date`。
  - `rule-state skips rules referencing rollup`
  - `isWritable false for rollup`
  - `createRecord sends details`、`updateRecord sends details`
  - `designer mutation invalidates all schemas`
- [ ] **Step 2：跑測試確認失敗**：`bunx vitest run field-type-registry rule-state dynamic-api modules-queries`。
- [ ] **Step 3：實作**：照 Interfaces 做。`rollup` 在 registry 比照 `formula` 用唯讀文字。
- [ ] **Step 4：跑測試確認通過**：`bunx vitest run && bunx vue-tsc --noEmit`。
- [ ] **Step 5：Commit**：`feat(frontend): 彙總與明細的型別、API`

### Task 7：設計器：明細、表頭內編號、彙總設定

**Files:**
- Modify：`presentation/dynamic/designer/FieldEditorDialog.vue`（+ spec）、`pages/system/dynamic-modules/{ModuleListPage,ModuleEditorPage}.vue`（+ spec，若有）、權限分頁元件（+ spec）、i18n
- Test：`FieldEditorDialog.vitest.spec.ts`、`ModuleListPage.vitest.spec.ts`、權限分頁的 spec

**Interfaces:**
- Consumes：Task 6 的型別與 `rollupSources`、既有的 `ConditionEditor`（`presentation/dynamic/list/ConditionEditor.vue`，props `fields`、`modelValue`）、`toSchemaFields`。

- [ ] **Step 1：寫失敗測試**
  - `FieldEditorDialog`：
    - 明細：
      - `reference module target shows relation option in create mode`
      - `detail relation hides onDelete and locks required`
      - `relation readonly in edit mode`
    - 表頭內編號：`autonumber in detail module shows scope parent`；非明細模組不顯示。
    - 彙總：
      - `rollup lists sources from rollupSources`
      - `rollup count hides aggregate field`
      - `rollup aggregate options are numeric or date fields of source module`：彙總欄位、引用彙總欄位的公式不出現。
      - `rollup condition uses ConditionEditor with source fields`
      - `rollup payload shape`：`{ rollup: { sourceFieldId, function, aggregateFieldId, condition } }`，沒有 `required`、`defaultValue`。
    - 伺服器錯誤：`server Config.Rollup / Config.Relation / Config.Scope errors shown inline`。
    - 公式：`formula insert list includes rollup fields`
  - `ModuleListPage`：`detail module shows detailOf marker`，顯示「明細：{表頭名稱}」。
  - 權限分頁：`detail module hides privilege matrix and scope, shows inherited note`；欄位例外照常可編。
- [ ] **Step 2：跑測試確認失敗**：`bunx vitest run FieldEditorDialog ModuleListPage Permissions`。
- [ ] **Step 3：實作**
  - 彙總對象與條件要用來源模組的欄位：以 `useModuleDetail(來源模組鍵)` 取得，再經 `toSchemaFields` 轉換。
  - `SHOWN` 加 `Config.Rollup`、`Config.Relation`、`Config.Scope`。
- [ ] **Step 4：跑測試確認通過**：`bunx vitest run && bunx vue-tsc --noEmit`。
- [ ] **Step 5：Commit**：`feat(frontend): 設計器的明細、表頭內編號與彙總設定`

### Task 8：表單裡逐列編輯明細

**Files:**
- Create：`components/framework/InlineEditableDataTable.vue`（+ spec）、`presentation/dynamic/details/detail-rows.ts`（+ spec）、`presentation/dynamic/details/DetailGrid.vue`（+ spec）
- Modify：`presentation/dynamic/components/DynamicForm.vue`（+ spec）、`pages/dynamic/FormPage.vue`（+ spec）、i18n
- Test：上面各 spec

**Interfaces:**
- Consumes：
  - Task 6 的 `SchemaDetail`、`DetailRowPayload`、`details` API。
  - 既有的 `buildZodSchema`、`ruleStateOf`、`fieldTypeRegistry`。
- Produces：
  - `InlineEditableDataTable`：
    - props：`columns: { name: string; label: string }[]`、`rows: unknown[]`、`rowKey: (row) => string`、`readonly?: boolean`。
    - slot：`cell-{name}`，參數 `{ row, index }`；`row-actions`。
    - emits：`add`。
    - POC 的薄包裝，上線時換成 framework 的同名元件。
  - `detail-rows.ts`：
    - `type DetailRowState = { uid: string; id?: number; rowVersion?: string; values: Record<string, unknown>; original?: Record<string, unknown>; deleted: boolean }`
    - `toRowStates(rows: DynamicRecord[] | undefined): DetailRowState[]`
    - `buildDetailPayload(rows: DetailRowState[]): { items: DetailRowPayload[]; uidAt: string[] }`
      - 新增列送 `{ values }`。
      - 改過的既有列送 `{ id, rowVersion, values: 只含改動欄位 }`。
      - 刪除的既有列送 `{ id, rowVersion, _delete: true }`。
      - 沒改的列不送；新增後又刪掉的列不送。
      - `uidAt[i]` 是 payload 第 i 項對應的列，用來把伺服器錯誤對回正確的列。
    - `rowErrors(errors: Record<string, string[]> | undefined, childKey: string, uidAt: string[]): Map<string, Record<string, string>>`：把 `details.{childKey}[i].{fieldKey}` 換成「列 uid → 欄位鍵 → 訊息」；列層級錯誤的欄位鍵是 `_record`。
    - `validateRows(detail: SchemaDetail, rows: DetailRowState[]): Map<string, Record<string, string>>`：用 `buildZodSchema` 逐列檢查。
  - `DetailGrid`：
    - props：`detail: SchemaDetail`、`modelValue: DetailRowState[]`、`readonly: boolean`、`errors: Map<string, Record<string, string>>`。
    - emits：`update:modelValue`。
  - `DynamicForm` 的 `submit` 改成 `submit: [values: Record<string, unknown>, details: Record<string, DetailRowPayload[]>]`，並對外提供 `uidAt`，讓 FormPage 回填錯誤。
  - DynamicForm 加 prop `detailErrors`；FormPage 把 `details.` 開頭的錯誤轉給它。

- [ ] **Step 1：寫失敗測試**
  - `detail-rows`：
    - `new row payload has values only`
    - `changed row sends changed fields with id and rowVersion`
    - `deleted existing row sends _delete`
    - `new then deleted row omitted`
    - `unchanged row omitted`
    - `rowErrors maps payload index back to row uid`
    - `validateRows flags invalid number`
  - `InlineEditableDataTable`：
    - `renders columns and cell slots`
    - `add button hidden when readonly`
  - `DetailGrid`：
    - `renders one column per detail field, excluding parent field`
    - `computed columns are read-only and show stored values`：formula、autonumber、rollup。
    - `add row appends empty row`
    - `delete new row removes it; delete existing row marks deleted and hides it`
    - `row rule readOnlyAll locks that row`
    - `row visibility false shows blank non-editable cell`
    - `cell error shown under input`
  - `DynamicForm`：
    - `renders DetailGrid per schema.details below blocks`
    - `submit emits details payload`
    - `client row validation blocks submit`
    - `readOnlyAll header makes grids read-only`
  - `FormPage`：
    - `create sends details`
    - `edit sends only changed rows`
    - `edit with no header change but detail change still saves`
    - `400 detail error keys shown on rows, not as outside notify`
    - `409 stale on detail shows reload`
- [ ] **Step 2：跑測試確認失敗**：`bunx vitest run detail-rows InlineEditableDataTable DetailGrid DynamicForm FormPage`。
- [ ] **Step 3：實作**
  - 列的狀態自己管，不放進 vee-validate：`DynamicField` 綁的是最上層欄位鍵。
  - 每格直接用 registry 的 `component` 與 `props`，`label` 給空字串，表頭列顯示欄位名稱。
  - 規則用 `ruleStateOf(明細 schema, 列值, 列的 original, { skipComputed: true })`，逐列計算。
  - `messagesOutside` 判斷時，把 `details.` 開頭、而且對得到列的錯誤鍵視為已顯示。
- [ ] **Step 4：跑測試確認通過**：`bunx vitest run && bunx vue-tsc --noEmit`。
- [ ] **Step 5：Commit**：`feat(frontend): 表單逐列編輯明細並與表頭一起存檔`

### Task 9：明細頁顯示明細

**Files:**
- Create：`presentation/dynamic/details/DetailTables.vue`（+ spec）
- Modify：`presentation/dynamic/components/DynamicDetail.vue` 或 `pages/dynamic/DetailPage.vue`（+ spec）、i18n
- Test：`DetailTables.vitest.spec.ts`、`DetailPage.vitest.spec.ts`

**Interfaces:**
- Consumes：Task 6 的型別、Task 8 的 `InlineEditableDataTable`（`readonly`）。

- [ ] **Step 1：寫失敗測試**
  - `DetailTables`：
    - `renders one table per schema.details with record.details rows`
    - `cells formatted by registry, references by referenceView`
    - `empty shows noData`
  - `DetailPage`：`details render between fields and related tabs`
- [ ] **Step 2：跑測試確認失敗**：`bunx vitest run DetailTables DetailPage`。
- [ ] **Step 3：實作**：放在 `DynamicDetail` 之後、`RelatedTabs` 之前。每個明細模組一張唯讀表，表頭是明細模組名稱。
- [ ] **Step 4：跑測試確認通過**：`bunx vitest run && bunx vue-tsc --noEmit && bunx quasar build`。
- [ ] **Step 5：Commit**：`feat(frontend): 明細頁顯示明細表`

### Task 10：驗收

**Files:**
- Create：
  - `poc/backend/tests/DynamicModules.WebAPITests/Phase6AcceptanceTests.cs`
  - `docs/acceptance/phase6.md`
  - `docs/acceptance/phase6/*.png`
  - `docs/superpowers/plans/2026-11-04-phase6-rulings.md`
- Modify：
  - `README.md`：進度表第 6 列、文件表加入計畫與裁決。
  - `docs/manual.md`：新增「表頭與明細、彙總」一節。
  - spec §15：新增條目，見下。

- [ ] **Step 1：寫驗收測試** `Phase6_Scenario`（固定時鐘 2026-11-05），以 API 走完並逐步斷言：
  1. admin 建 `order`（訂單）：
     - 訂單編號：`autonumber` `SO-{yyyyMM}-{0000}`，當標題。
     - 客戶：`reference` → `entity:sales.customer`。
     - 訂單日期、交期：`date`，交期不得早於訂單日期（驗證規則）。
     - 狀態：`enum`，草稿、已結案；已結案時 `*` 唯讀。
     - 稅率：`percent`，預設 5。
  2. 建 `order-line`（訂單明細）：
     - 訂單：`reference` → `module:order`，`relation = detail`。確認回應裡的 `required = true`、`onDelete = cascade`。
     - 項次：`autonumber` `{0}`，`scope = parent`。
     - 品名：`text`，當標題。
     - 數量：`decimal`。
     - 單價：`currency`。
     - 金額：`formula` `{數量} * {單價}`，`resultType = currency`。
  3. 在 `order` 加：
     - 未稅合計：`rollup` `sum(金額)`。確認 `resultType = currency`。
     - 明細筆數：`rollup` `count`。
     - 稅額：`formula` `ROUND({未稅合計} * {稅率} / 100, 0)`，currency。
     - 含稅總額：`formula` `{未稅合計} + {稅額}`。
     - 兩個模組都啟用。`GET /menu` 有 `order`、沒有 `order-line`。
  4. 建立含 3 列明細的訂單。逐列的數量、單價、預期金額：

     | 列 | 數量 | 單價 | 金額 |
     |---|---|---|---|
     | 1 | 2 | 100 | 200 |
     | 2 | 1 | 50 | 50 |
     | 3 | 4 | 25 | 100 |

     預期：回 201；項次 1、2、3；未稅合計 350、筆數 3、稅額 18、含稅總額 368。
  5. 改第 2 列數量為 3：未稅合計 450、稅額 23、總額 473；表頭 `RowVersion` 換掉。
  6. 刪除第 3 列：未稅合計 350、筆數 2。再新增一列，項次是 `4`。
  7. 交期早於訂單日期：回 400，表頭與明細都沒變。
  8. 明細某一列數量不是數字：回 400，鍵 `details.order-line[0].{數量鍵}`。
  9. 狀態改成已結案之後，只改明細：回 400 `_record`：`此記錄目前不可修改`。
  10. 權限：
      - `order` 給 sales 群組 Read、Add、Edit，範圍 Own，沒有 Pricing；sales-manager 範圍 All，有 Pricing。
      - sales01 建一張訂單。sales02 看不到這張訂單（404），也直接打不到它的明細列（404）。
      - sales01 的 schema 沒有單價、金額、未稅合計、稅額、含稅總額。
      - sales-manager 看得到全部。
  11. 一般參照的彙總：
      - 建 `repair-req`（標題申請人）、`repair-log`（處理內容、工時 `decimal`、申請單參照 `repair-req`）。
      - 在 `repair-req` 加：總工時 `sum(工時)`、紀錄筆數 `count`。
      - 兩筆紀錄（1.5、2）→ 申請的總工時 3.5、筆數 2。
      - 改一筆的申請單指向申請 B → A 是 1.5 與 1，B 是 2 與 1。
      - 刪掉 B 的紀錄 → B 是 0 與 0。
  12. 刪除訂單：回 200，它的明細全部軟刪除。
- [ ] **Step 2：跑驗收與全部測試**：
  - `dotnet test poc/backend --filter Phase6_Scenario` → 通過。
  - `dotnet test poc/backend` → 全部通過，記下各專案數字。
  - `cd poc/frontend && bunx vitest run && bunx vue-tsc --noEmit && bunx quasar build` → 全部通過。
- [ ] **Step 3：實機走查**（`poc/start.command`，Chrome），逐項截圖到 `docs/acceptance/phase6/`：
  1. 設計器：參照欄位勾「明細」、自動編號勾「在表頭內編號」、彙總欄位設定（來源、函數、欄位、條件）；模組清單標示明細模組。
  2. 訂單新增頁：明細表格加 3 列、存檔後明細頁顯示項次、金額與表頭的合計、稅額、總額。
  3. 編輯：改一列數量、刪一列、加一列，存檔後彙總更新、新列項次接續。
  4. 明細列驗證錯誤顯示在那一格。
  5. 已結案的訂單：表單與明細表格都唯讀。
  6. 切成 sales01：看不到單價、金額、合計欄位。
  7. 維修申請的「總工時」在新增維修紀錄後更新。
- [ ] **Step 4：寫文件**
  - `docs/acceptance/phase6.md`：照 `phase5.md` 的格式（自動測試表、情境、走查表、逐項觀察、截圖表、已知限制、實作裁決）。
  - `2026-11-04-phase6-rulings.md`：執行中的裁決（R1…）。
  - spec §15 新增：
    - 第 32 條：彙總算全部子記錄，不看讀者的資料範圍；只看得到本人資料的使用者，可以從彙總值推測別人的記錄（使用者裁決接受）。彙總對象在來源模組對使用者隱藏，不影響彙總欄位的可見性。
    - 第 33 條：一般參照的子記錄存檔會換掉父記錄的 `RowVersion`，正在編輯父記錄的人存檔會收到 409；彙總值沒變時不換。
    - 第 34 條：前端不即時計算公式與彙總，逐列編輯明細時要存檔後才看到新的金額與合計。
    - 第 35 條：`RollupRecalc` 與 `FormulaRecalc` 一樣在交易內載入整個模組，上線前改成分批（併入第 21 條處理）。
  - README 進度表加第 6 列、文件表加第 6 階段計畫與裁決。
  - manual 加「表頭與明細、彙總」一節：怎麼把模組設成明細、明細的權限沿用表頭、彙總的四個函數與條件、什麼時候重算。
- [ ] **Step 5：Commit**：驗收測試、文件、截圖逐檔 add，訊息 `docs: 第 6 階段驗收`。
