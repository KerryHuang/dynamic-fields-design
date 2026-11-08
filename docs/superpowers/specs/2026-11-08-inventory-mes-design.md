# 階段 2 設計：內建「進銷存」與「MES 模具廠」、四個範本併入 MES、Demo 資料

> 2026-11-08｜路線階段 2 的應用程式部分｜前置：[路線](2026-11-06-erp-roadmap-design.md)、[事件、檢查、分錄](2026-11-08-events-ledger-design.md)、[CRM 應用程式](2026-11-07-crm-app-design.md)、[應用程式套件](2026-11-06-app-package-design.md)、[單據能力](2026-11-06-document-flow-design.md)、[Demo 資料](2026-11-05-demo-data-design.md)
> 標「（我的判斷）」的是我代為決定、可以改的，彙整在 §14。這份文件只寫決定與理由，細節以程式與測試為準。

## 1. 範圍與原則

兩個內建應用程式，隨 WebAPI 發行，由 `AppCatalog` 啟動時載入驗證，沒開啟之前不產生任何模組、選單或資料（同 CRM）：

| 應用程式 | 檔案 | id | 模組鍵前綴 |
|---|---|---|---|
| 進銷存 | `poc/backend/apps/inventory.app.json` | `inventory` | `inv-` |
| MES 模具廠 | `poc/backend/apps/mes-mold.app.json` | `mes-mold` | `mes-` |

另外：現有四個內建範本（訂單、故障報修、模具保養含客戶擴充、品質異常）併入 MES，不再出現在「從範本建立」；Demo 資料併入並擴充；補工令單與出貨單列印範本。

**核心程式不新增任何業務邏輯。** 兩個應用程式全部是 `apps/*.app.json`（格式 4）加測試。核心唯一的改動是 §9 的兩個小項（缺口 A：`TemplateCatalog` 容忍缺目錄；缺口 D：一個通用的日期篩選運算子），兩者都與業務無關，且 `NoBusinessWordsTests` 照跑。

**這份設計沒有發現必須先補的通用能力。** 階段 2 前置的事件、跨記錄驗證、不可修改分錄，加上階段 1 的單據轉換、狀態機、帶值、列印，足以表達兩個應用程式。§9 列出四個「有缺口但不阻擋」的項目：兩項這次做（A、D），兩項只寫清楚缺什麼、留給使用者決定（B、C）。

## 2. 跨應用程式的決定

### 2.1 每個應用程式都能獨立開關

- **MES 不依賴 CRM，也不依賴進銷存。** MES 自帶報價單與訂單；它的 `requires` 只有既有實體（`design.mold`、`sales.customer`，沿用四個範本現在的做法），沒有 `requires.apps`。
- **進銷存不依賴 MES，也不依賴 CRM。** 自帶品項、倉庫、供應商、客戶；`requires` 為空。
- 三個應用程式（CRM、MES、進銷存）兩兩之間沒有任何參照欄位、事件、檢查或轉換。套件設計 §7.4 禁止跨應用程式引用欄位，事件與檢查也只能指向同一份套件的模組（事件設計 §1），所以「不依賴」是格式本身保證的，不靠約定。
- 開啟順序任意，三個可以只開任何一個、任兩個、全部。關閉任何一個，其他的與自己的既有資料仍可查閱（套件設計 §6.3，唯讀）；沒有誰擋住誰關閉，因為沒有 `requires.apps`。
- 事件與檢查的 `code`（全站唯一的 `Key`）、報表與列印範本代碼一律以 `inv-`／`mes-` 當前綴，三個應用程式並存不撞名。模組 `Code`（應用程式內唯一）可以重複（CRM、MES 都有 `order`），因為資料庫識別用的是 `Key`，Demo 產生器沿用 CRM 的做法另存一份代號對照。

### 2.2 出貨：兩邊各有一個，彼此不連動（我的判斷）

- MES 的出貨單：依**工令**交貨給客戶，記數量、日期、狀態；不動庫存。模具廠的工令成品多半直接交貨，不入成品倉。
- 進銷存的出貨單：依品項與倉庫出庫，經事件產生庫存異動，餘額不可為負。
- 兩邊的模組名稱都叫「出貨單」，靠選單的應用程式分組區分；程式裡一個是 `mes-shipment`、一個是 `inv-shipment`。
- 錯了的代價：如果使用者要的是「MES 出貨自動扣進銷存庫存」，需要先有「可選相依」這個通用能力（§9 缺口 B），再把兩邊接起來。目前兩邊各自正確，同時開啟也互不干擾。驗收裡「完工量與庫存都對」，我解讀為：MES 的完工量與出貨量對，進銷存的庫存餘額對，兩者同庫並存時各自仍對。

### 2.3 品項不共用（我的判斷）

進銷存的「品項」是自己的模組（`inv-item`）。MES 的訂單明細、工令品項沿用現有做法，用文字欄位記料號與品名（四個範本的訂單明細本來就是這樣）。理由：共用要嘛讓 MES 依賴進銷存（違反獨立），要嘛靠欄位層級跨應用程式引用（套件設計 §7.4 不允許）。錯了的代價：同一個料號在兩邊各打一次，使用者之後想串接時要靠 §9 缺口 B。

### 2.4 客戶主檔不統一（需要使用者決定，見 §15）

三處都有「客戶」：CRM 的 `crm-customer`（動態模組）、MES 的客戶（既有實體 `sales.customer`，在 POC 裡是模擬的外部客戶主檔）、進銷存的 `inv-customer`（動態模組，只放出貨需要的基本資料）。各自獨立是目前最小的做法，也讓三個應用程式能單獨使用。但真實客戶會問「客戶資料以誰為準」，這是產品方向，不由我決定。

### 2.5 群組

套件各自宣告群組代號，開啟時由管理員對應。Demo 開啟時的對應見 §11.2。

- MES：沿用四個範本現有的代號 `sales`、`sales-manager`、`floor`、`maint`、`qa`，新增 `planner`（生管）。
- 進銷存：`buyer`（採購）、`keeper`（倉管）、`manager`（進銷存主管）。

## 3. 進銷存

### 3.1 模組與欄位

鍵 `inv-` 開頭，欄位標籤四語系（th、vi 為機器翻譯，標明待校對）。除註明外，下拉選項代號與標籤在 JSON 內定義。

**基本資料**（section「進銷存基本資料」）

- `inv-item` 品項：`no` 自動編號 `ITM-{0000}`、`name` 文字必填（標題）、`category` 下拉（raw 原料／part 零件／finished 成品／consumable 耗材）、`unit` 下拉（pcs／set／kg／box）、`price` 金額（參考單價，需金額權限）、`safetyStock` 小數（安全庫存）、`active` 布林。
- `inv-warehouse` 倉庫：`code` 文字必填、`name` 文字必填（標題）、`location` 文字、`active` 布林。
- `inv-supplier` 供應商：`name`（標題）、`contact`、`phone`、`email`、`terms`（付款條件）。
- `inv-customer` 客戶：`name`（標題）、`contact`、`phone`、`email`、`address`。

**採購**（section「採購」）

