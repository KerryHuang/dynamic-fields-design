# 動態模組與自訂欄位 設計文件

> 2026-11-03｜目錄 `dynamic-fields-design`｜前置研究：[vtiger 研究](../../research/vtiger-dynamic-fields.md)
> 標「（我的判斷）」的是我代為決定、可以改的；其餘是使用者或 PO 已拍板的決策。

## 1. 意圖

管理員新增一個功能（模組），在上面加區塊與自訂欄位，系統就自動長出這個功能：列表、新增、編輯、明細、篩選、排序、權限、關聯、歷程，**不用替每個功能寫程式**。先做成獨立 POC，之後成為獨立的正式產品（每家客戶一套部署，資料庫由客戶選擇），所以 POC 從一開始就要把分層守好，資料庫相關的部分集中在一層。

## 2. 已定決策

| 決策 | 內容 | 來源 |
|---|---|---|
| 用途 | 使用者輸入型的自訂欄位與模組 | 使用者 |
| 範圍 | 設計與 POC 都做到最複雜的情境 | 使用者、PO |
| POC 資料庫 | SQLite | 使用者 |
| 權限 | 完整角色權限：模組、動作、欄位、資料範圍 | 使用者 |
| 關聯 | 參照欄位＋反向關聯清單＋刪除連帶處理 | 使用者 |
| 架構 | 獨立產品；POC 先守好分層，正式版再換資料庫與登入 | PO |

選 SQLite 的理由是 POC 只是暫時用的；正式版要換成客戶選擇的主流資料庫（如 SQL Server），所以資料庫方言必須集中在一層，不可以散落各處。

## 3. 目標、非目標、反目標

**目標（POC 全部要做）**

1. **模組管理**：
   - 名稱多語系、圖示、選單分區。
   - 記錄標題欄位、自動編號規則。
   - 狀態（草稿、啟用、停用）。
2. **區塊與欄位管理**：
   - 新增、排序、停用、清除。
   - 20 種欄位型別（§7），含公式、自動編號、參照、使用者。
3. **選項**：多語系標籤、顏色、停用；依權限群組限定可選的選項；下拉連動。
4. **規則**：條件顯示、條件必填、條件唯讀、跨欄位驗證（§8）。
5. **關聯**：
   - 參照欄位的目標可以是動態模組，也可以是外部系統的既有實體。
   - 明細頁有反向關聯清單。
   - 刪除時可選限制、清空或連帶刪除（§9）。
6. **擴充既有實體**：替外部系統的既有實體（如客戶、模具）加自訂欄位，值存在動態儲存區，不碰外部系統的資料表。POC 用一個模擬的既有實體示範。
7. **列表**：
   - 欄位選擇、依型別的篩選運算子、多欄排序、伺服器分頁。
   - 儲存清單檢視（個人與共用）。
8. **權限**：
   - 模組 × 八個動作（沿用 ProgramCode 權限模型）。
   - 欄位層的隱藏與唯讀。
   - 資料範圍（全部、本人、同群組）。
   - 金額遮罩（Pricing）。
9. **稽核**：建立與修改的人和時間；欄位層級的變更歷程。
10. **匯出與匯入**：CSV／Excel 匯出依權限與遮罩；CSV 匯入走同一套驗證。
11. **metadata 版本**：版本號、快取、併發修改偵測。
12. **模組範本**（§13.1）：
    - 站台啟動時載入內建範本，管理員可以「從範本建立」模組。
    - 任何模組都可以匯出成範本、再匯入。
    - 內建第一個範本是「訂單」（表頭＋明細）。
13. **表頭與明細**：子模組以參照欄位掛在父模組下，父模組的明細頁可以直接逐列編輯子記錄，表頭和明細在同一個交易存檔（§9）。
14. **彙總欄位**：父記錄依子記錄計算加總、筆數、最小、最大（§7、§8）。

**非目標**

- 工作流程、觸發器、通知、核准流程（Approve 軸先保留不用）。
- 拖拉式報表或 BI。
- 公式寫任意 SQL；彙總欄位以外的跨記錄計算（例如查另一個不相關模組的值）。
- 正式的身分驗證（如 OIDC）（POC 用模擬身分，§13）。

**反目標（做了算失敗）**

- 對外部系統的資料表寫入或改結構。
- 執行期 DDL：每加一個欄位或模組就建表、加欄。
- 權限 fail-open：模組或代碼找不到時放行。
- 前端替個別模組手寫頁面。
- 驗證只做在前端。

## 4. 限制

以下是設計時要遵守的限制。

- **外部系統的表凍結**：動態功能的表全部放在獨立的 schema `Dynamic`，不改動外部系統的資料表。表結構的變更一律走 EF migration。
- **每客戶一庫、一部署**：metadata 和資料都在客戶自己的庫裡，沒有跨租戶問題。但建表與種子資料要能在每個客戶庫重複執行（existence guard）。
- **SQL Server 最低版本未定**（文件上 2008 與 2019+ 都有出現）：設計以 2008 為底線，**資料庫端不使用任何 JSON 函式**，JSON 只在應用層解析。
- **API 必須走生成契約**：前端禁止手寫 DTO，所以動態記錄要用一個固定形狀的泛型 DTO（§11.3）。
- **權限唯一來源是 ProgramCode**：上限 8 碼、七軸 OR 合併、全域 filter 判定。動態模組必須登記代碼，而且動態端點自己要 fail-closed。
- **前端路由與選單全靜態**：用固定的通用路由承載所有動態模組；選單的動態入口是前端唯一需要改動殼層的地方（§12.2）。
- **前端 UI 只能組 framework 元件**；**i18n 是 build-time 的**，使用者自訂的文字要由 metadata 自帶多語系。

## 5. 架構總覽

```
[設計器] 管理員 ──► 模組／區塊／欄位／選項／規則／權限 metadata（Dynamic.* 固定結構表）
                                 │  版本號 + 快取
                                 ▼
[執行期] 使用者 ──► 通用頁殼（列表／表單／明細）──► 通用 API ──► Record Service
                     ▲ 依 metadata 渲染                        │ 依 metadata 驗證、算公式、套權限
                     │ 型別 → framework 元件                     ▼
                     └──────────── schema API ◄────── Dynamic.Record（值 JSON）
                                                       Dynamic.RecordIndex（可篩選值）
                                                       Dynamic.RecordHistory（歷程）
```

前後端各有一份**型別處理器註冊表**和一份**規則引擎**。後端是權威，前端用來即時互動；兩邊共用同一份規則語法。

## 6. 資料模型

### 6.1 儲存策略：JSON 值 + 型別化索引表（我的判斷）

**每筆記錄的完整值存成一欄 JSON**（`Record.ValuesJson`），**需要篩選、排序、關聯的欄位，另外寫進型別化的索引表**（`RecordIndex`）。所有表的結構都固定，加欄位或加模組都不需要 DDL。

不照 vtiger 的實體欄位作法，原因有三：

- 表結構的變更必須走 EF migration，而且每個客戶各自部署；執行期 DDL 會讓各客戶的結構和原始碼脫鉤。
- 實體欄位會撞到寬表的單列大小上限。
- 每次加欄都是一次線上 DDL。

也不只用 JSON，原因是 SQL Server 2008 沒有 `JSON_VALUE`，資料庫端無法篩選 JSON 內容。

索引表就是 EAV 的優點（結構固定、能建索引），但只用在需要查詢的欄位上；主資料仍然是一列一筆記錄，讀取明細不用 PIVOT。代價是寫入時要同步維護索引列，而且 BI 不能直接 SELECT 出寬表（§15 風險 6）。SQLite、SQL Server 2008、PostgreSQL 都支援這個方案，上線時只換方言層。

