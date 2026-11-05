# 動態模組與自訂欄位設計

目標是新增一個功能（模組）、在上面加自訂欄位之後，系統自動長出這個功能的列表、新增、編輯、明細、查詢與權限，不用替每個功能寫程式。做法參考 vtiger CRM。

現在的做法是先在 `poc/` 做獨立的 POC，用 SQLite，之後再換成正式資料庫，做成可由客戶選擇資料庫的獨立產品。怎麼操作見[操作手冊](docs/manual.md)。

## POC 進度

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
- **身分**：POC 沒有登入，右上角「身分」下拉可以切換 5 個示範使用者。`admin` 是系統管理員，只有它能用設計器。
- **重設資料**：停止服務後刪除 `poc/backend/data/poc.db`，下次啟動會重建，並只放入使用者與群組這兩種種子資料。

## 測試

```bash
dotnet test poc/backend                                        # 後端：Domain、Application、WebAPI
cd poc/frontend && bunx vitest run && bunx vue-tsc --noEmit    # 前端：單元測試與型別檢查
```

效能基準另外跑，預設不含：`dotnet test poc/backend --filter Category=Benchmark`。

規則的判斷語意以 `poc/shared/rule-cases.json` 為準，前後端的測試都跑這份案例，兩邊結果必須一致。列表篩選的運算子表在 `poc/shared/filter-operators.json`，也是前後端共用。

## Demo 部署（Render 免費方案）

`poc/Dockerfile` 把前端建置結果和後端打包成一個容器，API 與畫面在同一個網址；`render.yaml` 是 Render 的 Blueprint。

