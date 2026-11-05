# vtiger CRM 動態模組與自訂欄位設計研究

調查日 2026-11-03。來源：`github.com/vtiger-crm/vtigercrm` 分支 `7.4`（commit `27225d1`，`vtigerversion.php` 為 7.4.0），只讀原始碼，沒有架站實測。
文中路徑都相對於 vtiger 原始碼根目錄。信心度：**C**=Confirmed（讀到程式碼）、**L**=Likely（由程式碼推論或業界常識）、**U**=Unknown。

目標情境：**新增一個功能（模組）→ 在上面新增自訂欄位 → 系統自動長出這個功能的列表、新增、編輯、明細、查詢、權限**，不用為每個功能寫程式。

## 0. 先講結論

1. **vtiger 做得到這件事，靠三個機制疊起來**：
   - 模組與欄位都是 metadata（`vtiger_tab`、`vtiger_blocks`、`vtiger_field`）。
   - 每個模組有自己的基底表與擴充表，新增欄位時直接對擴充表 `ALTER TABLE ADD COLUMN`。
   - 畫面與動作的類別解析有 fallback：模組沒有自己的 `List`／`Edit`／`Detail`／`Save` 類別，就用通用的 `Vtiger_*` 類別。這些通用類別全部照 metadata 運作。（C）
2. **「新增模組」本身也是一段程式流程，不是純設定**：寫 `vtiger_tab`、建兩張表、建區塊與欄位、設定記錄標題欄位、建預設清單檢視、展開權限、註冊 webservice、從樣板產生一個 PHP 類別檔、掛選單。開源版 7.4 沒有建模組的畫面，只有命令列工具 `vtlib/tools/console.php` 和匯入模組套件。（C）
3. **型別決定行為**：欄位型別對應到一個 UIType 類別，負責編輯元件、顯示格式、存入格式；找不到就退回基底類別。這是最值得搬的設計。型別本身卻用 `uitype` 數字、`typeofdata` 字串、依欄位名稱的特例三套編碼，這一點不要照抄。（C）
4. **權限三層、全展開**：模組層 `vtiger_profile2tab`、動作層 `vtiger_profile2standardpermissions`、欄位層 `vtiger_profile2field`。每建一個模組或欄位，就替每個 profile 各寫一列。（C）
5. **痛點集中在刪除與特例**：
   - 刪欄位要清掉一堆用字串拼起來的參照，格式像「`表:欄:欄名:模組_標籤:型別`」。
   - 每個下拉欄位各開一張值表。
   - 驗證規則依欄位名稱寫死。
   - 刪欄位直接 `DROP COLUMN`，資料救不回來。（C）

---

## 1. 資料模型

### 1.1 metadata 表

| 表 | 角色 |
|---|---|
| `vtiger_tab` | 模組（功能） |
| `vtiger_entityname` | 模組的記錄標題欄位 |
| `vtiger_blocks` | 版面區塊（欄位分組） |
| `vtiger_field` | **欄位定義（核心）** |
| `vtiger_customview`、`vtiger_cvcolumnlist` | 清單檢視（篩選器）與其欄位 |
| `vtiger_profile2tab`、`vtiger_profile2standardpermissions` | 每個 profile 對模組、對動作的權限 |
| `vtiger_def_org_field`、`vtiger_profile2field` | 欄位權限：組織預設、每個 profile |
| `vtiger_ws_fieldtype` | uitype → 語意型別 |
| `vtiger_fieldmodulerel` | 參照欄位可以指向哪些模組 |
| `vtiger_picklist`、`vtiger_<欄名>`、`vtiger_role2picklist` | 下拉選單 |
| `vtiger_picklist_dependency` | 下拉連動 |

`vtiger_tab` 的關鍵欄位：`tabid`、`name`、`tablabel`、`presence`（啟用與否）、`isentitytype`（是否為記錄型模組）、`customized`（1＝自訂模組）、`parent`（掛在哪個選單）。

`vtiger_field` 的欄位（`schema/DatabaseSchema.xml`）：