### 6.2 表

全部放在 `Dynamic` schema；SQLite 沒有 schema，用表名前綴 `Dynamic_` 代替。每張表都有稽核欄位 `CreatedUserId`、`CreatedTime`、`ModifiedUserId`、`ModifiedTime`；需要軟刪除的表加 `DeleteFlag`，沿用常見的慣例。

**metadata**

| 表 | 主要欄位 |
|---|---|
| `Module` | `Id`、`Key`、`ProgramCode`、`NameI18n`、`Icon`、`Section`、`TitleFieldId`、`Kind`、`HostEntityKey`、`Status`、`Version` |
| `Block` | `Id`、`ModuleId`、`LabelI18n`、`Sequence`、`Collapsed` |
| `Field` | `Id`、`ModuleId`、`BlockId`、`Key`、`LabelI18n`、`Type`、`Config`、`Sequence`、`Status` |
| `FieldOption` | `Id`、`FieldId`、`LabelI18n`、`Color`、`Sequence`、`Active` |
| `FieldOptionScope` | `OptionId`、`GroupId` |
| `Rule` | `Id`、`ModuleId`、`Kind`、`Condition`、`Targets`、`MessageI18n`、`Sequence`、`Active` |
| `NumberSequence` | `ModuleId`、`FieldId`、`Prefix`、`Period`、`NextValue` |
| `ListView` | `Id`、`ModuleId`、`OwnerUserId`、`NameI18n`、`Columns`、`Filter`、`Sort`、`IsDefault` |

欄位補充：

- `Module.Kind` 有兩種值：`Standalone`（獨立模組）與 `Extension`（擴充既有實體，`HostEntityKey` 指向既有實體，例如 `design.mold`）。
- `Module.Status` 有三種值：草稿、啟用、停用。只有啟用的模組會出現在選單和通用 API。
- `Module.Version` 在任何 metadata 異動時遞增，作為快取鍵與 ETag。
- `Field.Key` 由系統產生（`f_<id>`），不讓使用者輸入；畫面上顯示的是 `LabelI18n`。
- `Field.Config` 是 JSON，依型別存放必填、唯讀、長度、小數位、預設值、參照目標、公式、`IsFilterable`、`IsSortable`、`ShowInList` 等設定。
- `FieldOption` 的停用不刪除；既有記錄仍然顯示停用的選項，只是新資料不能選。
- `FieldOptionScope` 只存例外：沒有任何列代表所有群組都能選。
- `ListView.OwnerUserId` 為 null 代表共用檢視；`Columns`、`Filter`、`Sort` 一律存 `Field.Id`，不存字串鍵。
- 所有 `*I18n` 欄位都是 JSON，格式 `{ "zh-TW": "...", "en-US": "...", "th": "...", "vi": "..." }`，缺語系時退回 zh-TW。

**資料**

| 表 | 主要欄位 |
|---|---|
| `Record` | `Id`、`ModuleId`、`HostKey`、`Title`、`OwnerUserId`、`ValuesJson`、`RowVersion`、`DeleteFlag` |
| `RecordIndex` | `RecordId`、`FieldId`、`Seq`、`TextValue`、`NumberValue`、`DateValue`、`RefKey` |
| `RecordHistory` | `Id`、`RecordId`、`FieldId`、`Operation`、`OldValue`、`NewValue`、`ChangedUserId`、`ChangedTime` |

- `Record.Id` 是全系統唯一的 `bigint`，跨模組參照只需要 id。
- `Record.HostKey` 只用在擴充模組，存既有實體的主鍵字串；`(ModuleId, HostKey)` 唯一。
- `Record.Title` 是記錄標題欄位的去正規化副本，給參照顯示、搜尋、反向關聯清單用。
- `Record.ValuesJson` 格式為 `{ "f_12": "...", "f_13": 3.5, "f_14": ["opt_3","opt_7"] }`；選項存選項 id，參照存目標記錄 id 或 HostKey。
- `Record.RowVersion` 做樂觀併發，衝突回 409。
- `RecordIndex` 主鍵是 `(RecordId, FieldId, Seq)`；`Seq` 給多選。索引建在 `(FieldId, TextValue)`、`(FieldId, NumberValue)`、`(FieldId, DateValue)`、`(FieldId, RefKey)`。只有 `IsFilterable`、`IsSortable`、參照、多選欄位會寫進索引表。
- `TextValue` 是 `nvarchar(440)`（索引鍵連同 FieldId 共 888 bytes，低於 SQL Server 2008 的 900-byte 上限，見 §15）；超過長度只截取前段，篩選前段即可。
- `RecordHistory` 只記實際有變的欄位；值存成 JSON 字串。

**權限**（正式版的對應見 §14）

| 表 | 主要欄位 |
|---|---|
| `Program` | `ProgramCode`、`NameI18n`、`IsBeta`、`Active` |
| `PermissionGroup`、`UserGroupMapping` | 群組與成員 |
| `GroupProgramPrivilege` | `GroupId`、`ProgramCode`、`CanRead`、`CanAdd`、`CanEdit`、`CanDelete`、`CanPrint`、`CanAmt`、`CanApprove` |
| `UserProgramPrivilege` | `UserId`、`ProgramCode`，以及與 `GroupProgramPrivilege` 相同的 7 個 `Can*` 欄位（個人例外權限，與群組權限逐軸 OR） |
| `FieldPermission` | `FieldId`、`GroupId`、`Access`（`Hidden`／`ReadOnly`） |
| `ModuleDataScope` | `ModuleId`、`GroupId`、`Scope`（`All`／`Own`／`Group`） |

## 7. 型別系統

型別只用一個列舉。每個型別在前後端各註冊一個處理器：

- **後端** `IFieldTypeHandler`：驗證、正規化、產生索引值、顯示值、可用的篩選運算子、匯入解析。
- **前端**：輸入元件、zod 片段、表格格式化、篩選元件。

| 型別 | JSON 值 | 索引欄 | framework 元件 |
|---|---|---|---|
| `text` | string | Text | TextInput |
| `textarea` | string | 不索引 | TextareaInput |
| `integer` | number | Number | MoneyNumberInput（0 位小數） |
| `decimal` | number | Number | MoneyNumberInput |
| `currency` | number | Number | MoneyNumberInput |
| `percent` | number | Number | MoneyNumberInput |
| `date` | `yyyy-MM-dd` | Date | DateSelect |
| `datetime` | ISO 8601 | Date | DateSelect＋TimeInput |
| `time` | `HH:mm` | Text | TimeInput |
| `boolean` | bool | Number | ToggleInput |
| `email`、`phone`、`url` | string | Text | TextInput（格式驗證） |
| `enum` | 選項 id | Text | GenericSelect |
| `multienum` | 選項 id 陣列 | Text（多列） | GenericSelect（多選） |
| `reference` | 記錄 id 或 HostKey | RefKey | PopupSelect |
| `user` | 使用者 id | RefKey | GenericSelect |
| `autonumber` | string | Text | 唯讀顯示 |
| `formula` | number／date | Number／Date | 唯讀顯示 |
| `rollup` | number／date | Number／Date | 唯讀顯示 |

`email`、`phone`、`url` 三種在表上合併成一列，所以列表是 18 列、21 種型別。

規則：

