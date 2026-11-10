# s2d 設計：共用主檔（往來對象、品項）與模具歸位

> 起草 2026-11-08｜路線階段 2 的收尾，排在 s2e（CRM 與 MES 的訂單合併）與階段 3 會計（s3c）之前｜前置：[路線](2026-11-06-erp-roadmap-design.md)、[應用程式套件](2026-11-06-app-package-design.md)、[進銷存與 MES](2026-11-08-inventory-mes-design.md)、[應用程式整合層（s2c）](2026-11-09-app-integration-design.md)
> 本文件**只寫設計，不改程式**。實作等 s3a（報表引擎）合併後才開始，因為兩者都會動 `poc/backend/apps/*.app.json`。
> 標「（我的判斷）」的是我代為決定、可以改的，彙整在 §16；需要主控裁決的在 §17，各附建議。整體資料模型 `2026-11-10-erp-data-model-design.md`（分支 `docs/erp-model` 的草稿，未審查）我已讀過 §2、§6.1、§9 並對齊，採用與不採用的差異在 §19。
> **修訂 2026-11-08（第一輪審查後）：** 依第一輪審查的 10 項必修與 1 項建議改寫正文，另配合 ERP 資料模型審查拆分付款條件、品項加供應方式；逐項對照在 §20。這些裁決是**主控裁決，待使用者確認**。總原則：**fail closed、結果不依開啟順序、往返不遺失。**
> **修訂 2026-11-08（第二輪審查後）：** 依第二輪審查的 7 項必修、7 項建議、6 項實作準備度改寫正文，與裁決牴觸的舊寫法已一併改掉（例如取消「關閉時轉停用」備案、`Dynamic_AppGrant` 不再存群組代號、資料範圍改為逐動作取聯集）；逐項對照在 §20.2。同樣是**主控裁決，待使用者確認**。
> **修訂 2026-11-08（第三輪審查後）：** 依第三輪審查的 7 項必修改寫正文：貢獻參照禁止 `cascade`、`fromLead` 改 `setNull`、清空參照列入凍結路徑；貢獻欄位不收 `rollup`；整合可解析主檔目標；孤兒表加 `Kind`、接回不避撞；權限頁存檔以重算值為基準取差異；往返測試改用非內建 id 的套件；孤兒區塊不得新增欄位。逐項對照在 §20.3。同樣是**主控裁決，待使用者確認**。

## 1. 問題與現況盤點

使用者裁決（2026-11-08）：客戶等主檔**全系統只維護一份**，照 Odoo 與 SAP 的慣例。現在不是這樣。我重新核對過三份套件與核心程式，重複的有這些：

| 對象 | 現有的份數與位置 |
|---|---|
| 客戶 | 4：核心模擬 `sales.customer`、MES 擴充模組 `mes-customer-info`、CRM `crm-customer`、進銷存 `inv-customer`（「出貨客戶」） |
| 供應商 | 1：進銷存 `inv-supplier`（但同一家公司可能既是客戶又是供應商，沒有辦法表達） |
| 聯絡人 | CRM `crm-contact`（掛在 CRM 客戶下），加上進銷存客戶與供應商用文字欄位 `contact` 存聯絡人 |
| 品項 | CRM `crm-product`（欄位 `sku`、`price`）、進銷存 `inv-item`（`category`、`price`、`safetyStock`）、MES 報價明細／訂單明細／工令用文字欄位 `itemNo`、`itemName` |
| 模具 | 核心模擬 `design.mold`，MES 以擴充模組 `mes-mold-info` 加欄位 |

幾個值得注意的點：

- 核心裡寫死的 `sales.customer`、`design.mold`（`MockCustomerProvider`、`MockMoldProvider`、兩張 `Mock_*` 表、`DemoPeople` 種子）是 POC 遺留，違反鐵則 1「ERP 邏輯不寫死」。
- 「品項價格」在進銷存是同一個欄位 `price`，採購明細和出貨明細都從它帶值（`item.price`）：採購時它是進價，出貨時它是售價，語意混在一起。這是拆主檔時要順手釐清的（§5.3、§12.4）。
- MES 與進銷存各有一張出貨單、MES 與 CRM 各有一套報價和訂單。這是 s2e 的範圍，這份設計只負責讓它們**共用同一份客戶與品項**，使 s2e 能直接動手（§14）。

## 2. 目標與不做

**目標：**

1. 往來對象（客戶、供應商、聯絡人）合成一份，品項合成一份；各應用程式只在這一份上加自己的欄位。
2. 每個應用程式仍能單獨開關；空系統仍是合法起點，**主檔不是平台必裝**，只在有應用程式需要它時才出現。
3. 核心不寫死任何主檔：主檔本身也是用套件格式描述的定義，核心只補通用能力。
4. 移除 `sales.customer`、`design.mold` 模擬實體；模具改為 MES 自己的模組。

**不做：**

- 多公司、多幣別、品項變體（Odoo 的 `product.product`）、單位換算、版本與有效日期（路線已列「這版不做」）。
- 主檔套件的升級入口（路線已延後，第一個客戶上線前要決定）。
- s2e 的工作：訂單合併、出貨合併、逐列轉換。
- 倉庫要不要也做成共用主檔：目前只有進銷存用，不符「至少兩個不相干應用程式」的門檻（鐵則 2），不做。

## 3. 選定的方案與為什麼

**主檔是一種特殊的套件（`manifest.kind = "master"`），由使用它的應用程式自動開啟；使用者應用程式只能在主檔上「貢獻」欄位，不能改主檔的其他部分。**

具體上：

- 往來對象、聯絡人、品項各是一份內建的主檔套件（`partner`、`contact`、`item`），內容就是一般的模組、欄位、檢視、群組，格式與驗證、開啟、關閉、匯出的程式全部重用。
- 應用程式在 `manifest.requires.masters` 宣告「需要往來對象」「需要品項」。開啟時，主檔還沒開就自動開，已開就綁上去；之後只在主檔的模組上加自己的區塊與欄位（新增頂層 `contributions[]`）。
- 主檔的狀態只能由使用它的應用程式推得，不能直接開關：**只要有任何一個應用程式（直接或經另一個主檔間接）使用它且是 `Open`，主檔就是 `Open`**；全部關閉，主檔也 `Closed`（資料唯讀保留）；全部刪除，主檔留著不動（§7）。
- 貢獻欄位歸屬貢獻者：貢獻者不是 `Open` 時，這些欄位在寫入管線最底層凍結，任何寫入者（含自動計算）都改不了；預設只有貢獻者自己的群組看得到（§6、§7）。

**考慮過、不採用的做法：**

| 做法 | 不採用的理由 |
|---|---|
| 核心內建往來對象實體（像現在的 `sales.customer`，改成真資料表） | 欄位寫死在核心，客戶改不了，違反鐵則 1 與差異化定位（客戶自己改應用程式） |
| 沿用「擴充既有實體」（`Module.Kind = Extension`、`HostEntityKey`，MES 現在的 `mes-customer-info`） | 每個實體只能有一個擴充模組（`HostEntityExists`），多個應用程式無法各加一份；擴充資料是另一筆記錄（`Record.HostKey`），列表、篩選、報表要跨記錄；寫入時還要走實體提供者。這個機制保留給「外部系統既有實體」，不是為了應用程式之間共用 |
| 每個應用程式各帶一份完整的主檔定義，先開的為準 | 定義會漂移：CRM 帶的客戶欄位和進銷存帶的不同，誰先開誰決定；套件作者無法預期主檔長相 |
| 每個用到的套件都完整宣告同一份基本欄位，目錄層對帳（整體設計草稿 §6.1、§9 第 4 題的建議） | 客戶開啟後可以改主檔，之後才開的套件宣告就和實際模組不一致，要「拒絕開啟」還是「以誰為準」都說不通；三份套件重複同一段定義，維護成本三倍。主檔獨立成一份套件，基本定義仍由套件宣告、不寫在核心，也滿足同一份草稿「不寫死在核心」的要求（§19） |
| 把主檔當 s2c 的「整合」夥伴 | 整合是**可選**相依（夥伴不在就靜默略過），而主檔是**必要**相依；整合只能加欄位到擁有者自己的模組，不能加到夥伴的模組。語意相反，勉強套用會讓狀態機（`Waiting`、`Suspended`）白白複雜化（§10） |

這個方案與 Odoo 的做法一致（`res.partner` 一張表，各模組用繼承加欄位），也等同 SAP 的「一般資料＋角色／視圖資料」（§12）。

## 4. 格式（套件格式 5 → 6）

`CurrentFormatVersion` 提高到 **6**。格式 5 以下的檔案出現 `kind: master`、`requires.masters` 或 `contributions` 一律整份拒絕（fail-closed，理由同 s2c §3.1：舊讀取器靜默略過會讓客戶以為有共用主檔卻沒有）。三個內建應用程式套件因為要貢獻欄位，都升到 6。

### 4.1 主檔套件

主檔套件和應用程式套件同一個檔案格式，差別在 `manifest.kind`（預設 `"app"`）：

```json
{
  "format": "formwright-app",
  "formatVersion": 6,
  "manifest": {
    "id": "partner", "kind": "master", "version": "1.0.0",
    "name": { "zh-TW": "往來對象", "en-US": "Business partners" }
  },
  "groups": [ { "code": "steward", "name": { "zh-TW": "往來對象管理員" } } ],
  "modules": [ {
    "code": "partner", "key": "master-partner", "kind": "standalone",
    "titleField": "name", "section": "主檔", "icon": "handshake",
    "extensible": true,
    "blocks": [ { "code": "main", "fields": [ /* §5.1 */ ] } ],
    "views": [ /* 客戶、供應商 兩個共用檢視 */ ],
    "permissions": [ { "group": "steward", "scope": "all", "privileges": { "read": true, "add": true, "edit": true, "delete": true, "printExport": true, "pricing": true, "approve": true } } ]
  } ]
}
```

主檔套件的限制：

- 只收 `groups`、`modules`（含 `views`、`rules`、`permissions`）、`fieldAccess`、`optionScopes`、`reports`。不收 `workflows`、`conversions`、`events`、`checks`、`ledgers`、`printTemplates`、`integrations`、`contributions`，`requires` 只能有 `masters`。理由：主檔是穩定的基礎資料，不帶流程；流程屬於使用它的應用程式。
- 至少一個模組標 `"extensible": true` 才接受貢獻（只有主檔套件能設這個旗標）。
- `manifest.id` 與應用程式共用同一個命名空間（`Dynamic_App.Key` 唯一），內建主檔用 `partner`、`contact`、`item`。
- 模組的 `key` 用 `master-` 前綴（`master-partner`、`master-contact`、`master-item`），避免與客戶自建模組撞名。
- **主檔不能直接開啟、關閉、重開**：`OpenApp`、`close`、`reopen` 對 `kind: master` 一律回 409「由使用它的應用程式管理」，可開啟清單（`AppPackageSource.ListAsync` 的對外結果）不列主檔（§7；第二輪必修 7，主控裁決，待使用者確認）。
- 主檔模組上的欄位（基本欄位與貢獻欄位）可帶 `"active": false`，表示客戶停用了它，往返後仍是停用（§9；第二輪建議 3，主控裁決，待使用者確認）。省略時為啟用。這是格式 6 本來就要加的屬性之一，不另升版。

### 4.2 `requires.masters`

```json
"requires": { "masters": [
  { "id": "partner", "modules": ["partner"] },
  { "id": "item",    "modules": ["item"] } ] }
```

形狀同 `requires.apps`，語意不同：

- `requires.apps`（不變）：硬相依，缺了開不起來，**不自動開**。
- `requires.masters`（新增）：硬相依，但**自動滿足**：主檔不存在就用目錄裡的主檔套件開起來，已存在就綁定，已存在但 `Closed` 就重新開啟（§7）。目錄裡找不到該主檔 id 時開啟回 409，原因 `masterNotAvailable`。
- 一個套件至多 5 個主檔。主檔套件自己也可以有 `requires.masters`（例如聯絡人需要往來對象），開啟時依相依順序自動開啟；使用者套件只要列直接用到的主檔，不必列主檔的相依（但引用主檔符號時，該主檔必須列在自己的 `requires.masters`）。
- **主檔只能出現在 `requires.masters`：** 同一個 id 不得同時出現在 `requires.apps` 與 `requires.masters`；`requires.apps` 裡的 id 若是目錄或已匯入套件中的主檔（`kind: master`），整份拒絕（第二輪必修 1，主控裁決，待使用者確認）。
- **循環檢查要把主檔的邊納入：** 現行 `ImportAppPackage` 建相依圖時只取 `Requires.Apps`（`ImportAppPackage.cs` 約 33～38 行），目錄載入也一樣。兩處都改成同時加入 `requires.masters` 的邊，主檔之間、主檔與應用程式之間都不得循環（第二輪建議 7，主控裁決，待使用者確認）。

### 4.3 `contributions[]`：在主檔上加自己的欄位

```json
"contributions": [ {
  "master": "partner", "module": "partner",
  "block": { "code": "crm", "name": { "zh-TW": "CRM", "en-US": "CRM" } },
  "fields": [
    { "code": "industry", "type": "enum", "label": { "zh-TW": "產業" },
      "config": { "isFilterable": true, "showInList": true },
      "options": [ { "code": "manufacturing", "label": { "zh-TW": "製造" } } ] },
    { "code": "fromLead", "type": "reference", "label": { "zh-TW": "來源潛在客戶" },
      "config": { "readOnly": true, "onDelete": "setNull", "refTarget": { "kind": "module", "key": "lead" } } }
  ],
  "permissions": [
    { "group": "sales",   "scope": "all", "privileges": { "read": true, "add": true, "edit": true, "delete": false, "printExport": true, "pricing": true, "approve": false } },
    { "group": "manager", "scope": "all", "privileges": { "read": true, "add": true, "edit": true, "delete": false, "printExport": true, "pricing": true, "approve": false } } ],
  "fieldAccess": [ ]
} ]
```

| 屬性 | 說明 |
|---|---|
| `master`、`module` | 必須在 `requires.masters` 裡，且該模組在主檔套件中標 `extensible`。同一個（主檔, 模組）至多一筆貢獻 |
| `block` | 這個應用程式在主檔模組裡的**專屬區塊**（代號、名稱）。每個貢獻者一個區塊，貢獻的欄位都放在裡面，畫面上就是主檔記錄下的「CRM」「進銷存」分區（類似 SAP 的視圖） |
| `fields[]` | 要加的欄位，格式同模組欄位，**不得必填**，型別有限制（見下） |
| `permissions[]` | 貢獻者自己的群組對這個主檔模組的權限（§6）。只能列貢獻者套件自己宣告的群組；開啟時記成權限來源記錄 |
| `fieldAccess[]` | 同一般 `fieldAccess`，只能針對貢獻者自己的欄位，用來在貢獻者自己的群組之間再收緊 |

不收：規則（`rules`）、檢視、區塊重排。貢獻只加欄位與區塊，不碰主檔的其他定義。

貢獻欄位不得必填，原因同 s2c §5.2：別的應用程式建立主檔記錄時，不該被不相干的欄位擋住。

**貢獻欄位允許的型別**（第二輪必修 5、準備度 5，主控裁決，待使用者確認）：

- 允許：文字類（`text`、`textarea`、`email`、`phone`、`url`）、數字類（`integer`、`decimal`、`currency`、`percent`）、日期類（`date`、`datetime`、`time`）、`enum`、`boolean`、`user`、`reference`、`formula`。
- `reference` 只能指向貢獻者**自己的**模組或 `requires.masters` 裡的主檔模組。
- **`reference` 的 `onDelete` 只收 `restrict` 或 `setNull`，禁止 `cascade`**（第三輪必修 1，主控裁決，待使用者確認）。理由：主檔記錄不可以因為某個應用程式的記錄被刪而消失；`cascade` 會讓 CRM 刪一筆潛在客戶就連帶軟刪進銷存正在用的往來對象。指向**貢獻者自己模組**的參照（例如 `fromLead` → `lead`）一律宣告 `setNull`：刪潛在客戶時清掉追溯值，刪除預覽本來就列出被清空的筆數。若宣告成 `restrict`（現行預設），轉換過一次之後潛在客戶就刪不掉（`fromLead` 又是唯讀，使用者清不掉），CRM 的成員模組永遠留著未軟刪除的記錄，應用程式也刪不掉。
- `formula` 只能引用主檔基本欄位與本貢獻自己的欄位，不得引用別的貢獻者的欄位。
- 禁止：`autonumber`（取號不經輸入，凍結後仍會取號）、`multienum`、`attachment`、`rollup`。
- **不收 `rollup`**（第三輪必修 2，主控裁決，待使用者確認）：彙總放在主檔模組上，加總的一定是別的模組的記錄（`RollupRefsOf` 落在子模組），照「只能引用基本欄位與本貢獻欄位」寫不出合法的彙總；若放寬到貢獻者自己的模組，刪除 A 時 `DeletePlanner.ModuleBlockersAsync` 會對它加 `Rollup` 阻擋，放寬到別的應用程式則成了跨應用程式依賴。

裁決原文列的是 text、integer、decimal、date、enum、boolean、user、參照。上面把它對應成實際的型別列舉（`FieldType`）時，文字、數字、日期三類各含同族型別：理由是遷移要用的 MES `website` 是 `url`、進銷存 `cost` 是 `currency`，照字面清單會擋掉現有欄位（§17 問題 6 已裁決採同族解讀，主控裁決，待使用者確認）。

**主檔表單的區塊順序固定**（第二輪建議 5，主控裁決，待使用者確認）：主檔的基本區塊在前，貢獻區塊在後、依貢獻者的 `manifest.id` 字典序排列，與開啟順序無關。實作上是每次新增或移除貢獻區塊時，在同一個交易內重排該模組所有區塊的 `Sequence`。

貢獻欄位的可見範圍不必宣告：一律預設**只有貢獻者自己映射到的群組與系統管理員看得到**，其他群組要管理員另外授予（§6）。貢獻欄位的選項代號與欄位代號同樣以貢獻者為命名空間（§5.4）。

### 4.4 符號：引用主檔欄位

沿用 s2c 的夥伴符號寫法，以主檔 id 開頭：模組 `partner/partner`、欄位 `partner/partner.name`、選項 `partner/partner.customer`。「主檔 id」和「模組代號」恰好同名，是因為三份內建主檔都只有一個模組；符號第一段永遠是 `manifest.id`。

選項符號**和現有契約一致**：s2c 的選項對照名稱是 `<模組代號>.<選項代號>`（`Dynamic_AppOption.Name`，例如 `inventory/move.out`），選項代號在模組內唯一、不含欄位代號。這裡不另發明「模組.欄位.選項」三段式；需要指明欄位的地方（`refGuard` 的 `field`）本來就有 `field` 屬性，`in` 只列選項代號。

使用者套件可以在下列位置引用主檔符號（因為 `requires.masters` 保證主檔存在，不需要像整合那樣處理「夥伴不在」）：

| 位置 | 例子 |
|---|---|
| 參照欄位的目標 | `"refTarget": { "kind": "module", "key": "partner", "app": "partner" }` |
| 參照守衛 `refGuard` | `{ "field": "partner/partner.role", "in": ["customer", "both"] }`（只能選角色為客戶或兩者的往來對象） |
| 帶值 `fill.map` | `{ "from": "partner/partner.phone", "to": "customerPhone" }` |
| 轉換 `header.copy`、目標模組 | 見 §4.5 |
| 報表、事件、檢查 | 照 s2c 的夥伴符號規則；`with` 不必列主檔 |
| 整合（`integrations[]`）欄位的參照目標 | `stockOut` 的 `stockItem` → `item/item`；`with` 不必列主檔（§10） |

