# 測試怎麼跑（給 AI agent）

完整 gate 約 30 分鐘、很吃 CPU。開發時只跑窄範圍，合併前才跑完整 gate。

## 測試專案

| 專案 | 位置 | 內容 |
|---|---|---|
| DomainTests | `backend/tests/DynamicModules.DomainTests` | 純領域邏輯，最快 |
| ApplicationTests | `backend/tests/DynamicModules.ApplicationTests` | 服務層、EF＋SQLite（雙跑集另跑 PostgreSQL） |
| WebAPITests | `backend/tests/DynamicModules.WebAPITests` | 整合測試，最慢、最怕 CPU 爭用 |
| 前端 | `frontend`（`*.vitest.spec.ts`） | vitest、vue-tsc、quasar build |

## 開發時：只跑窄範圍

```bash
# 後端：指定一個專案＋filter，不跑整個 sln
cd backend
dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~TaskRule"

# 前端：指定檔案，限制 worker 數
cd frontend
bunx vitest run src/application/dynamic/queries --maxWorkers=2
bun run typecheck
```

改到日期時間解析或報表日期時，本機加跑一次 `TZ=UTC dotnet test ... --filter ...`（開發機是台北時區，會掩蓋誤用主機時區的錯；CI 跑 UTC）。

改到共用的種子資料、應用程式套件或 i18n 檔，就擴大到 `SeedDataTests`、`AppCatalogChecks`、`locales.vitest.spec.ts`。

## PostgreSQL 雙跑集

正式版資料庫是 PostgreSQL。測試預設跑 SQLite；設 `FORMWRIGHT_TEST_DB=postgres` 時，共用的測試基礎（ApplicationTests 的 `TestDb`、WebAPITests 的 `TestApp`）改用 PostgreSQL（Windows 用 `set`／`$env:` 設同一個變數）。

- 伺服器：Testcontainers 起 `postgres:16-alpine`（要 Docker Desktop、OrbStack 或 colima），一個測試行程一個容器，跑完自動清掉。設了 `FORMWRIGHT_TEST_PG`（可以 `CREATE DATABASE` 的連線字串，例如 Homebrew 裝的本機 PostgreSQL）就改用它、不起容器。
- 資料隔離：第一次用到時建樣板資料庫並套 migration，之後每個 `TestDb`／`TestApp` 從樣板複製一個資料庫、結束時刪掉。
- 分類（xUnit trait）：`Category=Postgres` 是雙跑集，gate 在 SQLite 全套之外另在 PostgreSQL 上跑這一組；`Category=SqliteOnly` 只在 SQLite 有意義；`Category=PgDeferred` 是等 pg1 才會過的報表 SQL 路徑測試（要寫原因）。只在 PostgreSQL 有意義的案例用 `[PostgresFact]`，SQLite 跑時略過。
- **規則**：新增或修改 repository、並行、raw SQL、migration 相關的測試，類別加 `[Trait("Category", "Postgres")]` 進雙跑集，並在 PostgreSQL 跑過一次。raw SQL 的識別字一律加雙引號。
- 並行測試要證明「不靠序列化也正確」時，`TestApp` 設 `WithoutWriteSerialization = true`（無鎖模式：閘門與全庫鎖都不做事，只在 PostgreSQL 有作用），例如 `SchedulerNoLockTests`。
- 自己組 DI 的 ApplicationTests 用 `TestDb.AddProviderServices(services)` 註冊方言與交易管理，不直接寫 `SqliteDialect`／`EfTransactionManager`。

```bash
cd backend
# 窄範圍：一個類別在 PostgreSQL 上跑
FORMWRIGHT_TEST_DB=postgres dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~Ops.SchedulerTests"
# 雙跑集（gate 的 PostgreSQL 步驟）；命令列 filter 會蓋掉 csproj 的預設，要自己再排除 Benchmark
FORMWRIGHT_TEST_DB=postgres dotnet test tests/DynamicModules.ApplicationTests --filter "Category=Postgres&Category!=SqliteOnly&Category!=PgDeferred&Category!=Benchmark"
FORMWRIGHT_TEST_DB=postgres dotnet test tests/DynamicModules.WebAPITests --filter "Category=Postgres&Category!=SqliteOnly&Category!=PgDeferred&Category!=Benchmark"
```

全套在 PostgreSQL 上試跑（不進 gate，動到 Persistence、migration、交易或上線前手動跑）：把上面的 filter 換成 `Category!=SqliteOnly&Category!=PgDeferred&Category!=Benchmark`。

## 合併前：gate（GitHub Actions）

功能分支推上 GitHub 會跑 `.github/workflows/gate.yml`：後端三個測試專案、PostgreSQL 雙跑集（兩個專案各一個 job）、前端 test／typecheck／build 分開平行跑，範圍依改動決定（只改文件不跑、只改前端只跑前端、其他全跑）。用 `bash scripts/unattended/ci-wait.sh <sha>` 等結果。

- 公開 repo：推分支前先跑外洩掃描。
- 本機完整 gate `bash scripts/unattended/gate.sh <worktree 絕對路徑>` 留作備援；跑的時候同一台機器不跑其他測試或 build。
- 細節與失敗處理見 `formwright-ship` skill（`.claude/skills/formwright-ship/SKILL.md`）。

## 異常判讀

- **大量逾時、時間是平常好幾倍**：先查電腦有沒有睡過 `pmset -g log | grep -E "Entering Sleep|DarkWake to FullWake"`（macOS）。是睡眠或爭用就照原樣重跑，不改測試、不加長逾時。
- **只在完整 gate 失敗、單獨跑會過**：多半是 CPU 爭用，先單獨重跑那個專案確認。
- **PostgreSQL 測試環境無法啟動**：Docker 沒有在跑（`docker info` 失敗），或沒設 `FORMWRIGHT_TEST_PG`。不會默默跳過；gate 一開始會叫起 Docker Desktop，到 PostgreSQL 步驟前最多再等 60 秒。
- **`53300: too many connections` 或取不到池連線**：測試容器開了 `max_connections=300`；外部伺服器的上限可能較低，調高或改用容器。也檢查新測試是否沒 Dispose `TestApp`／`TestDb`（資料庫與連線池跟著它們清）。
- **`source database "fw_template_…" is being accessed by other users`**：有測試連到樣板資料庫上。樣板只在建立時連一次，測試要用 `PostgresTestServer.CreateDatabaseAsync()` 給的連線字串，不要自己連樣板。
- **只在 PostgreSQL 失敗的 `42P01 relation "dynamic_xxx" does not exist`**：raw SQL 的識別字沒加雙引號，PostgreSQL 把它折成小寫。
- **新增種子使用者或資料後計數斷言壞掉**：改種子前先 grep 既有測試對那些資料的斷言。