- `inv-po` 採購單：`no` `PO-{yyyyMM}-{0000}`、`supplier` 參照必填、`warehouse` 參照（預定入庫倉）、`orderDate` 預設今天、`expectedDate`、`status` 下拉（draft／confirmed／closed／cancelled）、`subtotal` 彙總（明細金額加總）、`taxRate` 百分比預設 5、`tax` 公式、`total` 公式、`remark`。規則：預定交期不得早於採購日期；取消時備註必填。
- `inv-po-line` 採購明細（明細）：`po` 明細參照、`seq`、`item` 參照必填（帶值 `default`：品名、單位、單價）、`itemName`、`unit`、`qty` 小數必填、`price` 金額、`amount` 公式。
- `inv-receipt` 進貨單：`no` `RC-{yyyyMM}-{0000}`、`fromPo` 參照採購單（唯讀，轉換追溯）、`supplier` 參照必填、`warehouse` 參照必填、`receiptDate` 預設今天、`status` 下拉（draft／posted／cancelled）、`remark`。
- `inv-receipt-line` 進貨明細：`receipt`、`seq`、`item` 參照必填（帶值：品名、單位）、`itemName`、`unit`、`qty` 小數必填且須大於 0（驗證規則）、`price` 金額、`amount` 公式、`sourceLine` 整數唯讀（轉換追溯）。

**庫存**（section「庫存」）

- `inv-shipment` 出貨單：`no` `SH-{yyyyMM}-{0000}`、`customer` 參照必填、`warehouse` 參照必填（出庫倉）、`shipDate` 預設今天、`status` 下拉（draft／shipped／cancelled）、`remark`。
- `inv-shipment-line` 出貨明細：`shipment`、`seq`、`item` 參照必填（帶值）、`itemName`、`unit`、`qty` 小數必填且大於 0、`price` 金額、`amount` 公式。
- `inv-adjust` 盤點調整單：`no` `AJ-{yyyyMM}-{0000}`、`warehouse` 參照必填、`adjustDate` 預設今天、`reason` 下拉（count 盤點／damage 損耗／opening 期初／other）、`status` 下拉（draft／posted／cancelled）、`remark`。
- `inv-adjust-line` 調整明細：`adjust`、`seq`、`item` 參照必填、`qty` 小數必填（盤盈為正、盤虧為負，不可為 0）、`note`。
- `inv-move` 庫存異動（**分錄**）：`item` 參照必填、`warehouse` 參照必填、`qty` 小數必填（入庫正、出庫負）、`kind` 下拉（in／out／adjust）、`moveDate` 日期、`docNo` 文字（來源單號）、`reverses` 參照庫存異動自己（唯讀）、`note`。`item`、`warehouse`、`qty` 設為可篩選（檢查引用，套件開啟時就設好，不靠儲存檢查時自動打開）。

### 3.2 狀態機

- 採購單：draft → confirmed（採購、主管）、confirmed → closed（採購、主管；手動結案）、draft／confirmed → cancelled（主管）。closed、cancelled 唯讀。
- 進貨單、出貨單、盤點調整單：draft → posted／shipped（倉管、主管）、posted／shipped → cancelled（**主管**）、draft → cancelled（倉管、主管）。posted／shipped、cancelled 唯讀。

草稿單據可以編輯與刪除，過帳後全單唯讀（事實鎖加唯讀狀態雙重保證）。要更正就取消，事件沖銷庫存，再重開一張。

### 3.3 單據轉換

- `inv-po-to-receipt`：採購單（confirmed）轉進貨單，含明細的部分轉換，數量用 `qty`、追溯欄位 `fromPo`／`sourceLine`，複製供應商、倉庫、品項、單價。剩餘量、併發互斥沿用單據能力 §4。**已知限制（沿用單據能力 §4.3）：** 取消目標單據不釋放已轉量；已過帳的進貨單取消後，採購單剩餘量不會回來，要補收請直接開進貨單（不經轉換）。

出貨與盤點不經轉換（沒有上游單據）。

### 3.4 事件（`code` 即全站唯一鍵）

| 事件 | 來源與觸發 | 效果 |
|---|---|---|
| `inv-receipt-post` | 進貨單 `transition` → posted | `post`：`create` 庫存異動，`each` 進貨明細：品項 ← 明細品項、倉庫 ← 表頭倉庫、數量 ← 明細數量、`kind` = in、`moveDate` = today、`docNo` ← 表頭單號 |
| `inv-receipt-cancel` | 進貨單 `transition` posted → cancelled | `reverse`：沖銷 `inv-receipt-post` 的 `post` |
| `inv-shipment-post` | 出貨單 → shipped | 同上，數量用公式 `-{明細數量}`，`kind` = out |
| `inv-shipment-cancel` | 出貨單 shipped → cancelled | `reverse` |
| `inv-adjust-post` | 盤點調整單 → posted | 數量 ← 明細數量（已含正負），`kind` = adjust |
| `inv-adjust-cancel` | 盤點調整單 posted → cancelled | `reverse` |

事件循環圖：來源模組是三種單據，效果寫入的庫存異動沒有事件，無環。庫存異動的明細欄位（品項、數量）沒有被任何群組隱藏，`allowHiddenSource` 不需要。倉管沒有單價權限，但事件不複製單價，所以不洩漏。

### 3.5 檢查與分錄

- 檢查 `inv-no-negative-stock`：模組庫存異動，`groupBy` [品項, 倉庫]，`sum(qty) ≥ 0`，訊息「庫存不足，不能出庫」。出貨過帳、盤虧、取消進貨都經過它，所以三種情境都不會透支。
- 分錄 `inv-move`：沖銷欄位 `reverses`，`negate` [`qty`]，無 `swap`。庫存異動不能修改、刪除，只能沖銷；沖銷後同品項同倉庫的加總同樣受檢查約束（沖銷一筆已被出貨消耗的進貨，會因餘額為負被擋，這是對的）。
- 異動不開放使用者直接新增給倉管：只有主管群組有 Add 權限（用來手動沖銷）；平常異動只由事件產生。期初庫存用盤點調整單（reason = opening）入帳。

### 3.6 權限

- 品項、倉庫、供應商、客戶：採購、主管可新增修改；倉管唯讀。品項單價與單據金額欄位需要金額權限，倉管沒有（看不到金額，也不影響過帳）。
- 採購單與進貨單：採購、主管為主；倉管可讀進貨單並過帳。
- 出貨單、盤點調整單：倉管、主管可新增修改與過帳，採購唯讀。
- 庫存異動：全部群組唯讀，主管有 Add（沖銷用）。
- 資料範圍全部 `all`；沒有依人員隔離的需求。

### 3.7 報表（隨套件）

報表引用規則同 CRM：彙總欄位不能直接進報表，所以對明細或異動求聚合。

- `inv-stock-balance` 庫存餘額（品項 × 倉庫）：主模組異動，分組 [品項, 倉庫]，度量 `sum(qty)`，表格。
- `inv-flow-monthly` 月別進出量：分組 [`moveDate` 月, `kind`]，度量 `sum(qty)`，長條圖（出庫為負值）。
- `inv-po-status` 採購單狀態分布：圓環圖。
- `inv-purchase-by-supplier` 各供應商採購金額（未稅）：主採購單、子採購明細，分組供應商，度量 `sum(amount)`，長條圖。
- `inv-ship-by-customer` 各客戶出貨金額（未稅）：同上，主出貨單。