| 欄位 | 意義 |
|---|---|
| `fieldid` | 主鍵；自訂欄位的實體欄名就是 `cf_<fieldid>` |
| `tabid`、`block`、`sequence` | 所屬模組、區塊、排序 |
| `tablename`、`columnname` | **值存在哪張表的哪一欄** |
| `fieldname`、`fieldlabel` | 程式用名稱、畫面標籤 |
| `generatedtype` | 1 標準、2 自訂 |
| `uitype` | UI 型別代碼 |
| `typeofdata` | 驗證字串，如 `V~O~LE~100` |
| `presence` | 0 一律啟用、2 啟用可關、1 停用 |
| `displaytype` | 在哪些畫面出現 |
| `readonly`、`defaultvalue`、`maximumlength` | 唯讀、預設值、長度 |
| `quickcreate`、`masseditable`、`summaryfield`、`headerfield` | 快速新增、大量編輯、摘要、表頭 |

幾個欄位的行為補充：

- `generatedtype`：`isCustomField()` 只看這個欄位（`modules/Vtiger/models/Field.php:1239`）。核心欄位與自訂欄位在執行期沒有其他差別。
- `presence`：程式判斷啟用用的是 `presence IN (0,2)`（`Field.php:1247`）；Layout Editor 對 `presence=0` 的欄位不提供停用選項（`modules/Settings/LayoutEditor/models/Field.php:163`）。
- `displaytype`：可編輯要求值為 1 或 5（`Field.php:426`），值為 4 時不顯示（`Field.php:345`）。其他值的完整語意這次沒有逐一驗證（L）。
- `typeofdata` 的格式是 `型別~必填~比較子~長度`；必填不是獨立欄位，而是第二段的 `O`／`M`（`Field.php:1226`）。

### 1.2 值怎麼存：共用身分表 + 模組基底表 + 1:1 擴充表

每個模組在自己的類別上宣告用哪些表，例如 `modules/Accounts/Accounts.php:27-33`：

```php
var $tab_name = Array('vtiger_crmentity','vtiger_account','vtiger_accountbillads','vtiger_accountshipads','vtiger_accountscf');
var $tab_name_index = Array('vtiger_crmentity'=>'crmid','vtiger_account'=>'accountid', ... ,'vtiger_accountscf'=>'accountid');
var $customFieldTable = Array('vtiger_accountscf', 'accountid');
```

- `vtiger_crmentity`：**所有模組的記錄共用**的身分表，存 id、擁有者、建立／修改時間、刪除旗標。所以每筆記錄的 id 全系統唯一，跨模組關聯不用帶模組名稱。
- `vtiger_account`：模組的核心欄位。
- `vtiger_accountscf`：**自訂欄位專用的 1:1 擴充表**，一開始只有主鍵，之後每加一個自訂欄位就多一欄。（C）

這樣做的好處是核心表結構穩定，自訂欄位與系統升級隔離。代價是每個自訂欄位都是一次線上 DDL；欄位一多，會撞到資料庫的單列大小上限（MySQL／InnoDB 寬表常見 `Row size too large` 錯誤，L，本次沒有實測）。

### 1.3 型別：使用者可新增的 15 種

`Settings_LayoutEditor_Module_Model::getAddSupportedFieldTypes()`（`modules/Settings/LayoutEditor/models/Module.php:91`）列出使用者能新增的型別，`getTypeDetailsForAddField()`（同檔 `:211`）把每種型別翻成 uitype、typeofdata、實體欄型別三件事：

| 型別 | uitype | typeofdata | 實體欄型別 |
|---|---|---|---|
| Text | 1 | `V~O~LE~<長度>` | `VARCHAR(<長度>)` |
| Decimal | 7 | `NN~O` | `NUMERIC(長度+小數+1, 小數)` |
| Integer | 7 | `I~O` | `INTEGER(n)`／`BIGINT(n)` |
| Percent | 9 | `N~O~2~2` | `NUMERIC(5,2)` |
| Currency | 71 | `N~O` | `NUMERIC(長度+小數, 小數)` |
| Date | 5 | `D~O` | `DATE` |
| Time | 14 | `T~O` | `TIME` |
| Email | 13 | `E~O` | `VARCHAR(50)` |
| Phone | 11 | `V~O` | `VARCHAR(30)` |
| URL | 17 | `V~O` | `VARCHAR(255)` |
| Skype | 85 | `V~O` | `VARCHAR(255)` |
| Checkbox | 56 | `C~O` | `VARCHAR(3)` |
| TextArea | 21 | `V~O` | `TEXT` |
| Picklist | 16／15 | `V~O` | `VARCHAR(255)` |
| MultiSelectCombo | 33 | `V~O` | `TEXT` |

