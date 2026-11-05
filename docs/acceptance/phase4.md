# 第 4 階段驗收

範圍：公式欄位、自動編號欄位與規則引擎——顯示／隱藏、必填、唯讀、驗證、下拉連動，前後端共用同一份案例檔（`poc/shared/rule-cases.json`）。

## 自動測試

驗收日：2026-11-03

| 指令 | 結果 |
|---|---|
| `dotnet test poc/backend --filter Phase4_Scenario` | 通過（1/1） |
| `dotnet test poc/backend` | 通過：DomainTests 651、ApplicationTests 253、WebAPITests 80，共 984，失敗 0 |
| `cd poc/frontend && bunx vitest run` | 通過：31 個檔案、610 個測試 |
| `cd poc/frontend && bunx vue-tsc --noEmit` | 通過（無錯誤） |
| `cd poc/frontend && bunx quasar build` | 通過（SPA 編譯成功） |

`Phase4_Scenario`（`poc/backend/tests/DynamicModules.WebAPITests/Phase4AcceptanceTests.cs`）固定時鐘為 2026-11-03，以 API 走完下列情境，每步皆斷言：

1. `admin` 建模組 `repair-rules`：申請單號（`autonumber`，`REP-{yyyyMM}-{0000}`，設為標題欄位）、申請人（原標題文字欄位）、申請日與需求日（`date`）、故障類別（`enum`：模具損壞、機台異常、外包）、故障細項（`enum`：模具損壞 2 個、機台異常 2 個、外包 1 個）、外包廠商（`text`，`Config.Required`）、預估費用（`currency`）、距需求日天數（`formula`，`DATEDIFF({需求日}, {申請日})`）、狀態（`enum`：草稿、已結案）
2. `PUT /modules/repair-rules/rules` 送 6 條規則：Visibility（故障類別＝外包 → 外包廠商）、Validation（需求日為空，或需求日 ≥ 申請日 → 需求日，訊息「需求日不得早於申請日」）、3 條 OptionFilter（每個故障類別各限制故障細項）、ReadOnly（狀態＝已結案 → `*`），然後啟用模組
3. 故障類別＝機台異常、不填外包廠商，回 201：申請單號為 `REP-202611-0001`，`title` 相同，距需求日天數為 9
4. 故障類別＝外包、不填外包廠商，回 400，錯誤鍵為外包廠商
5. 需求日早於申請日，回 400，需求日欄位的訊息為「需求日不得早於申請日」
6. 故障類別＝機台異常、故障細項選外包的細項，回 400，故障細項欄位的訊息為「選項不在可選範圍」
7. 狀態改成已結案，回 200；再修改，回 400，錯誤鍵 `_record`，訊息「此記錄目前不可修改」
8. `PUT /modules/repair-rules/permissions` 給 `floor` 群組 `Read`／`Add`／`Edit`、不給 `Pricing`，再加 Required 規則（預估費用 > 1000 → 外包廠商）；`floor01` 讀 schema：沒有預估費用欄，規則裡沒有任何引用預估費用的條件，但有外包廠商的 Visibility 規則

## 實機走查

2026-11-03 以 `poc/start.command` 啟動，在 Chrome 走模組 `p4walk`（P4 模具維修申請）。基本欄位（申請人、申請日、需求日、故障類別與細項、外包廠商、預估費用、狀態）和 `floor` 群組權限用 API 預先建好；公式、自動編號、標題欄位與 7 條規則都在設計器畫面上操作。

| # | 項目 | 結果 | 截圖 |
|---|---|---|---|
| 1 | 設計器：建公式與自動編號欄位；錯誤的公式顯示錯誤訊息 | 通過 | 01、02 |
| 2 | 設計器規則分頁：新增 Visibility、Required、ReadOnly、Validation、OptionFilter 五種規則，儲存後重新整理仍在 | 通過 | 03、04 |
| 3 | 表單：選「外包」才出現外包廠商，且必填 | 通過 | 05、08 |
| 4 | 表單：故障細項跟著故障類別變 | 通過 | 05、08 |
| 5 | 表單：需求日早於申請日時，送出前就擋下 | 通過 | 06 |
| 6 | 表單：存檔後顯示申請單號與天數 | 通過 | 07 |
| 7 | 已結案的記錄：打開編輯，整張表單唯讀 | 通過 | 09 |
| 8 | 切 `floor01`：表單照樣套用規則，看不到預估費用 | 通過 | 10 |

逐項觀察：

