# 名稱欄位多語 設計文件（段 i3）

> 2026-11-21｜平台共用能力（非應用程式）｜前置：[語系包與自訂語系](2026-11-17-language-packs-design.md)（§4.1 語系代碼、§10.2 可翻譯路徑、§10.5 套件、§18 第 6 點、§20 i4e／i4f）、[政策設定](2026-11-11-policy-settings-design.md)（§3.5、A18 必填的多語文字）、[應用程式套件](2026-11-06-app-package-design.md)（§4.9 formatVersion）、[報表](2026-11-05-reports-design.md)
> 狀態：主控撰寫（2026-11-21），這次只寫設計、不改程式。依使用者 2026-11-21 裁決：會改變使用者操作的小題照推薦先定，寫成「主控暫定，待使用者確認」；技術類主控直接定。
> 標記：「（使用者裁決）」是使用者已拍板的；「（Q#）」是行為類，主控暫定、待使用者確認，彙整在 §20.1；「（T#）」是技術類，主控已定、可推翻，彙整在 §20.2。

## 1. 目的

報表、儀表板、儀表板卡片標題、任務看板與甘特圖的自訂檢視，名稱目前只存一個字串。這些名稱都會分享或指派給別人看：泰籍組長建了一張「本週出貨」看板分享給全部門，台籍同事看到的是泰文；管理員指派「業務儀表板」給越南廠，越南同事看到的是中文。模組、欄位、選項、列表檢視、群組、部門早就是多語（`I18nText`），這幾個是漏網的。

i3 把它們改成多語，讓：

1. 建立者可以替名稱填各語系的譯名，檢視者依自己的語系看到名稱，缺翻時照既有退回鏈。
2. i4e（設計資料 Excel 翻譯）翻得到報表與儀表板名稱（語系包設計 §20 i4e 依賴 i3）。
3. 套件的報表名稱能帶多語，並和 i4f（套件語系鍵改代碼格式）合成一次 `formatVersion` 升版（語系包設計 §10.5、§18 第 6 點）。

順手收掉 m1（選單分區多語）審查留下的兩項：套件與範本驗證器不檢查 `section`、API 不收字串 `section`。

## 2. 範圍

**範圍內**

- `Report.Name`、`Dashboard.Name`、`TaskView.Name` 改 `I18nText`（一個 migration，SQLite 與 PostgreSQL）。
- 儀表板卡片標題（`LayoutJson` 裡的 `title`，指派版、個人版、自己的儀表板共用）改多語，不加 migration（§5.3）。
- API、DTO、MCP 唯讀工具、稽核、刪除影響清單、匯出等所有讀名稱的地方改依語系解析。
- 前端六個名稱輸入處換多語輸入框，所有顯示處改用 `resolveI18n`。
- 套件格式 9：`reports[].name` 可寫多語物件；全部 `i18n` 語系鍵改代碼格式（即 i4f）；欄位可帶「不提供給 AI」旗標 `excludeFromAi`（範本同步升格式 2）；內建三套件的 13 份報表名稱補四語。
- Demo：群組 12 個、部門 6 個、示範報表、示範儀表板與卡片標題補四語；系統程式「動態模組設計」補四語。
- 選單分區：套件與範本驗證器檢查 `section`；建立、修改模組的 API 也收字串 `section`；拒收空值語系；有其他語系卻缺繁中時報錯，不再靜默丟掉分區（Q4）。

**範圍外**

- 列表檢視「另存新檢視」的輸入方式（`ViewMenu.vue` 單一輸入框、寫進每個必填語系）不改，見 §16 第 6 點。
- 群組的改名方式（依目前語系改那一格，p3 D4、D5）不改；i3 只補 Demo 群組的譯名。
- 報表清單的搜尋與依名稱排序：現在沒有這兩個功能（`ReportListPage.vue` 的表格欄沒有 `sortable`、沒有篩選框），清單依 Id 排，i3 不新增（§16 第 7 點）。
- 量值、欄位標題：報表的量值沒有自訂名稱（`ReportMeasure` 只有 Id），欄位標題來自欄位定義，本來就是多語。
- 列印範本本文、記錄標題 snapshot、使用者顯示名稱（`AppUser.DisplayName` 是人名，不翻）、MCP token 名稱（只給本人看）。
- 套件 `dashboards` 段：格式 9 仍是保留 section，只收空陣列（套件設計 §4.6）。
- 報表、儀表板加 `Code`：不加，i4e 的路徑另定（§16 第 1 點）。

## 3. 現況盤點（main 43d22acf）

| 名稱 | 現在的型別 | 證據 |
|---|---|---|
| 報表名稱 | `string`，必填、去空白後 ≤ 100 | `Domain/Entities/Dynamic/Report.cs`、`ReportService/Data/ReportRules.cs` `CheckName` |
| 儀表板名稱 | `string`，同報表的 `CheckName` | `Dashboard.cs`、`DashboardService/Commands/*Dashboard*Handler.cs` |
| 卡片標題 | `string?`，在 `LayoutJson`，≤ 100，空的顯示報表名稱 | `Domain/Reports/DashboardLayout.cs` `DashboardCard`、`DashboardRules.MaxTitleLength` |
| 任務看板、甘特檢視 | `string`，≤ 100；另存副本加「（副本）」 | `TaskView.cs`、`TaskViewService/TaskViewHandlers.cs` `TaskViewRules.Name`、`CopyName` |
| 列表檢視 | `I18nText` | `ListView.cs` `NameI18n` |
| 群組 | `I18nText`；Demo 只有繁中 | `PermissionGroup.cs`、`Persistence/Seed/DemoPeople.cs:64` |
| 部門 | `I18nText`；Demo 只有繁中 | `Department.cs`、`DemoPeople.cs:88` |
| 系統程式名稱 | `I18nText`；「動態模組設計」只有繁中，「權限設定」四語 | `DynamicProgram.cs`、`Persistence/Seed/SeedData.cs:25`、`:35` |
| 選單分區 | `Section`（繁中分組鍵）＋`SectionI18n` | `Module.cs` `SetSection`、`SectionText` |
| 套件報表名稱 | `string`，1–200 字 | `AppService/Data/AppPackageData.cs` `AppPackageReport`、`AppPackageValidator.V3.cs:210` |

資料表：`Dynamic_Report.Name`、`Dynamic_Dashboard.Name` 是 `HasMaxLength(200)`、`Dynamic_TaskView.Name` 是 `HasMaxLength(100)`（`Domain/Data/Configurations/ReportConfiguration.cs`、`DashboardConfiguration.cs`、`TaskViewConfigurations.cs`）。其他 `I18nText` 欄存成 JSON 文字（`JsonConversions.HasJsonConversion`；PostgreSQL 是 `text`、collation `C`，不用 `jsonb`，PostgreSQL provider 設計 §5.4），序列化用預設編碼器，中文會存成 `\uXXXX`，所以資料庫端不能對名稱 JSON 做 `LIKE`、`=`。

讀名稱的程式（改型別後編譯器會全部抓出來，這裡列出來方便估工）：

- 報表：`ReportResponses.cs`（清單、詳細）、`ExportReportHandler.cs:55`（工作表名稱）、`CopyReportHandler.cs`、`DashboardAccess.cs:53`（卡片引用的報表名稱）、`GetDashboardsHandler.cs:30`（分享給我的報表）、`AccountRepository.cs:104`（刪群組影響清單）、`AppExporter.cs:619–628`、`AppFlowInstaller.cs:136–143`、MCP `ReadTools.cs:156`（`list_reports`）、`ReportRepository.NameExistsAsync`（Demo 預檢）。
- 儀表板：`DashboardResponses.cs`、`GetDashboardsHandler.cs`、`DashboardRepository.cs:35–45`、`DeletionRepository.cs:48–55`、`DeletePlanner.cs:334`、`AccountRepository.cs:106`、`AdminCreateDashboardRequest`／`AdminUpdateDashboardRequest` 的稽核標籤。
- 任務檢視：`TaskViewAccess.cs:56`、`:63`、`TaskViewHandlers.cs:107`、`:143`、`:175`、`TaskBoardScenario.cs`（Demo 建兩張看板）。