Integer 長度超過 10 改用 `BIGINT`。Picklist 勾選「依角色限定」時用 uitype 15，否則 16。多選的值以 ` |##| ` 串接成一個字串存放。

系統內建模組還用到更多 uitype，像是 2（必填的記錄標題）、53（擁有者）、70（系統時間）、10（參照其他模組的記錄）；這些不開放給使用者在 Layout Editor 新增。

語意型別的解析順序（`include/Webservices/WebserviceField.php:230`）：

1. 先查 `vtiger_ws_fieldtype`，把 uitype 對到 `picklist`、`currency`、`boolean`、`reference` 等型別；種子資料在 `modules/Users/DefaultDataPopulator.php:2227`。
2. 查不到就退回 `typeofdata` 第一段（`:300`）：`D`→date、`N`/`NN`→double、`I`→integer、`E`→email、`T`→time，其他都當 string。
3. `Vtiger_Field_Model::getFieldDataType()`（`modules/Vtiger/models/Field.php:146`）在上面再疊一層 uitype 特例，甚至依欄位名稱判斷，例如 uitype 55 且名稱含 `firstname` 就當 salutation。

### 1.4 下拉選單

`vtlib/Vtiger/Field.php:35` `setPicklistValues()` 的做法：

- **每個下拉欄位一張值表** `vtiger_<欄名>`，欄位有 id、值、`presence`、`picklist_valueid`、`sortorderid`、`color`，並在 `vtiger_picklist` 登記。
- uitype 15（依角色限定）會把每個值對**所有角色**各寫一列 `vtiger_role2picklist`，之後再依角色收掉；uitype 16 不分角色。
- 記錄上存的是**值的文字本身**，不是值的 id。改選項文字要回頭更新既有資料（L，沒有追改名的流程）。
- 連動規則存 `vtiger_picklist_dependency`，用 `sourcefield` + `sourcevalue` 對到 `targetvalues`。

### 1.5 權限

**模組與動作**：建模組時 `Vtiger_Profile::initForModule()`（`vtlib/Vtiger/Profile.php:126`）替每個 profile 寫一列 `vtiger_profile2tab`，再對每個動作（檢視、新增、編輯、刪除…）各寫一列 `vtiger_profile2standardpermissions`。有分享設定的實體模組，另外由 `Vtiger_Access::initSharing()`（`vtlib/Vtiger/Access.php:69`）寫 `vtiger_org_share_action2tab`。

**欄位**：讀取權限的判斷在 `Field.php:1148` `getPermissions()` → `preFetchModuleFieldPermission()`：

```sql
vtiger_field.presence IN (0,2)
AND vtiger_def_org_field.visible = 0
AND vtiger_profile2field.visible = 0  -- 使用者任一 profile
-- 要寫入再加 vtiger_profile2field.readonly = 0
```

有全域讀取權限的使用者直接放行。新增欄位時 `Vtiger_Profile::initForField()`（`Profile.php:81`）寫一列 `def_org_field`，再對**每個 profile** 各寫一列 `profile2field`，預設全開。列數是欄位數 × profile 數，會一直長。

---

## 2. 新增功能（模組）的流程

開源版 7.4 沒有建模組的畫面。`modules/Settings/ModuleManager` 只有啟用／停用、匯入、匯出模組套件。建模組的完整步驟寫在命令列工具 `vtlib/tools/console.php:235` `create()`，等於官方示範「一個最小可用的模組要有哪些東西」：

