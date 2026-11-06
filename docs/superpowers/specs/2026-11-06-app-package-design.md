# 應用程式封裝格式 設計文件

> 2026-11-06｜上位文件：[由核心長成簡單版 ERP：路線與核心缺口](2026-11-06-erp-roadmap-design.md)（已核准）；相關：[動態模組與自訂欄位](2026-11-03-dynamic-modules-design.md)（§13.1 模組範本）、[報表與儀表板](2026-11-05-reports-design.md)、[使用者與群組管理](2026-11-05-users-groups-design.md)
> 狀態：草稿，待使用者核准。這份文件屬路線階段 0，只設計不實作；實作由階段 1 的 worker 依 §10 的清單進行。
> 標「（我的判斷）」的是我代為決定、可以改的，全部彙整在 §12；其餘來自路線文件的已定決策。文中引用的類別、資料表與路由都已對照目前 `feature/stage0` 分支的程式核實；對不上的地方列在 §14。

## 1. 意圖

路線要求「應用程式可以由客戶選擇開哪些、開了之後屬於客戶自己改」。要做到這件事，需要三樣東西：

1. 一份檔案格式，描述一個應用程式由哪些模組、欄位、規則、報表、權限組成。
2. 「開啟」：把這份檔案在客戶的資料庫裡展開成一般的模組與設定，之後它們就是客戶的，沒有任何東西還依賴那份檔案。
3. 「關閉」：停止使用但不丟資料，既有引用與資料仍可查閱。

現有的單模組範本（第 8 階段）已經做了 (1) 與 (2) 的一半。這份設計的原則是**擴充它，不重寫它**：封裝格式是範本格式的超集，開啟就是「重播設計器指令」那套機制加上應用程式層級的記帳、相依與關閉。

## 2. 已定決策與範圍

來自路線文件與使用者的裁決：

- 關閉後既有引用與資料仍可查閱；開啟後應用程式屬於客戶，新版不自動合併。
- 目標客戶 50 人以下，只做簡單版 ERP；宣告式優先；資料庫先留 SQLite。
- 鐵則：ERP 邏輯不寫死、不做執行期 DDL、權限不 fail-open、驗證不只做在前端、空系統永遠是合法起點。
- 階段 0 的「安全刪除」：只有未啟用、無資料、未被引用才可刪。

**本文範圍**：封裝格式、開啟與關閉的行為規則、跨應用程式引用、與範本的關係、階段 1 清單。**不含**：任何實作、CRM 應用程式的內容（階段 1 另寫）、儀表板與狀態機等階段 1 其他能力的格式（只預留 section，§4.6）。

## 3. 應用程式由哪些定義組成

### 3.1 對照表

「封裝 section」是 §4 檔案裡放這類定義的位置。「現況」欄的「無」表示目前核心沒有這個能力，只預留 section。

| 類別 | 現有資料表／實體 | 現有 API 與格式 | 封裝 section | 現況 |
|---|---|---|---|---|
| 模組 | `Dynamic_Module`（`Module`） | `/modules`；範本 `modules[]` | `modules[]` | 有 |
| 區塊 | `Dynamic_Block` | 範本 `blocks[]` | `modules[].blocks[]` | 有 |
| 欄位、選項 | `Dynamic_Field`、`Dynamic_FieldOption` | 範本 `fields[]`、`options[]` | `blocks[].fields[]` | 有 |
| 明細關係 | `Field.Config.Relation = detail` 加 `RefTarget` | 範本欄位 `config.relation` | 同欄位設定 | 有 |
| 規則 | `Dynamic_Rule` | `/modules/{key}/rules`；範本 `rules[]` | `modules[].rules[]` | 有 |
| 公式、彙總、自動編號 | `Field.Config` 的 `Formula`、`Rollup`、`Format` | 範本欄位 `config` | 同欄位設定 | 有 |
| 共用檢視 | `Dynamic_ListView`（`OwnerUserId` 為 null） | `/modules/{key}/views`；範本 `views[]` | `modules[].views[]` | 有 |
| 報表 | `Dynamic_Report`、`Dynamic_ReportShare` | `/reports`；`ReportDefinition` JSON | `reports[]` | 有，範本沒帶 |
| 模組動作與資料範圍權限 | `Dynamic_GroupProgramPrivilege`、`Dynamic_ModuleDataScope` | `/modules/{key}/permissions`；範本 `permissions[]` | `modules[].permissions[]` | 有 |
| 欄位隱藏與唯讀、選項範圍 | `Dynamic_FieldPermission`、`Dynamic_FieldOptionScope` | 同上 | `fieldAccess[]`、`optionScopes[]` | 有，範本沒帶 |
| 群組（角色） | `Dynamic_PermissionGroup` | `/admin/groups` | `groups[]` | 有 |
| 選單 | `Module.Section`、`Module.Icon` | `GET /menu` | 模組的 `section`、`icon` | 有，沒有排序 |
| 首頁、儀表板 | 階段 1a 實作時的分支已有（`/dashboards`、`/system/dashboards`、首頁儀表板），2026-11-06 實作時更正 | `/dashboards` | `dashboards[]`（保留，格式 2 不收） | 無 |
| 流程、狀態機、單據轉換 | 無 | 無 | `workflows[]`、`conversions[]`（保留） | 階段 1 才有 |
| 事件 | 無 | 無 | `events[]`（保留） | 階段 2 才有 |
| 列印範本 | 無 | 無 | `printTemplates[]`（保留） | 階段 1 才有 |
| 參照目標（既有實體） | `IReferenceProvider`（程式註冊，不是資料） | `/entities` | `manifest.requires.entities[]` | 有，只能宣告相依 |

### 3.2 補充說明

- **範本 `modules[]` 的形狀照搬**，不改欄位名稱，所以現有四個內建範本與匯入程式的資料結構不用動（§8）。
- **選單沒有獨立的 section。** 現行選單由模組的 `Section`（單一語系的字串）與 `Icon` 組成（`GetMenuHandler`），首頁依 `Section` 分組。封裝只決定每個模組的 `section` 與 `icon`；選單排序目前不存在（依名稱排序），這份設計不新增。儀表板出現後，首頁內容來自 `dashboards[]`。
- **權限的群組用「代號」，不用 id。** 套件宣告自己需要哪些群組（`groups[]`），開啟時由管理員決定對應到哪個既有群組、新建、或略過（§6.2）。
- **不進封裝的東西**（開啟時從來不會被建立，匯出時也不會輸出）：記錄資料（`Record`、`RecordIndex`、`RecordHistory`）、`NumberSequence`（流水號從頭開始）、使用者與群組成員、個人權限（`UserProgramPrivilege`）、個人清單檢視與個人報表、外觀設定、Demo 資料。示範資料不放在封裝裡；Demo 站由 Demo 產生器在開啟之後自己灌（路線文件「Demo」一節）。
- **現有的 `ProgramCode` 與 id 一律在開啟時重新配發**，檔案裡不會出現任何資料庫 id（§6.2）。

## 4. 檔案格式

### 4.1 檔案、編碼、大小

- 副檔名 `.app.json`；媒體型別 `application/json`。
- 編碼 UTF-8。允許開頭有 BOM（讀取時去掉）；其他編碼（UTF-16 等）拒絕。寫出時不加 BOM，中文不跳脫（沿用 `TemplateJson.Options` 的 `UnsafeRelaxedJsonEscaping`）。
- 大小上限 1 MiB（UTF-8 位元組），與現有範本匯入（`TemplatesController.Import` 的 `RequestSizeLimit(1024 * 1024)`）一致。超過在讀取請求本文前就拒絕，回 413。
- JSON 巢狀深度上限 32。
- 同一個物件內**重複的屬性名稱視為錯誤**。據我所知 net8.0 的 `System.Text.Json` 沒有內建選項能擋（後者覆蓋前者），要用 `Utf8JsonReader` 先掃一遍；實作時以測試確認（§10 測試項目）。
- 屬性名稱 camelCase；列舉值用字串，沿用 `TemplateJson` 的慣例：`FieldType` 全小寫（`text`、`multienum`），其餘列舉 camelCase（`standalone`、`readOnly`、`own`）。
- 檔案內容是資料，不是程式：沒有任何欄位會被當成運算式以外的程式碼執行。公式與規則條件沿用現有的受限語法。

### 4.2 頂層結構

```json
{
  "format": "formwright-app",
  "formatVersion": 2,
  "manifest":      { },
  "groups":        [ ],
  "modules":       [ ],
  "fieldAccess":   [ ],
  "optionScopes":  [ ],
  "reports":       [ ],
  "dashboards":    [ ],
  "workflows":     [ ],
  "conversions":   [ ],
  "events":        [ ],
  "printTemplates":[ ]
}
```

| 屬性 | 型別 | 必填 | 說明 |
|---|---|---|---|
| `format` | string | 是 | 固定 `"formwright-app"` |
| `formatVersion` | integer | 是 | 目前 `2`（`1` 是現有範本格式，§4.9） |
| `manifest` | object | 是 | §4.3 |
| `groups` | array | 否，預設空 | §4.4 |
| `modules` | array | 是，至少 1 個 | 元素就是現有的 `TemplateModule`（§4.4） |
| `fieldAccess` | array | 否 | §4.4 |
| `optionScopes` | array | 否 | §4.4 |
| `reports` | array | 否 | §4.5 |
| `dashboards`、`workflows`、`conversions`、`events`、`printTemplates` | array | 否 | 保留；目前只能是空陣列（§4.6） |

