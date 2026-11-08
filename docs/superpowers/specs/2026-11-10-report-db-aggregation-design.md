# 報表資料庫端聚合 設計文件

> 2026-11-10｜上位文件：[ERP 路線](2026-11-06-erp-roadmap-design.md)（階段 3 開頭）；相關：[報表與儀表板](2026-11-05-reports-design.md)、[儲存層負載測試與數值精度報告](2026-11-06-storage-load-report.md)、[動態模組與自訂欄位](2026-11-03-dynamic-modules-design.md)
> 使用者已裁決：先做這件；PostgreSQL 目前不加；SQLite 維持；`mmap` 預設關閉。標「（我的判斷）」的是我代為決定、可以改的；§14 是要主控裁決的問題。

> 第一輪審查（6 必修、1 建議）與第二輪審查（4 必修、2 建議）已併入。第二輪主控裁決**取消 `IndexScaleExact` 標記**：改為 Scale 變動同交易重建索引、一次性回填舊資料、`ScaledValue` 只在精確時才有值（§4.1）；探測改用索引表上的「截斷」欄位與部分索引（§8.3）；報表讀取在同一份快照內並複查欄位設定（§8.4）；順帶修正記憶體路徑的多欄分組鍵撞名（§10）。總原則見 §3。

## 1. 結論

1. **報表的聚合改在 SQL 內做，查詢成本跟命中筆數成正比，不再跟來源模組總筆數成正比。** 走 SQL 的形狀（§4）不載入任何記錄、不受 20 萬筆載入上限與 5 萬列 `Reports:MaxRows` 限制。
2. **做不到 SQL 的形狀退回記憶體路徑，但先用 SQL 縮小要載入的記錄。** 縮小只用「證明過精確、不可能排除記憶體路徑會納入的記錄」的條件，記憶體路徑照舊重套全部條件，所以縮小只會少載入，不會改結果；拿不準的條件不拿來縮小。載入上限只對縮小後的筆數生效，並在回傳裡標明走了哪條路、為什麼退回（§8）。
3. **權限不動：** 模組讀取、欄位可見、資料範圍的判斷全部留在 C#，在任何 SQL 產生之前完成；SQL 只收到已授權的欄位與範圍，每個涉及的記錄別名都強制帶範圍條件（§6）。
4. **結果與現行實作逐項相同**，保證方式是三層：現有報表測試整套在 SQL 路徑再跑一次、隨機定義的新舊對照測試、壓測資料上的對照（§7）。
5. **精度規則不變：** 加總用索引時就算好的精確整數（`ScaledValue`）在 SQL 內加，轉回 decimal；平均與比率在 C# 用 decimal 除；金額 15 位有效數字的規則不動（§9）。
6. **前提是欄位有進索引。** 現行索引只收「可篩選、可排序、多選、參照」的欄位（`RecordIndexer.ShouldIndex`），沒勾的欄位值只在 `ValuesJson` 裡。未索引的欄位仍可用於報表，只是走記憶體路徑（縮小後）。這是產品面的取捨，見 §14 Q1。

## 2. 現況與代價

`ReportRunner.RunAsync` 的流程（`ReportService/ReportRunner.cs`）：

1. 載入模組、授權、綁定欄位、檢查隱藏與型別（步驟 1 至 4，都是 C# 判斷，不碰記錄）。
2. 步驟 5：`ListForReportAsync` 把來源模組全部未刪除、範圍內的記錄載入（上限 `MaxLoadRows` 20 萬，多取一筆判斷超過就拒絕）；有子模組時子模組同樣載入。
3. 逐筆解析 `ValuesJson` 組儲存格（`BuildCellsAsync`），參照欄位另外查標題與狀態，沿參照帶出的欄位另外查目標記錄。
4. 步驟 6：條件與日期區間在載入後才套用；組列、左合併子記錄；列數超過 `MaxRows`（5 萬）回「資料太多」。
5. `ReportCalculator.Calculate` 分組、去重、彙總（純函式，`Domain/Reports`）；輸出（排序、標籤、明細列）在 `ReportRunner.Output.cs`。

所以成本由來源筆數決定：19 萬筆單人 1.8 秒、20 人同時 8 至 22 秒；30 萬筆以上直接被拒（儲存報告 §4.4）。被拒的報表仍先讀 20 萬筆再丟掉，每次約 0.6 秒的無效 CPU。

## 3. 總原則與不變式（任何實作都要守）

**總原則（主控裁決）：結果與記憶體路徑不同就是錯，速度其次。** 任何 SQL 無法證明和記憶體路徑逐筆相同的情形，一律退回記憶體路徑並寫進 `fallbackReasons`。用來縮小後備載入範圍的條件，只能是證明過精確、不可能排除記憶體路徑會納入之記錄的條件；不確定就不拿來縮小。

| # | 不變式 | 怎麼守 |
|---|---|---|
| I1 | 授權先於 SQL | 步驟 1 至 4 原封不動；SQL 計畫只從授權後的 `Bound` 物件產生，不直接讀定義 |
| I2 | 範圍每個別名都帶 | 主、子、帶出目標、參照分組目標各自帶 `ModuleId`、`DeleteFlag = 0`、`OwnerUserId` 範圍；產生器缺任何一項就丟例外，不是省略（fail-closed） |
| I3 | 不默默回錯數字 | 任一欄位、運算子、型別、資料狀態不在支援表（§4）內，或欄位資格、資料狀態探測、快照複查沒過（§8.3、§8.4），就退回記憶體路徑；記憶體路徑也做不到就回明確的「資料太多」或「查詢逾時」，不回部分結果。不使用容差、門檻推論這類「幾乎一定對」的判斷 |
| I4 | 兩條路徑的結果相同 | §7 的對照測試；結果的組裝（標籤、排序、欄位、下鑽、卡片選項）兩條路徑共用同一份程式 |

## 4. 哪些形狀走 SQL

### 4.1 判斷單位

以報表定義裡每個「欄位引用」（`Bound`）為單位判斷，整張報表的所有引用都支援才走 SQL，否則整張退回（但仍縮小，§8）。一個引用「可 SQL」的共同前提：