驗證器：

- `AppPackageValidator.CheckText`（`AppPackageValidator.cs:183`）：繁中必填、語系鍵限 `FormatLocales` 四種、每語 1–200 字；名稱類都走它，報表名稱沒走（V3 另寫字串檢查）。
- `TemplateValidator.HasZhTw`（`TemplateValidator.cs:276`）：只看繁中有沒有值，不擋語系鍵。
- 兩者都沒檢查模組的 `section`（m1 審查建議 2）。
- 建立、修改模組時 `ModuleRules.ValidModuleSection` 只驗語系鍵格式與每語 ≤ 100，`null` 值放行（m1 審查建議 4）；`ModulesController` 的 `CreateModuleBody`／`UpdateModuleBody` 的 `Section` 是 `I18nText?`，舊 client 送字串在 model binding 就回 400（m1 審查建議 3）。
- 字串或物件都收的讀法已經有：`TemplateService/Data/SectionTextJsonConverter.cs`（字串＝只有繁中，寫出時只有繁中就寫字串）。

必填語系：`Common/RequiredTextGuard.cs` 依政策 `locale.requiredText` 檢查，修改時內容沒變就不檢查（政策設計 §3.5）；開啟套件與樣板期間只要求繁中（`Common/PackageTextScope.cs`，p3 判斷 12）。選填的多語文字（任務規則的說明）不檢查必填語系（`TaskRuleValidator.CheckText` 的 `required: false`）。

前端：

- 多語輸入框 `presentation/dynamic/designer/I18nTextInput.vue`：必填語系各一格在上，其他啟用語系收在「其他語系」；有 `optional`、`original`、`maxlength`。
- 顯示用 `domain/dynamic/i18n-text.ts` `resolveI18n`，退回鏈與後端 `LocaleChain` 跑同一份共用案例。
- 名稱輸入處：`reports/designer/StepPreview.vue:15`（報表）、`pages/dashboards/DashboardPage.vue:78`（新增自己的儀表板）、`dashboards/DashboardView.vue:110`（頁首就地改名）、`pages/system/DashboardsAdminPage.vue:147`（指派版）、`dashboards/CardOptionsDialog.vue:70`（卡片標題）、`tasks/board/BoardPicker.vue:169`（看板、甘特另存）。
- 型別：`domain/dynamic/report-types.ts` `ReportItem.name`、`dashboard-types.ts` `DashboardCard.title` 與清單項目、`task-board-types.ts` `TaskViewSummary.name` 都是 `string`。

套件：`CurrentFormatVersion = 8`；內建 `crm-sales`、`inventory` 是 8、`mes-mold` 是 6、`partner`、`contact` 是 7；報表名稱只有繁中（crm-sales 2 份、inventory 5 份、mes-mold 6 份）；`dashboards` 都是空的；`app.schema.json` 的 `$defs/i18n` 用 `propertyNames enum` 限四語。

## 4. 已定決定（本設計照用，不再問）

- **任務看板與甘特檢視名稱一起改多語**（使用者裁決，2026-11-20 設計題分流 B 區 i3-1）。判準：會分享給別人看的名稱就多語。
- **報表、儀表板名稱的必填語系照政策 `locale.requiredText`，不另開「至少填一種」的例外**（使用者裁決，2026-11-20 分流 B 區 i3-2；依據使用者 11-12 Q10「必填語系套用到所有必填的多語文字，含檢視名稱」）。只給自己看的報表也一樣。
- **缺翻退回**：要求的語系 → 上層語系 → 系統預設語系 → 繁中 → 第一個有值的（使用者裁決 U3，2026-11-17；後端規則「語系退回」）。名稱欄位同一條。
- **i3 與 i4f 的套件格式升版合成一次**；舊 `formatVersion` 只收四語鍵，新版才放寬（語系包設計 §10.5、§18 第 6 點）。
- **套件仍要求繁中**（p3 判斷 12，使用者 2026-11-12 確認）。
- **寫進資料的系統文字用系統預設語系**（使用者 2026-11-20 裁決）：稽核目標名稱解析名稱時照這條。
- **同一次升版讓套件與範本帶欄位的「不提供給 AI」旗標**（`Dynamic_Field.ExcludeFromAi`）；套用套件時要寫入 true 必須有「權限設定」權限（權限軸設計 §7.3）；重新套用不清掉既有的 true（使用者裁決，2026-11-21）。這推翻 MCP 設計 §5.5 末條「旗標不進套件與範本」（mcp1a 計畫 Q4，規劃者暫定），細節見 §8.1。

## 5. 資料模型與 migration

### 5.1 實體（T1）

| 實體 | 改前 | 改後 |
|---|---|---|
| `Report` | `string Name` | `I18nText NameI18n` |
| `Dashboard` | `string Name` | `I18nText NameI18n` |
| `TaskView` | `string Name` | `I18nText NameI18n` |
| `DashboardCard`（JSON） | `string? Title` | `I18nText? Title` |

- 屬性名稱用 `NameI18n`，與 `Module`、`ListView`、`Department` 一致；欄位設定 `HasJsonConversion()`，不設長度（每語長度由驗證管，JSON 跳脫後長度不可預估）。
- 舊屬性 `Name` 刪掉，不留同步欄：留著就會有兩個來源（選單分區的 `Section`／`SectionI18n` 是因為要當分組鍵才留，名稱不需要）。

### 5.2 migration（T2）

一個 migration `NameI18n`，用 `scripts/db/add-migration.sh NameI18n` 一次產兩份。EF 產生的是「加 `NameI18n` 欄（非空、預設 `''`）→ 刪 `Name` 欄」，在兩步之間插入資料搬移 SQL，前例是 `SharedMailSettings`（migration 裡唯一的手寫 SQL 是資料搬移，交付說明列出）。

| 項目 | SQLite | PostgreSQL |
|---|---|---|
| 舊值轉 JSON | `json_object('zh-TW', trim("Name"))` | `json_build_object('zh-TW', btrim("Name"))::text` |
| 舊值空白 | `'{}'` | `'{}'` |
| Down：還原字串 | `coalesce(json_extract("NameI18n", '$."zh-TW"'), '')` | `coalesce("NameI18n"::json ->> 'zh-TW', '')` |

- **舊值一律放繁中**，不猜原本是哪種語言（分流技術決定 25）。現在沒有正式客戶，Demo 每天重建、預設語系是繁中，結果和「放系統預設語系」相同；放繁中的好處是 migration 不必讀政策表。代價留給未來：預設語系是越南文的系統升級時，舊的越南文名稱會出現在「名稱（zh-TW）」那一格，顯示不受影響（退回鏈最後會走到繁中）。
- **空白**：`trim` 後空字串存 `{}`。顯示和現在一樣是空白；下次改名時照必填檢查。正常流程寫不出空白名稱（`CheckName` 擋），這是防資料修補留下的列。
- 用資料庫的 JSON 函式組字串，名稱裡的引號、反斜線、換行由資料庫跳脫，不手串字串。兩種資料庫的 SQL 不同句，各自寫在自己的 migration 裡。
- JSON 存法和程式寫入的不同（資料庫函式存原字、程式存 `\uXXXX`；`json_build_object` 冒號旁有空白）。讀取一律反序列化，不影響；EF 的變更偵測以反序列化後重新序列化的結果比對，讀出未改的列不會被當成變更。
- Down 只還原繁中，其他語系的譯名會遺失（降版本來就不保證無損，交付說明寫明）。

### 5.3 卡片標題不寫 migration（T3）

卡片標題在 `Dashboard.LayoutJson` 與 `DashboardPersonalVersion.LayoutJson` 兩處，是陣列裡的屬性。在兩種資料庫用 SQL 改巢狀 JSON 陣列很難寫對，改用**讀取寬鬆**：