- **型別建立後不可以改**，照 vtiger。要換型別就新增欄位再搬資料（我的判斷）。
- `currency` 欄位受 **Pricing 軸**控制：沒有 Pricing 權限的使用者，API 回 null、匯出也是空白，和一般的金額遮罩做法一致。
- 文字長度用「字元數」限制。`Dynamic` schema 一律用 `nvarchar`，沒有 dbo 那種 Big5 位元組的問題。長度以字元數計（UTF-16 code unit，與 SQL Server `nvarchar` 一致）。
- 通用 FieldRenderer 需自行實作並放進元件庫（§15 風險 2）。

## 8. 規則引擎

一份 JSON 條件語法，前後端各實作一個求值器，用同一份測試案例驗證兩邊結果一致。

```json
{ "all": [ { "field": 12, "op": "eq", "value": "opt_3" },
           { "any": [ { "field": 15, "op": "gt", "value": 1000 },
                      { "field": 16, "op": "isEmpty" } ] } ] }
```

- 運算子依型別提供：`eq`、`ne`、`in`、`notIn`、`gt`、`gte`、`lt`、`lte`、`between`、`contains`、`startsWith`、`isEmpty`、`isNotEmpty`；日期另有相對日期，例如 `今天起 N 天內`。
- 比較對象可以是常數，也可以是另一個欄位（`{"fieldRef": 13}`），跨欄位驗證就靠這個。

| `Rule.Kind` | 條件成立時 |
|---|---|
| `Visibility` | 目標欄位或區塊顯示（不成立就隱藏） |
| `Required` | 目標欄位必填 |
| `ReadOnly` | 目標欄位唯讀 |
| `Validation` | 條件**不**成立時，存檔失敗並顯示訊息 |
| `OptionFilter` | 目標下拉只剩指定選項（下拉連動） |

求值規則：

- 前端每次值變動就重算一次，用來控制畫面；後端存檔時重算一次，作為權威結果。
- 被規則隱藏的欄位：值保留不清除，必填檢查也略過（我的判斷：避免使用者切換條件時資料不見）。
- 規則的優先序：權限的隱藏與唯讀 > 規則 > 欄位預設設定。權限隱藏的欄位，規則不能讓它顯示。

**公式**（`formula` 型別）：

- 運算式只允許同一筆記錄的數字或日期欄位、四則運算、`ROUND`、`IF`、`DATEDIFF`、`DATEADD`、`TODAY`。
- 存檔時由後端解析成 AST 再求值，結果寫進 JSON 和索引表；不翻成 SQL。
- 建立公式時檢查引用循環與型別。
- `TODAY` 相關的公式只在存檔時計算，不會每天自動重算（我的判斷）。

**彙總**（`rollup` 型別，我的判斷）：

- 設定為「子模組的哪個參照欄位指向我」＋「彙總子模組的哪個欄位」＋函數（`SUM`、`COUNT`、`MIN`、`MAX`），可以再加 §8 的條件，只彙總符合條件的子記錄。
- 子記錄新增、修改、刪除時，在同一個交易內重算父記錄的彙總值，寫回父記錄的 JSON 與索引表，並遞增父記錄的 `RowVersion`。
- 彙總值可以再被父記錄的公式引用，例如「稅額 = 合計 × 5%」。計算順序是：先算子記錄的公式，再算父記錄的彙總，最後算父記錄的公式。
- 只支援一層，也就是直接子記錄；不支援孫記錄的彙總，避免連鎖重算。

**自動編號**：

- 格式例如 `REQ-{yyyyMM}-{0000}`，可依年或月重置。
- 在寫入交易內取號；SQL Server 用 `UPDLOCK`，SQLite 本來就只有單一寫入者。

## 9. 關聯

**參照目標**有兩類：

- **動態模組**：存目標 `Record.Id`。
- **外部系統的既有實體**：存既有實體的主鍵字串。這類目標要先在後端「參照提供者註冊表」登記，每個提供者提供 light 查詢（用來選取與顯示）和存在檢查。POC 用模擬實體示範；正式版對接外部系統的 `GET .../light` 端點。

**反向關聯清單**：

- 明細頁替每個「指向本模組的參照欄位」各開一個分頁。
- 用 `RecordIndex WHERE FieldId=? AND RefKey=?` 查來源記錄，套用來源模組的權限與資料範圍。
- 分頁可以直接新增一筆來源記錄，參照欄位自動帶入目前這筆記錄。

**表頭與明細**（我的判斷）：

- 子模組的參照欄位設為 `Config.relation = "detail"` 時，這個子模組就是父模組的「明細」。
- 明細模組不單獨出現在選單；父記錄的新增、編輯頁裡，明細區塊是一張可以逐列編輯的表格（`InlineEditableDataTable`），欄位依子模組的 metadata 渲染。
- 存檔時表頭和所有明細列一起送出：`POST/PUT /records` 的 body 帶 `details: { "<子模組 key>": [ {id?, values, rowVersion?, _delete?} ] }`。後端在同一個交易內寫入，所以任何一列驗證失敗，整張單都不存。
- 明細的刪除連帶固定為 `Cascade`；明細的權限沿用父模組的 ProgramCode，不另外登記代碼。
- 一般的參照（`relation = "lookup"`）照下面的反向關聯清單處理，各自存檔。

**刪除連帶**：由參照欄位的 `Config.onDelete` 決定（我的判斷：預設 `Restrict`）。

| 設定 | 目標記錄被刪除時 |
|---|---|
| `Restrict` | 還有來源記錄就拒絕刪除，回 409 並列出來源 |
| `SetNull` | 清空來源記錄的參照值，並寫進歷程 |
| `Cascade` | 來源記錄一併軟刪除（遞迴，有深度上限） |

既有實體被刪除時，是由外部系統自己處理，動態系統收不到事件。所以指向既有實體的參照，在顯示時會檢查目標是否還存在，不存在就標示「已失效」（§15 風險 7）。

## 10. 權限

**模組與動作**：

- 每個動態模組在建立時登記一個 ProgramCode（8 碼，格式 `DYN` 加 5 位數，例如 `DYN00012`，我的判斷），和 `Module.Status` 在同一個交易內寫入。
- 設計器本身也有一個代碼 `DYN00000`：Add／Edit／Delete 軸控制誰能建模組、改欄位。不要只限 MIS，「完整角色權限」要求這件事也能分派。

| 動作軸 | 動態模組用途 |
|---|---|
| Read | 列表、明細、反向關聯、light 查詢 |
| Add | 新增、匯入 |
| Edit | 修改 |
| Delete | 刪除 |
| PrintExport | 匯出 |
| Pricing | 看得到 `currency` 欄位 |
| Approve、CloseOverride | 保留，先不使用 |

- 有效權限：個人權限 ∪ 群組權限，逐軸 OR。
- 通用端點無法用靜態的 `[RequireProgram]`，改在 handler 前由 `DynamicAccessGuard` 判定：先由 `moduleKey` 查出 ProgramCode，再呼叫權限判定服務判定動作。
- **fail-closed**：模組不存在、未啟用、或代碼不在目錄中，一律回 404 或 403。這和全域 filter 對未知代碼放行的行為不同，是刻意的設計（反目標第 3 條）。

**欄位層**（`FieldPermission`）：

- 預設依模組權限；只記錄例外，`Hidden` 或 `ReadOnly`。
- 使用者屬於多個群組時，**限制優先**：任一群組設為隱藏就隱藏（我的判斷）。原因是欄位權限只存「限制」這種例外；如果用 OR 合併，只要有一個群組沒設例外就會放行，這張表就形同虛設。這和動作軸的 OR 合併方向相反，上線前要跟權限子系統的負責人確認（§15 風險 4）。
- 隱藏的欄位：API 不回傳、不能寫入、不能篩選或排序、匯出不含；schema API 也不列出這個欄位。