1. **登記模組** `Vtiger_Module::save()` → `Vtiger_ModuleBasic::__create()`（`vtlib/Vtiger/ModuleBasic.php:147`）：
   - 寫 `vtiger_tab`，`customized=1`。
   - 展開模組權限（`initForModule`）。
   - 實體模組再初始化分享權限（`initSharing`），並掛到選單。
2. **建表** `initTables()`（`ModuleBasic.php:287`）：建 `vtiger_<模組>`（基底表）與 `vtiger_<模組>cf`（擴充表），初始都只有主鍵 `<模組>id`。
3. **建區塊**：一個主資訊區塊 `LBL_<模組>_INFORMATION`，一個自訂資訊區塊 `LBL_CUSTOM_INFORMATION`。
4. **建必要欄位**：
   - 記錄標題欄位：uitype 2、`V~M`、存基底表。
   - 擁有者 `assigned_user_id`、建立時間、修改時間：這三個存在共用的 `vtiger_crmentity`，不在模組自己的表。
5. **設記錄標題** `setEntityIdentifier()`（`ModuleBasic.php:311`）：寫 `vtiger_entityname`。關聯、搜尋、彈窗選記錄時都用這個欄位當顯示名稱。
6. **建預設清單檢視「All」**，放入標題、擁有者、建立時間三欄（`vtiger_customview`）。程式註解寫明這是**必要**的（`console.php:294`），列表畫面依賴它。
7. **其他設定**：預設分享權限、啟用匯入／匯出／合併工具、註冊 webservice（`initWebservice`）。
8. **產生類別檔**（`console.php:318` `createFiles`）：把 `vtlib/ModuleDir/6.0.0/ModuleName.php` 樣板的 `ModuleName`、`<modulename>`、`<entityfieldname>` 等佔位字串替換掉，寫到 `modules/<模組>/<模組>.php`。
9. **掛到選單**（`Settings_MenuEditor_Module_Model::addModuleToApp`）。

第 8 步產生的類別只宣告表名與主鍵（`table_name`、`tab_name`、`tab_name_index`、`customFieldTable`）、列表與搜尋預設欄位、必填欄位、預設排序，還有一個空的生命週期掛勾 `vtlib_handler()`（安裝後、停用、移除前、更新前後），沒有任何畫面或存檔邏輯。這份資訊其實全部可以從 metadata 推出來，vtiger 會需要這個檔，是因為存檔與查詢程式是從類別屬性讀表名（L）。

## 3. 畫面與動作怎麼「自動長出來」

請求進來後，`includes/main/WebUI.php:183` 呼叫 `Vtiger_Loader::getComponentClassName()`（`includes/Loader.php:105`）找處理類別，依序嘗試：

1. 模組自己的類別 `modules/<模組>/views/List.php` → `<模組>_List_View`。
2. 子模組的中間 fallback（Settings 這類階層式模組才有）。
3. 都沒有，就用 `Vtiger_List_View`。

`modules/Vtiger/views` 提供 `List`、`Detail`、`Edit`、`QuickCreateAjax`、`RelatedList`、`Popup`、`Import`、`Export`、`FindDuplicates`、`MergeRecord` 等畫面；`modules/Vtiger/actions` 提供 `Save`、`SaveAjax`、`Delete`、`MassSave`、`MassDelete`、`ExportData` 等動作。這些通用類別全部照 metadata 運作：

- **編輯與明細**：依 `vtiger_blocks` 排區塊，依 `vtiger_field` 排欄位，每個欄位依型別交給 UIType 類別畫出來（§3.1）。
- **存檔**：`data/CRMEntity.php:98` `saveentity()` 逐表處理（§3.2）。
- **列表**：讀模組的預設清單檢視拿欄位，由 `QueryGenerator` 組 SQL（§3.3）。

所以新模組什麼程式都不寫，就有完整的 CRUD。要客製某個畫面時，只需在模組目錄放同名類別繼承通用類別、覆寫要改的方法。

### 3.1 欄位型別 → UIType 類別

`modules/Vtiger/uitypes/Base.php:53` `getInstanceFromField()` 用語意型別組類別名稱，依序找 `<模組>_<型別>_UIType` → `Vtiger_<型別>_UIType` → `Vtiger_Base_UIType`，和畫面類別是同一種 fallback 模式。每個 UIType 類別實作四個方法：

