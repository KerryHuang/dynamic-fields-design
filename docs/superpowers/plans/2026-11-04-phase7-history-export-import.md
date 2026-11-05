# 動態模組 POC 第 7 階段：歷程、匯出、匯入、設計器預覽、選單 實作計畫

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 每筆記錄有欄位層級的變更歷程，可以在明細頁查看；列表可以依目前的篩選匯出 CSV 或 Excel，也可以匯入 CSV：先預覽驗證結果，確認後才寫入；設計器多一個「預覽」分頁，即時呈現草稿表單；左側選單依模組的「選單分區」分組。

**Architecture:**
- **歷程**：所有使用者發起的寫入（新增、修改、刪除、明細列、刪除時的清空與連帶刪除）都經過同一個 `RecordHistoryWriter`，和記錄在同一個交易內寫入 `Dynamic_RecordHistory`。值存原始 JSON，讀取時才依讀者的權限過濾、依語系轉成顯示文字。
- **顯示文字**：新增 `FieldDisplay`，把欄位的 JSON 值轉成人看得懂的文字（選項名稱、使用者名稱、參照標題、數字小數位）。歷程與匯出共用它；匯入用它的反向 `FieldImportParser`，所以「匯出的檔案原封不動匯回去」會得到相同的值。
- **匯入**：解析出的值走和 `POST records` 完全相同的處理（`RecordCreator`，從 `CreateRecordHandler` 抽出），預覽不寫入，確認時在一個交易內全部寫入，任何一列有錯就整批不寫。
- **設計器預覽**：後端提供以草稿 metadata 組成的 schema（不套權限、不限啟用狀態），前端直接用執行期的 `DynamicForm` 渲染。

**Tech Stack:** 沿用第 1–6 階段。後端新增 `ClosedXML` 0.104.2（產生 .xlsx，MIT 授權）；前端不加套件。

**Spec:** `docs/superpowers/specs/2026-11-03-dynamic-modules-design.md`。本階段對應：
- §13.2 第 7 項。
- §3 目標 1（選單分區）、9（欄位層級的變更歷程）、10（匯出與匯入）。
- §6 `RecordHistory` 表與「只記實際有變的欄位、值存 JSON 字串」。
- §10 動作軸：`Add` 含匯入、`PrintExport` 是匯出；隱藏欄位匯出不含；資料範圍套用到匯出。
- §11.2 端點：`GET /records/{id}/history`、`GET /modules/{key}/export`、`POST /modules/{key}/import`；「匯入先做驗證預覽，確認後才寫入」。
- §11.4 第 5 步：記錄、索引、歷程在同一個交易。
- §12.1 明細頁含歷程；§12.2 選單；§12.4「預覽」分頁。
- §15 第 30 條（SetNull 不寫歷程）：本階段解決。

**前置文件**：第 1–6 階段裁決（`docs/superpowers/plans/2026-11-03-phase{1,2,3,4,5}-rulings.md`、`2026-11-04-phase6-rulings.md`）全部仍然有效。執行時的裁決寫進 `docs/superpowers/plans/2026-11-04-phase7-rulings.md`。

## 本階段範圍

**做：**
- **歷程**：寫入（含明細列、SetNull、連帶刪除）、`GET /records/{id}/history`、明細頁的「歷程」區塊。
- **匯出**：`GET /modules/{key}/export`，CSV（UTF-8 BOM）與 .xlsx；列表頁的「匯出」按鈕，帶目前的篩選、排序、關鍵字。
- **匯入**：`POST /modules/{key}/import`，只新增、不更新；列表頁的「匯入」對話框：選檔 → 預覽 → 確認。
- **設計器預覽**：`GET /modules/{key}/schema/preview`；設計器的「預覽」分頁。
- **選單分區**：建立與編輯模組時可填「選單分區」，左側選單依分區分組。

**不做：**
- 匯入更新既有記錄、匯入明細列：只新增表頭（或一般模組）記錄。
- 匯出、匯入明細模組：明細模組沒有自己的列表，端點回 404（沿用第 6 階段 R9）。
- 擴充模組的匯入：需要指定既有資料，回 400。
- 系統重算的歷程：彙總重算（`RollupRecalc`）、設計器觸發的公式重算（`FormulaRecalc`）、索引回填不寫歷程（決策 1）。
- 歷程的還原、比對兩個版本。
- 模組範本的匯出、匯入：第 8 階段。

## 已定決策（我的判斷，spec 沒說死的部分）

1. **歷程記什麼**：
   - **操作**：`RecordHistoryOperation { Create, Update, Delete }`，存字串（照 `FieldConfiguration` 的 `HasConversion<string>()` 慣例）。
   - **新增**：一列 `Create`，`FieldId`、`OldValue`、`NewValue` 都是 null。不逐欄記初始值。
   - **修改**：每個實際有變的欄位一列 `Update`。「有變」是比較正規化後 JSON 的 `GetRawText()`；鍵不存在與 JSON null 視為相同。
   - **刪除**：每筆被軟刪除的記錄一列 `Delete`（根記錄與連帶刪除的記錄都寫）。
   - **不記的欄位**：`formula`、`rollup`、`autonumber`。它們由系統計算，記了只是噪音，而且一般參照子記錄觸發的彙總變化本來就不會記（不跑歷程），只記一半更難懂。
   - **一般參照的清空（SetNull）**：被清空的來源記錄寫一列 `Update`，`OldValue` 是原參照值、`NewValue` null，`ChangedUserId` 是發起刪除的人。解決 §15 第 30 條。
   - **明細列**：照上面規則寫在明細列自己的 `RecordId` 下（新增列 `Create`、刪除列 `Delete`、修改列逐欄 `Update`）。
   - **時間與人**：同一次存檔的所有列用同一個 `IBusinessTime.Now` 與 `ICurrentUser.UserId`。
   - **一次存檔什麼都沒變**：不寫任何歷程（`RowVersion` 照舊換，第 1 階段行為不變）。
2. **歷程的資料表**：`Dynamic_RecordHistory`，欄位照 spec §6：`Id`（long, identity）、`RecordId`（long, FK → `Dynamic_Record`, Restrict）、`FieldId`（long?, FK → `Dynamic_Field`, Restrict）、`Operation`（nvarchar(10)）、`OldValue`、`NewValue`（nvarchar(max)?，原始 JSON 文字）、`ChangedUserId`（nvarchar(50)）、`ChangedTime`。索引 `(RecordId, ChangedTime)`。
   - 實體帶一個可選的導覽屬性 `Record? Record`：新記錄（`Id == 0`）在同一個 `SaveChanges` 前就能掛上歷程，由 EF 補上 `RecordId`。
   - Migration 名稱 `Phase7History`。