1. 在 [Render](https://render.com) 用 GitHub 登入，New → Blueprint，選這個 repo，Render 會讀 `render.yaml` 建立 `dynamic-modules-demo`。
2. 之後每次推上 `main` 都會自動重新部署。

限制：免費方案閒置 15 分鐘會休眠，下次開啟要等約 1 分鐘；沒有永久磁碟，休眠或重新部署後資料回到種子資料。POC 沒有登入，拿到網址的人都能切成管理員，網址不要公開散布。

啟動時自動產生 Demo 資料：設定環境變數 `Demo__SeedOnStartup=true`，站台會在背景產生訂單、故障報修、模具保養、品質異常四組模組與範例記錄；`render.yaml` 已開啟，本機預設關閉。也可以由系統管理員在「系統管理 → Demo 資料」手動產生。各情境怎麼示範見 [Demo 腳本](docs/demo.md)。

本機試跑容器：`cd poc && docker build -t dynamic-modules . && docker run -p 8080:8080 dynamic-modules`，開 `http://localhost:8080`。

## 目錄

| 路徑 | 內容 |
|---|---|
| `poc/backend/src` | .NET 8：`Domain`（欄位型別、公式、規則引擎）、`Application`（MediatR handler、權限）、`Persistence`（EF Core + SQLite、migration、種子資料）、`WebAPI` |
| `poc/backend/tests` | xUnit：Domain、Application、WebAPI，以及各階段的驗收情境 |
| `poc/frontend/src` | Vue 3 + Quasar：`domain`（型別、規則引擎）、`application`（查詢）、`presentation`（動態表單、列表、設計器）、`pages` |
| `poc/shared` | 前後端共用的運算子表與規則案例 |
| `docs` | 操作手冊、設計文件、各階段計畫、裁決與驗收 |

## 文件

| 文件 | 內容 |
|---|---|
| [`docs/manual.md`](docs/manual.md) | 操作手冊：設計器、模組範本、公式、規則、權限、列表與表單、歷程與匯出匯入 |
| [`docs/demo.md`](docs/demo.md) | Demo 腳本：每個情境用哪個身分、開哪個畫面、示範哪項能力 |
| [`docs/superpowers/specs/2026-11-03-dynamic-modules-design.md`](docs/superpowers/specs/2026-11-03-dynamic-modules-design.md) | 設計文件 |
| [`docs/superpowers/plans/2026-11-03-phase1-foundation.md`](docs/superpowers/plans/2026-11-03-phase1-foundation.md) | POC 第 1 階段實作計畫：基礎 CRUD |
| [`docs/superpowers/plans/2026-11-03-phase1-rulings.md`](docs/superpowers/plans/2026-11-03-phase1-rulings.md) | 第 1 階段實作裁決（計畫的補充與修正） |
| [`docs/superpowers/plans/2026-11-03-phase2-list.md`](docs/superpowers/plans/2026-11-03-phase2-list.md) | POC 第 2 階段實作計畫：列表篩選、排序、清單檢視 |
| [`docs/superpowers/plans/2026-11-03-phase2-rulings.md`](docs/superpowers/plans/2026-11-03-phase2-rulings.md) | 第 2 階段實作裁決 |
| [`docs/superpowers/plans/2026-11-03-phase3-permissions.md`](docs/superpowers/plans/2026-11-03-phase3-permissions.md) | POC 第 3 階段實作計畫：角色權限 |
| [`docs/superpowers/plans/2026-11-03-phase3-rulings.md`](docs/superpowers/plans/2026-11-03-phase3-rulings.md) | 第 3 階段實作裁決 |
| [`docs/superpowers/plans/2026-11-03-phase4-rules.md`](docs/superpowers/plans/2026-11-03-phase4-rules.md) | POC 第 4 階段實作計畫：公式、自動編號與規則 |
| [`docs/superpowers/plans/2026-11-03-phase4-rulings.md`](docs/superpowers/plans/2026-11-03-phase4-rulings.md) | 第 4 階段實作裁決 |
| [`docs/superpowers/plans/2026-11-03-phase5-relations.md`](docs/superpowers/plans/2026-11-03-phase5-relations.md) | POC 第 5 階段實作計畫：關聯、刪除行為與擴充模組 |
| [`docs/superpowers/plans/2026-11-03-phase5-rulings.md`](docs/superpowers/plans/2026-11-03-phase5-rulings.md) | 第 5 階段實作裁決 |
| [`docs/superpowers/plans/2026-11-04-phase6-details-rollup.md`](docs/superpowers/plans/2026-11-04-phase6-details-rollup.md) | POC 第 6 階段實作計畫：表頭與明細、彙總欄位 |
| [`docs/superpowers/plans/2026-11-04-phase6-rulings.md`](docs/superpowers/plans/2026-11-04-phase6-rulings.md) | 第 6 階段實作裁決 |
| [`docs/superpowers/plans/2026-11-04-phase7-history-export-import.md`](docs/superpowers/plans/2026-11-04-phase7-history-export-import.md) | POC 第 7 階段實作計畫：歷程、匯出匯入、預覽與選單分區 |
| [`docs/superpowers/plans/2026-11-04-phase7-rulings.md`](docs/superpowers/plans/2026-11-04-phase7-rulings.md) | 第 7 階段實作裁決 |
| [`docs/superpowers/plans/2026-11-05-phase8-templates.md`](docs/superpowers/plans/2026-11-05-phase8-templates.md) | POC 第 8 階段實作計畫：模組範本 |
| [`docs/superpowers/plans/2026-11-05-phase8-rulings.md`](docs/superpowers/plans/2026-11-05-phase8-rulings.md) | 第 8 階段實作裁決 |
| [`docs/superpowers/plans/2026-11-05-demo-data.md`](docs/superpowers/plans/2026-11-05-demo-data.md) | Demo 資料實作計畫：四個情境、背景產生、系統管理頁 |
| [`docs/superpowers/plans/2026-11-05-demo-data-rulings.md`](docs/superpowers/plans/2026-11-05-demo-data-rulings.md) | Demo 資料實作裁決 |
| [`docs/research/vtiger-dynamic-fields.md`](docs/research/vtiger-dynamic-fields.md) | vtiger CRM 7.4 研究 |

設計文件涵蓋資料模型、型別系統、規則引擎、關聯、權限、前後端設計、POC 做法、POC 到正式產品的對應與風險；§15 列出上線前要確認的風險。vtiger 研究涵蓋 metadata 表、建模組流程、畫面自動產生的 fallback 機制與欄位生命週期。

## 參考

- vtiger 原始碼：`github.com/vtiger-crm/vtigercrm` 分支 `7.4`（commit `27225d1`）
