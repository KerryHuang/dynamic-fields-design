# 動態模組 POC 第 8 階段：模組範本 實作計畫

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 管理員可以從內建的「訂單」範本（或匯入的範本）建立一組模組：可以改代碼、名稱，勾選要哪些欄位，在一個交易內建成草稿；任何模組（含它的明細模組）都可以匯出成同一種 JSON 範本，再匯入另一個庫。

**Architecture:**
- **範本格式**：一份 JSON 套件（`TemplatePackage`），欄位、區塊、選項、模組之間全用範本內部的代號（code）互相引用，不含任何資料庫 id。
- **從範本建立＝重播設計器指令**：`TemplateInstantiator` 依序送出既有的 MediatR 指令（`CreateModule`、`AddBlock`、`AddField`、`AddOption`、`UpdateField`、`ReorderFields`、`UpdateModule`、`SaveRules`、`CreateView`、`SaveModulePermissions`），每一步把代號換成剛配發的 id。驗證和手動在設計器操作完全相同，不另寫一套。
  - 外層 `InstantiateTemplateRequest` 是唯一的交易，內層指令沿用外層交易（`EfTransactionManager` 支援巢狀）。任何一步失敗，整批回滾。
- **內建範本**：`poc/backend/templates/*.json`，站台啟動時載入並做結構驗證，有錯就拒絕啟動。匯入的範本存進新的資料表 `Dynamic_ModuleTemplate`。
- **訂單範本需要的兩種預設值**：spec 的訂單範本寫「訂單日期預設今天」「明細交期預設帶表頭交期」，目前程式沒有，本階段補上（決策 2）。

**Tech Stack:** 沿用第 1–7 階段，不加套件。

**Spec:** `docs/superpowers/specs/2026-11-03-dynamic-modules-design.md`。本階段對應：
- §13.2 第 8 項。
- §3 目標 12（模組範本）。
- §13.1 全節：格式、內建範本、從範本建立、匯出與匯入、端點、內建範本一「訂單」、規則、權限建議。
- §13.2 Demo 情境一「從範本建訂單」。

**前置文件**：第 1–7 階段裁決（`docs/superpowers/plans/2026-11-03-phase{1,2,3,4,5}-rulings.md`、`2026-11-04-phase{6,7}-rulings.md`）全部仍然有效。執行時的裁決寫進 `docs/superpowers/plans/2026-11-05-phase8-rulings.md`。

## 本階段範圍

**做：**
- 範本格式、結構驗證、內建「訂單」範本、啟動時載入。
- `GET /templates`、`POST /templates/{key}/instantiate`、`GET /modules/{key}/export-template`、`POST /templates/import`。
- 欄位設定「預設今天」（日期欄位）與「預設帶表頭欄位」（明細模組的欄位），前後端都套用，設計器可以設定。
- 前端：模組清單的「從範本建立」對話框、「匯入範本」；模組設計器的「匯出範本」。

**不做：**
- 範本含資料：只有 metadata（spec §13.1）。
- 範本的版本升級、套用到已存在的模組（合併差異）。
- 刪除、覆蓋已匯入的範本：同代碼再匯入回 409。
- 匯出、套用欄位層權限（隱藏、唯讀）、選項範圍、個人權限：範本只帶「群組 × 動作軸 × 資料範圍」的建議（決策 6）。
- 啟動時自動建出範本模組（spec §13.1 明定不做）。

## 已定決策（我的判斷，spec 沒說死的部分）

1. **範本格式**（`formatVersion: 1`）：

```json
{
  "formatVersion": 1,
  "key": "order",
  "name": { "zh-TW": "訂單", "en-US": "Sales order" },
  "description": { "zh-TW": "表頭＋明細，含合計、稅額、狀態控管" },
  "modules": [
    {
      "code": "order", "key": "order", "name": { "zh-TW": "訂單" },
      "icon": "receipt_long", "section": "業務",
      "kind": "standalone", "hostEntityKey": null,
      "titleField": "no",
      "blocks": [
        { "code": "main", "label": { "zh-TW": "基本資料" }, "collapsed": false,
          "fields": [
            { "code": "no", "type": "autonumber", "label": { "zh-TW": "訂單編號" },
              "config": { "format": "SO-{yyyyMM}-{0000}", "showInList": true } },
            { "code": "status", "type": "enum", "label": { "zh-TW": "狀態" },
              "options": [ { "code": "draft", "label": { "zh-TW": "草稿" }, "color": null } ],
              "config": { "defaultValue": "opt_draft" } }
          ] }
      ],
      "rules": [ { "kind": "readOnly", "targets": ["*"], "active": true, "message": null, "options": [],
                   "condition": { "field": "status", "op": "in", "value": ["opt_closed", "opt_cancel"] } } ],
      "views": [ { "name": { "zh-TW": "全部訂單" }, "columns": ["no", "status"], "filter": null,
                   "sort": [ { "field": "no", "order": "desc" } ], "isDefault": true } ],
      "permissions": [ { "group": "sales", "scope": "own",
                         "privileges": { "read": true, "add": true, "edit": true, "delete": false,
                                         "printExport": false, "pricing": false, "approve": false } } ]
    }
  ]
}
```

   - **代號**：`key`（範本）與模組 `code`、`key` 照模組代碼規則（`ValidModuleKey`）。區塊、欄位代號 `^[a-z][a-z0-9_]{0,39}$`，在同一個模組內唯一；選項代號 `^[a-z][a-z0-9_-]{0,39}$`，在同一個模組內唯一（不只同一個欄位）。
   - **欄位的 `config`** 就是 `FieldConfig` 的 JSON（camelCase），只有這幾處把 id 換成代號：
     - `formula`：`{代號}` 取代 `{欄位 id}`，只能引用同模組的欄位。
     - `defaultValue`、條件的 `value`：選項寫 `opt_<選項代號>`。選項代號以英文開頭，不會和 `opt_<數字>` 混淆。
     - `refTarget`：`kind` 是 `module` 時，`key` 先找範本內的模組代號，找不到才當作目標庫裡既有模組的鍵；`entity` 照舊。
     - `rollup`：`{ "sourceField": "<模組代號>.<欄位代號>", "function": "sum", "aggregateField": "<模組代號>.<欄位代號>", "condition": … }`，取代 `sourceFieldId`、`aggregateFieldId`；`condition` 裡的欄位是來源模組的代號。
     - `parentDefaultField`：`"<表頭模組代號>.<欄位代號>"`，取代 `parentDefaultFieldId`（決策 2）。
   - **規則**：`targets` 是 `field:<代號>`、`block:<代號>` 或 `*`；`options`（選項限制用）是選項代號清單，取代 `optionIds`；條件裡的 `field`、`fieldRef` 是欄位代號。
   - **清單檢視**：只有共用檢視；`columns`、`sort[].field`、`filter` 裡的欄位都是代號。
   - **權限**：`group` 是群組的 `Key`（如 `sales`）。明細模組不能帶 `permissions`（它沿用表頭，第 6 階段）。