3. **讀取歷程** `GET /records/{id}/history?currentPage=1&pagesSize=20&locale=zh-TW`：
   - 權限：模組的 `Read`，記錄要在資料範圍內；不符照 `GET /records/{id}` 回 404。明細列 id 回 404 `記錄不存在`（第 6 階段決策 2）。
   - 範圍：這筆記錄自己的歷程，加上它所有明細列（含已刪除的列）的歷程。明細列以 `RecordIndex.RefKey` 找，不看 `DeleteFlag`（軟刪除不清索引）。
   - **欄位過濾**：`FieldId` 對讀者 `CanSee == false` 的列整列不回。表頭欄位用表頭的權限，明細列的欄位用 `AuthorizeDetailAsync` 的權限。`Create`、`Delete` 列一律回。
   - 排序：`ChangedTime` 遞減，再 `Id` 遞減。回 `IPagedResponse<RecordHistoryResponse>`，分頁參數沿用 `CurrentPage`、`PagesSize`。
   - 回應：`record RecordHistoryResponse(long Id, string Operation, string? FieldKey, I18nText? FieldLabel, string? OldText, string? NewText, string ChangedUserId, string ChangedUserName, DateTime ChangedTime, HistoryDetailRow? Detail)`；`record HistoryDetailRow(string ModuleKey, I18nText ModuleName, long RecordId, string Title)`。`Operation` 是 `create`／`update`／`delete`。`Detail` 只有明細列的歷程才有（依第 5 階段 R7，null 省略）。
   - `OldText`、`NewText` 由 `FieldDisplay.Format` 依 `locale` 產生；參照值依讀者目前的權限解析標題（見決策 4）。
4. **顯示文字** `FieldDisplay.Format(Field field, JsonElement? value, string locale, ReferenceInfo? reference, IReadOnlyDictionary<string, string> userNames) → string`（`Application/Dynamic/Display/FieldDisplay.cs`，純函式）：
   - null → `""`。
   - `text`、`textarea`、`email`、`phone`、`url`、`time`、`autonumber`：原字串。
   - `integer`、`decimal`、`currency`：`InvariantCulture`、固定 `Config.Scale` 位小數（`integer` 0 位），不加千分位。例：`1234.5`、scale 2 → `1234.50`。
   - `percent`：同上再加 `%`。例：`5` → `5%`。
   - `date`：`yyyy-MM-dd`；`datetime`：存的是 ISO 8601，轉成營運時區（+08:00）`yyyy-MM-dd HH:mm`。
   - `boolean`：zh-TW `是`／`否`，en-US `Yes`／`No`，th、vi 用 en-US。
   - `enum`：選項標籤（`I18nText.Resolve(locale)`，含已停用的選項）；找不到選項時回原 id。`multienum`：各標籤以 `; ` 連接，依選項的排序。
   - `user`：`userNames[id]`，找不到回原 id。
   - `reference`：`reference.State == ok` 時回 `Title`；`restricted` 回 `（無權限）`；`missing` 回 `（已刪除）`；`reference` 為 null 回原鍵。
   - `formula`、`rollup`：依 `Config.ResultType` 當作 `decimal`／`currency`／`percent`／`date` 格式化。
   - 參照標題用既有的 `ReferenceResolver.ResolveAsync(module, perms, rows)`。歷程的舊值、新值不是目前的記錄，就組成暫時的 `Record`（`Id` 用歷程列的 Id、`ValuesJson` 放 `{ fieldKey: 值 }`）交給它解析，不另寫一套。
5. **匯出** `GET /modules/{key}/export?format=csv|xlsx&filter=&sortBy=&sortOrder=&q=&locale=zh-TW`：
   - 權限：`PrintExport`；資料範圍照列表。明細模組、未啟用模組回 404 `模組不存在或未啟用`；沒有 `PrintExport` 回 403。
   - 記錄：和 `records/paged` 相同的篩選、排序、關鍵字（同一個 `RecordQuery` 建法），不分頁。超過 **10000** 筆回 400，鍵 `filter`，訊息 `匯出最多 10000 筆，請加篩選條件`。
   - **欄位**：模組裡讀者看得到（`CanSee`）的啟用欄位，依區塊順序、欄位順序。沒有 Pricing 的人沒有金額欄位那一欄：spec §7 寫「匯出也是空白」，但 §10 寫「隱藏的欄位匯出不含」，而 `PermissionResolver` 本來就把金額欄位列為隱藏，所以照 §10 不含（schema 也不列它，兩邊一致）。
   - 表頭第一列是欄位標籤（`locale`，缺語系退回 zh-TW）。不加 Id、擁有者、稽核欄位，讓匯出檔可以直接匯回。
   - 值：`FieldDisplay.Format`。
   - **CSV**：UTF-8 含 BOM、CRLF、RFC 4180 引號規則（含 `,`、`"`、換行的值加雙引號，內部 `"` 變 `""`）。**公式注入防護**：文字值以 `=`、`+`、`-`、`@` 開頭時前面加 `'`（只對 `text`、`textarea`、`email`、`phone`、`url` 型別，數字負號不加）。
   - **xlsx**：ClosedXML，一個工作表，名稱是模組名稱（截到 31 字元，去掉 `[]:*?/\`）。`integer`、`decimal`、`currency` 與數字結果的 `formula`、`rollup` 寫成數字儲存格，其餘寫文字儲存格（文字儲存格不會被 Excel 當公式，不需要加 `'`）。
   - 檔名：`{moduleKey}-{yyyyMMdd-HHmm}.csv` 或 `.xlsx`（營運時間），`Content-Disposition: attachment`。
