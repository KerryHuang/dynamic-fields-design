# 第 1 階段驗收

範圍：動態模組基礎 CRUD（建模組、區塊、欄位、選項、啟用、記錄新增／修改／明細／刪除）。

## 自動測試

驗收日：2026-11-03

| 指令 | 結果 |
|---|---|
| `dotnet test poc/backend --filter Phase1_Scenario` | 通過（1/1） |
| `dotnet test poc/backend` | 通過：DomainTests 69、ApplicationTests 86、WebAPITests 28，共 183，失敗 0 |
| `cd poc/frontend && bunx vitest run` | 通過：9 個檔案、35 個測試 |
| `cd poc/frontend && bunx vue-tsc --noEmit` | 通過（無錯誤） |

`Phase1_Scenario`（`poc/backend/tests/DynamicModules.WebAPITests/Phase1AcceptanceTests.cs`）以 API 走完下列情境，每步皆斷言：

1. 建立模組 `repair-request`（模具維修申請），代碼為 `DYN00001`
2. 加區塊「故障資訊」
3. 加 8 個欄位（申請日、故障類別〔模具損壞／機台異常／外包〕、檢查項目、預估費用、是否急件、聯絡 Email、承辦人、說明）
4. 啟用模組
5. 以 `sales01` 新增記錄：`ownerUserId` 為 `sales01`，「是否急件」套用預設值 false
6. 修改說明，`rowVersion` 改變
7. 停用「外包」選項：原本選外包的記錄可修改其他欄位（200）；新記錄選外包被拒（400）
8. 刪除記錄，paged 列表不再包含

## 實機步驟

執行 `poc/start.command` 後在瀏覽器手動走同一情境。截圖存於 `docs/acceptance/phase1/`。

| # | 步驟 | 結果 | 截圖 |
|---|---|---|---|
| 1 | 設計器建立模組 `repair-request`（模具維修申請） | 通過：ProgramCode `DYN00001`、草稿、版本 1 | `01-create-module-dialog.png`、`02-editor-after-create.png`、`03-module-list.png` |
| 2 | 加區塊「故障資訊」與 8 個欄位、故障類別三個選項 | 通過：8 個欄位設定經 API 核對；編輯既有欄位時型別鎖定並顯示「欄位型別建立後不能更改」 | `04`–`11` |
| 3 | 啟用模組，側邊選單出現「模具維修申請」 | 通過 | `12-module-activated-menu.png` |
| 4 | 新增記錄（是否急件預設為否、必填驗證） | 通過：空送出時名稱、申請日顯示「此欄位必填」 | `13`–`15` |
| 5 | 列表顯示新記錄 | 通過（分頁文字缺陷已修，見下方） | `16-record-list-with-row.png`、`25-list-pagination-fixed.png` |
| 6 | 編輯記錄、修改說明並儲存 | 通過：PUT 200，回明細且值已更新 | `18-record-edited-detail.png` |
| 7 | 明細頁顯示各欄位值 | 通過：金額 12,000、是否 否、多選「外觀、尺寸」 | `17-record-detail.png` |
| 8 | 設計器停用「外包」選項後，既有記錄仍可編輯 | 通過：既有記錄只改說明 PUT 200；新增頁「外包」呈停用不可選 | `19`–`22` |
| 9 | 刪除記錄，列表不再出現 | 通過：確認對話框後 DELETE 200，列表「沒有資料」 | `23-delete-confirm.png`、`24-list-after-delete.png` |

全程 console 無 error／warn，沒有空白的錯誤提示。

### 實機發現與處置

- **列表分頁文字錯誤（已修）**：有 1 筆資料時分頁顯示「1–0 of 0」。原因是 `q-table` 只傳 `:pagination` 而沒有 `@update:pagination` 時，Quasar 只拿它當初始值，之後 `rowsNumber` 更新不會反映。`ListPage.vue` 補上 `@update:pagination` 後實測顯示「1–1 of 1」。
- **一般使用者看得到「系統管理 > 動態模組設計」**：第 1 階段不做權限（權限是後續階段），屬預期行為。
- **未驗證**：切換模組時列表頁碼回到第 1 頁（實機只有一個模組）；Windows `start.cmd` 未在 Windows 上執行。