- `DashboardCard.Title` 掛「字串或物件」的轉換器（§6.1 的共用轉換器）：讀到字串＝`{"zh-TW": 字串}`；空字串、只有空白、`null` 都當沒有標題；讀到物件照收。
- 寫出一律是物件。版面下次存檔時就換成新格式，沒存過的舊版面永遠以字串留著，照樣讀得懂。
- 代價：換成新格式的版面，舊版程式讀不懂（降版後該儀表板打不開）。Demo 每天重建，正式客戶還沒有，接受；交付說明寫明。

### 5.4 長度（T4）

每語系去空白後最多 100 字，四種名稱都一樣：報表、儀表板沿用 `ReportRules.MaxNameLength`，卡片標題沿用 `DashboardRules.MaxTitleLength`，看板沿用 `TaskViewRules.MaxName`。這是技術限制，列進可寫死清單（§15）。儀表板前端輸入框現在寫 `maxlength="200"`，和後端的 100 不一致，改成 100。

## 6. API 與 DTO

### 6.1 輸入：字串或物件都收（T5）

把 `SectionTextJsonConverter` 搬到 `Domain/ValueObjects/`（`DashboardLayout` 在 Domain 要用），改名 `I18nTextOrStringJsonConverter`，行為不變：讀字串＝只有繁中、讀物件照收、其他型別丟 `JsonException`。用在：

- 報表 `SaveBody.Name`、儀表板 `CreateBody.Name`／`SaveBody.Name`、指派版的建立與修改、任務檢視 `CreateBody.Name`／`UpdateBody.Name`。
- `DashboardCard.Title`（§5.3）。
- 模組 `CreateModuleBody.Section`／`UpdateModuleBody.Section`（m1 審查建議 3）。
- 範本與套件的 `section`（原用法）、套件 `reports[].name`（§8）。

字串一律當繁中，與 migration 同一規則。用途是部署當下還開著舊版畫面的使用者：舊畫面送字串照樣存得進去，不會在 model binding 就 400。必填語系不是繁中的系統，送字串會被必填檢查擋下，訊息照常。

### 6.2 輸出：回物件（T6）

| 回應 | 欄位 | 型別 |
|---|---|---|
| `ReportItem`、`ReportDetail` | `name` | `I18nText` |
| `DashboardListItem`、`MyDashboardItem`、`SharedReportItem`、`DashboardDetail`、`AdminDashboardItem`、`AdminDashboardDetail` | `name` | `I18nText` |
| `DashboardDetail.reportNames` | 值 | `I18nText` |
| `DashboardCard` | `title` | `I18nText?` |
| `TaskViewSummary`、`TaskViewDetail` | `name` | `I18nText` |

與模組、列表檢視、群組相同：後端回整個物件，前端依目前語系解析（後端不替畫面解析，切語言不必重新查詢）。

以下地方由後端解析成字串：

| 地方 | 用哪個語系 |
|---|---|
| MCP `list_reports` | 呼叫的語系（同 `list_modules` 的 `m.Name.Resolve(call.Locale)`），MCP 的回應形狀不變 |
| 報表匯出的 Excel 工作表名稱 | 請求語系（`requestLocale.For(request.Locale)`，同匯出的合計列） |
| 刪除影響清單、刪群組影響清單 | 請求語系 |
| 稽核目標名稱（指派版的建立、修改） | 系統預設語系（§4），由 handler 以 `AuditContext.Target` 補，請求 record 裡拿不到政策 |
| 套件開啟、匯出的警告（「報表「…」引用了被排除的欄位」） | 繁中（套件訊息原本就是繁中，跟著 i4c 的訊息搬遷再改） |
| Demo 預檢的同名比對 | 比對繁中值（§11.3） |

### 6.3 相容性

- 回應從字串變物件是破壞性變更。前後端同一次部署；部署當下沒重新整理的舊畫面會把名稱顯示成 `[object Object]`，重新整理即好。目前沒有外部 client，接受（同 m1 的取捨）。
- MCP 工具輸出不變（後端解析成字串），不影響已發的 token。

## 7. 驗證

### 7.1 名稱（必填）

- 報表、儀表板（自己的與指派版）、任務檢視的名稱都走 `RequiredTextGuard.Check(incoming, stored, subject)`：新增時 `stored` 傳 `null`；修改時傳資料庫裡的值，內容沒變就不檢查新增的必填語系（政策設計 §3.5「改了才檢查」）。
- 每語系去空白後 ≤ 100；語系鍵只驗代碼格式（`I18nText.IsWellFormed`，沒安裝的語系也收，語系包設計 M1）；拒收 `null` 值（`{"en-US": null}` 回 400，訊息「語系 en-US 的文字不能是空值」，同 `TaskRuleValidator`）；存檔前去掉空字串的語系。
- 訊息主詞維持各功能原本的字：報表、儀表板「名稱」；看板、甘特圖沿用 `TaskViewMessages.NameRequiredOf(kind)` 的分種類用字。超過長度的訊息帶上限數字（後端規則「上限與政策的通則」）。
- 開啟套件建立的報表在 `PackageTextScope` 內，只要求繁中（p3 判斷 12），不必另寫。

### 7.2 卡片標題（選填）

- 沒有值、或全部語系都是空字串：當沒有標題，顯示報表名稱。
- 有值時：語系鍵格式、每語 ≤ 100、拒收 `null`；**不檢查必填語系**（T7）。前例是任務規則的說明（選填多語文字不檢查必填語系）；理由是卡片標題缺的語系最後會退回到有值的那一格，不會空白。

### 7.3 任務檢視另存副本

每個有值的語系各自加上該語系的「（副本）」後綴（Q3）；後綴從 `IMessageCatalog` 依語系取，超過 100 字時照現在的規則截短原名、保留後綴。報表的「複製」照舊不加後綴（`CopyReportHandler`）。

## 8. 套件格式 9（與 i4f 合併，T8）

`CurrentFormatVersion` 從 8 升到 9。fg6、s2e 原本也各自計畫升版，未開工，之後依序用 10、11（分流技術決定 29）。

**格式 9 新增**：

1. **`reports[].name` 可寫多語物件**。讀法用 §6.1 的轉換器（字串＝只有繁中）；驗證改走 `CheckText`，繁中必填、每語 1–100 字（`ReportRules.MaxNameLength`）。匯出時只有繁中寫字串、有其他語系寫物件，同 `section` 的做法，只有繁中的套件匯出結果和格式 8 一字不差（版號除外）。
2. **全部 `i18n` 的語系鍵改代碼格式**（即原 i4f）：`CheckText` 的 `FormatLocales` 檢查在格式 9 改成 `LocaleCode.IsValid`；`TemplateValidator` 同步；`app.schema.json` 的 `$defs/i18n` 的 `propertyNames` 從 `enum` 改成代碼格式的 `pattern`（schema 只有 `AppSchemaTests` 讀，C# 驗證器才是真正擋的地方，兩邊一起改）。
3. 模組 `section` 的語系鍵同上。

**格式 2–8 不放寬**（`.claude/rules/app-packages.md`）：

- `reports[].name` 只能是字串，或只有 `zh-TW` 的物件；有其他語系鍵整份拒絕，訊息「reports[0].name：formatVersion 9 才支援多語報表名稱」。
- 語系鍵仍只收四語；帶 `ja` 整份拒絕。
- 報表名稱長度：格式 2–8 原本收 1–200 字，但 101–200 字的套件開啟時一定會被 `CreateReportRequest` 擋下、從來開不成功，所以**所有版本都改成 100**（不是政策調嚴，只是把開啟才會報的錯提前到驗證）。

4. **欄位的「不提供給 AI」旗標 `excludeFromAi`**：見 §8.1。

**內建套件**：`crm-sales`、`inventory`、`mes-mold` 升到格式 9、升 `manifest.version`（次版號），報表名稱補四語（th、vi 沿用 `x-translation` 機翻標記）。`partner`、`contact` 沒有報表，不動。內建套件沒有需要標「不提供給 AI」的欄位（沒有身分證字號、薪資、銀行帳號這類欄位），不加旗標。