1. 運算式 `DATEDIFF({16}, {99}` 儲存時，運算式下方顯示 `公式不正確：欄位 99 不存在或未啟用`；改成 `DATEDIFF({16}, {15})` 後存成「距需求日天數」。自動編號格式 `REP-{yyyyMM}-{0000}` 即時顯示範例 `REP-202611-0001`。兩段說明文字的 `{欄位編號}`、`{yyyyMM}` 都照字面顯示。標題欄位下拉列出申請人、外包廠商、申請單號，改成申請單號後存檔。
2. 規則分頁新增：
   - 顯示：故障類別 = 外包 → 外包廠商。
   - 驗證：任一符合「需求日是空的、需求日 ≥ 申請日」→ 需求日，訊息 `需求日不得早於申請日`。比較欄位下拉只列出同為日期的「申請日」。
   - 選項限制 3 條：不勾選項就儲存時顯示 `請至少勾選一個選項`。
   - 唯讀：狀態 = 已結案 → 整筆記錄。「整筆記錄」只出現在唯讀規則的目標裡。
   - 必填：預估費用 > 1000 → 外包廠商。

   按「儲存規則」顯示「已儲存」，重新整理後 7 條規則依序都在。
3. 新增表單一開始沒有外包廠商；故障類別選外包後，外包廠商出現且帶 `*`。
4. 故障類別選外包時，細項只剩委外修復；選機台異常時，只剩油壓異常、加熱失效。編輯一筆機台異常／油壓異常的記錄，把類別改成外包後儲存，故障細項顯示 `選項不在可選範圍`，外包廠商顯示 `此欄位必填`，沒有送出。
5. 申請日 2026-11-10、需求日 2026-11-01 按儲存，需求日下方顯示 `需求日不得早於申請日`，沒有送出。
6. 需求日改成 2026-11-20 後儲存，明細頁標題與申請單號都是 `REP-202611-0001`，距需求日天數 10。第二筆是 `REP-202611-0002`。
7. 編輯表單把狀態改成已結案時沒有立刻鎖住，儲存成功。重新打開編輯，頂端顯示 `此記錄目前不可修改`，沒有儲存鈕，所有輸入框都是唯讀。
8. 切到現場人員（`floor01`）新增：沒有預估費用欄。選外包後外包廠商出現且必填，細項只剩委外修復。schema 裡引用預估費用的必填規則沒有送下來（API 確認：規則種類只有顯示、驗證、3 條選項限制、唯讀）。

### 截圖

| 檔案 | 內容 |
|---|---|
| [01-formula-error.png](phase4/01-formula-error.png) | 錯誤公式的訊息 |
| [02-autonumber-editor.png](phase4/02-autonumber-editor.png) | 自動編號格式與即時範例 |
| [03-rule-validation-fieldref.png](phase4/03-rule-validation-fieldref.png) | 驗證規則：任一符合＋比較另一個欄位 |
| [04-rules-tab.png](phase4/04-rules-tab.png) | 重新整理後的規則清單 |
| [05-form-outsource-visible.png](phase4/05-form-outsource-visible.png) | 選外包後出現外包廠商 |
| [06-form-date-validation.png](phase4/06-form-date-validation.png) | 需求日早於申請日被擋 |
| [07-detail-number-days.png](phase4/07-detail-number-days.png) | 明細頁的申請單號與天數 |
| [08-form-option-out-of-range.png](phase4/08-form-option-out-of-range.png) | 上層改變後下層舊值不在範圍 |
| [09-closed-record-readonly.png](phase4/09-closed-record-readonly.png) | 已結案記錄整張唯讀 |
| [10-floor01-form.png](phase4/10-floor01-form.png) | 現場人員的表單 |

## 已知限制

- 條件引用公式或自動編號欄位的規則，記錄表單不即時套用（前端不計算公式），儲存時才由伺服器判斷；明細頁用已存的計算結果。因此公式欄位上的 Visibility 規則在表單與明細頁可能不同（spec §15 第 25 條）。
- 規則條件的相對日期，前端用機器本地日期，後端用營運時間（+08:00）；UTC+8 以外的使用者在日期交界可能看到不一致（spec §15 第 26 條）。
- 規則引用某些群組看不到的欄位，有推論隱藏值的管道（spec §15 第 24 條）；自動編號在 SQL Server 的併發需改用鎖或原子更新（spec §15 第 23 條）。

## 實作裁決

執行計畫時的補充與修正見 [`docs/superpowers/plans/2026-11-03-phase4-rulings.md`](../superpowers/plans/2026-11-03-phase4-rulings.md)（R1–R16）。M2（選項範圍檢查略過無法寫入的欄位）與 M3（驗證錯誤掛在看得到的目標欄位）排在最後一波修正才實作於後端，驗收當下後端尚未落實；現已實作於後端（commit `bb44db3`）。
