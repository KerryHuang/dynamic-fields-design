# 報表與儀表板驗收

範圍：查詢引擎（`ReportRunner`）的數字、權限與效能。第一段其餘項目完成時再補。

## 自動測試

驗收日：2026-11-05

| 指令 | 結果 |
|---|---|
| `dotnet test poc/backend/tests/DynamicModules.ApplicationTests --filter "ReportRunner\|ReportPermissionMatrix"` | 通過（109/109） |
| `dotnet test poc/backend --filter "Category!=Benchmark"` | 通過：DomainTests 745、ApplicationTests 819、WebAPITests 242，失敗 0 |

## 效能基準

`dotnet test poc/backend/tests/DynamicModules.ApplicationTests --filter FullyQualifiedName~ReportBenchmark`（`Category=Benchmark`，`ReportBenchmarkTests`）

資料：1 萬筆主記錄、每筆 3 列子記錄（共 3 萬筆）。報表依子模組欄位分 5 組，量值是主模組金額加總（去重）、子模組數量加總、主記錄筆數。每次都斷言總計數字。環境是 Apple Silicon、.NET 8.0.30、SQLite 記憶體資料庫。

| 項目 | 結果 |
|---|---|
| 目標（spec §5） | 中位數 < 1000 ms |
| 5 次（ms） | 480、344、399、352、343 |
| 中位數 | 352 ms，達標 |

## 權限

`ReportPermissionMatrixTests` 以不同身分跑同一份定義，涵蓋：

- 隱藏欄位放在欄位、分組、量值、條件、排序、帶出、主要日期、量值條件八個位置，都回 `restricted`，訊息不含欄位名稱。
- 沒有 Pricing 時，金額欄位、金額結果公式、引用金額的數字公式放在欄位、分組、加總、條件、排序、比率六個位置都回 `restricted`，序列化結果不含數值；條件猜中與猜錯的回應完全相同。明細金額跟著表頭的 Pricing，帶出金額跟著目標模組的 Pricing。
- 本人與全部範圍的數字不同；明細繼承表頭範圍；子模組無讀取權限、彙總欄位與間接引用彙總的公式、模組無讀取權限。