**順序**：這段要等 §5、§6 的報表名稱多語合併（套件開啟走 `CreateReportRequest`，要能收多語名稱）。

### 8.1 欄位的「不提供給 AI」旗標（使用者裁決 2026-11-21；做法 T13）

背景：mcp1a 已上線 `Dynamic_Field.ExcludeFromAi`（MCP 設計 §5.5）。標了旗標的欄位在 MCP 通道視同沒有讀取權。它是權限軸 §7.4 的守衛欄，任何寫入要「權限設定」許可；現在只有 API 能設，套件與範本不帶（`AddFieldRequest` 的註解「套件、範本、內部合併一律 false」），所以匯出再匯入另一套系統會失去旗標。

**格式**

- 欄位（`TemplateField`，套件的 `modules[].blocks[].fields[]` 與 `contributions[].fields[]` 共用這個型別）加選填布林 `excludeFromAi`，省略等於 false。`app.schema.json` 的欄位定義同步加。
- 套件格式 2–8 帶 `excludeFromAi`（不論 true 或 false）整份拒絕，訊息「{模組代號}.{欄位代號}.excludeFromAi：formatVersion 9 才支援」。舊版程式讀格式 9 時，`AppJson` 的未知屬性拒絕（`UnmappedMemberHandling.Disallow`）加上版號檢查都會擋下，不會靜默丟掉旗標。
- **範本格式從 1 升到 2**：範本的讀取器（`TemplateJson`）不拒絕未知屬性，舊版程式讀到帶旗標的範本會**靜默丟掉旗標**（欄位變成 AI 看得到，往不安全的方向錯）。升到 2 後，舊版程式的 `formatVersion 必須是 1` 會整份擋下。新版收 1 與 2；格式 1 帶 `excludeFromAi` 整份拒絕。範本匯出一律寫 2（同套件匯出一律寫目前版本）。

**套用（開啟套件、從範本建立）**

- 欄位旗標是 true：
  - 新建欄位：`AddFieldRequest` 帶 `ExcludeFromAi: true`，走既有的權限設定檢查（`AddFieldHandler` 先問 `PermissionAdminGate`）。
  - 已存在的欄位（共用主檔已開、接回的孤兒欄位、整合合併）：`UpdateFieldRequest` 帶 `ExcludeFromAi: true`；現值已是 true 時 handler 視為沒改，不檢查權限。
- 欄位旗標是 false 或省略：新建欄位寫 false；已存在的欄位送 `ExcludeFromAi: null`（不改）。**重新套用不清掉既有的 true**：旗標只會被套件加上、不會被套件拿掉；要拿掉只能由有權限設定的人在欄位設定改。
- 沒有權限設定、而套件要寫入 true：
  - **開啟套件**（含產生 Demo）：403 `permissionAdminRequired`，整個開啟回滾（fail closed，照權限軸 §7.3 M3 對開啟套件的規則）。實際上開啟要系統管理員、產生 Demo 宣告 `IPermissionAdminRequest`，正常不會發生。
  - **從範本建立**（不是開啟套件）：照權限軸 §7.3 已寫的契約，旗標一律寫 false，回應的 `warnings` 加一則「你沒有『權限設定』權限，已略過範本的『不提供給 AI』設定，共 N 個欄位；請洽權限設定者到欄位設定勾選」，和 M3 略過權限建議同精神。
- 預覽：套件預覽（開啟前）列出「會標為不提供給 AI 的欄位：N 個」，讓管理員開啟前看得到（Q5）。

**匯出**：套件與範本匯出帶出 `excludeFromAi: true`（false 不寫），往返不再失去旗標。MCP 設計 §5.5 的已知限制「匯出再匯入另一套系統會失去旗標」由這段解除。

**稽核**：旗標寫入照 mcp1a 既有的欄位稽核（`field.add`／`field.update`），不另加動作代碼。

## 9. 選單分區（m1 遺留）

1. **套件與範本驗證器檢查 `section`**（`AppPackageValidator` 模組迴圈、`TemplateValidator.ValidateModule`）：語系鍵格式（格式 2–8 限四語、格式 9 代碼格式）；有任何非空值時繁中必填；每語 ≤ `ModuleRules.SectionMaxLength`；拒收 `null` 值。錯誤路徑 `{模組代號}.section`。現在這些錯誤要到開啟套件、建模組時才報，缺繁中的則整組分區靜默消失。
2. **API 收字串 `section`**：§6.1。
3. **API 拒收 `null` 值、去掉空字串語系**：`ValidModuleSection` 加 `null` 檢查；`Module.SetSection` 存檔前丟掉空字串的語系。
4. **建立、修改模組時，分區有其他語系卻缺繁中：回 400「選單分區有其他語系時必須有繁體中文」**（Q4）。現在 `SetSection` 把它當成不分區、默默丟掉。分區的分組鍵是繁中（`Module.Section`），這條是因為分組鍵寫死在繁中，不是必填語系政策。

不做 m1 審查建議 5（`Module.Section` 可公開寫入）與 6（選單排序的 `localeCompare` 沒指定語系），兩者都不屬於 i3 的名稱多語，維持既有行為。

## 10. 前端

### 10.1 輸入（Q1、Q2）

六個名稱輸入處都換成既有的 `I18nTextInput`（元件本身不改）：

| 畫面 | 位置 | 選項 |
|---|---|---|
| 報表設計最後一步 | `StepPreview.vue` | 必填；修改時傳 `original` |
| 新增自己的儀表板 | `DashboardPage.vue` 新增對話框 | 必填 |
| 自己的儀表板改名 | `DashboardView.vue` | 頁首就地輸入框改成「重新命名」對話框（Q2） |
| 指派版新增、改名 | `DashboardsAdminPage.vue` | 必填；修改時傳 `original` |
| 卡片標題 | `CardOptionsDialog.vue` | `optional`；佔位文字是目前語系解析的報表名稱 |
| 看板、甘特另存與改名 | `BoardPicker.vue` | 必填；修改時傳 `original` |

出廠設定（必填只有繁中）下，畫面是「名稱（zh-TW）*」一格，下面是收合的「其他語系」，看起來和現在差不多；越南廠把必填改成越南文後，第一格就是越南文。`maxlength` 一律 100。

### 10.2 顯示

- 所有顯示名稱的地方改 `resolveI18n(name, locale.value)`：報表清單、報表檢視頁標題與刪除確認、儀表板側欄與頁首、首頁儀表板、指派版清單、「新增卡片」的報表清單、卡片標題、分享給我的報表、看板與甘特的選單與頁首、分享對話框。
- 卡片標題：`resolveI18n(card.title)` 有值就用，沒有才用 `resolveI18n(reportNames[reportId])`。標題本身照退回鏈解析，所以只填了繁中標題的卡片，英文使用者看到的是繁中標題、不是英文的報表名稱（照「名稱欄位同一條退回鏈」與分流技術決定 26，例子寫進手冊）。
- 型別：`ReportItem.name`、儀表板清單項目的 `name`、`DashboardDetail.reportNames` 的值、`DashboardCard.title`、`TaskViewSummary.name` 改 `I18nText`（卡片標題 `I18nText | null`）。
- 前端送出一律送物件（`compactI18n` 去空白），不送字串。

### 10.3 版面

重新命名對話框、新增儀表板對話框、卡片設定對話框、看板另存對話框加了「其他語系」摺疊區後變高，要在 390、820、1366、1920 四種寬度驗過不橫向捲動（AGENTS.md「有畫面的改動」）。

## 11. Demo 種子與內建資料

### 11.1 群組與部門四語（T9）

`DemoPeople.Groups`、`DemoPeople.Departments` 的名稱從字串改 `I18nText`；繁中值不變（既有測試以繁中比對，如 `TaskBoardScenario` 的 `d.Name.Resolve("zh-TW") == SalesDepartment`）。和內建套件同義的群組沿用套件的譯名；th、vi 為機器翻譯，待母語者校對（功能清單 #30）。