2. **兩種新的預設值**（`FieldConfig` 加兩個屬性）：
   - `DefaultToday: bool`：只能用在 `date`，不能和 `DefaultValue` 同時設定。新增記錄沒提供值時，後端帶 `IBusinessTime.Today`；前端新增表單帶瀏覽器本地的今天（時區差異同 spec §15 第 26 條）。
   - `ParentDefaultFieldId: long?`：只能用在明細模組（有 `relation = detail` 參照的模組）的欄位，指向表頭模組的一個啟用中欄位，兩邊型別相同，型別限 `text`、`textarea`、`integer`、`decimal`、`currency`、`percent`、`date`、`datetime`、`time`、`boolean`、`email`、`phone`、`url`、`user`（選項各模組不同，所以不含 `enum`、`multienum`）。新增明細列沒提供值時，帶「這次存檔後」表頭的值；前端新增一列時帶表單上表頭目前的值。只在新增列時帶，之後改表頭不會回頭改明細。
   - 錯誤訊息（鍵 `Config.DefaultToday`／`Config.ParentDefaultFieldId`）：`只有日期欄位可以預設今天`、`預設今天與預設值只能擇一`、`只有明細模組的欄位可以帶表頭的值`、`表頭欄位不存在、未啟用或型別不同`。
   - 和 `DefaultValue` 一樣，`ParentDefaultFieldId` 指向的表頭欄位被停用時，視同沒有設定。
3. **從範本建立** `POST /templates/{key}/instantiate`：
   - Body：`{ "modules": [ { "code": "order", "key": "order", "name": { "zh-TW": "訂單" } } ], "excludeFields": ["order.cancelReason"] }`。`modules` 可以只列要改的模組；沒列的用範本的 `key`、`name`。
   - 權限：設計器 `Add`（`IDesignerRequest`）。
   - **排除欄位**：`excludeFields` 是 `<模組代號>.<欄位代號>`。
     - 鎖定的欄位不能排除：模組的 `titleField`、`relation = detail` 的參照欄位。回 400，鍵 `excludeFields`，訊息 `「{欄位名稱}」是必要欄位，不能排除`。
     - 被保留的欄位依賴被排除的欄位時回 400，鍵 `excludeFields`，訊息 `「{保留的欄位}」引用了「{排除的欄位}」，要一起排除`。「依賴」：公式引用、彙總的來源與彙總欄位、彙總條件的欄位、`parentDefaultField`。
     - 規則只要條件、目標、選項任何一處引用被排除的欄位（或它的選項、它所在的區塊不受影響），整條規則不建；檢視去掉被排除的欄、排序，篩選引用被排除的欄位時篩選改成 null。
   - **重播順序**（每一步都是既有指令，錯誤原樣帶回，見下）：
     1. 每個模組 `CreateModule`（`CreateTitleField: false`，見 Task 4），依範本順序。
     2. 每個模組：`CreateModule` 建的「基本資料」區塊用 `UpdateBlock` 改成範本第一個區塊，其餘區塊 `AddBlock`，`collapsed` 為 true 的再 `UpdateBlock`。
     3. 非計算欄位（不是 `formula`、`rollup`），依範本順序 `AddField`；送出時拿掉 `defaultValue`（下拉欄位要先有選項）與 `parentDefaultField`（表頭欄位要先存在）。下拉、多選接著逐一 `AddOption`。
     4. 計算欄位（`formula`、`rollup`）依跨模組的依賴拓撲排序後逐一 `AddField`；同層維持範本順序。
     5. 有 `defaultValue` 或 `parentDefaultField` 的欄位 `UpdateField`（帶完整設定）。
     6. 每個區塊 `ReorderFields`，恢復範本的欄位順序（計算欄位是後加的）。
     7. 每個模組 `UpdateModule`（`titleField`、`icon`、`section`、名稱）。
     8. 每個模組 `SaveRules`（全部規則一次送）。
     9. 每個檢視 `CreateView`（`Shared: true`）。
     10. 每個帶 `permissions` 的模組 `SaveModulePermissions`（`users`、`fields`、`options` 為空）。目標庫沒有的群組鍵略過，加一則警告 `群組「{key}」不存在，已略過它的權限建議`。
   - **錯誤**：內層指令失敗時，外層回同一種狀態（400／404／409），400 的錯誤鍵加前綴：欄位類 `{模組代號}.{欄位代號}.{原鍵}`，模組類 `{模組代號}.{原鍵}`，規則 `{模組代號}.rules.{原鍵}`，檢視 `{模組代號}.views.{原鍵}`；404／409 的訊息前面加 `模組「{模組代號}」：`。例：代碼已被用掉 → 409 `模組「order」：模組代碼已存在`。
   - **回應** 201：`{ "modules": [ { "code": "order", "key": "order", "id": 12 } ], "warnings": [] }`。模組都是草稿；啟用由管理員在設計器做（表頭、明細各自啟用）。
4. **巢狀交易**：`EfTransactionManager.BeginAsync` 在 `db.Database.CurrentTransaction` 已存在時回一個不做事的 handle（Commit、Rollback 都不動作）。外層失敗時由外層回滾；內層指令照常回 `ServiceResult`，外層看到失敗就停止重播並回傳。其他請求的行為不變（它們本來就沒有外層交易）。
5. **列出範本** `GET /templates`：權限設計器 `Read`。回內建與匯入的範本，內建在前、各自依 `key` 排序：

```
TemplateSummary(string Key, I18nText Name, I18nText? Description, string Source /* builtIn | imported */,
                List<TemplateModuleSummary> Modules)
TemplateModuleSummary(string Code, string Key, I18nText Name, string? DetailOf /* 表頭模組代號 */,
                      List<TemplateFieldSummary> Fields)
TemplateFieldSummary(string Code, I18nText Label, string Type, bool Locked, List<string> DependsOn /* <模組代號>.<欄位代號> */)
```
   前端靠 `Locked`、`DependsOn` 做勾選連動，不必自己解析範本。
6. **匯出範本** `GET /modules/{key}/export-template`：權限設計器 `Read`。回檔案 `{key}.template.json`（`application/json`，縮排）。
   - 內容：這個模組，加上以它為表頭的所有明細模組（`ListDetailModulesAsync`）；明細模組自己匯出時只有它自己，明細參照指向外部模組鍵。範本 `key` 等於模組鍵，`name`、`description` 取模組名稱、null。
   - 代號：模組代號＝模組鍵；欄位代號＝`Field.Key`（`f_123`）；區塊 `b_<Id>`；選項 `o_<Id>`。
   - 只匯出啟用中的欄位與選項、共用檢視、群組權限（不含個人、欄位層、選項範圍）。規則全部匯出（含停用的，保留 `active`）。
   - **無法表達的引用**：彙總的來源欄位在套件外的模組、`parentDefaultFieldId` 指向套件外的欄位、引用停用欄位的規則與檢視欄 —— 這些欄位（及依賴它們的公式、規則）不匯出，在套件頂層 `warnings` 加一則，例如 `「總工時」彙總了範本外的模組，未匯出`。`warnings` 匯入時忽略。
   - 匯出的套件必須能通過決策 7 的結構驗證（Task 5 測試）。
