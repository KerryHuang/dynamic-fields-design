# 第 5 階段驗收

範圍：關聯——參照欄位（指向動態模組或既有實體）、反向關聯分頁、刪除行為（擋下、清空、連帶刪除）與擴充模組（替既有實體加欄位）。

## 自動測試

驗收日：2026-11-04

| 指令 | 結果 |
|---|---|
| `dotnet test poc/backend --filter Phase5_Scenario` | 通過（1/1） |
| `dotnet test poc/backend` | 通過：DomainTests 676、ApplicationTests 340、WebAPITests 93，共 1109，失敗 0 |
| `cd poc/frontend && bunx vitest run` | 通過：35 個檔案、681 個測試 |
| `cd poc/frontend && bunx vue-tsc --noEmit` | 通過（無錯誤） |
| `cd poc/frontend && bunx quasar build` | 通過（SPA 編譯成功） |

`Phase5_Scenario`（`poc/backend/tests/DynamicModules.WebAPITests/Phase5AcceptanceTests.cs`）固定時鐘為 2026-11-04，以 API 走完下列情境，每步皆斷言：

1. `admin` 建模組 `repair-req`（模具維修申請）：申請人（標題）、模具（`reference` → `entity:design.mold`，必填）、故障說明（`textarea`），啟用
2. 建 `repair-log`（維修紀錄）：處理內容（標題）、申請單（`reference` → `module:repair-req`，`onDelete = restrict`）、工時（`decimal`），啟用
3. 建擴充模組 `mold-ext`（`extension`、宿主 `design.mold`）：保養週期（`integer`）、上次保養日（`date`），啟用
4. 申請 A 參照 `M-0001`，回 201，`references` 為 `{ key: "M-0001", title: "M-0001 外殼上蓋模", state: "ok" }`；參照 `M-9999`，回 400，訊息「參照的資料不存在或無權選取」
5. 兩筆維修紀錄參照申請 A；`GET /records/{A}/related` 有一個分頁（維修紀錄），分頁清單回 2 筆
6. 刪除申請 A，回 409「還有其他資料參照這筆記錄，不能刪除」，`total` 為 2；刪掉兩筆維修紀錄後再刪申請 A，回 200
7. `mold-ext` 替 `M-0001` 建擴充資料，回 201，`title` 為 `M-0001 外殼上蓋模`；再建一次，回 409「這筆既有資料已經有擴充資料」；`GET /modules/mold-ext/records/by-host/M-0001` 回 200，保養週期正確
8. `repair-log` 給 `floor` 群組讀取、新增，範圍本人（`repair-req` 給讀取、全部）；`floor01` 與 `admin` 各建一筆維修紀錄參照申請 B；`floor01` 看申請 B 的反向分頁只有 1 筆
9. 把 `Mock_Mold` 的 `M-0002` 標為已刪除（測試直接改資料庫）；參照 `M-0002` 的申請 `state` 為 `missing`、沒有 `title`；只改故障說明照樣回 200

## 實機走查

2026-11-04 以 `poc/start.command` 啟動，在 Chrome 走完下列 8 項。走查用的模組：`p5-req`（P5 模具維修申請，模具參照 `design.mold`、必填）、`p5-log`（P5 維修紀錄，申請單參照 `p5-req`、禁止刪除）、`p5-mold-ext`（P5 模具擴充，宿主 `design.mold`）；`floor` 群組對 `p5-req` 有讀取（全部）、對 `p5-log` 有讀取／新增／編輯（本人）。

| # | 項目 | 結果 | 截圖 |
|---|---|---|---|
| 1 | 設計器加參照欄位（模組目標與實體目標）、選刪除方式；擴充模組的建立對話框 | 通過 | 01、02、11 |
| 2 | 表單用搜尋視窗挑模具 | 通過 | 03 |
| 3 | 明細顯示模具標題；維修紀錄的申請單是連結，點了到申請明細 | 通過 | 04、06 |
| 4 | 申請明細的「維修紀錄」分頁，按新增後表單已帶好申請單 | 通過 | 05 |
| 5 | 刪除有維修紀錄的申請，對話框列出參照來源 | 通過 | 07 |
| 6 | 模具擴充資料：新增時挑模具、存檔後明細標題是模具名稱 | 通過 | 08、09、10 |
| 7 | 已失效的模具顯示「已失效」 | 通過 | 12 |
| 8 | `floor01` 看反向分頁只有自己的記錄 | 通過 | 13 |

逐項觀察：