| 方法 | 用途 |
|---|---|
| `getTemplateName()` | 編輯元件樣板 |
| `getDisplayValue()` | 資料庫值 → 顯示值 |
| `getDBInsertValue()` | 使用者輸入 → 資料庫值 |
| `getUserRequestValue()` | 請求值正規化 |

樣板在 `layouts/v7/modules/Vtiger/uitypes/*.tpl`，每種型別有編輯用、明細用（`*DetailView.tpl`）、列表搜尋用（`*FieldSearchView.tpl`）三種版本。

前端驗證大部分來自 `typeofdata`。額外的規則寫在 `Field.php:1016` `getValidator()`，**依欄位名稱寫死**在 `switch` 裡，例如 `birthday` 要早於今天、`enddate` 要晚於 `startdate`，自訂欄位拿不到這類規則。

### 3.2 存檔

`saveentity()`（`data/CRMEntity.php:98`）的流程：

```
開交易
for 表 in $tab_name:
    vtiger_crmentity → insertIntoCrmEntity()
    其他表         → insertIntoEntityTable()（:404）
        查 vtiger_field WHERE tabid=? AND tablename=? AND displaytype IN (...) AND presence IN (0,2)（:485）
        非管理員再 JOIN profile2field／def_org_field，只留可寫的欄位
        逐欄依 uitype 轉值：56 勾選→'1'/'0'；15/16 下拉遇「無權限」保留舊值；33 多選陣列→' |##| ' 串接…
        組 INSERT 或 UPDATE
呼叫模組的 save_module() 掛勾
提交
```

`vtiger_field` 的查詢結果用函式內的 `static $_privatecache` 快取，只在 bulk save 模式才填。

### 3.3 列表查詢

`include/QueryGenerator/QueryGenerator.php:428` `getFromClause()` 依選到的欄位收集各自的 `tablename`，只 JOIN 用得到的表；擴充表用 `tab_name_index` 的主鍵對回基底表。沒選自訂欄位時，就不會 JOIN 擴充表。

---

## 4. 欄位的生命週期

### 4.1 新增欄位

入口 `Settings_LayoutEditor_Module_Model::addField()`（`modules/Settings/LayoutEditor/models/Module.php:133`）：

1. 同一個模組內 `fieldlabel` 不可重複（`checkFieldExists`，`:313`）；型別必須在白名單內。
2. 用 `vtiger_field` 的序號產生器取 `fieldid`，欄名定為 `cf_<fieldid>`。表名取模組類別的 `customFieldTable[0]`，沒有就用 `vtiger_<模組>cf`。
3. 依型別算出 uitype、typeofdata、實體欄型別（§1.3），預設值也依型別轉成資料庫格式。
4. `Block::addField()` → `Vtiger_FieldBasic::__create()`（`vtlib/Vtiger/FieldBasic.php:148`）：
   - `Vtiger_Utils::AddColumn()`（`vtlib/Vtiger/Utils.php:225`）：欄位不存在才執行 `ALTER TABLE ... ADD COLUMN`（`FieldBasic.php:184`）。
   - `INSERT INTO vtiger_field ...`。
   - `Vtiger_Profile::initForField()` 展開權限列。
5. 回到 `addField()` 才檢查實體欄有沒有真的建起來，沒有就丟例外。**但這時 `vtiger_field` 那列已經寫入了**，和註解寫的「建失敗就不加 `vtiger_field`」順序不符（L，由程式順序推論）。整段沒有交易包覆；MySQL 的 DDL 本來就會隱式 commit。
6. 下拉／多選欄位再呼叫 `setPicklistValues()` 建值表。

### 4.2 修改欄位

`Settings_LayoutEditor_Field_Action::save()`（`modules/Settings/LayoutEditor/actions/Field.php:58`）只改 metadata：必填、`presence`、`quickcreate`、`summaryfield`、`headerfield`、`masseditable`、預設值。這個 action 完全不碰 `uitype` 與實體欄型別，所以**型別和長度建立後就不能改**（C）。要換型別只能刪掉重建。