| 群組代號 | zh-TW | en-US | th | vi |
|---|---|---|---|---|
| mis | MIS | MIS | MIS | MIS |
| sales | 業務 | Sales | ฝ่ายขาย | Kinh doanh |
| sales-manager | 業務主管 | Sales manager | ผู้จัดการฝ่ายขาย | Trưởng phòng kinh doanh |
| floor | 現場人員 | Shop floor | พนักงานหน้างาน | Nhân viên hiện trường |
| maint | 保養 | Maintenance | ซ่อมบำรุง | Bảo trì |
| qa | 品保 | Quality assurance | ประกันคุณภาพ | Đảm bảo chất lượng |
| planner | 生管組 | Production planning | วางแผนการผลิต | Kế hoạch sản xuất |
| purchasing | 採購組 | Purchasing | ฝ่ายจัดซื้อ | Mua hàng |
| warehouse | 倉管組 | Warehouse | คลังสินค้า | Kho |
| ops-manager | 進銷存管理組 | Inventory manager | ผู้จัดการคลังสินค้า | Quản lý kho |
| inv-viewer | 進銷存查詢組 | Inventory viewer | ผู้ดูข้อมูลคลังสินค้า | Người xem tồn kho |
| design | 產品設計 | Product design | ฝ่ายออกแบบผลิตภัณฑ์ | Thiết kế sản phẩm |

| 部門代號 | zh-TW | en-US | th | vi |
|---|---|---|---|---|
| exec | 總經理室 | General Manager's Office | สำนักงานผู้จัดการทั่วไป | Văn phòng Tổng giám đốc |
| sales | 業務部 | Sales | ฝ่ายขาย | Phòng Kinh doanh |
| design | 產品設計部 | Product Design | ฝ่ายออกแบบผลิตภัณฑ์ | Phòng Thiết kế sản phẩm |
| ops | 營運部 | Operations | ฝ่ายปฏิบัติการ | Phòng Vận hành |
| purchasing | 採購部 | Purchasing | ฝ่ายจัดซื้อ | Phòng Mua hàng |
| production | 生產部 | Production | ฝ่ายผลิต | Phòng Sản xuất |

`EnsureAsync` 的「已存在的不覆寫」不變：已經有這些群組、部門的本機資料庫不會自動補譯，刪資料庫重建或重新產生 Demo 才有。

### 11.2 系統程式名稱（T10）

`SeedData` 建「動態模組設計」（`DYN00000`）時給四語，譯名和選單一致（`shared/i18n/*/ui.json` 的 `menu.dynamicModules`：Dynamic Module Designer、ออกแบบโมดูลไดนามิก、Thiết kế module động）。

既有資料庫：這一列已存在，`SeedData` 原本不動它。改成**只在名稱恰好是出廠值 `{"zh-TW":"動態模組設計"}` 時補上三語**；管理員改過名稱（任何一格不同）就不動。同「權限設定」那一列的做法：系統列的狀態由種子維持。

### 11.3 示範報表、儀表板、卡片標題四語（T11）

- `DemoReports.Spec.Name`、`DemoDashboards.Spec.Name`、`CardSpec.Title` 改 `I18nText`；`CardSpec.ReportName` 仍以繁中值引用報表（引用鍵，不顯示）。
- 譯名由實作者補，th、vi 機翻並在檔頭註明，同套件的 `x-translation`。
- `DemoPreflight` 的同名比對：`IReportRepository.NameExistsAsync`、`IDashboardRepository.AssignedNameExistsAsync` 改成比繁中值。名稱 JSON 不能在資料庫端比對（§3），改成載入後在記憶體比（只在產生 Demo 前跑一次，同 `GroupNameExistsAsync` 的做法）。
- `TaskBoardScenario` 建的兩張看板（「業務部負載」「業務部任務」）補四語。

### 11.4 Demo 示範路徑（AGENTS.md「Demo 資料」）

| 分支 | 怎麼示範 |
|---|---|
| 成功路徑 | 登入後切 English、ไทย：首頁儀表板名稱、卡片標題、報表清單、看板名稱都換語言 |
| 退回 | 用 English 介面建一張報表，只填英文以外的必填格（繁中），切回英文看到繁中名稱 |
| 被擋 | 新增報表時清空「名稱（zh-TW）」存檔，看到「名稱必須有繁體中文」 |
| 改了才檢查 | 政策改成必填繁中＋英文後，編輯 Demo 既有報表只改定義不改名稱，可以存檔 |

不改的種子：沒有「停用」「個人化」分支要示範（名稱沒有這兩種狀態）。

## 12. 開關組合下誰負責

| 情況 | 名稱怎麼顯示、怎麼存 |
|---|---|
| 出廠設定（必填繁中） | 輸入框一格繁中＋收合的其他語系；顯示依使用者語系，缺翻退到繁中 |
| 必填改成越南文 | 新建的名稱要有越南文；既有只有繁中的名稱照常顯示、照常能改定義，改名時才要求越南文 |
| 停用某語系 | 輸入框不列它，已存的該語系譯名保留；使用者切不到這個語系，看不到它 |
| 匯入新語系 `ja`（i4d1） | 啟用後輸入框多一格 `ja`；沒填 `ja` 的名稱依 `ja` 的上層語系與預設語系退回 |
| 開啟應用程式套件 | 報表名稱只要求繁中（`PackageTextScope`），不跟政策 |
| 格式 2–8 的舊套件 | 報表名稱只收字串或只有繁中；照舊開得起來；帶 `excludeFromAi` 整份拒絕 |
| 套件或範本帶 `excludeFromAi: true` | 開啟套件：要權限設定，否則 403 整個回滾；從範本建立：沒有權限設定時寫 false 並警告 |
| 重新套用到旗標已是 true 的欄位 | 不清掉，不論套件寫 false 或省略 |
| 舊畫面（部署當下未重新整理） | 送字串照收（當繁中）；回應的物件顯示成 `[object Object]`，重新整理即好 |
| MCP | `list_reports` 回呼叫語系解析後的字串 |
| 沒開任何應用程式 | 自己建的報表、儀表板、看板照常多語；Demo 種子不跑 |

## 13. ERP 對照

以下依主控對主流產品的了解整理，未另行上網查證。

- **開源 ERP**：新版把記錄名稱（含報表、選單、儀表板動作名稱）存成以語言為鍵的 JSON 欄，使用者介面語言決定顯示哪一格，缺的退回英文；編輯時欄位旁有地球圖示可展開各語言。同 Formwright 的 `I18nText` 與「其他語系」摺疊區。
- **傳統大型 ERP**：報表標題、文字元素屬於開發物件，靠翻譯工具逐語言翻；使用者自己的版面變式名稱通常只有一種語言。Formwright 的報表與儀表板是使用者資料，比照開源 ERP 存多語，因為目標客戶的廠區常有兩三種語言的同事看同一張報表。
- **共用主檔與單據**：i3 不新增模組、主檔或單據，不碰單據鏈、數量契約、會計與開關組合矩陣（ERP 資料模型 §7 檢核 1–7 不適用）。群組、部門是核心的組織骨架（ERP 資料模型 §2.6），全系統一份，i3 只補 Demo 譯名。
- **核心能力**：用的是既有的 `I18nText`、`RequiredTextGuard`、退回鏈，沒有替特定應用程式開後門（檢核 9）。

## 14. 宗旨檢查（formwright-purpose-check）