- 欄位啟用中，且 `RecordIndexer.ShouldIndex(field)` 為真（索引列齊全）。
- 沿參照帶出（`Via`）時，來源的參照欄位與目標欄位都要符合；目標必須是**動態模組**，且觀看者對它有讀取權限與標題可見（`TitleVisibleTo`）。實體目標（`ReferenceTarget.EntityKind`，標題來自 `IReferenceProvider`）退回。目標欄位本身若是參照欄位（巢狀）退回。
- **數值索引不變式：`ScaledValue` 非 NULL ⇒ 它等於「目前 Scale 下的原值 × 10^Scale」且沒有捨入。** 不用任何旗標（取消第一輪的 `IndexScaleExact`：旗標放在可整包寫入的 `FieldConfig`，會被 API 改回 true，補算也只驗原值不驗索引）。靠三件事維持：
  1. **換算改為精確。** `ScaledNumber.Of` 現在對原值 × 10^Scale 四捨五入；索引專用的換算改成「乘完不是整數，或超出 `long`，就回 NULL」。正常寫入已由 `NormalizeDecimal` 限制小數位數不超過 Scale、公式與彙總在寫入時捨入到 Scale，所以新資料一律精確；只有 Scale 被調低後的舊值（1.23 的欄位把 Scale 由 2 改成 1）會變成 NULL，而不是被悄悄捨成 12。
  2. **Scale 變動時，在同一個交易內重建該欄位的索引**（Q4）。欄位更新與索引重建一起提交或一起回滾，沒有「設定已改、索引未改」的可見狀態（§8.4 的快照讀取再補上讀取端的保證）。
  3. **升級前的舊資料：一次性回填。** 依目前 Scale 從原值重算全部數值欄位的 `ScaledValue`，並補寫文字截斷欄位（下一點）。**選啟動時以版本列守門、只跑一次的工作，不用 EF migration 的資料步驟**，理由：(a) 要用 decimal 從 JSON 原值精確重算，SQLite 的 JSON 函式只給 double，migration 的 SQL 做不到；(b) 換算規則（`ScaleOf`、截斷規則）在應用程式裡，migration 不應依賴會變動的應用程式程式碼；(c) 需要分頁、可中斷續跑，migration 是單一交易。版本列放在既有的 `SystemSetting`（鍵 `IndexBackfillVersion`），工作每處理完一個模組就推進該模組的進度、全部完成才寫入目前版本；**版本列未達目前版本時，報表一律走記憶體路徑**（結果仍正確，只是不走 SQL）。
  - `ScaledValue` 為 NULL 但 `NumberValue` 非空的列有三種來源（溢位、Scale 調低後的不精確舊值、尚未回填的舊列），一律由 §8.3 的數值探測攔下退回，原因統一為 `scaledUnavailable:<欄位 id>`。
- **文字類欄位的索引值會被截斷**（`HandlerHelpers.TruncateIndexText`：超過 440 個 UTF-16 單位取前 440 個；第 440 個單位是高代理字元時取前 439 個，不切斷 surrogate pair）。截斷後分組會把不同的值併成一組，`eq`、`ne`、`in`、`notIn` 也會判錯（`a`×440 加 `b` 與 `a`×440 的索引值相同）。**不用長度或位元組門檻推論，改在索引表記下「這一列被截斷了」：**
  - `Dynamic_RecordIndex` 新增 `TextTruncated`（布林，預設 0，EF migration 加欄，不是執行期 DDL）。`RecordIndexer.BuildField` 依實際截斷規則寫入：索引值與原值字串不相等即為 1。選項、多選、使用者、參照、是否等不經截斷的型別永遠是 0。
  - 一併加兩個部分索引，讓探測只走罕見的壞列、不掃資料：`(FieldId) WHERE TextTruncated = 1` 與 `(FieldId) WHERE ScaledValue IS NULL AND NumberValue IS NOT NULL`。
  - 舊資料的 `TextTruncated` 併入上述一次性回填；回填完成前走記憶體路徑。
  - 分組或任何條件（`eq`、`ne`、`in`、`notIn` 都算）涉及的文字類欄位，只在**查詢範圍內有被截斷的列**時才退回記憶體路徑（§8.3），原因 `textTruncated:<欄位 id>`。沒有被截斷的列，信箱、網址、`MaxLength` 大於 440 的文字欄位也能走 SQL（取消第一輪「依欄位型別與長度靜態排除」的規則）。

### 4.2 報表形狀對照

| 報表元素 | SQL 做法 | 備註 |
|---|---|---|
| 分組：選項、使用者、是否、文字、數字、日期 | `GROUP BY` 最終分組鍵運算式（§5.3） | 數字以 `ScaledValue` 分組，C# 轉成與現行相同的鍵字串（一對一，不會合併） |
| 分組：日期分桶（日、週、月、季、年） | 方言函式 `DateBucket`，輸出與 `ReportDates.BucketKey` 完全相同的字串 | 週用 ISO 週（週一起算），季、年同現行格式；有測試逐日對照 |
| 分組：日期時間欄位 | 取 `DateValue` 的日期部分（台北牆上時間，索引本來就存牆上時間） | |
| 分組：多選 | 不限 `Seq = 0`，每個選項一列，一筆記錄落在多個分組 | 與現行「各組相加可能大於總計」一致 |
| 分組：參照（動態模組目標） | 鍵 = 空 `''`／已刪除 `__missing`／範圍外 `__restricted`／`RefKey`；標題另外用一次查詢補（只查結果裡出現的鍵） | 現行語意見 `ReferenceResolver.ResolveModuleAsync` |
| 分組：沿參照帶出（Via） | 來源參照索引列 → 目標記錄（含範圍）→ 目標欄位索引列，鍵運算式同上 | |
| 量值 `sum`／`avg` | `SUM(ScaledValue)`（高低位拆開加，見 §9）與 `COUNT`，C# 轉 decimal 後相除 | 與現行 `picked.Sum() / picked.Count` 同一組輸入、同一種運算 |
| 量值 `min`／`max` | 數字用 `ScaledValue`，日期用 `DateValue`，C# 轉 `DayNumber` | |
| 量值 `countMain`／`countChild` | `COUNT`（有子模組時 `countMain` 用內層去重後的列數） | 無子模組時 `countChild` 為 0，與現行相同 |
| 量值 `ratio`（條件占比） | `SUM(CASE WHEN 條件 THEN 1 ELSE 0 END)` 除以範圍內筆數 | 條件用同一個謂詞產生器，見 §5.4 |
| 條件（篩選、量值條件） | 謂詞產生器（§5.4），運算子逐一對應現行 `Matches`；數值條件用 `ScaledValue` 精確整數比較 | 文字 `contains`／`startsWith` 退回，理由見下 |
| 日期區間、主要日期欄位 | 主要日期欄位的 `DateValue` 區間，上界為「迄日隔天 00:00 之前」 | 與 `ReportDates.Contains` 相同：空值與非 ok 不在區間內 |
| 合併子模組（左合併） | 主記錄左連子記錄，兩層聚合去重（§5.5） | 條件落在子欄位時，子記錄全被篩掉的主記錄也消失 |
| 卡片：前 N 名、累計占比 | 不動：在分組結果上算 | 兩者都吃 `RunAsync` 的結果，路徑無關 |
| 卡片：與前 N 期比較 | 不動：每期各呼叫一次 `RunAsync` | 每期都走 SQL，成本乘以 N + 1 |
| 儀表板、卡片、分享 | 不動 | 都呼叫 `RunCardAsync` → `RunAsync` |
| 明細報表（有 `Columns`、不分組） | 退回記憶體路徑（縮小後） | 輸出列數本來就受 `MaxRows` 限制，載入的是縮小後的命中記錄 |
| 下鑽 | 退回記憶體路徑；沒有子模組時，分組鍵與條件一併當縮小條件；有子模組時只用**主模組**上的精確條件縮小（§8.1） | 本來就最多回 500 列（`DrillLimit`）；子模組分組鍵的下鑽要先組出完整的左合併列才能篩，不縮小子側 |