### 3.8 列印範本

- `inv-ship-sheet` 出貨單（掛在 `inv-shipment`）：客戶、出庫倉、日期、單號、`{{#each inv-shipment-line}}` 明細表（項次、品名、單位、數量、單價、金額）、合計、備註。沿用 CRM 報價單的寫法，值一律跳脫。

## 4. MES 模具廠

### 4.1 流程

```
報價單 ──轉換──▶ 訂單 ──轉換──▶ 工令（品項＋製程）──▶ 報工 ──▶ 出貨單
 (mes-quote)    (mes-order)     (mes-workorder)      (mes-report)  (mes-shipment)
```

另有故障報修、模具保養（含模具與客戶擴充）、品質異常，原樣併入。

### 4.2 併入的八個模組（四個範本）

鍵加 `mes-` 前綴，其餘維持原樣（欄位、選項、規則、檢視、權限、順序），做為遷移的**逐項等價**基準（§8 有等價測試）：

- 訂單 `mes-order`、訂單明細 `mes-order-line`（section「接單」）
- 故障報修 `mes-repair`（section「設備」）
- 模具擴充 `mes-mold-info`、客戶擴充 `mes-customer-info`、模具保養 `mes-mold-care`、更換零件 `mes-mold-care-part`（section「設備」，客戶擴充放「接單」）
- 品質異常 `mes-quality`（section「品質」）

對這八個模組做的**改動**只有下面幾項，其餘不碰：

1. 訂單：新增 `fromQuote` 參照（唯讀，轉換追溯）；新增狀態機（見 §4.4）並拿掉原本「已結案、已取消時整單唯讀」的規則，改由狀態機的唯讀狀態達成（效果相同，一處表達）；新增「品質異常筆數」彙總欄位（Demo 原本在建立後用指令補加，現在是套件的一部分）。
2. 訂單明細：新增 `sourceLine` 整數唯讀（報價轉訂單的追溯）。
3. 品質異常：新增 `workOrder` 參照（選填），讓異常能掛到工令。
4. 原本 Demo 在範本套用後用指令補的權限（品保可讀訂單；品質異常的 `responsibleUnit` 對業務唯讀、`defectRate` 對現場隱藏、`critical` 選項只開給品保），搬進套件自己的 `permissions`／`fieldAccess`／`optionScopes`。
5. 四語系：範本只有繁中與英文，th、vi 為機器翻譯補齊。

### 4.3 新增的模組與欄位

**接單**

- `mes-quote` 報價單：`no` `QT-{yyyyMM}-{0000}`、`customer` 實體參照（`sales.customer`）必填、`quoteDate` 預設今天、`validUntil`、`status`（draft／pending／approved／rejected）、`owner` 使用者、`subtotal` 彙總、`taxRate`、`tax` 公式、`total` 公式、`remark`。
- `mes-quote-line` 報價明細：`quote`、`seq`、`itemNo`、`itemName` 必填、`qty` 小數必填、`unit` 下拉、`price` 金額、`amount` 公式。

**生產**

- `mes-workorder` 工令：
  - `no` `WO-{yyyyMM}-{0000}`、`fromOrder` 參照訂單（唯讀，轉換追溯）、`customer` 實體參照（轉換時由訂單複製）、`plannedStart` 日期、`dueDate` 日期、`priority` 下拉（normal／urgent）、`status` 下拉（見 §4.4）、`holdReason` 下拉（shortage 缺料／equipment 設備／change 客戶變更／other），狀態為 hold 時顯示且必填。
  - 彙總與衍生：`totalQty` 彙總（工令品項數量加總）、`finalQty` 彙總（報工數量加總，條件「末道工序」為真）、`finalDefect` 彙總（報工不良數加總，同條件）、`completedQty` 公式（`finalQty − finalDefect`，**已完工量**）、`defectQty` 彙總（報工不良數加總，全部工序）、`shippedQty` 彙總（出貨單數量加總，條件狀態為 shipped）、`remainingQty` 公式（`totalQty − completedQty`）。
  - `lastReportDate`、`lastShipDate` 日期（唯讀，事件寫入）、`remark`。
  - 驗證規則：交期不得早於預計開工；狀態轉為「完工」時已完工量須 ≥ 工令數量（`fieldRef` 比較），短交請走「短交結案」。
- `mes-wo-item` 工令品項（明細）：`workorder`、`seq`、`itemNo`、`itemName` 必填、`qty` 小數必填、`unit`、`sourceLine` 整數唯讀（訂單轉工令追溯）。
- `mes-wo-op` 製程（工令的第二個明細模組）：`workorder`、`seq`（工序序號，明細內自動編號）、`process` 下拉（material 備料／rough 粗加工／cnc 精加工／edm 放電／wire 線割／heat 熱處理／polish 拋光／assemble 組裝／trial 試模）、`workstation` 文字、`plannedHours` 小數、`plannedStart`、`plannedEnd`。
- `mes-report` 報工：
  - `no` `RP-{yyyyMM}-{0000}`、`workOrder` 參照工令必填、`opSeq` 整數必填（對應製程的序號）、`process` 下拉必填、`reportDate` 預設今天、`worker` 使用者。
  - `qty` 整數必填（本次報工數，含不良）、`defectQty` 整數預設 0、`goodQty` 公式（`qty − defectQty`）、`defectReason` 下拉（dimension 尺寸／scratch 刮傷／burr 毛邊／crack 裂紋／material 材料／other），不良數大於 0 時顯示且必填、`final` 布林（末道工序；為真時這筆的良品數計入工令完工量）、`hours` 小數（工時）、`remark`。
  - 驗證規則：不良數不得大於報工數。

**出貨**

- `mes-shipment` 出貨單：`no` `SP-{yyyyMM}-{0000}`、`workOrder` 參照工令必填、`order` 參照訂單（唯讀，帶值 `lock` 自工令的 `fromOrder`）、`customer` 實體參照（唯讀，帶值自工令）、`shipDate` 預設今天、`qty` 整數必填且大於 0、`carrier` 文字、`status`（draft／shipped／cancelled）、`remark`。一張出貨單對應一張工令（分批出貨就開多張）。

### 4.4 狀態機

- 報價單：draft → pending → approved／rejected（核准與退回只有 `sales-manager`）、pending → draft（主管）、rejected → draft。approved 唯讀。
- 訂單：draft → confirmed（業務、業務主管）、confirmed → shipping（業務、業務主管、生管）、shipping → closed（業務主管）、draft／confirmed → cancel（業務主管）。closed、cancel 唯讀。
- 工令：draft → released（生管）、released → running（生管）、running → done／short（生管）、draft／released／hold → cancelled（生管）、released／running → hold（生管）、hold → released（生管）。done、short（短交結案）、cancelled 唯讀。
  - `released → running` 的群組只給生管，現場人員不能手動推進；實際推進由報工事件做（事件不檢查群組，但轉移必須已定義，§4.5）。
  - `hold → running` **刻意不定義**，所以暫停中的工令收到報工會整筆被擋；要先由生管恢復到 released。