6. **匯入** `POST /modules/{key}/import?commit=false|true`，`multipart/form-data`，檔案欄位名 `file`：
   - 權限：`Add`。明細模組、未啟用模組 404；擴充模組 400，鍵 `file`，訊息 `擴充模組不支援匯入`。
   - **檔案限制**（鍵一律 `file`）：
     - 沒有檔案或空檔：`請選擇檔案`。
     - 超過 1 MB：`檔案不能超過 1 MB`。
     - 不是有效的 UTF-8：`請把檔案存成 UTF-8 的 CSV`（Excel 在繁中 Windows 預設存成 Big5，這是最常見的錯）。開頭的 BOM 略過。
     - 沒有表頭列：`檔案沒有表頭`。
     - 資料列超過 **1000**：`一次最多匯入 1000 筆`。空白列（所有欄位都是空字串）略過，不算列數。
     - CSV 格式錯誤（引號沒關）：`第 {n} 行格式不正確`。
   - **欄位對應**：表頭每一欄 trim 後，依序比對：等於欄位 `Key`；或不分大小寫等於欄位標籤的任一語系值。只比對讀者看得到的啟用欄位（隱藏欄位當作不存在，不洩漏）。
     - 對到 `formula`、`rollup`、`autonumber`：略過，原因 `系統計算的欄位`。
     - 對到讀者不能寫的欄位（`CanWrite == false` 或 `Config.ReadOnly`）：略過，原因 `唯讀欄位`。
     - 對不到：略過，原因 `找不到這個欄位`。
     - 兩欄對到同一個欄位：整個檔案 400，鍵 `file`，訊息 `欄位「{標籤}」重複`。
   - **值的解析** `FieldImportParser.Parse(Field field, string text, ImportLookups lookups) → (JsonElement? Value, string? Error)`（`FieldDisplay` 的反向）：
     - trim 後空字串 → null（交給必填檢查）。
     - 文字型別：以 `'` 開頭且第二個字元是 `=`、`+`、`-`、`@` 時去掉 `'`（匯出時加的）。
     - 數字：`InvariantCulture`，允許千分位逗號；`percent` 允許結尾 `%`。不是數字 → `請輸入數字`。
     - `boolean`：不分大小寫的 `是`、`true`、`yes`、`1` → true；`否`、`false`、`no`、`0` → false；其他 → `請輸入「是」或「否」`。
     - `date`：`yyyy-MM-dd` 或 `yyyy/M/d`；`datetime`：`yyyy-MM-dd HH:mm`（營運時區）或 ISO 8601；`time`：`H:mm`。
     - `enum`：依標籤（任一語系、不分大小寫）找選項；對不到 → `找不到選項「{text}」`；對到多個 → `選項「{text}」不只一個`。`multienum`：以 `;` 分割後逐一對應。停用的選項照樣轉成 id，再由型別處理器照第 1 階段規則拒絕。
     - `user`：等於 `UserId` 或 `DisplayName`；對不到 → `找不到使用者「{text}」`。
     - `reference`：依目標的標題完全相等找。動態模組用 `IRecordRepository.QueryLightAsync`（套讀者對目標模組的資料範圍）、既有實體用 provider 的 light 查詢；對不到 → `找不到「{text}」`；多筆 → `「{text}」有多筆同名資料`。
     - 解析成功的值再走 `RecordCreator.ValidateAsync`，也就是 `POST records` 的同一套處理（正規化、必填、規則、選項範圍、參照存在、公式）。
   - **錯誤鍵**：`rows[{line}].{fieldKey}` 與 `rows[{line}]._record`。`line` 是檔案裡的行號（表頭是第 1 行），使用者可以直接對照檔案。
   - **預覽**（`commit=false`）：不寫入任何東西，回 200 `ImportPreviewResponse(List<ImportColumn> Columns, int Total, int Valid, List<ImportRowError> Errors)`；`record ImportColumn(string Header, string? FieldKey, string? IgnoredReason)`；`record ImportRowError(int Line, Dictionary<string, string[]> Errors)`。只列有錯的列。
   - **確認**（`commit=true`，`ITransactionalRequest`）：重新解析、驗證整個檔案（不信任前一次預覽）。任何一列有錯回 400，錯誤就是上面的鍵，整批不寫。全部通過才逐列新增：取自動編號、擁有者是匯入者、寫歷程 `Create`、觸發彙總重算（同 `POST records`）。回 200 `ImportResultResponse(int Created)`。
7. **`RecordCreator`**（`Application/Dynamic/RecordService/RecordCreator.cs`）：把 `CreateRecordHandler` 裡「明細以外」的步驟抽出來，`POST records` 與匯入共用：
   - `Task<(Dictionary<string, JsonElement?> Values, Dictionary<string, string[]> Errors)> ValidateAsync(Module module, EffectivePermissions perms, IReadOnlyDictionary<string, JsonElement?> input, IReadOnlyDictionary<string, JsonElement?>? rollupValues, CancellationToken ct)`：補上一般參照彙總的空集合值（R13）、`ReferenceValidator.ValidKeysAsync`、`RecordValueProcessor.Process`。
   - `Task<Record> InsertAsync(Module module, Dictionary<string, JsonElement?> values, string? hostKey, string? hostTitle, CancellationToken ct)`：取號、建 `Record`、索引、`records.Add`、歷程 `Create`。不 commit。
   - `CreateRecordHandler` 改用這兩個方法，行為不變：第 1–6 階段的測試全部要照舊通過。
8. **設計器預覽** `GET /modules/{key}/schema/preview`：
   - `IDesignerRequest`，需要設計器 `Read`（`DYN00000`）；不看模組的啟用狀態，明細模組也可以預覽。
   - 權限當作系統管理員（全部動作、看得到全部欄位），回應形狀與 `GET schema` 相同，不帶 ETag。
   - `details` 只列啟用中的明細模組（與執行期相同），草稿中的明細模組在預覽裡看不到（已知限制）。
   - 前端「預覽」分頁：頂端說明 `預覽使用目前的草稿設定，不會存檔`，下面是 `DynamicForm`（`mode="create"`）。按「儲存」只跳通知 `預覽不會存檔`，不呼叫 API。欄位、規則、權限改動後，預覽在切回分頁時重新載入（query key 帶 `module.version`）。
9. **選單分區**：
   - `Module.Section` 沿用既有的 `string?`（第 1 階段就有，只是前端沒用）。不做多語系：分區名稱是管理員輸入的文字，長度上限照既有的 validator。
   - 設計器：新增模組對話框與模組設定都多一個「選單分區」文字欄位，可留空。
   - 左側選單：有分區的模組依分區名稱分組，每組一個 `q-item-label header`；沒有分區的模組放在原本的「自訂功能」組。組的順序：「自訂功能」在最前，其餘依分區名稱排序（`localeCompare`）；組內維持 `GET /menu` 的順序。

## Global Constraints

- 後端分層照 spec §11.1：Domain 不依賴 Application；寫入一律 `ITransactionalRequest`、回 `ServiceResult`；時間一律 `IBusinessTime`。
- 錯誤沿用 `ServiceResult` 的 400／403／404／409 映射；欄位錯誤回 `ValidationResponseModel`。
- JSON 回應省略 null（第 5 階段 R7）。
- 固定介面文字放 i18n，四個語系（zh-TW、en-US、th、vi）都要有鍵；th、vi 可以先放英文。
- 下拉選單照第 6 階段 R12 用 `useSelectFilter`。
- 不加計畫以外的套件；ClosedXML 只用在匯出。
- 測試：後端 `dotnet test poc/backend`；前端 `cd poc/frontend && bunx vitest run && bunx vue-tsc --noEmit`。每個 task 結束時全部通過。
- Commit 只 add 本 task 的檔案（不用 `git add -A`／`.`），訊息結尾 `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`。

## Review Focus

1. **權限外漏**：沒有 Pricing 的人，從歷程或匯出看到金額，或看到引用金額的公式、金額彙總；隱藏欄位出現在匯出欄、歷程列、或匯入的「略過原因」。預期全部看不到 —— Task 3、Task 4、Task 6 各有測試。
2. **Excel 存出的 CSV**：繁中 Windows 的 Excel 預設存成 Big5，或帶 BOM 的 UTF-8；值裡有逗號、雙引號、換行。預期 Big5 得到明確訊息、其他都能正確解析 —— Task 5 測試。
3. **匯出再匯入**：把匯出檔直接匯回去，選項、使用者、參照、布林、百分比、日期都要得到相同的值 —— Task 6 的往返測試。
4. **公式注入**：文字欄位存了 `=HYPERLINK(...)`，匯出 CSV 用 Excel 打開不能變成公式，匯回時要還原原文 —— Task 4、Task 5 測試。
5. **歷程的資料範圍**：資料範圍是本人的使用者，用別人記錄的 id 打歷程 API，預期 404，不是空清單 —— Task 3 測試。