**一律不得引用的：** 別的應用程式貢獻的欄位。主檔對外的契約只有**基本欄位**（§5）；貢獻欄位屬於貢獻者，只在貢獻者自己的套件符號空間內可用（也是 `partner/partner.industry` 這種寫法，但驗證時要求它出現在**本套件**的 `contributions[]`）。理由：貢獻者可能關閉或刪除，別人不能依賴它。兩個應用程式需要同一個欄位時，這個欄位應該升為基本欄位（§5.4 的判準）。

### 4.5 轉換的目標是主檔模組

CRM 的「潛在客戶轉客戶」現在建立 `crm-customer`。改成建立 `partner/partner`，追溯欄位 `fromLead` 是 CRM 貢獻的欄位。這需要轉換定義允許 `target` 是主檔模組、`sourceField` 是貢獻欄位。屬於實作前要驗證的假設（§15.1 #4），備案見該處。

**新記錄要有角色。** 轉換只複製 `company`、`phone`、`email`，新的往來對象 `role` 會是空的，立刻被銷售單據的 `refGuard`（客戶或兩者）排除，等於轉了卻選不到。所以轉換要把 `role` 設成 `customer`：轉換標頭新增固定值 `header.set`（`[{ "to": "partner/partner.role", "option": "customer" }]`，只收選項欄位與選項代號），套件格式 6 一起加入。現行轉換只有 `copy`、沒有固定值，而且帶值與轉換不能複製下拉（進銷存設計 §3.3），所以這是新能力，標「實作前驗證」（§15.1 #15）。備案：基本欄位 `role` 設預設值 `customer`，轉換建立記錄時套用預設值（`RecordValueProcessor` 新增記錄時本來就套預設值）；代價是從其他入口新建的往來對象也預設為客戶，採購端要記得改。

**執行（s2d-1e 起）。** 開啟流程建立轉換時帶入固定值，轉換時把它寫進目標表頭（只收選項欄位與選項代號）。固定值跟追溯欄位一樣由系統寫入，不受使用者對該欄位的唯讀、隱藏權限限制；值是設計者固定的，使用者不能改。安全刪除把固定值欄位算成轉換的引用。

**匯出時不得略過。** 現行 `AppExporter.ExportConversionsAsync` 要求來源與目標模組都是本應用程式的成員，否則當成「不屬於這個應用程式」靜默略過（`AppExporter.cs` 約 294 行）。改成：目標（或明細目標）是本應用程式 `requires.masters` 裡主檔的模組時照樣匯出，目標寫主檔符號 `partner/partner`，`header.copy`、`header.set`、`sourceField` 裡的主檔欄位與本套件貢獻欄位用主檔符號表示；匯入開啟時經合併的 `TemplateIdMap` 解析回主檔（§10）。來源仍必須是本應用程式的成員。

### 4.6 驗證（沿用四層，新增這些）

- **結構層：** 格式 6 才收；`kind` 列舉；主檔套件的限制（§4.1）；`requires.masters` 不重複、至多 5 個；同一個 id 不得同時在 `requires.apps` 與 `requires.masters`；貢獻欄位不得必填、型別只收 §4.3 的允許清單（不含 `rollup`）；貢獻參照欄位的 `onDelete` 不得是 `cascade`；貢獻的 `permissions[]`、`fieldAccess[]` 只能引用本套件的群組與欄位；轉換 `header.set` 只收選項欄位與選項代號；欄位的 `active` 只出現在主檔套件的模組與 `contributions[].fields[]`。
- **引用層：** `contributions[].master/module` 在 `requires.masters` 且 `extensible`；主檔符號的第一段在 `requires.masters`；貢獻欄位代號、貢獻選項代號在**同一個貢獻內**不重複（不同應用程式的貢獻互不干涉，見 §5.4 撞名）；貢獻的參照欄位 `refTarget` 只能是本套件的模組或主檔模組，指向本套件模組的必須是 `onDelete: setNull`；貢獻的公式只能引用主檔基本欄位與本貢獻的欄位；轉換的目標可以是 `requires.masters` 裡的主檔模組；整合欄位的 `refTarget.app` 可以是 `with` 夥伴或 `requires.masters` 主檔（§10）；`requires.apps` 的 id 不得是主檔（對照目錄與已匯入套件的 `kind`）。
- **目錄層：** `AppCatalog` 啟動時，內建應用程式套件對內建主檔套件逐欄位對帳（引用的基本欄位與選項都要存在；貢獻欄位與貢獻選項代號不得和基本代號相同），對不上拒絕啟動，同 s2c §3.3。
- **開啟時：** 匯入的套件引用的主檔若被客戶改過（基本欄位被刪或改型別），欄位層級解析失敗，整批 409，原因 `unbound`，列出缺的符號。

## 5. 三份內建主檔

### 5.1 往來對象 `partner`（一個模組）

一筆往來對象是一家公司或一個個人客戶。客戶與供應商不分兩個模組，用角色欄位區分。

| 欄位 | 型別 | 說明 |
|---|---|---|
| `no` | 自動編號 `BP-{0000}` | |
| `name` | 文字，必填，標題欄位 | |
| `role` | 下拉：客戶 `customer`／供應商 `supplier`／兩者 `both` | 角色，見下 |
| `taxId` | 文字 | 統一編號 |
| `phone`、`email`、`address` | 電話、Email、文字 | 一筆只有一個公司地址 |
| `owner` | 使用者 | 負責人（業務或採購窗口） |
| `salesPaymentTerms` | 文字 | 銷售付款條件（這家客戶付我們的條件） |
| `purchasePaymentTerms` | 文字 | 採購付款條件（我們付這家供應商的條件） |
| `active` | 布林，預設是 | 封存用，不刪除 |

付款條件分兩欄，是配合 ERP 資料模型審查：同一家公司既是客戶又是供應商時，兩個方向的條件通常不同（SAP 也分在客戶與供應商的公司代碼資料）。

**角色怎麼來：** 明確存在 `role` 欄位（SAP 做法），不從單據反推（Odoo 的 `customer_rank` 是過帳後累計的）。用下拉而不是複選或兩個布林，是因為現有的 `refGuard` 只支援下拉，不需要新核心能力（同整體設計草稿 §9 第 6 題）。使用者套件的參照欄位用 `refGuard` 限制可選範圍：銷售單據 `in: ["customer", "both"]`，採購單據 `in: ["supplier", "both"]`。代價：之後要加角色（外包商）得改選項與各處 `refGuard`。「用過自動補角色」可以日後用事件 `update` 補，本設計不做。

**共用檢視（不是新模組）：** 「客戶」（`role` 為客戶或兩者）、「供應商」。

**送貨地址：** 不做子地址（草稿 §9 第 7 題）。進銷存貢獻一個 `shipAddress` 文字欄位（派工範例的「進銷存加出貨地址」），單據可從它帶值後再改。

**群組：** `steward`（往來對象管理員）：全權限，含刪除。

### 5.2 聯絡人 `contact`（一個模組，主檔套件 `contact`）

獨立的共用主檔模組，`requires.masters` 往來對象。照整體設計草稿 §2.3 與 §9 第 5 題：不採 Odoo「聯絡人也是往來對象」的同表做法，因為公司與個人欄位不同，同表會讓每個貢獻欄位都出現在個人列、又不能用規則隱藏（貢獻不收規則）。「掛在公司底下」仍成立：`partner` 參照往來對象，公司頁面的反向關聯分頁自動列出底下的聯絡人（核心既有能力）。

| 欄位 | 型別 | 說明 |
|---|---|---|
| `name` | 文字，必填，標題欄位 | 姓名 |
| `partner` | 參照 `partner/partner`，必填 | 目前公司 |
| `title` | 文字 | 目前職稱 |
| `phone`、`email` | 電話、Email | |

**任職經歷**（使用者裁決 2026-11-09）：聯絡人會離職換到別家公司，要看得到他待過哪幾家。做法是保留上面的「目前公司」，再加一個明細模組 `employment`（任職經歷，主模組是 `contact`）：

| 欄位 | 型別 | 說明 |
|---|---|---|
| `partner` | 參照 `partner/partner`，必填 | 公司 |
| `title` | 文字 | 職稱 |
| `startDate`、`endDate` | 日期 | 到職日、離職日；離職日空白表示仍在職 |

- 聯絡人頁的明細列出任職經歷；公司頁的反向關聯分頁同時列出「目前的聯絡人」（`contact.partner`）與「待過的人」（`employment.partner`），兩者都是核心既有能力。
- 「目前公司」與明細由使用者自己維護，系統不自動同步：換公司時改目前公司，並在明細補一列。v1 不做一致性檢查。之後若要做，再另行設計。
- 舊單據不受影響：訂單等單據本來就分開記往來對象與聯絡人。

群組：`steward`（聯絡人管理員）。進銷存客戶與供應商原本的 `contact` 文字欄位移除，改在聯絡人維護。

### 5.3 品項 `item`（一個模組）

| 欄位 | 型別 | 說明 |
|---|---|---|
| `no` | 文字，必填，可篩選 | 品號。用文字而不是自動編號：模具廠的品號常是客戶料號，不能由系統產生（草稿 §2.4）。取代 CRM `sku`、MES `itemNo`、進銷存自動編號 `no` |
| `name` | 文字，必填，標題欄位 | |
| `unit` | 文字 | 維持文字，因為帶值與轉換不能複製下拉（進銷存設計 §3.3） |
| `price` | 金額 | **參考售價**（Odoo `list_price`） |
| `supplyType` | 下拉，必填：自製 `make`／外購 `buy` | 供應方式，見下 |
| `active` | 布林，預設是 | |

**供應方式 `supplyType`**（第二輪準備度 4，主控裁決，待使用者確認）：對應 Odoo 的路線 Manufacture／Buy、SAP 的採購類型 E／F。

- 必填、沒有預設值：CRM、MES、進銷存任何一方新建品項時都要明確選。
- Demo 種子逐筆指定（§11.5）：被 MES 工令參照的品項設 `make`，其餘設 `buy`。舊資料庫不遷移（§11），所以這條規則只用在 Demo 與測試資料。
- 行為：只有 `make` 的品項會在訂單確認時建立工令。這個串接屬於 s2e（§14.1）；`supplyType` 是空值的品項（例如客戶在設計器把它改成選填後建立的）不建工令並提示原因，fail closed。
- s2d 不在 MES 工令或進銷存採購的品項參照上加 `supplyType` 的 `refGuard`（我的判斷）：裁決只規定建工令的條件；加守衛會讓「外購件也要開工令加工」這類例外選不到品項，等 s2e 有實際流程再決定。

**進價另外放：** 進銷存貢獻 `cost`（標準成本／採購參考價，Odoo `standard_price`），採購明細改從 `cost` 帶值，出貨明細與 CRM／MES 報價從 `price` 帶值。現況進價與售價共用 `price` 的混用到此為止（草稿沒有處理這點，§19）。

**群組：** `steward`（品項管理員）全權限。

三份主檔的 `steward` 群組在自動開啟時與一般群組一樣讓管理員決定對應（對應既有群組、新建、略過），預設建議新建；略過時只有系統管理員能維護主檔。

### 5.4 基本欄位與貢獻欄位的判準，以及撞名

**判準：** 屬於「這個對象本身」、至少兩個不相干的應用程式用得到（鐵則 2）、或是法規／身分類的穩定屬性（統編）的，放基本欄位。只有一個應用程式用的，由它貢獻。

- 例：MES 客戶的「付款條件」與進銷存供應商的「供應條件」都是付款條件，且會計一定用，所以放基本欄位，並依方向分成 `salesPaymentTerms`（MES 客戶的付款條件併入）與 `purchasePaymentTerms`（進銷存供應商的供應條件併入）；CRM 的「負責業務」與 MES 的「客戶業務窗口」語意相同，合成 `owner`；統編進基本欄位（Odoo `vat`、SAP 的稅號都在一般資料），這與派工範例「會計加統編」不同，理由是報價單與出貨單也要印統編，且基本欄位開啟時才能放（見 §17 問題 2）。
- 基本欄位以內建主檔套件為準；開啟後客戶可以自己改（和任何模組一樣）。因為沒有套件更新入口，**基本欄位要一次想完整**，後續新增只對新開啟的資料庫生效。

**欄位代號撞名：** 貢獻欄位的 `Field.Key` 一律由資料庫配發（`f_<id>`），代號只存在於套件符號空間。對照表的鍵含貢獻者 id（§8），所以兩個應用程式各貢獻一個叫 `phone` 的欄位不會衝突，彼此看不見對方的代號。規則：

1. 貢獻欄位代號不得與主檔**基本欄位**代號相同（驗證時擋下，開啟前就知道）。
2. 不同應用程式的貢獻互相不比對代號。
3. 標籤與主檔既有欄位（含別的貢獻者）相同時，預覽顯示警告（不擋），提醒這可能是該升為基本欄位的候選。

**選項代號撞名，照欄位代號處理。** 選項對照名稱是 `<模組代號>.<選項代號>`，在模組內唯一（§4.4），所以基本欄位 `role` 的 `customer` 與某貢獻者下拉 `customerType` 的 `customer`，在合併的 `TemplateIdMap` 裡都會是 `partner/partner.customer`。規則與欄位相同：

1. 貢獻選項代號不得與該主檔模組任何**基本選項**代號相同（驗證與目錄對帳時擋下；匯入的主檔若客戶改過、在開啟時才發現，整批 409 `unbound`，列出撞到的代號，不覆寫、不改名）。
2. 貢獻選項存在貢獻者名下的 `Dynamic_AppOption`（名稱 `partner/partner.<選項代號>`），不同貢獻者互不比對；合併對照只併入「主檔基本＋本套件自己的貢獻」，看不到別人的貢獻，所以不會撞。
3. 匯出時代號走同一份避撞對照：沿用 s2c 的 `ExportCodeBook`（有對照列用保存的代號，沒有的退回 `o_<id>`，遇到已佔用的代號加 `_2`、`_3`）；匯出貢獻時，已佔用集合要**先放入該主檔模組的基本選項代號**，避免退回的代號和基本選項相同。欄位與區塊的避撞做法相同。

**設計器在主檔模組上新增的欄位、選項、區塊，歸屬照所在區塊決定**（第二輪必修 6，主控裁決，待使用者確認）：

1. 加在某個貢獻者區塊裡的，歸該貢獻者：寫入貢獻者名下的對照列（名稱含 `/`），標 `VisibilityRestricted` 並替貢獻者映射到的群組寫可見列，受生命週期鎖管制（§6、§7），隨貢獻者匯出、刪除。
2. 加在主檔基本區塊（含客戶在設計器新建的區塊）的，歸主檔：寫入主檔名下的對照列，是基本欄位。
3. 歸屬在建立時決定，之後不隨拖曳改變：設計器把欄位跨歸屬移到別的區塊時回 409（我的判斷），避免同一個欄位在兩次匯出之間換主人。
4. **每個欄位、選項、區塊建立時就寫對照列，代號是持久的：** 設計器新增時由系統產生代號 `u` 加 8 碼小寫英數亂碼（例如 `u7k2m9qxa`，我的判斷），與資料庫 Id 無關，跨庫不會因 Id 相同而誤認；匯出與合併匯入都以對照列的代號為準，不再退回 `f_<id>`、`o_<id>`、`b_<id>`。萬一代號仍撞到，走 `ExportCodeBook` 避撞。這讓 A→B→A 往返與「自己匯出再匯入自己」都不會產生重複（§9）。
5. 現有資料庫裡沒有對照列的主檔欄位不存在（主檔是新的），所以不需要補寫舊資料。
6. **孤兒區塊不得新增或拖入**（第三輪必修 7，主控裁決，待使用者確認）：已刪除應用程式留下的區塊（§7 刪除第 7 點）沒有擁有者。設計器在孤兒區塊裡新增欄位、選項，或把欄位拖進孤兒區塊，一律回 409「這個區塊屬於已刪除的應用程式」，並提示改加在主檔的基本區塊。孤兒欄位拖出孤兒區塊比照跨歸屬，也回 409。理由：歸主檔的話，主檔匯出會帶出這個欄位，但它的區塊不匯出，欄位沒有區塊可放；該應用程式重開接回區塊後，又會出現「主檔的欄位放在貢獻者區塊裡」。

## 6. 權限

主檔模組被多個應用程式授權，權限必須能回答三件事：同一群組被兩個應用程式授權時聽誰的、匯出某個應用程式時哪些權限是它的、關閉或刪除它時收回哪些。所以權限改成「**來源記錄 → 重算有效值**」。

- **權限來源記錄：** 新表 `Dynamic_AppGrant`（§8）記「哪個來源、對哪個主檔模組、替哪個群組（`GroupId`）、授予哪些動作、每個動作的資料範圍」。**不另存群組代號**：匯出時群組代號和模組權限走同一個 `GroupCode()`，以 `GroupId` 為準，同一個群組在一份匯出檔只有一個代號（§9；第二輪必修 2，主控裁決，待使用者確認）。來源有三種（`SourceKind`）：貢獻者 `app`（`contributions[].permissions[]`）、主檔自己 `master`（`steward` 群組，自動開啟主檔時授予）、管理員手動 `manual`（見下）。
- **有效權限一律取聯集，範圍逐動作取：** 主檔模組上每個群組的權限列（現有的群組權限列與資料範圍列，`PermissionResolver` 照舊讀它們）由來源記錄重算：**動作取聯集；資料範圍依每一種動作分別取各來源中最寬的**（`all` > 同群組 > `own`），不是整組取最寬。例如 CRM 授予 G「讀取／all」、進銷存授予 G「讀取＋編輯／own」，合併後是讀取 all、編輯 own，沒有任何一個來源授予過的「編輯 all」不會出現（第二輪建議 1，主控裁決，待使用者確認）。重算在開啟、關閉、重開、刪除與管理員儲存時於同一個交易內進行，只看來源記錄，所以**結果與開啟順序無關**。兩個應用程式把各自的群組映射到同一個既有群組時不撞鍵：來源記錄以來源區分，權限列只有重算後的一列。現有的 `SaveModulePermissions` 是整個模組覆寫，這裡改由重算函式寫入（§15.1 #5）。
- **逐動作範圍需要核心改動：** 現行 `Dynamic_ModuleDataScope` 每個（模組, 群組）只有一個範圍，`PermissionResolver.Resolve` 從有讀取權的群組取最大範圍、套用到所有動作（`PermissionResolver.cs` 約 26～37 行）。所以新增逐動作範圍表 `Dynamic_ModuleActionScope`（§8），只由重算函式寫入主檔模組；`Resolve` 遇到模組有逐動作範圍列時，每個動作各自取「使用者所屬群組中授予該動作者」的最寬範圍，`EffectivePermissions` 依動作提供範圍與擁有者集合。沒有逐動作範圍列的一般模組維持現行行為（§17 問題 7 已裁決，主控裁決，待使用者確認；避免改變既有模組的有效權限）。能否在所有查詢與寫入路徑依動作套用範圍是實作前驗證（§15.1 #16）。
- **管理員手動修改**（第三輪必修 5，主控裁決，待使用者確認）：管理員在權限頁儲存主檔模組權限時，存的是有效值，系統把差異寫回來源：
  - **差異的基準是存檔當下重算出的有效值**，不是來源記錄的聯集，也不是畫面載入時的值。只有和它不同的部分才寫回：多出的動作或放寬的範圍記成「手動」來源；被拿掉的動作，只從**目前有計入有效值**的來源記錄移除（含應用程式授予的），避免下一次重算又長回來。原封不動存檔時差異為空，不產生任何手動記錄，也不動任何來源記錄。
  - **關閉中的應用程式授予的寫入動作不計入有效值，也就不在差異裡**，所以不會因為畫面上看不到而被當成「管理員拿掉了」刪掉；它重開後照常恢復。
  - **主檔模組的權限頁逐動作設定範圍**：`SavePermissionsBody` 的群組列在主檔模組上帶逐動作的範圍（每個動作一個 `scope`），`GET` 也逐動作回傳；畫面在主檔模組上每個動作各一個範圍選單。一般模組維持每群組一個範圍。主檔模組上的 `Dynamic_ModuleDataScope` 由重算寫入**讀取動作的範圍**，只供沿用舊欄位的地方參考，實際判斷看 `Dynamic_ModuleActionScope`。這樣就不會發生「CRM 給讀取 all、進銷存給讀取＋編輯 own，管理員只改了一個欄位的 Hidden 就存出一筆手動的編輯 all」。
  - 收回權限永遠以管理員為準。