- 出貨單：draft → shipped（生管、業務）、shipped → cancelled（生管；退貨）、draft → cancelled。shipped、cancelled 唯讀。

### 4.5 單據轉換

- `mes-quote-to-order`：報價單（approved）轉訂單，含明細的部分轉換（報價明細 → 訂單明細，`qty`，複製品號、品名、單位、單價；表頭複製客戶、業務），追溯 `fromQuote`／`sourceLine`。
- `mes-order-to-workorder`：訂單（confirmed、shipping）轉工令，含明細的部分轉換（**訂單明細 → 工令品項**，`qty`，複製品號、品名、單位），表頭複製客戶與交期，追溯 `fromOrder`／`sourceLine`。同一張訂單可分次、分多張工令轉，剩餘量與併發互斥沿用單據能力 §4；這是「工令品項」存在的理由，也是它與一般明細不同之處。製程不是轉換的產物，由生管在工令上自行填。

### 4.6 事件

| 事件 | 來源與觸發 | 效果 |
|---|---|---|
| `mes-report-progress` | 報工 `create` | `touch`：`update` 經 `workOrder`，工令 `status` ← running、`lastReportDate` ← today |
| `mes-shipment-ship` | 出貨單 `transition` draft → shipped | `touchWo`：`update` 經 `workOrder`，`lastShipDate` ← today；`touchOrder`：`update` 經 `order`，訂單 `status` ← shipping |

說明與取捨（我的判斷）：

- **已完工量用彙總，不用事件回寫。** 任務原文寫「報工回寫工令已完工量（連動事件）」，但事件設計 §1 已決定事件不做累加（重送與修改時不冪等），可由彙總算出的值一律用彙總。報工新增、修改、刪除後工令的已完工量即時更新，行為上等於回寫，且天然冪等。事件做彙總做不到的事：把工令推進到生產中、記錄最後報工日、出貨後把訂單推進到出貨中。
- 報工事件的轉移檢查：工令必須在 released 或 running 才收得下報工。draft、hold、done、short、cancelled 的工令收到報工，`update` 效果的轉移未定義或目標唯讀，整筆回滾，訊息是「連動事件「報工推進工令」失敗」加原因。
- 出貨事件同理：訂單已 closed 或 cancel 時，出貨會被擋。工令沒有來源訂單（直接開的工令）時 `via` 為空，略過訂單那個效果，不算錯誤。
- 兩個事件的來源都沒有 `create`／`reverse` 效果，不產生事件事實，所以報工與出貨單過帳前後都不被事實鎖凍結；出貨單靠狀態機唯讀，報工可事後更正（工令彙總跟著重算）。
- 事件圖：報工 → 工令（`on = update`／`transition` 的事件，來源是工令）：沒有；出貨單 → 工令、訂單：沒有以工令或訂單為來源的事件，無環。

### 4.7 檢查

- `mes-report-within-wo-qty`：模組報工，`groupBy` [工令, 工序序號]，`sum(qty) ≤` 經 `workOrder` 取工令的 `totalQty`。訊息「此工序累計報工數超過工令數量」。每道工序各自累計（一個工令 100 件，每道工序都可以各報到 100，不是全部工序合計 100）。
  - **選擇跨記錄驗證，不用既有剩餘量檢查**（任務要求說明）：剩餘量檢查是單據轉換專用，只管「來源明細列被轉成目標明細列」的數量；報工不是轉換產物（它是獨立的流水記錄，會反覆新增、修改、刪除），也不依單一來源列計量。跨記錄驗證天生處理新增、修改（移組）、刪除與併發。
- `mes-shipment-within-completed`：模組出貨單，`groupBy` [工令]，`where` 狀態為 draft 或 shipped，`sum(qty) ≤` 經 `workOrder` 取工令的 `completedQty`。訊息「出貨數量超過工令已完工量」。取消的出貨單不計入，取消即釋放數量。
- 已知限制（沿用事件設計 §5.4）：工令數量事後被調低、或已出貨後報工被刪除，不會重新觸發出貨檢查；列表上看得到 `shippedQty > completedQty`，由生管處理。

### 4.8 權限（摘要，以套件 JSON 為準）

| 模組 | 生管 `planner` | 現場 `floor` | 業務／主管 | 品保／設備 |
|---|---|---|---|---|
| 報價、訂單 | 訂單可讀 | 訂單可讀 | 沿用現有 | 品保可讀訂單 |
| 工令、製程 | 全權 | 可讀 | 可讀 | 可讀 |
| 報工 | 全權 | 新增與修改（範圍：群組） | 可讀 | 品保可讀 |
| 出貨單 | 全權 | 可讀 | 可讀、業務可新增 | 無 |

報工的資料範圍設為「群組」：同組現場人員互相看得到，比照故障報修現有做法。金額欄位（訂單、報價）維持原有的金額權限。

### 4.9 報表（隨套件）

- `mes-wo-status` 工令狀態分布：圓環圖。
- `mes-output-monthly` 月別完工量：主模組報工，分組報工日期（月），度量 `sum(goodQty)`，條件「末道工序」，折線圖。
- `mes-defect-reason` 不良原因分布：度量 `sum(defectQty)`，分組不良原因，圓環圖。
- `mes-defect-by-process` 各工序報工與不良：分組工序，度量 `sum(qty)`、`sum(defectQty)`，表格（不良率由兩欄看出；報表目前沒有比率度量，不為此新增）。
- `mes-ship-monthly` 月別出貨量：主模組出貨單，條件狀態 shipped，分組出貨日期（月），度量 `sum(qty)`，長條圖。
- `mes-overdue-wo` 逾期未完工工令：主模組工令，條件「交期早於今天」且狀態在 released／running／hold，表格。需要 §9 的「早於今天」運算子。

Demo 既有的示範報表（`DemoReports`，訂單、報修、保養、品質）不屬於套件，原樣保留；它們引用的是四個範本模組，併入 MES 後改由 MES 的代號對照解析，內容不變。

### 4.10 列印範本

- `mes-wo-sheet` 工令單（掛在 `mes-workorder`）：工令號、客戶、訂單號、交期、優先度；`{{#each mes-wo-item}}` 品項表；`{{#each mes-wo-op}}` 製程表（序號、工序、工作站、預計工時、預計起訖）；已完工量、備註；底部簽核欄。
- `mes-ship-sheet` 出貨單（掛在 `mes-shipment`）：出貨單號、客戶、工令號、訂單號、數量、出貨日期、運送方式、備註、簽收欄。出貨單本身不存品名（品名在工令品項），單據上以工令號對照。

**製程為什麼是工令的明細，而不是獨立模組。** 單據能力 §5 的做法 A（製程為明細、報工指向工令）才印得出「含製程明細」：列印引擎只迴圈明細模組。代價是報工用 `opSeq` 整數加 `process` 下拉對應製程，系統**不驗證**報工的序號存在於該工令的製程，也不在製程列上累計報工量（要看某工序累計，從報工清單依 `opSeq` 篩選，或看 `mes-defect-by-process`）。做法 B（製程獨立）能補這兩點，但需要列印引擎能迴圈「參照本單據的非明細模組」，那是新的通用能力，這次不做（§9 缺口 C）。