**資料範圍**（`ModuleDataScope`）：

- `All`：所有記錄。
- `Own`：`OwnerUserId` 是自己的記錄。
- `Group`：擁有者和自己在同一個群組的記錄。
- 多群組時取最寬的範圍（和動作軸一致）。
- 列表、明細、反向關聯、匯出、light 查詢都要套用。

**選項範圍**（`FieldOptionScope`）：限定某些選項只有特定群組能選。既有值仍然照常顯示。

MIS 管理員和服務帳號的放行規則，沿用同一條判定鏈。

## 11. 後端設計

### 11.1 分層

| 層 | 位置 | 內容 |
|---|---|---|
| Domain | `Entities/Dynamic/`、`Repositories/Dynamic/` | 實體、Repository 介面、型別處理器介面、規則 AST |
| Application | `Dynamic/{ModuleService,FieldService,RuleService,RecordService,ViewService,PermissionService}/Commands|Queries` | MediatR Request/Handler、Validator |
| Persistence | `Repositories/Dynamic/`、`Dynamic/Dialects/` | EF 寫入、Dapper 查詢、`ISqlDialect` |
| WebAPI | `Controllers/v3/Dynamic/` | `[Area("dynamic")]` |

- 寫入一律實作 `ITransactionalRequest`，回傳 `ServiceResult`。
- 時間一律用 `IBusinessTime`。
- 使用者取自 `ICurrentUser`；POC 用模擬實作。

### 11.2 端點

基底路徑 `/api/v3/dynamic`。

| 用途 | 端點 |
|---|---|
| 模組設計 | `GET/POST /modules`、`GET/PUT/DELETE /modules/{key}`、`POST /modules/{key}/activate` |
| 區塊、欄位、選項 | `/modules/{key}/blocks`、`/modules/{key}/fields`、`/fields/{id}/options` |
| 規則、權限設定 | `/modules/{key}/rules`、`/modules/{key}/permissions` |
| 執行期 schema | `GET /modules/{key}/schema` |
| 選單 | `GET /menu` |
| 記錄 | `GET /modules/{key}/records/paged`、`GET /records/{id}`、`POST /records`、`PUT /records/{id}`、`DELETE /records/{id}` |
| 參照選取 | `GET /modules/{key}/records/light` |
| 反向關聯 | `GET /records/{id}/related/{fieldId}/paged` |
| 歷程 | `GET /records/{id}/history` |
| 清單檢視 | `/modules/{key}/views` |
| 匯出、匯入 | `GET /modules/{key}/export`、`POST /modules/{key}/import` |

- schema API 回傳**已套用目前使用者權限**的 metadata，帶 ETag（`Module.Version` 加權限版本）。
- 選單 API 只回目前使用者有 Read 權限的啟用模組。
- 匯入先做驗證預覽，確認後才寫入。

### 11.3 契約

```csharp
public sealed class DynamicRecordResponse {
    public long Id { get; init; }
    public string ModuleKey { get; init; }
    public string Title { get; init; }
    public Dictionary<string, JsonElement?> Values { get; init; }      // key = Field.Key
    public Dictionary<string, LightRef> References { get; init; }      // 參照欄位的 {id,title}
    public string OwnerUserId { get; init; }
    public string CreatedUserId { get; init; } public DateTime CreatedTime { get; init; }
    public string ModifiedUserId { get; init; } public DateTime ModifiedTime { get; init; }
    public string RowVersion { get; init; }
}
```

- 外層形狀固定，所以 Swashbuckle 能產生 OpenAPI，前端也能照規定生成 client。`Values` 的內容由 schema API 在執行期描述，前端再用 metadata 產生 zod 驗證。
- 參照欄位回巢狀的 light 物件（放在 `References`）。
- 分頁參數沿用 `CurrentPage`、`PagesSize`、`SortBy[]`、`SortOrder[]`，回傳 `IPagedResponse<DynamicRecordResponse>`。
  - `SortBy` 填 `Field.Key`，必須是 `IsSortable` 的欄位，這等同 `EntitySortWhitelist`。
  - 篩選參數 `filter` 用 §8 的條件語法，必須是 `IsFilterable` 的欄位。
- 錯誤沿用 `ServiceResult` 對 400、404、409 的映射。欄位驗證錯誤回 `ValidationResponseModel`，鍵是 `Field.Key`。

### 11.4 寫入流程（`SaveRecordHandler`）

1. 由快取取得模組 metadata（版本不符就重載）與使用者的有效權限。
2. 丟掉使用者無權寫入的欄位：隱藏、唯讀、規則唯讀、系統欄位。送了這些欄位不算錯，直接忽略（我的判斷）。
3. 各型別處理器做正規化與驗證，再跑規則引擎：必填、跨欄位驗證、選項範圍、參照存在檢查。
4. 計算公式，新增時取自動編號。
5. 比對 `RowVersion`，寫入 `Record`、重建該筆記錄的 `RecordIndex`，並寫 `RecordHistory`。這三件事在同一個交易內。
6. commit 之後再發 `DynamicRecordChanged` 事件，保留給未來的工作流程使用。

### 11.5 查詢

- 列表用 Dapper，由 `DynamicQueryBuilder` 組參數化 SQL：
  - 每個篩選條件轉成一個 `EXISTS (SELECT 1 FROM RecordIndex WHERE RecordId=r.Id AND FieldId=@f AND NumberValue > @v)`。
  - 每個排序欄位轉成一次 `LEFT JOIN RecordIndex`。
  - 資料範圍轉成 `OwnerUserId IN (...)`。
- 欄位 id 和值一律走參數，SQL 結構只依 metadata 白名單組成，不接受任何使用者字串。
- 方言層 `ISqlDialect` 負責分頁語法與上鎖語法：SQLite 用 `LIMIT/OFFSET`，SQL Server 2008 用 `ROW_NUMBER()`。
- 這裡需要用到 `QueryAsync<dynamic>`。rule 46 允許 dynamic schema 例外，但呼叫處必須註明理由。

### 11.6 快取

- metadata 以 `(ModuleKey, Version)` 為鍵放在 `ICache`；上線後用 Redis，多台機器才能共享。
- 設計異動時遞增 `Module.Version`，舊的快取鍵自然失效。
- 使用者有效權限的快取：鍵是使用者，權限異動時清除。

## 12. 前端設計

### 12.1 歸屬與路由

- 新增一個 context `dynamic`，有自己的 manifest、i18n namespace `dynamic`、API tag `dynamic`。它只依賴 `platform` 與 `shared`。
- 新 context 會改動 context 依賴的 baseline，要先徵得前端負責人同意（§15 風險 1）。
- **固定路由**，只加一次，之後不再隨模組增加：

| 路由 | 頁面 |
|---|---|
| `/dynamic/:moduleKey` | 通用列表 |
| `/dynamic/:moduleKey/new` | 通用新增 |
| `/dynamic/:moduleKey/:id` | 通用明細（含反向關聯分頁、歷程） |
| `/dynamic/:moduleKey/:id/edit` | 通用編輯 |
| `/system/dynamic-modules` | 模組設計器 |
| `/system/dynamic-modules/:moduleKey` | 欄位、規則、權限設計 |

- 分層照前端規則：`domain/dynamic`（型別、條件求值器）、`application/dynamic/queries`（TanStack Query hooks）、`presentation/dynamic`（渲染元件）、`pages/dynamic/*`。

### 12.2 選單與權限