- **資料範圍：** 貢獻者群組的 `scope` 照其宣告（`all`／`own`／同群組），記在該來源記錄授予的每個動作上；同群組多個來源時逐動作取較寬（見上）。
- **欄位層，預設 fail closed：** 貢獻欄位預設**只對貢獻者自己映射到的群組**（該應用程式在 `Dynamic_AppGrant` 有記錄的群組）**與系統管理員可見**；其他群組即使對主檔模組有 Read 也看不到，要管理員另外授予。做法：貢獻欄位建立時在欄位上標 `VisibilityRestricted`，可見群組存在新表 `Dynamic_FieldGrant`（§8）：開啟時替貢獻者映射到的每個群組寫一列（來源＝貢獻者），管理員另外授予的來源記為手動。限制旗標獨立於授予列，所以授予列被刪光時是「只剩系統管理員看得到」，不會變成所有人可見。後來才建立的群組取得主檔 Read，看到的也只有基本欄位。主檔的基本欄位照一般權限。實作上在 `PermissionResolver.Resolve` 算 `hidden` 時，把「有可見範圍限制、而使用者的群組都不在範圍內」的欄位加進 `hidden`，之後的列表、篩選、報表、匯出、列印、公式遮罩都沿用既有的 `hidden` 路徑（§15.1 #12）。不需要跨套件引用群組：範圍由開啟時的群組映射決定，不寫進別人的套件。貢獻者自己的 `fieldAccess[]` 仍可在自己的群組之間再收緊；金額型別的 `pricing` 遮罩照舊。設計器加在貢獻者區塊裡的欄位同樣受限（§5.4）。
- **管理員另外授予可見的入口**（第二輪準備度 3，主控裁決，待使用者確認）：不另做新頁，掛在既有的欄位存取設定之下。
  - API：既有的 `PUT /api/v3/dynamic/modules/{key}/permissions`，body（`SavePermissionsBody`）新增 `fieldGrants`：每列是（貢獻欄位 Id, 群組 Id 清單），只收 `VisibilityRestricted` 的欄位，存成 `Dynamic_FieldGrant` 的手動來源。`GET` 同一路徑回傳每個受限欄位的可見群組與來源，讓畫面分得出哪些是貢獻者給的、哪些是手動加的。
  - 畫面：群組權限頁既有的欄位存取區，在主檔模組上多列出貢獻欄位，每個群組一個「可見」勾選；貢獻者來源的列顯示來源、不可取消（要收回請用既有的 Hidden）。
  - 和既有 `fieldAccess`（Hidden／ReadOnly，存在 `Dynamic_FieldPermission`）並存時，**收緊優先**（我的判斷）：可見授予只解除「限定可見」這一層，群組若對該欄位設了 Hidden 仍看不到，設了 ReadOnly 仍唯讀。這延續總原則 fail closed。
- **關閉只收回自己的寫入動作：** 關閉 A 時，A 的來源記錄保留、但重算時只計入讀取類動作（讀取、列印匯出、價格），新增、編輯、刪除、核准不計；別的來源授予的照算。這和關閉的既有定位一致（關閉＝唯讀保留）。重開 A 時重算恢復。貢獻欄位的可見範圍不因關閉改變（仍可讀），寫入另由 §7 擋下。
- **刪除只收回自己的全部：** 刪除 A 時移除 A 的全部來源記錄（`Dynamic_AppGrant` 與 `Dynamic_FieldGrant` 中來源為 A 的列）後重算；別的應用程式、主檔、手動授予的照算。不再是「刪除不收回權限列」：有了來源記錄，就知道哪些是 A 的。A 留下的停用欄位（§7）保留 `VisibilityRestricted`，管理員在設計器重新啟用時只有系統管理員與手動授予的群組看得到；若是 A 重新開啟而接回（§7 刪除第 6 點），重新替 A 的群組寫可見列。
- **主檔的設計器修改（含基本欄位與貢獻欄位）要設計器權限加系統管理員**（整體設計草稿 §6.1 第 5 點）：主檔被多個應用程式依賴，不能讓只有設計器權限的人改。這比一般模組嚴格，客戶仍然能改，只是限管理員。
- 主檔套件本身沒有跨應用程式的操作權限需求：所有操作仍走一般的模組權限，沒有新的系統身分寫入路徑。

## 7. 生命週期

**不變式（以使用者閉包定義）：** 主檔 M `Open` ⇔ 至少一個 `Open` 的**應用程式**（`Kind = app`）直接使用 M，或經其他主檔間接使用 M（例如只列 `contact` 的套件，經 `contact` 間接使用 `partner`）。主檔本身不算使用者。完整性檢查函式驗這個不變式（供測試與啟動時 log 用）。

**主檔狀態只能推得**（第二輪必修 7，主控裁決，待使用者確認）：

1. API 拒絕直接開啟、關閉、重開主檔，一律 409（§4.1）。
2. 每次應用程式開啟、關閉、重開、刪除之後，於**同一個交易內**依主檔相依順序重算所有主檔的狀態：關閉方向先處理依賴者、再處理被依賴者（先 `contact`、再 `partner`），開啟方向相反（先 `partner`、再 `contact`）。每個主檔用上面的閉包定義判斷，不看「此刻別的主檔是不是 `Open`」，所以結果與判斷順序無關。
3. 反例檢查：只開進銷存（自動開 `partner`、`contact`、`item`）再關閉進銷存 → 三份主檔都 `Closed`；只列 `contact` 的套件開啟 → `partner`、`contact` 都 `Open`，關閉後都 `Closed`。

「使用」記在 `Dynamic_AppMasterUse`（§8）：開啟時寫入，刪除時移除，關閉不動。主檔對主檔的相依也記在這張表（使用者是主檔），閉包沿這些邊往上找到應用程式。

| 操作 | 主檔 | 貢獻欄位 |
|---|---|---|
| 開啟 A，主檔不存在 | 在同一個交易內自動開啟（`Kind = master`），綁定 A | 建立區塊與欄位，寫入權限來源記錄並重算（§6） |
| 開啟 A，主檔 `Open` | 不動，綁定 A | 同上 |
| 開啟 A，主檔 `Closed` | 先重新開啟主檔，再綁定 | 同上 |
| 關閉 A，另有 `Open` 的使用者 | 維持 `Open`，主檔不消失 | A 貢獻的欄位**凍結**（寫入管線最底層，見下），表單顯示為唯讀並提示原因，A 的區塊預設收起；仍可讀、可出現在報表；A 授予的寫入動作收回（§6） |
| 關閉 A，A 是最後一個 `Open` 的使用者 | 一起 `Closed`（唯讀保留，選單移到「已關閉的應用程式」） | 同上，整個模組唯讀 |
| 重新開啟 A | 主檔若 `Closed` 先重開 | 欄位恢復可寫，權限重算恢復 |
| 刪除 A（必須先 `Closed`） | 不刪、不關 | 見下；A 的權限來源記錄全部移除後重算 |
| 刪除 A，A 是最後一個使用者 | 保留，維持 `Closed`，回應 `warnings` 提醒「共用主檔 X 仍有 N 筆資料，已保留」 | 同上 |
| 之後有應用程式再開啟 | 重新採用保留的主檔：重開、綁定、加貢獻 | 新的貢獻照常；同一個應用程式重開時接回它留下的停用欄位（刪除第 6 點），其他孤兒欄位仍在 |
| 手動刪除保留的主檔 | `DELETE /api/v3/dynamic/apps/{主檔 key}`：套用模組安全刪除的全部條件，加上「沒有任何 `Dynamic_AppMasterUse`」 | |

**主檔不能被直接開啟、關閉、重開：** 對 `Kind = master` 的應用程式呼叫 `open`／`close`／`reopen` 回 409「由使用它的應用程式管理」。刪除只在沒有使用者時允許。

**為什麼最後一個使用者刪除時不自動刪主檔：** 主檔可能有客戶累積的資料與自己調整過的定義，自動刪除是不可逆；保留一份 `Closed` 的主檔只多一個選單項目（在「已關閉的應用程式」分組）。代價：空系統的目標被放寬成「沒有任何 `Open` 的東西」，需要完全清空要手動刪主檔（§17 問題 5 的子項）。

**貢獻者不是 `Open` 時凍結（寫入管線最底層，所有寫入者）**（第一輪必修 1、第二輪必修 5，主控裁決，待使用者確認）：「唯讀」不能只靠前端、一般的權限唯讀或 `RecordValueProcessor` 的輸入判斷。現行 RVP 對無權寫入的欄位是「忽略輸入、保留舊值」，`systemKeys`（事件、轉換等系統寫入）可以繞過一般的可寫判斷；而且自動編號由 handler 在 RVP 之後另外取號（`RecordAutoNumbers.cs` 檔頭註解）、公式每次存檔由 `ApplyFormulas` 重算、彙總由彙總重算寫入，這些值都不是「輸入」。所以：

1. **鎖放在寫入管線最底層：** 記錄值組裝完成、寫回 `Dynamic_Record` 之前的共同落地點（RVP、`RecordAutoNumbers`、公式與彙總計算都做完之後），加上批次重算 `FormulaRecalc`、`RollupRecalc` 的寫回點。欄位的貢獻者（以 `FieldId` 反查含 `/` 的對照列，§8）不是 `Open` 時，這個欄位**凍結**：落地時一律以舊值覆蓋上游算出的任何值。系統管理員也一樣，這不是權限而是狀態。「最底層」指各寫入路徑寫回記錄值的共同函式，不是 `SaveChanges` 攔截器這類資料層攔截，否則刪除計畫內的 `FieldPurge` 清除也會被擋（§15.1 實作注意）。
2. **衍生值保持最後的值、不重算：** 貢獻的公式在凍結期間不重算，即使它引用的主檔基本欄位被改了；重開後下一次存檔或批次重算才更新。自動編號與彙總本來就不允許貢獻（§4.3）。新增記錄時凍結欄位不套預設值。
3. **明確輸入不同的值時 409：** 使用者或 API 對凍結欄位**給了不同於舊值的值**時，不是靜默忽略，而是整筆 409「欄位屬於已關閉的應用程式『○○』」；值沒變或沒給則照常通過，其他欄位照常寫。這一道在 RVP 判斷，和狀態機唯讀（`IsWorkflowLocked`）同層、優先於 `systemKeys`；最底層的落地點是第二道。
4. **系統寫入整個失敗：** 轉換（`ConvertRecord`，含 `header.set`）、事件（`EffectWriter`／`EffectApplier`）、整合事件要寫凍結欄位時，整個動作失敗（fail closed，同 s2c 整合事件的第二道），不當成條件不成立而略過。
5. 涵蓋的路徑：一般新增與修改（`CreateRecord`、`UpdateRecord`、明細 `DetailWriter`）、匯入（`ImportRecords`）、轉換、事件、整合事件、自動編號、公式重算、彙總重算，以及**刪除記錄時的清空參照**（`DeleteRecordHandler` 依 `ReferenceDeletePlanner` 的 `ToClear` 直接改寫來源記錄的 `ValuesJson`；第三輪必修 1，主控裁決，待使用者確認）。清空也算寫入：要清空的來源欄位是凍結的貢獻欄位時，整筆刪除回 409「欄位屬於已關閉的應用程式『○○』」，和 `TouchesLockedRecordAsync` 擋狀態機唯讀同一層。例如 CRM 貢獻「主要聯絡人 → `contact`」（`setNull`），CRM 關閉期間進銷存刪那位聯絡人 → 409。是否全部經過共同落地點是**實作前驗證**（§15.1 #7）：每條路徑各寫一個「貢獻者關閉後嘗試寫入 → 409 或值不變」的失敗測試。
6. **沒有「關閉時轉停用」備案**（第二輪建議 3，主控裁決，待使用者確認）：沒經過共同落地點的路徑各自補同一個檢查；仍有擋不住的，停下回報主控（§15.1 #7），不得自行採用任何會讓欄位可寫或看不到的做法。

**前端顯示凍結**（第二輪建議 4，主控裁決，待使用者確認）：記錄與 schema 讀取時，把凍結欄位加進 `EffectivePermissions.ReadOnlyFieldIds`（只影響顯示），並回傳每個凍結欄位的原因（貢獻者 id 與名稱，§8），表單把它顯示為唯讀並提示「○○應用程式已關閉」，貢獻者區塊預設收起。伺服器端的兩道鎖仍是真正的關卡。

**刪除 A 時 A 貢獻的欄位怎麼辦（有資料時能不能移除欄位）：**

1. 先盤點引用。沿用安全刪除的規則：貢獻欄位若被 A 以外的東西引用（客戶自己建的報表、檢視、規則、其他模組的欄位），整個刪除 409，列出來源。A 自己的報表、檢視等隨 A 一起刪，不算。設計器加在 A 區塊裡的欄位算 A 的貢獻（§5.4），規則相同。
2. **A 擁有的轉換、事件隨 A 一起刪，即使目標是主檔**（第二輪必修 3，主控裁決，待使用者確認）：轉換和事件歸屬於擁有它的應用程式（來源模組是 A 的成員），不屬於目標。現行 `DeletePlanner` 只有在來源、目標、明細模組全部在刪除集合裡時才把轉換當成集合內（`InsideSet`，`DeletePlanner.cs` 約 110、144～153 行），事件也是 `Inside(refs.ModuleKeys)` 全部在集合內才算（約 121～126 行）。改成：擁有者在刪除集合裡、目標或其他引用的模組是 A 的 `requires.masters` 主檔模組時，視為集合內、不構成阻擋。這些主檔模組只是在判斷上視為集合內，不得因此被 `ModuleRemover` 移除。檢查（`Dynamic_Check`）若同樣以主檔為引用，比照處理（我的判斷）。
3. 沒有值的貢獻欄位：直接刪除，區塊若空了也刪。
4. **參照 A 自己模組的貢獻欄位（例如 `fromLead` 指向 CRM 的 `lead`）：連同欄位與殘留值一起清除**（第一輪必修 5、第二輪必修 4、第三輪必修 1、2，主控裁決，待使用者確認）。它指向的模組會跟著 A 刪掉，保留下來只會是指向不存在模組的參照；而現行 `DeletePlanner.ModuleBlockersAsync` 會把任何模組裡參照要刪模組的欄位（含 `Inactive`）列為阻擋（`DeletePlanner.cs` 約 99 行），保留它會讓 A 永遠刪不掉。這類欄位一律 `setNull`（§4.3），所以刪除 A 之前軟刪潛在客戶時，`fromLead` 的值通常已經被清空；清除集合負責把**欄位本身**移除，並兜底清掉殘留值（例如軟刪潛在客戶時那筆往來對象已經軟刪，`ListByRefAsync` 不會清到它）。機制：
   - 刪除計畫把這些欄位列入**清除集合**。**清除集合與隨 A 刪除的欄位，豁免 `ModuleBlockersAsync` 對它們產生的全部阻擋類型**（例如 `ReferenceField`、`FieldActive`、`FieldData`、`Formula`、`Rollup`、`ParentDefault`、`Fill`、`RefGuard`，以實作當下的列舉為準，不逐項維護），也不套自身轉換的阻擋（`AddConversionBlockers`，見第 2 點）。例如 `fromLead` 若設了 `fill.map` 從 `lead` 帶值，`Fill` 阻擋同樣豁免。
   - 清除以系統身分執行，由既有的 `FieldPurge` 清值、索引與歷史。`ClosedDataGuard.RejectPurge` 對「刪除計畫內的清除」放行，主檔是 `Closed`（A 是最後一個使用者）時也一樣；理由是欄位指向的模組在同一個交易內消失，不算改寫唯讀資料。設計器的一般刪欄位仍照 `RejectPurge` 擋。
   - **確認數：** 刪除確認的 `Purged` 改為「軟刪除記錄筆數＋被清除的欄位值筆數」，預覽分開列出兩個數字（「將清除往來對象上的『來源潛在客戶』，N 筆有值」）；送出的 `Purged` 與重算的總數不符就 409（`DeleteApp.cs` 約 82 行的同一套協定），預覽之後新增的值不會沒經確認就被清掉。
   - 這是「有值停用保留」的唯一例外，標「實作前驗證」（§15.1 #9）。備案是刪除 409 並列出這些欄位；代價是管理員要先重開 A、在設計器安全刪除這些欄位、再關閉 A 才能刪（主檔 `Closed` 時設計器刪欄位會被 `RejectPurge` 擋）。走備案前先回報主控。
5. **其他有值的貢獻欄位：改為停用（`Inactive`），值保留，記成 A 的孤兒。** A 名下的對照列隨 A 刪除，改在 `Dynamic_AppOrphan`（§8）記下「前擁有者 A 的 id＋種類（欄位、選項、區塊）＋原代號」。孤兒列要在 A 的對照列被 cascade 刪掉**之前**建好（§15.1 實作注意）。停用欄位不顯示在表單，但資料還在，管理員之後可以在設計器重新啟用，或等欄位清空後用安全刪除移除。管理員重新啟用後的可見範圍只有系統管理員與手動授予的群組（§6）。這些欄位**不匯出**：匯出依歸屬決定，孤兒的擁有者已不存在，沒有任何一份套件擁有它（§9）。理由：貢獻的值存在**主檔記錄**上，是主檔的資料，不該因為某個應用程式被刪就一併丟失；也不應該讓「某欄位有值」擋住整個應用程式的刪除。
6. **同一個應用程式重新開啟時接回**（第二輪建議 6，主控裁決，待使用者確認）：開啟 A 時，貢獻欄位若在 `Dynamic_AppOrphan` 有「同一個 A 的 id＋同一種類＋同一個代號」的孤兒，就接回那個欄位：改回 `Active`、重寫 A 名下的對照列與可見列、刪除孤兒列，不另建新欄位，舊值因此回來。選項與區塊照同樣規則接回，套件有、孤兒沒有的選項照常新增。
   - **型別不同時不接回，新欄位沿用原代號**（第三輪必修 4，主控裁決，待使用者確認）：A 名下原代號的對照列已隨 A 刪除、空了出來，所以新欄位直接用套件的原代號寫對照列，**不走避撞改名**，A 的匯出代號因此和套件一致。同時刪除舊的孤兒列：舊欄位只留成停用欄位，失去接回資格，開啟預覽列出警告。這樣（`FormerAppKey`, `Kind`, `Name`）永遠只有一列，A 之後再刪一次也不會撞唯一索引。
