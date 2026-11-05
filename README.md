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
- **登入**：全新資料庫只有 `admin`。`start.command` 以 Development 環境啟動，`admin` 的初始密碼是 `admin1234`，第一次登入要改密碼。其他使用者與群組由 `admin` 在「系統管理」建立。
- **重設資料**：停止服務後刪除 `poc/backend/data/poc.db`（旁邊的 `keys` 目錄是登入金鑰，可一併刪除），下次啟動會重建，只剩 `admin`。

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

限制：免費方案閒置 15 分鐘會休眠，下次開啟要等約 1 分鐘；沒有永久磁碟，休眠或重新部署後資料回到種子資料。Demo 站開了一鍵登入（`Demo__LoginEnabled`），拿到網址的人點一下就能以任何 Demo 帳號含 `admin` 登入，網址不要公開散布。Render 上 cookie 的 `Secure` 旗標是否生效：未驗證。

啟動時自動產生 Demo 資料：設定環境變數 `Demo__SeedOnStartup=true`，站台會在背景產生訂單、故障報修、模具保養、品質異常四組模組與範例記錄；`render.yaml` 已開啟，本機預設關閉。也可以由系統管理員在「系統管理 → Demo 資料」手動產生。各情境怎麼示範見 [Demo 腳本](docs/demo.md)。

### 環境變數

| 變數 | 用途 |
|---|---|
| `Auth__InitialAdminPassword` | 全新安裝時 `admin` 的初始密碼（至少 8 碼）；沒設就產生隨機密碼，印在啟動 log |
| `Auth__ResetAdminPassword` | 緊急救援：啟動時重設 `admin` |
| `Auth__TrustForwardedHeaders` | 在反向代理後面時設 `true`，cookie 才會加 `Secure` |
| `Demo__LoginEnabled` | 登入頁列出 Demo 帳號一鍵登入 |
| `Demo__SeedOnStartup` | 啟動時背景產生 Demo 資料 |

`Auth__InitialAdminPassword` 只在資料庫還沒有可用的管理員時有效，之後改它不會覆寫既有密碼。`Auth__ResetAdminPassword` 至少 8 碼，會重設 `admin` 的密碼、啟用、解鎖、要求改密碼並讓所有 session 失效；救回後要把它移除，否則每次重啟都會再重設。`Demo__LoginEnabled` 絕不能在正式站開啟，它不驗密碼。Swagger 只在 Development 環境開放。

登入失敗計數用程序內的鎖保護，假設每家客戶只跑一個後端 instance。Docker 部署要把資料庫目錄（`/app/data`）掛成 volume，登入金鑰才會保留。

本機試跑容器（容器以 Production 環境執行，`admin` 沒有預設密碼：請加 `-e Auth__InitialAdminPassword=...`，或從啟動 log 找隨機密碼）：`cd poc && docker build -t dynamic-modules . && docker run -p 8080:8080 dynamic-modules`，開 `http://localhost:8080`。

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
| [`docs/manual.md`](docs/manual.md) | 操作手冊：登入、使用者與群組、設計器、模組範本、公式、規則、權限、列表與表單、歷程與匯出匯入 |
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
| [`docs/superpowers/specs/2026-11-05-users-groups-design.md`](docs/superpowers/specs/2026-11-05-users-groups-design.md) | 使用者與群組設計文件：登入、使用者與群組管理 |
| [`docs/superpowers/plans/2026-11-05-users-groups.md`](docs/superpowers/plans/2026-11-05-users-groups.md) | 使用者與群組實作計畫 |
| [`docs/superpowers/plans/2026-11-05-users-groups-rulings.md`](docs/superpowers/plans/2026-11-05-users-groups-rulings.md) | 使用者與群組實作裁決 |
| [`docs/research/vtiger-dynamic-fields.md`](docs/research/vtiger-dynamic-fields.md) | vtiger CRM 7.4 研究 |

設計文件涵蓋資料模型、型別系統、規則引擎、關聯、權限、前後端設計、POC 做法、POC 到正式產品的對應與風險；§15 列出上線前要確認的風險。vtiger 研究涵蓋 metadata 表、建模組流程、畫面自動產生的 fallback 機制與欄位生命週期。

## 參考

- vtiger 原始碼：`github.com/vtiger-crm/vtigercrm` 分支 `7.4`（commit `27225d1`）
