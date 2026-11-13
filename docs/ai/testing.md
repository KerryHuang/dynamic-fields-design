# 測試怎麼跑（給 AI agent）

完整 gate 約 30 分鐘、很吃 CPU。開發時只跑窄範圍，合併前才跑完整 gate。

## 測試專案

| 專案 | 位置 | 內容 |
|---|---|---|
| DomainTests | `poc/backend/tests/DynamicModules.DomainTests` | 純領域邏輯，最快 |
| ApplicationTests | `poc/backend/tests/DynamicModules.ApplicationTests` | 服務層、EF＋SQLite |
| WebAPITests | `poc/backend/tests/DynamicModules.WebAPITests` | 整合測試，最慢、最怕 CPU 爭用 |
| 前端 | `poc/frontend`（`*.vitest.spec.ts`） | vitest、vue-tsc、quasar build |

## 開發時：只跑窄範圍

```bash
# 後端：指定一個專案＋filter，不跑整個 sln
cd poc/backend
dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~TaskRule"

# 前端：指定檔案，限制 worker 數
cd poc/frontend
bunx vitest run src/application/dynamic/queries --maxWorkers=2
bun run typecheck
```

改到共用的種子資料、應用程式套件或 i18n 檔，就擴大到 `SeedDataTests`、`AppCatalogChecks`、`locales.vitest.spec.ts`。

## 合併前：gate（GitHub Actions）

功能分支推上 GitHub 會跑 `.github/workflows/gate.yml`：後端三個測試專案、前端 test／typecheck／build 分開平行跑，範圍依改動決定（只改文件不跑、只改前端只跑前端、其他全跑）。用 `bash scripts/unattended/ci-wait.sh <sha>` 等結果。

- 公開 repo：推分支前先跑外洩掃描。
- 本機完整 gate `bash scripts/unattended/gate.sh <worktree 絕對路徑>` 留作備援；跑的時候同一台機器不跑其他測試或 build。
- 細節與失敗處理見 `formwright-ship` skill（`.claude/skills/formwright-ship/SKILL.md`）。

## 異常判讀

- **大量逾時、時間是平常好幾倍**：先查電腦有沒有睡過 `pmset -g log | grep -E "Entering Sleep|DarkWake to FullWake"`（macOS）。是睡眠或爭用就照原樣重跑，不改測試、不加長逾時。
- **只在完整 gate 失敗、單獨跑會過**：多半是 CPU 爭用，先單獨重跑那個專案確認。
- **新增種子使用者或資料後計數斷言壞掉**：改種子前先 grep 既有測試對那些資料的斷言。