7. 區塊同樣：還有欄位在就保留，記成孤兒；空了才刪。孤兒區塊不得再新增或拖入欄位（§5.4 第 6 點）。

客戶在設計器對貢獻欄位的操作照一般規則（編輯、停用、安全刪除），不特別限制；欄位被刪，對照列因外鍵 cascade 消失，匯出時自然少一個欄位。

**相依檢查要排除自己的貢獻欄位：** 現行 `AppDependencyChecker.DependentsOfAsync` 逐一查「誰有參照欄位指向 A 的成員模組」，並把那個欄位所在模組的應用程式算成 A 的依賴者（`AppDependencyChecker.cs` 約 65 行）。CRM 貢獻的 `fromLead` 放在主檔 `partner` 上、指向 CRM 的 `lead`，照現行邏輯主檔會被算成 CRM 的依賴者，關閉 CRM 時要求 cascade，連帶關閉主檔，主檔關閉又讓進銷存失去主檔。改法：比照現行排除整合欄位的做法，把**A 自己貢獻的欄位**（A 名下含 `/` 的對照列）從入向參照中排除；關閉與刪除都排除，因為關閉時它們跟著凍結，刪除時依上面第 4 點一起清除。主檔不得因 A 的貢獻被要求 cascade 關閉。`requires.masters` 不算進「誰依賴 A」：主檔的狀態由不變式驅動、不走使用者的相依檢查流程，關閉 A 時不需要使用者決定主檔，所以不產生新的 `cascade` 提示。

## 8. 資料模型（migration：s2d-0 的 `RemoveMockMold`、s2d-1 的 `SharedMasters`）

只走 EF migration，沒有執行期 DDL。s2d-0 自帶一個只移除 `Mock_Mold` 的 migration `RemoveMockMold`；其餘異動都在 s2d-1 的 `SharedMasters`（第二輪準備度 1，主控裁決，待使用者確認）。

| 異動 | 內容 |
|---|---|
| `Dynamic_App` 新欄位 `Kind` | `app`（預設）或 `master`，列舉 `AppKind` |
| 新表 `Dynamic_AppMasterUse` | `AppId`（使用者，cascade）、`MasterAppId`（FK 到 `Dynamic_App`，restrict）；主鍵 (`AppId`, `MasterAppId`) |
| 貢獻欄位的所有權 | 重用 s2c 的 `Dynamic_AppField`／`Dynamic_AppOption`／`Dynamic_AppBlock`，不加欄位（見下） |
| 新表 `Dynamic_AppGrant` | 權限來源記錄（§6），代理主鍵，一列一個動作 |
| 新表 `Dynamic_ModuleActionScope` | 主檔模組的逐動作資料範圍（§6），重算結果 |
| 新表 `Dynamic_FieldGrant` | 貢獻欄位的可見群組（§6），代理主鍵 |
| 新表 `Dynamic_AppOrphan` | 已刪除貢獻者留下的欄位、選項、區塊（§7 刪除第 5、6 點），唯一鍵（`FormerAppKey`, `Kind`, `Name`） |
| 欄位新增 `VisibilityRestricted` | 布林，預設否 |
| 移除 | s2d-0：`Mock_Mold`；s2d-1：`Mock_Customer`（§11.4） |

**貢獻的所有權：** 貢獻者的 `AppId` 加上名稱 `<主檔 id>/<模組代號>.<欄位代號>`（選項是 `<主檔 id>/<模組代號>.<選項代號>`），含 `/` 的就是貢獻列，外鍵 cascade 照舊。第一輪的「因關閉停用」旗標隨備案取消（§7），不加。設計器在主檔上新增的欄位、選項、區塊也在建立時寫對照列，名稱用持久代號（§5.4）。

**`VisibilityRestricted`：** 貢獻欄位（含設計器加在貢獻者區塊裡的）建立時為是，之後不因貢獻者刪除而清掉。

**EF Core 定義**（第二輪準備度 2，主控裁決，待使用者確認）：四張新表都用代理主鍵或全非空的複合鍵，不用可空的主鍵屬性，也不拿保留值當外鍵；來源用 `SourceKind`（`app`／`master`／`manual`）區分，`AppId` 可空，再用篩選式唯一索引保證不重複（SQLite 與 SQL Server 都支援 `HasFilter`）。群組刪除時四張表的相關列一起 cascade。

```csharp
// Dynamic_AppGrant：一個來源、一個模組、一個群組、一個動作一列
b.ToTable("Dynamic_AppGrant");
b.HasKey(x => x.Id);
b.Property(x => x.SourceKind).HasConversion<string>().HasMaxLength(10);   // app | master | manual
b.Property(x => x.Action).HasConversion<string>().HasMaxLength(20);       // PermissionAction
b.Property(x => x.Scope).HasConversion<string>().HasMaxLength(20);        // DataScope
b.HasIndex(x => new { x.AppId, x.ModuleId, x.GroupId, x.Action }).IsUnique().HasFilter("AppId IS NOT NULL");
b.HasIndex(x => new { x.ModuleId, x.GroupId, x.Action }).IsUnique().HasFilter("AppId IS NULL");  // manual
b.HasOne<App>().WithMany().HasForeignKey(x => x.AppId).OnDelete(DeleteBehavior.Cascade);
b.HasOne<Module>().WithMany().HasForeignKey(x => x.ModuleId).OnDelete(DeleteBehavior.Cascade);
b.HasOne<PermissionGroup>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
b.ToTable(t => t.HasCheckConstraint("CK_AppGrant_Source", "(SourceKind = 'manual') = (AppId IS NULL)"));

// Dynamic_ModuleActionScope：重算結果，鍵全部非空
b.ToTable("Dynamic_ModuleActionScope");
b.HasKey(x => new { x.ModuleId, x.GroupId, x.Action });
b.Property(x => x.Action).HasConversion<string>().HasMaxLength(20);
b.Property(x => x.Scope).HasConversion<string>().HasMaxLength(20);
b.HasOne<Module>().WithMany().HasForeignKey(x => x.ModuleId).OnDelete(DeleteBehavior.Cascade);
b.HasOne<PermissionGroup>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);

// Dynamic_FieldGrant：貢獻欄位對哪個群組可見、來源是誰
b.ToTable("Dynamic_FieldGrant");
b.HasKey(x => x.Id);
b.Property(x => x.SourceKind).HasConversion<string>().HasMaxLength(10);   // app | manual
b.HasIndex(x => new { x.FieldId, x.GroupId, x.AppId }).IsUnique().HasFilter("AppId IS NOT NULL");
b.HasIndex(x => new { x.FieldId, x.GroupId }).IsUnique().HasFilter("AppId IS NULL");            // manual
b.HasOne<Field>().WithMany().HasForeignKey(x => x.FieldId).OnDelete(DeleteBehavior.Cascade);
b.HasOne<PermissionGroup>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
b.HasOne<App>().WithMany().HasForeignKey(x => x.AppId).OnDelete(DeleteBehavior.Cascade);
b.ToTable(t => t.HasCheckConstraint("CK_FieldGrant_Source", "(SourceKind = 'manual') = (AppId IS NULL)"));

// Dynamic_AppOrphan：前擁有者只存 id 字串（應用程式已刪，不能是外鍵）
b.ToTable("Dynamic_AppOrphan");
b.HasKey(x => x.Id);
b.Property(x => x.FormerAppKey).HasMaxLength(40).IsRequired();
b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(10);         // field | option | block
b.Property(x => x.Name).HasMaxLength(100).IsRequired();                  // 原對照名稱
b.HasIndex(x => new { x.FormerAppKey, x.Kind, x.Name }).IsUnique();
b.HasOne<Field>().WithMany().HasForeignKey(x => x.FieldId).OnDelete(DeleteBehavior.Cascade);
b.HasOne<FieldOption>().WithMany().HasForeignKey(x => x.OptionId).OnDelete(DeleteBehavior.Cascade);
b.HasOne<Block>().WithMany().HasForeignKey(x => x.BlockId).OnDelete(DeleteBehavior.Cascade);
b.ToTable(t => t.HasCheckConstraint("CK_AppOrphan_One",
    "(FieldId IS NOT NULL) + (OptionId IS NOT NULL) + (BlockId IS NOT NULL) = 1"));
```

**`Dynamic_AppOrphan` 的 `Kind`**（第三輪必修 4，主控裁決，待使用者確認）：欄位與選項的對照名稱形狀相同（`partner/partner.<代號>`），區塊也記在同一張表，同一個貢獻者有欄位 `vip` 和選項 `vip` 時，只用（`FormerAppKey`, `Name`）當唯一鍵會撞。所以加 `Kind`（`field`／`option`／`block`，須與實際填的那個外鍵一致），唯一索引改成（`FormerAppKey`, `Kind`, `Name`）。配合 §7 刪除第 6 點「型別不同時刪掉舊孤兒列」，同一組鍵永遠只有一列。這個結構要在產生 `SharedMasters` migration **之前**定案。

`Dynamic_AppMasterUse` 維持複合主鍵（`AppId`, `MasterAppId`），兩欄都非空，沒有上述問題。檢查約束的布林運算寫法 SQLite 可用；SQL Server 要改成 `CASE` 表示式，實作時依 provider 產生（我的判斷，不影響結構）。

對照列的來源：主檔自己的基本欄位在主檔那個 `Dynamic_App` 名下（`AppId = 主檔`，名稱無 `/`），使用者套件要引用 `partner/partner.name` 時由這裡解析成實際的欄位 Id，等於 s2c 的夥伴解析（§5.4 步驟 1）；貢獻欄位在貢獻者名下。寫入時的生命週期鎖（§7）用 `FieldId` 反查含 `/` 的對照列取得貢獻者，再看該應用程式是否 `Open`；該模組沒有任何貢獻列時零成本（查詢結果快取於請求）。同一份查詢結果也供 schema 與記錄讀取回傳凍結欄位的唯讀原因（每個凍結欄位回傳 `lockedBy`：貢獻者 id 與名稱），前端據此顯示唯讀與提示（§7）。

`Dynamic_App.RequiresJson` 照常存 `manifest.requires`（含 `masters`），相依判斷與匯出用。

主檔模組上的群組權限列、資料範圍列與逐動作範圍列（`Dynamic_ModuleActionScope`）是 `PermissionResolver` 讀的來源；它們由 `Dynamic_AppGrant` 重算而來（§6），不直接被應用程式開關寫入。主檔模組上的資料範圍列（`Dynamic_ModuleDataScope`）寫讀取動作的範圍（§6）。完整性檢查函式同時驗「權限列＝來源記錄重算的結果」。

## 9. 匯出與匯入

**總則：匯出依歸屬決定，不依啟用狀態**（第二輪建議 3，主控裁決，待使用者確認）。主檔名下的欄位隨主檔匯出，貢獻者名下的隨貢獻者匯出，停用的照樣匯出並標 `"active": false`（§4.1）；只有孤兒（擁有者已刪除，§7 刪除第 5 點）沒有任何一份套件擁有，不匯出。現行 `TemplateExporter` 只輸出 `Active` 欄位（`ActiveFields`），對主檔模組與貢獻要改成依歸屬輸出。

- **匯出應用程式 A：** 輸出 A 自己的模組，`contributions[]` 從資料庫重建：A 名下對照列含 `/` 的欄位、區塊、選項（含設計器加在 A 區塊裡的，§5.4），用對照表保存的代號（沿用 s2c §5.6 的「保留代號」，避撞見 §5.4）。與 s2c 整合只輸出「開啟當時的宣告」不同，貢獻欄位是真實欄位，所以**反映客戶事後的修改**。匯出時主檔的基本欄位與別人的貢獻欄位都不會出現在 A 的檔案裡。
- **主檔歸 `requires.masters`，不進 `requires.apps`**（第二輪必修 1，主控裁決，待使用者確認）：現行 `AppExporter.ExportAsync` 的 `RefTarget`（`AppExporter.cs` 約 39～50 行）把「別的應用程式的模組」都收進 `usedApps`，最後組成 `requires.apps`（約 127～130 行）；主檔也是 `Dynamic_App`，CRM 訂單的 `customer → partner` 會被寫成 `requires.apps: [{ id: "partner" }]`，匯入空庫後先開 CRM 就因 `notInstalled` 開不起來，結果依開啟順序。改成：`RefTarget` 遇到 `Kind = master` 的應用程式時，以主檔符號輸出、收進 `requires.masters`，不收進 `usedApps`。`requires.masters` 由 A 開啟時的宣告與匯出時實際引用到的主檔合併而成。匯入時 `requires.apps` 出現主檔 id 整份拒絕（§4.2）。
- **群組代號只有一個來源**（第二輪必修 2，主控裁決，待使用者確認）：**權限只帶 A 自己授予的部分**，從 `Dynamic_AppGrant` 中來源為 A 的記錄重建 `contributions[].permissions[]`；群組代號和 A 自己模組的權限、貢獻的 `fieldAccess[]` 走同一個 `GroupCode(GroupId)`（`AppExporter.cs` 約 64～76 行），`groups[]` 收齊所有被引用的代號。同一個群組在一份匯出檔只有一個代號。別的應用程式、主檔、手動授予的不出現，即使是同一個群組。
- **轉換的目標是主檔模組時照樣匯出**，以主檔符號表示，匯入時解析回主檔（§4.5）。
- **匯出主檔：** `GET /apps/{主檔 key}/export` 輸出 `kind: master`，含主檔名下的全部基本欄位、選項、區塊（含客戶在設計器新增的，用持久代號），停用的標 `"active": false`；**排除所有貢獻欄位與其區塊**。孤兒欄位**不匯出**，在匯出回應的 `warnings` 逐一列出名稱（「往來對象的『信用等級』是已刪除應用程式留下的停用欄位，未匯出，資料仍保留在資料庫」）。主檔的權限只帶來源為主檔的記錄（`steward`）。
- **匯入應用程式套件：** 存進 `Dynamic_AppPackage`。若其 `requires.masters` 的 id 不在目錄與已匯入套件裡，**允許匯入**（可能稍後才匯入主檔），開啟時才以 `masterNotAvailable` 擋下，預覽會講明缺哪一份。
- **匯入主檔套件：合併進既有主檔，不回 409。** 現行 `ImportAppPackage` 對 id 與內建目錄相同或已匯入過的一律 409（`ImportAppPackage.cs` 約 31 行），`AppPackageSource.FindAsync` 又總是內建優先（`AppPackageSource.cs` 約 17 行），所以改過的內建 `partner` 匯出再匯入，修改會遺失。改成依 `kind` 分流，`kind: master` 走合併：
  1. **資料庫裡還沒有這個主檔：** 把匯入內容與目前的定義（已匯入的那份，沒有就是內建那份）依代號合併後存成匯入的那份；主檔 id 的 `FindAsync` 改為**匯入的優先於內建**（只對 `kind: master`，應用程式套件不變）。之後第一個使用者開啟時用合併後的定義自動開啟主檔。
  2. **資料庫裡已有這個主檔（`Open` 或 `Closed`）：** 直接合併進既有主檔：依**對照列的持久代號**補上缺少的基本欄位、選項，以及承載它們所需的區塊；**絕不刪除或覆寫**既有的欄位、選項、區塊、檢視、權限，也不改既有欄位的啟用狀態（匯入的 `active: false` 只用在新補的欄位）。代號相同但型別或設定不同的，保留既有的並列入 `warnings`。檢視、報表、群組等其他部分不合併，列入 `warnings`。補上的欄位同樣不得必填（已有資料的模組不能新增必填欄位）。同時把匯入的那份更新成合併結果，讓之後的匯出與重新採用一致。
  3. 補欄位與選項沿用貢獻實例化的路徑（對另一個應用程式擁有的 `Active` 模組新增非必填欄位，§15.1 #6），並寫入主檔名下的對照列。**補上的欄位、選項、區塊代號若撞到某貢獻者的貢獻代號，走 `ExportCodeBook` 避撞**（加 `_2`、`_3`），以避撞後的代號寫入主檔名下的對照列，列入 `warnings`（第二輪建議 2，主控裁決，待使用者確認）。這樣合併的 `TemplateIdMap` 裡同一個符號只會對到一個欄位；代價是引用原代號的使用者套件會 `unbound`，預覽會講明。
  4. 合併屬於主檔的設計器修改，要設計器權限加系統管理員（§6）。主檔之間與主檔對應用程式的循環檢查要把 `requires.masters` 的邊納入（§4.2；第二輪建議 7）。合併是否能完全重用既有路徑是**實作前驗證**（§15.1 #14）；備案是只支援情況 1（尚未開啟時合併定義），情況 2 回 409 並說明「主檔已開啟，請在設計器手動補欄位」，不覆寫既有主檔。走備案前先回報主控（§15.1）。
- **往返測試用非內建 id 的測試套件**（第三輪必修 6，主控裁決，待使用者確認）：`ImportAppPackage` 對 id 與內建目錄相同的應用程式套件一律回 409「套件代碼已存在」（`ImportAppPackage.cs` 約 32 行），每個資料庫都帶著內建的 `crm-sales`、`inventory`，所以從庫 A 匯出的內建 CRM 匯不進庫 B。往返測試因此用兩份**只在測試專案註冊**的套件，例如 `crm-lite`、`stock-lite`，各自帶 `contributions`、`requires.masters`，`crm-lite` 另帶目標為主檔的轉換（比照現有 `AppV3Tests.Export_CrossAppReference_..._AndRoundTrips` 用非內建 id 的做法）。主檔套件照上面走合併，不受這條限制，直接用內建的 `partner`。內建 CRM、進銷存只驗「匯出內容正確」：`requires.apps` 不含主檔、`requires.masters` 含 `partner`、群組代號同一群組只有一個。
- **往返測試：** 庫 A 開 `crm-lite` 與 `stock-lite`、主檔已有兩份貢獻，且客戶在 `partner` 加了一個基本欄位與一個 `role` 選項、停用一個基本欄位、在 `crm-lite` 區塊裡加一個欄位；匯出三份（兩個測試套件、各主檔）；匯入空庫 B，**分別以兩種順序開兩個測試套件，兩種都要開啟成功**，主檔欄位、選項與區塊序列（含客戶的修改、停用狀態、區塊順序）、使用者套件的引用、轉換（含目標為主檔的轉換）、各應用程式授予的權限都與庫 A 相同。另測：`crm-lite` 的群組映射到既有群組與開啟時新建兩種情況，匯出檔 `groups[]` 涵蓋所有被引用的代號、同一個群組只有一個代號；庫 B 已開啟主檔後再匯入，只補不刪；**A→B→A（測試套件與 `partner`）與 `partner` 匯出再匯入 A，都不產生重複的欄位與選項**；有孤兒欄位的主檔匯出時出現警告、匯入後沒有那個欄位。第二輪必修 1 的驗收（匯入空庫後先開哪一個都成功）也以這組測試套件執行。
- **客戶刪過主檔的基本欄位後匯出：** 匯出的主檔套件少了那個欄位；匯入空庫後開啟的使用者套件，若引用到被刪的基本欄位 → `unbound`。合併只補不刪，所以匯入到已有該欄位的庫不會刪掉它。這是「改過的主檔不再是契約」的自然結果，已列入已知限制。