頂層的 JSON Schema（draft 2020-12）。巢狀物件的細節在後面的表格；實作用 C# record 加驗證器，這份 schema 是對外的契約，放進 `poc/backend/templates/` 旁的 `app.schema.json`（階段 1 產出，§10）。

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "type": "object",
  "required": ["format", "formatVersion", "manifest", "modules"],
  "additionalProperties": false,
  "patternProperties": { "^x-": {} },
  "properties": {
    "format": { "const": "formwright-app" },
    "formatVersion": { "const": 2 },
    "manifest": { "$ref": "#/$defs/manifest" },
    "groups": { "type": "array", "maxItems": 20, "items": { "$ref": "#/$defs/group" } },
    "modules": { "type": "array", "minItems": 1, "maxItems": 30 },
    "fieldAccess": { "type": "array", "maxItems": 2000 },
    "optionScopes": { "type": "array", "maxItems": 2000 },
    "reports": { "type": "array", "maxItems": 50 },
    "dashboards": { "type": "array", "maxItems": 0 },
    "workflows": { "type": "array", "maxItems": 0 },
    "conversions": { "type": "array", "maxItems": 0 },
    "events": { "type": "array", "maxItems": 0 },
    "printTemplates": { "type": "array", "maxItems": 0 }
  },
  "$defs": {
    "code": { "type": "string", "pattern": "^[a-z][a-zA-Z0-9_]{0,39}$" },
    "appId": { "type": "string", "pattern": "^[a-z][a-z0-9-]{1,39}$" },
    "i18n": {
      "type": "object",
      "required": ["zh-TW"],
      "propertyNames": { "enum": ["zh-TW", "en-US", "th", "vi"] },
      "additionalProperties": { "type": "string", "minLength": 1, "maxLength": 200 }
    },
    "manifest": {
      "type": "object",
      "required": ["id", "version", "name"],
      "additionalProperties": false,
      "patternProperties": { "^x-": {} },
      "properties": {
        "id": { "$ref": "#/$defs/appId" },
        "version": { "type": "string", "pattern": "^(0|[1-9]\\d*)\\.(0|[1-9]\\d*)\\.(0|[1-9]\\d*)$" },
        "name": { "$ref": "#/$defs/i18n" },
        "description": { "$ref": "#/$defs/i18n" },
        "icon": { "type": "string", "maxLength": 100 },
        "vendor": { "type": "string", "maxLength": 100 },
        "requires": {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "apps": { "type": "array", "maxItems": 10, "items": {
              "type": "object", "required": ["id", "modules"], "additionalProperties": false,
              "properties": {
                "id": { "$ref": "#/$defs/appId" },
                "modules": { "type": "array", "minItems": 1, "items": { "$ref": "#/$defs/appId" } } } } },
            "entities": { "type": "array", "maxItems": 20, "items": { "type": "string", "maxLength": 100 } }
          }
        }
      }
    },
    "group": {
      "type": "object",
      "required": ["code", "name"],
      "additionalProperties": false,
      "properties": { "code": { "$ref": "#/$defs/code" }, "name": { "$ref": "#/$defs/i18n" } }
    }
  }
}
```

### 4.3 manifest

| 屬性 | 型別 | 必填 | 說明 |
|---|---|---|---|
| `id` | string | 是 | 應用程式代碼，`^[a-z][a-z0-9-]{1,39}$`（與 `ModuleRules.ModuleKeyPattern` 相同）。開啟後成為 `Dynamic_App.Key`，全站唯一 |
| `version` | string | 是 | `MAJOR.MINOR.PATCH`。**只是資訊**：記在 `App.PackageVersion`，不參與任何相容判斷或合併（新版不自動合併） |
| `name` | i18n | 是 | 應用程式名稱；`zh-TW` 必填，語系鍵只能是 `zh-TW`、`en-US`、`th`、`vi`（`I18nText.SupportedLocales`），每語系 1 到 200 字 |
| `description` | i18n | 否 | 同上，上限 200 字 |
| `icon` | string | 否 | Quasar 圖示名稱（同 `Module.Icon`） |
| `vendor` | string | 否 | 發行者，純顯示 |
| `requires.apps[]` | array | 否 | 相依的其他應用程式：`id` 加上「需要它的哪些模組」（模組代號，§4.7）。全部為硬相依，沒有可選相依 |
| `requires.entities[]` | array | 否 | 相依的既有實體鍵（`ReferenceProviderRegistry` 要找得到，例如 `sales.customer`） |

`requires` 必須涵蓋套件實際用到的每一個外部目標（§7.1 的檢查會對帳）。

### 4.4 groups、modules、fieldAccess、optionScopes

**`groups[]`**：套件期望存在的權限群組。

| 屬性 | 型別 | 必填 | 說明 |
|---|---|---|---|
| `code` | string | 是 | 套件內的群組代號，`^[a-z][a-zA-Z0-9_]{0,39}$`；`modules[].permissions[].group`、`fieldAccess[].group`、`optionScopes[].groups[]`、`reports[].shareWith[]` 都引用它 |
| `name` | i18n | 是 | 管理員選「新建」時用的名稱 |

**`modules[]`**：每個元素就是現有 `TemplateModule`（`Application/Dynamic/TemplateService/Data/TemplatePackage.cs`），欄位如下。

| 屬性 | 型別 | 必填 | 說明 |
|---|---|---|---|
| `code` | string | 是 | 模組在套件內的代號，`ModuleKeyPattern`；也是 `Dynamic_AppModule.Code`，**開啟後永遠不變**，其他套件靠它引用（§7） |
| `key` | string | 是 | 預設的 `Module.Key`；與別的模組衝突時由開啟請求改寫（§6.2） |
| `name` | i18n | 是 | |
| `icon`、`section` | string | 否 | 選單用；`section` 是單一語系字串（沿用 `Module.Section`） |
| `kind` | `standalone` \| `extension` | 是 | |
| `hostEntityKey` | string \| null | `extension` 時必填 | 擴充的既有實體；同時要列入 `requires.entities` |
| `titleField` | string \| null | 非擴充模組必填 | 欄位代號，型別限 `text`、`autonumber` |
| `blocks[]` | array | 是，至少 1 個 | `{code, label, collapsed, columns(1–3, 預設 2), fields[]}` |
| `blocks[].fields[]` | array | 否 | `{code, type, label, config, options[]}`；`config` 是 `FieldConfig` 的 JSON，其中公式、選項值、彙總、`parentDefaultField` 用代號（見 §4.7） |
| `rules[]` | array | 否 | `{kind, targets[], condition, options[], message, active}`；`targets` 元素是 `field:<代號>`、`block:<代號>` 或 `*` |
| `views[]` | array | 否 | 共用檢視 `{name, columns[], filter, sort[], isDefault}` |
| `permissions[]` | array | 否 | `{group, privileges, scope}`：`group` 在 v2 是 `groups[].code`；`privileges` 是七軸 `{read, add, edit, delete, printExport, pricing, approve}`；`scope` 是 `own`、`group`、`all` 或 null。明細模組不能有（沿用表頭，與現有驗證一致） |

與 v1 完全相同的驗證都保留（區塊與欄位代號格式、公式引用、彙總來源、循環依賴、規則條件引用等，見 `TemplateValidator`）。v2 另外對 `config` 做**未知鍵檢查**（§4.8）。

**`fieldAccess[]`**：欄位層例外（`Dynamic_FieldPermission`），補上範本帶不了的部分。

| 屬性 | 型別 | 必填 | 說明 |
|---|---|---|---|
| `field` | string | 是 | 限定欄位名 `<模組代號>.<欄位代號>`，必須在本套件內 |
| `group` | string | 是 | `groups[].code` |
| `access` | `hidden` \| `readOnly` | 是 | 標題欄位不能是 `hidden`（與 `SaveModulePermissions` 的規則一致） |

**`optionScopes[]`**：選項只限特定群組可選（`Dynamic_FieldOptionScope`）。

| 屬性 | 型別 | 必填 | 說明 |
|---|---|---|---|
| `option` | string | 是 | `<模組代號>.<選項代號>` |
| `groups` | array of string | 是，至少 1 個 | `groups[].code` |

兩者在開啟時與 `modules[].permissions[]` 合併成**每個模組一次** `SaveModulePermissions`（它是整份取代，不能分次呼叫）。沒有對應的群組（略過或未對應）時，該群組的所有權限列都不建立，並在 warnings 回報。

### 4.5 reports

| 屬性 | 型別 | 必填 | 說明 |
|---|---|---|---|
| `code` | string | 是 | 套件內報表代號，`Dynamic_AppReport.Code` |
| `name` | string | 是 | 報表名稱（`Report.Name` 是一般字串，1 到 200 字，不分語系） |
| `shareWith` | array of string | 否 | `groups[].code`；開啟時分享給對應到的群組（`SetReportShares`） |
| `definition` | object | 是 | `ReportDefinition`（`Domain/Reports/ReportDefinition.cs`）把 id 換成代號 |

`definition` 的屬性與 `ReportDefinition` 相同（`version` 必須是 `1`；`viz`、`measures[].kind`、`dateRange.kind`、`filters[].op` 的合法值沿用現有定義），只有參照 id 的地方改成代號：

| `ReportDefinition` 現況 | 封裝形式 |
|---|---|
| `MainModuleKey` | `mainModule`：模組代號 |
| `Child { ModuleKey, LinkFieldId }` | `child { module, linkField }`：模組代號、限定欄位名 |
| `ReportFieldRef { Source, FieldId, ViaFieldId }` | `{ source, field, via }`：`field`、`via` 都是限定欄位名；沿參照帶出標題時 `field` 為 null |
| 篩選與量值條件的值 | 選項值寫 `opt_<選項代號>`（同規則條件） |
| `ReportSort.Key`（量值 id，或 `ReportCalculator.KeyOf` 的 `{source}:{fieldId}:{viaFieldId}`） | 量值 id 原樣；欄位鍵寫 `{source}:{field}:{via}`，欄位與 via 是限定欄位名，空的留空字串 |

報表的欄位引用**只能指向本套件的模組**（含沿參照帶出的目標欄位）。理由見 §7.4。開啟時由 `AppReportCodec` 把代號換回 id，再送 `CreateReportRequest`，所以現有的報表定義驗證照樣會跑。報表的擁有者是執行開啟的管理員。

### 4.6 保留的 section

`dashboards`、`workflows`、`conversions`、`events`、`printTemplates` 在格式 2 只能是空陣列或不寫。讀取到非空陣列時整份拒絕，訊息為「此版本不支援 `<section>`」。這是 fail-closed：一個靜默略過 `workflows` 的讀取器，會讓客戶開出缺了控管的單據流程。

各 section 的內容格式由實作它們的階段在自己的設計文件裡定義，並以**提高 `formatVersion`**（§4.9）的方式啟用，不在這份文件預先決定形狀。階段 1 的單據轉換、狀態機、列印範本設計文件完成時，要同步更新本文件 §3.1 與 §4.2 的 schema。

### 4.7 代號與引用語法

套件內所有引用都用代號；id 與 `Field.Key`（`f_<id>`）都不出現。

- **限定欄位名** `<模組代號>.<欄位代號>`：沿用 `TemplateGraph.Qualify`。
- **限定選項名** `<模組代號>.<選項代號>`：選項代號在模組內唯一（`TemplateValidator` 現有規則）。
- **選項值** `opt_<選項代號>`：用在 `defaultValue`、規則與檢視條件的值、報表篩選值。
- **公式** `{<欄位代號>}`：同模組欄位（沿用 `TemplateGraph.FormulaReference`）。
- **彙總** `rollup.sourceField`、`rollup.aggregateField`：限定欄位名。
- **參照欄位目標** `config.refTarget`：

  | 形狀 | 意義 |
  |---|---|
  | `{"kind":"module","key":"<模組代號>"}` | 本套件的模組 |
  | `{"kind":"module","key":"<模組代號>","app":"<應用程式 id>"}` | 另一個應用程式的模組，該應用程式與該模組必須列在 `requires.apps[]` |
  | `{"kind":"entity","key":"<實體鍵>"}` | 既有實體，必須列在 `requires.entities[]` |

  `app` 是 v2 新增的屬性。v1 範本沒有它，v1 的「`key` 不在套件內就當成既有的 `Module.Key`」行為只在升級器內處理（§8.3），v2 不允許。

### 4.8 驗證規則

四層，由淺到深，前一層失敗就不跑下一層；錯誤格式沿用範本匯入：400，`errors["package"]` 是字串清單，每則 `{位置}: {原因}`，模組內的位置沿用 `{模組代號}.{欄位代號}`，其他 section 用 `groups[1].code` 這類路徑。

1. **檔案層**：大小、UTF-8、JSON 語法、深度、重複屬性（§4.1）。
2. **結構層**：
   - `format`、`formatVersion` 正確；未知頂層屬性一律拒絕，只有 `x-` 開頭的屬性會被讀取器丟掉（給工具附註用，內容永遠不被解讀）。
   - **`config` 的未知鍵拒絕。** 允許的鍵：`required`、`readOnly`、`maxLength`、`precision`、`scale`、`defaultValue`、`defaultToday`、`parentDefaultField`、`fullWidth`、`showInList`、`isFilterable`、`isSortable`、`formula`、`resultType`、`format`、`refTarget`（只有 `kind`、`key`、`app`）、`onDelete`、`relation`、`scope`、`rollup`（只有 `sourceField`、`function`、`aggregateField`、`condition`）。原因：v1 的 `FieldConfig` 反序列化會靜默忽略未知鍵，`reqired: true` 這類拼錯不會被發現。
   - 各代號格式（沿用 `TemplateValidator` 的 `Code`、`OptionCode`、`ModuleKey` 三個正規式）、必填、列舉值、i18n（`zh-TW` 必填、語系鍵合法）。**修正 v1 的一個缺口：** v1 的 `HasZhTw` 不擋不支援的語系鍵，要等到開啟時 `CreateModule` 的 `IsValid` 才失敗；v2 在這一層就擋。
   - 數量上限：模組 30、每模組欄位 300、每欄位選項 200、每模組規則 100、檢視 20、群組 20、報表 50、`fieldAccess` 與 `optionScopes` 各 2000。目的是限制單一交易重播的設計器指令數（訂單範本約 60 次）。
3. **引用層**：v1 的全部檢查，加上
   - `groups[].code` 不重複；所有 `group` 引用都存在。
   - 明細（`relation: detail`）的表頭必須在本套件內（不能指向別的應用程式）。
   - 彙總的來源與對象必須在本套件內（v1 已要求）。
   - `refTarget.app` 與 `requires` 對帳：用到的外部模組都在 `requires.apps[]`；`requires` 裡宣告但沒用到的也警告（不失敗）。
   - `reports[]` 引用的模組、欄位、群組、選項都在本套件內。
   - `fieldAccess`、`optionScopes` 引用的欄位、選項、群組存在，不重複。
   - 套件內模組互相參照不限制（沿用現行行為）；`requires` 之間不得循環（§7.3）。
4. **環境層**（開啟時才做，需要資料庫）：§6.2 的前置檢查，加上現有設計器指令自己的驗證（型別設定是否合法、公式能否解析等），它們在重播時失敗就整批回滾。

### 4.9 版本與向前相容

- `formatVersion` 是整數，只在「舊的讀取器讀新檔案可能理解錯誤」時提高。目前：`1` 是現有單模組範本格式，`2` 是本文的封裝格式。
- **新版讀取器讀舊檔案：** 讀取器依 `formatVersion` 分派，`1` 走 `TemplateJson.Parse` 再經升級器轉成 v2 的記憶體表示（§8.3），`2` 走 v2 讀取器。每提高一版就加一個「N 到 N+1」的升級步驟，串起來能開所有舊格式；寫出一律用最新版。
- **舊版讀取器讀新檔案：** `formatVersion` 大於讀取器支援的最大值時拒絕，訊息「這個套件需要較新版本的 Formwright」。不嘗試部分讀取。
- 同一版內只允許**加選填屬性且舊讀取器忽略它也不會出錯**的變更，且必須放在 `x-` 開頭的屬性之下；其他任何新增都提高版本。結合 §4.8 的未知屬性拒絕，這保證舊讀取器不會默默漏掉新的權限或控管內容。
- `manifest.version` 與 `formatVersion` 無關（前者是內容版本、純資訊，後者是格式版本）。
- 儲存的套件原文（匯入的套件存在 `Dynamic_AppPackage.Json`）每次讀取時才升級，不回寫，與現有 `TemplateSource` 對匯入範本的做法相同。

### 4.10 範例

虛構的簡化版「客戶＋聯絡人」。含兩個群組、兩個模組（一般參照，聯絡人指向客戶）、一個彙總、兩條規則、一個檢視、權限與欄位例外、一張報表。模組部分的形狀對照 `poc/backend/templates/order.json` 與 `TemplateValidator` 的規則手寫，尚未實際跑過驗證器；階段 1 要把它放進測試資料，作為第一個必須通過的案例。

```json
{
  "format": "formwright-app",
  "formatVersion": 2,
  "manifest": {
    "id": "crm-lite",
    "version": "1.0.0",
    "name": { "zh-TW": "客戶與聯絡人", "en-US": "Customers and contacts" },
    "description": { "zh-TW": "簡化示範：客戶與聯絡人", "en-US": "A small sample: customers and contacts" },
    "icon": "contacts",
    "vendor": "Example Studio",
    "requires": { "apps": [], "entities": [] }
  },
  "groups": [
    { "code": "sales", "name": { "zh-TW": "業務", "en-US": "Sales" } },
    { "code": "salesLead", "name": { "zh-TW": "業務主管", "en-US": "Sales lead" } }
  ],
  "modules": [
    {
      "code": "customer", "key": "crm-customer",
      "name": { "zh-TW": "客戶", "en-US": "Customer" },
      "icon": "business", "section": "客戶關係",
      "kind": "standalone", "hostEntityKey": null, "titleField": "name",
      "blocks": [
        { "code": "main", "label": { "zh-TW": "基本資料", "en-US": "General" }, "collapsed": false, "columns": 2,
          "fields": [
            { "code": "name", "type": "text", "label": { "zh-TW": "客戶名稱", "en-US": "Name" },
              "config": { "required": true, "maxLength": 100, "showInList": true, "isFilterable": true, "isSortable": true } },
            { "code": "status", "type": "enum", "label": { "zh-TW": "狀態", "en-US": "Status" },
              "options": [
                { "code": "lead", "label": { "zh-TW": "潛在", "en-US": "Lead" }, "color": null },
                { "code": "active", "label": { "zh-TW": "往來中", "en-US": "Active" }, "color": null },
                { "code": "dormant", "label": { "zh-TW": "停止往來", "en-US": "Dormant" }, "color": null }
              ],
              "config": { "defaultValue": "opt_lead", "required": true, "showInList": true, "isFilterable": true } },
            { "code": "phone", "type": "phone", "label": { "zh-TW": "電話", "en-US": "Phone" },
              "config": { "showInList": true } },
            { "code": "dormantReason", "type": "textarea", "label": { "zh-TW": "停止往來原因", "en-US": "Reason" },
              "config": {} },
            { "code": "contactCount", "type": "rollup", "label": { "zh-TW": "聯絡人數", "en-US": "Contacts" },
              "config": { "showInList": true, "rollup": { "sourceField": "contact.customer", "function": "count" } } }
          ] }
      ],
      "rules": [
        { "kind": "visibility", "targets": ["field:dormantReason"], "active": true, "options": [],
          "condition": { "all": [ { "field": "status", "op": "eq", "value": "opt_dormant" } ] } },
        { "kind": "required", "targets": ["field:dormantReason"], "active": true, "options": [],
          "condition": { "all": [ { "field": "status", "op": "eq", "value": "opt_dormant" } ] } }
      ],
      "views": [
        { "name": { "zh-TW": "全部客戶", "en-US": "All customers" },
          "columns": ["name", "status", "phone", "contactCount"],
          "filter": null, "sort": [ { "field": "name", "order": "asc" } ], "isDefault": true }
      ],
      "permissions": [
        { "group": "sales", "scope": "own",
          "privileges": { "read": true, "add": true, "edit": true, "delete": false, "printExport": false, "pricing": false, "approve": false } },
        { "group": "salesLead", "scope": "all",
          "privileges": { "read": true, "add": true, "edit": true, "delete": true, "printExport": true, "pricing": false, "approve": false } }
      ]
    },
    {
      "code": "contact", "key": "crm-contact",
      "name": { "zh-TW": "聯絡人", "en-US": "Contact" },
      "icon": "person", "section": "客戶關係",
      "kind": "standalone", "hostEntityKey": null, "titleField": "name",
      "blocks": [
        { "code": "main", "label": { "zh-TW": "基本資料", "en-US": "General" }, "collapsed": false, "columns": 2,
          "fields": [
            { "code": "name", "type": "text", "label": { "zh-TW": "姓名", "en-US": "Name" },
              "config": { "required": true, "maxLength": 50, "showInList": true, "isFilterable": true, "isSortable": true } },
            { "code": "customer", "type": "reference", "label": { "zh-TW": "客戶", "en-US": "Customer" },
              "config": { "required": true, "showInList": true, "isFilterable": true,
                          "refTarget": { "kind": "module", "key": "customer" }, "onDelete": "restrict" } },
            { "code": "email", "type": "email", "label": { "zh-TW": "電子郵件", "en-US": "Email" },
              "config": { "showInList": true } }
          ] }
      ],
      "rules": [],
      "views": [],
      "permissions": [
        { "group": "sales", "scope": "own",
          "privileges": { "read": true, "add": true, "edit": true, "delete": false, "printExport": false, "pricing": false, "approve": false } },
        { "group": "salesLead", "scope": "all",
          "privileges": { "read": true, "add": true, "edit": true, "delete": true, "printExport": true, "pricing": false, "approve": false } }
      ]
    }
  ],
  "fieldAccess": [
    { "field": "customer.phone", "group": "sales", "access": "readOnly" }
  ],
  "optionScopes": [],
  "reports": [
    { "code": "customerByStatus", "name": "客戶狀態分布", "shareWith": ["salesLead"],
      "definition": {
        "version": 1, "mainModule": "customer", "child": null,
        "columns": [],
        "groups": [ { "field": { "source": "main", "field": "customer.status", "via": null }, "bucket": null } ],
        "measures": [ { "id": "n", "kind": "countMain", "field": null, "condition": null } ],
        "filters": [], "primaryDateField": null, "dateRange": null, "sort": [], "viz": "donut" } }
  ]
}
```

跨應用程式引用的片段（另一個虛構應用程式 `quote-lite` 的一部分，只列與相依有關的地方）：

```json
{
  "manifest": {
    "id": "quote-lite", "version": "1.0.0", "name": { "zh-TW": "簡易報價" },
    "requires": { "apps": [ { "id": "crm-lite", "modules": ["customer"] } ], "entities": [] }
  },
  "modules": [
    { "code": "quote", "key": "quote", "titleField": "no", "blocks": [ { "code": "main", "fields": [
        { "code": "no", "type": "autonumber", "config": { "format": "Q-{yyyyMM}-{0000}" } },
        { "code": "customer", "type": "reference",
          "config": { "refTarget": { "kind": "module", "key": "customer", "app": "crm-lite" } } } ] } ] }
  ]
}
```

（片段省略了必填的 `label`、`kind` 等，只示意 `requires` 與 `refTarget.app` 的寫法。）

## 5. 資料模型：新增與異動

全部走 EF migration（建議名稱 `AppPackages`），SQLite 用 `Dynamic_` 前綴，JSON 欄位用現有 `HasJsonConversion`，稽核欄位用現有 `AuditableEntity`。沒有任何執行期 DDL。

**新表 `Dynamic_App`**（實體 `App`）

| 欄位 | 型別 | 說明 |
|---|---|---|
| `Id` | bigint，主鍵 | |
| `Key` | nvarchar(40)，唯一索引 | = `manifest.id` |
| `NameI18n` | JSON | 開啟當時 `manifest.name` 的副本，之後可由客戶改 |
| `Icon` | nvarchar(100)，可空 | |
| `PackageVersion` | nvarchar(20) | `manifest.version`，僅資訊 |
| `FormatVersion` | int | 開啟時套件的格式版本，僅資訊 |
| `Source` | nvarchar(20) | `builtIn` 或 `imported`（同 `TemplateSource`） |
| `Status` | nvarchar(20) | `Open` 或 `Closed`（列舉 `AppStatus`） |
| `RequiresJson` | JSON | `manifest.requires` 的副本；相依判斷的依據之一（§7.2） |
| `RowVersion` | nvarchar(36)，併發 token | GUID，每次寫入換新值（同 `Report`），避免兩位管理員同時開關 |
| 稽核欄位 | | `CreatedUserId`、`CreatedTime`（=開啟的人與時間）、`ModifiedUserId`、`ModifiedTime`（最後一次開關） |

**新表 `Dynamic_AppModule`**（實體 `AppModule`）：`AppId`（FK 到 `Dynamic_App`，cascade）、`Code`（nvarchar(40)，套件內模組代號）、`ModuleId`（FK 到 `Dynamic_Module`，cascade）；主鍵 `(AppId, Code)`；`ModuleId` 唯一索引（一個模組最多屬於一個應用程式）。這張表同時是「模組屬於哪個應用程式」與「代號對應到實際模組」的唯一來源，`Module` 本身不加欄位，所以不屬於任何應用程式的模組（手動建立的、範本建立的）完全不受影響。

**新表 `Dynamic_AppReport`**（實體 `AppReport`）：`AppId`（FK，cascade）、`ReportId`（FK 到 `Dynamic_Report`，cascade）、`Code`（nvarchar(40)）；主鍵 `(AppId, ReportId)`。用途：匯出時重建 `reports[]`、刪除應用程式時一併刪報表。

**新表 `Dynamic_AppPackage`**（實體 `AppPackage`）：匯入的套件，形狀同 `Dynamic_ModuleTemplate`——`Id`、`Key`（nvarchar(40) 唯一，= `manifest.id`）、`Version`（nvarchar(20)）、`Json`（收到的原文）、稽核欄位。

**異動**

- `ModuleStatus` 列舉新增 `Closed`。`Module.Status` 是字串欄位（`HasConversion<string>`，長度 20），新增值不需要 schema 變更，**也不需要為它單獨產生 migration**（snapshot 不記列舉值；實作時確認過）。階段 1a 的 migration `AppPackages` 只為新表而產生。
- `ModuleSchemaResponse` 新增 `readOnly`（`Status == Closed`）；`MenuItem` 新增 `closed`。這兩個是 API 回應的新增欄位，不是資料表欄位。
- `DynamicProgram.Active`：關閉的模組**保持 true**（§6.3），所以 `SeedData` 啟動時的同步規則要改（§10）。

不新增 `Module.AppId`、不在 `Report` 加欄位：應用程式與物件的關係只放在上面三張關聯表。

## 6. 開啟、關閉、重新開啟、刪除

### 6.1 狀態

應用程式只有兩個狀態：`Open`、`Closed`。模組的狀態沿用 `ModuleStatus`（`Draft`、`Active`、`Inactive`）加上新的 `Closed`：

- `Closed` 只由應用程式的關閉動作設定，只有應用程式的重新開啟能改回 `Active`。
- 單獨對 `Closed` 的模組呼叫 `ActivateModule`、`DeactivateModule` 回 409「請由應用程式重新開啟或關閉」。
- 關閉時只有 `Active` 的模組會變 `Closed`；`Draft`、`Inactive` 的模組（客戶自己停用的）保持原狀，重新開啟時也不會被啟用。
- 沒有 `Dynamic_App` 的模組（手動建立的）行為完全照舊：只有 `Draft`、`Active`、`Inactive`。

選用新的列舉值而不是旗標，是因為現有程式有約 30 處用 `Status: ModuleStatus.Active` 判斷「能不能用」（§6.3 逐項列出）。新增 `Closed` 後，**漏改任何一處的結果是 404（fail-closed）**，而不是讓寫入通過。

### 6.2 開啟

**請求**（`POST /api/v3/dynamic/apps/packages/{id}/open`，body）：

| 屬性 | 說明 |
|---|---|
| `moduleKeys` | 物件，`{ 模組代號: 新的 Module.Key }`，只列要改的；用來解決 key 衝突 |
| `excludeFields` | 沿用範本：限定欄位名清單，取消勾選的欄位（鎖定欄位不能排除；規則由 `TemplateInstantiator.CheckExclude` 現有邏輯處理） |
| `groupMap` | 物件，`{ 群組代號: { "mode": "map", "groupId": 3 } 或 { "mode": "create" } 或 { "mode": "skip" } }`；沒列出的群組視為 `skip` 並回報 warning（fail-closed：不給任何人權限） |
| `locale` | 建立群組與選單分區名稱時用的語系，預設 `zh-TW` |

**預覽**（`POST .../packages/{id}/preview`，同樣的 body）做所有前置檢查但不寫入，回傳：`errors`、`keyConflicts`（模組代號與衝突的 key）、`hostEntityConflicts`、`missingDependencies`（§7.2）、`groups`（每個群組代號、名稱，以及建議的對應：現有群組的 `Key` 等於代號就建議 `map`，否則 `create`）、`warnings`。前端的開啟精靈先預覽、使用者處理完衝突再送開啟。

**權限：** 開啟、關閉、重新開啟、刪除都要同時通過 `IDesignerRequest`（設計器 `DYN00000` 的 Add；關閉與重新開啟用 Edit；刪除用 Delete）與 `ISystemAdminRequest`。原因：開啟會替群組授權，而路線文件與核心設計 §15 都已指出「設計器權限就能替自己提權」是未決風險，這個動作不該再放大它；建立群組本來就限系統管理員。預覽與列表只要設計器 Read。

**前置檢查**（任何一項失敗就整個回 409 或 400，什麼都不寫）：

1. 套件讀取與 §4.8 的前三層驗證。
2. `Dynamic_App.Key` 尚未存在（重複開啟，見下）。
3. 相依檢查（§7.2）。
4. 每個模組的最終 `Key`（預設值或 `moduleKeys` 覆寫）不與現有 `Module.Key` 衝突（`IModuleRepository.KeyExistsAsync`），也不與套件內其他模組衝突。
5. 擴充模組的 `hostEntityKey` 沒有被別的擴充模組佔用（`HostEntityExistsAsync`）。兩個應用程式不能擴充同一個既有實體，這是核心的既有限制。
6. 所有 `entity` 目標與擴充宿主都有對應的 `IReferenceProvider`（`ReferenceProviderRegistry.Get`）。
7. `groupMap` 的 `map` 指向存在的群組；`create` 的名稱不與現有群組同名（沿用 `CreateGroup` 的規則）。

**建立流程**：整個開啟是一個 `ITransactionalRequest`，任何一步回傳失敗或丟例外，`TransactionBehavior` 整批回滾，不留半個應用程式。

1. 新增 `Dynamic_App`（`Status = Open`）。
2. 處理 `groupMap` 的 `create`：呼叫現有 `CreateGroup` 的邏輯，`Key` 照該指令的規則自動產生（`g-` 加亂碼），群組代號只用在這次開啟，不存。
3. 重播設計器指令：重用 `TemplateInstantiator` 的步驟 1 到 9（建模組、區塊、欄位與選項、預設值、順序、標題欄位、規則、共用檢視），所有 id、`Field.Key`（`f_<id>`）、選項值（`opt_<id>`）、`ProgramCode`（`IProgramRepository.NextModuleProgramCodeAsync`）都由資料庫重新配發，**檔案裡沒有也不可能有任何 id**。
4. 權限：每個模組合併 `permissions[]`、`fieldAccess[]`、`optionScopes[]`，用對應後的群組 id 呼叫一次 `SaveModulePermissions`。
5. 依相依順序啟用所有模組（`ActivateModule`）；順序同 `DemoCatalog.ActivationOrder` 的規則——被參照、被彙總的先啟用。啟用會把 `Program.Active` 設為 true。套件的模組開啟後直接是 `Active`，沒有「開了但還是草稿」的中間狀態。
6. 寫入 `Dynamic_AppModule`。
7. 報表：`AppReportCodec` 把定義換回 id，用開啟者身分呼叫 `CreateReport`、`SetReportShares`，寫入 `Dynamic_AppReport`。
8. 回傳 201 與 `AppResponse`：應用程式、每個模組的代號、實際 key 與 id、報表 id、`warnings`。

**id 重新配發與 key 衝突**：檔案不含 id；`Module.Key` 預設用檔案的 `key`，衝突時**不自動加字尾**（自動改名會讓套件之間靠 key 的引用悄悄指到別的模組），而是在預覽與開啟回 409，列出 `{ 模組代號, key }`，由管理員用 `moduleKeys` 指定。模組代號（`AppModule.Code`）不受 key 改名影響，跨應用程式引用靠它（§7）。

**重複開啟與冪等**：

- `Dynamic_App.Key` 是唯一索引。同一個 `manifest.id` 已存在（不論 `Open` 或 `Closed`）回 409「應用程式已開啟」，並用 `ServiceResult.ConflictWith` 帶上既有應用程式的 `key`、`status`；`Closed` 時訊息提示改用重新開啟。網路重送的第二個請求因此不會產生第二份。
- 不支援「同一個套件開第二份」。需要並存的情況，由套件作者發行新的 `manifest.id`（§12 決策 2）。
- 兩個請求同時開啟：SQLite 序列化寫入，第二個會在步驟 2 的存在檢查或模組 key 檢查失敗而得到 409；換成 SQL Server 後唯一索引衝突要映射成 409（核心設計 §15 第 11 條是同一件事，目前會變成 500）。

**開啟後屬於客戶**：`Dynamic_App` 只是帳。執行期沒有任何程式讀取 `PackageVersion`，沒有「從套件更新」的 API（§12 決策 3）；客戶可以改任何模組的欄位、規則、權限、名稱，也可以走（階段 0 的）安全刪除刪掉單一模組。套件的新版本不會、也不能套用到已開啟的應用程式；`POST .../open` 對已開啟的 `manifest.id` 一律 409。

### 6.3 關閉

**請求**：`POST /api/v3/dynamic/apps/{key}/close`，body `{ "cascade": false }`。

1. 應用程式必須是 `Open`；已經 `Closed` 時回 200 且不變更（冪等，方便重送）。
2. 相依檢查（§7.3）：有其他 `Open` 的應用程式依賴它時，`cascade = false` 回 409 並列出依賴者；`cascade = true` 先依相依順序（依賴者在前）關閉它們再關閉自己。
3. 成員模組中 `Status = Active` 的改成 `Closed`，每個模組 `TouchVersion`（讓 schema 的 ETag 失效，前端重取到 `readOnly`）。`Program.Active` **不動**，仍是 true。
4. `Dynamic_App.Status = Closed`，換新 `RowVersion`，寫入 `ModifiedUserId`、`ModifiedTime`。
5. 資料、報表、檢視、權限列、`NumberSequence` 全部不動。

**關閉後停用的功能**（對 `Closed` 的模組）：

- 新增、修改、刪除記錄，含表頭明細一起存檔；匯入。
- 建立、修改、刪除清單檢視（個人與共用）。
- 在其他模組的參照欄位挑選它的記錄（`records/light`）；其他模組新寫入或改變指向它的參照值（`ReferenceValidator` 只對「有變動」的參照驗證，所以其他欄位的修改不受影響）。
- 單獨啟用或停用模組。
- 側邊欄的一般選單位置：`closed` 的項目改放在選單底部的「已關閉的應用程式」分組，仍可點進去，頁面顯示唯讀橫幅。

**關閉後仍可用的功能**：

- 列表、明細、反向關聯分頁、歷程、schema API（帶 `readOnly: true`）。
- 匯出 CSV 與 Excel（仍要 `PrintExport`）。
- 既有報表的執行、下鑽、匯出；報表目錄仍列出 `Closed` 模組（可以用歷史資料做分析，建立新報表也行）。
- 既有引用的顯示：別的模組的參照欄位繼續顯示標題，不會變成「無權限」（現況對非 `Active` 目標就是這樣，見 §14 第 1 點，所以要改）。
- 權限設定仍可修改（收回權限是安全動作）；設計器對欄位、規則的編輯與現在 `Inactive` 模組相同，不另設限制。
- **例外（實作時補，見 §15 第 6 點）：** 會連帶改寫既有記錄值的設計器變更在 `Closed` 一律擋（409），系統管理員也一樣：換記錄標題欄位、新增或改動公式與彙總（觸發重算）、重新啟用公式或彙總欄位、刪除仍有值的欄位或區塊。其餘定義修改照上一條放行。

**怎麼落實**（階段 1 要改的程式，逐項在 §10）：

- **集中的判定。** 新增 `ModuleStatusExtensions`：`IsReadable`（`Active` 或 `Closed`）、`IsWritable`（只有 `Active`）。現有 `Status: ModuleStatus.Active` 的每一處改成其中之一：
  - 改 `IsReadable`：`GetRecordHandler`、`GetPagedRecordsHandler`、`GetRecordByHostHandler`、`GetRecordHistoryHandler`、`ExportRecordsHandler`、`RelatedTabs`（兩處）、`GetModuleSchemaHandler`、`ModuleSchemaBuilder`、`ReferenceResolver`、`ReportRunner`、`ReportModuleLoader`、`GetReportCatalogHandler`（三處）、`GetViewsHandler`；另有 `GetMenu`、`GetReportMainModules` 走的 `IModuleRepository.ListActiveAsync`，實作時改名為 `ListReadableAsync`（原清單漏了）。
  - 維持只有 `Active`：`CreateRecordHandler`、`UpdateRecordHandler`、`DeleteRecordHandler`、`DetailWriter`（只對寫入端；讀取端改成 `ReadableDetailModulesAsync`）、`GetLightRecordsHandler`、`ReferenceValidator`、`ImportPlanner`（模組本身改用 `ModuleGate`；查參照標題那處維持 `Active`）、`CreateViewHandler`、`UpdateViewHandler`、`DeleteViewHandler`。其中寫入類處理器遇到 `Closed` 改回 409「應用程式已關閉，資料唯讀」，而不是 404。
  - `UpdateModuleHandler` 的「啟用中必須有標題欄位」判斷把 `Closed` 也算進去。
- **守門員。** `DynamicAccessGuard.AuthorizeAsync` 與 `AuthorizeDetailAsync` 在 `Resolve` 之前先判斷：`module.Status == Closed` 且動作不是 `Read` 或 `PrintExport` 就 `Deny`。必須在 `Resolve` 之前、而且不看使用者身分，因為 `PermissionResolver` 對系統管理員回傳所有動作；放在結果之後檢查就擋不到管理員。這是 handler 檢查之外的第二道防線。
- **`SeedData`。** 啟動時同步 `Program.Active` 的規則（`program.Active = 有 Active 模組`）改成「有 `Active` 或 `Closed` 模組」，否則重開機後關閉中應用程式的 `Program.Active` 會被設回 false，守門員會連讀取都拒絕。
- **選單。** `GetMenuHandler` 目前只用 `ListActiveAsync`；改成也取 `Closed` 的非明細模組，仍然要通過 `Read` 授權，回傳時 `MenuItem.Closed = true`。

### 6.4 重新開啟

`POST /api/v3/dynamic/apps/{key}/reopen`。

1. 應用程式必須是 `Closed`；已經 `Open` 回 200 不變更。
2. 相依檢查（§7.2）：它依賴的應用程式與模組都必須是 `Open` 且模組 `Active`，否則 409，列出缺的。
3. 成員模組中 `Status = Closed` 的改回 `Active`，`TouchVersion`；`Dynamic_App.Status = Open`。
4. 關閉時被 `cascade` 連帶關閉的依賴者**不會自動重新開啟**，管理員逐一重開。

資料與設定在關閉期間沒有被動過，所以重新開啟就是狀態翻回去，沒有任何重建或重算。

### 6.5 與安全刪除的關係

階段 0 的安全刪除已實作（`2026-11-06-safe-delete-design.md`），模組層級的規則是：只有**未啟用**（`Draft` 或 `Inactive`）、**沒有未軟刪除的記錄**、**未被引用**才可刪；只剩軟刪除記錄時，連同記錄、索引、歷程一起永久清除，呼叫端要帶上看過的筆數（`purged`）。判斷與清除的程式在：

- `DeletePlanner.PlanAsync(target, key, id)`：盤點所有引用來源（參照欄位、彙總、公式、預設帶表頭值、記錄標題、規則、檢視、報表），回傳 `DeletePlan`（檢查結果、模組 id、要刪的欄位與區塊 id）。
- `DeleteModuleHandler`：核對 `purged`、呼叫 `IDeletionRepository.PurgeModuleAsync`、移除模組，並驗事後條件。

應用程式層級的刪除**直接重用這兩個元件，不另寫判斷**。唯一要補的是：`DeletePlanner` 目前把「自己模組內部的引用」視為不擋，其他模組的引用一律擋；刪除整個應用程式時，成員模組彼此的引用也應視為內部。階段 1 要給 `PlanAsync` 加一個「一起刪除的模組鍵集合」參數，集合內的模組互相引用不算被引用，其餘行為不變。

- **`Closed` 的模組不能用模組層級的刪除。** 它屬於應用程式，不是「未啟用」。需要刪除時走應用程式層級的刪除。
- **應用程式層級的刪除**：`DELETE /api/v3/dynamic/apps/{key}`。必須是 `Closed`（`Open` 要先關閉，兩步是刻意的），並且對**每個成員模組**重新判斷：
  - 沒有未軟刪除的記錄（只剩軟刪除記錄的模組可刪，清除筆數照安全刪除的規則告知並核對）。
  - 未被應用程式外部引用（`DeletePlanner` 帶「一起刪除的模組鍵集合」；成員之間互相引用是內部的，同一個交易一起刪）。
  - 沒有其他應用程式依賴它（宣告的 `RequiresJson` 與實際的參照欄位都算，§7.3）。
  - 任一不成立整個回 409 並列出原因，什麼都不刪。
- 成立時同一個交易內：刪成員模組（連帶區塊、欄位、選項、規則、共用檢視、權限列、`NumberSequence`、`Program` 登錄）、`Dynamic_AppReport` 列出的報表（儀表板上線後，被儀表板引用的報表依該設計文件的規則擋下）、`Dynamic_App`（cascade 清 `Dynamic_AppModule`、`Dynamic_AppReport`）。**不刪**套件開啟時新建的群組（它們可能已經有成員與其他授權）。
- 實際刪除與清除模組的程式是階段 0 已有的 `IDeletionRepository.PurgeModuleAsync` 加 `IModuleRepository.Remove`；`DeleteModuleHandler` 的核對與事後條件（模組刪除後不可殘留記錄列，否則丟例外回滾）階段 1 要抽成共用方法給 `DeleteApp` 呼叫，兩邊的判斷條件必須是同一份程式。
- 成員模組被客戶單獨安全刪除後，`Dynamic_AppModule` 的列因 cascade 消失；之後該應用程式的匯出會少這個模組，別的套件若 `requires` 它，之後的開啟會因相依缺失失敗（預期行為）。

## 7. 跨應用程式引用

### 7.1 引用的種類

跨應用程式只允許兩種連結，且都只在**模組層級**：

1. 參照欄位的目標是另一個應用程式的模組（`refTarget.app`）。
2. 參照欄位的目標是既有實體（`entity`，與應用程式無關，列入 `requires.entities`）。

欄位、規則、公式、彙總、報表、檢視都**不能**引用另一個應用程式的欄位。理由是 §7.4。

### 7.2 開啟時的相依檢查

對 `requires.apps[]` 的每一項 `{ id, modules[] }`：

| 情況 | 結果 |
|---|---|
| 沒有 `Dynamic_App.Key = id` | 失敗，原因 `notInstalled` |
| 應用程式是 `Closed` | 失敗，原因 `closed`（提示先重新開啟） |
| 應用程式裡找不到 `AppModule.Code = <模組代號>`（客戶刪掉了） | 失敗，原因 `moduleMissing` |
| 該模組不是 `Active`（客戶自己停用） | 失敗，原因 `moduleNotActive` |
| 全部通過 | 取得「代號到實際 `Module.Key`」的對應，交給 `TemplateInstantiator` 把 `refTarget.app` 換成實際 key |

對 `requires.entities[]`：`ReferenceProviderRegistry.Get` 找不到就失敗，原因 `entityMissing`。

結果一律是整批 409，帶 `missingDependencies`（每項含種類、應用程式 id、模組代號、原因）。**沒有「先建、之後再補」的懸空參照**：不開啟依賴不到的應用程式，也不自動連帶開啟（§12 決策 5；要一次開多個應用程式靠階段 5 的開啟頁面，一個一個依相依順序開）。

之所以能這麼嚴格，是因為核心本來就不允許參照欄位指向不存在的模組（`FieldRules.IsValidRefTargetAsync` 要求目標存在、是 `Standalone`、不是明細），所以相依檢查只是把同一個限制提前、並讓錯誤訊息講人話。

### 7.3 關閉與刪除被依賴的應用程式、循環相依

**「誰依賴 A」的判定**（關閉與刪除共用）：應用程式 B（不是 A）是 `Open`，並且符合任一：

- B 的 `RequiresJson.apps[]` 含 A 的 `Key`（宣告的相依）。
- B 的成員模組中有 `Active` 的參照欄位，目標是 A 的成員模組（實際的相依；客戶自己加的參照也算）。

不屬於任何應用程式的模組（手動建立的）若有欄位參照 A 的模組：**不擋關閉**，在關閉的回應 `warnings` 列出，由客戶自己處理（§12 決策 6）。刪除則由安全刪除的「未被引用」擋下（它是硬性的，`DeletePlanner` 會列出引用來源）。

處理方式：關閉時有依賴者回 409 並列出，帶 `cascade: true` 可連帶關閉依賴者（遞移，依賴者先關）；刪除時有依賴者一律 409，沒有 cascade。重新開啟時 §6.4 已規定被依賴的應用程式必須 `Open`。

**循環相依**分兩種：

- **應用程式之間的 `requires` 不得循環。** A 要求 B、B 要求 A 永遠開不起來（誰都要等對方先開）。檢查三個時機：內建套件載入時（有循環就拒絕啟動，與 `TemplateCatalog.Load` 一致）、匯入時（把新套件加進「所有已知套件加上已開啟應用程式的 `RequiresJson`」的圖，有環就拒絕匯入並列出環）、開啟時不需要另外檢查（相依必須已經開啟，已隱含無環）。
- **同一個應用程式內的模組互相參照照舊允許**（例如客戶參照聯絡人、聯絡人參照客戶）。跨應用程式必須單向；需要「從客戶看到它的報價」時用現有的反向關聯分頁（核心設計 §9：明細頁替每個指向本模組的參照欄位開一個分頁），不必在被依賴的那一邊加欄位。

### 7.4 為什麼不允許跨應用程式引用欄位

欄位的穩定識別是 `Field.Id`，套件只有欄位代號，而代號開啟後**沒有存進資料庫**（`TemplateIdMap` 只存在於那次重播的記憶體）。若要跨應用程式引用欄位，就得在 `Dynamic_Field` 加代號欄位，並讓客戶改欄位時維持代號不變，這等於把套件格式滲進核心。模組層級只需要 `AppModule.Code`，成本低得多。階段 1 的 CRM 與報價、訂單的關係都只需要模組層級的參照，所以先不做；真的需要時另開設計（§13）。

## 8. 與現有單模組範本的關係

### 8.1 超集

封裝格式 v2 是範本格式 v1 的**超集**，不是取代：

- `modules[]` 的元素完全是 `TemplateModule`；v2 多出的是 `manifest`、`groups`、`fieldAccess`、`optionScopes`、`reports`、保留 section，以及 `refTarget.app`。
- 範本仍然是「模組藍本」：`GET /templates`、`POST /templates/{key}/instantiate`（建成**草稿**、沒有應用程式記帳、沒有關閉概念）、`GET /modules/{key}/export-template`、`POST /templates/import` 全部不變。四個內建範本（`order`、`repair`、`mold-care`、`quality`）與 Demo 產生器（`DemoStructure` 直接 `InstantiateTemplateRequest`）都不受影響。
- 應用程式是「帶生命週期的一組模組」：開啟即啟用、有狀態、有相依。兩者並存，路由分開（`/templates` 與 `/apps`）。

### 8.2 能重用的程式

| 現有程式 | 怎麼用 |
|---|---|
| `TemplateModule` 等 record（`TemplatePackage.cs`） | v2 `modules[]` 直接用 |
| `TemplateValidator.Validate` | 先用轉接器把 v2 套件包成一個 `TemplatePackage`（`FormatVersion = 1`、`Key = manifest.id`、`Modules = modules`）再呼叫，現有的格式、代號、公式、彙總、規則、循環依賴檢查原封不動；v2 的新檢查（§4.8）另寫在 `AppPackageValidator`。**注意：** 轉接器是為了不動現有驗證器而取的捷徑，且 `TemplateValidator` 的 `FormatVersion != 1` 檢查就是靠轉接器設成 1 才通過 |
| `TemplateGraph`（依賴、鎖定、計算順序） | 同上，用轉接後的套件；`excludeFields` 的連動規則靠它 |
| `TemplateInstantiator` | 重播步驟 1 到 9；步驟 10（權限）改成接受群組對應與 `fieldAccess`、`optionScopes`；`IdMap.Config` 對 `refTarget.app` 查外部模組對應。改動集中在三處，細節見 §10 |
| `TemplateExporter` | 應用程式匯出的模組部分；要補輸出 `fieldAccess`、`optionScopes`，並讓模組代號用 `AppModule.Code` 而不是 `Module.Key` |
| `TemplateJson.Options` | v2 另建一份 `AppJson.Options`（同樣的轉換器，但未知屬性拒絕），不改 v1 的選項，避免影響現有範本檔的讀取 |
| `TemplateCatalog.Load`、`TemplateSource` | 照著做 `AppCatalog`（讀 `apps/*.app.json`，有錯拒絕啟動）與 `AppPackageSource`（內建加匯入）；不共用類別，因為格式與存放表不同 |
| `ImportTemplateHandler` | 匯入流程（解析、驗證、key 已存在回 409、存原文）直接照抄到 `ImportAppPackageHandler` |
| `Report` 相關指令 | 報表開啟時走現有的 `CreateReport`、`SetReportShares`，不另寫驗證 |

### 8.3 現有範本升級成單模組應用程式

升級器 `AppPackageUpgrader.FromTemplate(TemplatePackage, IReadOnlyList<KnownPackage>)`：

1. `manifest.id = template.key`、`manifest.name = template.name`、`manifest.description = template.description`、`manifest.version = "1.0.0"`、`manifest.icon` 取第一個模組的 `icon`。
2. `modules` 原樣帶入（v1 的 `permissions[].group` 本來就是群組 `Key`，原樣當成 v2 的群組代號）。
3. `groups[]` 由所有出現過的群組代號組成，`name` 用代號當 `zh-TW`（升級器沒有更好的名稱；管理員在開啟精靈可以改成「對應到既有群組」，而 §6.2 的預設建議「現有群組的 `Key` 等於代號就對應」讓現有 Demo 群組 `sales`、`floor` 等開箱即用，行為與 v1 的「找得到就套用、找不到略過並警告」一致）。
4. v1 允許 `refTarget.key` 不在套件內（當成既有的 `Module.Key`）；v2 不允許，升級器在別的已知套件中找 `key` 相符的模組：找到就改寫成 `{"key":<模組代號>,"app":<該套件 id>}` 並補進 `requires.apps[]`，找不到就升級失敗並列出。例：內建的 `quality` 範本有欄位指向 `order`（不在 `quality` 內），升級後得到「`quality` 要求應用程式 `order` 的模組 `order`」。
5. 實體目標與擴充模組的宿主補進 `requires.entities[]`。
6. `reports`、`fieldAccess`、`optionScopes` 為空；保留 section 為空。

升級是記憶體內的轉換，不修改範本檔。`POST /apps/packages/import` 同時接受 v1 範本檔與 v2 套件（依 `formatVersion` 分派），所以現有的匯出檔與內建範本都可以不改直接當成應用程式開啟。是否把內建範本也升級成內建應用程式是階段 1 的產品決定（§13），不影響格式。

## 9. 鐵則檢核

- **ERP 邏輯不寫死：** 格式裡沒有任何業務概念；套件內容全部是模組、欄位、規則、權限、報表這些通用定義。程式裡不得出現任何內建套件的 id 或模組 key（內建套件只在 `apps/*.app.json` 檔案裡）。檢驗方式：階段 1 要同時有兩個不相干的套件通過同一套開啟、關閉、相依測試（CRM，以及測試專用的一個採購情境小套件，後者只放測試專案，不隨產品發行）。
- **不做執行期 DDL：** 開啟只寫固定結構的資料表（`Dynamic_*`）；新表只透過 EF migration 加入。
- **權限不 fail-open：**
  - 開啟需要設計器 Add 加系統管理員；套件不能建立使用者、不能設定個人權限、不能授權 `DYN00000`。
  - 沒對應的群組一律略過，不給任何人權限（`groupMap` 缺項視為 `skip`）。
  - 守門員對 `Closed` 先擋（§6.3），模組不存在或未開啟仍是 403／404。
  - 保留 section 非空整份拒絕（§4.6）；未知屬性整份拒絕（§4.8）。
- **驗證不只做在前端：** 所有檢查都在後端（驗證器與指令）；前端的預覽只是呈現同一個預覽 API 的結果。
- **空系統永遠是合法起點：** 新表全空是正常狀態；`GET /apps` 回空陣列；選單、首頁、報表、設計器都不查 `Dynamic_App`；`Module` 不加指向應用程式的欄位，所以沒有應用程式的模組與現在完全一樣。測試：全新資料庫、沒有任何應用程式時，現有全部測試照樣通過，並新增一個明確的 `FreshInstall_NoApps` 測試（§10）。
- **每階段可展示：** 這份設計本身不展示；階段 1 的驗收「應用程式關閉後，既有引用與資料仍可查閱」由 §10 的測試與 Demo 走查覆蓋。

## 10. 階段 1 worker 實作清單

只列，不實作。路徑以 `poc/backend/src/DynamicModules.*` 為基準。

**Domain**

- `Entities/Dynamic/{App,AppStatus,AppModule,AppReport,AppPackage}.cs`，`Data/Configurations/AppConfigurations.cs`（欄位與索引照 §5），migration `AppPackages`。
- `ModuleStatus.Closed`，新增 `ModuleStatusExtensions`（`IsReadable`、`IsWritable`）。
- `Repositories/Dynamic/IAppRepository.cs`（取得與列出應用程式、依 id 取成員、依模組取所屬應用程式、依參照欄位找依賴者、新增、刪除）、`IAppPackageRepository.cs`；Persistence 對應實作並在 `DependencyInjection.AddPersistenceRepositories` 註冊。

**Application：新增 `Dynamic/AppService/`**

- `Data/`：`AppPackage` record 家族（重用 `TemplateModule`）、`AppJson`（嚴格選項、重複屬性掃描、`x-` 處理、BOM、深度）、`AppPackageValidator`（§4.8 結構層與引用層）、`AppPackageUpgrader`（v1 到 v2，§8.3）、`AppPackageAdapter`（包成 `TemplatePackage`，§8.2）、`AppReportCodec`（報表定義代號與 id 互轉，含 `ReportSort.Key` 的重寫）、各 Response record。
- `AppCatalog`（讀 `poc/backend/apps/*.app.json`，有錯拒絕啟動，含 `requires` 循環檢查）、`AppPackageSource`、`AppDependencyChecker`（§7.2 的檢查與 §7.3 的依賴者查詢）。
- Commands：`ImportAppPackage`、`OpenApp`、`CloseApp`、`ReopenApp`、`DeleteApp`（皆 `ITransactionalRequest`，並依 §6.2 的權限規則同時實作 `IDesignerRequest` 與 `ISystemAdminRequest`）。
- Queries：`GetAppPackages`、`PreviewOpenApp`、`GetApps`、`GetApp`（含依賴者）、`ExportApp`。

**Application：修改既有**

- `TemplateInstantiator`：(a) 權限步驟接受「群組代號到群組 id」的解析器與 `fieldAccess`、`optionScopes`；(b) `IdMap.Config` 解析 `refTarget.app`；(c) 回傳的 `TemplateIdMap` 已有欄位對照，開啟者用它建 `AppReportCodec` 的對照。現有範本流程的行為與測試不得改變。
- `TemplateExporter`：輸出 `fieldAccess`、`optionScopes`；模組代號可由呼叫端指定。
- 所有 `ModuleStatus.Active` 判斷依 §6.3 的清單分類修改；寫入類處理器回 409；`DynamicAccessGuard` 兩個方法加 `Closed` 守門；`GetMenuHandler` 加 `Closed`；`ModuleSchemaResponse.ReadOnly`；`UpdateModuleHandler`；`ActivateModuleHandler`、`DeactivateModuleHandler` 擋 `Closed`。
- `DeletePlanner` 加「一起刪除的模組鍵集合」參數，並把 `DeleteModuleHandler` 的核對與事後條件抽成共用方法（§6.5）；`DeleteApp` 直接呼叫。

**Persistence**

- `SeedData.EnsureSeededAsync` 的 `Program.Active` 同步改成「有 `Active` 或 `Closed` 模組」。

**WebAPI**：新增 `Controllers/v3/Dynamic/AppsController.cs`，路由都在 `/api/v3/dynamic` 之下（`EndpointRoutesTests` 要求）：

- `GET apps/packages`、`POST apps/packages/import`（`RequestSizeLimit(1024 * 1024)`，413 以下）、`GET apps/packages/{id}`
- `POST apps/packages/{id}/preview`、`POST apps/packages/{id}/open`
- `GET apps`、`GET apps/{key}`、`POST apps/{key}/close`、`POST apps/{key}/reopen`、`DELETE apps/{key}`、`GET apps/{key}/export`
- 內建套件放 `poc/backend/apps/*.app.json`，`DynamicModules.WebAPI.csproj` 比照 `templates` 複製到輸出目錄；`app.schema.json` 放同處。

**前端**（`poc/frontend/src`）

- 路由 `system/apps`（管理頁），頁面 `pages/system/AppsPage.vue`：套件目錄與已開啟的應用程式兩個清單、匯入檔案、開啟精靈（預覽、key 衝突改名、群組對應、取消勾選欄位）、關閉（含依賴者確認與 cascade 選項）、重新開啟、刪除（先顯示不可刪的原因）、匯出。
- `domain/dynamic/menu-nav.ts` 的 `systemLinks` 加入口（`me.designer.read` 且 `isSystemAdmin`）；`MainLayout.vue` 選單底部的「已關閉的應用程式」分組；`HomePage.vue` 同步顯示。
- 唯讀橫幅與按鈕隱藏：`ListPage`、`DetailPage`、`FormPage` 讀 schema 的 `readOnly`；現有的按鈕權限判斷旁加一個條件，不另寫頁面。
- 四個語系（`zh-TW`、`en-US`、`th`、`vi`）的新字串。

**測試**（xUnit 與 vitest；後端用現有 `TestApp`、`DesignerTestBase`）

- 格式：往返（匯出再匯入結構相同）、v1 升級（四個內建範本全數升級成功，`quality` 得到對 `order` 的相依）、`formatVersion` 為 3 被拒、未知屬性與 `config` 未知鍵被拒、重複屬性被拒、BOM、非 UTF-8、超過大小與數量上限、保留 section 非空被拒、不支援的語系鍵被拒。
- 開啟：成功建立全部物件且 `Program.Active` 為 true；任一步驟失敗整批回滾（在中途注入失敗，確認沒有留下 `Dynamic_App`、模組、群組）；id 不來自檔案；key 衝突 409 並列出；擴充宿主衝突 409；`groupMap` 三種模式與缺項略過；重複開啟 409 且帶既有應用程式；報表與分享；`fieldAccess`、`optionScopes` 落地。
- 關閉：每個停用功能各一個測試（新增、修改、刪除、匯入、建檢視、light 挑選、新參照值）；每個仍可用功能各一個（列表、明細、歷程、匯出、既有報表、既有引用顯示標題而不是無權限）；**系統管理員同樣不能寫入**；重啟（`SeedData`）後 `Program.Active` 仍為 true；已關閉再關閉、已開啟再重開皆冪等；重新開啟後資料完整可編輯；`Draft`、`Inactive` 的成員不被重新啟用。
- 相依：缺少、已關閉、模組缺失、模組停用各自失敗；`requires` 與實際引用不符的驗證；關閉被依賴者 409 與 cascade；循環 `requires` 被拒（內建載入與匯入）；重新開啟時相依未開啟被拒。
- 刪除：開啟中不能刪；有資料不能刪；被外部引用不能刪；成立時全部清乾淨且保留新建群組。
- 空系統：`FreshInstall_NoApps`（全新資料庫：`GET /apps` 為空、選單、首頁、報表、設計器照常）；一個測試確認原始碼不含內建套件 id。
- 兩個不相干套件（CRM 與測試用採購小套件）都走完開啟、跨套件參照、關閉、重新開啟。
- 前端：開啟精靈的衝突與對應、唯讀頁面按鈕隱藏、關閉分組、系統管理入口只給管理員。
- 實機走查：Demo 模式全新資料庫，開啟 CRM、開啟依賴它的第二個應用程式、關閉第一個（被擋、cascade）、關閉後查閱資料與報表、重新開啟。沒走到的標「未實機驗證」。

## 11. 不在範圍內

- 應用程式更新、版本升級、三方合併、套件之間的差異比較（路線：新版不自動合併）。
- 同一套件開兩份；可選相依；自動連帶開啟相依應用程式。
- 套件攜帶資料（種子資料、範例資料）；`NumberSequence` 初始值。
- 套件簽章、來源信任、線上市集或下載。
- 跨應用程式引用欄位、規則、公式、彙總或報表欄位（§7.4）。
- 儀表板、狀態機、單據轉換、事件、列印範本的格式（只保留 section）。
- 使用者、群組成員、個人權限、外觀設定。
- 選單排序、選單分區的多語系（沿用核心設計 §15 第 45 條的限制）。
- 備份還原、應用程式開啟頁面的產品化（階段 5）。
- 在階段 1 把四個內建範本升級成內建應用程式（產品決定，§13）。

## 12. 決策記錄

這些是我代為決定的，使用者核准這份文件時請一併確認。每項寫決策、理由、錯了的代價。

1. **新增 `ModuleStatus.Closed`，而不是用旗標或讓關閉等同 `Inactive`。**
   - 理由：現況 `Status != Active` 時所有讀寫都 404，且參照目標非 `Active` 時顯示成「無權限」，讀不到資料，達不到「關閉後仍可查閱」。新列舉值讓約 30 處判斷逐一分成「可讀」與「可寫」，漏改的結果是 404（fail-closed）。旗標方案則漏改寫入處等於寫入放行（fail-open）。
   - 代價：要逐一修改約 30 處並補測試；若使用者想要別的語意（例如關閉後仍能新增），改的是同一批判斷點。
2. **同一個 `manifest.id` 只能開一份，`Dynamic_App.Key` 唯一。**
   - 理由：相依靠 id 解析，並存兩份會讓「誰是 `crm-lite`」不明確；也省掉每個物件的改名。
   - 代價：想並存兩個版本的客戶，要靠發行者換新的 `id`。這跟「新版不自動合併」放在一起，客戶沒有「把舊的換成新的」的路徑（見 §13 第 1 題）。
3. **沒有任何「用新版套件更新已開啟應用程式」的 API，`PackageVersion` 只是資訊。**
   - 理由：路線已定新版不自動合併；留一個半套的更新入口會讓人以為有支援。
   - 代價：之後若要做升級，是新設計，不是這份格式的延伸。
4. **關閉時只把 `Active` 的成員模組改 `Closed`；`Draft`、`Inactive` 保持原狀。**
   - 理由：這些是客戶自己決定的狀態，重新開啟不該替客戶啟用他停用的東西。
   - 代價：重新開啟後，客戶可能還要手動啟用自己曾停用的模組；若他以為「一鍵還原」會覺得少了東西。
5. **相依一律是硬相依，缺少就整批 409，不自動連帶開啟。**
   - 理由：可選相依與自動開啟都會讓開啟的結果取決於當下裝了什麼，不好測也不好說明；階段 5 的開啟頁面再處理一次開多個。
   - 代價：開 CRM 之外的應用程式要照順序一個個開，錯誤訊息要寫得夠清楚。
6. **關閉時，不屬於任何應用程式的模組參照被關閉的應用程式：只警告，不擋。**
   - 理由：那些是客戶自己手動建立的東西，阻擋會讓應用程式關不掉；刪除則由「未被引用」硬性擋住。
   - 代價：客戶自建模組的新增資料可能因為參照值挑不到而卡住，要到發生才知道（警告可能被忽略）。
7. **開啟、關閉、重新開啟、刪除都要求系統管理員，再加設計器對應動作軸。**
   - 理由：開啟會授權；設計器權限能替自己提權的風險（核心設計 §15 的「上線前必做」）已經存在，不該再放大。
   - 代價：被授予設計器 Add 但不是系統管理員的人，連開啟都做不到；要放寬時改兩個請求的宣告即可。
8. **群組在套件裡用代號，開啟時由 `groupMap` 對應；沒列出的視為略過。**
   - 理由：`CreateGroup` 的 `Key` 是自動產生的亂碼，套件不能指望群組 `Key` 存在；缺項略過是 fail-closed。
   - 代價：管理員忘了對應就開出沒有任何人能用的應用程式，要事後到權限頁補（開啟的回應會列出略過的群組）。
9. **module key 衝突不自動改名，回 409 要求管理員指定。**
   - 理由：自動加字尾會讓依賴 key 的東西（報表、跨套件引用、客戶的書籤）悄悄指到別的模組。
   - 代價：開啟精靈多一步；內建套件與客戶現有模組撞名的客戶會遇到。
10. **跨應用程式只允許模組層級的參照，且 `AppModule.Code` 存進資料庫；欄位代號不存。**
    - 理由：§7.4；`Field` 不為套件加欄位，核心保持乾淨。
    - 代價：報表、公式、彙總不能引用另一個應用程式的欄位，真的需要時要另外設計（可能得在 `Field` 加代號）。
11. **應用程式與物件的關係放三張關聯表，不在 `Module`、`Report` 加欄位。**
    - 理由：沒有應用程式的模組與現在完全相同，空系統不受影響。
    - 代價：「某模組屬於哪個應用程式」要多一個 join。
12. **v2 讀取器對未知屬性與 `config` 未知鍵一律拒絕；只有 `x-` 開頭的屬性會被丟掉。**
    - 理由：權限與控管內容被靜默忽略比讀取失敗危險；v1 對 `config` 未知鍵靜默忽略，拼錯鍵不會被發現。
    - 代價：每次新增格式內容都要提高 `formatVersion`（除了 `x-` 附註），舊版系統打不開新檔案。
13. **保留 section 在格式 2 只接受空陣列，非空整份拒絕。**
    - 理由：§4.6；階段 1 才能決定這些 section 的形狀。
    - 代價：階段 1 一旦定義 `workflows` 等就要提高 `formatVersion` 到 3，v2 檔案要走升級器。
14. **匯入的套件存新表 `Dynamic_AppPackage`，不重用 `Dynamic_ModuleTemplate`。**
    - 理由：`TemplateSource.ListAsync` 會把表中每一列當成 v1 範本解析；混放會讓範本清單出現壞資料。
    - 代價：多一張表與一組 repository；與範本一樣，匯入的套件不能刪除或覆蓋（核心設計 §15 第 48 條的限制照搬）。
15. **開啟後套件原文不保留；匯出由目前的模組重新產生。**
    - 理由：開啟後內容屬於客戶，保留原文只會變成誤導的「原始版本」。
    - 代價：看不到「客戶改了什麼」。匯出的 `manifest.version` 沿用開啟時的值，不反映客戶的修改。
16. **用轉接器把 v2 的 `modules` 包成 `TemplatePackage` 交給既有的 `TemplateValidator`、`TemplateGraph`，不先重構它們。**
    - 理由：把改動降到最小，現有範本測試不受影響。
    - 代價：轉接器是暫時的捷徑；之後若兩種格式分歧，要拆成共用的模組驗證層。
17. **檔案大小上限 1 MiB，與範本匯入相同；數量上限（模組 30、欄位 300 等）是我估的。**
    - 理由：對齊現有限制；數量上限用來限制單一交易重播的指令數（訂單範本約 60 次）。
    - 代價：七個應用程式中最大的（ERP 類）若超過會需要拆分或調高；調整只是改常數與 schema。
18. **關閉的模組在選單底部的「已關閉的應用程式」分組仍可進入，而不是從選單消失。**
    - 理由：「關閉後仍可查閱」需要有入口；只靠報表與引用連結太隱蔽。
    - 代價：選單多一個分組；若使用者希望關閉就收起來，改成只留在應用程式管理頁。
19. **關閉的模組不允許建立、修改、刪除清單檢視，但允許讀取。**
    - 理由：檢視是資料的一部分設定，關閉等於凍結；`GetViewsHandler` 是讀取。
    - 代價：關閉期間不能存個人檢視，極少影響。
20. **開啟時模組一律啟用，沒有「開啟但保持草稿」的選項。**
    - 理由：少一個狀態與一個請求參數；套件本來就是成品。
    - 代價：客戶想先看看再啟用做不到；可以開了之後單獨停用個別模組。

## 13. 待使用者裁決

1. **客戶怎麼拿到套件的新版？** 路線已定「新版不自動合併」，本設計因此沒有任何更新入口，同一個 `id` 開了就不能再開（決策 2、3）。結果是：客戶已開啟舊版後，沒有任何官方途徑拿到新版內容，除非發行者換新的 `id`（這時兩份各自獨立，資料要客戶自己用匯出與匯入搬）。請確認這是預期的，還是要在階段 1 或之後加「另開新版為新的應用程式」之類的輔助。我傾向維持現狀：階段 1 只有一個內建的 CRM，這個問題不急，但越晚決定、客戶資料越多。
2. **內建的四個範本（訂單、報修、模具保養、品質異常）是否在階段 1 升級成內建應用程式？** 格式已支援（§8.3），而且 `quality` 對 `order` 的依賴剛好是現成的跨應用程式案例；不升級則只有 CRM 一個內建應用程式可以驗證跨應用程式行為（測試專用的小套件仍然可以）。這是產品範圍，不影響格式。
3. **關閉被依賴的應用程式時，自建（非應用程式）模組的參照只警告不擋（決策 6）。** 若要改成也擋，需要決定客戶怎麼才能關閉它（先刪掉自建模組的欄位？），請確認。

## 14. 讀程式碼時發現的問題

以下是對照 `feature/stage0` 分支（含已提交的安全刪除）時看到的，與這份設計相關。

1. **非 `Active` 的模組現在完全不能查閱。** 約 30 處（§6.3 逐項列出）用 `Status: ModuleStatus.Active` 判斷，其他狀態回 404；`ReferenceResolver` 對非 `Active` 的目標回 `Restricted`（顯示成「無權限」）；`ReportRunner`、`ReportModuleLoader` 同樣只認 `Active`。所以「關閉後仍可查閱」目前做不到，路線文件的階段 1 驗收項目依賴這次修改。
2. **模組層級的刪除已由階段 0 實作，但一次只認單一模組。** `DeletePlanner` 對「一起刪除的多個模組」沒有概念，成員模組彼此的參照欄位會被當成外部引用而擋下；§6.5 已列出階段 1 要補的參數。報表引用也在盤點內：應用程式自己的報表（`Dynamic_AppReport`）也會被盤點成引用來源而擋下成員模組：階段 1 要讓 `PlanAsync` 同時接受「一起刪除的報表」並排除它們，或在同一交易內先刪報表並存檔再規劃，兩種擇一。
3. **`TemplateValidator` 對模組參照目標不做存在性檢查。** 它只檢查彙總來源與公式引用；`refTarget` 的 `key` 是否在套件內、是否存在、`entity` 目標是否登錄，都等到開啟（重播 `AddField`）才由 `IsValidRefTargetAsync` 擋，整批回滾。內建的 `quality` 範本就有欄位指向套件外的 `order`（以字面 `Module.Key` 引用），沒有任何機制宣告這個相依。v2 的 `requires` 補的就是這件事。
4. **`TemplateValidator` 的 `HasZhTw` 只檢查 `zh-TW` 非空，不擋不支援的語系鍵**；`I18nText.IsValid` 才擋，要到 `CreateModule` 才會失敗。範本的 `config` 也不檢查未知鍵（`FieldConfig` 反序列化用寬鬆選項，未知鍵靜默忽略）。核心設計 §15 第 51 條已記錄「不檢查型別設定」，這兩點是同類。
5. **`SeedData.EnsureSeededAsync` 每次啟動把 `Program.Active` 重設成「有 `Active` 模組」。** 若只新增 `Closed` 而不改這段，重開機會讓關閉的應用程式連讀取都被守門員拒絕。
6. **`PermissionResolver` 對系統管理員回傳所有動作。** 所以 `Closed` 的寫入限制不能只靠 `EffectivePermissions.Can`，必須在守門員 `Resolve` 之前判斷（§6.3）。
7. **現有匯出（`TemplateExporter`）不輸出欄位隱藏、唯讀與選項範圍**（核心設計 §15 第 47 條），應用程式匯出必須補上，否則「匯出再匯入」會悄悄丟掉權限限制。
8. **兩個內建範本的模組 `key` 與 `code` 不同**（`order` 範本的 `line` 模組 key 是 `order-line`），`TemplateExporter` 目前匯出時代號等於 `Module.Key`；若不加 `AppModule.Code` 的對應，匯出後再匯入的應用程式模組代號會改變，別的套件靠代號引用就會對不上（§10 `TemplateExporter` 修改項）。
9. **唯一索引衝突目前會變成 500。** 核心設計 §15 第 11 條已記錄；應用程式開啟會同時建立多個唯一鍵（`Dynamic_App.Key`、各模組 `Key`），上線前這條必須處理，POC 靠 SQLite 序列化寫入才不會出事。
10. **`TemplateInstantiator` 建出來的模組是 `Draft`，`CreateViewRequest` 又只接受 `Active` 模組**，所以它自己重做了一份檢視驗證直接寫入（`CreateViewAsync`）。應用程式開啟沿用這段，不需要先啟用再建檢視；但這表示啟用步驟必須排在步驟 9 之後（§6.2 步驟 5）。

## 15. 實作更正與未做項目（階段 1a，2026-11-06）

階段 1a 對照程式碼實作時，這份設計有下列出入；以此節為準，上文未逐處改寫的以此節覆蓋。

**更正**

1. **儀表板已存在。** §3 表格原寫「目前分支沒有」，實際已有 `/dashboards`、`/system/dashboards` 與首頁儀表板。格式 2 的 `dashboards` 仍是保留 section（只收空陣列），所以對封裝沒有影響，但刪除應用程式時「被儀表板引用的報表」要等報表納入封裝後才會碰到。
2. **`Closed` 不需要自己的 migration**（見 §5）。
3. **§6.3 的清單漏了 `IModuleRepository.ListActiveAsync` 的呼叫端**（`GetMenu`、`GetReportMainModules`）。若不改，關閉的模組會從選單消失、報表主模組選不到。
4. **§4.10 範例含 `reports` 項目，但階段 1a 沒有實作 `reports`**；`reports` 與 `AppReport` 一併留待之後，格式 2 目前遇到非空 `reports` 與保留 section 一樣整份拒絕。測試套件用的是去掉 `reports` 的版本。
6. **「設計器編輯與 Inactive 模組相同」漏了會改寫既有記錄值的路徑。** 換標題欄位會重寫全部記錄的 `Title`；公式與彙總的新增、變更、重新啟用會重算並寫回記錄值（還會連帶重算以它為來源的彙總父模組，父模組若是 `Closed` 也算）；刪除仍有值的欄位或區塊會從記錄、索引與歷程拿掉值。這些在 `Closed` 擋下（`ClosedDataGuard`）。權限、規則、區塊、選項、欄位標籤與一般設定的修改，以及沒有值要清的刪除，照規格放行。
5. **`ModuleSchemaResponse.readOnly` 之外，`permissions.add／edit／delete` 在 Closed 時由後端直接給 `false`**，前端只要照 `permissions` 隱藏按鈕，`readOnly` 只用來顯示「已關閉」提示。

**未做（階段 1a 範圍外或未到）**

- `ExportApp`、`TemplateExporter` 一般化（應用程式匯出，§10）。
- `reports[]`、`AppReport`、`AppReportCodec`（見上第 4 點）。
- 內建的 CRM 應用程式內容（本階段只做機制，目錄 `apps/` 沒有任何內建套件）。
- `apps/app.schema.json` 只涵蓋最上層結構，模組內部仍以 `TemplateValidator` 與 `AppPackageValidator` 為準。
- 應用程式管理頁只做到匯入、開啟（先預覽）、關閉（含連帶）、重新開啟；沒有刪除、群組對應的手動調整、模組鍵改名的介面（API 都支援）。

**§13 三個待裁決項，階段 1a 先代為決定**（使用者可推翻）：客戶沒有更新入口（維持文件設計）、內建四個範本不升級成內建應用程式（測試用小套件替代）、關閉被依賴的應用程式時自建模組的參照只警告。