**文字 `contains`／`startsWith` 為什麼退回（我的判斷）：** 現行是 `OrdinalIgnoreCase`（含非 ASCII 的大小寫折疊），SQLite 的 `LIKE` 只折疊 ASCII，兩者在非英文字母上會有不同結果。要精確一致得在連線上註冊自訂函式，成本與方言風險都高於效益（會計報表的條件以科目、狀態、日期為主）。退回後仍會用其他條件縮小。

### 4.3 型別對照

| 欄位型別 | 索引欄 | 可分組 | 可條件 | 可 sum／avg | 可 min／max |
|---|---|---|---|---|---|
| 整數、小數、金額、百分比 | `NumberValue`、`ScaledValue` | 是 | 是 | 是 | 是 |
| 公式（結果數字） | 同上 | 是 | 是 | 是 | 是 |
| 公式（結果日期）、日期、日期時間 | `DateValue` | 是（可分桶） | 是 | 否 | 是 |
| 選項 | `TextValue`（`opt_<id>`） | 是 | 是 | 否 | 否 |
| 多選 | `TextValue`，每選項一列 | 是 | 是 | 否 | 否 |
| 是否 | `NumberValue`（1／0） | 是（鍵 `true`／`false`） | 是 | 否 | 否 |
| 使用者 | `RefKey` | 是 | 是 | 否 | 否 |
| 參照（動態模組） | `RefKey` | 是 | 是 | 否 | 否 |
| 文字、電話、信箱、網址、時間、自動編號 | `TextValue`（可能被截斷，有 `TextTruncated` 標記） | 是（範圍內無截斷列，§8.3） | 是（`contains`／`startsWith` 除外；同上） | 否 | 否 |
| 彙總、附件、長文字 | 現行就排除 | | | | |

## 5. 查詢怎麼組

### 5.1 分層

- **Application**：新增 `ReportSqlPlanner`，把授權後的 `Bound`、條件、分組、量值、範圍轉成與資料庫無關的**計畫物件**（欄位 id、運算子、值、範圍名單、各模組 id）；計畫物件不含 SQL 字串。無法規劃時回傳「退回原因」。
- **Domain**：新增 `IReportQueryRepository`，一個方法收計畫、回傳原始列（分組鍵陣列加量值的原始數字）；縮小用的方法是 `ListForReportAsync` 多收一個計畫片段參數。
- **Persistence**：新增 `ReportQueryBuilder`（與 `DynamicQueryBuilder` 並列）把計畫轉成參數化 SQL，經 Dapper 在 EF 的連線與交易上執行。SQL 只由常數與白名單片段組成，欄位 id、值、範圍名單一律走參數。需要方言的運算式（日期分桶、取日期部分）加進 `ISqlDialect`，目前只有 `SqliteDialect` 實作，不在查詢建構器裡寫 SQLite 專屬語法。

### 5.2 驅動表與索引

直接寫 `r.Id IN (SELECT …)` 時，SQLite 可能先掃整個模組再逐筆檢查（儲存報告 §8 第 2 項的問題）。報表查詢改成**由最能縮小的條件驅動**：

- **驅動條件依估計的命中筆數挑，不固定順序。** 候選是主模組上能由既有索引直接定位的條件：日期區間或日期篩選（`(FieldId, DateValue)`）、參照／選項／使用者／電話／時間／自動編號的 `eq` 與 `in`（`(FieldId, RefKey)`、`(FieldId, TextValue)`；文字候選只限 §4.1 欄位層通過者）。數值條件不當驅動（索引欄是 double，只能用於不精確的範圍）。
- **估計方式：封頂的索引區間計數。** 對每個候選執行 `SELECT COUNT(*) FROM (SELECT 1 FROM Dynamic_RecordIndex WHERE FieldId = @f AND <區間或相等> LIMIT @cap)`，`in` 對每個值各數一次再相加。成本最多是 `cap` 個索引項，與資料量無關。先以 `cap = 1,000` 數所有候選；若全部都碰到上限，改以 `cap = 100,000` 再數一次；挑計數最小者，同分取日期條件。不用 `ANALYZE`（儲存報告 §5 已評估有退步風險）。一個候選都沒有時掃 `(ModuleId, DeleteFlag)`。
- 有驅動條件時，從索引表的條件子查詢出發，用 `CROSS JOIN` 固定連接順序（SQLite 的慣用做法，規劃器不會重排）再連 `Dynamic_Record`；其他欄位用主鍵 `(RecordId, FieldId, Seq)` 查。不新增索引，不改 schema。其餘條件留在 `WHERE`。
- 合併子模組的報表只以**主模組**的條件當驅動。條件只落在子模組、且子條件命中極少時，這樣會偏慢（主模組日期窗內的記錄都要走一遍），v1 接受並在 §11 列為已知限制，壓測要量。
- 實作時要用 `EXPLAIN QUERY PLAN` 看過壓測資料上的實際計畫，並寫計畫測試：(1) 典型形狀（區間加分組、參照等於加分組）的計畫不得出現對 `Dynamic_Record` 的全表掃描；(2) **選擇性反例**：日期區間涵蓋全部 300 萬筆、參照條件只命中幾十筆時，驅動必須是參照條件，計畫以 `(FieldId, RefKey)` 起頭，不得以 `(FieldId, DateValue)` 掃全部日期。這比憑 LINQ 或 SQL 猜可靠。

沒有任何縮小條件的報表（例如全部歷史依科目加總）要掃全部命中筆數，是這個設計裡最貴的形狀；成本大約是「筆數 × 每筆幾次主鍵查詢」，壓測要量（§12），量到超標再討論覆蓋索引或預先彙總（不在這次範圍）。

### 5.3 分組鍵

每個分組欄位產生一個**最終鍵運算式**，直接 `GROUP BY` 它，結果不需要事後合併：