## 10. 與 s2c 整合層的關係

| 項目 | 處置 |
|---|---|
| 代號對照表（`Dynamic_AppField`／`Option`／`Block`） | **重用**，加 `<主檔 id>/` 前綴區分貢獻（§8）；選項名稱維持 `<模組>.<選項>` 兩段，撞名規則見 §5.4 |
| 合併的 `TemplateIdMap`（夥伴鍵加前綴） | **重用**：開啟使用者套件時把主檔的對照併入，翻譯 `fill`、`refGuard`、轉換、報表、事件、檢查裡的主檔符號 |
| `AppEffectsCodec`、`AppReportCodec` 的 lookups | **重用**，需要驗證主檔符號在報表欄位路徑中也能解析（§15 #2） |
| 保留代號的匯出（`ExportCodeBook`） | **重用**，貢獻的避撞集合先放入主檔基本代號；設計器新增的欄位建立時就有持久代號（§5.4）；轉換目標是主檔時照樣匯出（§4.5） |
| `IntegrationReconciler` 與五種整合狀態 | **主檔不走這套狀態。** 主檔對使用者是必要相依，不變式保證它存在，沒有 `Waiting`／`Suspended`／`Held` 這些「夥伴不在」的狀態；但整合欄位可以把主檔當參照目標，解析方式見下一列 |
| 整合的 `with[]` | 引用主檔**不必**列在 `with`；`with` 只放可選夥伴。主檔模組不在其他應用程式的 `modules` 內，既有整合的 `with[].modules` 會因此失效，要改（§11.2 MES 的 `stockOut`） |
| 整合解析主檔目標 | 驗證、目錄對帳、`IntegrationWorld`、`ConfigOf` 都把 owner 的 `requires.masters` 主檔當成永遠在場的符號來源（見下） |
| 整合欄位新增到擁有者模組 | 不變。貢獻是另一條路：新增到**主檔**的模組 |
| 入口守衛、fail-closed | 不需要。貢獻在開啟的同一個交易建立，沒有「兩次調和之間」的空窗；貢獻欄位被客戶刪除會被安全刪除的引用檢查擋下，被停用則使用者套件自己的規則（如必填）照一般規則運作 |

**整合要能解析主檔目標**（第三輪必修 3，主控裁決，待使用者確認；s2d-2 範圍）：MES 的 `stockOut` 改指主檔品項後（§11.2），現行程式只認 `with` 夥伴，套件在驗證層就會被拒絕，繞過驗證也會在調和時 `unbound`。四處都要改成「`with` 夥伴，或 owner 的 `requires.masters` 主檔」：

1. `AppPackageValidator.V5`（約 174～177 行）：整合參照欄位的 `app` 可以是 `with` 夥伴或 `requires.masters` 主檔，後者不報「夥伴模組沒有列在 with」。
2. `AppCatalogChecks`（約 30～32 行）：目錄層對主檔目標也對內建主檔套件查模組與欄位。
3. `IntegrationWorld`（約 64～70 行）：除了 owner 與 `with` 夥伴，也載入 owner `requires.masters` 主檔的對照，前綴用主檔 id。
4. `IntegrationReconciler.ConfigOf`（約 343～351 行，呼叫點約 206 行）：`refTarget.app` 是主檔時從 world 裡的主檔對照解析，主檔一定在場，不產生 `Waiting`，也不因主檔而 `unbound`（客戶刪了主檔基本欄位的情況照 §4.6 的 `unbound` 規則）。

一句話：**整合是「可選夥伴之間的連動」，貢獻是「對必要共用基礎的擴充」**，兩者共用對照表與符號翻譯，不共用狀態機。

## 11. 既有套件與資料的遷移

Demo 每次部署重建，正式客戶尚未上線，所以**不寫舊資料庫的資料遷移**：EF migration 只負責結構（新增 `Dynamic_AppMasterUse`、`Kind`，移除 `Mock_*`）。舊資料庫（含已開啟的 CRM、MES、進銷存）不會被自動轉成使用共用主檔，只有重建的資料庫才有；這一點與 s2c 對舊資料庫的態度相同（路線「套件更新入口延後」），第一個客戶上線前要一併決定。

### 11.1 CRM（`crm-sales`）

| 現有 | 新做法 |
|---|---|
| 模組 `customer`「客戶」 | 移除；改為往來對象。欄位 `industry`、`fromLead` 變貢獻欄位（區塊「CRM」）；`owner` 併入基本欄位 `owner`；`name`、`phone`、`email`、`address` 併入基本欄位 |
| 模組 `contact`「聯絡人」 | 移除；改為共用主檔聯絡人（§5.2），欄位對應 `name`、`customer` → `partner`、`title`、`phone`、`email` |
| 模組 `product`「產品」 | 移除；改為品項（`sku` → `no`、`price` → `price`、`active` → `active`）。CRM 新建品項時 `supplyType` 必選（§5.3） |
| `lead`、`opportunity`、`activity`、`quote`、`order`、明細 | 保留；`customer` 欄位改指 `partner`（守衛：角色為客戶或兩者）；明細 `product` 改名 `item` 指品項；帶值來源改主檔符號 |
| 轉換 `crm-lead-to-customer` | 目標改為 `partner/partner`；`header.set` 把 `role` 設為 `customer`（§4.5），補測試劇本：轉換後的往來對象可在 CRM 訂單的客戶欄位選到 |
| 群組 `sales`、`manager` | 保留；對往來對象、聯絡人、品項的權限改由 `contributions[].permissions` 授予（品項：業務唯讀，主管讀寫）。`requires.masters`：`partner`、`contact`、`item` |

### 11.2 MES（`mes-mold`）

| 現有 | 新做法 |
|---|---|
| `requires.entities`：`sales.customer`、`design.mold` | s2d-0 移除 `design.mold`；s2d-1 移除 `sales.customer` 後清空，改 `requires.masters`：`partner`、`item` |
| 擴充模組 `mes-customer-info`（`sales.customer`） | 移除；`creditLevel`、`website` 變貢獻欄位（區塊「MES」）；`paymentTerms`（併入 `salesPaymentTerms`）、`accountRep`（併入 `owner`）、`phone`、`email` 併入基本欄位 |
| `mes-quote.customer`、`mes-order.customer`、`mes-shipment.customer`（實體參照） | 改指 `partner`（守衛：客戶） |
| 報價明細、訂單明細、工令的 `itemNo`／`itemName` 文字 | 新增 `item` 參照（工令與訂單明細必填，報價明細選填，見 §17 問題 1）；`itemNo`、`itemName` 保留為帶值（`fill` 預設，可改），列印與報表不用動 |
| 擴充模組 `mes-mold-info`（`design.mold`） | 移除；改為 MES 自己的模組 `mes-mold`「模具資料」（只有 MES 用，不共用）：`no` 模具編號（文字，必填）、`name` 名稱（標題、必填），加上原擴充欄位 `lastCareDate`、`cycleDays`、`shotCount`（放「維護」區塊） |
| `mes-repair.mold`、`mes-mold-care.mold`（實體參照 `design.mold`） | 改為模組參照 `mold` |
| 整合 `stockOut` | JSON：`stockItem` 的 `refTarget` 改為 `{ "key": "item", "app": "item" }`，`with[].modules` 移除 `item`。核心：驗證、目錄對帳、`IntegrationWorld`、`ConfigOf` 改成可解析主檔目標（§10） |

### 11.3 進銷存（`inventory`）

| 現有 | 新做法 |
|---|---|
| 模組 `item`「品項」 | 移除；改為品項。自動編號 `no` 不保留（品號改文字）；`category`、`safetyStock`、`cost` 變貢獻欄位（區塊「進銷存」）；`price` 併入基本欄位（售價），進價改放 `cost`。`requires.masters`：`partner`、`contact`、`item` |
| 模組 `supplier`、`customer` | 移除；改為往來對象（角色供應商或客戶）。`contact` 文字欄位不再保留，聯絡人改在共用主檔聯絡人維護；供應商的供應條件併入基本欄位 `purchasePaymentTerms`；出貨地址變貢獻欄位 `shipAddress` |
| 採購、進貨、出貨、盤點、庫存異動 | 保留；`supplier`、`customer`、`item` 改指主檔；採購明細的 `price` 改從 `item/item.cost` 帶值，出貨明細從 `item/item.price` 帶值 |
| 事件、檢查、分錄、報表、列印、轉換 | 符號 `item.*`、`move.item` 之類的欄位路徑改為主檔符號（`item/item.name` 等）；內容不變 |
| 群組 `buyer`、`keeper`、`manager`、`viewer` | 保留；對主檔的權限改為貢獻授予 |

### 11.4 核心與測試

- **移除，分兩段**（第二輪準備度 1，主控裁決，待使用者確認）：
  - s2d-0：`MockMoldProvider`、`MockMold`、`MockConfigurations` 裡模具的設定、`DependencyInjection` 的模具註冊、`DemoPeople` 的模具種子與衝突檢查；migration `RemoveMockMold` 只刪 `Mock_Mold`。
  - s2d-1：`MockCustomerProvider`、`MockCustomer`、`MockConfigurations` 剩下的部分（檔案隨之移除）、`DependencyInjection` 的客戶註冊、`DemoPeople` 的客戶種子與衝突檢查；`Mock_Customer` 在 `SharedMasters` 刪除。
- **保留機制：** `IReferenceProvider`、`/entities`、擴充模組（`Kind = Extension`）是給「外部系統既有實體」的核心能力，不隨模擬實體移除。
- **測試：** 目前有約 30 個測試檔使用 `sales.customer`／`design.mold` 當夾具（`ExtensionModuleTests`、`ReferenceReadTests`、`EntitiesApiTests`、`Phase5`～`Phase7AcceptanceTests`、`TemplateValidatorTests` 等）。改為**只在測試專案註冊**的測試用實體提供者（命名為與業務無關的 `test.entity`、`test.host`），產品程式碼不留任何模擬實體。s2d-0 建立測試用提供者，並只改用到 `design.mold` 的夾具；用到 `sales.customer` 的夾具在 s2d-1 改。這是實作時最大的機械性工作量，每段各自單獨一個 commit。
- **舊範本夾具**（`tests/Fixtures/templates/*.json`，引用 `sales.customer`）：同樣改指測試用實體。
- **`NoBusinessWordsTests`：** 新增的核心程式不得出現業務名詞，用 master、contribution、binding 這類詞。

### 11.5 Demo

- Demo 的「資料」改由一個新的 `MasterScenario` 先行建立，其他情境引用它：往來對象約 12 家公司（客戶、供應商、兩者並存）與約 10 位聯絡人（共用主檔聯絡人，掛在客戶公司下），品項約 24 個（原進銷存品項為基礎，CRM、MES 的報價與訂單明細從其中的成品類取用）。**只建立當下已開啟的應用程式需要的角色與品項**（沒開進銷存就不建供應商與原料）。
- MES 情境自己建立模具記錄（原 12 筆）；CRM 與進銷存情境刪除各自建立客戶、供應商、產品的步驟，改引用主檔記錄。
- 資料全部虛構，沿用現有虛構名稱，不新增真實公司。
- `DemoPeople` 不再種子模具與客戶；`DemoPreflight` 對已有舊擴充模組的資料庫維持衝突判定（需重建資料庫，寫進結果檔）。
- 品項的 `supplyType` 逐筆指定（§5.3）：被 MES 工令種子參照的成品設 `make`，原料與其他品項設 `buy`。
- 開啟順序不影響結果：三種內建應用程式任意順序開啟，主檔欄位集合與區塊**序列**（基本區塊在前、貢獻區塊依應用程式 id 排序，§4.3）相同（Id 無關），要有測試。

## 12. ERP 對照

這一節是這份設計的基準：先對照 ERP 慣例，再說明 Formwright 怎麼取捨。

### 12.1 與既有應用程式重複的主檔與單據

| 項目 | 現況 | 處置 |
|---|---|---|
| 往來對象（客戶、供應商） | 4 份客戶、1 份供應商 | **併成共用主檔 `partner`**（s2d） |
| 聯絡人 | CRM 聯絡人、進銷存兩個文字欄位 | **共用主檔 `contact`**，掛在往來對象下（s2d） |
| 品項 | CRM 產品、進銷存品項、MES 文字欄位 | **併成共用主檔 `item`**（s2d） |
| 模具 | 模擬實體加 MES 擴充 | 改成 MES 自有模組（s2d），不共用 |
| 報價單、銷售訂單 | CRM 與 MES 各一套 | **s2e**：兩者都開時，CRM 訂單是唯一銷售訂單，確認後經整合建立 MES 工令 |
| 出貨單 | MES 與進銷存各一張 | **s2e**：兩者都開時，MES 報工完工成品入庫，出貨只用進銷存出貨單、依銷售訂單開立；MES 出貨單只在單開 MES 時用 |
| 倉庫、採購單、進貨單、庫存異動 | 只有進銷存有 | 不重複，不處理 |
| 人員（負責人、業務、技師） | 全部是系統使用者型別 | 不重複，不處理 |

### 12.2 單據擁有者與上下游

「擁有者」是該單據唯一的維護者；兩個應用程式都有時依 s2e 之後的狀態。

| 單據 | 擁有者 | 上游 | 下游 |
|---|---|---|---|
| 潛在客戶 | CRM | — | 往來對象（轉客戶）、商機 |
| 商機 | CRM | 潛在客戶 | 報價單 |
| 報價單 | CRM（只開 MES 時用 MES 報價單） | 商機 | 銷售訂單 |
| 銷售訂單 | CRM（只開 MES 時用 MES 訂單） | 報價單 | MES 工令或進銷存出貨單 |
| 工令、製程、報工 | MES | 銷售訂單 | 完工入庫（進銷存開啟時）、MES 出貨單（單開 MES 時） |
| 出貨單 | 進銷存（只開 MES 時用 MES 出貨單） | 銷售訂單 | 庫存異動、銷項發票 |
| 採購單、進貨單 | 進銷存 | — | 庫存異動、進項發票 |
| 銷項發票、收款 | 會計（s3c） | 出貨單或銷售訂單 | 傳票 |
| 進項發票、付款 | 會計（s3c） | 進貨單或採購單 | 傳票 |

s2d 完成時，報價單、訂單、出貨單仍是兩套並存（s2e 才合併），但它們指向的客戶與品項已經是同一份，這是 s2e 能動手的前提。

### 12.3 各應用程式開關組合下的行為

「主檔」欄指往來對象與品項。CRM＝C、MES＝M、進銷存＝I、會計＝A（s3c）。

| 開啟的組合 | 主檔 | 報價／訂單 | 出貨 | 備註 |
|---|---|---|---|---|
| 空系統 | 不存在 | — | — | 合法起點 |
| 只開 C | 往來對象、品項由 C 建立 | CRM | 無 | C 的品項沒有庫存欄位（進銷存貢獻的欄位不存在） |
| 只開 M | 往來對象、品項由 M 建立 | MES | MES 出貨單，不動庫存 | 模具是 MES 自有模組 |
| 只開 I | 由 I 建立 | 無 | 進銷存出貨單 | 品項帶 `category`、`cost` |
| C＋M | 一份，兩邊貢獻欄位並存 | s2d：兩套並存；s2e 後只有 CRM 訂單 | s2d：MES 出貨單 | 同一個客戶不用再對應 |
| M＋I | 一份 | MES | s2d：兩張出貨單並存，`stockOut` 整合讓 MES 出貨扣庫存；s2e 後只用進銷存出貨單 | 同一個品項，MES 完工入庫可直接用同一筆品項 |
| C＋I | 一份 | CRM | 進銷存出貨單 | 銷售訂單轉出貨單的轉換（s2e 之後）兩邊用同一份客戶與品項 |
| C＋M＋I | 一份 | s2e 後 CRM 訂單唯一 | s2e 後只用進銷存出貨單 | 貫穿流程 |
| 以上加 A | 一份，會計貢獻自己的欄位 | 不變 | 不變 | 發票的往來對象與品項直接引用主檔 |
| 關閉其中一個 | 另一方仍 `Open` 時主檔不動；該方貢獻欄位凍結、表單顯示唯讀，該方授予的寫入權限收回、其他來源照算 | — | — | 全部關閉後主檔 `Closed` 唯讀保留；相依檢查不把主檔算成關閉者的依賴者 |

### 12.4 主檔欄位歸屬（往來對象）

| 欄位 | 層級 | 來源舊欄位 | Odoo 對應 | SAP 對應 |
|---|---|---|---|---|
| `name`、`phone`、`email`、`address` | 基本 | 各客戶、供應商 | `res.partner` 同名欄位 | 一般資料（名稱、地址、通訊） |
| `role` | 基本 | 無（原本以模組區分） | `customer_rank`、`supplier_rank`（用後累計） | BP 角色（客戶、供應商） |
| `taxId`、`owner` | 基本 | MES 客戶資料、CRM `owner` | `vat`、`user_id` | 稅號、銷售員 |
| `salesPaymentTerms` | 基本 | MES 客戶資料的付款條件 | `property_payment_term_id`（客戶付款條件） | 客戶的公司代碼資料：付款條件 |
| `purchasePaymentTerms` | 基本 | 進銷存供應商 `terms` | `property_supplier_payment_term_id`（供應商付款條件） | 供應商的公司代碼資料：付款條件 |
| `industry`、`fromLead` | 貢獻：CRM | CRM 客戶 | CRM 模組擴充的欄位 | 銷售視圖／關係 |
| `shipAddress` | 貢獻：進銷存 | 進銷存客戶 `address` | 子聯絡人（類型為送貨地址） | 收貨方（Ship-to）地址 |
| `creditLevel`、`website` | 貢獻：MES | MES 客戶資料 | 信用額度屬會計模組 | 信用管理 |

聯絡人 `contact`：`name`、`partner`、`title`、`phone`、`email` 全部是基本欄位，來源是 CRM 聯絡人；Odoo 是 `res.partner` 的子列（`parent_id`），SAP 是 BP 之間的關係。貢獻欄位目前沒有。

品項：

| 欄位 | 層級 | 來源舊欄位 | Odoo 對應 | SAP 對應 |
|---|---|---|---|---|
| `no`、`name`、`unit`、`active` | 基本 | CRM `sku`、MES `itemNo`、進銷存 `no`、各 `name` | `default_code`、`name`、`uom_id`、`active` | 物料基本資料（物料號碼、描述、基本單位） |
| `price` | 基本 | CRM `price`、進銷存 `price` | `list_price` | 銷售視圖的價格 |
| `supplyType` | 基本 | 無（新增） | 路線（Manufacture／Buy） | MRP 視圖的採購類型（E 自製／F 外購） |
| `cost` | 貢獻：進銷存 | 進銷存 `price`（作為進價的用法） | `standard_price` | 會計視圖的標準價 |
| `category`、`safetyStock` | 貢獻：進銷存 | 進銷存同名 | 庫存屬性、最小庫存規則 | 倉儲與 MRP 視圖 |

### 12.5 對會計（s3c）的影響