- 側邊欄新增一個分區「自訂功能」，子項目在執行期由 `GET /dynamic/menu` 產生。這是唯一需要改殼層的地方：現在的側邊欄是靜態分類，要讓它接受執行期的項目，可能需要改 framework。
- 路由守衛：
  - 設計器的頁面用靜態的 `programCodes: ['DYN00000']`。
  - 通用頁面的 programCode 在執行期由 schema 取得，再呼叫 `usePrivilegeStore.hasPermission(code, action)`。這是「programCodes 靜態宣告」規則的例外，需要前端負責人同意（§15 風險 1）。
- 按鈕權限沿用 `usePageActionPermissions(code)`；欄位權限由 schema 決定，因為後端已經把隱藏欄位拿掉了。

### 12.3 渲染

- `useDynamicSchema(moduleKey)`：TanStack Query，staleTime 長，用 ETag 驗證版本。
- **型別註冊表** `fieldTypeRegistry`：`type → { component, zod(field), format(value), filterInput, operators }`，可覆寫。
- **表單**：
  - `buildZodSchema(schema)` 依欄位設定產生 zod，用 `superRefine` 疊加規則引擎的條件必填與跨欄位驗證，經 `toTypedSchema` 交給 `useRForm`。這是 `mold-form-schema.ts` 先例的通用版。
  - 區塊用 `SectionedForm`。
  - 規則求值器監看表單值，控制顯示與唯讀。
- **列表**：
  - `DataTable` 的欄位定義由清單檢視產生，包在 `computed()` 裡。
  - 篩選用 `FilterContainer`，依型別放對應的運算子與輸入元件。
  - 分頁沿用 `createDataStore`；欄位顯示偏好沿用 `use-column-visibility-store`。
- **參照**：`PopupSelect` 呼叫 `records/light`；既有實體的參照由 schema 告訴前端該呼叫哪個 light 端點。
- **i18n**：模組與欄位標籤取 metadata `*I18n` 中目前語系的值，缺值退回 zh-TW；固定的介面文字放在 `dynamic` namespace，四個語系都要有。
- **page-context**：動態欄位用 `useRegisterPageContext` 註冊 FieldMeta，讓 agent 看得到。

### 12.4 設計器

- 模組清單用 `DataTable`；區塊與欄位編輯沿用 `mold-field-config` 頁的 `InlineEditableDataTable` 樣式。
- 規則編輯器：條件樹的 UI（全部符合／任一符合，加上欄位、運算子、值）。
- 權限頁：群組 × 八軸的矩陣、欄位例外清單、資料範圍。
- 「預覽」分頁：用執行期同一套渲染元件，以草稿 metadata 即時呈現表單。

## 13. POC 實作方式

**目錄**（在本 repo）：

```
dynamic-fields-design/
  poc/backend/   .NET 8 solution，分層為：Domain／Application／Persistence／WebAPI／Tests
  poc/frontend/  Vue 3 + Quasar + TanStack Query + vee-validate + zod，分層對應後端的 Clean Architecture 分層
  docs/
```

- **後端**：EF Core（SQLite provider）負責寫入，Dapper 負責查詢，MediatR、FluentValidation、xUnit。資料庫是單一檔案 `poc/backend/data/poc.db`，啟動時由 EF migration 建立並灌種子資料。
- **方言**：先只實作 `SqliteDialect`，但 `ISqlDialect` 介面與測試要能切換。上線前補 `SqlServer2008Dialect`，並用 Testcontainers 跑同一套查詢測試。
- **身分**：沒有正式登入。用畫面左上角的身分切換（種子資料有多個使用者和群組），以 `X-User` header 帶給後端，`ICurrentUser` 用模擬實作。這是 POC 專用，正式版換掉。
- **權限表**：形狀貼近 ProgramCode 權限模型，正式版可以直接對映（§14）。
- **framework 元件**：POC 建一層薄包裝元件，正式版換成正式元件庫時只換 import（§15 風險 2）。
- **既有實體模擬**：種子資料建兩個唯讀實體，各有自己的 light 端點，用來示範參照既有實體與擴充欄位：
  - 「模具」（`design.mold`）。
  - 「客戶」（`sales.customer`），給訂單範本用。

### 13.1 模組範本

- **格式**：一份 JSON 套件，內容是一或多個模組的完整 metadata，包括區塊、欄位、選項、規則、清單檢視、預設權限建議，以及範本內各模組之間的參照。範本內的欄位用範本內部的代號互相引用；套用時才配發真正的 `Field.Id`、`Field.Key`、ProgramCode。
- **內建範本**：放在 `poc/backend/templates/*.json`，站台啟動時載入並驗證，有錯就拒絕啟動。範本只是「可以拿來建模組的藍本」，啟動時不會自動建出模組（我的判斷：避免每個客戶庫一開機就多出用不到的功能）。
- **從範本建立**：設計器選範本 → 可以改名稱、勾選要哪些欄位 → 在一個交易內建出所有模組，狀態是草稿；管理員確認後再啟用。
- **匯出與匯入**：任何模組（含明細子模組）都可以匯出成同一種格式，再到另一個客戶庫匯入。這相當於 vtiger 的模組套件，但只有 metadata、不含程式。
- **端點**：`GET /api/v3/dynamic/templates`、`POST /templates/{key}/instantiate`、`GET /modules/{key}/export-template`、`POST /templates/import`。

**內建範本一：訂單**

- **訂單（表頭）**：
  - 訂單編號：`autonumber`，`SO-{yyyyMM}-{0000}`。
  - 客戶：`reference`，參照既有實體；POC 用模擬的「客戶」。
  - 訂單日期：`date`，預設今天。
  - 交期：`date`，跨欄位驗證不得早於訂單日期。
  - 狀態：`enum`，草稿、已確認、出貨中、已結案、取消。
  - 業務員：`user`。
  - 幣別：`enum`。
  - 備註：`textarea`。
  - 取消原因：`textarea`，只在狀態為「取消」時顯示。
  - 未稅合計：`rollup`，`SUM(明細.金額)`。
  - 稅額：`formula`，`ROUND(未稅合計 × 稅率, 0)`。
  - 稅率：`percent`，預設 5%。
  - 含稅總額：`formula`，`未稅合計 + 稅額`。
  - 明細筆數：`rollup`，`COUNT`。
- **訂單明細（明細子模組）**：
  - 訂單：`reference`，`relation = "detail"`。
  - 項次：`autonumber`，在單內重新編號。
  - 品號、品名：`text`。
  - 數量：`decimal`。
  - 單位：`enum`。
  - 單價：`currency`。
  - 金額：`formula`，`數量 × 單價`，`currency` 型別的結果。
  - 交期：`date`，預設帶表頭交期。
- **規則**：
  - 狀態為「取消」時，必填「取消原因」（`textarea`），而且全單唯讀。
  - 狀態為「已結案」時，全單唯讀。
- **權限建議**：
  - 業務：資料範圍是本人。
  - 業務主管：可以看全部。
  - 看不到單價與金額的群組：不給 Pricing 軸。

訂單範本會用到明細、彙總、公式、自動編號、跨欄位驗證、條件規則、參照既有實體、金額遮罩，所以它同時是 POC 最完整的驗收案例。

新的需求在上面幾項之外還帶出兩個規格細節，一併定案（我的判斷）：

- `Visibility`、`ReadOnly` 規則的目標可以是「整個記錄」（`Targets = ["*"]`），用來實作「已結案全單唯讀」。
- 自動編號可以設定成「在父記錄內編號」（`Config.scope = "parent"`），讓明細的項次從 1 開始。

