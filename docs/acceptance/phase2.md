# 第 2 階段驗收

範圍：動態模組列表的篩選、排序、關鍵字搜尋與清單檢視。

## 自動測試

驗收日：2026-11-03

| 指令 | 結果 |
|---|---|
| `dotnet test poc/backend --filter Phase2_Scenario` | 通過（1/1） |
| `dotnet test poc/backend`（不含 Benchmark） | 通過：DomainTests 120、ApplicationTests 137、WebAPITests 37，共 294，失敗 0 |
| `dotnet test poc/backend --filter Category=Benchmark` | 通過（1/1），結果見下方「效能基準」 |
| `cd poc/frontend && bunx vitest run` | 通過：19 個檔案、75 個測試 |
| `cd poc/frontend && bunx vue-tsc --noEmit` | 通過（無錯誤） |

`Phase2_Scenario`（`poc/backend/tests/DynamicModules.WebAPITests/Phase2AcceptanceTests.cs`）以 API 走完下列情境，每步皆斷言：

1. 建立模組 `repair-request` 並加欄位：申請日（`date`，可篩選、可排序）、故障類別（`enum`，三個選項，可篩選、可排序）、檢查項目（`multienum`，可篩選）、預估費用（`currency`，可篩選、可排序）、承辦人（`user`，可篩選）、說明（`textarea`）、標題欄（`text`，可排序）
2. 啟用模組後建 15 筆記錄，其中 3 筆不填故障類別
3. 篩「故障類別不等於外包」：筆數等於非外包 8 筆加未填 3 筆，共 11
4. 篩「預估費用介於 1000 到 5000」加「承辦人是 sales01」（`all`）：5 筆
5. 依故障類別升冪、預估費用降冪排序：前 5 筆為 T12、T09、T06、T03、T13；未填者排最後
6. 存共用預設檢視與 `sales01` 的個人預設檢視：`sales02` 只看到共用的，`sales01` 看到兩個
7. 關掉「預估費用」的可篩選後再讀檢視：`staleFieldIds` 包含它，檢視的篩選條件被剔除
8. 帶「預估費用」條件查列表：400，錯誤鍵 `filter`

## 效能基準

指令：`dotnet test poc/backend --filter Category=Benchmark`（`ListBenchmarkTests.List_10k_Records_30_IndexedFields`，預設不跑）。

資料：一個模組、30 個可篩選且可排序的欄位（8 個整數、8 個文字、7 個日期、7 個 `enum`，每個 `enum` 5 個選項），1 萬筆記錄全部經 `CreateRecordHandler` 建立（走正式索引路徑，每筆寫 30 列索引，共約 30 萬列）。資料庫是 SQLite in-memory（`SqliteTestDb`），分頁每頁 20 筆；每項量測跑 5 次取中位數，測試只斷言筆數，不斷言時間。

環境：

| 項目 | 值 |
|---|---|
| CPU | Apple M5 |
| 記憶體 | 32 GB（`hw.memsize` = 34359738368） |
| .NET | 8.0.30（SDK 8.0.424） |
| SQLite | 3.41.2（執行期 `select sqlite_version()`） |

測試輸出（原文，採用 R8 的非相關子查詢後）：

```
 SQLite 3.41.2
 .NET 8.0.30
 建立 10000 筆：總計 13.2 s，每筆平均 1.32 ms
 單一條件 + 單欄排序，第 1 頁：中位數 9.0 ms（5 次）
 單一條件 + 單欄排序，第 100 頁：中位數 12.5 ms（5 次）
 3 條件 + 3 欄排序，第 1 頁：中位數 19.8 ms（5 次）
 不篩選只算 count：中位數 0.4 ms（5 次）
```

| 項目 | 結果 |
|---|---|
| 建立 1 萬筆 | 13.2 s，每筆平均 1.32 ms |
| 單一條件 + 單欄排序，第 1 頁 | 9.0 ms |
| 單一條件 + 單欄排序，第 100 頁 | 12.5 ms |
| 3 條件 + 3 欄排序，第 1 頁 | 19.8 ms |
| 不篩選只算 count | 0.4 ms |

### 為什麼改成非相關子查詢

第一版每個條件寫成相關的 `EXISTS (SELECT 1 ... WHERE i.RecordId = r.Id AND i.FieldId = @f AND i.NumberValue >= @v)`，同一批資料量到單一條件 15.2–15.5 s、3 條件 21.3 s。單獨的 `COUNT(*)` 就要 7.7 s，所以慢在篩選本身。`EXPLAIN QUERY PLAN` 顯示 SQLite 對每筆記錄都走 `(FieldId, NumberValue)` 範圍索引，掃約 5000 個索引項再比對 `RecordId`，總成本約 1 萬 × 5000。