7. **結構驗證**（`TemplateValidator.Validate(TemplatePackage) → IReadOnlyList<string>`，純函式，啟動與匯入共用）。檢查：
   - `formatVersion == 1`；範本 `key`、模組 `code`、`key` 格式；代號格式與唯一性（決策 1）；至少一個模組，每個模組至少一個區塊。
   - 欄位 `type` 是已知型別；`titleField` 存在且是 `text` 或 `autonumber`。
   - 所有代號引用都解析得到：公式 `{…}`、`opt_…`（選項屬於該條件的欄位）、`refTarget`（module 種類找不到範本內代號時不算錯，留給套用時檢查）、`rollup` 兩個欄位、`parentDefaultField`、規則目標與選項、檢視欄與排序。
   - 計算欄位之間沒有循環依賴。
   - 明細模組（有 `relation = detail` 參照的模組）沒有 `permissions`。
   - 錯誤訊息格式：`{模組代號}.{欄位代號}: {原因}`（無欄位時省略），例如 `order.tax: 公式引用了不存在的欄位 rate`。
   - 這只檢查結構；型別設定是否合法（例如自動編號格式）要等套用時由既有指令判斷。內建範本另有「每個都能成功套用並啟用」的測試（Task 4）。
8. **匯入範本** `POST /templates/import`：權限設計器 `Add`。Body 是範本 JSON（`application/json`，上限 1 MB）。
   - 結構驗證失敗回 400，鍵 `template`，值是 `TemplateValidator` 的訊息清單。
   - `key` 與內建或已匯入的範本相同回 409 `範本代碼已存在`。
   - 成功回 201 `TemplateSummary`，存進 `Dynamic_ModuleTemplate`：`Id`（long, identity）、`Key`（nvarchar(40), unique）、`Json`（nvarchar(max)，原文）、稽核欄位（`AuditableEntity`）。Migration `Phase8Templates`。
9. **內建「訂單」範本**（`poc/backend/templates/order.json`，照 spec §13.1）：
   - **訂單**（code `order`，key `order`，icon `receipt_long`，分區 `業務`，標題 `no`）：
     - 區塊「基本資料」：`no` 訂單編號 autonumber `SO-{yyyyMM}-{0000}`（showInList、isSortable）；`customer` 客戶 reference entity `sales.customer`（required、showInList、isFilterable）；`orderDate` 訂單日期 date（defaultToday、required、showInList、isFilterable、isSortable）；`dueDate` 交期 date（showInList、isFilterable、isSortable）；`status` 狀態 enum：`draft` 草稿、`confirmed` 已確認、`shipping` 出貨中、`closed` 已結案、`cancel` 取消，預設 `opt_draft`（required、showInList、isFilterable）；`salesRep` 業務員 user（showInList、isFilterable）；`currency` 幣別 enum：`twd` TWD、`usd` USD、`jpy` JPY，預設 `opt_twd`。
     - 區塊「金額」：`subtotal` 未稅合計 rollup sum `line.amount`（來源 `line.order`）；`taxRate` 稅率 percent scale 0 預設 5；`tax` 稅額 formula `ROUND({subtotal} * {taxRate}, 0)` currency scale 0；`total` 含稅總額 formula `{subtotal} + {tax}` currency scale 0（showInList）；`lineCount` 明細筆數 rollup count（來源 `line.order`）。百分比在公式裡是小數（第 6 階段 R19），所以不除以 100。
     - 區塊「備註」：`remark` 備註 textarea；`cancelReason` 取消原因 textarea。
     - 規則：
       1. validation，目標 `field:dueDate`，條件 `any: [dueDate isEmpty, dueDate gte {fieldRef: orderDate}]`，訊息 `交期不得早於訂單日期`。
       2. visibility，目標 `field:cancelReason`，條件 `status eq opt_cancel`。
       3. required，目標 `field:cancelReason`，條件 `status eq opt_cancel`。
       4. readOnly，目標 `*`，條件 `status in [opt_closed, opt_cancel]`。
     - 檢視「全部訂單」（預設）：欄 `no`、`customer`、`orderDate`、`dueDate`、`status`、`total`；排序 `orderDate desc`。
     - 權限建議：`sales` 讀取、新增、修改，資料範圍本人；`sales-manager` 讀取、新增、修改、刪除、列印匯出、金額，資料範圍全部。業務沒有金額軸，所以看不到單價、金額、合計（spec「看不到單價與金額的群組：不給 Pricing 軸」）。
   - **訂單明細**（code `line`，key `order-line`，標題 `itemName`）：一個區塊「明細」：`order` 訂單 reference module `order` relation detail；`seq` 項次 autonumber `{0}` scope parent；`itemNo` 品號 text；`itemName` 品名 text required；`qty` 數量 decimal scale 2 required；`unit` 單位 enum：`pcs` 個、`set` 組、`kg` 公斤；`price` 單價 currency scale 0；`amount` 金額 formula `{qty} * {price}` currency scale 0；`dueDate` 交期 date，`parentDefaultField: "order.dueDate"`。
   - en-US 標籤都要有；th、vi 不放（`I18nText.Resolve` 會退回）。

## Global Constraints

- 後端分層照 spec §11.1：Domain 不依賴 Application；寫入一律 `ITransactionalRequest`、回 `ServiceResult`；時間一律 `IBusinessTime`。
- 錯誤沿用 `ServiceResult` 的 400／403／404／409 映射；欄位錯誤回 `ValidationResponseModel`。
- JSON 回應省略 null（第 5 階段 R7）。範本檔的讀寫用 `JsonSerializerDefaults.Web`（camelCase），列舉用 camelCase 字串、`FieldType` 用小寫（同 `Program.cs` 的 converter 設定）。
- 固定介面文字放 i18n，四個語系（zh-TW、en-US、th、vi）都要有鍵；th、vi 可以先放英文。
- 下拉選單照第 6 階段 R12 用 `useSelectFilter`。
- 不加套件。
- 測試：後端 `dotnet test poc/backend`；前端 `cd poc/frontend && bunx vitest run && bunx vue-tsc --noEmit`。每個 task 結束時全部通過。
- Commit 只 add 本 task 的檔案（不用 `git add -A`／`.`），訊息結尾 `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`。

## Review Focus