### 13.2 開發順序

**開發順序**（全部在 POC 範圍內，依賴由前往後）：

1. metadata 與記錄的 CRUD、型別處理器、schema API、通用表單與明細。
2. 列表：篩選、排序、分頁、清單檢視、索引表。
3. 權限：模組 × 八軸、欄位層、資料範圍、選項範圍、Pricing 遮罩、身分切換。
4. 規則引擎、公式、自動編號、下拉連動。
5. 關聯：參照、反向關聯清單、刪除連帶、既有實體參照與擴充模組。
6. 表頭與明細、彙總欄位。
7. 歷程、匯出、匯入、設計器預覽、選單。
8. 模組範本：格式、啟動時載入、從範本建立、匯出匯入、內建「訂單」範本。

**驗收**：每個階段都要通過該階段的自動測試（後端 xUnit、前端 vitest；規則引擎前後端共用同一份測試案例），再加下面的 Demo 情境實機走一遍。

**Demo 情境一：從範本建訂單**。從內建範本建立「訂單」並啟用，建一張含 3 列明細的訂單，確認以下各點：

- 合計、稅額、總額自動算出。
- 改明細數量後，合計跟著變。
- 刪掉一列明細，彙總值同步更新。
- 交期早於訂單日期時，存檔被拒。
- 結案後全單唯讀。
- 業務看不到別人的訂單。
- 沒有 Pricing 權限的群組看不到單價與金額。

**Demo 情境二：手動建模組**。從空白建一個「模具維修申請」模組，涵蓋範本沒用到的目標。

- 欄位：
  - 自動編號申請單號 `REP-{yyyyMM}-{0000}`。
  - 參照既有實體「模具」。
  - 下拉「故障類別」，連動「故障細項」。
  - 金額「預估費用」，受 Pricing 遮罩。
  - 日期「需求日」，跨欄位驗證：不得早於申請日。
  - 公式「距需求日天數」。
- 規則：故障類別為「外包」時，才顯示並必填「外包廠商」。
- 第二個模組「維修紀錄」：參照「模具維修申請」，申請明細頁出現反向關聯分頁；刪除申請單時套用 `Restrict`。
- 權限：
  - 「現場人員」群組：資料範圍只有本人，看不到預估費用。
  - 「主管」群組：可以看全部、可以匯出。
- 擴充模組：替「模具」加兩個自訂欄位。

## 14. POC 到正式產品的對應

| POC | 正式產品 |
|---|---|
| SQLite `Dynamic_*` 表 | 客戶選擇的正式資料庫（如 SQL Server 的 `Dynamic` schema），走 EF migration |
| POC `Program` 等權限表 | 正式的權限表（`Program`、`GroupProgramPrivilege`、`UserGroupMapping`） |
| `DynamicAccessGuard` 的模擬判定 | 正式的權限判定服務 |
| 模擬 `ICurrentUser` 與 `X-User` | 正式登入（如 OIDC 的 JWT） |
| 模擬「模具」light 端點 | 外部系統整合（既有實體的 `GET .../light`） |
| `SqliteDialect` | 各資料庫的方言（如 `SqlServerDialect`），依客戶選擇 |
| POC 的薄包裝元件 | 正式元件庫 |
| 自寫的 axios client | 由 OpenAPI 生成的 client |

換成正式版時：Domain 與 Application 層原封不動，Persistence 換方言與 provider，WebAPI 換認證，前端換 import 與 client。如果 POC 的分層沒有守住，這張表就不成立；所以 POC 的 code review 要把「是否守住分層規則」當成必查項目。

## 15. 風險與待確認

以下事項上線前要確認；POC 可以照本文的判斷先做，但上線前必須有答案。

1. **前端架構例外**：新 context `dynamic`、執行期才知道的 programCode、側邊欄的執行期項目。這三件事都碰到前端的 baseline 或規則，需要前端負責人同意。
2. **framework 存取**：正式版用哪個元件庫？通用 FieldRenderer 要放在元件庫還是應用內？
3. **SQL Server 最低版本**：如果確定是 2016 以上，可以考慮把部分 `RecordIndex` 換成 `JSON_VALUE` 計算欄，但不是必要。
4. **欄位權限的合併方向**：採限制優先，和動作軸的 OR 合併方向相反，要跟權限子系統的負責人確認。
5. **ProgramCode 執行期登記**：一般做法是新代碼用 migration 種進資料庫；動態模組是在執行期登記。需要確認這個例外，以及 `DYN` 前綴是否和既有代碼衝突。
6. **BI 與報表**：值在 JSON 裡，直接 SELECT 資料表的報表工具讀不到動態欄位。之後可以由後端依 metadata 產生唯讀的寬表 view（這是 DDL，要走 migration 或排程），這次先不做。
7. **既有實體被刪除**：動態系統收不到外部系統的刪除事件，指向既有實體的參照只能在顯示時檢查是否失效。
8. **訂單範本與正式訂單功能重疊**：訂單範本在 POC 只是驗收案例；上線後要不要當作內建範本提供給客戶，要問 PO，避免客戶誤以為可以取代正式的訂單功能。
9. **索引表的寫入成本**：每筆記錄的可查詢欄位數 × 記錄數，就是索引表的列數。POC 要量一次：1 萬筆記錄、30 個可查詢欄位時的寫入與列表查詢時間，作為上線前的效能基準。結果見 [`docs/acceptance/phase2.md`](../../acceptance/phase2.md) 的「效能基準」。
10. **`Module.Version` 的併發**：POC 靠 SQLite `BEGIN IMMEDIATE` 序列化寫入，Version 不是 concurrency token。換成 SQL Server（讀已提交）後，兩個設計者同時改同一模組會讀到同一個 Version、寫出同一個新值，ETag 相同但 schema 不同，前端會一直拿到 304。上線時要把 `Module.Version` 設為 `IsConcurrencyToken()`，並補一個空 migration 讓 snapshot 同步。
11. **唯一索引衝突映射 409**：建模組的 `Key`、`ProgramCode` 是先查後寫，併發時會撞唯一索引而變成 500。上線時要把唯一索引衝突映射成 409（POC 已有 `DbUpdateConcurrencyException` → 409 的 `IExceptionHandler`，可在同處擴充）。
12. **狀態列舉大小寫不一致**：請求端（如 `PUT fields/{id}/status`）走全域 camelCase converter，送 `active`；回應的 `status`、`Module.Status` 是 `ToString()` 的 `Active`。換成 OpenAPI 生成的 client 後會變成請求是 enum、回應是 string，上線前要統一。
13. **使用者清單三份，且使用者目錄掛在 Domain**：前端寫死 `SEED_USERS`、後端 `SeedData.Users`、驗證 `X-User` 用 `IKnownUsers`（程序生命週期快取）而 user 欄位驗證用 `IModuleRepository.ListUserIdsAsync`（每次查）。使用者目錄掛在 Domain 的 `IModuleRepository` 上，改接正式登入與權限資料時必須改 Domain 介面，與 §14「Domain 原封不動」衝突。上線前收斂成 Application 層一個 `IUserDirectory`。
14. **OpenAPI 缺回應型別**：回 `IActionResult` 的 action 都沒有 `[ProducesResponseType]`，Swashbuckle 產不出回應 schema，與 §4「API 必須走生成契約」衝突。上線前補齊。
15. **`DynamicProgram.Active` 不跟模組停用**（第 3 階段已處理）：建模組時 `Program.Active` 為 false（草稿），`activate` 改 true，`deactivate` 改 false，與 `Module.Status` 在同一個交易；啟動時 `SeedData` 依現有模組狀態同步一次。權限判定讀 `Program.Active = false` 時回 403。
16. **匿名記錄的清理**（第 3 階段已處理）：第 1 階段寫入的記錄 `OwnerUserId` 落成 `anonymous`。第 3 階段起除 `GET /users` 外，缺少或未知的 `X-User` 一律回 401；啟動時 `SeedData` 冪等地把 `OwnerUserId` 不在 `AppUser` 的記錄改歸 `admin`，資料範圍 `Own` 不會出現沒人擁有的孤兒。
    - 有效權限（動作軸、資料範圍、欄位權限、選項範圍）每個請求即時計算，不存快取；schema 的 ETag 含有效權限的雜湊（`permHash`），換人或改權限就會失效。正式版若把有效權限併入既有權限快取，必須走 `ICache`，並在權限異動（`PUT /permissions`、群組成員變更）時清除。