---

### Task 1: 歷程的寫入

**Files:**
- Create: `poc/backend/src/DynamicModules.Domain/Entities/Dynamic/RecordHistory.cs`（含 `RecordHistoryOperation`）
- Create: `poc/backend/src/DynamicModules.Domain/Data/Configurations/RecordHistoryConfiguration.cs`
- Create: `poc/backend/src/DynamicModules.Domain/Repositories/Dynamic/IRecordHistoryRepository.cs`
- Create: `poc/backend/src/DynamicModules.Persistence/Repositories/Dynamic/RecordHistoryRepository.cs`
- Create: `poc/backend/src/DynamicModules.Application/Dynamic/RecordService/RecordHistoryWriter.cs`
- Create: migration `Phase7History`（`dotnet ef migrations add Phase7History`，專案與 startup 照既有 migration 的做法）
- Modify: `CreateRecordHandler.cs`、`UpdateRecordHandler.cs`、`DeleteRecordHandler.cs`、`DetailWriter.cs`、Persistence 與 Application 的 `DependencyInjection.cs`
- Test: `poc/backend/tests/DynamicModules.ApplicationTests/Dynamic/RecordHistoryWriteTests.cs`

**Interfaces:**
- Produces:
  - `IRecordHistoryRepository.Add(RecordHistory h)`；`Task<(IReadOnlyList<RecordHistory> Items, int Total)> QueryAsync(IReadOnlyCollection<long> recordIds, int page, int size, CancellationToken ct)`（Task 3 用；排序照決策 3）。
  - `RecordHistoryWriter`（scoped）：`void Created(Record r)`、`void Deleted(Record r)`、`void Updated(Record r, Module module, IReadOnlyDictionary<string, JsonElement?>? before, IReadOnlyDictionary<string, JsonElement?> after)`。依決策 1 決定寫哪些列；`ChangedUserId`、`ChangedTime` 取 `ICurrentUser`、`IBusinessTime`，同一個 writer 實例在一次請求內用同一個時間。

- [ ] **Step 1: 寫失敗的測試**（`RecordHistoryWriteTests`，用既有 `DesignerTestBase` 的建模組輔助）