1. **半途失敗留下殘骸**：套用到第 7 步（例如規則）才失敗，或第二個模組的代碼和既有模組重複。預期整批不留任何模組、欄位、ProgramCode —— Task 4 的 `Instantiate_FailureMidway_LeavesNothing`。
2. **同一範本套用兩次**：第二次把代碼改成 `order2`／`order2-line`，明細參照、彙總、表頭預設都要指向第二組模組，不是第一組 —— Task 4 的 `Instantiate_Twice_WithNewKeys_IsIndependent`。
3. **排除欄位後的連鎖**：排除「稅率」卻保留「稅額」要被擋；排除「取消原因」時兩條相關規則消失、其他規則還在 —— Task 4 測試。
4. **匯出再套用**：把第 6 階段式的訂單模組匯出，匯入後以新代碼套用，欄位、選項、規則、檢視、權限都等價（id 不同） —— Task 5 的往返測試。
5. **設計器重新存檔弄丟新設定**：在設計器打開一個「預設帶表頭欄位」的欄位、只改名稱就存檔，`parentDefaultFieldId` 和 `defaultToday` 要保留 —— Task 2 的 `FieldEditorDialog` 測試。

---

### Task 1: 兩種新的預設值（後端）

**Files:**
- Modify: `poc/backend/src/DynamicModules.Domain/ValueObjects/FieldConfig.cs`（加 `DefaultToday`、`ParentDefaultFieldId`）
- Modify: `poc/backend/src/DynamicModules.Application/Dynamic/FieldService/Data/FieldRules.cs`（`CheckDynamicDefaults`）
- Modify: `AddFieldHandler.cs`、`UpdateFieldHandler.cs`（呼叫檢查）
- Modify: `poc/backend/src/DynamicModules.Application/Dynamic/RecordService/RecordValueProcessor.cs`、`DetailWriter.cs`（套用）
- Test: `poc/backend/tests/DynamicModules.ApplicationTests/Dynamic/DynamicDefaultTests.cs`

**Interfaces:**
- Produces:
  - `FieldConfig.DefaultToday`（`bool`）、`FieldConfig.ParentDefaultFieldId`（`long?`），JSON `defaultToday`、`parentDefaultFieldId`。schema 的欄位 `config` 原樣帶出（確認 `SchemaResponses` 有序列化這兩個屬性；沒有就補）。
  - `FieldRules.CheckDynamicDefaults(Module module, Field field, FieldConfig config, Module? parent) → ServiceResult?`（`parent` 是明細模組的表頭，非明細模組傳 null）。
  - `RecordValueProcessor` 的處理入口多一個可選參數 `IReadOnlyDictionary<string, JsonElement?>? parentValues`（表頭的 `Field.Key → 值`），`DetailWriter` 新增列時傳入表頭這次存檔後的值。

- [ ] **Step 1: 寫失敗的測試**

```csharp
[Fact] DefaultToday_FillsBusinessToday_OnCreate          // 不給值 → IBusinessTime.Today 的 "yyyy-MM-dd"
[Fact] DefaultToday_ProvidedValueWins
[Fact] DefaultToday_NotOnUpdate                          // 修改時清空 → 保持空白
[Fact] DefaultToday_OnlyDate                             // text 欄位設 defaultToday → 400 Config.DefaultToday「只有日期欄位可以預設今天」
[Fact] DefaultToday_WithDefaultValue_Rejected            // 400「預設今天與預設值只能擇一」
[Fact] ParentDefault_NewDetailRow_CopiesHeaderValue      // 表頭交期 2026-12-01，新增列不給交期 → 2026-12-01
[Fact] ParentDefault_UsesHeaderValueOfThisSave           // 同一次存檔把表頭交期改成 2026-12-10 並新增一列 → 2026-12-10
[Fact] ParentDefault_ExistingRowUntouched                // 改表頭交期，舊列不變
[Fact] ParentDefault_NotDetailModule_Rejected            // 400 Config.ParentDefaultFieldId「只有明細模組的欄位可以帶表頭的值」
[Fact] ParentDefault_TypeMismatch_Rejected               // 明細 date 指向表頭 text → 400「表頭欄位不存在、未啟用或型別不同」
[Fact] ParentDefault_EnumNotAllowed                      // 明細 enum 指向表頭 enum → 同上訊息
[Fact] ParentDefault_HeaderFieldInactive_Ignored         // 表頭欄位停用後新增列 → 空白，不報錯
```

- [ ] **Step 2: 跑測試確認失敗** — Run: `dotnet test poc/backend --filter FullyQualifiedName~DynamicDefaultTests`，Expected: 編譯失敗。
- [ ] **Step 3: 實作**（決策 2）。套用點是 `RecordValueProcessor` 現有「沒提供值且是新增時套 `DefaultValue`」那段：依序看 `DefaultValue`、`DefaultToday`、`ParentDefaultFieldId`（三者互斥，只會有一個）。`UpdateField` 把設定改掉時不回頭改既有記錄。
- [ ] **Step 4: 跑全部後端測試** — Run: `dotnet test poc/backend`，Expected: 全部 PASS。
- [ ] **Step 5: Commit** `feat: 欄位可預設今天、明細欄位可帶表頭的值`

### Task 2: 兩種新的預設值（前端）

**Files:**
- Modify: `poc/frontend/src/domain/dynamic/types.ts`（`FieldConfig` 加 `defaultToday?: boolean`、`parentDefaultFieldId?: number`）
- Modify: `poc/frontend/src/presentation/dynamic/field-type-registry.ts`（`defaultValueOf`）
- Modify: `poc/frontend/src/presentation/dynamic/details/detail-rows.ts`（新增列帶表頭值）與呼叫它的明細表格元件
- Modify: `poc/frontend/src/presentation/dynamic/designer/FieldEditorDialog.vue`（兩個控制項）
- Modify: 四個 i18n 檔
- Test: `field-type-registry.vitest.spec.ts`、`details/*.vitest.spec.ts`、`FieldEditorDialog.vitest.spec.ts`

**Interfaces:**
- Consumes: Task 1 的 `defaultToday`、`parentDefaultFieldId`。
- Produces: `defaultValueOf(f: SchemaField, today?: string): unknown`（`today` 預設本地日期 `yyyy-MM-dd`，測試注入）；`newRow(detail, headerValues?: Record<string, unknown>, headerFields?: SchemaField[])` —— 依 `parentDefaultFieldId` 找表頭欄位的 key 取值（實際參數形狀由實作者照現有 `newRow` 決定，在 report 寫明）。

- [ ] **Step 1: 寫失敗的測試**

```ts
// defaultValueOf：date + defaultToday、today='2026-11-05' → '2026-11-05'；沒有 defaultToday → config.defaultValue
// newRow：明細交期 parentDefaultFieldId = 表頭交期 id，表頭值 '2026-12-01' → 新列交期 '2026-12-01'；表頭沒值 → null
// FieldEditorDialog：date 欄位顯示「預設今天」開關，打開後預設值輸入停用；送出 config.defaultToday === true
// FieldEditorDialog：明細模組的欄位顯示「預設帶表頭欄位」下拉，只列同型別、啟用中的表頭欄位；非明細模組不顯示
// FieldEditorDialog：編輯既有欄位（config 有 defaultToday 或 parentDefaultFieldId），只改名稱就送出 → 兩個值原樣保留（Review Focus 5）
```