17. **`NumberValue` 的精度**：POC 的 `RecordIndex.NumberValue` 在 SQLite 是 `REAL`（double），金額與百分比的比較、排序可能有浮點誤差。換成 SQL Server 時要改成 `decimal(28,10)`，並確認 `Boolean` 與選項 id 也能存進去。
18. **`NumberValue` 參數型別**：Dapper 會把 double 的 `NumberValue` 參數當 float 送出，SQL Server 的型別優先序是 float 高於 decimal，會把欄位轉成 float 而無法 seek；上線時查詢參數也要綁成 decimal，不只改欄位型別（見第 17 條）。
19. **`TextValue` 的定序差異**：SQLite 的 `=`、`IN` 與排序區分大小寫（binary），SQL Server 預設定序不分大小寫；`eq`／`in`／排序的行為會變，上線時要選定 `TextValue` 欄位的定序。
20. **`EscapeLike` 要跳脫 `[`、`]`**：`SqlServerDialect.EscapeLike` 除了 `%`、`_`、跳脫字元，還必須跳脫 `[`、`]`（SQL Server 的字元集萬用字元），不能直接複製 `SqliteDialect`。
21. **`RecordIndexBackfill` 與 `FormulaRecalc` 一次載入全部記錄**：回填（UpdateField 開啟索引、重新啟用欄位）把模組的記錄一次讀進記憶體，上線前要改成分批。`FormulaRecalc`（公式新增、變更、重新啟用時）同樣在一個交易內載入模組的全部記錄，並換掉每一筆的 RowVersion，期間正在編輯的人存檔會收到 409。
22. **索引文字上限 440**：`RecordIndex.TextValue` 上限設為 440 字元，是為了讓 `(FieldId, TextValue)` 索引鍵（8 + 880 = 888 bytes）不超過 SQL Server 2008 的 900-byte 上限。
23. **自動編號的併發**（第 4 階段）：`NumberSequence.NextAsync` 在 POC 是先讀再更新，靠 SQLite 序列化寫入而安全。換成 SQL Server（讀已提交）後，兩個交易會讀到同一個 `NextValue` 而發出重複的業務編號。上線時要改用 `UPDLOCK` 讀取，或用單一原子 `UPDATE … SET NextValue = NextValue + 1` 再讀回，並處理某期別第一次插入時的主鍵衝突（映射成重試）。
24. **規則的推論管道**（第 4 階段）：規則引用某些群組看不到的欄位時，那些使用者可以反覆存檔，從規則成立與否推測隱藏欄位的值。設計者不可在這類欄位上寫給使用者看的規則；之後可考慮在儲存規則時依群組驗證並拒絕。另外，條件引用了某使用者看不到的欄位的 ReadOnly 規則，不會出現在該使用者的 schema 裡：表單會把欄位顯示成可編輯，但後端視為不可寫而默默忽略這次修改，仍回 200。
25. **含公式或自動編號條件的規則不即時套用**（第 4 階段）：前端不計算公式，記錄表單不套用條件引用公式或自動編號欄位的規則，儲存時才由伺服器判斷。因此「條件引用公式欄位或自動編號欄位」的規則（如 Visibility），在表單與明細頁的結果可能不同（明細頁用已儲存的計算結果）。新增記錄時，引用自動編號欄位的規則一律看到空值，連伺服器也一樣，因為編號在規則求值之後才產生。
26. **「今天」的時區不一致**（第 4 階段）：規則條件的相對日期（如「今天之前」），前端用機器本地日期，後端用營運時間（+08:00）。UTC+8 以外的使用者在日期交界會看到表單與伺服器判斷不一致。
27. **同時被規則隱藏、被 OptionFilter 限制，且帶著過期值的 enum 子欄位**（第 4 階段）：這種欄位若持有已超出可選範圍的舊值，會擋住存檔，直到使用者把上層欄位改回去並清掉該值為止；錯誤顯示在頁面頂端的橫幅。前後端行為一致（決策 8），刻意保留。
28. **mock 既有實體不檢查權限**（第 5 階段）：參照欄位指向既有實體時，light 查詢、存在檢查與擴充資料建立都不檢查權限，任何登入的使用者都能查到 `Mock_Mold`、`Mock_Customer`。正式版整合外部系統後，light、存在檢查、擴充資料建立都要依宿主實體的 `ProgramCode` 套用權限與資料範圍。
29. **連帶刪除不檢查來源模組的刪除權限**（第 5 階段）：連帶刪除（Cascade）只看發起刪除的那筆記錄，使用者有沒有被連帶刪除的來源模組的 `Delete` 權限不影響結果。正式版要決定是否改成「無權刪除來源就視同 Restrict」。
30. **SetNull 不寫歷程**（第 5 階段；第 7 階段已處理）：第 5 階段刪除時把來源記錄的參照清空，只更新值、索引、`RowVersion` 與修改稽核欄位，不寫變更歷程。第 7 階段起，被清空的來源記錄寫一列 `Update` 歷程：舊值是原本的參照、新值空白，修改者是發起刪除的人。
31. **刪除行為的併發**（第 5 階段）：刪除分成「規劃」與「提交」兩步。兩步之間若有人新增了 Restrict 來源，或修改了要清空或連帶刪除的來源記錄，POC（SQLite 序列化寫入）不會出事；SQL Server 讀已提交的隔離等級下，可能漏擋 Restrict，或蓋掉別人剛做的修改。上線時要在交易內鎖定相關記錄，或提交前重新驗證 `RowVersion`。
32. **彙總不看讀者的資料範圍**（第 6 階段）：彙總欄位計入所有子記錄，不依讀者的資料範圍過濾；只看得到本人資料的使用者，可以從彙總值推測別人的記錄（使用者裁決接受）。彙總對象在來源模組對使用者隱藏，不影響彙總欄位本身的可見性；結果是金額的彙總照金額權限隱藏。
33. **子記錄存檔會換掉父記錄的 `RowVersion`**（第 6 階段）：一般參照的子記錄新增、修改、刪除後重算父記錄的彙總，值有變時換掉父記錄的 `RowVersion`，正在編輯父記錄的人存檔會收到 409；彙總值沒變時不換。
34. **前端不即時計算公式與彙總**（第 6 階段）：在表單逐列編輯明細時，金額、合計、稅額要存檔後才會看到新值。
35. **彙總重算一次載入整個模組**（第 6 階段）：`RollupRecalc` 與 `FormulaRecalc` 一樣，在設計器變更時於同一個交易內載入整個模組重算；上線前改成分批（與第 21 條一起處理）。
36. **存表頭不重算經一般參照指回表頭的彙總**（第 6 階段）：表頭存檔時略過表頭本身的重算（明細彙總已在記憶體算過）。若明細模組另有一個一般參照也指向同一個表頭模組，靠這個參照做的彙總在存表頭時不重算，要等整個模組重算（例如修改彙總設定）時才更新。情況罕見，接受（裁決 R14）。
37. **一般參照彙總的欄位隱藏**（第 6 階段）：明細彙總的彙總對象若對使用者隱藏，彙總也跟著隱藏；一般參照的彙總只依自己的結果型別判斷金額權限。來源模組裡「宣告為數字、但引用金額欄位」的公式被加總時，沒有金額權限的人仍看得到合計。上線前要決定是否也套用來源模組的欄位隱藏（讀者對來源模組可能沒有任何權限）。
38. **系統重算不寫歷程**（第 7 階段）：彙總重算、設計器觸發的公式重算都不寫歷程；公式、彙總、自動編號欄位的值變了也不記。這些值由系統計算，記了只是噪音，而且子記錄觸發的彙總變化本來就不跑歷程，只記一半更難懂。
39. **歷程的參照標題依目前資料解析**（第 7 階段）：歷程只存原始值，顯示時才查參照的標題。目標改名後，舊歷程顯示新名稱；目標刪除後顯示「（已刪除）」，包括 SetNull 清空的那一列（裁決 R14）。要看當時的標題，得在寫歷程時另存快照。
40. **匯入只新增**（第 7 階段）：匯入不更新既有記錄、不含明細列，擴充模組不能匯入。
41. **匯出、匯入一次載入記憶體**（第 7 階段）：匯出上限 10000 筆、匯入上限 1000 筆（檔案 1 MB），都是整批讀進記憶體再處理。上線前改成串流與分批，與第 21、35 條一起處理。
42. **沒有金額權限時，匯出不含金額欄位**（第 7 階段）：§7 寫「匯出也是空白」，§10 寫「隱藏的欄位匯出不含」；`PermissionResolver` 本來就把金額欄位列為隱藏，所以 POC 照 §10 整欄不含，與 schema 一致。上線前和既有匯出的做法對齊。
43. **`ClosedXML`**（第 7 階段）：xlsx 匯出用的 `ClosedXML` 是 POC 自選的套件，正式版改用團隊選定的 Excel 元件。
44. **預覽看不到草稿中的明細模組**（第 7 階段）：設計器預覽的明細只列啟用中的明細模組，和執行期相同；明細模組還在草稿時，表頭的預覽裡沒有它的表格。
45. **選單分區只有一種語系**（第 7 階段）：分區名稱是管理員輸入的文字，不像模組名稱有四個語系；切換語系時分區名稱不變。
46. **歷程每頁載入整個明細模組**（第 7 階段）：歷程要顯示明細列標題（含已刪除的列），POC 沒有「依 Id 取含已刪除記錄」的查詢，每頁都用 `ListAllByModuleAsync` 把明細模組整個載入再過濾。明細列多時很慢；上線前改成依 Id 批次查詢，與第 21、35、41 條一起處理。
47. **範本只帶群組層的權限建議**（第 8 階段）：範本裡只有群組的動作權限與資料範圍；欄位隱藏、欄位唯讀、選項範圍、個人權限都不在範本裡。從範本建出模組後，要到權限分頁補上（計畫決策 6）。範本提到的群組在目標庫不存在時直接略過。
48. **匯入的範本不能刪除或覆蓋**（第 8 階段）：匯入的範本存在資料庫裡，沒有刪除或更新的端點；同一個代碼（含內建範本的代碼）再匯入回 409「範本代碼已存在」。要換內容只能改代碼另外匯入。
49. **範本沒有版本升級**（第 8 階段）：從範本建出的模組和範本之間沒有連結。範本之後改了，已經建出的模組不會跟著更新，也沒有「比較差異、套用升級」的功能。
50. **匯出時無法表達的引用直接省略**（第 8 階段）：彙總範本外的模組、引用停用欄位的公式與彙總、引用停用欄位或選項的規則，匯出時整個略過；「帶表頭欄位」指向範本外的欄位時只拿掉這個設定（裁決 R7）。略過的東西只寫在匯出檔的 `warnings`，畫面上不會提示。
51. **結構驗證不檢查型別設定**（第 8 階段）：匯入時的結構驗證檢查代號、引用、循環依賴與規則條件引用的欄位，不檢查各型別的設定內容（例如自動編號格式、條件值的型別）。這類錯誤要到「從範本建立」時才由設計器指令擋下，整批回滾。
52. **「預設今天」前後端的今天不同**（第 8 階段）：新增表單用瀏覽器本地日期帶值，伺服器在沒有送值時用營運時區的今天；UTC+8 以外的使用者在日期交界會看到不同的日期（同第 26 條）。
53. **「帶表頭欄位」只在新增明細列時帶值**（第 8 階段）：明細欄位設了帶表頭的值，只在新增一列（前端）或新增明細列沒送值（後端）時帶入；之後改表頭的交期，既有明細列的交期不會跟著改。
54. **從範本建立是逐條重播設計器指令**（第 8 階段）：每個欄位、規則、權限都走一次既有的設計器指令（含驗證、重算、`TouchVersion`），訂單範本大約 60 次指令，全部包在同一個交易裡。模組是剛建的空模組，不影響結果，只是步驟多、模組版本號一開始就大於 1。
55. **「帶表頭的值」可能帶入呼叫端看不到的表頭值**（第 8 階段）：新增明細列沒送某個欄位時，後端從表頭複製它設定的欄位值；即使呼叫端對那個表頭欄位是欄位層隱藏，值照樣寫進新的明細列。前端每次都送出明細的所有欄位鍵，所以只有直接呼叫 API、又省略那個鍵時才會發生。金額欄位不受影響：表頭與明細兩邊都由金額軸遮蔽。上線前決定是否在複製時檢查呼叫端對表頭欄位的可見性。
56. **`TemplateCatalog.Load` 在 Application 層讀檔案系統**（第 8 階段）：內建範本由 `TemplateCatalog.Load` 直接讀站台輸出目錄的 `templates/*.json`，Application 層因此依賴檔案系統。上線時把讀檔移到 Infrastructure，放在 `ITemplateSource`（或另一個介面）後面，Application 只拿到解析好的套件。