```csharp
[Fact] Create_WritesOneCreateRow          // POST 後：1 列 Operation=Create、FieldId=null、ChangedUserId=目前使用者
[Fact] Update_WritesOnlyChangedFields     // 改 text 欄 a→b、另一欄不變：只有 1 列 Update，OldValue="\"a\""、NewValue="\"b\""
[Fact] Update_NothingChanged_WritesNothing
[Fact] Update_ClearingValue_WritesNullNew // 值改成 null：NewValue == null
[Fact] Update_SkipsFormulaRollupAutonumber// 改數量讓公式結果變了：只有數量那一列
[Fact] Delete_WritesDeleteRow_ForRootAndCascaded
[Fact] Delete_SetNull_WritesUpdateOnClearedSource // 來源記錄多一列 Update，FieldId=參照欄位，NewValue=null，ChangedUserId=刪除者
[Fact] DetailRows_WriteUnderRowRecordId   // 表頭存檔新增一列、修改一列、刪除一列：三筆明細列各自的 Create／Update／Delete
[Fact] FailedSave_WritesNothing           // 驗證失敗回 400 時資料表沒有新歷程（TransactionBehavior 回滾）
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `dotnet test poc/backend --filter FullyQualifiedName~RecordHistoryWriteTests`
Expected: 編譯失敗（`RecordHistory` 不存在）。

- [ ] **Step 3: 實作實體、設定、repository、migration**（決策 2）

- [ ] **Step 4: 實作 `RecordHistoryWriter`，掛進寫入點**
  - `CreateRecordHandler`：`records.Add` 之後 `Created`。
  - `UpdateRecordHandler`：寫入 `ValuesJson` 之後 `Updated(record, module, previous, values)`。
  - `DetailWriter.ApplyAsync`：新增列 `Created`、修改列 `Updated`（before 用 `DetailRowChange.Before`）、刪除列 `Deleted`。
  - `DeleteRecordHandler`：`deleted` 每筆 `Deleted`；`ToClear` 每筆 `Updated(source, sourceModule, before, values)`（來源模組要載入欄位才能判斷型別：沿用迴圈下方已載入的模組）。
  - `RollupRecalc`、`FormulaRecalc`、`RecordIndexBackfill` 不動。

- [ ] **Step 5: 跑測試確認通過，並跑全部後端測試**

Run: `dotnet test poc/backend`
Expected: 全部 PASS（第 1–6 階段的測試不受影響）。

- [ ] **Step 6: Commit** `feat: 記錄寫入時寫欄位層級歷程（含明細列與刪除清空）`

### Task 2: 顯示文字 `FieldDisplay`

**Files:**
- Create: `poc/backend/src/DynamicModules.Application/Dynamic/Display/FieldDisplay.cs`
- Test: `poc/backend/tests/DynamicModules.ApplicationTests/Dynamic/FieldDisplayTests.cs`

**Interfaces:**
- Produces: `FieldDisplay.Format(Field field, JsonElement? value, string locale, ReferenceInfo? reference, IReadOnlyDictionary<string, string> userNames) → string`（決策 4）；`FieldDisplay.IsNumericCell(Field field) → bool`（xlsx 用：`integer`、`decimal`、`currency`，以及 `ResultType` 是 `decimal`／`currency` 的 `formula`、`rollup`）。

- [ ] **Step 1: 寫失敗的測試**（`[Theory]` 逐型別，值與預期照決策 4）

```csharp
// decimal scale 2: 1234.5 → "1234.50"；integer 3 → "3"；currency scale 0: 1500 → "1500"
// percent scale 0: 5 → "5%"
// date "2026-11-04" → "2026-11-04"；datetime "2026-11-04T01:30:00Z" → "2026-11-04 09:30"
// boolean true: zh-TW "是"、en-US "Yes"、th "Yes"
// enum opt_3 → 選項 3 的 zh-TW 標籤；locale en-US 且有 en-US 標籤 → en-US 標籤；已停用選項照樣顯示；未知 id → "opt_99"
// multienum ["opt_7","opt_3"] → 依選項排序以 "; " 連接
// user "sales1" → userNames["sales1"]；未知 → "nobody"
// reference ok → Title；restricted → "（無權限）"；missing → "（已刪除）"；reference null → 原鍵
// formula ResultType currency scale 0: 52500 → "52500"
// null → ""
```

- [ ] **Step 2: 跑測試確認失敗** — Run: `dotnet test poc/backend --filter FullyQualifiedName~FieldDisplayTests`，Expected: 編譯失敗。
- [ ] **Step 3: 實作**（營運時區沿用 `TaipeiBusinessTime` 的 +08:00；不要讓 Application 依賴 WebAPI，把偏移常數放在 `IBusinessTime` 旁或以參數傳入，擇一並在 rulings 記下）
- [ ] **Step 4: 跑測試確認通過** — Run 同上，Expected: PASS。
- [ ] **Step 5: Commit** `feat: 欄位值的顯示文字（歷程與匯出共用）`

### Task 3: 歷程查詢 API

**Files:**
- Create: `poc/backend/src/DynamicModules.Application/Dynamic/RecordService/Queries/GetRecordHistory/GetRecordHistoryRequest.cs`、`GetRecordHistoryHandler.cs`
- Create: `poc/backend/src/DynamicModules.Application/Dynamic/RecordService/Data/HistoryResponses.cs`
- Modify: `IRecordRepository.cs` 與 `RecordRepository.cs`（加 `ListDetailRowIdsAsync`）、`RecordsController.cs`
- Test: `poc/backend/tests/DynamicModules.ApplicationTests/Dynamic/RecordHistoryReadTests.cs`、`poc/backend/tests/DynamicModules.WebAPITests/Dynamic/HistoryApiTests.cs`

**Interfaces:**
- Consumes: Task 1 的 `IRecordHistoryRepository.QueryAsync`；Task 2 的 `FieldDisplay.Format`；既有 `ReferenceResolver.ResolveAsync`、`IDynamicAccessGuard.AuthorizeDetailAsync`、`IModuleRepository.ListUserIdsAsync`（使用者名稱用 `GET /users` 同一個來源）。
- Produces:
  - `GetRecordHistoryRequest(long Id, int CurrentPage, int PagesSize, string Locale) : IRequest<ServiceResult>`。
  - `IRecordRepository.ListDetailRowIdsAsync(long detailFieldId, string parentKey, CancellationToken ct) → Task<IReadOnlyList<long>>`（含已刪除）。
  - 端點 `GET /api/v3/dynamic/records/{id}/history`，回應照決策 3。前端型別名 `RecordHistoryEntry`。

- [ ] **Step 1: 寫失敗的測試**

```csharp
[Fact] History_NewestFirst_WithDisplayText    // 建立後改兩次：3 列，第一列是最後一次修改，OldText/NewText 是選項標籤
[Fact] History_IncludesDetailRows_WithRowTitle // 明細列的 Update：Detail.ModuleKey=明細模組、Detail.Title=列標題；刪掉的列也在
[Fact] History_HiddenField_Omitted             // 欄位對群組 Hidden：該欄的 Update 列不出現，Create 列仍在
[Fact] History_CurrencyWithoutPricing_Omitted  // 無 Pricing：金額欄位的列不出現（明細列的單價也是）
[Fact] History_OutOfScope_404                  // 資料範圍 Own 的使用者查別人的記錄 → 404 記錄不存在
[Fact] History_DetailRowId_404
[Fact] History_ReferenceTitle_RespectsReaderAccess // 參照目標讀者沒有 Read → "（無權限）"
[Fact] History_Paged                           // pagesSize=2：total 正確、第二頁接續
```

WebAPI 測試一條：`GET records/{id}/history?locale=en-US` 回 200，`operation` 是小寫 `create`，`changedUserName` 是顯示名稱。

- [ ] **Step 2: 跑測試確認失敗**
- [ ] **Step 3: 實作 handler 與 repository 方法**（權限、範圍、過濾照決策 3；參照標題照決策 4 的暫時 `Record` 做法）
- [ ] **Step 4: 跑全部後端測試確認通過**
- [ ] **Step 5: Commit** `feat: 記錄歷程查詢 API（含明細列、依權限過濾）`

### Task 4: 匯出 API

**Files:**
- Create: `poc/backend/src/DynamicModules.Application/Dynamic/ExportService/Queries/ExportRecords/ExportRecordsRequest.cs`、`ExportRecordsHandler.cs`
- Create: `poc/backend/src/DynamicModules.Application/Dynamic/ExportService/CsvWriter.cs`、`XlsxWriter.cs`
- Modify: `poc/backend/src/DynamicModules.Application/DynamicModules.Application.csproj`（`ClosedXML` 0.104.2）、新增 `poc/backend/src/DynamicModules.WebAPI/Controllers/v3/Dynamic/ImportExportController.cs`
- Test: `poc/backend/tests/DynamicModules.ApplicationTests/Dynamic/ExportTests.cs`、`poc/backend/tests/DynamicModules.WebAPITests/Dynamic/ExportApiTests.cs`

**Interfaces:**
- Consumes: Task 2 的 `FieldDisplay`；`GetPagedRecordsHandler` 建 `RecordQuery` 的邏輯（抽成兩邊共用的靜態方法，不要複製一份）。
- Produces:
  - `ExportRecordsRequest(string ModuleKey, string Format, string? Filter, IReadOnlyList<string> SortBy, IReadOnlyList<string> SortOrder, string? Q, string Locale) : IRequest<ServiceResult>`，成功時回 `ExportFile(string FileName, string ContentType, byte[] Content)`。
  - `CsvWriter.Write(IReadOnlyList<string> header, IEnumerable<IReadOnlyList<string>> rows) → byte[]`；`CsvWriter.EscapeFormula(string value) → string`。
  - `format` 不是 `csv`／`xlsx` 回 400，鍵 `format`，訊息 `格式只能是 csv 或 xlsx`。

- [ ] **Step 1: 寫失敗的測試**

```csharp
[Fact] Csv_HasBomHeaderAndRows             // 開頭 EF BB BF；第一列是 zh-TW 標籤；CRLF
[Fact] Csv_QuotesCommaQuoteNewline         // 值 a,"b"\nc → "a,""b""\nc"（含外層引號）
[Fact] Csv_EscapesFormulaInjection         // text "=1+1" → "'=1+1"；decimal -5 → "-5"
[Fact] Export_RespectsFilterSortAndScope   // 篩選後只剩 2 筆、依排序；Own 使用者只匯出自己的
[Fact] Export_WithoutPricing_NoCurrencyColumn // 無 Pricing：沒有單價、金額公式、金額彙總那幾欄
[Fact] Export_HiddenField_NoColumn
[Fact] Export_WithoutPrintExport_403
[Fact] Export_DetailModule_404
[Fact] Export_OverLimit_400                // 以替身或常數注入讓上限變小來測，不要真的建 10001 筆
[Fact] Xlsx_NumericCellsAreNumbers         // 用 ClosedXML 讀回：decimal 欄是數字、enum 欄是標籤文字、工作表名稱是模組名
```

WebAPI 測試：`GET export?format=csv` 回 `text/csv; charset=utf-8`，`Content-Disposition` 檔名是 `{key}-yyyyMMdd-HHmm.csv`。

- [ ] **Step 2: 跑測試確認失敗**
- [ ] **Step 3: 實作**（決策 5）
- [ ] **Step 4: 跑全部後端測試確認通過**
- [ ] **Step 5: Commit** `feat: 匯出 CSV／Excel（依權限、資料範圍與列表條件）`

### Task 5: 匯入的解析（CSV 讀取與值解析）

**Files:**
- Create: `poc/backend/src/DynamicModules.Application/Dynamic/ImportService/CsvReader.cs`
- Create: `poc/backend/src/DynamicModules.Application/Dynamic/ImportService/FieldImportParser.cs`
- Test: `poc/backend/tests/DynamicModules.ApplicationTests/Dynamic/CsvReaderTests.cs`、`FieldImportParserTests.cs`

**Interfaces:**
- Produces:
  - `CsvReader.Read(byte[] content) → CsvReadResult`；`record CsvReadResult(string? Error, IReadOnlyList<string> Header, IReadOnlyList<CsvRow> Rows)`；`record CsvRow(int Line, IReadOnlyList<string> Cells)`。`Error` 是決策 6 的檔案層級訊息（UTF-8、表頭、格式、1000 筆）；`Line` 是起始行號（一筆值內含換行時，下一筆的行號照實際行數往後算）。
  - `FieldImportParser.Parse(Field field, string text, ImportLookups lookups) → (JsonElement? Value, string? Error)`。
  - `record ImportLookups(IReadOnlyDictionary<string, string> UserIdByName, IReadOnlyDictionary<long, IReadOnlyDictionary<string, IReadOnlyList<string>>> RefKeysByTitle)`：`RefKeysByTitle[fieldId][title]` 是同名的參照鍵（Task 6 預先查好再傳進來，parser 保持純函式）。

- [ ] **Step 1: 寫失敗的測試**

```csharp
// CsvReader
[Fact] Read_SkipsBom
[Fact] Read_QuotedCommaQuoteNewline        // 與 Task 4 的 Csv 測試互為往返：CsvWriter 寫出的位元組讀回相同的 cells
[Fact] Read_Big5_ReturnsUtf8Error          // Encoding.GetEncoding(950) 需要 CodePagesEncodingProvider；測試裡可直接用寫死的 Big5 位元組
[Fact] Read_UnclosedQuote_ReturnsLineError // "第 3 行格式不正確"
[Fact] Read_SkipsBlankRows_CountsLimit     // 1001 筆資料列 → "一次最多匯入 1000 筆"；空白列不算
[Fact] Read_LineNumbersAccountForEmbeddedNewlines
// FieldImportParser（值照決策 6）
[Theory] Parse_Numbers                     // "1,234.5"→1234.5；"5%"(percent)→5；"abc"→請輸入數字
[Theory] Parse_Boolean                     // 是/否/TRUE/no/1/0；"maybe"→錯誤訊息
[Theory] Parse_Dates                       // "2026/11/4"→"2026-11-04"；datetime "2026-11-04 09:30"→ +08:00 對應的 ISO
[Fact] Parse_Enum_ByAnyLocaleLabel_CaseInsensitive
[Fact] Parse_Enum_Ambiguous / Parse_Enum_NotFound
[Fact] Parse_MultiEnum_SplitsSemicolon
[Fact] Parse_User_ByIdOrName
[Fact] Parse_Reference_UniqueAmbiguousMissing
[Fact] Parse_Text_StripsInjectionApostrophe // "'=1+1"→"=1+1"；"'abc"→"'abc"
[Fact] Parse_Empty_IsNull
```

- [ ] **Step 2: 跑測試確認失敗**
- [ ] **Step 3: 實作**（UTF-8 驗證用 `new UTF8Encoding(false, throwOnInvalidBytes: true)`）
- [ ] **Step 4: 跑測試確認通過**
- [ ] **Step 5: Commit** `feat: CSV 讀取與匯入值解析`

### Task 6: 匯入 API 與 `RecordCreator`

**Files:**
- Create: `poc/backend/src/DynamicModules.Application/Dynamic/RecordService/RecordCreator.cs`
- Create: `poc/backend/src/DynamicModules.Application/Dynamic/ImportService/Commands/ImportRecords/ImportRecordsRequest.cs`、`ImportRecordsHandler.cs`、`PreviewImportRequest.cs`、`PreviewImportHandler.cs`
- Create: `poc/backend/src/DynamicModules.Application/Dynamic/ImportService/ImportPlanner.cs`（兩個 handler 共用：欄位對應、預查 lookups、逐列解析與驗證）
- Create: `poc/backend/src/DynamicModules.Application/Dynamic/ImportService/Data/ImportResponses.cs`
- Modify: `CreateRecordHandler.cs`（改用 `RecordCreator`）、`ImportExportController.cs`
- Test: `poc/backend/tests/DynamicModules.ApplicationTests/Dynamic/ImportTests.cs`、`poc/backend/tests/DynamicModules.WebAPITests/Dynamic/ImportApiTests.cs`

**Interfaces:**
- Consumes: Task 1 的 `RecordHistoryWriter.Created`；Task 4 的 `CsvWriter`（往返測試）；Task 5 的 `CsvReader`、`FieldImportParser`、`ImportLookups`。
- Produces:
  - `RecordCreator.ValidateAsync`、`RecordCreator.InsertAsync`（決策 7）。
  - `PreviewImportRequest(string ModuleKey, byte[] Content) : IRequest<ServiceResult>` → `ImportPreviewResponse`。
  - `ImportRecordsRequest(string ModuleKey, byte[] Content) : IRequest<ServiceResult>, ITransactionalRequest` → `ImportResultResponse`。
  - 端點 `POST /modules/{key}/import?commit=false|true`（決策 6；`commit` 預設 false）。前端型別名 `ImportPreview`、`ImportResult`。

- [ ] **Step 1: 寫失敗的測試**

```csharp
[Fact] Preview_MapsHeadersByKeyOrAnyLocaleLabel_ReportsIgnored // 公式欄「系統計算的欄位」、唯讀欄「唯讀欄位」、亂打的欄「找不到這個欄位」
[Fact] Preview_DuplicateColumn_400
[Fact] Preview_ReportsRowErrorsByLine_WritesNothing            // 第 3 行必填缺、第 5 行選項不存在：errors 鍵 f_x，Line 3、5；資料表沒有新記錄
[Fact] Preview_HiddenField_TreatedAsUnknown                    // 隱藏欄位的表頭 → 「找不到這個欄位」，不洩漏它存在
[Fact] Preview_AppliesRulesAndOptionScope                      // 條件必填、跨欄位驗證、選項範圍與 POST records 相同的錯誤
[Fact] Commit_AnyRowInvalid_400_WritesNothing                  // 鍵 rows[3].f_x
[Fact] Commit_CreatesAll_WithAutonumberOwnerHistory            // 3 筆：自動編號遞增、擁有者=匯入者、各有一列 Create 歷程
[Fact] Commit_ChildOfLookupRollup_RecalculatesParent           // 匯入一般參照的子記錄，父記錄的彙總跟著變
[Fact] RoundTrip_ExportThenImport_SameValues                   // 匯出含 enum、multienum、user、reference、boolean、percent、date、text "=x" 的記錄，原檔匯入，新記錄 Values 與原記錄相同（除自動編號、公式）
[Fact] Import_WithoutAdd_403 / Import_DetailModule_404 / Import_Extension_400
[Fact] CreateRecord_StillBehavesTheSame                        // 不寫新測試：第 1–6 階段的 RecordServiceTests、DetailSaveTests 全部通過即是
```

WebAPI 測試：multipart 上傳 `file`，`commit=false` 回 200 預覽，`commit=true` 回 200 `{ created: 2 }`；超過 1 MB 回 400 鍵 `file`。

- [ ] **Step 2: 跑測試確認失敗**
- [ ] **Step 3: 抽出 `RecordCreator`，`CreateRecordHandler` 改用它；跑全部後端測試，確認沒有回歸**
- [ ] **Step 4: 實作 `ImportPlanner` 與兩個 handler**（預覽與確認走同一份 planner，確認時才呼叫 `InsertAsync` 與彙總重算；每列的錯誤鍵加 `rows[{line}].` 前綴）
- [ ] **Step 5: 跑全部後端測試確認通過**
- [ ] **Step 6: Commit** `feat: CSV 匯入（預覽驗證、確認後整批寫入）`

### Task 7: 設計器預覽 schema API

**Files:**
- Create: `poc/backend/src/DynamicModules.Application/Dynamic/SchemaService/Queries/GetPreviewSchema/GetPreviewSchemaRequest.cs`、`GetPreviewSchemaHandler.cs`
- Modify: `GetModuleSchemaHandler.cs`（把組 schema 的部分改成兩個 handler 共用，權限以參數傳入）、`SchemaController.cs`
- Test: `poc/backend/tests/DynamicModules.ApplicationTests/Dynamic/SchemaServiceTests.cs`（新增案例）

**Interfaces:**
- Produces: `GetPreviewSchemaRequest(string ModuleKey) : IRequest<ServiceResult>, IDesignerRequest`（`RequiredDesignerAction = Read`）；端點 `GET /api/v3/dynamic/modules/{key}/schema/preview`，回應與 `ModuleSchemaResponse` 相同、不帶 ETag。前端 `dynamicApi.getPreviewSchema(key)`。

- [ ] **Step 1: 寫失敗的測試**

```csharp
[Fact] Preview_DraftModule_ReturnsSchema        // 草稿模組：GET schema 是 404，preview 是 200 且含全部啟用欄位與規則
[Fact] Preview_IgnoresFieldPermissions           // 有 Hidden 例外的欄位也在；permissions 全部為 true
[Fact] Preview_DetailModule_Allowed
[Fact] Preview_WithoutDesignerRead_403
[Fact] Preview_UnknownModule_404
```

- [ ] **Step 2: 跑測試確認失敗**
- [ ] **Step 3: 實作**（決策 8；系統管理員權限用 `PermissionResolver.Resolve` 對 `IsSystemAdmin` 的既有分支組出，不要另寫一份權限物件）
- [ ] **Step 4: 跑全部後端測試確認通過**
- [ ] **Step 5: Commit** `feat: 設計器預覽用的草稿 schema API`

### Task 8: 明細頁的歷程區塊

**Files:**
- Create: `poc/frontend/src/presentation/dynamic/history/RecordHistory.vue`
- Modify: `poc/frontend/src/services/api/dynamic-api.ts`（`recordHistory`）、`poc/frontend/src/domain/dynamic/types.ts`（`RecordHistoryEntry`）、`poc/frontend/src/application/dynamic/queries/records-queries.ts`（`useRecordHistory`）、`poc/frontend/src/pages/dynamic/DetailPage.vue`、四個 i18n 檔
- Test: `poc/frontend/src/presentation/dynamic/history/RecordHistory.vitest.spec.ts`

**Interfaces:**
- Consumes: Task 3 的端點與回應形狀。
- Produces: `dynamicApi.recordHistory(id: number, page: number, size: number, locale: string) → Promise<PagedResponse<RecordHistoryEntry>>`；`<RecordHistory :record-id="number" />`。

- [ ] **Step 1: 寫失敗的測試**

```ts
it('lists entries newest first with field label, old → new, user and time')
it('shows create and delete as sentences, not as field rows')   // 「建立了這筆記錄」「刪除了這筆記錄」
it('prefixes detail-row entries with the detail module and row title') // 「明細 › 品名 A：數量」
it('pages with q-pagination when total exceeds the page size')
it('refetches when the locale changes')                         // query key 含 locale
```

- [ ] **Step 2: 跑測試確認失敗** — Run: `cd poc/frontend && bunx vitest run src/presentation/dynamic/history`
- [ ] **Step 3: 實作**：明細頁在 `RelatedTabs` 之下加一張 `q-card`「歷程」，內含 `q-table`（欄：時間、使用者、欄位、修改前、修改後），每頁 20 筆。記錄存檔或刪除明細後，`useRecordHistory` 的 query 要失效（沿用 `records-queries.ts` 既有的 invalidate 寫法）。
- [ ] **Step 4: 跑前端測試與 vue-tsc 確認通過**
- [ ] **Step 5: Commit** `feat: 明細頁顯示記錄歷程`

### Task 9: 列表頁的匯出與匯入

**Files:**
- Create: `poc/frontend/src/presentation/dynamic/list/ExportMenu.vue`、`poc/frontend/src/presentation/dynamic/list/ImportDialog.vue`
- Modify: `dynamic-api.ts`（`exportRecords`、`previewImport`、`commitImport`）、`types.ts`（`ImportPreview`、`ImportResult`）、`poc/frontend/src/pages/dynamic/ListPage.vue`、四個 i18n 檔
- Test: `poc/frontend/src/presentation/dynamic/list/ExportMenu.vitest.spec.ts`、`ImportDialog.vitest.spec.ts`、`ListPage.vitest.spec.ts`（新增案例）

**Interfaces:**
- Consumes: Task 4、Task 6 的端點。
- Produces:
  - `dynamicApi.exportRecords(key: string, format: 'csv' | 'xlsx', query: Omit<PagedRecordsQuery, 'currentPage' | 'pagesSize'>, locale: string) → Promise<{ blob: Blob; fileName: string }>`：axios `responseType: 'blob'`，檔名取自 `Content-Disposition`（POC 沒有 cookie 身分，`X-User` 只能走 axios，不能用 `<a href>` 直接下載）。
  - `dynamicApi.previewImport(key: string, file: File) → Promise<ImportPreview>`、`dynamicApi.commitImport(key: string, file: File) → Promise<ImportResult>`（`FormData`，欄位名 `file`）。

- [ ] **Step 1: 寫失敗的測試**

```ts
// ExportMenu
it('is hidden without printExport permission')
it('passes the current filter, sort, keyword and locale and saves the blob with the server file name')
// ImportDialog
it('is reachable only with add permission')                    // ListPage 案例
it('previews: shows mapped and ignored columns, total/valid counts and row errors by line')
it('disables confirm while any row has errors')
it('confirms, notifies the created count and refetches the list')
it('shows the server file error (key "file") inline')
```

- [ ] **Step 2: 跑測試確認失敗**
- [ ] **Step 3: 實作**：
  - 列表工具列在「新增」旁加「匯出」下拉（CSV、Excel）與「匯入」按鈕。
  - 匯入對話框：`q-file`（`accept=".csv"`）→ 自動呼叫預覽 → 顯示欄位對應表（表頭、對應的欄位標籤或略過原因）、`共 {total} 筆，{valid} 筆可匯入`、錯誤表（行號、欄位標籤、訊息；`_record` 顯示為「整筆」）→「確認匯入」。有任何錯誤時確認按鈕停用，提示 `修正檔案後重新選擇`。確認成功 `已匯入 {n} 筆`、關閉對話框、列表 refetch。
  - 確認時伺服器回 400（預覽與確認之間資料變了，例如參照目標被刪）：把錯誤顯示回錯誤表，不關閉。
- [ ] **Step 4: 跑前端測試與 vue-tsc 確認通過**
- [ ] **Step 5: Commit** `feat: 列表頁匯出 CSV／Excel 與 CSV 匯入對話框`

### Task 10: 設計器預覽分頁與選單分區

**Files:**
- Create: `poc/frontend/src/presentation/dynamic/designer/PreviewTab.vue`
- Modify: `dynamic-api.ts`（`getPreviewSchema`）、`schema-queries.ts`（`usePreviewSchema(key, version)`）、`ModuleEditorPage.vue`（第四個分頁、模組設定的分區欄位）、`ModuleListPage.vue`（新增對話框的分區欄位）、`MainLayout.vue`（依分區分組）、四個 i18n 檔
- Test: `PreviewTab.vitest.spec.ts`、`MainLayout.vitest.spec.ts`、`ModuleListPage.vitest.spec.ts`、`ModuleEditorPage.vitest.spec.ts`（新增案例）

**Interfaces:**
- Consumes: Task 7 的端點；既有 `DynamicForm`、`GET /menu` 的 `section`。

- [ ] **Step 1: 寫失敗的測試**

```ts
// PreviewTab
it('renders DynamicForm in create mode from the preview schema with the draft notice')
it('does not call any write API on save and notifies "預覽不會存檔"')
it('refetches when the module version changes')
// ModuleEditorPage
it('has a preview tab')
it('saves the menu section with the module settings')
// ModuleListPage
it('sends section when creating a module; empty input sends no section')
// MainLayout
it('groups menu items by section, unsectioned under 自訂功能 first, sections sorted by name')
```

- [ ] **Step 2: 跑測試確認失敗**
- [ ] **Step 3: 實作**（決策 8、9）
- [ ] **Step 4: 跑前端測試與 vue-tsc 確認通過**
- [ ] **Step 5: Commit** `feat: 設計器預覽分頁、選單分區`

### Task 11: 驗收情境、文件與實機走查

**Files:**
- Create: `poc/backend/tests/DynamicModules.WebAPITests/Phase7AcceptanceTests.cs`
- Create: `docs/acceptance/phase7.md`、`docs/acceptance/phase7/*.png`
- Modify: `README.md`（進度表第 7 列、計畫與裁決連結）、`docs/manual.md`（歷程、匯出、匯入、預覽、選單分區各一節；已知限制）、spec §15（第 30 條標為第 7 階段已處理；新增本階段的已知限制）

- [ ] **Step 1: 寫 `Phase7_Scenario`**（一個 WebAPI 測試走完整條路，沿用 `Phase6AcceptanceTests` 的寫法）：
  1. 建訂單與明細模組（同第 6 階段），訂單分區設為「業務」，啟用；`GET /menu` 的項目帶 `section: "業務"`。
  2. 草稿時 `schema/preview` 回 200、`schema` 回 404。
  3. 業務建一張含 2 列明細的訂單，改表頭狀態、改一列數量、刪一列：歷程依時間倒序，含表頭的 `Create`、狀態 `Update`（選項標籤）、明細列的 `Create`／`Update`／`Delete`。
  4. 沒有 Pricing 的群組看歷程：沒有單價、金額相關的列。
  5. 主管匯出 CSV：有 BOM、欄位標籤、狀態是標籤文字；業務沒有 PrintExport → 403。
  6. 把匯出檔改兩列後匯入預覽：一列選項打錯 → 錯誤在正確的行號；修正後確認匯入 → 建立 N 筆，每筆有自己的訂單編號與 `Create` 歷程。
  7. 刪掉被 SetNull 參照的記錄：來源記錄多一列 `Update` 歷程。
- [ ] **Step 2: 跑全部測試**：`dotnet test poc/backend`、`cd poc/frontend && bunx vitest run && bunx vue-tsc --noEmit && bunx quasar build`。
- [ ] **Step 3: 實機走查**（`poc/start.command`，Chrome）：選單分區、預覽分頁、明細頁歷程、匯出 CSV 用文字編輯器確認 BOM 與內容、匯出 xlsx、匯入預覽錯誤與確認。每項截圖存 `docs/acceptance/phase7/`。
- [ ] **Step 4: 寫 `docs/acceptance/phase7.md`**（格式照 `phase6.md`：自動測試表、情境、走查表、截圖表、已知限制、裁決連結），更新 README、手冊、spec §15。
- [ ] **Step 5: Commit** `docs: 第 7 階段驗收、手冊與 spec 已知限制`

## 已知限制（寫進 spec §15 與手冊）

- 系統重算（彙總、設計器觸發的公式重算）不寫歷程；公式、彙總、自動編號欄位的變化也不記（決策 1）。
- 歷程的參照標題依目前的資料解析；目標改名後，舊歷程顯示新名稱。
- 匯入只新增、不更新，不含明細列；擴充模組不能匯入。
- 匯出上限 10000 筆、匯入上限 1000 筆，一次載入記憶體；上線前改成串流與分批（與 §15 第 21、35 條一起處理）。
- 匯出沒有 Pricing 時不含金額欄位（照 §10），而不是 §7 寫的「空白」；上線前和既有匯出的做法對齊。
- `ClosedXML` 是 POC 自選的套件；正式版改用團隊選定的 Excel 元件。
- 預覽看不到草稿中的明細模組。
- 選單分區是單一語系的文字。