- 發票、收付款、應收帳齡、傳票的往來對象**直接是 `partner`**，不需要會計另建主檔，也不需要和 CRM、進銷存的客戶做對應。這是使用者原本擔心的重複來源。
- 發票上的客戶用 `refGuard`（角色含客戶），進項發票用角色含供應商；應收帳齡以發票的往來對象分組，下鑽回發票與出貨單。
- 會計貢獻自己的欄位到往來對象（區塊「會計」）：例如應收科目、應付科目、結帳日；統編與兩個付款條件已經是基本欄位，會計直接用（銷項發票到期日從 `salesPaymentTerms`、進項發票從 `purchasePaymentTerms` 帶值，或再貢獻結構化的天數欄位，由 s3c 決定）。會計貢獻的科目這類欄位預設只有會計的群組看得到（§6），其他群組要管理員授予。
- 銷項發票的品項直接引用 `item`。品項基本欄位目前沒有實物／服務之分；會計若需要，由會計貢獻（草稿 §2.4 也沒有放進基本欄位）。
- 只開會計（沒有 CRM、進銷存）時，往來對象與品項由會計建立，仍是合法組合。
- 關閉會計：其貢獻欄位凍結、會計授予的寫入權限收回，往來對象與品項主檔不動（有其他使用者時）。

### 12.6 Odoo 與 SAP 的做法對照

| 面向 | Odoo | SAP S/4HANA | Formwright 這份設計 |
|---|---|---|---|
| 往來對象 | `res.partner` 一張表；公司與聯絡人同表，以 `parent_id` 連結 | Business Partner 一份一般資料，加角色與關係 | 往來對象一個模組、聯絡人另一個模組（簡化：欄位不同、貢獻欄位不會出現在個人列） |
| 客戶／供應商 | 以使用推得（`customer_rank` 等） | 明確指派角色 | 明確的 `role` 下拉（SAP 式），用 `refGuard` 限制挑選 |
| 擴充欄位 | 各模組繼承 `res.partner` 加欄位 | 角色專屬資料（銷售組織、公司代碼等） | 各應用程式貢獻欄位，放在自己的區塊（類似視圖）；預設只有貢獻者的群組看得到 |
| 付款條件 | 客戶、供應商各一個欄位 | 客戶與供應商的公司代碼資料各一份 | 基本欄位分 `salesPaymentTerms`、`purchasePaymentTerms` |
| 品項 | `product.template`＋`product.product`（變體） | 物料主檔：基本資料加多個視圖（銷售、採購、MRP、會計） | 一個模組 `item`；各應用程式貢獻欄位於自己的區塊；**不做變體** |
| 單據擁有者 | 銷售訂單屬銷售、出貨屬庫存、發票屬會計；MRP 的製造單完工後入庫 | 同 | 同；MES 完工成品入庫、出貨由進銷存負責（s2e） |
| 解除安裝 | 刪除該模組新增的欄位 | 不適用 | 無值的欄位刪除，有值的停用保留、重開同一應用程式時接回；參照自己模組的欄位連值清除並計入確認數（§7） |

差異與理由：不做變體、不做多公司與多銷售組織；聯絡人與地址不併進往來對象（整體設計草稿 §5.2 的理由：結構簡單比貼近 Odoo 重要，客戶自己改模組時好懂）；多個送貨地址在 Odoo 以子聯絡人表達，這裡先只有一個 `shipAddress` 貢獻欄位，需求出現時再另開設計（列為後續）。

### 12.7 設計檢核清單逐題（整體設計草稿 §7）

| 題 | 回答所在 |
|---|---|
| 1 重複概念盤點 | §1、§12.1 |
| 2 主檔欄位歸屬 | §5、§12.4 |
| 3 單據擁有者 | §12.2 |
| 4 上下游與產生方式 | §12.2、§14；s2d 不新增單據，沒有新的轉換或整合 |
| 5 開關組合行為 | §12.3、§7 |
| 6 會計影響 | §12.5；s2d 不產生傳票，沒有 `skip`／`block` 選擇 |
| 7 對照一般 ERP | §12.6 |
| 8 核心能力 | 新增：主檔套件與自動開啟、貢獻、貢獻欄位的生命週期鎖（寫入管線最底層）、權限來源記錄與聯集重算（逐動作範圍）、貢獻欄位預設限定可見、主檔套件合併匯入、設計器欄位的持久代號、孤兒欄位接回、轉換固定值 `header.set`。第二個不相干情境：往來對象（CRM、MES、進銷存、會計、客服）與品項（CRM、MES、進銷存、會計）各被多個不相干的應用程式使用，已滿足鐵則 2 |

## 13. 品項與往來對象要不要同一套機制，以及切分建議

**同一套機制。** 兩者的需求完全相同：多個應用程式共用一份定義、各自加欄位、生命週期由使用者推得。分開做等於做兩次核心。品項額外的複雜度只在套件遷移（欄位路徑要全部改成主檔符號，尤其進銷存的事件、檢查、分錄與報表引用品項最密集）。

**範圍確實偏大，建議同一位 worker 分三段依序做，每段單獨驗收、單獨 commit 群：**

| 段 | 內容 | 驗收 |
|---|---|---|
| s2d-0 模具歸位 | MES 自有模組 `mes-mold`、移除 `design.mold`、`MockMoldProvider`；migration `RemoveMockMold`；模具相關夾具改測試專用實體提供者 | MES 單開仍可開啟；模具保養、報修、Demo 正常；既有測試全綠 |
| s2d-1 核心能力＋往來對象與聯絡人 | 開頭先做可行性驗證（§15.1）；格式 6、主檔套件、`requires.masters`、`contributions`、生命週期、資料表；`partner`、`contact` 主檔；CRM、進銷存、MES 改用；移除 `sales.customer`、`MockCustomerProvider` 與客戶夾具；Demo | §15 的測試清單中與往來對象有關的全部；四種版型實機 |
| s2d-2 品項 | `item` 主檔；CRM、進銷存、MES 改用品項；`stockOut` 整合調整（含整合解析主檔目標的核心改動，§10）；Demo | 同上，加上價格／進價拆分的行為測試 |

s2d-0 與核心能力無相依，可以最先做，**不必等可行性驗證**（驗證放在 s2d-1 的開頭）；s2d-1 是主體；s2d-2 的核心工作在 s2d-1 已經完成，只剩套件遷移。三段都動 `apps/*.app.json`，不能和其他動這些檔案的工作同時進行，所以排在 s3a 合併之後。

## 14. 對 s2e、s3c 的介面，以及與出貨單的關係

### 14.1 給 s2e（訂單合併、出貨合併、CRM 訂單建立 MES 工令）

s2d 完成後 s2e 可以直接依賴的事實：

1. CRM 訂單明細的 `item`、MES 工令的 `item`、進銷存庫存異動的 `item`，指向**同一個模組** `item/item`。事件的 `set` 與轉換的 `header.copy` 複製參照值不需要任何對應表（參照欄位目標相同才能複製）。
2. CRM 訂單的 `customer`、MES 出貨單的客戶、進銷存出貨單的 `customer`，指向同一個模組 `partner/partner`。「CRM 訂單確認後建立 MES 工令」「銷售訂單轉進銷存出貨單」都不需要處理客戶對應。
3. 引用主檔符號不需要 `with` 宣告：s2e 的整合只列真正的可選夥伴（例如進銷存、MES）。
4. 主檔的基本欄位是契約：s2e、s3c 可以引用 `partner/partner.<基本欄位>`、`item/item.<基本欄位>`。
5. 品項的 `supplyType` 必填（§5.3）：s2e 的「CRM 訂單確認後建立 MES 工令」只對 `make` 的品項建工令；`supplyType` 是空值的品項不建工令並提示，fail closed。

**s2e 需要自己解決的**（不在 s2d）：逐列轉換（缺口 E）、一個銷售訂單明細對應一張工令、MES 單開與合開時報價與訂單模組的取捨（隱藏哪一邊）、MES 完工入庫事件。

### 14.2 與兩張出貨單的關係

s2c 的 `stockOut` 整合讓 MES 出貨扣進銷存庫存。s2d 不改兩張出貨單本身，只做兩件事：MES 出貨單與進銷存出貨單的客戶都改指 `partner`；`stockOut` 的 `stockItem` 改指主檔品項（§11.2），為此整合要能解析主檔目標（§10）。這之後 MES 的工令已經有 `item`，MES 出貨單可以從工令帶出品項，`stockItem` 這個「出貨時再選一次」的欄位其實變成多餘，但移除它連同「MES 出貨改成單開 MES 才用」都屬於 s2e 改寫 `stockOut` 的範圍，s2d 不提前做，避免同一段整合改兩次。

### 14.3 給 s3c（會計）

見 §12.5。需要 s2d 保證的：`partner`、`item` 主檔存在時基本欄位穩定、貢獻機制可用（會計貢獻區塊「會計」）、角色欄位可作 `refGuard`、關閉會計不影響主檔。

## 15. 實作前的可行性驗證、測試清單、順序

### 15.1 假設與備案

這些我讀過設計文件與部分程式但沒有逐行驗證，s2d-1 第一步先用失敗測試確認，不成立就用備案並寫進結果檔。**任何備案都不得放寬權限，也不得讓應該凍結的欄位變成可寫**；找不到符合這條的備案就停下來回報，不自行降級。

**會改變格式或使用者行為的備案要先停下回報**（第二輪準備度 6，主控裁決，待使用者確認）：#4（取消自動轉客戶）、#7（擋不住的寫入路徑）、#14（已開啟的主檔不能合併）一旦不成立，實作者停下，把問題寫進結果檔回報主控，不得自行採用備案後一路做完 s2d-1。#16（逐動作範圍）的備案會收窄使用者看得到的資料，同樣先回報（我的判斷）。其餘各項的備案不改格式也不改行為，可以照表採用並記錄。

| # | 假設 | 不成立時的備案 |
|---|---|---|
| 1 | 開啟應用程式時，可在同一個交易內先開（或重開）主檔，再實例化使用者模組；兩個請求同時開啟，唯一索引衝突時可重新載入狀態後綁定 | 先檢查再開，衝突時整批回 409 請重試 |
| 2 | 合併的 `TemplateIdMap`（主檔鍵加 `<id>/` 前綴）能在 `fill`、`refGuard`、轉換、報表欄位路徑、事件、檢查中解析主檔符號；整合欄位經 §10 的四處改動後能以主檔為參照目標（s2d-2 驗） | 在使用者套件開啟前先把主檔符號換成內部別名 |
| 3 | 主檔套件可以 `requires.masters` 另一個主檔（聯絡人需要往來對象），開啟時依序自動開啟；`refGuard` 的 `in` 可列多個選項（客戶、兩者） | 聯絡人與往來對象放同一份主檔套件的兩個模組 |
| 4 | 轉換定義的 `target` 可以是主檔模組、`sourceField` 可以是貢獻欄位；`AppExporter.ExportConversionsAsync` 可改成接受目標是 `requires.masters` 主檔的轉換並以主檔符號輸出，匯入時經合併對照解析回去 | 潛在客戶轉客戶改成：潛在客戶上加「已轉往來對象」參照欄位，由使用者在往來對象手動建立後選取（失去自動帶值）；匯出問題隨之消失 |
| 5 | 主檔模組的群組權限列與資料範圍列可由「來源記錄重算」函式在同一交易內寫入（不經 `SaveModulePermissions` 的整模組覆寫），管理員儲存主檔模組權限時可攔截並寫回來源記錄 | 重算函式包在 `SaveModulePermissions` 之上，讀取—合併—寫回，用交易保護；管理員改主檔模組權限改走專用端點，原權限頁對主檔模組唯讀顯示有效值 |
| 6 | `AddFieldRequest`／`AddBlock` 能對另一個應用程式擁有的 `Active` 模組、已有資料時新增非必填欄位與區塊 | 與 s2c 假設 #3 同，改走內部路徑 |
| 7 | 一般新增修改、明細、匯入、轉換、事件、整合事件、自動編號、公式重算、彙總重算（含 `FormulaRecalc`、`RollupRecalc`）、刪除記錄的清空參照（`DeleteRecordHandler` 的 `ToClear`）全部經過記錄值的共同落地點，能在那裡依 `FieldId` 反查貢獻者、凍結其欄位；RVP 的輸入判斷能優先於 `systemKeys`；該模組沒有貢獻列時無額外成本 | 沒經過共同落地點的路徑各自補同一個檢查；仍有擋不住的，**停下回報主控**。不再有「關閉時轉停用」備案（§7），**不得退而仍可寫** |
| 8 | `SeedData` 啟動時同步 `Program.Active` 的規則能涵蓋自動關閉的主檔 | 調整規則為「有 `Active` 或 `Closed` 模組」（已是套件規格 §6.3 的做法） |
| 9 | 刪除應用程式時，沒有值的貢獻欄位可直接刪、有值的可改為 `Inactive` 並記成孤兒，過程不違反欄位安全刪除的事後條件；`DeletePlanner` 能把 A 擁有、目標是主檔的轉換與事件視為集合內，並把參照自己模組的貢獻欄位列入清除集合（豁免 `ModuleBlockersAsync` 對清除集合與隨 A 刪除欄位產生的全部阻擋類型，以及自身轉換阻擋），由 `FieldPurge` 以系統身分清除、`ClosedDataGuard` 對計畫內清除放行；`Purged` 能納入清除的值筆數 | 有值的欄位保持原狀不動、只記孤兒；參照自己模組的貢獻欄位改為刪除 409 並列出，管理員重開 A、安全刪除後再刪（§7 刪除第 4 點） |
| 10 | 測試專案可註冊測試專用的 `IReferenceProvider`，不影響 WebAPI 的產品註冊 | 在測試的 `WebApplicationFactory` 覆寫服務 |
| 11 | `AppDependencyChecker.DependentsOfAsync` 能比照排除整合欄位的方式，排除目標應用程式自己貢獻的欄位（關閉與刪除都排除） | 在相依結果出來後過濾掉主檔應用程式（`Kind = master` 不得出現在依賴者清單） |
| 12 | `PermissionResolver.Resolve` 的 `hidden` 集合涵蓋所有讀取路徑（記錄、列表、篩選、排序、報表、匯出、列印、帶值、公式），把限定可見的欄位加進去就能全面生效 | 沒走 `hidden` 的讀取路徑各自補檢查；補不完前，限定可見的欄位對非系統管理員一律隱藏（只收緊，不放寬） |
| 13 | `ExportCodeBook` 可以接受預先佔用的代號集合（主檔基本代號），退回代號時避開 | 匯出貢獻時一律用 `o_<id>`／`f_<id>`／`b_<id>` 並加 `c_` 前綴，與基本代號不可能相同 |
| 14 | 主檔套件匯入可依 `kind` 分流：`AppPackageSource.FindAsync` 對主檔匯入優先、合併進已開啟的主檔可重用貢獻實例化的新增欄位路徑 | 只支援主檔尚未開啟時合併定義；已開啟時回 409 說明「請在設計器手動補欄位」，不覆寫 |
| 15 | 轉換標頭可加固定值 `header.set`（選項欄位設為指定選項），執行時經 `RecordValueProcessor` 驗證 | `role` 預設值設為 `customer`，轉換建立記錄時套用預設值 |
| 16 | `PermissionResolver` 與所有查詢、寫入路徑能依動作套用 `Dynamic_ModuleActionScope` 的範圍（`EffectivePermissions` 依動作提供擁有者集合），沒有逐動作列的模組行為不變 | 主檔模組上同一群組來源範圍不同時，整組取各來源中最窄的範圍（只收緊），開啟預覽警告；先回報主控 |
| 17 | 設計器新增欄位、選項、區塊的路徑（`AddFieldRequest`、`AddBlock` 等）能依所在區塊判斷歸屬，在同一交易內寫對照列與持久代號 | 主檔模組上的設計器新增改走專用的內部路徑，由它補寫對照列；寫不了就拒絕在主檔上新增，不留沒有歸屬的欄位 |

**實作注意**（第三輪審查附帶兩點，主控裁決，待使用者確認）：

- **凍結鎖不要做成資料層攔截器。** §7 凍結第 1 點的「最底層」若做成 `SaveChanges` 攔截器這類真正的資料層，刪除計畫內的清除（`FieldPurge` 直接改寫 `ValuesJson`）也會被擋下。現行寫入 `ValuesJson` 的地方有 `UpdateRecordHandler`、`DetailWriter`、`RecordCreator`、`DeleteRecordHandler`、`AttachmentHandlers`、`FormulaRecalc`、`RollupRecalc`，再加上 `FieldPurge`，沒有現成的共同落地點；#7 驗證得到「要各自補」是預期結果，照備案各自補同一個檢查即可，不必停下回報（真的擋不住才回報）。
- **`DeleteAppHandler` 的順序。** `DeleteApp.cs` 約 82 行的 `apps.Remove(app)` 一提交，A 名下的對照列、`Dynamic_AppGrant`、`Dynamic_FieldGrant` 就被 cascade 刪掉。孤兒列（§7 刪除第 5 點）與清除集合要在這一步**之前**從 A 的對照列建好；主檔權限的重算（§6）要在這一步**之後**，才不會把 A 的來源記錄算進去。

### 15.2 測試清單

**單元與整合（先寫失敗測試再實作）：**