## 5. 開關與組合行為

| 組合 | 行為 |
|---|---|
| 只開 MES | 看得到 MES 的選單與全部功能；沒有進銷存模組。工令、報工、出貨完整可用 |
| 只開進銷存 | 同理；採購、進貨、出貨、盤點、庫存報表可用 |
| 兩者都開 | 兩組選單並存，互不影響；各自的事件、檢查、報表照常 |
| 關閉其中一個 | 該應用程式唯讀可查；另一個不受影響，仍可寫入；重新開啟後資料仍在 |
| 三個全開（含 CRM） | 模組鍵、事件鍵、報表代碼、列印範本代碼全站唯一；各自的群組對應獨立 |

「單獨開啟 MES」的前提是環境有 `design.mold`、`sales.customer` 兩個實體提供者（POC 內建模擬提供者，真實部署由整合提供）；缺少時開啟回 409 的 `missingDependencies`，原因 `entityMissing`（套件設計 §7.2）。這與四個範本現在的行為相同。

## 6. 範本併入 MES 的遷移

### 6.1 檔案與程式

- 刪除 `poc/backend/templates/{order,repair,mold-care,quality}.json`；內容轉成 `mes-mold.app.json` 的 `modules`（由腳本一次轉換，輸出進版本庫，腳本不留在版本庫）。
- `TemplateCatalog.Load`：缺目錄時目前會丟例外使站台無法啟動。改成缺目錄回空目錄的 catalog（§9 缺口 A）。`templates/` 之後只放使用者匯入的範本，內建為空。
- `AppPackageSource` 注入 `TemplateCatalog.BuiltIns` 的用途（升級 v1 範本時當作「其他已知套件」）與 `ImportTemplateHandler` 的撞名檢查，內建為空時行為自然退化，不需要改邏輯，只需要測試確認。
- 前端「從範本建立」對話框在清單為空時已顯示「沒有可用的範本」，不需要改。
- Demo：`DemoCatalog.TemplateKeys`、`ActivationOrder` 退役；`DemoStructure` 改為開啟三個應用程式（§11）。

### 6.2 既有測試怎麼調整

原則：**測試的意圖不丟，資料來源換掉。**

| 測試 | 調整 |
|---|---|
| `TemplateValidatorTests`、`TemplateGraphTests`、`TemplateInstantiateTests`、`TemplateExportTests`、`TemplateImportTests`、`Phase8AcceptanceTests`（範本機制本身的測試） | 四個 json 搬到測試專案 `Fixtures/templates/` 當**測試資料**（範本功能仍然存在，只是不再隨產品發行）；`TestApp` 改用 fixtures 目錄的 catalog；`Catalog_LoadsFourBuiltIns` 改成「fixtures 載入四個」並新增「產品的 `templates/` 沒有內建範本」 |
| `AppPackageFormatTests.Upgrade_AllFourBuiltInTemplates_*` | 同樣改讀 fixtures，升級與 `quality` 要求 `order` 的斷言原樣保留 |
| `DemoTemplatesTests`（規則、群組範圍、選項過濾、刪除行為） | 改名 `MesAppTests` 的一部分：改成開啟 `mes-mold` 後做**同一組斷言**（鍵加 `mes-` 前綴）；`Instantiate_Quality_WithoutOrderModule_400` 換成「quality 與 order 同在 MES 內，開啟一次全部到位」與「缺實體提供者時開啟 409」 |
| 新增：等價測試 | 對 fixtures 的每個範本，斷言其每個模組、欄位（型別、設定）、選項、規則、檢視、權限都存在於 MES 套件對應的 `mes-` 模組，只允許 §4.2 列出的改動（逐項白名單）。這是遷移沒丟東西的客觀證據 |
| Demo 系列（`DemoGenerationTests`、`DemoStructureTests`、`OrderRepairScenarioTests`、`MoldCareQualityScenarioTests`、`DemoDataApiTests` 等） | 改用 MES 的代號對照；既有資料的數量斷言維持不變（40 張訂單、報修、保養、品質的數量不動），新增資料另外斷言 |

### 6.3 既有 Demo 資料

Demo 產生器每次部署重建（Render 免費磁碟重新部署會清空），模具廠的訂單 40 張、報修、保養、品質異常的資料與規劃**內容不變**，只是建立在 MES 的模組上。已經用舊版產生過、含舊範本模組的資料庫，`DemoPreflight` 會判為衝突（模組 `order` 等已存在而 MES 沒開），需要重建資料庫；這與 CRM 的已知限制（舊資料庫不補開）同類，寫進結果檔。

## 7. 套件格式與驗證

- 兩個套件都是 `formatVersion` 4（用到事件與檢查）。**不改格式。**
- 引用被使用者取消勾選的欄位時，依賴它的事件、檢查整個略過並警告（事件設計 §12）；因此事件與檢查引用的欄位應是套件裡的必要欄位，設計器的勾選畫面會提示。
- 套件要通過：`AppCatalog` 啟動載入（錯誤拒絕啟動）、`AppPackageValidator`（含 V3、V4 引用層）、事件循環圖、`allowHiddenSource` 檢查、範本列印欄位引用檢查。這些都有現成測試，新套件各加一個「載入並驗證」測試。
- ExportApp 往返：兩個應用程式開啟後匯出、再匯入到空資料庫，事件、檢查、分錄、轉換、列印範本都在（沿用 `AppV4Tests` 的做法）。

## 8. 實作前的可行性驗證（先寫失敗測試，不確定的先釘死）

下列每一項都是我讀程式碼後**推論可行但沒實跑**的假設。實作第一步用最小的測試套件驗證，不成立就採備案並寫進結果檔，不硬做。

| # | 假設 | 不成立時的備案 |
|---|---|---|
| 1 | 一個表頭可以同時有兩個明細模組（工令品項、製程），列印與 `details` 寫入都支援 | 製程改成工令表頭上的一組欄位不可行；改為工令只有「製程」明細，品項改成表頭欄位（工令一張一品項），訂單轉工令改為表頭轉換（一單一工令）並在結果檔說明 |
| 2 | 彙總可以對公式欄位（`goodQty`）取和，且可帶條件（布林欄位為真） | 改彙總 `qty` 與 `defectQty` 兩個輸入欄位（本設計的 `finalQty`／`finalDefect`），`goodQty` 只做顯示 |
| 3 | 檢查的 `limit.via` 可以讀被參照記錄上的彙總或公式欄位值（讀 `ValuesJson`） | 改成把 `totalQty` 之類的值由事件寫成一般欄位，或限制只用輸入欄位 |
| 4 | 事件 `update` 效果能經參照把狀態設成常數，且已在該狀態時不失敗；轉移未定義時整筆回滾且訊息可讀 | 訊息不可讀則在設計文件列為體驗缺口 |
| 5 | 實體參照欄位可以帶值（`fill`）到另一個同實體的參照欄位，轉換的 `header.copy` 同理 | 客戶改為不帶值，使用者自行選 |
| 6 | 報表可以對公式欄位取和、度量帶條件、日期分組可按月 | 報表改對輸入欄位取和 |
| 7 | 事件 `create`＋`each` 的目標欄位型別相容：自動編號 → 文字、日期常數 `today`、下拉常數 | 單據號不帶入 `docNo` |
| 8 | `fromOrder`（訂單參照）可以從工令帶值到出貨單的 `order` 欄位（`fill`，目標相同的參照欄位） | 出貨單的訂單欄位由使用者選或略過，事件對訂單的效果改為略過 |