| 型別 | 鍵運算式 |
|---|---|
| 選項、文字、使用者 | `COALESCE(TextValue 或 RefKey, '')` |
| 是否 | `CASE WHEN NumberValue IS NULL THEN '' WHEN NumberValue = 1 THEN 'true' ELSE 'false' END` |
| 數字 | 以 `ScaledValue` 分組（無值為 NULL）；C# 依欄位 `Scale` 轉回 decimal，再用與 `ReportCalculator.KeysOf` 相同的格式輸出鍵字串。不同的 `ScaledValue` 對應不同的字串，所以不會合併。前提是 §4.1 的精確性標記為 true，且 §8.3 的溢位探測通過（`ScaledValue` 溢位成 NULL 的值，如 1e20 與 2e20，會被併成同一個「無值」分組） |
| 日期、日期時間 | `DateBucket(DateValue, 'day')`，其餘分桶同理；無值為 `''` |
| 參照（動態模組） | `CASE WHEN 索引列不存在 THEN '' WHEN 目標記錄不存在或已刪除 THEN '__missing' WHEN 目標不在範圍內 THEN '__restricted' ELSE RefKey END`；`RefKey` 轉整數時用 `CAST`，動態模組的鍵一定是數字 |
| 沿參照帶出 | 來源參照列不存在 → `''`；目標記錄不存在 → `__missing`；不在目標範圍 → `__restricted`；否則取目標欄位的上述運算式 |

多選以外的型別只連 `Seq = 0` 的索引列；多選連全部選項列。兩個多選一起分組時自然是笛卡兒積，與 `GroupKeysOf` 相同。

### 5.4 條件的謂詞

**不直接重用 `DynamicQueryBuilder.Condition`**，因為語意不同：報表的日期條件是「只給日期就是一整天」的區間（`Span`），日期時間欄位的 `eq` 是整天，列表篩選是精確相等；報表的相對日期用報表基準日（`BasisDateAsync`，Demo 站可能凍結），不是今天。謂詞產生器為報表另寫，運算子逐一對應 `ReportRunner.Matches`：

- 空值：`isEmpty`、`isNotEmpty`；`ne`、`notIn` 包含空值，比較類（`gt`、`gte`、`lt`、`lte`、`between`、`eq`、`in`）不含空值，與現行一致。
- **數值條件一律用 `ScaledValue`（精確整數）比較，不用 double 的 `NumberValue`，也不把條件常數轉成 double。** 記憶體路徑用 decimal 比較，double 在 15 位有效數字以上會失準（`999999999999999` 的 `ne` `999999999999999.01`：double 兩者相等，decimal 不等）。條件常數 `c` 在 C# 先以 decimal 算出 `k = c × 10^Scale`（`Scale` 取欄位目前值，§4.1 的標記保證每個記錄值都是 `Scale` 位小數內的精確值），再依 `k` 是否為整數換成整數條件：
  - `k` 是整數：`eq`、`ne`、`gt`、`gte`、`lt`、`lte`、`between`、`in`、`notIn` 直接用 `ScaledValue` 與 `k` 比。
  - `k` 不是整數（常數的小數位超過 `Scale`，沒有任何記錄值會等於它）：`eq` 恆假；`ne` 恆真（含空值，與 `Matches` 對 `ne` 的語意一致）；`gt` ≡ `ScaledValue > ⌊k⌋`；`gte` ≡ `ScaledValue >= ⌈k⌉`；`lt` ≡ `ScaledValue < ⌈k⌉`；`lte` ≡ `ScaledValue <= ⌊k⌋`；`between [a, b]` ≡ `ScaledValue >= ⌈ka⌉ AND ScaledValue <= ⌊kb⌋`；`in` 去掉非整數成員（全去掉則恆假）；`notIn` 去掉非整數成員（全去掉則恆真）。
  - `k` 超出 `long` 範圍：退回，原因 `conditionConstantOutOfRange`。
  - 是否欄位的條件值先在 C# 與 `true`／`false` 比對（`Scalar` 的字串語意），再轉成對 `NumberValue` 的 1／0 比較或恆真恆假。
  - 這條規則同時適用於縮小後備（§8.1）：縮小只用這套精確比較。
- 日期：`eq`＝`>= 當天 00:00 且 < 隔天 00:00`、`gt`＝`>= 隔天 00:00`、`lte`＝`< 隔天 00:00`、`between`＝`>= 起日 00:00 且 < 迄日隔天 00:00`、`withinNext/LastDays` 與 `beforeToday` 用基準日；條件值帶時間時是那一刻（`Span` 的 `(at, at + 1 tick)`）。
- 沿參照帶出的謂詞：來源參照列不存在 → 套「空值格」的結果（`isEmpty` 與 `ne`、`notIn` 為真，其餘為假）；目標記錄不存在或不在範圍 → 假（與 `Matches` 對 `missing`、`restricted` 一律不符合相同，`ne` 也不例外）；目標存在 → 目標欄位的謂詞（目標欄位無值同樣當空值格）。
- 謂詞以「某筆記錄的 id 運算式」為參數（主記錄 `r.Id`、子記錄 `c.Id`），篩選、量值條件（`ratio`）、日期區間共用同一個產生器。

### 5.5 一個層級與兩個層級

**沒有子模組：** 單層聚合，一筆主記錄一列。

```sql
SELECT <鍵運算式…> AS k1, …,
       COUNT(*) AS row_count,
       COUNT(m1.ScaledValue) AS m1_n,
       SUM(m1.ScaledValue / 1000000000) AS m1_hi, SUM(m1.ScaledValue % 1000000000) AS m1_lo,
       MIN(m1.ScaledValue) AS m1_min, MAX(m1.ScaledValue) AS m1_max
FROM (SELECT i.RecordId FROM Dynamic_RecordIndex i
      WHERE i.FieldId = @drv AND i.DateValue >= @from AND i.DateValue < @until) d
CROSS JOIN Dynamic_Record r ON r.Id = d.RecordId
  AND r.ModuleId = @m AND r.DeleteFlag = 0 AND r.OwnerUserId IN (@o0, @o1, …)   -- 範圍 null 時省略這一行，空名單時為 1 = 0
LEFT JOIN Dynamic_RecordIndex g1 ON g1.RecordId = r.Id AND g1.FieldId = @g1 AND g1.Seq = 0
LEFT JOIN Dynamic_RecordIndex m1 ON m1.RecordId = r.Id AND m1.FieldId = @m1 AND m1.Seq = 0
WHERE <其餘條件的謂詞>
GROUP BY k1, …
```

另有一個不分組的**總計查詢**（只有量值），「總計一律獨立重算（去重）」的語意不變（`reports-design` §6.3）。有分組時總計不能由各組相加得到（多選與子欄位分組會重複），所以固定多跑一次總計；它與分組查詢掃的是同一批列，成本約加倍，量到問題再優化。

**有子模組（左合併）：** 主模組欄位的量值每筆主記錄在每個分組內只算一次。做法是兩層：

1. 內層：`GROUP BY 分組鍵…, r.Id`，每個（分組, 主記錄）一列，主欄位量值取 `MAX`（同一筆主記錄的值相同），子欄位量值先在這一層對該主記錄的子列加總、計數。
2. 外層：`GROUP BY 分組鍵…`，加總內層的結果；`countMain` 是外層的列數，平均用「加總與筆數」各自相加後在 C# 相除。

