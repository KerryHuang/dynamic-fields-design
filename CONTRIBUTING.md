# 開發指南

這份文件給要在本機跑起 Formwright、改程式或跑測試的人。產品介紹見 [README](README.md)，部署與環境變數見 [docs/deploy.md](docs/deploy.md)，操作畫面見[操作手冊](docs/manual.md)。

## 啟動

需要 .NET 8 SDK、Bun 1.4 以上。

```bash
# 第一次：安裝前端相依（postinstall 會跑 quasar prepare）
cd poc/frontend && bun install && cd ../..
# macOS
poc/start.command
# Windows
poc\start.cmd
```

腳本會同時啟動後端（`http://127.0.0.1:5180`）與前端（`http://127.0.0.1:9180`），就緒後自動開瀏覽器。缺 `poc/frontend/node_modules` 時，腳本會先自動 `bun install`。

- **停止**：macOS 按 Ctrl-C 同時停止兩者；Windows 關閉兩個視窗即停止。
- **登入**：全新資料庫只有 `admin`。`start.command` 以 Development 環境啟動，`admin` 的初始密碼是 `admin1234`，第一次登入要改密碼。其他使用者與群組由 `admin` 在「系統管理」建立。
- **重設資料**：停止服務後刪除 `poc/backend/data/poc.db`（旁邊的 `keys` 目錄是登入金鑰，可一併刪除），下次啟動會重建，只剩 `admin`。

## 測試

```bash
dotnet test poc/backend                                        # 後端：Domain、Application、WebAPI
cd poc/frontend && bunx vitest run && bunx vue-tsc --noEmit    # 前端：單元測試與型別檢查
```

效能基準另外跑，預設不含：`dotnet test poc/backend --filter Category=Benchmark`。

規則的判斷語意以 `poc/shared/rule-cases.json` 為準，前後端的測試都跑這份案例，兩邊結果必須一致。列表篩選的運算子表在 `poc/shared/filter-operators.json`，也是前後端共用。

## 目錄

| 路徑 | 內容 |
|---|---|
| `poc/backend/src` | .NET 8：`Domain`（欄位型別、公式、規則引擎）、`Application`（MediatR handler、權限）、`Persistence`（EF Core + SQLite、migration、種子資料）、`WebAPI` |
| `poc/backend/tests` | xUnit：Domain、Application、WebAPI，以及各階段的驗收情境 |
| `poc/frontend/src` | Vue 3 + Quasar：`domain`（型別、規則引擎）、`application`（查詢）、`presentation`（動態表單、列表、設計器）、`pages` |
| `poc/shared` | 前後端共用的運算子表與規則案例 |
| `docs` | 操作手冊、設計文件、各階段計畫、裁決與驗收 |

## 設計背景與 POC 進度

核心做法：新增一個功能（模組）、在上面加自訂欄位之後，系統自動長出這個功能的列表、新增、編輯、明細、查詢與權限，不用替每個功能寫程式。這個做法參考 vtiger CRM 7.4（原始碼：`github.com/vtiger-crm/vtigercrm` 分支 `7.4`，commit `27225d1`；研究筆記見 [`docs/research/vtiger-dynamic-fields.md`](docs/research/vtiger-dynamic-fields.md)，涵蓋 metadata 表、建模組流程、畫面自動產生的 fallback 機制與欄位生命週期）。

實作先在 `poc/` 做獨立的 POC，用 SQLite，之後換成正式資料庫，做成可由客戶選擇資料庫的獨立產品。

[設計文件](docs/superpowers/specs/2026-11-03-dynamic-modules-design.md)涵蓋資料模型、型別系統、規則引擎、關聯、權限、前後端設計、POC 做法、POC 到正式產品的對應與風險；§15 列出上線前要確認的風險。各階段的實作計畫與裁決在 [`docs/superpowers/plans/`](docs/superpowers/plans/)，其他設計文件在 [`docs/superpowers/specs/`](docs/superpowers/specs/)，驗收記錄在 [`docs/acceptance/`](docs/acceptance/)。

| 階段 | 內容 | 狀態 |
|---|---|---|
| 1 | 基礎 CRUD：設計器建模組、區塊、16 種欄位與選項；自動產生列表、表單、明細 | 完成，[驗收](docs/acceptance/phase1.md) |
| 2 | 列表：篩選、排序、關鍵字搜尋、欄位選擇、個人與共用檢視 | 完成，[驗收](docs/acceptance/phase2.md) |
| 3 | 權限：群組與個人動作權限、資料範圍、欄位權限、選項範圍 | 完成，[驗收](docs/acceptance/phase3.md) |
| 4 | 公式、自動編號；規則（顯示、必填、唯讀、驗證、選項限制），前後端共用同一套判斷 | 完成，[驗收](docs/acceptance/phase4.md) |
| 5 | 關聯：參照欄位（動態模組、既有實體）、反向關聯分頁、刪除行為（擋下／清空／連帶刪除）、擴充模組 | 完成，[驗收](docs/acceptance/phase5.md) |
| 6 | 表頭與明細（明細模組、表頭內編號、表單逐列編輯）、彙總欄位（明細與一般參照）、下拉選單打字篩選 | 完成，[驗收](docs/acceptance/phase6.md) |
| 7 | 記錄歷程、匯出 CSV／Excel、CSV 匯入（預覽與確認）、設計器預覽、選單分區 | 完成，[驗收](docs/acceptance/phase7.md) |
| 8 | 模組範本：內建「訂單」範本、從範本建立、匯出與匯入範本；欄位預設今天、明細帶表頭的值 | 完成，[驗收](docs/acceptance/phase8.md) |
| 報表（第一段） | 查詢引擎在應用層算（合併、去重、彙總）、五步驟設計器、表格與四種圖表、下鑽、匯出、分享、Demo 示範報表 | 完成，[驗收](docs/acceptance/reports.md) |
| 報表（第二段） | 儀表板：12 欄拖放版面、卡片（前 N 名、累計占比、與前幾期比較、連到另一張儀表板）、暫時日期篩選、管理員指派給群組、個人調整版與還原最新版、首頁嵌入、Demo 預設儀表板 | 完成，[驗收](docs/acceptance/reports.md)；操作見[手冊第 14 節](docs/manual.md) |

報表第一段的路線圖：幣別綁定（金額欄位指定幣別欄位）、結果快取。

之後的產品路線（CRM、進銷存、MES 等內建應用程式）見 [ERP 路線設計](docs/superpowers/specs/2026-11-06-erp-roadmap-design.md)。