- [ ] **Step 2: 跑測試確認失敗** — Run: `cd poc/frontend && bunx vitest run src/presentation/dynamic`，Expected: FAIL。
- [ ] **Step 3: 實作**。文字（zh-TW）：`designer.defaultToday`＝`預設今天`、`designer.parentDefault`＝`預設帶表頭欄位`、`designer.parentDefaultHint`＝`新增明細列時帶入表頭這個欄位目前的值`。表頭欄位清單從設計器已載入的表頭模組取得（`ModuleEditorPage` 已知道明細模組的表頭，`designer.detailOf`）；取不到時下拉為空。
- [ ] **Step 4: 跑前端測試與 vue-tsc** — Run: `cd poc/frontend && bunx vitest run && bunx vue-tsc --noEmit`，Expected: PASS。
- [ ] **Step 5: Commit** `feat(frontend): 預設今天、明細帶表頭欄位的表單與設計器`

### Task 3: 範本格式、結構驗證、內建訂單範本、啟動載入

**Files:**
- Create: `poc/backend/src/DynamicModules.Application/Dynamic/TemplateService/Data/TemplatePackage.cs`（格式的 record 型別）
- Create: `poc/backend/src/DynamicModules.Application/Dynamic/TemplateService/TemplateValidator.cs`
- Create: `poc/backend/src/DynamicModules.Application/Dynamic/TemplateService/TemplateGraph.cs`（依賴、鎖定、拓撲排序；Task 4、5 共用）
- Create: `poc/backend/src/DynamicModules.Application/Dynamic/TemplateService/TemplateCatalog.cs`（singleton，內建範本）
- Create: `poc/backend/templates/order.json`（決策 9）
- Modify: `poc/backend/src/DynamicModules.WebAPI/DynamicModules.WebAPI.csproj`（`..\..\templates\*.json` 以 `templates\` 複製到輸出目錄）
- Modify: `poc/backend/src/DynamicModules.WebAPI/Program.cs`（啟動時載入；失敗丟例外結束程序）
- Modify: Application `DependencyInjection.cs`
- Test: `poc/backend/tests/DynamicModules.ApplicationTests/Dynamic/TemplateValidatorTests.cs`、`TemplateGraphTests.cs`

**Interfaces:**
- Produces:
  - `TemplatePackage(int FormatVersion, string Key, I18nText Name, I18nText? Description, List<TemplateModule> Modules, List<string>? Warnings)`；`TemplateModule(string Code, string Key, I18nText Name, string? Icon, string? Section, ModuleKind Kind, string? HostEntityKey, string TitleField, List<TemplateBlock> Blocks, List<TemplateRule> Rules, List<TemplateView> Views, List<TemplatePermission> Permissions)`；`TemplateBlock(string Code, I18nText Label, bool Collapsed, List<TemplateField> Fields)`；`TemplateField(string Code, FieldType Type, I18nText Label, JsonElement Config, List<TemplateOption> Options)`；`TemplateOption(string Code, I18nText Label, string? Color)`；`TemplateRule(RuleKind Kind, List<string> Targets, JsonElement Condition, List<string> Options, I18nText? Message, bool Active)`；`TemplateView(I18nText Name, List<string> Columns, JsonElement? Filter, List<TemplateSort> Sort, bool IsDefault)`；`TemplateSort(string Field, string Order)`；`TemplatePermission(string Group, PrivilegesDto Privileges, DataScope? Scope)`。`Config` 保留 `JsonElement`，代號替換在套用時做。集合屬性缺漏時視為空清單。
  - `TemplateJson.Options`（`JsonSerializerOptions`，見 Global Constraints）、`TemplateJson.Parse(string json) → (TemplatePackage? Package, string? Error)`。
  - `TemplateValidator.Validate(TemplatePackage p) → IReadOnlyList<string>`（決策 7）。
  - `TemplateGraph.Build(TemplatePackage p)`：`IReadOnlyList<string> DependsOn(string qualifiedField)`、`bool IsLocked(string qualifiedField)`、`IReadOnlyList<string> ComputedOrder()`（拓撲排序後的計算欄位，限定名 `<模組代號>.<欄位代號>`）、`bool HasCycle`。
  - `TemplateCatalog`：`IReadOnlyList<TemplatePackage> BuiltIns`；`static TemplateCatalog Load(string directory)` —— 任何檔案解析或驗證失敗丟 `InvalidOperationException`，訊息含檔名與全部錯誤；範本 `key` 重複也是錯。

- [ ] **Step 1: 寫失敗的測試**

```csharp
// TemplateValidatorTests（用一個最小的合法套件當基底，每個測試改壞一處）
[Fact] Valid_MinimalPackage_NoErrors
[Fact] BuiltIn_Order_IsValid                     // 讀 templates/order.json → 0 個錯誤
[Fact] FormatVersion_Not1
[Fact] DuplicateFieldCode_InModule
[Fact] DuplicateOptionCode_AcrossFieldsInModule  // 兩個下拉各有 draft → 錯
[Fact] Formula_UnknownCode                       // "order.tax: 公式引用了不存在的欄位 rate"
[Fact] Condition_UnknownOptionCode               // value "opt_nope"
[Fact] Rollup_UnknownSourceField
[Fact] ParentDefault_Unknown
[Fact] RuleTarget_UnknownBlock
[Fact] View_UnknownColumn
[Fact] TitleField_NotTextOrAutonumber
[Fact] ComputedCycle                             // a = {b} + 1、b = {a} + 1
[Fact] DetailModule_WithPermissions
[Fact] RefTarget_ExternalModuleKey_IsNotAnError  // refTarget module "customer-x" 不在範本內 → 不算錯
// TemplateGraphTests（用 order.json）
[Fact] Order_ComputedOrder_AmountBeforeSubtotalBeforeTaxBeforeTotal  // line.amount < order.subtotal < order.tax < order.total；order.lineCount 在 order.total 前後都可
[Fact] Order_Locked                              // order.no、line.order、line.itemName 鎖定；order.remark 不鎖定
[Fact] Order_DependsOn                           // order.tax → [order.subtotal, order.taxRate]；order.subtotal → [line.order, line.amount]；line.dueDate → [order.dueDate]
// TemplateCatalog
[Fact] Load_InvalidFile_ThrowsWithFileName       // 暫存目錄放一個壞檔
```

- [ ] **Step 2: 跑測試確認失敗** — Run: `dotnet test poc/backend --filter "FullyQualifiedName~TemplateValidatorTests|FullyQualifiedName~TemplateGraphTests"`，Expected: 編譯失敗。
- [ ] **Step 3: 實作型別、`TemplateJson`、`TemplateGraph`、`TemplateValidator`**。條件 JSON 的代號引用要遞迴走 `all`／`any`；`opt_` 的判斷用 `^opt_[a-z]`。
- [ ] **Step 4: 寫 `order.json`**（決策 9 全部內容），跑 `BuiltIn_Order_IsValid`。
- [ ] **Step 5: `TemplateCatalog` 與啟動載入**：`Program.cs` 在 `InitializeDatabaseAsync` 之前 `TemplateCatalog.Load(Path.Combine(AppContext.BaseDirectory, "templates"))`，註冊成 singleton。手動確認：把 `order.json` 改壞後 `dotnet run` 會在啟動時結束並印出錯誤，改回後正常（寫進 report）。
- [ ] **Step 6: 跑全部後端測試** — Run: `dotnet test poc/backend`，Expected: PASS。
- [ ] **Step 7: Commit** `feat: 模組範本格式、結構驗證與內建訂單範本`

### Task 4: 從範本建立（API）

**Files:**
- Modify: `poc/backend/src/DynamicModules.Persistence/EfTransactionManager.cs`（決策 4）
- Modify: `CreateModuleRequest.cs`、`CreateModuleHandler.cs`（加 `bool CreateTitleField = true`；`false` 時不建「名稱」欄位、`TitleFieldId` 為 null。WebAPI 的建立模組端點不開放這個參數）
- Create: `poc/backend/src/DynamicModules.Application/Dynamic/TemplateService/TemplateInstantiator.cs`
- Create: `.../TemplateService/Commands/InstantiateTemplate/InstantiateTemplateRequest.cs`、`InstantiateTemplateHandler.cs`
- Create: `.../TemplateService/Queries/GetTemplates/GetTemplatesRequest.cs`、`GetTemplatesHandler.cs`
- Create: `.../TemplateService/Data/TemplateResponses.cs`（決策 3、5 的回應型別）
- Create: `poc/backend/src/DynamicModules.WebAPI/Controllers/v3/Dynamic/TemplatesController.cs`
- Test: `poc/backend/tests/DynamicModules.WebAPITests/Dynamic/TemplateInstantiateTests.cs`、`poc/backend/tests/DynamicModules.ApplicationTests/Persistence/NestedTransactionTests.cs`

**Interfaces:**
- Consumes: Task 3 的 `TemplatePackage`、`TemplateGraph`、`TemplateCatalog`。
- Produces:
  - `InstantiateTemplateRequest(string TemplateKey, List<InstantiateModuleInput> Modules, List<string> ExcludeFields) : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest`（`Add`）；`InstantiateModuleInput(string Code, string? Key, I18nText? Name)`。
  - `TemplateInstantiator.InstantiateAsync(TemplatePackage p, IReadOnlyDictionary<string, InstantiateModuleInput> overrides, IReadOnlySet<string> exclude, CancellationToken ct) → ServiceResult`（用 `ISender` 重播，決策 3；Task 5 的往返測試也用它）。
  - `ITemplateSource.FindAsync(string key, CancellationToken ct) → TemplatePackage?`、`ListAsync(CancellationToken ct) → IReadOnlyList<(TemplatePackage Package, string Source)>`：本 task 只回內建（`TemplateCatalog`）；Task 6 加上匯入的。
  - 端點：`GET api/v3/dynamic/templates`、`POST api/v3/dynamic/templates/{key}/instantiate`。範本不存在回 404 `範本不存在`。

- [ ] **Step 1: 寫失敗的測試**

```csharp
// NestedTransactionTests
[Fact] Nested_InnerHandleIsNoop_OuterRollbackUndoesAll   // 外層 Begin → 內層 Begin/Commit → 外層 Rollback → 資料不在
// TemplateInstantiateTests（admin）
[Fact] GetTemplates_ListsOrder_WithLockedAndDependsOn
[Fact] Instantiate_Order_CreatesDraftModules              // 201；order、order-line 兩個草稿模組；欄位數、區塊名稱、欄位順序與範本相同；
                                                          // order.titleFieldId 是「訂單編號」；line 的「訂單」是 detail；subtotal 的 rollup 指向 order-line 的欄位 id；
                                                          // 規則 4 條；預設檢視 1 個；sales、sales-manager 權限照決策 9；warnings 空