內層的列數是命中的（分組, 主記錄）數，不是來源筆數。子記錄用 `Dynamic_RecordIndex` 的連結欄位（參照欄位一定有索引，有 `(FieldId, RefKey)` 索引）連回主記錄，子記錄自己帶 `ModuleId`、`DeleteFlag`、範圍名單與子欄位條件。主記錄納入的規則照現行 `RunAsync`：條件落在子欄位（或主要日期在子欄位）時，通過條件的子記錄至少要有一筆；否則沒有子記錄的主記錄保留一列、子欄位為空。

總計查詢同理，只是內層只依 `r.Id` 分組。

### 5.6 結果組裝

SQL 路徑回傳「分組鍵陣列 → 量值」的原始列，C# 轉成與 `ReportCalculator.Calculate` 相同型別的 `ReportCalcResult`（分組、總計、`DedupedNote`、列數），之後走**同一份**標籤、排序、欄位、卡片選項的程式（`LabeledGroups`、`MeasureColumn` 等）。需要的小調整：

- `NeedsDedupedNote` 目前是 `ReportCalculator` 的私有函式，改成公開，兩條路徑共用。
- 參照分組的標題：只對結果裡出現的 `ok` 鍵查一次（`IRecordRepository.ListExistingAsync`），放進 `RunContext.Titles`。
- 使用者姓名：`ListUsersAsync` 照舊。
- 狀態：總計查詢的列數為 0 時 `empty`，否則 `ok`。

## 6. 權限如何在 SQL 內套用（不能比現在寬）

現行的權限判斷有兩層，兩層在 SQL 路徑都維持原樣：

**第一層：整張報表能不能跑（C#，SQL 產生前，完全不動）。** 步驟 2 的模組讀取（明細子模組走表頭授權 `AuthorizeDetailAsync`）、步驟 4 的欄位可見與可用（`UnusableMessage`、條件與量值用到不可讀目標的整張擋下、型別重驗）。任何一項不過，直接回 `restricted` 等狀態，不會進到規劃。

**第二層：能看到哪些記錄（原本是 `ListForReportAsync` 的 `OwnerUserIds` 與 `ViaCell` 的目標範圍，改成 SQL 內的範圍條件）。**

| 位置 | 現行做法 | SQL 做法 |
|---|---|---|
| 主記錄 | `ListForReportAsync` 帶 `ownerUserIds`（null 為全部） | 主別名帶 `ModuleId`、`DeleteFlag = 0`、`OwnerUserId IN (…)`；名單為空時 `1 = 0`（現行 `Contains` 對空集合也是零筆） |
| 子記錄 | 子模組權限（明細繼承表頭範圍）的 `OwnerUserIds` | 子別名同上，範圍用 `childSide.Perms.OwnerUserIds` |
| 沿參照帶出的目標 | `ViaCell`：目標模組不可讀 → restricted；記錄不存在 → missing；`OwnerUserId` 不在目標範圍 → restricted；都不帶目標的值 | 目標別名用 `LEFT JOIN`，不存在 → `__missing`，範圍外 → `__restricted`，兩者都不取目標欄位值；目標模組不可讀（`Perms == null`）的情形退回 |
| 參照欄位分組的目標 | `ResolveModuleAsync`：模組不可讀或標題不可見 → 全部 restricted | 標題可見且可讀才走 SQL（鍵如 §5.3）；否則退回 |

保證的方式：

1. **產生器缺範圍就丟例外。** `ReportQueryBuilder` 每新增一個 `Dynamic_Record` 別名，必須同時收到它的範圍，否則在組 SQL 時 `InvalidOperationException`（I2）。範圍名單為 null 是「全部」的明確值，不是漏傳。
2. **SQL 文字測試。** 對權限矩陣的每個身分產生 SQL，斷言每個 `Dynamic_Record` 別名都有 `ModuleId`、`DeleteFlag = 0`，範圍非 null 者有 `OwnerUserId IN`，值全在參數裡、不在字串裡。
3. **權限矩陣測試兩條路徑各跑一次**（現有 `ReportPermissionMatrixTests` 23 個測試，見 §7），包含隱藏欄位用在條件、分組、量值、排序、帶出各位置，以及金額權限、參照目標無權限、子模組無權限、明細繼承。
4. **軟刪除：** `DeleteFlag = 0` 在每個別名上。索引表在軟刪除時不清（`reports-design` §5），所以 SQL 一律從 `Dynamic_Record` 的過濾出發，不能只查索引。

## 7. 結果相同的保證方式

現行實作（記憶體路徑）是基準，不動它的結果。

1. **現有測試整套再跑一次。** 目前 `ReportRunnerTests`（69 個）、`ReportPermissionMatrixTests`（23 個）、`DemoReportsTests`、WebAPI 的 `ReportsApiTests`、`DashboardCardsApiTests` 等，欄位都用預設設定建立（`IsFilterable` 為假），所以**今天全部跑記憶體路徑**。做法：
   - `ReportOptions` 新增 `Engine`（`Auto`、`MemoryOnly`、`SqlOnly`，預設 `Auto`）。`SqlOnly` 遇到無法規劃的形狀直接讓測試失敗，用來證明覆蓋。
   - 測試基底類別加一個「欄位建立時一律設為可篩選」的開關；`ReportRunnerTests`、`ReportPermissionMatrixTests` 各加一個子類別，開關打開、引擎設 `SqlOnly`，**同樣的斷言**再跑一次。其中本來就走退回路徑的案例（文字 `contains` 等）在子類別標明改用 `Auto` 並斷言走了記憶體路徑。
   - 原本的類別（記憶體路徑）繼續跑，所以退回路徑的行為也有基準。
2. **隨機定義的對照測試。** 固定亂數種子產生數百到數千個報表定義（分組 0 至 2 個含分桶與多選、量值全種類、條件全運算子含空值與邊界、主要日期與區間、帶出、子模組、下鑽），在一份刻意含邊界值的資料集上（空值、已刪除的參照目標、範圍外的目標、同一主記錄多個子記錄且落在不同分組、日期時間跨午夜與跨日、負數與零、小數位邊界、停用的選項）用三個不同範圍的身分各跑兩條路徑，序列化成 JSON 後逐位元組比較。這是新舊對照測試。
3. **壓測資料上的對照。** `tools/LoadTest` 新增 `report-check` 指令：對 5 萬與 19 萬筆的資料庫跑壓測用的全部報表定義，兩條路徑結果必須相同（19 萬筆記憶體路徑還在上限內）。
4. **單元層：** `ReportQueryBuilder` 的 SQL 文字測試（§6）、`DateBucket` 與 `ReportDates.BucketKey` 逐日對照（含年底、ISO 第 53 週）、數字鍵格式與 `KeysOf` 對照。
5. **審查反例（固定測試，兩條路徑結果必須相同，且斷言退回原因或走了 SQL）：**