## 9. 通用能力缺口

### A. `TemplateCatalog.Load` 容忍缺目錄（做，很小）

內建範本全部搬走後，輸出目錄沒有 `templates/`，目前丟例外使站台啟動失敗。改成目錄不存在視為空 catalog。所有情境（沒有內建範本的部署、測試）都受益，不涉及業務。

### B. 可選相依（不做，需要使用者決定）

缺什麼：應用程式宣告「若 X 存在則連動，否則略過」。現在 `requires` 只有硬相依，事件與檢查又只能指向同一份套件。為什麼兩個以上情境需要：MES 出貨扣進銷存庫存、CRM 訂單產生 MES 工令、階段 3 的會計過帳都是同一個需求。為什麼這次不做：它牽動套件格式、相依檢查、事件圖、關閉規則，是產品方向（應用程式要不要有「整合」這一層）而不是這次的實作問題。目前的設計不依賴它。見 §15。

### C. 列印迴圈「參照本單據的非明細模組」（不做）

缺什麼：列印範本只能迴圈明細模組。為什麼可能有兩個情境：MES 製程獨立成模組（做法 B）、發票列印收款紀錄。為什麼這次不做：做法 A 已能滿足「工令單含製程明細」，而這個能力要處理迴圈模組的權限與資料範圍，風險不小，等真的有第二個情境再做。

### D. 日期「早於今天」篩選運算子（做，小而獨立，列為可砍項）

缺什麼：現有日期篩選只有「未來／過去 N 天內」，視窗含今天（`ConditionEvaluator`、`DynamicQueryBuilder`、`ReportRunner` 都是 [今天 − N, 今天]）。「逾期」要的是嚴格早於今天，用「過去 N 天內」只能近似，而且會把今天到期的算進去。為什麼兩個以上情境需要：MES 逾期未完工工令、進銷存逾期未到貨的採購單；CRM 的逾期活動、階段 3 的應收帳齡同理。

做法：新增篩選運算子 `beforeToday`（日期欄位值 < 業務日），只用於檢視、報表、儀表板篩選與規則條件；改動集中在 `FilterOperator`、`ConditionParser`、欄位處理器的運算子白名單、`DynamicQueryBuilder`、`ReportRunner.Conditions`、`ConditionEvaluator`，前端條件編輯器加一個選項與四語系文案。**這項若實作時遇到預期外的阻礙，就退回「過去 N 天內」近似並寫進結果檔，不拖住其他項目。**

## 10. Demo 資料

### 10.1 原則

- Demo 是一套完整資料。產生時開啟三個內建應用程式（CRM、MES、進銷存），資料一次產生；訪客可以用既有的應用程式關閉與開啟端點開關它們，資料不因關閉而消失（階段 5 的開啟頁面做完後就是訪客的入口）。
- 沿用現有產生機制：`IDemoScenario` 只用 `plan.Today` 與固定種子的 `Random` 規劃步驟，全部走一般的建立、更新、轉換指令，以示範帳號的身分執行，所以權限、狀態機、檢查、事件真的跑過一次。時間點全部落在今天之前、180 天內。
- 既有四個情境（訂單 40 張、報修、保養、品質）與 CRM 情境**不動**，只新增。新增情境排在它們之後，可以引用前面情境建的記錄。
- 資料全部虛構：公司與人名自編、電話用 555 號段、Email 一律 `example.com`，不使用任何真實公司或產品名稱；實作時做一次人工檢視與關鍵字掃描。

### 10.2 新增的 Demo 帳號與群組

沒有任何機制新增，只是 `DemoPeople` 多幾筆資料：

| 群組 Key | 帳號 | 用途 |
|---|---|---|
| `planner` | `planner01` | MES 生管 |
| `purchasing` | `buyer01` | 進銷存採購 |
| `warehouse` | `keeper01` | 進銷存倉管 |
| `ops-manager` | `ops01` | 進銷存主管 |

應用程式群組對應：MES `planner` → `planner`；進銷存 `buyer` → `purchasing`、`keeper` → `warehouse`、`manager` → `ops-manager`。MES 其餘代號沿用現有 Demo 群組（`sales`、`sales-manager`、`floor`、`maint`、`qa`）。`admin`（群組 `mis`）三個應用程式都能看。`DemoPeople.ConflictsAsync` 與 `DemoLoginApiTests` 隨之更新。

### 10.3 MES 新增情境

**`MesQuoteScenario`**（種子 20261108）：報價單 14 張，建立日分布在今天前 150 天到 50 天；草稿 3、待核准 2、退回 1、核准 8。8 張核准的報價單中 5 張轉成訂單（其中 2 張分兩次部分轉換），訂單狀態有已確認與出貨中。業務 `sales01`／`sales02` 建立，`manager01` 核准。

**`MesProductionScenario`**（種子 20261109）：

- 工令 28 張，由訂單（含既有 40 張與新轉的）以轉換建立，建立日散在今天前 135 天到 5 天；每張 1 到 2 個品項、3 到 5 道製程（備料、粗加工、精加工、放電、線割、拋光、組裝、試模按模具廠常見順序取）。
- 狀態分布：完工 11、短交結案 1、生產中 6、已發放 3、暫停 3（缺料 2、設備 1）、取消 2、草稿 2。
- **逾期：** 其中 4 張交期早於今天仍在 released、running、hold（含 2 張缺料暫停）。
- 報工約 135 筆，`floor01`／`floor02` 建立，橫跨 5 個月；完工的工令每道工序 1 到 3 批報工，進行中的工令報到中途。不良率基底 2% 到 4%，**刻意做出兩個波動**：某個月的放電工序拉高到約 9%、拋光約 7%，讓「各工序不良」與「不良原因」報表有東西看。
- 出貨單約 19 張（出貨 16、草稿 2、取消 1），`planner01` 建立並出貨；完工工令分 1 到 2 批出貨。
- 品質異常 6 筆，掛在高不良的工令上（`mes-quality.workOrder`）。
- **被擋下的紀錄：** 產生器新增「預期被拒」步驟：送出一個應該被擋的請求，斷言回 400 或 409，不寫入任何東西；失敗就是 Demo 產生失敗（等於把檢查在 Demo 資料上驗證一次）。放 4 個：兩次超過工令數量的報工、兩次超過已完工量的出貨。被擋的結果不會留下記錄，所以另外留兩張**草稿出貨單**，備註寫「完工量不足，暫緩出貨」，讓訪客在清單上看得到卡關的樣子。

### 10.4 進銷存情境

**`InventoryScenario`**（種子 20261110）：

