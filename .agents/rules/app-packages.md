---
paths:
  - "poc/backend/apps/**"
  - "poc/backend/src/DynamicModules.Application/Dynamic/DemoService/**"
  - "poc/backend/src/DynamicModules.Persistence/Seed/**"
trigger: always_on
---

# 應用程式套件與 Demo 種子

- 套件只能用核心定義組成（AGENTS.md 鐵則 1、6）：缺的能力回頭補成通用能力；共用主檔套件 `partner`、`contact` 由其他套件擴充，不另建。
- 改套件內容要升 `manifest.version`；用到新格式的欄位要升 `formatVersion`（各 `*.app.json` 開頭，格式定義在 `apps/app.schema.json`），並確認舊格式帶新欄位會被擋。
- 動 `poc/backend/apps/*.app.json` 的兩項工作不並行。
- Demo 每次部署重建，種子日期用相對天數。
- 改套件或種子前 grep 既有測試（`AppCatalogChecks`、`SeedDataTests`、各情境測試）對那些資料的斷言。