| 反例 | 預期 |
|---|---|
| 小數欄位 Scale 由 2 降為 1，既有值 1.23 | 同交易重建後該列 `ScaledValue` 為 NULL；`sum` 退回記憶體路徑，結果是 1.23 而不是 1.2；原因 `scaledUnavailable` |
| 數值 1e20 與 2e20 的記錄（`ScaledValue` 溢位成 NULL），依該欄位分組加 `countMain` | 退回記憶體路徑，兩個分組各 1 筆，不是併成一組 2 筆；原因 `scaledOverflow` |
| 數值 `999999999999999` 與 `999999999999999.01`（`Scale` 2），條件 `ne 999999999999999.01`、`eq` 與 `gt`／`lt` 的邊界 | SQL 路徑與記憶體路徑一致；常數小數位超過 `Scale` 時 `eq` 為假、`ne` 為真、比較運算換算成整數邊界 |
| 左合併下鑽：A 有子記錄 X、B 沒有子記錄，下鑽子模組分組鍵為空白的分組 | 只回 B 的空白列，A 不出現；縮小只用主模組條件，子側不縮小 |
| 文字欄位值 `a`×440 與 `a`×440 加 `b`，條件 `ne a`×440（也測 `eq`、`in`、`notIn`、分組） | 後者的 `TextTruncated` 為 1，範圍內有截斷列，退回記憶體路徑；結果分開計兩筆 |
| 文字欄位值第 440 個 UTF-16 單位是 😀 的高代理字元（索引值長度 439） | `TextTruncated` 為 1（索引值與原值不相等），退回記憶體路徑；長度剛好 440 單位且未被截斷的值 `TextTruncated` 為 0、仍走 SQL |
| 多欄分組鍵 `("a\u001fb", "c")` 與 `("a", "b\u001fc")` | 兩個分組分開，不併成一組（記憶體路徑與 SQL 路徑一致，§10） |
| 報表讀完欄位設定後，另一個連線把 Scale 調低並重建索引 | 結果與「改前」或「改後」其中一個一致版本相符，不混用（§8.4） |
| 主記錄只命中 10 筆、被引用的目標模組有 300 萬筆，目標欄位探測 | 計畫只查被引用的目標，不掃目標模組（§8.3） |
| 日期區間涵蓋全部資料、參照條件只命中幾十筆 | 驅動條件是參照條件（計畫測試，§5.2） |

## 8. 做不到 SQL 的形狀怎麼處理

### 8.1 縮小後的記憶體路徑

退回時：

1. **只用主模組上的精確條件縮小主記錄**（可 SQL 的篩選、主模組側的主要日期區間；沒有子模組時再加下鑽的分組鍵）。「精確」的意思是該謂詞通過 §4.1 的欄位資格與 §8.3 的資料狀態探測，並且運算子語意與 `Matches` 逐筆相同（§5.4）。任何一項沒過的條件不拿來縮小。
2. **子記錄不依自己的條件縮小**，只載入「連結欄位指向已載入主記錄」的子記錄（以縮小後的主記錄 id 為範圍）。原因是左合併：主記錄沒有（通過條件的）子記錄時要補一列空白列，縮小子側會改變「有沒有子記錄」的判斷，例如下鑽子分組為空白時，A 有子記錄 X、B 沒有子記錄，若把子記錄縮小成「分組鍵為空白」，X 被排除，A 反而被當成沒有子記錄而多回一列。子模組上的篩選與子模組分組鍵的下鑽，一律在載入後由記憶體路徑套用。
3. `ListForReportAsync` 帶縮小謂詞載入主記錄：`SELECT r.* … WHERE <範圍> AND <縮小謂詞> ORDER BY r.Id LIMIT @limit`；子記錄以連結欄位 `IN` 已載入主記錄 id（超過參數上限時分批）載入。
4. 其餘照舊：記憶體路徑**重套全部條件**。

縮小為什麼安全：所有條件都是 AND，縮小用的每個謂詞本身對該筆主記錄的判斷與 `Matches` 相同，所以只會排除「記憶體路徑也一定排除」的主記錄（現行 `Passes("main")` 失敗的主記錄不產生任何列）。子記錄只被排除在「連結的主記錄不會產生列」的情形下，這樣的子記錄本來就不會被用到。縮小謂詞用不到的條件（`contains`、探測沒過的欄位等）就不縮小，只會多載入，不會少載入。

### 8.2 上限與回報

- `MaxLoadRows`（20 萬）改成只對**縮小後**載入的筆數生效；`MaxRows`（5 萬列）照舊只管明細輸出與記憶體路徑的組列。
- 超過時回 `tooMany`，訊息改成能指出原因與出路：「條件套用後仍超過記憶體上限」，並在原因是欄位未索引時提示「將欄位設為可篩選後，這張報表可處理更大的資料量」。新增訊息鍵要補四個語系（`ReportText` 現有 zh-TW、en、th、vi）。
- `ReportResult` 新增兩個可省略的欄位：`engine`（`sql` 或 `memory`）與 `fallbackReasons`（代碼清單，如 `fieldNotIndexed:<欄位 id>`、`operatorNotPushable:contains`、`targetNotModule`）。舊的前端忽略即可（API 向後相容，我的判斷）；畫面行為不變，不加提示 UI。後端另以 `Information` 記錄每次退回與原因，壓測與營運都看得到。
- SQL 路徑的逾時：命令逾時採 `Reports:SqlTimeoutSeconds`（預設 30，我的判斷），並串接請求的 `CancellationToken`；逾時回 `tooMany` 狀態與「查詢耗時過長，請縮小條件」，不回部分結果。

### 8.3 欄位資格與資料狀態探測（I3）

走 SQL（以及用條件縮小後備）之前，對報表用到的每個欄位引用（分組、條件、量值、主要日期、帶出的目標欄位）依序過下列檢查，任何一項不過就整張退回記憶體路徑，並把原因寫進 `fallbackReasons`。不用容差，也不用長度門檻推論。

**欄位資格（靜態，只看欄位設定）：**

1. 欄位已索引（`RecordIndexer.ShouldIndex`），否則 `fieldNotIndexed:<欄位 id>`。
2. 一次性回填已完成（`IndexBackfillVersion` 達目前版本），否則 `indexBackfillPending`。

**資料狀態探測（讀索引，用 §4.1 的部分索引，成本跟壞列的數量有關、跟資料量無關）：**

| 探測 | 範圍內存在 | 退回原因 |
|---|---|---|
| 數值不可用 | 用在分組、條件、量值的數值欄位，有 `ScaledValue IS NULL AND NumberValue IS NOT NULL` 的列（溢位如 1e20、Scale 調低後的不精確舊值、尚未回填的列） | `scaledUnavailable:<欄位 id>` |
| 文字被截斷 | 用在分組或任何條件的文字類欄位，有 `TextTruncated = 1` 的列 | `textTruncated:<欄位 id>` |