1. **機制還是政策**：通過。名稱多語、退回、讀取寬鬆是平台機制；哪些語系必填是既有政策 `locale.requiredText`，不新增政策；長度 100 是技術限制（§15）。
2. **寫死掃描**：設計階段不適用；新的寫死項目列在 §15，審查 diff 時照 skill 的 grep 再跑。
3. **單一來源**：通過。長度上限各沿用一個常數（`ReportRules.MaxNameLength`、`DashboardRules.MaxTitleLength`、`TaskViewRules.MaxName`），套件驗證改引用 `ReportRules.MaxNameLength`，不另寫 200；必填語系由前端 `requiredTextLocales()` 讀 API 的政策值。前端輸入框的 `maxlength` 是既有的重複（同 `MAX_BOARD_NAME`），不在 i3 收斂，列為已知限制。
4. **兩個應用程式**：通過。報表、儀表板、看板是平台功能，CRM、進銷存、生產管理都在用。
5. **空系統**：通過。不開任何應用程式也能建多語的報表（任務報表）、儀表板、看板。
6. **套件只用定義組成**：通過。內建套件的報表名稱多語走格式 9 的通用欄位，沒有特例。
7. **ERP 對照**：通過，見 §13。
8. **權限 fail closed、驗證不只在前端、沒有執行期 DDL**：通過。名稱驗證在後端（`RequiredTextGuard`、長度、語系鍵），前端只是提早提示；套件帶「不提供給 AI」旗標時寫入 true 要權限設定，開啟套件沒有就整批回滾，舊格式與範本格式 1 帶這個屬性整份拒絕、不靜默丟掉（§8.1）；schema 變更走 EF migration（§5.2）。
9. **不限產業**：通過。取捨理由是「同一廠區有多種語言的同事」，服務業（多國籍客服）、貿易商（海外分公司）、批量工廠（外籍移工）都成立。

## 15. 可寫死清單

| 項目 | 值 | 類別 |
|---|---|---|
| 報表、儀表板名稱每語長度 | 100 | 技術限制（沿用） |
| 卡片標題每語長度 | 100 | 技術限制（沿用） |
| 任務檢視名稱每語長度 | 100 | 技術限制（沿用） |
| 選單分區每語長度 | 100 | 技術限制（沿用） |
| 字串名稱一律當繁中 | `zh-TW` | 格式相容規則（同 `section`） |
| 套件名稱必須有繁中 | `zh-TW` | 套件格式規則（p3 判斷 12） |
| 選單分區有值時必須有繁中 | `zh-TW` | 資料完整性（分組鍵是繁中） |
| 語系代碼格式 | 語系包設計 §4.1 | 格式守衛（沿用） |

## 16. 與既有設計或程式的矛盾

1. **語系包設計 §10.2 的路徑 `report:{code}/name`、`dashboard:{code}/name`（i3 之後）**：報表與儀表板沒有 `Code`，只有 Id；套件帶進來的報表靠 `AppReport.Code` 綁定。i4e 改成：套件報表用 `app:{appKey}/report:{code}/name`（綁定代碼優先，同 §10.2 第 1 點），使用者自建的報表、儀表板與 Demo 儀表板用 `local:report:{id}/name`、`local:dashboard:{id}/name`（只在本系統有效）。i3 不加 `Code` 欄。i4e 開工時改 §10.2 的文字。
2. **套件設計 `AppPackageReport` 的註解「Name 不分語系（同 Report.Name）」與 §4.2 的報表格式**：格式 9 起可多語；實作時一起改註解與套件設計的報表欄位表。
3. **套件驗證器的報表名稱 1–200 字 vs API 的 100 字**（`AppPackageValidator.V3.cs:210` vs `ReportRules.MaxNameLength`）：§8 統一成 100。
4. **儀表板前端 `maxlength="200"` vs 後端 100**（`DashboardPage.vue:78`、`DashboardView.vue:110`、`DashboardsAdminPage.vue:147` vs `ReportRules.CheckName`）：§5.4 改成 100。
5. **路線與功能清單**：roadmap 的 i4f 列與 i3 合併（本設計的 i3c 就是 i4f）；`docs/feature.md` 的 i3 兩項、i4 的 i4f 描述要對應改（主控改）。
6. **列表檢視的「另存新檢視」是單一輸入框**（`ViewMenu.vue:35`，名稱寫進每個必填語系與目前語系），i3 的名稱輸入是多語輸入框（Q1）。兩種寫法並存；Q1 照推薦定案後，若要統一，另開 backlog 票改列表檢視，不併進 i3。
7. **分流技術決定 27「報表清單排序依顯示語系；搜尋比對所有語系的值」**：現在沒有排序與搜尋功能，i3 不新增，這條作廢。之後加搜尋時照這條做（名稱 JSON 不能在資料庫端比對，要在記憶體比，或另存正規化的搜尋欄）。
8. **手冊**：第 1273 行「語系鍵不是繁中／English／ไทย／Tiếng Việt 會被拒絕」改成格式 9 的規則；第 956–989 行儀表板段、第 1058 行看板段補多語名稱與卡片標題的退回例子；報表段補名稱多語。
9. **mg1（生產管理通用化）也要改 `mes-mold.app.json`**：「動 `backend/apps/*.app.json` 的兩項工作不並行」（`.claude/rules/app-packages.md`），i3c 與 mg1 排開。
10. **MCP 設計 §5.5 末條「旗標不進套件與範本；匯出再匯入另一套系統會失去旗標」、第 848 行「套件與範本不帶（Q4）」、§17 第 4 點**：使用者 2026-11-21 裁決改成帶旗標（§8.1）；`AddFieldRequest` 的參數註解「套件、範本、內部合併一律 false」同步改。MCP 設計 §17 第 4 點原排的「簽核 8、s2e 9、10」格式順序已過時：格式 8 已是簽核，9 是本段，fg6、s2e 之後再排。權限軸 §7.3 對套件與範本旗標的契約不變，本設計照做。

## 17. 切段

每段可獨立合併、Demo 不壞。

| 段 | 內容 | migration | 共用檔 | 依賴 |
|---|---|---|---|---|
| i3a | 報表、儀表板、卡片標題、任務檢視名稱多語，前後端一起：實體與 migration `NameI18n`、共用轉換器搬到 Domain、DTO、驗證、MCP、稽核、匯出、刪除影響清單、前端六個輸入處與全部顯示、Demo 示範報表與儀表板與看板四語（§11.3）、手冊 | 有（SQLite＋PostgreSQL 各一） | `Report.cs`、`Dashboard.cs`、`TaskView.cs`、`DashboardLayout.cs`、兩份 model snapshot、`DemoReports.cs`、`DemoDashboards.cs` | i4a（已上線） |
| i3b | Demo 群組與部門四語、系統程式名稱四語與既有列補譯、選單分區的 API 收字串、拒收 `null`、缺繁中報錯（§9 第 2–4 點） | 無 | `DemoPeople.cs`、`SeedData.cs`、`ModulesController.cs`、`ModuleRules.cs`、`Module.cs` | i3a（共用轉換器） |
| i3c（＝i4f） | 套件格式 9：`reports[].name` 多語、語系鍵改代碼格式、`section` 驗證（§9 第 1 點）、欄位 `excludeFromAi` 與範本格式 2（§8.1：讀寫、套用的權限檢查與只加不減、預覽計數、匯出）、舊版不放寬的拒收案例、內建三套件升格式與報表名稱四語、`app.schema.json`、手冊與套件設計、MCP 設計 §5.5 回填 | 無 | `AppPackageData.cs`、`AppPackageValidator*.cs`、`TemplatePackage.cs`、`TemplateValidator.cs`、`TemplateInstantiator.cs`、`TemplateExporter.cs`、`AppExporter.cs`、`app.schema.json`、`backend/apps/*.app.json` | i3a；不和 mg1 並行 |

- i3a 是最大的一段（前後端都動），估約 40 個檔。真的太大時，可把任務檢視（後端 `TaskViewService`、前端 `BoardPicker.vue`）拆成 i3a2，但 migration 只能有一組，留在 i3a1；拆了以後 i3a1 的 migration 仍要改 `Dynamic_TaskView`，i3a2 只動程式。
- i3b、i3c 都依賴 i3a；兩者之間不相依、沒有共用檔，可以平行，但 i3b 很小，建議 i3a → i3b → i3c 依序做。
- i3a 是本段唯一的 migration，不能和其他會加 migration 的段平行（後端規則）。
- i3c 完成後，roadmap 的 i4f 一起標完成；i4e 可以開工。

## 18. 可驗收案例

### 18.1 資料轉換