[Fact] Instantiate_Order_ThenActivateBoth_Succeeds        // 依序 activate order、order-line → 200
[Fact] Instantiate_Twice_WithNewKeys_IsIndependent        // 第二次 order2／order2-line：明細參照、rollup、parentDefault 都指向第二組
[Fact] Instantiate_KeyTaken_409_LeavesNothing             // 先手動建 order-line → 409「模組「line」：模組代碼已存在」；order 也沒有被建出、ProgramCode 沒被用掉
[Fact] Instantiate_FailureMidway_LeavesNothing            // 用測試專用範本（規則的條件值不合法）→ 400 鍵 "x.rules.…"；模組數量不變
[Fact] Instantiate_Exclude_CancelReason_DropsTwoRules     // 規則剩 2 條（交期驗證、全單唯讀）
[Fact] Instantiate_Exclude_TaxRateButKeepTax_400          // 鍵 excludeFields，「稅額」引用了「稅率」，要一起排除
[Fact] Instantiate_Exclude_Locked_400                     // 排除 order.no →「訂單編號」是必要欄位，不能排除
[Fact] Instantiate_UnknownGroup_Warns                     // 測試範本的 group "nope" → 201，warnings 一則
[Fact] Instantiate_NonDesigner_403                        // sales01
[Fact] Instantiate_Rename_UsesGivenNameAndKey
```

- [ ] **Step 2: 跑測試確認失敗** — Run: `dotnet test poc/backend --filter "FullyQualifiedName~TemplateInstantiateTests|FullyQualifiedName~NestedTransactionTests"`，Expected: 編譯失敗。
- [ ] **Step 3: 巢狀交易與 `CreateTitleField`**，跑全部後端測試確認既有行為不變。
- [ ] **Step 4: 實作 `TemplateInstantiator`**（決策 3 的十個步驟）。代號替換集中在一個私有的 `IdMap`（模組代號→`Module`，限定欄位名→`Field.Id`，選項代號→`FieldOption.Id`，區塊代號→`Block.Id`）；欄位 `config` 的替換在 `JsonElement` 層做完再反序列化成 `FieldConfig`。內層指令的回應從 `ServiceResult.Data` 取（`ModuleResponse`、`FieldResponse` 等現有型別）。
- [ ] **Step 5: handler、`GetTemplates`、controller**。
- [ ] **Step 6: 跑全部後端測試** — Run: `dotnet test poc/backend`，Expected: PASS。
- [ ] **Step 7: Commit** `feat: 從範本建立模組（重播設計器指令、單一交易）`

### Task 5: 匯出範本

**Files:**
- Create: `poc/backend/src/DynamicModules.Application/Dynamic/TemplateService/TemplateExporter.cs`
- Create: `.../TemplateService/Queries/ExportTemplate/ExportTemplateRequest.cs`、`ExportTemplateHandler.cs`
- Modify: `TemplatesController.cs`（`GET api/v3/dynamic/modules/{key}/export-template`）
- Test: `poc/backend/tests/DynamicModules.WebAPITests/Dynamic/TemplateExportTests.cs`

**Interfaces:**
- Consumes: Task 3 的 `TemplatePackage`、`TemplateValidator`；Task 4 的 `TemplateInstantiator`。
- Produces: `TemplateExporter.ExportAsync(Module module, CancellationToken ct) → TemplatePackage`（決策 6）。回應沿用第 7 階段的 `ExportFile(byte[] Content, string ContentType, string FileName)`。

- [ ] **Step 1: 寫失敗的測試**

```csharp
[Fact] Export_OrderFromTemplate_RoundTrip       // 套用內建範本 → 匯出 order → 驗證 0 錯誤 → 以 order3／order3-line 套用 →
                                                // 兩組模組的欄位（型別、標籤、設定，id 換成對應的）、選項、規則、檢視、群組權限等價