- 基本資料：倉庫 3（原料倉、成品倉、耗材倉）、品項 24（原料 8、零件 6、成品 6、耗材 4）、供應商 6、客戶 6。
- 期初：今天前 150 天，每個倉庫一張盤點調整單（reason = opening）入帳。
- 採購單 18 張（今天前 145 天到 8 天）：結案 12（其中 4 張分兩次進貨）、已確認 3（**2 張預定交期已過、未到貨完畢：逾期**）、草稿 1、取消 2。進貨單約 26 張：由採購單轉換 18 張、直接進貨 6 張、取消 1 張（已過帳後取消，產生沖銷異動）、草稿 1 張。
- 出貨單 20 張：出貨 16、已出貨後取消 1（產生沖銷異動）、草稿 3。**缺料：** 3 張草稿中有 2 張是產生器嘗試過帳、被 `inv-no-negative-stock` 擋下（預期被拒步驟，斷言 400 與訊息），之後在備註寫「庫存不足，待補貨」。
- 盤點調整 4 張（期初之外）：盤虧 2、盤盈 1、另有 1 次「盤虧超過餘額」預期被拒。
- 庫存異動自動產生約 130 筆，其中 2 筆是沖銷。3 個品項期末餘額低於安全庫存。
- 進貨、出貨、盤點由 `keeper01` 建立過帳，採購單由 `buyer01` 建立，取消與主管動作由 `ops01`。

### 10.5 儀表板

Demo 儀表板引用套件自帶的報表（套件格式目前沒有 `dashboards` 區段，放不了），與 CRM 的做法相同；既有三張（業務、設備與保養、品質）與 CRM 儀表板不動。新增兩張：

- **生產儀表板**（指派 `planner`、`floor`、`mis`）：本月報工筆數（指標）、工令狀態分布（圓環）、月別完工量（折線）、不良原因分布（圓環）、各工序報工與不良（表格）、逾期未完工工令（表格）。
- **進銷存儀表板**（指派 `warehouse`、`purchasing`、`ops-manager`、`mis`）：低庫存前 8 名（庫存餘額表格，升序取前 8）、月別進出量（長條）、採購單狀態分布（圓環）、各供應商採購金額前 6 名（長條）、本月出貨單筆數（指標）。

### 10.6 已知限制

- 舊版 Demo 資料庫不會補開 MES 與進銷存，需要重建（§6.3）。
- Demo 站預設關閉附件上傳，兩個應用程式都沒有附件欄位，不受影響。

## 11. DemoStructure 的改動

1. 不再套用四個範本、不再用指令補「品質異常筆數」欄位與權限（都進套件了）。
2. 在補完群組之後依序開啟 `mes-mold`、`inventory`、`crm-sales`（群組對應見 §10.2），啟用順序由 `AppOpener` 處理。
3. `DemoContext.Ids` 改為 MES 的代號對照（既有四個情境與 `DemoReports` 用的 `order.status`、`quality.critical` 這類限定名不變，所以幾乎不用改）；`DemoContext.Inventory` 另存進銷存的對照，`DemoContext.Crm` 照舊。
4. 示範報表（`DemoReports`）與儀表板（`DemoDashboards`）照舊建立，再把兩個套件自帶的報表登進報名對照給新儀表板引用。
5. `DemoPreflight`：衝突檢查的模組來源改為「三個應用程式的成員模組鍵」；`AllPresent` 改為三個應用程式都已開啟。

## 12. 測試清單

原則同事件設計：每項先寫失敗測試再實作。

**單元（Application）**

- 兩個套件由 `AppCatalog` 載入與驗證通過；事件循環圖無環；`allowHiddenSource` 檢查通過；列印範本欄位引用有效；套件內所有 `fill`、轉換、報表引用能解析。
- 等價測試（§6.2）：四個舊範本的每個模組、欄位、選項、規則、檢視、權限逐項存在於 MES 套件，只允許白名單內的改動。
- 缺目錄的 `TemplateCatalog` 回空；`beforeToday` 運算子在解析、處理器白名單、規則求值、報表執行的單元測試（含「今天到期不算逾期」「昨天到期算」「欄位為空不算」）。
- Demo 規劃：新情境的步驟為確定性（同日同種子輸出相同）、全部落在 180 天內、不引用不存在的記錄、各狀態與數量符合 §10。

**整合（WebAPI，檔案型 SQLite 的 `TestApp`）**

進銷存（`InventoryAppTests`）：

- 空系統開啟、關閉後唯讀、重新開啟資料仍在。
- 採購單 → 進貨單部分轉換（剩餘量、追溯）；進貨過帳產生異動、數量與倉庫正確、擁有者取來源擁有者；過帳後單據唯讀；取消進貨沖銷異動，餘額回復。
- 出貨過帳扣庫存；庫存不足被擋（400，`_record` 為檢查訊息），整筆回滾（單據仍是草稿、沒有任何異動）；盤虧超過餘額被擋；沖銷一筆已被消耗的進貨被擋。
- 庫存異動不能 PUT、DELETE（409），重複沖銷 409，沖銷分錄不能再沖銷。
- 倉管沒有金額權限時看不到單價、仍能過帳。
- 套件往返：開啟 → 匯出 → 匯入空庫，行為一致。
- 列印出貨單：欄位、明細迴圈、XSS 跳脫。

MES（`MesAppTests`）：

- 四個舊範本的行為（規則、群組範圍、選項過濾、刪除行為：cascade、setNull、restrict）在 MES 內仍成立。
- 報價單 → 訂單 → 工令的轉換鏈：核准權限；部分轉換與剩餘量；8 個請求同時轉同一張訂單不超轉（≥ 1 成功、其餘 409、已轉量 ≤ 訂單量）。
- 工令推進：draft → released → running（報工事件）；hold 中的工令收報工被擋；done 的工令收報工被擋；完工量不足時轉 done 被擋，轉 short 通過。
- 報工：不良數、不良原因規則；工令的 `defectQty`、`completedQty`（末道工序）即時更新，修改、刪除報工後跟著變；某工序累計超過工令數量被擋（新增、修改、刪除再新增三種路徑），不同工序各自累計。
- 出貨：超過已完工量被擋；取消出貨釋放數量；出貨過帳後訂單進入 shipping；訂單已 closed 時出貨被擋；工令沒有來源訂單時出貨仍成功。
- 列印工令單：品項迴圈與製程迴圈都有內容、跳脫正確；未授權者 403／404。
- 整條主流程：訂單 → 工令 → 報工 → 出貨，完工量、出貨量、訂單狀態都對。

組合（`AppCombinationTests`）：

- 只開 MES、只開進銷存、兩者都開、三個都開：各自做一次主流程；事件鍵、檢查鍵、報表代碼無衝突。
- 兩者都開時，MES 出貨與進銷存庫存互不影響（MES 出貨後進銷存餘額不變，反之亦然）。
- 關閉進銷存後 MES 仍可寫入，進銷存資料仍可查；反向亦然；重新開啟恢復。
- 缺實體提供者時開啟 MES 回 409 `entityMissing`，不留殘餘。