| # | Given | When | Then |
|---|---|---|---|
| D1 | 升級前報表名稱「近 90 天訂單」 | 跑 migration | `NameI18n` 是 `{"zh-TW":"近 90 天訂單"}`；英文介面顯示「近 90 天訂單」 |
| D2 | 名稱含引號與反斜線 `A "B" \C` | 跑 migration | 讀回原字串，JSON 合法 |
| D3 | 名稱是三個空白 | 跑 migration | `NameI18n` 是 `{}`，畫面顯示空白，不報錯 |
| D4 | 儀表板版面裡卡片 `title` 是字串「本月訂單」 | 打開儀表板 | 卡片標題「本月訂單」；存一次版面後 `LayoutJson` 的 `title` 變成物件 |
| D5 | 個人版版面卡片 `title` 是空字串 | 打開儀表板 | 顯示報表名稱 |
| D6 | 已轉換的資料庫 | 跑 Down | `Name` 是繁中值；只有英文的名稱還原成空字串 |

### 18.2 輸入與驗證

| # | Given | When | Then |
|---|---|---|---|
| V1 | 出廠設定 | 建報表，名稱只填英文 | 400「名稱必須有繁體中文」 |
| V2 | 必填改成繁中＋越南文 | 編輯一張只有繁中名稱的報表，只改定義 | 存檔成功 |
| V3 | 同 V2 | 同一張報表把繁中名稱改掉 | 400「名稱必須有繁體中文、越南文」 |
| V4 | — | 建儀表板，名稱繁中 101 字 | 400，訊息帶「100」 |
| V5 | — | 送 `{"zh-TW":"A","en-US":null}` | 400「語系 en-US 的文字不能是空值」 |
| V6 | 已安裝未啟用的 `ja` | 送 `{"zh-TW":"A","ja":"エー"}` | 存檔成功、讀回保留 `ja` |
| V7 | — | 舊畫面送 `"name":"本週出貨"` 建看板 | 建立成功，`name` 是 `{"zh-TW":"本週出貨"}` |
| V8 | 必填只有越南文 | 舊畫面送字串名稱 | 400「看板名稱必須有越南文」 |
| V9 | — | 卡片標題 `{"en-US":"Orders"}`（沒有繁中） | 存檔成功（選填不檢查必填語系） |
| V10 | 看板名稱 `{"zh-TW":"本週出貨","en-US":"This week"}` | 另存副本 | `{"zh-TW":"本週出貨（副本）","en-US":"This week (copy)"}` |

### 18.3 顯示

| # | Given | When | Then |
|---|---|---|---|
| S1 | 報表名稱 `{"zh-TW":"訂單","en-US":"Orders"}` | 英文介面開報表清單 | 顯示「Orders」 |
| S2 | 同 S1 | 泰文介面（泰文沒譯名、預設語系繁中） | 顯示「訂單」 |
| S3 | 卡片標題只有繁中「本月訂單」，報表有英文名稱 | 英文介面看儀表板 | 卡片標題「本月訂單」 |
| S4 | 卡片沒有標題，報表 `{"zh-TW":"訂單","en-US":"Orders"}` | 英文介面 | 卡片標題「Orders」 |
| S5 | 同 S1 | MCP `list_reports`，呼叫語系 en-US | `name` 是字串「Orders」 |
| S6 | 同 S1 | 英文介面匯出 Excel | 工作表名稱「Orders」 |
| S7 | 指派版 `{"zh-TW":"業務儀表板","en-US":"Sales"}`，預設語系繁中 | 管理員用英文介面改版面 | 稽核目標名稱「業務儀表板」 |

### 18.4 套件

| # | Given | When | Then |
|---|---|---|---|
| P1 | 格式 9，報表名稱 `{"zh-TW":"A","en-US":"B"}` | 開啟 | 報表名稱兩語都在 |
| P2 | 格式 8，報表名稱 `{"zh-TW":"A","en-US":"B"}` | 預覽 | 整份拒絕「formatVersion 9 才支援多語報表名稱」 |
| P3 | 格式 8，報表名稱字串 | 開啟 | 成功，`{"zh-TW":…}` |
| P4 | 格式 8，任一名稱帶 `ja` 鍵 | 預覽 | 整份拒絕（不支援的語系：ja） |
| P5 | 格式 9，任一名稱帶 `ja` 鍵、`ja` 未安裝 | 開啟 | 成功、保留 `ja`（語系包設計 U6） |
| P6 | 任一格式，報表名稱 150 字 | 預覽 | 拒絕，訊息帶 100 |
| P7 | 模組 `section` 是 `{"en-US":"Sales"}` | 預覽 | 拒絕「{代號}.section：zh-TW 為必填」 |
| P8 | 只有繁中報表名稱的套件 | 匯出 | `name` 寫成字串，除 `formatVersion` 外和格式 8 匯出一致 |
| P9 | 內建 crm-sales | 英文介面開啟後看報表清單 | 「Opportunities by stage」之類的英文名稱 |
| P10 | 格式 9，欄位 `excludeFromAi: true`，操作者有權限設定 | 開啟 | 欄位 `ExcludeFromAi` 是 true；MCP 通道 schema 看不到這個欄位 |
| P11 | 同 P10，操作者沒有權限設定 | 開啟 | 403 `permissionAdminRequired`，沒有任何模組留下 |
| P12 | 格式 8，任一欄位帶 `excludeFromAi`（true 或 false） | 預覽 | 整份拒絕「formatVersion 9 才支援」 |
| P13 | 共用主檔已開、`partner.taxId` 已由權限設定者標 true；新套件同欄位省略旗標 | 開啟（重新套用） | `partner.taxId` 仍是 true |
| P14 | 範本格式 2 帶 `excludeFromAi: true`，設計者沒有權限設定 | 從範本建立 | 建立成功，欄位是 false，`warnings` 有「已略過…共 1 個欄位」 |
| P15 | 範本格式 1 帶 `excludeFromAi` | 從範本建立 | 整份拒絕 |
| P16 | 欄位旗標是 true 的模組 | 匯出套件、再到另一套系統開啟 | 旗標仍是 true |
| P17 | 套件有 2 個 `excludeFromAi: true` 欄位 | 預覽 | 預覽列「會標為不提供給 AI 的欄位：2 個」 |

### 18.5 選單分區與種子

| # | Given | When | Then |
|---|---|---|---|
| M1 | — | `PUT modules/{key}` 送 `"section":"主檔"` | 成功，分區 `{"zh-TW":"主檔"}` |
| M2 | — | 送 `"section":{"en-US":"Master"}` | 400「選單分區有其他語系時必須有繁體中文」 |
| M3 | — | 送 `"section":{"zh-TW":"主檔","en-US":""}` | 成功，存的是 `{"zh-TW":"主檔"}` |
| G1 | 新資料庫 | 產生 Demo、英文介面開群組頁 | 「Sales」「Quality assurance」等英文名稱 |
| G2 | 既有資料庫、「動態模組設計」是出廠值 | 啟動 | 名稱補齊四語 |
| G3 | 既有資料庫、管理員把「動態模組設計」改成「設計器」 | 啟動 | 名稱不動 |

## 19. 測試重點