[Fact] Export_HandBuiltModules_RoundTrip        // 用 API 手建第 6 階段式的訂單＋明細（含規則、公式、彙總、自動編號）→ 匯出 → 套用 → 等價
[Fact] Export_DetailModuleAlone_RefersExternalHeader // 匯出 order-line → 一個模組，明細參照 refTarget.key == "order"
[Fact] Export_RollupOverExternalSource_Omitted_WithWarning // 一般參照的彙總（第 6 階段的維修申請／紀錄）→ 不含「總工時」，warnings 一則
[Fact] Export_SkipsInactiveFieldsAndPersonalViews
[Fact] Export_ContentDisposition_FileName       // order.template.json
[Fact] Export_NonDesigner_403
```

  「等價」寫成一個測試輔助：把兩個模組各自轉成以代號表示的 `TemplatePackage`（就用 `TemplateExporter`，比較時忽略代號字串本身、改以欄位順序對齊），再比較 JSON。

- [ ] **Step 2: 跑測試確認失敗** — Run: `dotnet test poc/backend --filter FullyQualifiedName~TemplateExportTests`，Expected: 編譯失敗。
- [ ] **Step 3: 實作**（id → 代號的反向替換：公式、條件、規則目標、選項、彙總、`parentDefaultFieldId`、檢視；規則的 `RuleKind` 用 camelCase）。
- [ ] **Step 4: 跑全部後端測試** — Run: `dotnet test poc/backend`，Expected: PASS。
- [ ] **Step 5: Commit** `feat: 模組匯出成範本`

### Task 6: 匯入範本

**Files:**
- Create: `poc/backend/src/DynamicModules.Domain/Entities/Dynamic/ModuleTemplate.cs`
- Create: `poc/backend/src/DynamicModules.Domain/Data/Configurations/ModuleTemplateConfiguration.cs`
- Create: `poc/backend/src/DynamicModules.Domain/Repositories/Dynamic/IModuleTemplateRepository.cs`
- Create: `poc/backend/src/DynamicModules.Persistence/Repositories/Dynamic/ModuleTemplateRepository.cs`
- Create: migration `Phase8Templates`（照既有 migration 的做法）
- Create: `.../TemplateService/Commands/ImportTemplate/ImportTemplateRequest.cs`、`ImportTemplateHandler.cs`
- Modify: `ITemplateSource` 的實作（加上匯入的範本）、`TemplatesController.cs`（`POST api/v3/dynamic/templates/import`，`[RequestSizeLimit(1024 * 1024)]`，body 讀成字串交給 handler）、Persistence 與 Application 的 `DependencyInjection.cs`
- Test: `poc/backend/tests/DynamicModules.WebAPITests/Dynamic/TemplateImportTests.cs`

**Interfaces:**
- Consumes: Task 3 的 `TemplateJson`、`TemplateValidator`；Task 4 的 `ITemplateSource`。
- Produces: `IModuleTemplateRepository`：`Task<List<ModuleTemplate>> ListAsync(CancellationToken ct)`、`Task<ModuleTemplate?> GetByKeyAsync(string key, CancellationToken ct)`、`void Add(ModuleTemplate t)`。`ImportTemplateRequest(string Json) : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest`（`Add`）。

- [ ] **Step 1: 寫失敗的測試**

```csharp
[Fact] Import_ExportedPackage_ThenListedAndInstantiable // 匯出 → 改 key 為 "order-copy" → 匯入 201 → GET templates 有它（source imported，在內建之後）→ 以新代碼套用 201
[Fact] Import_InvalidJson_400                           // 鍵 template，「範本不是有效的 JSON」
[Fact] Import_StructuralErrors_400                      // 鍵 template，值含 TemplateValidator 的訊息
[Fact] Import_KeyOfBuiltIn_409                          // key "order" →「範本代碼已存在」
[Fact] Import_SameKeyTwice_409
[Fact] Import_NonDesigner_403
```

- [ ] **Step 2: 跑測試確認失敗** — Run: `dotnet test poc/backend --filter FullyQualifiedName~TemplateImportTests`，Expected: 編譯失敗。
- [ ] **Step 3: 實作實體、設定、repository、migration、handler**（決策 8）。
- [ ] **Step 4: 跑全部後端測試** — Run: `dotnet test poc/backend`，Expected: PASS。
- [ ] **Step 5: Commit** `feat: 匯入範本`

### Task 7: 前端：從範本建立、匯入與匯出範本

**Files:**
- Modify: `poc/frontend/src/services/api/dynamic-api.ts`（`getTemplates`、`instantiateTemplate`、`importTemplate`、`exportTemplate`）與 `dynamic-api.vitest.spec.ts`
- Modify: `poc/frontend/src/application/dynamic/queries/` 下對應的 query／mutation（照既有檔案的分法）
- Create: `poc/frontend/src/presentation/dynamic/designer/TemplateDialog.vue`、`template-selection.ts`（勾選連動，純函式）與測試
- Modify: `poc/frontend/src/pages/system/dynamic-modules/ModuleListPage.vue`（「從範本建立」「匯入範本」按鈕）與測試
- Modify: `poc/frontend/src/pages/system/dynamic-modules/ModuleEditorPage.vue`（「匯出範本」按鈕）與測試
- Modify: 四個 i18n 檔

**Interfaces:**
- Consumes: Task 4–6 的端點與 `TemplateSummary`。
- Produces: `template-selection.ts`：`uncheck(fields: TemplateFieldSummary[] /* 全部模組攤平，code 為限定名 */, selected: Set<string>, code: string): Set<string>`（連同所有直接、間接依賴它的欄位一起取消；鎖定的不動）、`check(fields, selected, code): Set<string>`（連同它依賴的欄位一起勾選）。

- [ ] **Step 1: 寫失敗的測試**

```ts
// template-selection：取消 order.taxRate → order.tax、order.total 也取消；再勾 order.total → order.tax、order.taxRate、order.subtotal 都勾回；鎖定欄位 uncheck 不變
// TemplateDialog：第 1 步選範本（列名稱與說明）；第 2 步每個模組一張卡：代碼、名稱（I18nTextInput）、欄位勾選清單（鎖定的 disabled）；
//   送出呼叫 instantiateTemplate(key, { modules, excludeFields })，excludeFields 是未勾選的限定名
// TemplateDialog：伺服器回 400，錯誤鍵帶前綴 "order.no.Config.Format" → 以 api-error.ts 既有方式顯示在對話框頂端；409 → 顯示訊息
// TemplateDialog：成功後 warnings 非空 → 每則一個 notify（type warning）；導向第一個模組的設計器 /system/dynamic-modules/{key}
// ModuleListPage：沒有設計器 Add 權限時兩個按鈕都不顯示
// ModuleListPage：匯入範本選檔 → importTemplate(文字內容) → 成功 notify「已匯入範本：{name}」；400 → 列出 template 鍵的訊息
// ModuleEditorPage：「匯出範本」下載 {key}.template.json（沿用 ExportMenu 的下載方式，revokeObjectURL 延後 1000 ms，第 7 階段）
```

- [ ] **Step 2: 跑測試確認失敗** — Run: `cd poc/frontend && bunx vitest run`，Expected: FAIL。
- [ ] **Step 3: 實作**。文字（zh-TW）：`designer.template.fromTemplate`＝`從範本建立`、`designer.template.import`＝`匯入範本`、`designer.template.export`＝`匯出範本`、`designer.template.choose`＝`選擇範本`、`designer.template.builtIn`＝`內建`、`designer.template.imported`＝`已匯入`、`designer.template.fields`＝`要建立的欄位`、`designer.template.locked`＝`必要欄位`、`designer.template.create`＝`建立`、`designer.template.created`＝`已從範本建立模組，請檢查後再啟用`、`designer.template.importDone`＝`已匯入範本：{name}`、`designer.template.detailOf`＝`明細（{header}）`。
- [ ] **Step 4: 跑前端測試與 vue-tsc** — Run: `cd poc/frontend && bunx vitest run && bunx vue-tsc --noEmit`，Expected: PASS。
- [ ] **Step 5: Commit** `feat(frontend): 從範本建立、匯入與匯出範本`

### Task 8: 驗收情境、文件與實機走查

**Files:**
- Create: `poc/backend/tests/DynamicModules.WebAPITests/Phase8AcceptanceTests.cs`
- Create: `docs/acceptance/phase8.md`、`docs/acceptance/phase8/*.png`
- Modify: `README.md`（進度表第 8 列、計畫與裁決連結）、`docs/manual.md`（模組範本一節、兩種新預設值；已知限制）、spec §15（本階段的已知限制）

- [ ] **Step 1: 寫 `Phase8_DemoScenario1`**（spec §13.2 Demo 情境一，一個 WebAPI 測試走完整條路）：
  1. admin 從內建範本建立訂單（預設代碼），啟用 `order`、`order-line`。
  2. sales01 建一張訂單：客戶 C-001、不給訂單日期、交期 = 今天 + 7 天，3 列明細（數量 2／3／1，單價 100／300／1000），明細不給交期。
     - 訂單日期 = 營運今天；三列的交期都 = 表頭交期；項次 1、2、3；訂單編號 `SO-yyyyMM-0001`。
  3. manager01 讀這張訂單：未稅合計 2100、稅額 105（`ROUND(2100 × 0.05, 0)`）、含稅總額 2205、明細筆數 3（數字刻意避開 .5，不受捨入方式影響）。
  4. 改第一列數量為 5 → 未稅合計 2400、稅額 120、含稅總額 2520。
  5. 刪掉第三列 → 未稅合計 1400、稅額 70、明細筆數 2。
  6. 交期改成訂單日期前一天 → 400，訊息「交期不得早於訂單日期」。
  7. 狀態改成「取消」不給原因 → 400（取消原因必填）；給原因 → 200；再改任何欄位 → 全單唯讀被拒（照第 4 階段的唯讀行為：回 400 或忽略修改，以既有測試的斷言方式為準，寫在 report）。
  8. 另一張訂單改成「已結案」後同樣全單唯讀。
  9. sales02 的列表看不到 sales01 的訂單；sales01 的 schema 與記錄沒有單價、金額、未稅合計、稅額、含稅總額（沒有 Pricing）。
- [ ] **Step 2: 寫 `Phase8_ExportImportScenario`**：匯出 `order` → 改 key 匯入 → 以 `order-b`／`order-b-line` 套用並啟用 → 在新模組建一張含 1 列明細的訂單，合計正確。
- [ ] **Step 3: 跑全部測試**：`dotnet test poc/backend`、`cd poc/frontend && bunx vitest run && bunx vue-tsc --noEmit && bunx quasar build`。
- [ ] **Step 4: 實機走查**（`poc/start.command`，Chrome）：從範本建立對話框（改代碼、取消勾選取消原因後看規則少兩條）、啟用、新增訂單時訂單日期帶今天、新增明細列帶表頭交期、合計與稅額、結案後唯讀、切換成業務看不到金額、匯出範本下載檔、匯入範本後清單出現。每項截圖存 `docs/acceptance/phase8/`。走不到的項目在 `phase8.md` 標「未實機驗證」。
- [ ] **Step 5: 寫 `docs/acceptance/phase8.md`**（格式照 `phase7.md`），更新 README、手冊、spec §15。
- [ ] **Step 6: Commit** `docs: 第 8 階段驗收、手冊與 spec 已知限制`

## 已知限制（寫進 spec §15 與手冊）

- 範本只帶群組層的權限建議，不帶欄位隱藏、唯讀、選項範圍、個人權限；套用後要在權限分頁補（決策 6）。
- 匯入的範本不能刪除、覆蓋；同代碼再匯入回 409。
- 範本沒有版本升級：已經從範本建出的模組，不會因為範本改了而更新。
- 匯出時無法表達的引用（彙總範本外的模組、帶範本外的表頭欄位、引用停用欄位的規則）直接省略，只在檔案的 `warnings` 說明。
- 結構驗證不檢查型別設定（例如自動編號格式）；匯入的範本要到套用時才會發現這類錯誤。
- 「預設今天」在前端用瀏覽器本地日期、後端用營運時區（同 §15 第 26 條）。
- 「預設帶表頭欄位」只在新增明細列時帶值，之後改表頭不會更新明細。
- 從範本建立是重播設計器指令，每個新欄位都會觸發一次既有的重算與 `TouchVersion`；模組是剛建的空模組，不影響結果，只是步驟多（訂單範本約 60 次指令）。