### 上線前必做（POC 未做）

第 3 階段最終審查列出、POC 刻意不處理的項目；上線前逐項處理。

- **擁有者 SQL 參數改成子查詢**：資料範圍為 `Group` 時，`DynamicQueryBuilder` 把同群組的每個擁有者展開成一個 SQL 參數（`@o0`、`@o1` …）。SQL Server 單一查詢最多 2100 個參數，大群組會失敗；改成 join `UserGroupMapping` 的子查詢。
- **schema 回應加 `Cache-Control: private` 與 `Vary`**：schema 內容依使用者而異。POC 沒有共用 proxy，ETag 又含 `permHash`，不受影響；正式環境前面若有共用 proxy 或 CDN，必須補 `Cache-Control: private` 與 `Vary: X-User`（改接正式登入後改 `Authorization`）。
- **決定是否拆開「設計」與「授權」兩條軸**：目前持有 `DYN00000` Edit 的人就能在權限分頁給自己任何模組的全部權限（含 Pricing、解除隱藏），等於自我提權。評估另立 `DYN00001` 專管權限設定，讓設計器編輯者不能改授權。
- **有效權限改用 `ICache`**：POC 每個請求即時計算。改用共用權限快取時必須走 `ICache`，並在 `PUT /permissions`、群組成員變更時清除。