「範圍內」的定義：主（與子）模組以模組 id、`DeleteFlag = 0` 與資料範圍名單界定。探測從部分索引出發，只走壞列，再回頭對 `Dynamic_Record` 檢查範圍，例如：

```sql
SELECT 1 FROM Dynamic_RecordIndex t
JOIN Dynamic_Record r ON r.Id = t.RecordId
WHERE t.FieldId = @f AND t.TextTruncated = 1
  AND r.ModuleId = @m AND r.DeleteFlag = 0 AND <範圍名單>
LIMIT 1
```

範圍刻意取「不含其他謂詞」的超集（欄位自己的謂詞排除掉的列，也可能是被誤判的列），寧可退回不漏判。

**帶出目標欄位只檢查被引用到的目標，不掃目標模組。** 目標側壞列同樣從部分索引出發，再用主記錄的參照索引列 `(FieldId, RefKey)` 反查「是否有範圍內的主記錄引用它」：

```sql
SELECT 1 FROM Dynamic_RecordIndex t
WHERE t.FieldId = @tf AND t.ScaledValue IS NULL AND t.NumberValue IS NOT NULL
  AND EXISTS (SELECT 1 FROM Dynamic_RecordIndex v
              JOIN Dynamic_Record r ON r.Id = v.RecordId
              WHERE v.FieldId = @via AND v.RefKey = CAST(t.RecordId AS TEXT)
                AND r.ModuleId = @m AND r.DeleteFlag = 0 AND <範圍名單>)
LIMIT 1
```

成本是目標側壞列數量乘一次索引查詢，主記錄只命中 10 筆、目標有 300 萬筆時也一樣便宜；計畫測試涵蓋這個形狀。

**縮小後備用同一套判斷**：沒通過的條件不拿來縮小（§8.1）。

**修源頭：** `UpdateFieldHandler` 目前只在「是否索引」改變時重建索引（`wasIndexed != isIndexed`）。改成 `Scale` 變動時也重建該欄位索引，與欄位更新在同一個交易內（`UpdateFieldHandler` 本來就在 `uow.CommitAsync` 才提交）；測試要包含「1.23 的欄位把 Scale 由 2 降為 1」：重建後該列 `ScaledValue` 為 NULL、報表退回記憶體路徑、結果是 1.23 的加總而不是 1.2 的加總。

### 8.4 讀取快照

報表要用的欄位設定與索引資料必須屬於同一個版本：否則報表讀完設定（Scale 2）後，另一個連線把 Scale 調成 1 並重建索引，SQL 就會拿舊設定去解讀新索引。做法：

1. 報表的 SQL 部分（探測與所有聚合查詢）在**同一個讀取交易**內執行。SQLite 的 WAL 模式下，延遲開始（`BEGIN`，不是 `BEGIN IMMEDIATE`，所以不取寫入鎖、不擋寫入）的交易在第一次讀取時固定快照，其後所有讀取都看同一份資料。
2. 交易內第一個語句重讀本次報表用到的所有欄位（含帶出的目標欄位）的設定，與 `RunAsync` 先前載入的設定逐項比對（型別、狀態、`Scale`、`ResultType`、`IsFilterable`、`IsSortable`、`MaxLength`）。任何一項不同，放棄 SQL 路徑，改走記憶體路徑（原因 `configChanged`）。記憶體路徑用的是 `RunAsync` 先前載入的同一份設定，結果對應「改前」的一個一致版本。
3. 因為欄位更新與索引重建在同一個交易提交（§4.1），快照內看到的設定與索引必定同版。

測試：報表讀完設定後，另一個連線改 `Scale` 並重建，報表的結果必須與「改前」或「改後」的其中一個版本逐位元組相同；用測試用的鉤子在讀設定與開快照之間插入這次變更。

## 9. 精度

| 項目 | 規則 |
|---|---|
| 加總 | `ScaledValue`（十進位值 × 10^Scale 的 `long`）加總。SQLite 的 `SUM` 整數溢位會丟錯誤，而 15 位有效數字的值加上 300 萬列可以超過 `long`，所以每列拆成 `v / 10^9` 與 `v % 10^9` 兩個加總（除法與餘數都向零取整、同號），C# 用 `Int128` 合併再除以 10^Scale 轉 decimal。結果與逐筆 decimal 加總完全相同 |
| 平均 | C# decimal：加總除以筆數（28 位），不在計算中捨入，顯示才捨入（`report-format.ts`，不動） |
| 最小、最大 | 數字取 `ScaledValue` 的極值；日期取 `DateValue` 的極值轉 `DayNumber` |
| 比率 | 命中筆數與範圍筆數都是整數，C# decimal 除法 |
| 捨入 | 記錄值已是捨入到 `Scale` 的 decimal（寫入驗證、公式捨入，`AwayFromZero`），SQL 路徑不新增任何捨入 |
| 15 位有效數字 | 寫入驗證不變。報表條件的比較改用 `ScaledValue` 精確整數，常數在 C# 以 decimal 精確換算（§5.4），不再有「條件值超過 15 位時 double 失準」的取捨；儲存報告 §6 規則 2 對報表 SQL 路徑不再適用，記憶體路徑本來就是 decimal 比較 |
| 精確性前提 | `ScaledValue` 非 NULL 即精確（§4.1 的不變式）；範圍內有 `ScaledValue` 不可用的列就退回（§8.3），不靠容差也不靠旗標 |
| 前端 | 合計超過 15 位有效數字時前端 double 顯示失去小數，與現況相同 |

## 10. 行為變更

1. **走 SQL 的聚合報表不再受 `MaxRows`（5 萬列）與 `MaxLoadRows`（20 萬）限制。** 這是這次的目的；「不得因來源筆數被拒」是功能性要求。明細報表與記憶體路徑仍受 `MaxRows` 限制。
2. 記憶體路徑的 `MaxLoadRows` 改成以縮小後的筆數計；因此有條件的報表在記憶體路徑也比現在容易通過。
3. 回傳新增 `engine`、`fallbackReasons`（可省略）；新增一個 `tooMany` 的訊息鍵。
4. `UpdateFieldHandler`：欄位 `Scale` 變動時在同一個交易內重建該欄位索引（§4.1、§8.3）。
5. `Dynamic_RecordIndex` 新增 `TextTruncated` 欄與兩個部分索引（EF migration）；`ScaledValue` 的換算改為精確（非整數或超出範圍為 NULL）；新增一次性、以版本列守門的回填工作補算舊資料（§4.1）。回填完成前報表走記憶體路徑，結果仍正確。
6. 內建應用程式套件與 Demo 範本中，報表（含 Demo 示範報表）會用到的欄位勾成可篩選，讓它們走 SQL。
7. **順帶修正（記憶體路徑的既有 bug）：多欄分組鍵撞名。** `ReportCalculator.Calculate` 用 `string.Join('\u001f', keys)` 串接多欄分組鍵當字典鍵，值本身含該分隔字元時 `("a\u001fb", "c")` 與 `("a", "b\u001fc")` 會併成同一組。改成結構化的多欄鍵（比較整個鍵序列的相等，不串接字串），兩條路徑一致；這是行為修正，會讓含分隔字元的罕見資料多出原本被誤併的分組。

