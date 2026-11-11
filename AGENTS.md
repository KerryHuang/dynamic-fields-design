# Formwright（表匠）— 給 AI agent 的專案規範

所有 AI 工具（Claude Code、Codex、其他）共用這一份。Claude Code 在沒有 `CLAUDE.md` 時會自動讀它，所以 **repo 裡不要放 `CLAUDE.md` 或 `CLAUDE.local.md`**，否則這份會被略過。

## 宗旨（設計與實作前先對照）

Formwright 是**動態模組設計系統**：核心不變，模組要能全自主定義。客戶在設計器裡定義模組、欄位、規則、流程、報表、權限；CRM、進銷存、MES 等應用程式只是一包用這些定義組成、客戶可以自己打開來改的設定，不必找開發商。

ERP 是產品目標，也是核心的壓力測試：做不出來的地方，就是核心缺的能力，要補成通用能力，不是替那個應用程式寫特例。目標客戶是 50 人以下的小型企業，只做簡單版 ERP。

依據：`docs/superpowers/specs/2026-11-06-erp-roadmap-design.md` 的「目的」「已定決定」「鐵則」。

## 鐵則

1. **ERP 邏輯不寫死。** 程式裡只放平台機制；業務邏輯一律是資料（模組、欄位、規則、流程、報表、權限的定義）。
2. **業務政策要能設定，不寫成程式常數**（使用者 2026-11-11 指示）：數量上限、預設值、天數與時間規則、通知對象、特定模組或應用程式代碼的特例分支、寫死的群組／角色／狀態代碼，都要由系統管理員或設計者設定。
   - 可以寫死、但要列清單讓使用者看過：資料完整性守衛（被引用的欄位不能刪或停用）、安全與權限檢查、格式與長度等技術限制。
   - 畫面呈現方式（按鈕隱藏或灰色）不屬於這條。
   - 同一個上限或預設值不准在 Domain、Application、前端各寫一份；集中成單一來源，前端從 API 取得。
3. **檢驗方式：** 同一個能力至少要有兩個不相干的應用程式用得上。
4. **空系統永遠是合法起點：** 不開任何應用程式時平台照常可用；平台任何功能都不能依賴特定應用程式存在。
5. **沿用核心反目標：** 不做執行期 DDL、權限不 fail-open、不替個別模組手寫頁面、驗證不只做在前端。
6. **以 ERP 慣例設計：** 主檔全系統一份；設計新應用程式時盤點和既有應用程式重複的主檔與單據；每份應用程式或功能設計都要有「ERP 對照」一節，以 `docs/superpowers/specs/2026-11-10-erp-data-model-design.md` 為準。

設計、計畫、審查時用 `formwright-purpose-check` skill 逐項檢查。

## 架構地圖

目錄、啟動、測試指令見 `CONTRIBUTING.md`。重點：

- 後端 `poc/backend/src`：`Domain`（欄位型別、公式、條件，純函式）→ `Application`（MediatR handler，依功能分在 `Dynamic/<功能>Service/`）→ `Persistence`（EF Core＋SQLite、migration、種子）→ `WebAPI`。
- 前端 `poc/frontend/src`：`domain`（型別、規則引擎）、`application`（查詢）、`presentation`（動態表單、列表、設計器，依功能分資料夾）、`pages`、`i18n`（四語 JSON）。
- 內建應用程式套件：`poc/backend/apps/*.app.json`。
- 前後端共用的判斷案例：`poc/shared/`（規則語意以 `rule-cases.json` 為準，兩邊測試都跑）。

依路徑自動載入的細則在 `.claude/rules/`（後端、前端、套件、文件各一份）；改那些目錄前先讀對應的那份。

## 工作方式

- **先說驗收方式再動手**；修 bug 先寫重現的失敗測試。
- **只跑相關測試**：`dotnet test <測試專案> --filter <類名>`；全套與 gate 一次只跑一個，不和其他吃 CPU 的測試並行。細節見 `docs/ai/testing.md`。
- **上線流程**（gate → 本機走查 → 外洩掃描 → fast-forward 推 main → 等部署 → Demo 冒煙檢查）用 `formwright-ship` skill；瀏覽器驗證用 `formwright-demo-verify` skill。
- **有畫面的改動**合併前要在本機（`scripts/unattended/local-demo.sh`）的 390、820、1366、1920 四種寬度驗過，不得橫向捲動。
- **git**：逐檔 `git add`，不用 `-A`／`.`、不用 `--no-verify`、不 force push、不 merge commit。

## 文件落點

- 進度：路線文件的工作段表格（`docs/superpowers/specs/2026-11-06-erp-roadmap-design.md`）。
- 裁決：寫回對應的設計文件；功能討論與規劃結果列進 `docs/feature-checklist.md`（改完跑清單腳本重算）。
- 使用者看得到的行為改變：同步 `docs/manual.md`。
- 不屬於產品功能的跨 session 雜事（bug 調查、開發流程、清理）：`docs/todo.md`，做完就刪。
- 站內連結用相對路徑。

## 公開 repo

- 不得寫入公司名稱或公司相關字串（連防外洩的掃描指令也不寫出字串）、個人絕對路徑（`/Users/...`）、內網 IP。
- Demo 資料（人名、公司、地點）全部虛構。

## AI 設定的來源與副本

- 來源只有 `.claude/`（skills、rules、hooks、scripts）。
- `.agents/skills/`、`.agents/rules/` 是腳本產生的副本，給 Codex 等工具讀；**不要手改**，改 `.claude/` 後由 SessionStart hook 或 `node .claude/scripts/sync-codex-mirror.mjs` 重產並一起 commit。