裁決 R8 改成 `r.Id IN (SELECT i.RecordId FROM Dynamic_RecordIndex i WHERE i.FieldId = @f AND ...)`，否定形式（`ne`、`notIn`、`isEmpty`）用 `r.Id NOT IN (...)`，子查詢只執行一次，不需要 SQLite 專屬的 planner 提示，SQL Server 也能用。測試 `Build_ConditionsUseUncorrelatedInSubquery` 固定這個形狀。

## 實機走查

執行 `poc/start.command` 後在瀏覽器走 Step 1 的情境（`repair-request`，9 筆記錄，身分 admin／sales01／sales02）。截圖存於 `docs/acceptance/phase2/`。

| # | 項目 | 結果 | 截圖 |
|---|---|---|---|
| 1 | 設計器勾選可篩選、可排序 | 通過 | `01`、`01b` |
| 2 | 篩選面板各型別的輸入 | 通過（缺陷 A） | `02a`–`02g` |
| 3 | 表頭排序與多欄排序 | 通過（缺陷 C） | `03a`、`03b` |
| 4 | 關鍵字搜尋 | 通過 | `04a`、`04b` |
| 5 | 欄位選擇 | 通過 | `05a`、`05b` |
| 6 | 另存個人、共用檢視，設為預設，重新整理後自動套用 | 通過 | `06a`–`06e` |
| 7 | 切換身分後看不到別人的個人檢視 | 通過（缺陷 B） | `07a`–`07c` |
| 8 | 停用欄位後，檢視顯示失效提示 | 通過 | `08a` |

各項觀察：

1. textarea 的兩個開關停用並顯示「此欄位型別不支援篩選／排序」；multienum 可篩選、不可排序。
2. date、enum、multienum、currency、user、text（`in`／`notIn` 逗號輸入）各條件筆數符合資料；「任一符合」群組 4 筆，改「全部符合」1 筆。
3. 表頭點擊依序升冪、降冪、取消；排序編輯器「故障類別升冪、預估費用降冪」順序正確，空值都排最後。
4. 標題關鍵字不分大小寫，無結果顯示「沒有資料」。
5. 勾選顯示／隱藏、上移下移即時生效。
6. sales01 存個人預設檢視與共用檢視，重新載入後自動套用個人預設。
7. 重新載入後 sales02 只看到共用檢視，且沒有更新、刪除選項。
8. 關掉「故障類別」可篩選後，`GET views` 回 `staleFieldIds:[3]`，列表顯示「此檢視有 1 個欄位已停用或不再開放篩選、排序，已略過」，篩選被略過、排序仍有效。

Console 無 error，只有缺陷 A 的 intlify 警告；xhr／fetch 全為 200／304。

### 實機發現與處置

- **A. 多選欄位「是空的」「不是空的」顯示成未翻譯 key**：`FilterPanel` 對 multienum 的所有運算子都查 `dynamic.op.multienum.*`，但那裡只有 `in`、`notIn`。已修：只有 `in`、`notIn` 走 multienum 標籤，其餘用 `dynamic.op.<op>`。
- **B. 切換身分後檢視選單未更新**：切換只呼叫 `queryClient.clear()`，畫面上的 views 查詢不會重抓，列表也不會重新套用新身分的預設檢視；重新載入後正確。已修：`router-view` 以身分為 key，切換時頁面重新掛載、重抓並重新挑選預設檢視；列表也改成等掛載後抓回的檢視與 schema 才初始化。
- **C. 標題欄設為可排序，表頭仍不能點**：列表的標題欄沒有帶 `sortable`，只能從排序編輯器選。已修：標題欄以欄位 key 為 name 並帶 `sortable`，表頭可排序。
- **檢視名稱在按鈕上顯示成大寫**：Quasar `q-btn` 預設大寫，`sales01` 顯示成 `SALES01`。已修：按鈕加 `no-caps`。
- **篩選下拉列出已停用的選項（「外包」）**：刻意如此，見裁決 R9。新增頁不列停用選項，但既有記錄仍可能存著它，篩選要能找到這些記錄。
- **multienum 沒有「含全部」**：運算子表（`poc/shared/filter-operators.json`）本來就只有 `in`、`notIn`、`isEmpty`、`isNotEmpty`，屬範圍外。
- **自動化操作偶發選單選了沒生效**：重選即生效，判斷是腳本點擊時機，不列缺陷。