併發與重送（`InventoryConcurrencyTests`，`Task.WhenAll`）：

- 初始庫存 100，50 個出貨單同時過帳各 3：恰好 33 個成功、17 個 400、無 500、最終餘額 1、無任何時刻為負。
- 同一把 `Idempotency-Key` 重送 20 次出貨單建立：一張單據；過帳重送不重複扣庫存（事實唯一約束）。
- 進貨與出貨同時過帳同一品項：餘額等於所有成功異動的加總。
- 10 個請求同時沖銷同一筆異動：一個成功、其餘 409。
- MES：多個報工同時新增到同一工序的剩餘量邊緣，累計不超過工令數量。

Demo（`DemoGenerationTests` 等）：

- 完整產生一次：三個應用程式都開、各模組筆數符合 §10、跨月（報工、異動至少跨 5 個月）、有逾期工令與逾期採購單、有不良、有被拒步驟（產生器內斷言）、2 張草稿出貨單備註為缺料。
- 兩次產生結果逐筆相同（確定性）。
- 示範帳號登入後看到對應的選單與儀表板；`planner01` 能推進工令、`floor01` 不能。
- 既有四個情境的斷言不變。

**前端**：沒有新增頁面（全部由 schema 驅動的通用頁面）。新增 `beforeToday` 運算子的條件編輯器選項、四語系文案（既有的 i18n 鍵一致性測試照跑）；`bun run test`、`typecheck`、`build`。

**實機**：本機以 Demo 資料走 MES 主流程（開工令、報工、出貨）與進銷存出貨（含一次被擋的出貨），四種版型各一輪（手機 390×844、平板 820×1180、筆電 1366×768、桌機 1920×1080），檢查水平溢出、遮擋、選單開關、按鈕可按。

## 13. 實作順序

每項先寫失敗測試，能分開就分開 commit：

1. 缺口 A（`TemplateCatalog` 容忍缺目錄）與缺口 D（`beforeToday`）。D 可砍。
2. 把四個範本 json 搬成測試 fixtures、`TestApp` 改用 fixtures、調整範本機制的測試，確認全綠（純重構，行為不變）。
3. §8 的可行性驗證（最小測試套件）；結果決定備案。
4. MES 套件：由腳本轉換四個範本、手寫新增模組；`MesAppTests`、等價測試；刪除 `templates/*.json`。
5. 進銷存套件與測試（含併發）。
6. 組合測試。
7. 列印範本（跟隨各自套件，但單獨 commit 以便審查）。
8. Demo：人員與群組、結構、新情境、報表與儀表板、Preflight；調整既有 Demo 測試。
9. th、vi 文案機器翻譯補齊（含舊範本欄位）；標明待母語者校對。
10. 實機走查（四種版型）、截圖放結果目錄。
11. 結果檔；路線文件的階段 2 加一行完成註記（不改其他內容）。

驗收順序：Domain、Application、WebAPI 三個測試專案逐一跑，前端 `bun run test`、`typecheck`、`build` 各自回報。

## 14. 我代為決定的事

1. **MES 獨立、進銷存獨立、兩者與 CRM 兩兩獨立（§2.1）。** 理由：鐵則要求每個應用程式能獨立開關，而跨應用程式引用欄位、事件、檢查都不允許。錯了的代價：要整合就得先做缺口 B。
2. **出貨在兩邊各有一個且不連動（§2.2）；品項不共用（§2.3）。** 錯了的代價同上，另外料號要打兩次。
3. **已完工量用彙總（末道工序報工的良品數）不用事件。** 理由：事件設計已決定不做累加，彙總天然冪等。錯了的代價：使用者用「事件回寫」的字面理解，會覺得不是事件；行為等價。
4. **報工上限用跨記錄驗證（每道工序各自累計）。** 理由 §4.7。
5. **製程為工令的明細（做法 A）、工令品項另為一個明細（訂單明細轉換的目標）。** 錯了的代價：報工序號不被驗證存在、製程列沒有累計報工量（§4.10、缺口 C）。
6. **「末道工序」用報工上的布林欄位，而不是自動判斷。** 理由：報工不能參照製程明細列，系統無從知道哪一道是最後一道。錯了的代價：使用者忘記勾末道，工令完工量不增；規則（轉 done 需完工量達標）會擋住結案提醒補勾。
7. **暫停（hold）中的工令不收報工（`hold → running` 不定義）。** 錯了的代價：暫停中趕著補報要先恢復到 released。
8. **MES 模組鍵加 `mes-` 前綴，訂單改用狀態機取代唯讀規則，新增追溯與彙總欄位（§4.2）。** 錯了的代價：已經用舊範本建立了 `order` 等模組的客戶，不會自動轉成 MES 的模組（新舊並存，不衝突）。
9. **Demo 開啟三個應用程式、新增四個示範帳號與群組（§10.2）。** 錯了的代價：Demo 帳號多四個；要撤回只需刪 `DemoPeople` 幾行與群組對應。
10. **「早於今天」運算子做成通用能力（缺口 D）。** 錯了的代價：多一個篩選運算子；可砍，退回近似。
11. **被擋下的情境以「預期被拒步驟＋草稿單備註」呈現，不新增紀錄「被擋事件」的機制。** 理由：失敗的請求整筆回滾，系統本來就不記，不為 Demo 破例。
12. **出貨單在 MES 一張只對一張工令。**
13. **列印範本三份：工令單、MES 出貨單、進銷存出貨單。**
14. **Demo 資料量**（§10.3、§10.4）是我估的，以「訪客一眼看得到跨月、異常、報表有內容」為目標，實作時可微調。

## 15. 要使用者決定的事

1. **要不要做「可選相依」（缺口 B）。** 做了，MES 出貨才能自動扣進銷存庫存、CRM 訂單才能一鍵轉 MES 工令；不做，三個應用程式永遠是各自獨立的資料孤島。這是產品方向：應用程式之間要不要有整合層。目前設計不依賴它，也沒有堵死它。
2. **客戶主檔以誰為準。** CRM、MES、進銷存各有各的客戶（§2.4）。
3. **MES 的「訂單」與 CRM 的「訂單」是否長期並存。** 兩個應用程式都有報價與訂單，為了獨立開關而重複。如果客戶通常兩個都開，這個重複是缺點。

## 16. 風險與已知限制

1. §8 的八項假設沒實跑；其中 1（兩個明細模組）與 2（彙總公式欄位）影響最大，備案已寫。
2. 沿用事件設計的限制：新增檢查不回頭掃描、限額被調低不觸發、事件不回補既有單據。
3. 單據能力 §4.3 的限制：取消目標單據不釋放已轉量；目標單據事後改大數量可能超轉。
4. MES 的報工序號不驗證存在於製程；出貨量在報工被刪除後可能暫時大於完工量（列表看得到）。
5. 模具廠的資料量遠小於負載報告的門檻；庫存餘額的聚合在熱門品項上的耗時，沿用事件設計 §5.5 的量測，必要時用結轉。
6. th、vi 為機器翻譯，術語（如「工令」「報工」「製程」）可能不符當地習慣，待母語者校對。
7. 舊版 Demo 資料庫需要重建（§6.3）。