- **migration**（`Category=Postgres`，SQLite 與 PostgreSQL 雙跑）：照 `MailSettingsMigrationTests` 的做法先 migrate 到前一版、塞舊格式資料、再 migrate 到最新；D1–D3、D6；三張表都驗；`MigrationConsistencyTests`。
- **轉換器**：字串、物件、`null`、數字（丟例外）；`DashboardLayout.Parse` 讀字串標題、空字串標題、物件標題（Domain 單元測試）。
- **驗證**：V1–V10；看板與甘特的訊息用字分種類；長度訊息帶上限。
- **repository**：`NameExistsAsync`、`AssignedNameExistsAsync` 改記憶體比對（加 `Category=Postgres`）；刪除影響清單、刪群組影響清單讀名稱。
- **解析處**：MCP `list_reports`、Excel 工作表名稱、稽核目標名稱用預設語系（S5–S7）。
- **套件**：P1–P8；P10–P17（旗標：開啟、重新套用只加不減、範本無權限略過、舊格式拒收、往返、預覽計數；P13 的重新套用含共用主檔綁定與接回孤兒欄位兩條路徑；權限軸 PAX 系列的守衛測試照過）；`AppSchemaTests`（schema 的 `pattern`）；`AppCatalogChecks` 內建套件全部通過；`ModuleSectionPackageTests` 補驗證器案例；舊格式帶新語系鍵被拒（`.claude/rules/app-packages.md`）。
- **種子**：改前 grep `DemoReportsTests`、`DemoDashboardsTests`、`DemoOrganizationTests`、`TaskBoardScenarioTests`、`TaskViewTests`、`TaskViewShareTests`、`SeedDataTests`、`CrmDesignCaseAppTests`、`TaskReportsApiTests` 對名稱的斷言（以繁中比對的照過，`r.Name` 改 `r.NameI18n.Resolve("zh-TW")`）；G1–G3。
- **前端 vitest**：六個輸入處送出物件、修改時帶 `original`；卡片標題與報表名稱的退回（S3、S4）；`resolveI18n` 用在所有顯示處（清單、頁首、側欄、新增卡片清單）；型別改動後 `vue-tsc` 通過。
- **畫面**：四種寬度不橫向捲動（§10.3）；Demo 冒煙：切 English 看首頁儀表板名稱與卡片標題。

## 20. 待確認清單

### 20.1 行為類（主控暫定，待使用者確認）

- **Q1 名稱輸入方式：用既有的多語輸入框（推薦）。**
  背景：報表、儀表板、卡片標題、看板的名稱要能填多語。系統裡已有兩種寫法：設計資料（模組、欄位）用多語輸入框，必填語系各一格、其他語系收在摺疊區；列表檢視的「另存新檢視」只有一格，存檔時把同一段文字寫進每個必填語系。
  例子：越南籍主管在越南文介面建報表，必填是繁中。用多語輸入框，他看到「名稱（zh-TW）*」一格，必須填一個繁中名稱，可以展開再填越南文；用單一輸入框，他填「Báo cáo tồn kho」，系統把這段越南文存進繁中與越南文兩格，台灣同事看到的也是越南文。
  - 推薦：多語輸入框。這段的目的就是讓名稱有真正的譯名；改名時每一格看得見，不會有「哪幾格是當初複製的」的暗規則；出廠設定下畫面就是一格加一個收合區。
  - 替代：單一輸入框（同列表檢視）。對一般使用者最簡單；代價是填不了譯名（只能切換介面語言再改那一格），改名時其他格是否跟著改要另訂規則。
  - 選錯的代價：小。只改六個前端輸入處，資料格式相同、後端不用改。
- **Q2 自己的儀表板在頁首改名：改成「重新命名」對話框（推薦）。**
  背景：現在編輯版面時頁首的名稱直接變成輸入框，離開就存。多語輸入框有摺疊區，塞在頁首一行會擠，手機寬度更明顯。
  - 推薦：編輯狀態時名稱旁出現「重新命名」按鈕，開對話框改各語系，按儲存才存。代價：多點一下。
  - 替代：頁首只就地改目前介面語系那一格，其他語系不給改。代價：要改譯名得切介面語言，和其他畫面不一致。
  - 選錯的代價：小，只動 `DashboardView.vue`。
- **Q3 看板、甘特圖另存副本：每個語系加自己語言的「副本」後綴（推薦）。**
  例子：「本週出貨／This week」另存後是「本週出貨（副本）／This week (copy)」。
  - 推薦：各語系各自的後綴。代價：後綴要進 `IMessageCatalog` 四語表。
  - 替代：每個語系都加繁中的「（副本）」。代價：英文介面看到「This week（副本）」。
  - 選錯的代價：極小。
- **Q4 選單分區有其他語系卻沒有繁中：存檔時報錯（推薦）。**
  背景：分區的分組鍵是繁中（不同應用程式同名的分區靠繁中合成一組）。現在只填英文分區「Sales」存檔，系統當成沒有分區、默默丟掉，使用者以為存了。
  - 推薦：回「選單分區有其他語系時必須有繁體中文」。代價：必填語系設成越南文的系統，設計者填分區時也要填繁中。
  - 替代：維持現狀（默默當成不分區）。代價：設計者填了分區卻沒出現，查不到原因。
  - 替代 2：分組鍵改用系統預設語系的值。代價：既有選單分組要重算，切換預設語系時分組會變，範圍超出 i3。
  - 選錯的代價：小。只是把靜默丟掉改成報錯，沒有資料會被改。
- **Q5 開啟套件前的預覽列出「會標為不提供給 AI 的欄位：N 個」（推薦）。**
  背景：使用者 2026-11-21 裁決套件可以帶「不提供給 AI」旗標。標了的欄位，AI 工具看不到、不能拿來篩選。管理員開啟一份外部拿到的套件時，現在的預覽看不出哪些欄位會被標。
  例子：廠商給的人事套件把「身分證字號」「月薪」標了旗標，預覽多一行「會標為不提供給 AI 的欄位：2 個」，展開列出欄位名稱。
  - 推薦：預覽列計數與欄位名稱。代價：預覽畫面多一行。
  - 替代：不列，開啟後到欄位設定才看得到。代價：管理員不知道開啟後 AI 工具會少看到哪些欄位，事後排查「AI 怎麼查不到月薪」要一個個欄位翻。
  - 選錯的代價：極小，只動預覽回應與畫面一行。

### 20.2 技術類（主控已定，可推翻）

- **T1** 三張表改 `NameI18n`、刪舊字串欄；卡片標題型別改 `I18nText?`（§5.1）。
- **T2** 一個 migration，EF 產生後插入資料搬移 SQL；舊值放繁中、空白存 `{}`、Down 只還原繁中（§5.2）。
- **T3** 卡片標題不寫 migration，讀取寬鬆、寫出一律物件；代價是降版後新格式的版面讀不懂（§5.3）。
- **T4** 每語系長度一律 100，儀表板前端 200 改 100（§5.4）。
- **T5** 共用「字串或物件」轉換器搬到 Domain，所有名稱輸入與模組 `section` 都收字串，字串當繁中（§6.1）。
- **T6** API 回 `I18nText` 物件由前端解析；MCP、匯出、影響清單用請求語系，稽核用預設語系（§6.2）。
- **T7** 卡片標題選填，不檢查必填語系（§7.2）。
- **T8** 格式 9 合併 i4f；舊版報表名稱只收字串或只有繁中；所有版本報表名稱上限改 100；內建三套件升版（§8）。
- **T9** Demo 群組、部門四語，繁中值不變（§11.1）。
- **T10** 系統程式「動態模組設計」四語；既有列只在是出廠值時補譯（§11.2）。
- **T11** 示範報表、儀表板、卡片標題、看板四語；Demo 預檢改在記憶體比繁中值（§11.3）。
- **T12** i4e 的報表、儀表板路徑用 `app:{appKey}/report:{code}` 與 `local:`，不加 `Code` 欄（§16 第 1 點）。
- **T13** 「不提供給 AI」旗標的做法（使用者 2026-11-21 裁決的落實）：放在 `TemplateField`，套件與範本共用；範本格式升 2 防舊版靜默丟旗標；格式 2–8 與範本格式 1 帶這個屬性整份拒絕；套件 false 或省略時對既有欄位送 `null`（不改）；開啟套件沒權限 403 回滾、從範本建立沒權限寫 false 加警告（權限軸 §7.3）；匯出只寫 true（§8.1）。

## 21. 留給實作者

- Demo 報表、儀表板、卡片標題、看板的英泰越譯名（§11.3）與內建三套件 13 份報表的譯名（§8）。
- `IMessageCatalog` 的「（副本）」鍵名與四語文案（Q3）。
- 重新命名對話框的版面與文案（Q2）。
- i3a 要不要拆 i3a1、i3a2，看實作計畫估的檔數（§17）。
