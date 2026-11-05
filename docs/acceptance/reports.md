# 報表與儀表板驗收

範圍：查詢引擎（`ReportRunner`）的數字、權限與效能。第一段其餘項目完成時再補。

## 自動測試

驗收日：2026-11-05

| 指令 | 結果 |
|---|---|
| `dotnet test poc/backend/tests/DynamicModules.ApplicationTests --filter "ReportRunner\|ReportPermissionMatrix"` | 通過（147/147） |
| `dotnet test poc/backend --filter "Category!=Benchmark"` | 通過：DomainTests 745、ApplicationTests 857、WebAPITests 242，失敗 0 |

## 效能基準

`dotnet test poc/backend/tests/DynamicModules.ApplicationTests --filter FullyQualifiedName~ReportBenchmark`（`Category=Benchmark`，`ReportBenchmarkTests`）

資料：1 萬筆主記錄、每筆 3 列子記錄（共 3 萬筆）。報表依子模組欄位分 5 組，量值是主模組金額加總（去重）、子模組數量加總、主記錄筆數。每次都斷言總計數字。環境是 Apple Silicon、.NET 8.0.30、SQLite 記憶體資料庫。

| 項目 | 結果 |
|---|---|
| 目標（spec §5） | 中位數 < 1000 ms |
| 5 次（ms） | 459、344、396、286、286 |
| 中位數 | 344 ms，達標 |

## 權限

`ReportPermissionMatrixTests` 以不同身分跑同一份定義，涵蓋：

- 隱藏欄位放在欄位、分組、量值、條件、排序、帶出、主要日期、量值條件八個位置，都回 `restricted`，訊息不含欄位名稱。
- 沒有 Pricing 時，金額欄位、金額結果公式、引用金額的數字公式放在欄位、分組、加總、條件、排序、比率六個位置都回 `restricted`，序列化結果不含數值；條件猜中與猜錯的回應完全相同。明細金額跟著表頭的 Pricing，帶出金額跟著目標模組的 Pricing。
- 本人與全部範圍的數字不同；明細繼承表頭範圍；子模組無讀取權限、彙總欄位與間接引用彙總的公式、模組無讀取權限。
- 同群組範圍只算群組成員的記錄；下鑽走同一套檢查，隱藏欄位與金額欄位在下鑽時同樣回 `restricted` 且結果不含數值。
- 不可讀模組的帶出欄位放在條件、比率條件、主要日期、量值時整張回 `restricted`；放在欄位與分組時值標「（無權限）」。
- 參照欄位本身放在條件或比率條件時，目標模組不可讀或標題看不到也整張回 `restricted`；目標模組停用回 `moduleDisabled`。目標模組可讀、只是單筆記錄不在範圍內時，是逐列不符合。

## 已知限制

載入上限在套用條件之前檢查：來源模組（主模組或子模組）在觀看者範圍內的記錄超過 `Reports:MaxRows` 時，就算條件後或合併後列數很少，也回 `tooMany`。這一版接受：只會多擋，不會算錯。之後要放大時，方向是先用索引縮小記錄（spec §5）。