- 格式：格式 6 才收主檔／`requires.masters`／`contributions`；主檔套件限制；貢獻欄位不得必填；貢獻欄位型別允許清單（`autonumber`、`multienum`、`attachment`、`rollup` 被擋；公式引用別的貢獻者欄位被擋）；貢獻參照欄位 `onDelete: cascade` 被擋；貢獻代號不得與基本欄位相同；貢獻只能引用本套件的群組與欄位；主檔符號第一段須在 `requires.masters`；同一個 id 同時在 `requires.apps` 與 `requires.masters`、或 `requires.apps` 列了主檔 → 整份拒絕；別的應用程式的貢獻欄位不可引用；主檔之間、主檔與應用程式之間的 `requires.masters` 循環在匯入與目錄載入都被擋。
- 目錄：內建套件對內建主檔逐欄位對帳（故意改錯一個欄位代號得到啟動錯誤）。
- 開啟：主檔不存在時自動開啟並建立 `steward` 群組對應；主檔已 `Open` 綁定；主檔 `Closed` 重開；直接開啟、關閉、重開主檔 → 409，可開啟清單不列主檔；三個內建應用程式**所有 6 種開啟順序**，最後主檔欄位集合與區塊**序列**相同。
- 生命週期：關閉非最後使用者→主檔 `Open`、貢獻欄位凍結；關閉最後使用者→主檔 `Closed`；**只開進銷存再關閉 → `partner`、`contact`、`item` 全部 `Closed`**；**只列 `contact` 的套件開啟再關閉 → `partner`、`contact` 一起開、一起關**；重開→全部恢復；刪除非最後使用者、最後使用者（主檔保留並有警告）；刪除後重新採用；手動刪除主檔的條件；不變式檢查函式（使用者閉包）在每個步驟後成立。
- 貢獻欄位刪除：無值刪除、有值停用保留且值可讀、被外部引用 409、區塊空了才刪；設計器加在貢獻者區塊裡的欄位照貢獻欄位處理。
- 孤兒接回：刪除 CRM（`industry` 有值，成為孤兒）後重新開啟 CRM → 接回同一個欄位、舊值可讀，沒有第二個「產業」；型別不同時另建新欄位、**沿用原代號 `industry`**（匯出代號與套件相同）、舊孤兒列刪除並警告；**型別不同重開後再刪一次 CRM → 成功**（孤兒表沒有撞鍵）；同一個貢獻者有欄位 `vip` 與選項 `vip` 時刪除 → 兩列孤兒都寫入、不撞鍵。
- 設計器欄位歸屬：在 `partner` 基本區塊加欄位 → 歸主檔、有持久代號、隨主檔匯出；在 CRM 區塊加欄位 → 歸 CRM、受限可見、CRM 關閉後凍結、隨 CRM 匯出；跨歸屬拖曳 → 409；CRM 刪除後，在孤兒「CRM」區塊新增欄位或選項、把欄位拖進去、把孤兒欄位拖出來 → 都是 409，訊息提示改加在基本區塊。
- 權限：兩個應用程式映射到同一群組（CRM「讀取／all」、進銷存「讀取＋編輯／own」）→ 有效值是讀取 all、編輯 own，沒有「編輯 all」，6 種開啟順序結果相同；一般模組（沒有逐動作範圍列）的有效權限與現行相同；關閉其中一個只收回它的寫入動作、刪除只收回它的全部；管理員拿掉的動作不會在重算後長回來；**不改任何東西直接存檔 → 來源記錄與有效值都不變**（不產生手動「編輯 all」）；**CRM 關閉時管理員存檔 → CRM 重開後編輯恢復**；主檔模組的權限頁逐動作回傳與送出範圍，`Dynamic_ModuleDataScope` 是讀取動作的範圍；貢獻欄位對其他群組與後建群組隱藏、對貢獻者群組與系統管理員可見、管理員經 `PUT /modules/{key}/permissions` 的 `fieldGrants` 授予後可見；授予可見但該群組設了 Hidden → 仍看不到；群組刪除時來源記錄與可見列一起消失；貢獻者刪除後的停用欄位重新啟用時仍受限；完整性檢查「權限列＝重算結果」。
- 關閉後凍結：一般新增修改、明細、匯入、轉換、事件、整合事件，各一個「貢獻者關閉 → 寫貢獻欄位得 409、值不變」；系統管理員同樣被擋；值沒變時其他欄位照常寫入；**刪除記錄的清空參照**：CRM 貢獻「主要聯絡人 → `contact`」（`setNull`）且有值，CRM 關閉後進銷存刪那位聯絡人 → 409、聯絡人與往來對象都不變。
- 凍結的衍生值：CRM 貢獻一個引用基本欄位 `name` 的公式後關閉 CRM，改 `name` → 公式值不變；`FormulaRecalc` 批次重算時凍結欄位不變；重開 CRM 後下一次存檔才更新。
- 凍結不擋計畫內清除：刪除 CRM 時清除集合裡的 `fromLead` 殘留值由 `FieldPurge` 清掉，沒有被凍結鎖擋下。
- 凍結的顯示：schema 與記錄讀取把凍結欄位放進 `ReadOnlyFieldIds` 並回傳 `lockedBy`。
- 相依：CRM 貢獻 `fromLead` 時與進銷存同開，關閉 CRM 不要求 cascade、主檔與進銷存維持 `Open`。
- 刪除參照自己模組的貢獻欄位（**不手動停用欄位**）：轉客戶產生 `fromLead` 值後，**先軟刪潛在客戶（`fromLead` 因 `setNull` 被清空，刪除預覽列出清空筆數）→ 關閉 → 直接刪除 CRM** → `crm-lead-to-customer` 轉換不構成阻擋、`fromLead` 欄位消失，往來對象記錄本身保留。分兩案：
  - 主檔 `Open`（進銷存也開著）。
  - 主檔 `Closed`（CRM 是最後使用者）：`ClosedDataGuard` 對計畫內清除放行。
  - **殘留值（兜底）：** 先軟刪往來對象、再軟刪潛在客戶，使 `fromLead` 留有值 → 刪除 CRM 時預覽列出清除的值筆數、`Purged` 等於軟刪筆數加清除筆數才成功、殘留值被清除。
  - **帶 `fill` 的 `fromLead`：** `fromLead` 設 `fill.map` 從 `lead` 帶值 → 刪除 CRM 不因 `Fill` 阻擋失敗。
  - 另測：預覽後再新增一筆 `fromLead` 殘留值，以舊的 `Purged` 送出 → 409。
  - 另測：`fromLead` 若宣告成 `restrict` → 驗證擋下（§4.3）。
- 選項撞名：基本 `role.customer` 與貢獻 `customerType.customer` → 驗證擋下；不同貢獻者的同名選項互不影響；匯出的退回代號不與基本代號相同。
- 轉換：潛在客戶轉客戶後 `role = customer`，能在 CRM 訂單客戶欄位選到；匯出 CRM 包含這個轉換。
- 匯出匯入：§9 的往返，用非內建 id 的測試套件（`crm-lite`、`stock-lite`；含客戶改過的主檔、停用的基本欄位、目標為主檔的轉換、各自授予的權限，兩種開啟順序都開啟成功）；內建 CRM 只驗匯出內容：`requires.apps` 不含主檔、`requires.masters` 含 `partner`、群組代號同一群組只有一個；A→B→A 與 `partner` 自己匯入自己不產生重複；匯出主檔不含貢獻、停用基本欄位帶 `active: false`、孤兒欄位不匯出且列在警告；主檔匯入合併只補不刪、不回 409，補的欄位撞到貢獻代號時避撞並警告；匯入缺主檔 → 開啟 `masterNotAvailable`；客戶刪基本欄位後 `unbound`。
- 併發：兩個請求同時開啟兩個不同應用程式（都需要同一個主檔）→ 恰好建立一份主檔，兩者都綁定成功。
- 往來對象與聯絡人行為：`refGuard` 限制客戶／供應商；角色為兩者的可被銷售與採購單據挑選；聯絡人必填所屬公司，出現在公司的反向關聯分頁；只開進銷存或只開 CRM 時聯絡人主檔自動出現，只開 MES 時不出現。
- 品項行為：採購明細從 `cost` 帶值、出貨明細從 `price` 帶值；MES 工令選品項後 `itemNo`／`itemName` 帶值且可改；`supplyType` 必填（沒選不能存）；Demo 種子每個品項都有 `supplyType`。
- 整合解析主檔目標（s2d-2）：MES 套件的 `stockOut` 指向 `item/item`、`with` 不列品項 → 驗證與目錄對帳通過；MES＋進銷存開啟後 `stockItem` 狀態不是 `unbound`、不產生 `Waiting`，MES 出貨扣得到進銷存庫存。
- 模具：MES 單開可開啟；保養、報修的模具參照；`design.mold` 不再出現在 `/entities`。
- 既有測試：`MesAppEquivalenceTests` 白名單、`DemoStructure`、`DemoGeneration`、`SeedDataTests`、`NoBusinessWordsTests`、`EntitiesApiTests` 等同步更新；**最後完整全跑一次**。
- 公開 repo 掃描：程式、文件、commit message 不得出現公司名稱、個人絕對路徑、內網 IP。

**前端：** `bun run test`、`typecheck`、`build`；四語系補文案（th、vi 標待校對）；顏色一律用語意 token。實機（`Demo__LoginEnabled=true Demo__SeedOnStartup=true`）：開啟順序不同的 Demo、往來對象的「客戶」「供應商」檢視與聯絡人模組、公司頁的貢獻區塊（基本區塊在前、貢獻區塊依應用程式 id 排序）與聯絡人分頁、關閉一個應用程式後貢獻欄位顯示唯讀並提示「○○應用程式已關閉」、群組權限頁欄位存取區的貢獻欄位可見勾選；手機 390x844、平板 820x1180、筆電 1366x768、桌機 1920x1080 各一遍。

### 15.3 實作順序

1. s2d-0：模具歸位、migration `RemoveMockMold`、模具相關測試夾具（獨立 commit 群，不等可行性驗證）。
2. s2d-1 開頭：可行性驗證（§15.1 的 1～7、9、11、12、14～17；#10 已在 s2d-0 用到）。#4、#7、#14、#16 走備案前先回報。
3. 資料表與 migration `SharedMasters`（`Dynamic_AppOrphan` 的 `Kind` 與唯一索引已在 §8 定案，產生前照它建）；格式 6 與驗證。
4. 主檔套件讀取；`AppOpener` 自動開啟與綁定、主檔狀態重算；貢獻實例化與區塊排序；權限來源記錄、逐動作範圍與重算、貢獻欄位限定可見與手動授予。
5. 關閉、重開、刪除（含參照自己模組的欄位、自身轉換與事件、孤兒與接回）、相依檢查排除、生命週期鎖（兩道）、完整性檢查函式。
6. 設計器欄位歸屬與持久代號；匯出（主檔歸 `requires.masters`、群組代號單一來源、權限只帶自己、轉換目標為主檔、依歸屬匯出與 `active`、孤兒警告、避撞）、匯入（主檔合併）。
7. `partner` 主檔與三個套件遷移；移除 `sales.customer` 與客戶夾具；Demo。
8. s2d-2：`item` 主檔與遷移（含 `supplyType`）；整合解析主檔目標（§10 的四處）與 `stockOut` 調整。
9. 文案、前端、完整全跑、實機。

## 16. 我代為決定的事

1. **主檔是特殊套件，重用應用程式的開啟、關閉、匯出機制（§3）。** 錯了的代價：若使用者要的是核心內建實體，需改為資料表加固定 API；已寫的對照表、貢獻語意可以保留。
2. **主檔跟隨使用者：任一應用程式（直接或經主檔間接）`Open` 主檔就 `Open`，全部關閉就 `Closed`；主檔不能直接開關（§7；第二輪必修 7 把閉包定義與「不能直接開啟」補齊，主控裁決，待使用者確認）。** 錯了的代價：若希望主檔永遠可寫，要加「主檔獨立開關」，多一種狀態與 UI。
3. **最後一個使用者被刪除時主檔保留，不自動刪（§7）。** 錯了的代價：想完全回到空系統要手動刪主檔；若希望自動刪，條件要改為「主檔沒有任何記錄」才刪，結構定義（客戶可能改過）的遺失需要使用者認可。
4. **刪除應用程式時有值的貢獻欄位停用保留成孤兒、不匯出，同一個應用程式重開時接回（§7、§9）；例外是參照貢獻者自己模組的欄位，一律 `setNull`，刪除 A 時連欄位與殘留值清除，計入刪除確認數，主檔 `Closed` 時也放行；應用程式擁有、目標是主檔的轉換與事件隨它刪除；型別不同不接回時新欄位沿用原代號、舊孤兒列刪除（第一輪必修 5、10，第二輪必修 3、4、建議 6，第三輪必修 1、4，主控裁決，待使用者確認）。** 錯了的代價：主檔上留有停用欄位，需要管理員自行清理；替代是擋下刪除（較安全但很煩）或清值（資料遺失）。
5. **聯絡人是獨立主檔 `contact`，參照往來對象（§5.2）。** 採整體設計草稿的建議。派工指示的「聯絡人照 Odoo 掛在公司底下」以「聯絡人屬於公司、公司頁列出聯絡人」滿足，不採 Odoo 同表做法。錯了的代價：若使用者要的是同表，要把 `contact` 併回 `partner` 並加 `kind`、`parent`，貢獻欄位會出現在個人列。
6. **角色用單一下拉 `role`（客戶／供應商／兩者），挑選用 `refGuard` 限制（§5.1）。** 採整體設計草稿的建議。錯了的代價：加角色要改選項與各處 `refGuard`。
7. **基本欄位多放：`taxId`、`salesPaymentTerms`、`purchasePaymentTerms`、`owner`；品項加 `supplyType`（§5.1、§5.3、§5.4）。** 與整體設計草稿 §2.2 的基本欄位一致，付款條件依 ERP 資料模型審查分成銷售與採購兩欄、品項加供應方式（自製／外購）。因為沒有更新入口，漏放的成本高於多放。品號用文字 `no`、不另設 `sku`（§5.3）。
8. **貢獻欄位不得必填、不收規則（§4.3）。** 原因同 s2c §5.2。
9. **貢獻欄位預設只對貢獻者的群組與系統管理員可見，其他群組要管理員授予（§6；推翻原「不做白名單」，第一輪審查必修 2，主控裁決，待使用者確認）。** 錯了的代價：跨應用程式本來就該共用的貢獻欄位，管理員要逐一授予；真正該共用的應升為基本欄位（§5.4）。
10. **主檔權限改為來源記錄＋聯集重算（§6；第一輪必修 3、6，第二輪必修 2、建議 1，主控裁決，待使用者確認）。** 動作取聯集、範圍逐動作取較寬（新增逐動作範圍表，只用在主檔模組，一般模組不變是我的判斷）；來源記錄只存 `GroupId`，匯出群組代號走 `GroupCode()`；匯出只帶自己授予的；刪除只收回自己的全部。**其中「關閉只收回自己的寫入動作、保留讀取」是我對裁決「關閉時只收回它自己的部分」的解讀**，理由是與既有「關閉＝唯讀保留」一致、貢獻欄位關閉後仍可讀；若裁決原意是關閉就收回全部，改成重算時不計入關閉應用程式的記錄即可，代價是它的群組關閉後讀不到主檔。管理員存檔以存檔當下重算的有效值為基準取差異，拿掉的動作只從目前有計入的來源移除，收回以管理員為準；主檔模組的權限頁逐動作設定範圍（第三輪必修 5，主控裁決，待使用者確認）。
11. **主檔模組 key 用 `master-` 前綴（§4.1）。**
12. **MES 的報價明細品項選填、訂單明細與工令必填（§11.2）。** 見 §17 問題 1。
13. **模具不共用，歸 MES（§11.2）。** 依派工指示。
14. **進價與售價分欄（§5.3）：** 主檔 `price` 是售價，進銷存貢獻 `cost`。這是順手修正現有混用，行為改變：進銷存採購明細的單價帶值來源變了。
15. **`stockOut` 整合只調整目標、不提前改寫（§14.2）；整合的驗證、目錄對帳、`IntegrationWorld`、`ConfigOf` 要能解析 `requires.masters` 主檔目標（§10；第三輪必修 3，主控裁決，待使用者確認）。**
16. **三段切分、同一位 worker 依序做（§13）。**
17. **貢獻者不是 `Open` 時，它的貢獻欄位在寫入管線最底層凍結，連系統管理員、`systemKeys`、公式重算、刪除記錄的清空參照也改不了；明確輸入不同值就整筆 409，不靜默忽略；前端顯示為唯讀並提示原因（§7；第一輪必修 1，第二輪必修 5、建議 3、4，主控裁決，待使用者確認）。** 沒有「關閉時轉停用」備案，擋不住就停下回報。
18. **相依檢查排除自己貢獻的欄位（§7；必修 4）。**
19. **主檔套件匯入走合併：未開啟時合併定義並讓匯入的優先於內建，已開啟時依持久代號只補不刪、不覆寫，補的代號撞到貢獻代號時避撞，不回 409（§9；第一輪必修 7，第二輪必修 6、建議 2，主控裁決，待使用者確認）。** 錯了的代價：客戶想「用匯入的版本取代」做不到，只能在設計器手動刪改。
20. **選項符號維持兩段 `<主檔>/<模組>.<選項>`，貢獻選項代號不得與基本選項相同（§4.4、§5.4；必修 8）。**
21. **轉換新增固定值 `header.set`，潛在客戶轉客戶時設 `role = customer`（§4.5；建議 11）；備案是 `role` 預設值。**
22. **匯出依歸屬、不依啟用狀態；主檔模組上的欄位可帶 `active: false`，往返後仍停用；孤兒欄位不匯出、只在警告列名（§4.1、§9；第一輪必修 10，第二輪建議 3，主控裁決，待使用者確認）。** 第一輪的「格式不新增 `Status`」改為格式 6 加 `active`。錯了的代價：換庫後孤兒欄位與資料不跟著走，要管理員自己決定是否重建。
23. **使用者套件匯出時主檔歸 `requires.masters`，`requires.apps` 不得列主檔（§4.2、§9；第二輪必修 1，主控裁決，待使用者確認）。**
24. **設計器在主檔上新增的欄位、選項、區塊照所在區塊決定歸屬，建立時寫對照列與持久代號（§5.4；第二輪必修 6，主控裁決，待使用者確認）。** 代號格式 `u`＋8 碼亂碼、跨歸屬拖曳回 409 是我的判斷。孤兒區塊不得新增或拖入欄位（第三輪必修 7，主控裁決，待使用者確認）。
25. **貢獻欄位型別有允許清單，禁用自動編號與彙總，參照禁止 `cascade`（§4.3；第二輪必修 5、準備度 5，第三輪必修 1、2，主控裁決，待使用者確認）。** 型別依同族解讀（§17 問題 6 已裁決）。
26. **品項 `supplyType` 必填、Demo 逐筆指定、只有 `make` 建工令（§5.3；第二輪準備度 4，主控裁決，待使用者確認）。** s2d 不加 `supplyType` 的 `refGuard` 是我的判斷。
27. **管理員另外授予貢獻欄位可見，掛在既有的模組權限 API 與欄位存取區（§6；第二輪準備度 3，主控裁決，待使用者確認）。** 與 Hidden／ReadOnly 並存時收緊優先是我的判斷。
28. **四張新表用代理主鍵或全非空複合鍵、`SourceKind` 加篩選式唯一索引，孤兒另記 `Dynamic_AppOrphan`，唯一鍵（`FormerAppKey`, `Kind`, `Name`）（§8；第二輪準備度 2、建議 6，第三輪必修 4，主控裁決，待使用者確認）。** 孤兒用獨立表而不是在對照表加欄位是我的判斷：對照表主鍵是（`AppId`, `Name`），兩個已刪除的應用程式各留一個同代號欄位時會撞鍵。
29. **主檔區塊順序固定：基本區塊在前，貢獻區塊依應用程式 id 排序（§4.3；第二輪建議 5，主控裁決，待使用者確認）。**

## 17. 待主控裁決的問題（各附建議）

1. **MES 的品項是否強制參照主檔？** 模具廠接單的零件常常是一次性的，要先建品項才能開訂單會增加輸入負擔。**建議：** 訂單明細與工令必填（才能與庫存、CRM 訂單串接），報價明細選填（報價時品項常常還不存在，轉訂單時補）。替代：全部必填（最乾淨，輸入最重）；全部選填（輕，但工令無品項時 s2e 的串接與入庫無法進行）。
2. **統編、付款條件、負責人放基本欄位，還是照範例由會計貢獻？** 範例是「CRM 加產業與來源、進銷存加出貨地址、會計加統編與付款條件」。**建議：** 放基本欄位（整體設計草稿 §2.2 也是這樣定）。理由：報價單、出貨單也要印統編；付款條件 MES 與進銷存已各有一份；Odoo 與 SAP 都在一般資料或共用欄位。沒有更新入口，事後從貢獻升為基本欄位對已開啟的資料庫無效。若堅持放會計貢獻，CRM、MES 報價單就無法引用統編。
3. **CRM、進銷存的「客戶」「供應商」「產品」選單消失，改在「主檔」分區的「往來對象」（客戶、供應商兩個共用檢視）、「聯絡人」「品項」，可以嗎？** 這是使用者看得到的介面變化。**建議：** 接受。選單項目目前只能是模組，沒有「檢視連結」；若要保留各應用程式自己的入口，需要選單項目支援指向共用檢視，是另一個核心能力，建議延後。
4. ~~敏感的貢獻欄位要不要有「僅貢獻者群組可見」的白名單？~~ **已由第一輪審查裁決（必修 2，主控裁決，待使用者確認）：** 要，而且是預設：貢獻欄位只對貢獻者的群組與系統管理員可見，其他群組要管理員授予（§6）。
5. **空系統的定義要不要放寬？** 目前刪除最後一個使用者時保留主檔，所以刪光所有應用程式後仍有 `Closed` 的主檔。**建議：** 接受，並在應用程式管理頁（階段 5）提供「刪除未使用的共用主檔」；不要在刪除應用程式時自動刪。
6. ~~（第二輪新增）貢獻欄位型別清單是字面還是分類？~~ **已裁決（主控裁決，待使用者確認）：** 當成分類，文字、數字、日期各含同族型別（§4.3），所以 MES `website`（`url`）與進銷存 `cost`（`currency`）照原型別貢獻；`cost` 保留金額型別的 `pricing` 遮罩。第三輪另從清單拿掉 `rollup`（第三輪必修 2）。
7. ~~（第二輪新增）逐動作資料範圍只用在主檔模組，還是全部模組？~~ **已裁決（主控裁決，待使用者確認）：** 只用在主檔模組（§6），一般模組維持現行「每群組一個範圍」，避免改變既有模組的有效權限；之後要全面改再另開設計。第三輪補上主檔模組權限頁逐動作設定範圍（第三輪必修 5）。