### 4.3 刪除欄位

`Settings_LayoutEditor_Field_Model::delete()`（`modules/Settings/LayoutEditor/models/Field.php:14`）：

1. 刪 `vtiger_field`、`def_org_field`、`profile2field`，解除相關清單，觸發 `vtiger.field.afterdelete` 事件（`FieldBasic.php:221`）。
2. `ALTER TABLE <擴充表> DROP COLUMN`（`:35`），**資料直接消失**。
3. 拼出 `表:欄:欄名:模組_標籤:型別` 等四種格式的字串，逐一刪除清單檢視、篩選條件、報表欄位、排序、日期篩選、彙總裡的參照（`:30-47`）。標籤裡的空白要換成底線；如果標籤改過，就可能對不上（L）。
4. 潛在客戶轉換對照對 Leads、Accounts、Contacts、Potentials 寫死特別處理（`:50-57`）。
5. 下拉欄位 `DROP TABLE vtiger_<欄名>` 與其 `_seq` 表，並清掉連動規則（`:60-67`）。

### 4.4 快取

| 快取 | 位置 | 範圍 |
|---|---|---|
| 模組欄位清單 | `include/utils/VTCacheUtils.php:106` | 單一請求 |
| uitype → 語意型別 | `WebserviceField.php:318` | 單一請求 |
| 欄位權限 | `Vtiger_Cache` `modulePermission-*` | 單一請求 |
| 存檔用欄位查詢 | `CRMEntity` `static $_privatecache` | 單一請求、僅 bulk save |

PHP 每個請求都重建這些快取，所以不用處理跨請求的失效問題。如果換成長駐程序（Node、.NET、Java），metadata 變更後的快取失效就要自己處理。

---

## 5. 值得學的、不要學的

**值得學**

- 模組、區塊、欄位都是 metadata，存檔、查詢、畫面、權限都由它驅動；核心欄位與自訂欄位走同一條路。
- 畫面、動作、欄位型別都用同一種 fallback：模組專屬 → 通用 → 基底。新功能零程式碼就能運作，要客製再覆寫。
- 所有記錄共用一張身分表，id 全系統唯一，擁有者與時間戳記集中管理。
- 每個模組一張擴充表，與核心表隔離，系統升級時不互相干擾。
- 實體欄名由系統產生（`cf_<id>`），不用使用者輸入的名稱，避開 SQL 注入與改名問題。
- 新模組一定帶「記錄標題欄位」與「預設清單檢視」，保證關聯、搜尋、列表一開始就能用。
- 欄位型別與長度建立後不能改，避開資料轉換的難題。
- 刪欄位時發事件、模組有生命週期掛勾，讓其他功能自己清理。
- 查詢只 JOIN 用得到的表。

**不要學**

- 型別用 `uitype` 數字、`typeofdata` 字串、名稱特例三套並存，讀程式要三處對照。
- 新模組要從樣板產生一個類別檔，而且內容全是 metadata 推得出來的東西。
- 每加一個欄位就一次線上 DDL，而且 DDL 與 metadata 寫入沒有原子性。
- 參照用 `表:欄:欄名:模組_標籤` 字串，刪改時要拼字串去清。
- 每個下拉欄位一張值表，記錄存值的文字而不是值的 id。
- 模組 × profile、欄位 × profile 全部展開成權限列。
- 刪欄位直接砍實體欄，沒有軟刪除也沒有還原。
- 驗證規則依欄位名稱寫死，自訂欄位用不到。

---

## 6. 值的儲存策略比較

vtiger 選的是 A。另外兩種常見作法一併列出，供選型參考（本節為業界常識，L）。