## 11. 風險與已知限制

- **一次性回填需要時間。** 舊資料升級後，回填完成前報表走記憶體路徑；回填是分頁掃描、每個模組一個進度點、可中斷續跑，300 萬筆的模組預計數十秒到數分鐘，只在版本列未達目前版本時執行。Scale 調低且既有值小數位較多的欄位，其壞列的 `ScaledValue` 永遠是 NULL，該欄位的報表只能走記憶體路徑，直到使用者整理資料（這次不提供整理工具）。
- **探測成本跟壞列數有關。** 部分索引讓探測只走壞列（通常是零列），但壞列很多的欄位（例如大量 1e20 級的值）每次探測都找得到，等於永遠退回；這是正確的行為，不是效能問題。
- **合併報表的驅動只看主模組條件**（§5.2）。條件只落在子模組且命中極少時偏慢，壓測新增這個情境。
- **索引覆蓋是前提。** 沒勾「可篩選」的欄位值不在索引裡；Demo 範本與壓測以外的多數現有模組，金額、數量類欄位常常沒勾。這些報表不會變快，只會被縮小。治本是在應用程式範本裡把報表會用的欄位勾成可篩選（階段 3 的會計應用程式本來就要做），不是在這裡自動改別人的欄位設定（`CheckIndexer` 會自動把欄位設成可篩選，但那是檢查規則由模組設計者建立；報表是任何有讀取權限的使用者都能組，不能反過來改模組設定）。
- **沒有縮小條件的全歷史報表最貴**（§5.2）。20 人同時跑、每條都掃數百萬列，會吃滿 CPU 並拖慢其他操作（儲存報告 §4.2 的 19 萬筆案例）。是否加並行閘門，**先量再決定**（§12 第 6 步），不預先加。
- **寫入與讀取共用一個資料庫檔案：** SQL 聚合是長讀取，WAL 模式下不擋寫入，但會讓 checkpoint 無法回收 WAL 到讀取結束，長時間大查詢期間 WAL 會增長；壓測量得到再記。
- **開啟欄位的「可篩選」在大模組上的回填是整模組載入記憶體**（`RecordIndexBackfill.RebuildAsync` 用 `ListAllByModuleAsync`）。報表要求欄位有索引後，這個動作會變得更常見；這次不改，列為後續（300 萬筆的模組開啟時會很重）。
- **`DateBucket` 與部分索引是方言耦合點**，已有逐日對照與探測測試，但新增資料庫引擎時要補實作（部分索引在不支援的引擎要改成其他探測方式）與同一組對照測試。
- **驅動條件的估計是封頂計數**（§5.2）。候選全部超過上限時只能二選一，可能挑到次佳的驅動；以計畫測試與壓測的計畫輸出把關，不加 `ANALYZE`。

## 12. 實作與壓測順序

1. 對照測試骨架先行：`ReportOptions.Engine`、測試基底的「欄位可篩選」開關、`SqlOnly` 子類別（此時全部紅，因為 SQL 路徑還沒有）。
2. 單層聚合（無子模組）：計畫物件、`ReportQueryBuilder`、`DateBucket`、謂詞產生器、結果組裝；逐項把 `SqlOnly` 子類別的測試轉綠。
3. 沿參照帶出與參照分組；然後子模組兩層聚合。
4. migration（`TextTruncated`、部分索引）、精確換算、一次性回填、`Scale` 變動同交易重建；欄位資格與資料狀態探測（§8.3）、讀取快照（§8.4）；縮小後的記憶體路徑與上限改寫；`engine`、`fallbackReasons`、訊息；多欄分組鍵修正。
5. 隨機對照測試、SQL 文字測試、計畫測試（含選擇性反例）、§7 第 5 項的審查反例；內建套件與 Demo 的報表欄位勾成可篩選。
6. 重新壓測（約 300 萬筆，方法照儲存報告 §2、§9：負載低於 4 才開始、一次只改一個變數，大型資料庫放結果目錄的新子目錄，不進 repo）：
   - 工作負載新增報表：全年依月加總、十年依科目加總（無縮小條件）、科目加一季依月、狀態篩選依客戶、含子模組的傳票明細（依科目分組）、**日期全範圍加參照條件命中極少**（驗證驅動條件的挑選）、條件只落在子模組的合併報表（量合併報表驅動的限制）。壓測同時記錄探測所占的時間。舊的 `report_month`、`report_quarter`、`report_customers` 保留，數字可與舊結果對照。
   - 單人逐項 20 次，再 20 人有思考時間；門檻 p95 ≤ 5 s、p99 ≤ 10 s，且不得因筆數被拒。
   - 若 20 人有思考時間不達標，才試並行閘門，且與其他變數分開量。
   - 結果新增一節寫進儲存報告（舊數字保留對照），並依該報告 §7 的三個條件寫「要不要加 PostgreSQL」的建議（只寫建議，不加）。

## 13. 不做

- 不加 PostgreSQL、不開 `mmap`、不改報表的定義格式（`ReportDefinition` 一個欄位都不動）。
- 不做執行期 DDL、不新增索引、不改 schema、不做預先彙總表或結果快取。
- 不做階段 3 其他項目（期間鎖定、分組小計、損益表列結構、Top N 下推、會計應用程式）。Top N 目前是在分組結果上算，聚合後分組數通常不大，不下推。
- 不新增畫面；報表與儀表板的畫面行為不變。

## 14. 已裁決與待裁決

第一輪審查的裁決（主控）：

- **Q1　未勾可篩選的欄位**：退回縮小後的記憶體路徑並標明原因，不自動改欄位設定；內建套件與 Demo 的報表欄位勾成可篩選（§10 第 6 項）。
- **Q2　SQL 聚合不受 `MaxRows` 限制**：採納。
- **Q3　文字 `contains`／`startsWith` 第一版退回記憶體**：採納。
- **Q4　`Scale` 變動時重建索引**：採納，並依審查改為搭配精確性標記（§4.1、§8.3）。
- **Q5　`ReportResult` 新增 `engine`、`fallbackReasons`**：採納。

第二輪審查的主控裁決：取消 `IndexScaleExact` 標記，改為同交易重建、一次性回填、讀取快照（§4.1、§8.4）；多欄分組鍵撞名修記憶體路徑（§10 第 7 項）；文字截斷改用索引表的 `TextTruncated` 欄（§4.1、§8.3），取代第一輪的位元組門檻；帶出目標的探測只檢查被引用的目標（§8.3）。

兩輪審查的逐項處理對照見結果目錄的 `design-round1-done.md` 與 `design-round2-done.md`。

待裁決：無。