## 18. 風險與已知限制

1. 沒有套件更新入口：基本欄位開啟後無法由新版套件補欄位，只能客戶自己加。第一個客戶上線前要決定（路線已列）。
2. 舊資料庫不遷移：已開啟的 CRM、MES、進銷存不會自動改用共用主檔；Demo 每次重建所以不受影響。
3. 客戶改過主檔的基本欄位後，後開的使用者套件可能 `unbound`；主檔的基本欄位是約定契約，不是不可改的規則。
4. 貢獻不收規則：貢獻欄位無法依其他欄位的值顯示或隱藏。
5. 貢獻者關閉時其貢獻欄位凍結，依賴寫入管線最底層的共同落地點涵蓋所有寫入路徑（含自動計算，§15.1 #7）。不再有「關閉時轉停用」備案；擋不住的路徑要停下回報，不會退成可寫或看不到。凍結期間貢獻的公式值可能和主檔基本欄位不一致，重開後下一次存檔才更新。
6. 一家公司只有一個地址與一個 `shipAddress`；多個送貨地址列為後續（同整體設計草稿 §9 第 7 題）。
7. 主檔上的欄位多了之後，往來對象的表單變長（每個應用程式一個區塊）；區塊折疊是前端既有能力，若不夠用再議。
8. th、vi 為機器翻譯，待母語者校對。
9. 整體設計草稿尚未審查；它的審查結論若與本文件的差異（§19）不同，以審查結論為準，本文件跟著改。
10. 權限來源記錄讓主檔模組的權限多一層間接：權限頁看到的是重算後的有效值，要知道「這筆是誰給的」得看來源記錄（前端可在階段 5 的應用程式管理頁呈現）。
11. 貢獻者刪除後留下的孤兒欄位不隨主檔匯出；換庫時這些欄位與資料不會跟著走，只在匯出警告列名。接回只在同一個資料庫、同一個應用程式 id 重開時發生。
12. 逐動作資料範圍只用在主檔模組（§6）：同一個使用者在主檔模組與一般模組上，範圍的計算規則不同，權限頁要能說明。
13. 合併匯入時補的基本欄位若撞到貢獻代號會改名避撞，引用原代號的使用者套件開啟時 `unbound`（§9）。
14. 客戶改過的內建應用程式（例如在 CRM 加了欄位）匯不進另一個資料庫：`ImportAppPackage` 對內建套件 id 一律 409（§9）。這是既有限制，和套件更新入口一併決定；往返測試因此改用非內建 id 的測試套件（第三輪必修 6）。主檔套件不受影響，走合併。

## 19. 與整體設計草稿（erp-data-model）的對齊

我讀了草稿 §2、§6.1、§9（分支 `docs/erp-model`，commit 6e9a557），沒有修改它。

**採用草稿的做法（原本不同）：** 聯絡人獨立成共用主檔（我原本用同表加 `parent`）；角色用單一下拉 `role`（我原本用複選，要新能力）；品號用文字 `no`（我原本用自動編號加 `sku`）；模具欄位名 `no`、`name`；主檔的設計器修改要系統管理員；關閉應用程式時貢獻區塊在表單收起。

**與草稿一致：** 往來對象基本欄位（付款條件拆分除外，見下表）、貢獻者欄位（CRM 產業與來源、MES 信用等級與網站）、統編與付款條件與負責人進基本欄位、倉庫與模具與科目不共用、移除模擬實體後保留外部實體機制、欄位歸屬與關閉唯讀、各應用程式替自己的群組宣告權限、代號以應用程式為命名空間且匯出保留代號、不做子地址與變體。

**不同之處與理由：**

| 項目 | 草稿 | 本設計 | 理由 |
|---|---|---|---|
| 基本定義放哪 | 每個套件完整宣告同一份，目錄與開啟時對帳（§6.1 第 2 點、§9 第 4 題） | 獨立的主檔套件，使用者套件用 `requires.masters` 引用 | 客戶開啟後可改主檔，之後才開的套件宣告會與實際模組不一致，「拒絕開啟」或「以誰為準」都說不通；也避免三份重複。基本定義仍由套件宣告，沒有寫進核心 |
| 刪除應用程式時它的欄位 | 只刪它加的欄位；最後一個宣告者刪除才刪模組，仍受安全刪除（§6.1 第 4 點） | 無值的欄位刪除，有值的停用保留、重開時接回；最後一個使用者刪除時主檔保留，另有手動刪除（§7） | 避免「欄位有值就擋住整個應用程式的刪除」，也避免資料遺失；差別只在有值時的處理 |
| 進銷存貢獻的欄位 | 未列 | `category`、`safetyStock`、`cost`、`shipAddress` | `cost` 是為了把進價與售價分開（現況共用 `price`）；`shipAddress` 是派工範例的「進銷存加出貨地址」 |
| 開啟時主檔缺失 | 未寫 | 自動開啟或重新採用，並有 `masterNotAvailable` 的錯誤 | 補足草稿沒寫到的開啟行為 |
| 付款條件 | 一個 `paymentTerms` | 分 `salesPaymentTerms`、`purchasePaymentTerms` | 配合 ERP 資料模型審查：同一家公司兩個方向的條件不同 |
| 品項供應方式 | 未列 | 基本欄位 `supplyType`（自製／外購） | 配合 ERP 資料模型審查；MES 與進銷存都用得到 |
| 貢獻欄位可見範圍 | 未寫 | 預設只對貢獻者群組與系統管理員可見 | 第一輪審查必修 2：fail closed |

## 20. 審查處置

三輪審查都照主控裁決改寫正文（**主控裁決，待使用者確認**）。總原則：fail closed、結果不依開啟順序、往返不遺失。

### 20.1 第一輪

第一輪審查提出 10 項必修、1 項建議，另有 ERP 資料模型審查的兩點配合。「第二輪異動」欄記第二輪裁決改掉或補強的部分，細節見 §20.2。

| # | 問題 | 裁決 | 改寫位置 | 第二輪異動 |
|---|---|---|---|---|
| 必修 1 | 關閉的貢獻者欄位在「檢查失敗仍可寫」備案下仍可寫 | 取消該備案；伺服器端拒寫所有路徑 | §3、§7、§15.1 #7、§15.2、§16 #17、§18 #5 | 鎖移到寫入管線最底層、涵蓋自動計算；「關閉時轉停用」備案取消（第二輪必修 5、建議 3） |
| 必修 2 | 敏感欄位無法限制，後建群組直接看見 | 貢獻欄位預設只對貢獻者群組與系統管理員可見 | §3、§4.3、§6、§8、§12.5、§12.6、§15.1 #12、§16 #9、§17 問題 4、§19 | 補手動授予的 API 與畫面（準備度 3） |
| 必修 3 | 兩個應用程式映射到同一群組時撞鍵、結果依開啟順序 | 權限取聯集，並記來源 | §6、§8、§15.1 #5、§15.2、§16 #10 | 範圍改逐動作取（建議 1）；表改代理主鍵（準備度 2） |
| 必修 4 | 相依檢查把主檔誤判為 CRM 的依賴者 | 關閉與刪除時排除自己的貢獻欄位 | §7、§12.3、§15.1 #11、§15.2、§16 #18 | 無 |
| 必修 5 | 刪除 CRM 時 `Inactive` 的 `fromLead` 擋住刪除 | 參照自己模組的貢獻欄位連值刪除 | §7、§12.6、§15.1 #9、§15.2、§16 #4 | 機制寫明、計入確認數、`Closed` 放行（第二輪必修 3、4） |
| 必修 6 | 匯出時分不出哪些權限屬於哪個應用程式 | 新增權限來源記錄 | §6、§8、§9、§15.2、§16 #10 | 不存群組代號，走 `GroupCode()`（第二輪必修 2） |
| 必修 7 | 改過的內建主檔匯入時 409，無法往返 | 主檔套件匯入改為合併，只補不刪 | §9、§15.1 #14、§15.2、§16 #19 | 依持久代號比對、撞名避撞（第二輪必修 6、建議 2） |
| 必修 8 | 基本與貢獻選項同名撞鍵 | 貢獻選項代號進同一份避撞對照，符號維持兩段 | §4.4、§4.6、§5.4、§8、§10、§15.1 #13、§15.2、§16 #20 | 無 |
| 必修 9 | 轉換目標改成主檔後匯出被略過 | exporter 匯出目標為主檔的轉換 | §4.5、§4.6、§9、§10、§15.1 #4、§15.2 | 主檔改歸 `requires.masters`（第二輪必修 1） |
| 必修 10 | 刪除後殘留的停用欄位匯出時遺漏或變回啟用 | 不匯出、在警告列名；格式不新增 `Status` | §7、§9、§15.2、§16 #22、§18 #11 | 孤兒仍不匯出；客戶停用的欄位改以 `active: false` 匯出（第二輪建議 3） |
| 建議 11 | 潛在客戶轉客戶沒有初始化 `role` | 轉換時設 `role = customer` | §4.5、§11.1、§15.1 #15、§15.2、§16 #21 | 無 |
| ERP 1 | 付款條件只有一欄 | 分銷售、採購兩欄 | §5.1、§5.4、§11.2、§11.3、§12.4、§12.5、§12.6、§16 #7、§19 | 無 |
| ERP 2 | 品項沒有供應方式 | 加 `supplyType` | §5.3、§12.4、§16 #7、§19 | 必填、Demo 種子、建工令條件（準備度 4） |

需要說明的取捨：

- **必修 1 的備案**：第一輪退到「關閉時轉停用」，第二輪建議 3 已取消這個備案；現在擋不住的路徑一律停下回報（§7、§15.1 #7）。
- **必修 6 的「關閉時收回」**我解讀為收回該應用程式授予的寫入類動作、保留讀取，與既有「關閉＝唯讀保留」一致；若原意是全部收回，改一行重算規則即可（§16 #10）。
- **管理員手動修改主檔權限**是裁決沒有直接提到、但來源記錄必須處理的情況：管理員拿掉的動作從所有來源移除，避免重算時長回來（§6）。第三輪必修 5 改為以存檔當下的重算值為基準，只從目前有計入的來源移除（§20.3）。
- **必修 7 的「沒有主檔就建立」**解讀為「資料庫還沒開啟主檔時，合併成匯入的定義並讓它優先於內建」，不在沒有使用者時自行開啟主檔，以維持不變式（§7）。

### 20.2 第二輪

第二輪審查提出 7 項必修、7 項建議、6 項實作準備度，全部照裁決改寫。

| # | 問題 | 裁決 | 改寫位置 |
|---|---|---|---|
| 必修 1 | 匯出把主檔寫進 `requires.apps`，結果依開啟順序 | 主檔歸 `requires.masters`；`requires.apps` 有主檔整份拒絕 | §4.2、§4.6、§9、§15.2、§16 #23 |
| 必修 2 | 貢獻權限與模組權限的群組代號兩套 | 來源記錄只存 `GroupId`，匯出走同一個 `GroupCode()` | §6、§8、§9、§15.2、§16 #10 |
| 必修 3 | CRM 被自己的轉換擋住刪除 | 擁有者在刪除集合、目標是主檔的轉換與事件視為集合內 | §7 刪除第 2 點、§15.1 #9、§15.2、§16 #4 |
| 必修 4 | `fromLead` 連值刪除機制不清、劇本矛盾 | 清除集合豁免阻擋、`ClosedDataGuard` 放行、`Purged` 含清除筆數、劇本不手動停用 | §7 刪除第 4 點、§15.1 #9、§15.2、§16 #4 |
| 必修 5 | 鎖攔不到自動編號、公式、彙總 | 鎖做在寫入管線最底層，衍生值保持最後的值 | §3、§4.3、§7、§12.3、§12.5、§15.1 #7、§15.2、§16 #17、§18 #5 |
| 必修 6 | 設計器在主檔上新增的欄位歸屬未定 | 照區塊決定歸屬，建立時寫持久代號 | §5.4、§8、§9、§10、§15.1 #17、§15.2、§16 #24 |
| 必修 7 | 不變式有直接開啟與遞移關閉兩個破口 | 主檔不能直接開關，狀態依相依順序在同一交易重算 | §3、§4.1、§7、§15.2、§16 #2 |
| 建議 1 | 範圍取較寬會放大寫入範圍 | 範圍逐動作取聯集 | §6、§8、§15.1 #16、§15.2、§16 #10、§17 問題 7、§18 #12 |
| 建議 2 | 合併補欄位可能撞到貢獻代號 | 撞到走 codebook 避撞 | §9 合併第 3 點、§15.2、§16 #19、§18 #13 |
| 建議 3 | 匯出漏掉停用狀態 | 取消轉停用備案；依歸屬匯出；主檔欄位加 `active: false` | §4.1、§4.6、§7、§9、§15.1 #7、§16 #22 |
| 建議 4 | 前端「能改卻存不了」 | 凍結欄位顯示唯讀並提示原因 | §7、§8、§15.2、§16 #17 |
| 建議 5 | 區塊順序依開啟順序 | 基本區塊在前，貢獻區塊依應用程式 id | §4.3、§11.5、§15.2、§16 #29 |
| 建議 6 | 刪除再重開不接回舊資料 | 依應用程式 id＋代號接回孤兒 | §6、§7 刪除第 5、6 點、§8、§12.6、§15.2、§16 #4、§18 #11、§19 |
| 建議 7 | 循環檢查沒納入主檔的邊 | `requires.masters` 納入匯入與目錄載入的相依圖 | §4.2、§9、§15.2 |
| 準備度 1 | s2d-0 邊界與 migration 衝突 | s2d-0 只做模具，自帶 `RemoveMockMold`；客戶移到 s2d-1；驗證放 s2d-1 開頭 | §8、§11.2、§11.4、§13、§15.3 |
| 準備度 2 | 兩張新表的鍵在 EF Core 不可行 | 代理主鍵、`SourceKind`、篩選式唯一索引 | §8、§16 #28 |
| 準備度 3 | 手動授予可見沒有入口 | 掛在既有模組權限 API 與欄位存取區 | §6、§15.2、§16 #27 |
| 準備度 4 | `supplyType` 沒落到遷移與 Demo | 必填、Demo 逐筆指定、只有 `make` 建工令 | §5.3、§11.1、§11.5、§14.1、§15.2、§16 #26 |
| 準備度 5 | 貢獻欄位型別沒有清單 | 列出允許型別，禁 autonumber | §4.3、§4.6、§15.2、§16 #25、§17 問題 6 |
| 準備度 6 | 備案的選擇沒有記錄點 | #4、#7、#14 走備案前停下回報 | §7、§9、§15.1、§15.3 |

需要說明的取捨：

- **準備度 5 的型別名稱**我對應成同族的實際型別，否則現有的 `website`、`cost` 會被擋；列為 §17 問題 6。
- **建議 1 的逐動作範圍**需要新表與 `PermissionResolver` 改動，我限定只用在主檔模組；列為 §17 問題 7 與 §15.1 #16，備案會收窄資料，比照準備度 6 先回報。
- **建議 6 的接回**需要在應用程式刪除後仍記得前擁有者，所以另加 `Dynamic_AppOrphan`；孤兒仍不匯出，和建議 3「依歸屬匯出」一致（擁有者已不存在）。
- **必修 3** 裁決只提轉換與事件；檢查若以主檔為引用比照處理，是我的判斷。

### 20.3 第三輪

第三輪審查提出 7 項必修，另有兩點實作注意與 §17 問題 6、7 的整理，全部採用審查者建議的修法，照主控裁決改寫（**主控裁決，待使用者確認**）。第三輪之後不再整輪審查，由主控抽查這 7 處。

| # | 問題 | 裁決 | 改寫位置 |
|---|---|---|---|
| 必修 1 | 貢獻參照的 `onDelete` 沒規定 | 禁 `cascade`；`fromLead` 改 `setNull`；清空參照列入凍結路徑 | §4.3、§4.6、§7 凍結第 5 點、§7 刪除第 4 點、§15.1 #7、§15.2、§16 #4、#25 |
| 必修 2 | 貢獻 `rollup` 寫不出合法引用；豁免不全 | 拿掉 `rollup`；豁免全部阻擋類型 | §4.3、§4.6、§7 凍結第 2 點、§7 刪除第 4 點、§15.1 #9、§15.2、§16 #25、§17 問題 6 |
| 必修 3 | `stockOut` 改指主檔後解析不到 | 驗證、目錄、`IntegrationWorld`、`ConfigOf` 解析主檔 | §4.4、§4.6、§10、§11.2、§13、§14.2、§15.1 #2、§15.2、§15.3、§16 #15 |
| 必修 4 | 孤兒表唯一索引會撞 | 加 `Kind`；型別不同沿用原代號、刪舊孤兒列 | §7 刪除第 5、6 點、§8、§15.2、§15.3、§16 #4、#28 |
| 必修 5 | 權限頁存檔會放大範圍、吃掉關閉中的授予 | 以重算值為基準取差異；主檔逐動作範圍 | §6、§8、§15.2、§16 #10、§17 問題 7 |
| 必修 6 | 往返測試匯入內建套件會 409 | 改用非內建 id 的測試套件 | §9、§15.2、§18 #14 |
| 必修 7 | 孤兒區塊的新增欄位歸屬未定 | 孤兒區塊新增、拖入、拖出都 409 | §5.4 第 6 點、§7 刪除第 7 點、§15.2、§16 #24 |
| 實作注意 1 | 凍結鎖做成資料層會擋 `FieldPurge` | 不做成攔截器 | §7 凍結第 1 點、§15.1 實作注意、§15.2 |
| 實作注意 2 | `DeleteAppHandler` 會 cascade 刪對照列 | 孤兒列在前建、權限在後重算 | §7 刪除第 5 點、§15.1 實作注意 |
| 整理 | §17 問題 6、7 已裁決 | 改成「已裁決」 | §4.3、§6、§17 |

需要說明的取捨：

- **必修 1** 審查給了兩個選項，裁決採 `setNull`，不在 `ReferenceDeletePlanner` 為貢獻欄位開例外；清除集合因此只剩「移除欄位本身」與殘留值兜底的用途。指向另一個主檔的貢獻參照仍可用 `restrict`，代價是別的應用程式刪那筆主檔記錄時會被擋，符合 `restrict` 原意。
- **必修 3** 審查也提了「`stockItem` 移出整合、改成 MES 一般參照」的較小改法，裁決採核心解析主檔，所以 §14.2「`stockOut` 只調整目標」維持不變。
- **必修 4** 改掉第二輪「型別不同另建新欄位、代號走避撞」的我的判斷。