| | A. 實體欄位（vtiger） | B. EAV（一個值一列） | C. JSON 欄位 |
|---|---|---|---|
| 結構 | 擴充表每欄位一個實體欄 | `記錄id, 欄位id, 值` | 擴充表一欄存 `{欄位id: 值}` |
| 新增欄位 | 需要 DDL | 只寫 metadata | 只寫 metadata |
| 型別約束 | 資料庫原生 | 多個型別欄或全轉字串 | 應用層驗證 |
| 篩選／排序 | 最快，可建索引 | 每個條件一次 JOIN，慢 | 可用；要快需加計算欄＋索引 |
| 報表／BI | 直接 SELECT | 要先轉寬表 | 要用 JSON 函式取值 |
| 欄位數上限 | 受單列大小限制 | 無 | 實務上無 |
| 刪除欄位 | DROP COLUMN | 刪值列 | metadata 標記刪除即可 |

幾點補充：

- C 的「計算欄＋索引」在 SQL Server 是 `ALTER TABLE ADD <欄> AS JSON_VALUE(data, '$.<id>') PERSISTED` 再建索引；PostgreSQL 用 `jsonb` 搭配 GIN 或運算式索引；MySQL 8 用 generated column。
- B 的效能問題在多條件篩選與列表排序最明顯；多數新系統已不選 EAV 當主要儲存。
- A 與 C 可以混用：值預設存 JSON，少數要大量篩選的欄位再升級成實體欄或計算欄。

---

## 7. 建議設計

以下是照 vtiger 的骨架、修掉它痛點的設計方向。技術堆疊與資料庫還沒定（見 §7.3），所以先寫到概念層。

### 7.1 骨架照 vtiger

1. **三層 metadata**：模組 → 區塊 → 欄位。模組至少有代碼、名稱、選單位置、啟用旗標、記錄標題欄位。
2. **共用記錄身分表**：`records(id, module_id, owner_id, created_at, updated_at, deleted_at)`。id 全系統唯一，關聯欄位只存 id。
3. **通用 CRUD + fallback**：列表、明細、編輯、存檔、刪除、匯入匯出都寫成一套通用元件，照 metadata 運作。每個環節保留「模組專屬實作優先，沒有就用通用版」的掛點，但**不要求**每個模組產生類別檔；模組沒有客製時，系統裡就只有 metadata。
4. **型別處理器註冊表**：`型別 → { 驗證, 轉成存入格式, 轉成顯示格式, 編輯元件, 明細元件, 篩選元件 }`，解析順序是模組覆寫 → 通用型別 → 基底。新增型別只要註冊一個處理器。
5. **建模組時一定帶的東西**：記錄標題欄位、擁有者、建立／修改時間、預設清單檢視、預設權限。照 vtiger 第 2 節的清單，做成一個交易內完成的服務，不靠命令列腳本。

### 7.2 修掉 vtiger 的痛點

1. **型別只用一個列舉**，例如 `text｜longtext｜integer｜decimal｜currency｜percent｜date｜time｜datetime｜boolean｜email｜phone｜url｜enum｜multienum｜reference｜user`。必填、長度、小數位、唯讀、預設值各自是獨立欄位，不塞進字串。
2. **所有參照存欄位 id**：清單檢視的欄位、篩選、排序、報表都存 `field_id`，不存字串拼接的組合鍵。刪除或改名時不用拼字串去清。
3. **值的儲存採 §6 的 C 為主、A 為升級路徑**：新增欄位零 DDL，也不撞寬表上限；少數常篩選的欄位，由管理員「升級」成計算欄＋索引。升級是少數、離峰、有人審的 DDL，不是每加一欄就做一次。
4. **下拉選項集中一張表**：`options(id, field_id, label, sort, color, active)`，記錄存選項 id，改文字不用回頭更新資料。連動規則同樣以 id 表示。
5. **軟刪除**：欄位與模組都用停用旗標，資料保留；真正清除另外做批次，執行前先備份。
6. **權限只存例外**：預設依模組權限決定，只另外記錄 `(欄位, 角色, 隱藏／唯讀)` 的例外，不展開成欄位 × 角色的全表。
7. **驗證規則資料化**：跨欄位規則（如「結束日不得早於開始日」）存成 metadata，例如 `{type: 'gte', field: 'end', other: 'start'}`，自訂欄位也能用，不依欄位名稱寫死。
8. **metadata 版本號**：長駐程序把 metadata 快取在記憶體，每次異動就遞增版本號，請求比對版本後重載。
9. **型別建立後不可改**：照 vtiger。要換型別就新增欄位再搬資料。