1. 參照欄位的目標種類選「既有實體」時，目標選單列出模具、客戶，不顯示刪除方式；選「動態模組」時多出「目標被刪除時」，選項是禁止刪除、清空欄位、連帶刪除，各附一行說明。新增模組對話框選「擴充模組」後出現必填的「既有實體」選單（模具、客戶）。
2. 模具欄位按下去開搜尋視窗，輸入「蓋」約 300 毫秒後清單縮成 M-0001、M-0002、M-0004。
3. 申請明細的模具顯示「M-0001 外殼上蓋模」，不是編號；維修紀錄明細的申請單顯示「王小明」並連到 `/dynamic/p5-req/18`。
4. 反向分頁標題是「P5 維修紀錄 · 申請單」；按「新增」開 `/dynamic/p5-log/new?prefill=f_32:18`，申請單直接顯示「王小明」。
5. 申請 18 有兩筆維修紀錄（admin 建的「更換頂針」、`floor01` 建的「現場清潔模面」）。刪除後跳出對話框「還有其他資料參照這筆記錄，不能刪除」，兩筆來源都列出、可點開，申請沒被刪。
6. 新增擴充資料時，表單頂端是必填的「既有實體」，未選就存檔顯示「請選擇既有實體」；選 M-0001、填保養週期 90 存檔後，明細標題是「M-0001 外殼上蓋模」，列表第一欄「既有實體」也是這個標題。替 M-0001 再建一筆，錯誤顯示在既有實體欄位下：「這筆既有資料已經有擴充資料」。
7. 建一筆參照 M-0002 的申請後，在 `poc.db` 把 `Mock_Mold` 的 M-0002 `DeleteFlag` 改成 1；申請明細的模具顯示「已失效」。
8. 切成 `floor01`（現場人員），申請 18 沒有編輯、刪除按鈕，反向分頁只列出自己建的「現場清潔模面」。

截圖：

| 截圖 | 內容 |
|---|---|
| [01-designer-entity-reference.png](phase5/01-designer-entity-reference.png) | 01：設計器：參照既有實體（模具） |
| [02-designer-module-reference-ondelete.png](phase5/02-designer-module-reference-ondelete.png) | 02：設計器：參照動態模組，刪除方式選單 |
| [03-popupselect-search-mold.png](phase5/03-popupselect-search-mold.png) | 03：搜尋視窗以關鍵字縮小模具清單 |
| [04-detail-mold-title-and-related-tab.png](phase5/04-detail-mold-title-and-related-tab.png) | 04：申請明細的模具標題與反向分頁 |
| [05-related-add-prefilled.png](phase5/05-related-add-prefilled.png) | 05：從反向分頁新增，申請單已帶入 |
| [06-log-detail-link.png](phase5/06-log-detail-link.png) | 06：維修紀錄明細的申請單連結 |
| [07-delete-blocked-dialog.png](phase5/07-delete-blocked-dialog.png) | 07：刪除被擋，列出參照來源 |
| [08-extension-form-host-picker.png](phase5/08-extension-form-host-picker.png) | 08：擴充資料表單挑選既有實體 |
| [09-extension-detail-title.png](phase5/09-extension-detail-title.png) | 09：擴充資料明細標題是模具名稱 |
| [10-extension-duplicate-host.png](phase5/10-extension-duplicate-host.png) | 10：同一模具重複建擴充資料的錯誤 |
| [11-create-extension-module-dialog.png](phase5/11-create-extension-module-dialog.png) | 11：新增模組對話框選擴充模組 |
| [12-reference-missing.png](phase5/12-reference-missing.png) | 12：模具已刪除時顯示「已失效」 |
| [13-related-tab-scope-floor01.png](phase5/13-related-tab-scope-floor01.png) | 13：floor01 的反向分頁只看到自己的記錄 |

## 已知限制

- mock 既有實體（模具、客戶）不檢查權限，任何登入的使用者都能查；上線後要依宿主實體的 `ProgramCode` 套權限與資料範圍（spec §15 第 28 條）。
- 連帶刪除不檢查使用者對來源模組的刪除權限，只看發起刪除的那筆；正式版要決定是否改成「無權刪除來源就視同 Restrict」（spec §15 第 29 條）。
- 刪除時清空（SetNull）不寫歷程，第 7 階段補（spec §15 第 30 條）。
- 刪除行為的併發：規劃到提交之間有人新增 Restrict 來源，或修改了要清空或連帶刪除的來源記錄，SQLite 序列化寫入不會出事；SQL Server 讀已提交下可能漏擋或蓋掉修改，上線時要在交易內鎖定或重驗 RowVersion（spec §15 第 31 條）。

## 實作裁決

執行計畫時的補充與修正見 [`docs/superpowers/plans/2026-11-03-phase5-rulings.md`](../superpowers/plans/2026-11-03-phase5-rulings.md)（R1–R10）。