### 7.3 待決

**新增約束（2026-11-03，PO）**：POC 完成後要做成獨立的正式產品，技術棧定為後端 .NET 8、前端 Vue 3 + Quasar。設計範圍以**最複雜的情境**為準，POC 再從中切出第一階段。資料庫維持 Q-1 的 SQLite（使用者裁決：POC 只是暫時用的）；正式版換成 SQL Server，所以儲存層要集中、可替換，JSON 取值語法不可散落各處。

- ~~Q-1 技術堆疊與資料庫~~ **已定（2026-11-03）：POC 用 SQLite**，透過 Node 內建的 `node:sqlite`。
  - 理由：零安裝、單一檔案；DDL 可以和欄位定義寫入包在同一個交易，修掉 vtiger「欄位定義已寫入、實體欄卻沒建起來」的問題；之後可以搬到免費的 Cloudflare D1。
  - 已在本機實測（Node 26.9、SQLite 3.53）：`json_valid` CHECK 約束、`data->>'$.欄位'` 取值、以 JSON 取值運算式建索引，都能用。
  - 代價：同時只能一個寫入者；日後換資料庫要改 JSON 取值語法，所以儲存層要集中在一個模組。
- ~~Q-2 誰能建模組、建欄位~~ **已定（2026-11-03）：POC 就做完整角色權限**，模組、動作、欄位三個層級都要管。實作照 §7.2 第 6 點，只記錄例外、不全展開。
- ~~Q-3 模組間關聯~~ **已定（2026-11-03）：POC 就做到最複雜**。參照欄位、明細頁的反向關聯清單、刪除時的連帶處理都要做（原本「只做參照欄位」的決定已作廢）。
- Q-4、Q-5 改由設計文件定案，見 [`../superpowers/specs/2026-11-03-dynamic-modules-design.md`](../superpowers/specs/2026-11-03-dynamic-modules-design.md)。
- **Q-4 客製掛點的形式**：模組要客製時寫程式覆寫（像 vtiger），還是只開放設定式的規則與工作流程？
- **Q-5 連動與條件顯示**：vtiger 只有下拉連動；如果要「某欄位值決定其他欄位顯示與否」，要另外設計規則格式。

---

## 附錄：本次讀過的檔案

| 檔案 | 看什麼 |
|---|---|
| `schema/DatabaseSchema.xml` | metadata 表結構 |
| `vtlib/tools/console.php` | 建模組的完整步驟 |
| `vtlib/ModuleDir/6.0.0/ModuleName.php` | 模組類別樣板 |
| `vtlib/Vtiger/ModuleBasic.php` | 模組登記、建表、記錄標題 |
| `includes/Loader.php`、`includes/main/WebUI.php` | 畫面／動作類別 fallback |
| `modules/Vtiger/views`、`modules/Vtiger/actions` | 通用畫面與動作清單 |
| `modules/Settings/LayoutEditor/models/Module.php` | 新增欄位、型別對照 |
| `modules/Settings/LayoutEditor/models/Field.php` | 刪除欄位 |
| `modules/Settings/LayoutEditor/actions/Field.php` | 修改欄位屬性 |
| `vtlib/Vtiger/FieldBasic.php`、`Field.php`、`Utils.php`、`Profile.php`、`Access.php` | 實體欄建立、下拉值表、權限展開 |
| `data/CRMEntity.php` | 存檔流程 |
| `modules/Accounts/Accounts.php` | 表與主鍵宣告範例 |
| `modules/Vtiger/models/Field.php` | 型別解析、可見／可編輯、權限、驗證 |
| `modules/Vtiger/uitypes/Base.php` | UIType 解析鏈 |
| `include/Webservices/WebserviceField.php`、`modules/Users/DefaultDataPopulator.php` | uitype → 語意型別 |
| `include/QueryGenerator/QueryGenerator.php` | 列表查詢的 JOIN 組法 |
| `include/utils/VTCacheUtils.php` | 欄位快取 |
