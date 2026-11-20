# 語系包 i4b Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 介面文字改成執行期從伺服器載入。內建四語的介面文字搬到 `shared/i18n/<code>/ui.json`，後端內嵌成內建層（兩份 Dockerfile、建置檢查、啟動自檢一起做）；新增 `GET i18n/ui/{code}`，由伺服器沿退回鏈補齊後回傳，依 `uiHash` 決定快取標頭；`auth/options` 與生效政策的 `locales` 加 `uiHash`。前端只打包 zh-TW，啟動時載入目前語系（stale-while-revalidate＋瀏覽器快取＋3 秒上限），取到的文字先預編譯、壞句換成繁中；`ensureLocaleMessages` 讓兩個多語產生點涵蓋每個已安裝語系；系統管理員可在自己的分頁試看未啟用語系（M2）。使用者看得到的行為：**Demo 上無變化**（首屏多一次請求）；冒煙是登入頁切 English、ไทย。

**Architecture:** 內建層的唯一來源是 `shared/i18n/<code>/{ui,locale}.json`。後端由 Application 內嵌（`BuiltinLocaleResources` 讀資源、壓平成 `鍵 → 句子`），Domain 的純函式 `UiTextMerge` 依「要求的語系 → 上層語系 → 系統預設 → 繁中」逐鍵補齊，`LocaleRegistry` 依（語系、預設語系）快取合併結果與雜湊，快取掛在不可變快照上、registry 換新就整份換掉。WebAPI 的 `I18nController` 只轉接並依「請求的 `v` 是否等於這台的雜湊」設 `Cache-Control`。前端新增 `src/i18n/ui-messages.ts`（取文字、預編譯、轉巢狀、`setLocaleMessage`、瀏覽器快取、`ensureLocaleMessages`）與 `src/i18n/startup.ts`（boot 的載入順序、與 auth guard 共用同一次 `auth/options`）；boot 只 import 繁中。

**Tech Stack:** .NET 8、EF Core（SQLite 研發、PostgreSQL 正式）、MediatR、xUnit＋FluentAssertions；Vue 3、Quasar 2、TanStack Query、vue-i18n 11、vitest、bun。不新增套件；`@intlify/message-compiler` 已是 vue-i18n 的相依，前端正式程式要直接 import，改在 `package.json` 明列、版本對齊 lockfile（判斷 11）。

**Spec:** `docs/superpowers/specs/2026-11-17-language-packs-design.md`（§1 寫死清單的 i4b 項目、§3、§4.4、§8 全部、§13.2–13.3、§14、§15、§15.1、§17、§18 第 4、8、11 點、§19 執行期載入／多台競態／內建層完整性／試看、§20 i4b 列、§22）。前置計畫：`docs/superpowers/plans/2026-11-17-language-packs-i4a.md`（i4a 已上線 `3d0dd59b`；判斷 1、12、16、17、22 與「待決」節）。repo 規範：`AGENTS.md`、`.claude/rules/{backend,frontend,docs,app-packages}.md`、`CONTRIBUTING.md`、`docs/ai/testing.md`。

**路徑慣例：** 後端相對 `backend/`，前端相對 `frontend/`，`shared/`、`scripts/`、`Dockerfile*` 與文件相對 repo 根目錄。行號以 main `56c7437e` 為準；不符時以符號為準。

---

## 第二輪修正對照（codex 審查 `5a682b92`，結論「修正後可開工」；審查全文在本機暫存，不進版控）

| # | 審查意見 | 改在哪裡 |
|---|---|---|
| 必修 1 | `*\ui.json` 配 `%(RecursiveDir)` 會丟掉語系目錄，四份撞成同名資源 | 判斷 4 改 `**`，建置檢查數實際內嵌的項目、擋重名；Task 3 加 `ResourceNames_EightUnique` |
| 必修 2 | Task 3 用到 Task 4 才建的 `Flatten`、Task 7 用到 Task 8 才建的 `locale-preview.ts`；`GetEffectivePoliciesHandler` 沒有 `CachedSystemPolicy` | `Flatten` 提前到 Task 3；`locale-preview.ts` 提前到 Task 7；判斷 10 改用這次回應的 `values["locale.default"]`；依賴新端點的 `auth/options` 測試移到 Task 5 |
| 必修 3 | 改成直接相依要同步 `bun.lock`（CI 與 Dockerfile 用 `--frozen-lockfile`） | 判斷 11、Task 6 Files 與驗收（沿用 11.4.19） |
| 必修 4 | 登入頁 `useAuthOptions()` 漏列；永久共用的 promise 讓 Demo 產生期間的輪詢拿到舊資料 | 判斷 12 重寫：啟動那次的結果只被 boot、guard、登入頁第一次取用，之後照常發新請求，失敗不鎖；Task 7 加登入頁與 `demoGenerating: true → false` 案例 |
| 必修 5 | 3 秒上限只管 boot，guard 會重等 | 判斷 12、13：`startupOutcome()` 最晚在預算到時 resolve、永不 reject，guard 只消費它、不等文字；Task 7 加「`auth/options`／文字永不回」案例 |
| 必修 6 | 換新失敗後結構共享讓 watcher 不再觸發，停在舊文字 | 判斷 15 重寫：看 `dataUpdatedAt`、每次重取都比雜湊；版本協調（只裝「要的那版」、舊請求晚到不覆蓋）；文字請求逾時；Task 6、9 加案例 |
| 必修 7 | `FlowDesignerPage` 初始節點在載入完成前就產生 | 判斷 16：建初始圖與允許新增前等同一次載入（有預算）；Task 10 驗初始節點與慢網路 |
| 必修 8 | 重新整理後用啟用清單判斷試看值必然失敗；`loadLocaleSettings` 會覆蓋試看 | 判斷 13、17：啟動不套試看值；順序「確認身分 → 管理員取完整已安裝清單 → 驗試看值 → 載入套用」，各分支寫明；Task 8 案例 |
| 必修 9 | 登入頁的 `LocaleSwitcher` 加 `useMe()` 會多一次 `/me` 401 | 判斷 17：試看項目改由 `MainLayout` 以 prop 傳入（它已有 `useMe()`），元件本身不查身分；Task 8、11 驗 Network |
| 建議 | 用「檔數夠但缺鍵」證明 `Program.cs` 真的拒絕啟動 | Task 3 Step 4 |
| 建議 | 雜湊排序與快照一致性 | 判斷 5、7（`StringComparer.Ordinal`、`Ui()` 只讀一份 `Published`） |
| 建議 | `baseCompile` 通過不等於符合 §13.2 白名單；補純文字輸出案例 | 判斷 11；Task 6 案例 |
| 建議 | 補「從沒選過語言、沿用預設」的快取案例 | 判斷 13 第 1 步；Task 7 案例 |
| 建議 | 快取秒數等不必設成業務政策 | 已是技術限制，維持列進 §14 |

**第三輪（codex 短核對 `5d94a2bf`：原 9 條 7 條已修，另 4 條要修）**

| # | 意見 | 改在哪裡 |
|---|---|---|
| 1 | Task 6 的 `ensureLocaleMessages` 用到 Task 7 才建的 `STARTUP_BUDGET_MS` | 常數改由 Task 6 建在 `src/i18n/locale.ts`，Task 7 的 startup 匯入（判斷 13 第 5 步、16；Task 6、7 Interfaces） |
| 2 | 判斷 15「同語系有請求就不重發」與 Task 6 案例（c）「目標變 C 要發新請求」矛盾 | 判斷 15：只合併「同語系、同目標雜湊」；目標改變就發新請求、舊請求作廢，依序號與最新 `wanted` 決定是否裝上 |
| 3 | `readUiCache(code)` 回傳沒有 `code`，startup 卻用 `readUiCache()?.code`；「只存 `formwright.locale` 記的語系」排除了沿用預設的人 | 判斷 14：`readUiCache()` 無參數、回 `{ code, hash, messages }`；快取「目前實際採用的非試看語系」（含沿用預設）；Task 6 Interfaces 與案例 |
| 4 | 背景試看載入中登出或結束試看，舊回應晚到仍會 `applyLocale(ja)` | 判斷 17、19：`switchLocale` 記切換序號與試看世代，清除試看、登出、session 失效時作廢；套用前重確認；Task 8 加「載入中登出／選回啟用語系」案例 |

## 前置條件（開工前逐項確認，不符就停下回報）

1. **i4a 已在 main**：`git log --oneline main | grep -E "3d0dd59b|67edc2b4"` 有結果；`backend/src/DynamicModules.Application/Common/LocaleRegistry.cs`、`LocaleInfoResponse.cs`、`frontend/src/i18n/locale-settings.ts`（`setLocaleInfos`、`localeChain`、`useLocales`）都在。
2. **i4b 沒開工**：`grep -rn "uiHash\|UiHash\|ensureLocaleMessages\|shared/i18n" backend/src frontend/src shared` 只有註解（`LocaleInfoResponse.cs` 的「`uiHash` 在 i4b 再加」）。
3. **四份語系檔仍在 `frontend/src/i18n/`**，且 `grep -rlE "from 'src/i18n/(zh-TW|en-US|th|vi)\.json'" frontend/src | wc -l` 是 148（設計 §8.1 寫 139，是 2026-11-17 的數字；不符時以當下數字為準，記進交付說明）。
4. **沒有其他動 `frontend/src/i18n/*.json` 的工作在跑**（搬檔會讓對方的 rebase 衝突整份檔）。若有，先協調誰先合。
5. **本段沒有 migration**，可以和會加 migration 的段平行（例如 i4c 不加 migration，可平行）。
6. **PostgreSQL 測試環境可用**（`docker info` 成功或設了 `FORMWRIGHT_TEST_PG`）；Task 11 的 Docker 映像冒煙也要 Docker。
7. **plugin 更新**（AGENTS.md「票」）：`claude plugin marketplace update plugharbor && claude plugin update tickets@plugharbor --scope project`，再 `/reload-plugins`；失敗就照舊版跑並記進回報。

## 子段切法（兩個實作者交棒點）

| 批 | 內容 | Task | 手寫行數（估） | migration |
|---|---|---|---|---|
| i4b-1 | 後端＋shared 搬家：搬檔與 import 改寫、`quasar-langs.json`、後端內嵌與建置檢查、啟動自檢、兩份 Dockerfile、合併與 `uiHash`、`GET i18n/ui/{code}` | 1–5 | ≈ 1050（另有約 190 行機械式 import 改寫） | 無 |
| i4b-2 | 前端載入：載入模組（預編譯、快取、`ensureLocaleMessages`）、啟動順序與只打包繁中、切換語系與試看（M2）、60 秒換新、兩個多語產生點、文件與走查 | 6–11 | ≈ 1200 | 無 |

同一分支 `i4b`（worktree `../formwright-i4b`），每個 Task 各自 commit。i4b-1 收尾跑一次窄範圍 PostgreSQL 雙跑（不跑 gate），交棒給 i4b-2 的實作者；i4b-2 收尾才跑 gate、合併。

**交棒條件（i4b-1 → i4b-2）**：Task 1–5 的驗收全過；前端照常打包四語（i4b-1 不改 `boot/i18n.ts` 的載入方式，只改 import 路徑），所以 i4b-1 單獨存在時使用者行為與 main 相同，`GET i18n/ui/{code}` 暫時沒有前端呼叫端。交棒說明寫明：端點的回應形狀（判斷 6）、`uiHash` 的意義（判斷 5）、`X-UI-Hash` 標頭、試看用的「已登入可取未啟用語系」規則。分界理由：後端合併與快取標頭有自己的 API 測試可以先審；前端的啟動順序、快取、預編譯是另一組風險，放同一批會讓單批超過 2000 行。

---

## 計畫期間的判斷

〔行為〕＝影響使用者看到或能做的事；〔技術〕＝實作選擇。已拍板的不再問。

1. 〔技術〕**搬檔用 `git mv`＋一個目錄 alias＋機械式改寫 import，不留轉出口**（設計 §8.1、§22 交給實作者選）。`git mv frontend/src/i18n/<code>.json shared/i18n/<code>/ui.json`（四份，保留歷史）；前端加目錄 alias `shared` → repo 根目錄的 `shared/`（`quasar.config.ts` 的 `build.alias`、`vitest.config.ts` 的 `resolve.alias`、`tsconfig.json` 的 `paths: { "shared/*": ["../shared/*"] }`），再用一條 `sed` 把 `'src/i18n/<code>.json'` 改成 `'shared/i18n/<code>/ui.json'`、`src/i18n/` 內 spec 的 `'./<code>.json'` 改成同一個 alias。理由：(a) 針對單一檔名的 alias 要排在 Quasar 自己的 `src` alias 前面才會命中，順序由 Quasar 合併 Vite 設定決定，`vue-tsc` 的 `paths` 也要另寫四條，任一邊漏掉就是建置正常、型別檢查失敗或反過來；目錄 alias 沒有順序問題。(b) JSON 不能轉出口，轉出口只能是 `.ts`，import 路徑一樣要改。(c) 改寫是 187 行、每行一處、同一條 sed 產生，單獨一個 commit，審查只要核對 sed 指令與 `git diff --stat`。既有三處 `../../../shared/...` 相對路徑（`locale.ts`、`rule-engine.ts`、`designer-state.ts`）不順手改（外科手術式修改）。
2. 〔技術〕**`shared/builtin-locales.json` 併進 `shared/i18n/<code>/locale.json`**（i4a 判斷 1「i4b 內嵌時併入」）。每份 `{ "code", "nativeName", "quasarLang", "order" }`，`order` 是 1–4（zh-TW、en-US、th、vi），因為各語系一個檔、列目錄沒有順序。刪除 `builtin-locales.json`；前端 `BUILTIN_LOCALES` 改用 `import.meta.glob('shared/i18n/*/locale.json', { eager: true, import: 'default' })` 依 `order` 排序；後端 `BuiltinLocalesFixtureTests` 改讀這四份。
3. 〔技術〕**Domain 的 `BuiltinLocales` 保留 C# 四列，不改成讀資源**（與 i4a 判斷 1「改成讀資源」不同，理由如下）。Domain 規則是純函式、不碰 IO；讀內嵌資源若放在 static 初始化，資源缺漏會變成 `TypeInitializationException`，`I18nText.Resolve` 等所有用到它的地方一起壞，與設計 §8.1「啟動自檢不齊就 fail fast、訊息清楚」相反。改成：內嵌資源只由 Application 的 `BuiltinLocaleResources` 讀；`BuiltinLocales` 與 `locale.json` 的一致性由兩道守衛保證：`BuiltinLocalesFixtureTests`（測試期，讀 repo 的檔）與啟動自檢（執行期，讀內嵌資源，判斷 4）。呼叫端不改。
4. 〔技術〕**建置檢查與啟動自檢**（設計 §8.1、M5）：
   - csproj（`DynamicModules.Application.csproj`）：屬性 `BuiltinI18nDir` 預設 `$(MSBuildThisFileDirectory)..\..\..\shared\i18n`、`SharedDir` 預設 `$(MSBuildThisFileDirectory)..\..\..\shared`；`<EmbeddedResource Include="$(BuiltinI18nDir)\**\ui.json;$(BuiltinI18nDir)\**\locale.json" LogicalName="i18n/%(RecursiveDir)%(Filename)%(Extension)" BuiltinI18n="true" />`、`<EmbeddedResource Include="$(SharedDir)\quasar-langs.json" LogicalName="quasar-langs.json" />`。**一定要用 `**`**（第二輪必修 1）：`%(RecursiveDir)` 是「`**` 比對到的那段目錄」，用單一 `*` 時是空字串，四份會撞成同一個 `i18n/ui.json`。
   - Target `CheckBuiltinI18n`（`BeforeTargets="BeforeBuild"`）檢查的是**實際要內嵌的項目**，不另外列一次目錄：`@(EmbeddedResource->WithMetadataValue('BuiltinI18n','true')->WithMetadataValue('Filename','ui')->Count())` 少於 4、`%(LogicalName)` 去重後的數量和項目數不同（重名）、或 `quasar-langs.json` 不存在，就 `<Error>`，訊息寫「內建語系檔不齊（shared/i18n/*/ui.json 少於 4 份或資源重名）：Docker 建置要 COPY shared/」。屬性可覆寫，所以驗收能用 `-p:BuiltinI18nDir=<空目錄>` 證明檢查有效，不必搬動檔案。
   - `RecursiveDir` 在 Windows 是反斜線：讀資源時把 `\` 換成 `/` 再解析代碼（跨平台）。資源名契約是 `^i18n/<代碼>/(ui|locale)\.json$`；`**` 也會收到更深一層的檔（例如有人誤放 `shared/i18n/zh-TW/x/ui.json`），`Load()` 遇到不符契約的名稱就列進 `Verify` 的問題清單，不默默略過。
   - 啟動自檢 `BuiltinLocaleResources.Verify(BuiltinResourceSet set) → IReadOnlyList<string>`（問題清單，空＝通過），檢查：內嵌的語系代碼集合等於 `BuiltinLocales.Codes`；每份 `locale.json` 的 `nativeName`、`quasarLang` 等於 `BuiltinLocales` 該列；四份 `ui.json` 都能解析、沒有空字串、壓平後的鍵集與 zh-TW 完全相同；`quasar-langs.json` 能解析且包含四個內建 `quasarLang`。`Program.cs` 在 `InitializeDatabaseAsync` **之前**呼叫，有問題就 log Critical 並丟 `InvalidOperationException`（fail fast，不退回）。放在 `--generate-demo-data` 分支之前，所以 `Dockerfile.demo` 在建映像時就會跑到它，部署檔漏了 `shared/` 在 Docker 建置階段就失敗，不會等到 Demo 上線。
5. 〔技術〕**`uiHash` 是合併結果的內容雜湊**：把補齊後的扁平物件依鍵排序、序列化成 UTF-8 JSON，取 SHA-256 的前 16 個十六進位字元；排序用 `StringComparer.Ordinal`（不受文化設定影響，前後端與每台伺服器結果相同）。設計 §8.2 的寫法是「輸入包含鏈上每一層的雜湊與 `locale.default`」，目的是「預設語系或上層翻譯改了，下游的雜湊要跟著變」；內容雜湊在內容有變時一定變、內容沒變時不變（例如預設語系改了但補進來的句子剛好相同），比組合輸入雜湊更貼近「網址相同＝內容相同」，也少一套組合規則。i4d1 加覆寫層時不必改雜湊規則。
6. 〔技術〕**`GET i18n/ui/{code}` 回扁平 JSON**（設計 §8.2、§5.1 的 `ui` 也是扁平鍵）：`{ "menu.home": "首頁", … }`，鍵依字母排序。前端轉回巢狀再 `setLocaleMessage`（不開 vue-i18n 的 `flatJson`，打包的繁中仍是巢狀，兩條路走同一個 `setLocaleMessage`）。合併規則：基準鍵集＝內建 zh-TW 壓平後的鍵；每個鍵依序取 [該語系的覆寫層（i4d1 才有，本段恆空）、該語系的內建層（內建語系才有）、上層語系的兩層（最多 3 層）、系統預設語系的兩層、zh-TW 的兩層] 第一個非空字串；基準以外的鍵丟掉。鏈取 `LocaleRegistry.FallbacksFor(code, default)`，**截到 zh-TW 為止**（`LocaleChain` 鏈尾的「其餘已安裝語系」對介面文字沒有意義：zh-TW 內建層完整，到它一定補齊）。
7. 〔技術〕**合併結果快取在 registry 的不可變快照上**：`Published` 加 `ConcurrentDictionary<(string Code, string Default), UiBundle> Ui`，快照換新（`Generation` 改變）時新快照帶一個空字典；同一組（語系、預設）第一次被要求時才算。`Ui()` 開頭只讀一次 `Volatile.Read(ref _current)`，組鏈用的語系清單與寫入的快取字典都來自這一份快照，不會「用新清單算、存進舊快照」。內建層是程式內嵌、整個行程不變，所以合併結果只會因為「已安裝清單或上層語系」（registry generation）與「預設語系」改變；i4d1 加覆寫層時把覆寫層的版本戳記併進 registry 的戳記（設計 §4.2 已規劃），本段不預留介面。
8. 〔技術〕**快取標頭**（設計 §8.2、T9）由 WebAPI 依 handler 結果設定，handler 回 `UiMessagesResult(string Hash, byte[] Json, bool Enabled)`：
   - `Enabled` 且請求的 `v == Hash` → `Cache-Control: public, max-age=31536000, immutable`。
   - `Enabled` 且 `v` 缺或不等 → `no-store`（打到還沒換新的另一台、或前端還不知道雜湊）。
   - 已登入取**未啟用**的語系（`Enabled == false`）→ `private, no-cache`。
   - 一律帶 `X-UI-Hash: <Hash>`、`Content-Type: application/json; charset=utf-8`。
   - 匿名取未啟用、未安裝、代碼格式不合法 → 404（不區分三者，不讓匿名者探出裝了哪些語系）。
   - 31536000 秒是 `immutable` 網址的慣用值，屬技術限制，列進設計 §14（Task 11），不是業務政策。
9. 〔技術〕**端點走 SessionMiddleware 的「登入與否都收」清單**（`IsAnonymousOptional`，現有 `ops/client-errors` 同一種）：GET 且路徑以 `/api/v3/dynamic/i18n/ui/` 開頭。有有效身分就當已登入，檢查不過就當匿名（不回 401），所以 cookie 過期的人在登入頁照樣取得到啟用語系。強制改密碼期間 optional 路徑不擋（同 `client-errors`），改密碼頁要顯示文字。WebAPI 傳 `SignedIn: User.Identity?.IsAuthenticated == true` 給 handler，啟用判斷在 handler（`.claude/rules/backend.md`：WebAPI 不放業務判斷）。
10. 〔技術〕**`LocaleInfoResponse` 加 `UiHash`（必有欄位）**，`auth/options` 的 `locales`（啟用語系）與 `policies/effective` 的 `locales`（全部已安裝）都帶，值是「該語系、目前預設語系」的雜湊。只加欄位，`enabledLocales`、`defaultLocale` 不動，`AuthApiTests.Options_IncludesLocaleSettings` 照過（i4a 判斷 12）。`LocaleInfoResponse.Of(InstalledLocaleInfo l)` 改成 `Of(InstalledLocaleInfo l, string uiHash)`。兩個呼叫端的預設語系來源不同（第二輪必修 2）：`GetAuthOptionsHandler` 已注入 `CachedSystemPolicy`，用 `policies.DefaultLocale`；`GetEffectivePoliciesHandler`（`PolicyHandlers.cs:158`）只有 `IPolicyReader`，用**同一份回應**的 `values["locale.default"]`（`GetString()`；不是字串或空值時退回 `I18nText.FallbackLocale`），所以回應裡的 `uiHash` 與同一份回應的預設語系一致，不另注入快取。
11. 〔技術〕**前端預編譯用 `@intlify/message-compiler` 的 `baseCompile`**（設計 §8.3 縱深防禦）：逐句 `baseCompile(msg, { onError })`，有錯就丟掉那句、改用打包的 zh-TW 同鍵；伺服器回了基準以外的鍵（不該發生）也丟。這個套件目前只是 vue-i18n 的間接相依（`locales.vitest.spec.ts` 已在用），前端正式程式直接 import 就要在 `package.json` 的 `dependencies` 明列，版本固定 `11.4.19`（`bun.lock` 裡 vue-i18n 用的那版），並**同步 `bun.lock`**（第二輪必修 3：CI 與兩份 Dockerfile 都用 `bun install --frozen-lockfile`，lockfile 的根相依宣告沒更新會直接失敗）；套件節點不增加、包體不變。**界線**：`baseCompile` 通過只代表不會讓渲染中斷，不代表符合設計 §13.2 的語法白名單（合法的 linked `@:`、複數 `|` 也編譯得過）；白名單是匯入時的後端驗證，屬 i4d1。本段內建層都是 repo 內的檔，由 `locales.vitest.spec.ts` 把關。翻譯一律當純文字輸出（§13.3），Task 6 補一個含 `<b>` 的句子渲染成文字的案例。開發模式（`import.meta.env.DEV`）把「取到的文字缺了哪些 zh-TW 鍵」與「丟掉的壞句」印到 console，正式環境不印。
12. 〔技術〕**首屏只打一次 `auth/options`：boot、guard、登入頁共用「啟動那一次」的結果，之後的重取照常發新請求**（第二輪必修 4、5）。設計 §8.2 寫「共用 TanStack Query 快取」，但 `quasar.config.ts` 的 boot 順序是 `pinia, theme, i18n, error-reporter, tanstack-query, axios`，i18n boot 跑的時候 QueryClient 還沒建立。`src/i18n/startup.ts` 提供兩個出口：
    - `startupOutcome(): Promise<StartupOutcome>`（`{ options: AuthOptions | null }`）：boot 開始時發出**唯一一次** `getAuthOptions()`；這個 promise **最晚在 `STARTUP_BUDGET_MS` 時 resolve**（逾時或失敗就是 `options: null`），**永不 reject**。auth guard 的 `loadLocaleSettings` 改成 await 它，不再自己呼叫 API，所以導覽最多等到預算用完；`null` 走既有斷線分支（`OFFLINE_LOCALES`，判斷 18）。
    - `takeStartupAuthOptions(): AuthOptions | undefined`：啟動那次**成功**且還沒被取用時回傳結果、取一次就清掉。`useAuthOptions()`（`application/dynamic/queries/auth-queries.ts:9`，登入頁用）的 `queryFn` 改成 `() => takeStartupAuthOptions() ?? dynamicApi.getAuthOptions()`：登入頁第一次掛載重用啟動結果；`demoGenerating` 期間每 2 秒的輪詢、之後的重取都是新請求。啟動那次失敗或逾時就不給，登入頁自己重發，失敗不會永久鎖住重試。
    - 啟動那次在預算用完之後才回來：照常交給背景流程（更新語系資訊與文字，判斷 13 第 4 步），也仍可被 `takeStartupAuthOptions` 取用一次。
    - `resetStartupForTests()` 給測試。
13. 〔技術〕**啟動順序**（設計 §8.2）由 `startLocale(i18n)` 實作，`boot/i18n.ts` 改成 async boot（Quasar 會等 boot 的 promise 完成才掛載 App）。**決定語系與載入文字都在這裡**，guard 只消費 `startupOutcome()` 設定語系資訊與政策（第二輪必修 5），不呼叫 `applyLocale`、不 await 任何文字請求：
    1. 起點 `code = readLocale() ?? readUiCache()?.code`：快取的語系就是上次實際使用的語系，包含「從沒選過、沿用系統預設」的人（建議收：否則這種人每次重新整理都用不到快取）。**試看值在啟動時不用**（判斷 17：要先確認身分與完整已安裝清單）。
    2. 有 `code` 且 `readUiCache()?.code === code`（判斷 14）→ 先 `installUiMessages(code, cached)`、`applyLocale(code, …, false)`，**不等網路就結束 boot**。背景等 `startupOutcome()`：`code` 已不在啟用清單、或使用者從沒選過（沒有 `formwright.locale`）而預設語系已改 → 改成預設語系（載入文字後套用）；否則雜湊不同才重取並換上（判斷 15 的版本規則）。
    3. 沒有快取 → await `startupOutcome()`（不超過預算）；`code ??= defaultLocale`；`code` 不在啟用清單 → 預設語系（原本 guard 的「記的語系已停用就套預設」搬到這裡）；在**剩餘預算**內 `loadUiMessages(code, uiHash)`；成功就 `installUiMessages`、寫快取、套用。
    4. 預算用完（`auth/options` 或文字沒回、或失敗）→ 套用 `code`（沒有 `auth/options` 時只在它屬於 `OFFLINE_LOCALES` 才套，否則繁中），畫面用打包的 zh-TW 顯示（`fallbackLocale`），背景繼續；之後取到就 `installUiMessages`，畫面直接換語言。
    5. 3 秒是設計 §14 已列的技術上限，寫成具名常數 `STARTUP_BUDGET_MS`，放在 `src/i18n/locale.ts`（Task 6 建，`ensureLocaleMessages` 的預設預算也用它；startup 匯入），註解指向 §14。
    - zh-TW 使用者也要取一次（設計 §8.2：可能有覆寫，U1）；打包檔的雜湊在建置時算不出來（前後端雜湊要逐位元一致），所以 zh-TW 一律取，取到前先用打包版本顯示（打包版本就是繁中內建層，不會閃成別的語言）。
14. 〔技術〕**瀏覽器快取只存一份**：`localStorage` 鍵 `formwright.ui`，值 `{ code, hash, messages }`（扁平）。寫入前先刪掉舊的一份（同一個鍵，覆寫即可）；`setItem` 丟 `QuotaExceededError` 或 localStorage 不可用時不存、不報錯。存的是**目前實際採用的非試看語系**：使用者選的（`formwright.locale`），或從沒選過、沿用系統預設的那個語系（第三輪 3）；**試看中的語系不寫快取**（試看只在分頁內，判斷 17），`ensureLocaleMessages` 載入的其他語系也不寫。讀取介面 `readUiCache()` 不帶參數，回整份 `{ code, hash, messages }`（判斷 13 第 1 步要用 `code`）；讀取時驗形狀（`code`、`hash` 是字串、`messages` 是物件），不符當沒有快取。
15. 〔技術〕**換新與版本協調**（設計 §8.2「已開著的頁面」；第二輪必修 6）：
    - `use-business-time-watch.ts` 改看 `query.dataUpdatedAt`（每次成功重取都會變，不受 TanStack Query 結構共享影響），每次都先 `applyLocalePolicies`、再 `refreshLoadedMessages(eff.locales)`。不能只靠「資料參考變了」：第一次拿到雜湊 B 但文字請求失敗，下一輪政策仍是 B、參考不變，watcher 就再也不會跑。
    - `ui-messages.ts` 對每個語系記 `wanted`（伺服器最新給的雜湊）、`loaded`（已裝上的雜湊）、`seq`（最後一次發出的請求序號）。`refreshLoadedMessages` 對每個已載入的語系（目前語系＋`ensureLocaleMessages` 載過的），`wanted !== loaded` 就重取。**只合併「同語系、同目標雜湊」的請求**（已有一個取 `wanted` 的請求在跑就不重發）；目標雜湊改了（例如 B 還沒回、`wanted` 已變 C）就發新請求，序號遞增，B 那次自動作廢（第三輪 2）。
    - 請求完成時：序號不是該語系最新的 → 丟掉（舊請求晚到不覆蓋新版，不論它先回還是後回）；回應的 `X-UI-Hash` 等於 `wanted` → 裝上、目前語系另寫快取；不等（打到還沒換新的另一台回了 A）→ 不裝、維持「待重試」，下一輪重取再試；失敗同樣待重試。例外：該語系**還沒有任何文字**時（啟動第 4 步的背景載入），不等也先裝上，總比沒有好，之後照規則換新。
    - 文字請求帶逾時 `UI_FETCH_TIMEOUT_MS = 15000`（axios 實例沒有全域逾時，永不回的請求會讓「進行中」卡住、待重試永遠不發生）；屬技術限制，列進 §14。
    - 不提示、不重新整理。登入頁（匿名）沒有這個輪詢，登入後才有，不另加。
16. 〔技術〕**`ensureLocaleMessages(codes, { budgetMs = STARTUP_BUDGET_MS }): Promise<LocaleCode[]>`**（設計 §8.4、i4a 判斷 16）：對每個代碼，已載入且雜湊等於 `localeInfo(code)?.uiHash` 就略過；否則並行載入（走判斷 15 的同一套版本協調）；**最晚在 `budgetMs` 時 resolve**，回傳「現在有文字的語系」（含打包的 zh-TW），沒回來的請求在背景繼續。單一語系失敗不丟例外（只是不在回傳清單），呼叫端只為回傳清單裡的語系產生文字（設計 §8.4「取不到的語系就不填那一格」）。同一語系同時被要求兩次共用同一個進行中的 promise。
    - `LedgerTab.vue`：建立沖銷欄位的 mutation 先 `await ensureLocaleMessages(installedLocales())`（有預算，不會讓按鈕無限轉圈），用它的回傳值產生標籤（比「元件掛載時先取」可靠：使用者在掛載後很快按下也不會漏）。**順帶修正**：現在的 `const { availableLocales } = useI18n()` 是建立當下的陣列快照，執行期 `setLocaleMessage` 之後不會更新；改用回傳清單後這個解構一併移除。
    - `designer-graph.ts` 的 `approvalNames`／`conditionLabels` 是同步純函式、在很多地方被呼叫：維持同步，`nameLocales()` 改成 `installedLocales().filter(hasUiMessages)`（`hasUiMessages` 每次呼叫時讀 `i18n.global.availableLocales`，不留快照）。`FlowDesignerPage.vue` 在 setup 時呼叫一次 `ensureLocaleMessages(installedLocales())`，完成後把 `namesReady` 設成 true；載入流程的 watcher（`FlowDesignerPage.vue:56`，`designer.load(null)` 會經 `newFlowState()` 產生初始簽核節點）多一個條件 `namesReady`，所以**建立初始圖與允許新增節點都在同一次載入嘗試完成之後**（第二輪必修 7；只打包繁中後，不等的話 Demo 上新流程的初始節點會只剩繁中）。等待期間頁面維持既有的載入中狀態；預算用完仍沒回來的語系照「取不到就不填」處理。
    - 已知限制（寫回設計 §15）：產生點拿到的是**補齊後**的文字，`ja` 缺的那句會寫進上層語系（例 English）的譯文；日後補了日文翻譯，已建立的欄位標籤不會跟著改。這和設計 §9.1 `AsI18n`「之後才裝的語系看到退回值」同性質。
17. 〔行為，M2 已裁決；細節見待決 Q2〕**管理員試看**：`src/i18n/locale-preview.ts`（Task 7 建）匯出 `previewLocale()`、`setPreviewLocale(code | null)`，存 `sessionStorage` 鍵 `formwright.locale.preview`（不碰 `localStorage` 的 `formwright.locale`）。
    - **選單**：`LocaleSwitcher.vue` 加 prop `allowPreview: boolean`（預設 false），本身**不查身分**；`MainLayout.vue` 已有 `useMe()`，傳 `:allow-preview="me?.isSystemAdmin === true"`；`LoginPage.vue` 不傳，所以公開頁面不會多一次 `/me`（第二輪必修 9）。`allowPreview` 為 true 時，啟用語系之後加分隔線與「已安裝但未啟用」的語系（`installedLocales()` 減 `enabledLocales()`），每項顯示 `{自稱}（{t('locale.notEnabled')}）`、`data-testid="locale-preview-<code>"`。選它 → `setPreviewLocale(code)` 後切換（不寫 `formwright.locale`、不寫瀏覽器快取）；選啟用語系 → `setPreviewLocale(null)` 再照常切換並記住。
    - **重新整理後**（第二輪必修 8）：啟動時不套試看值（判斷 13），由 auth guard 依序處理：確認身分（`/me`）→ 系統管理員才繼續 → `loadSessionSettings()` 取生效政策（含**完整已安裝清單**；`auth/options` 只有啟用語系，不能拿來判斷）→ 試看值在 `installedLocales()` 內 → 背景 `switchLocale(code, { preview: true })`（不阻塞導覽；載入失敗就提示並清除）。各分支：
      | 狀況 | 處置 |
      |---|---|
      | 匿名（`/me` 401） | 清除試看 |
      | 強制改密碼（政策 API 會 403） | 清除試看 |
      | 不是系統管理員 | 清除試看 |
      | 系統管理員，`loadSessionSettings` 失敗（導向服務無法使用頁） | 保留，下次成功載入時再驗 |
      | 系統管理員，試看值已不在已安裝清單 | 清除試看 |
      | 系統管理員，試看值已安裝 | 載入並套用 |
    - **進行中的試看切換要能作廢**（第三輪 4）：`locale-preview.ts` 另記一個試看世代 `previewEpoch`，`setPreviewLocale(null)`、登出、`handleUnauthorized`（session 失效）都遞增它。`switchLocale` 開始時記下（全域切換序號、`previewEpoch`、`useIdentityStore().userId`），文字回來、**`applyLocale` 之前**重新確認：切換序號仍是最新（後來又選了別的語言就作廢，試看與一般切換都適用）；試看切換另要 `previewEpoch` 沒變、`previewLocale() === code`、`userId` 沒變。不符就只裝文字、不套用、不提示。
    - 清除時若畫面語系正是試看值，切回 `readLocale() ?? defaultLocale()`。登出（`useLogout` 成功）與 `handleUnauthorized` 也清除。因為啟動不套試看值，`loadLocaleSettings` 不會覆蓋它；代價是試看中的管理員重新整理後，會先短暫顯示原語系、確認後才換成試看語系（只有管理員、只在試看中）。
    - 權限 fail closed：前端狀態不能讓一般使用者留在未啟用語系；後端本來就把未啟用的 `X-Locale` 當預設語系（p3 判斷 14），匿名取未啟用語系的文字是 404（判斷 8）。
18. 〔行為，原 Q1，主控裁決待使用者確認，見待決 Q1〕**API 斷線時語言選單列內建四語**（來源 `BUILTIN_LOCALES`＝`shared/i18n/*/locale.json`），與 i4a 判斷 22 相同。i4b 之後選了瀏覽器沒有文字的語系，切換流程取不到文字，照設計 §8.2「切換失敗提示並停在原語系」處理：目前是繁中就停在繁中（即「退回繁中」），目前是有快取的泰文就停在泰文。實作成單點可改：具名常數 `OFFLINE_LOCALES = BUILTIN_LOCALES` 定義在 `src/i18n/locale.ts`（`BUILTIN_LOCALES` 旁邊），startup（判斷 13 第 4 步）與 auth guard 的斷線分支都只引用它；改成只列繁中時只改這一行（`BUILTIN_LOCALES.filter((l) => l.code === FALLBACK_LOCALE)`）與它的測試預期。
19. 〔行為，依設計 §8.2〕**切換語系**：`switchLocale(code, { preview })`（`ui-messages.ts`）先 `ensureCurrentMessages(code)`；載入中選單項目顯示 `q-spinner` 並停用其他項目；成功且通過判斷 17 的作廢檢查才 `applyLocale`；失敗用 `Notify` 顯示 `t('locale.loadFailed', { name: nativeName(code) })`（「無法載入{name}，請稍後再試」）並停在原語系。新句子三句：`locale.notEnabled`（未啟用）、`locale.loadFailed`、`locale.loading`（載入中，給 spinner 的 `aria-label`），四語都給、單行 JSON（`.claude/rules/frontend.md`）。
20. 〔技術〕**測試環境照常有四語文字**：`vitest.setup.ts` 從 `shared/i18n/*/ui.json` import 四份，對 en-US、th、vi 呼叫 `i18n.global.setLocaleMessage`（zh-TW 已打包）。理由：上百個既有 spec 掛載元件後切 English 斷言英文文案，那是元件行為測試、不是載入測試；載入行為由 Task 6、7 的新 spec 測：vue-i18n 沒有安全的「移除某語系文字」API，所以 `startup`／`ui-messages` 的 spec 各自 `createI18n` 一個只有繁中的乾淨實例傳入受測函式，這兩個模組的函式因此**接收 i18n 實例參數**、不直接 import boot 的單例；`designer-graph`、`LedgerTab` 的 spec 用單例，案例內用沒有預載的代碼（`ja`）測。
21. 〔技術〕**`shared/quasar-langs.json`**（設計 §8.5、T2、§22）：`scripts/i18n/gen-quasar-langs.mjs`（手動執行的 node 腳本，列 `frontend/node_modules/quasar/lang/*.js` 檔名、去掉 `.js`、排序，寫成 JSON 陣列，一行一個）；`frontend/src/i18n/quasar-langs.vitest.spec.ts` 比對它與 `import.meta.glob('../../node_modules/quasar/lang/*.js')` 的鍵完全相同（升級 Quasar 後語言包變動會讓這個測試失敗，提示重跑腳本）。後端內嵌，`BuiltinLocaleResources.QuasarLangs` 供 i4d1 的安裝驗證；本段的用途是啟動自檢（內建四語的 `quasarLang` 都在清單內）與 `LocaleRegistry` 讀到資料表 `installed` 列的 `QuasarLang` 不在清單時當成 `null` 並記 Warning（只可能來自資料修補）。
22. 〔技術〕**gate 不加 Docker 映像建置**。判斷 4 的建置檢查讓 Docker 裡的 `dotnet publish` 在缺 `shared/` 時直接失敗，啟動自檢讓 `Dockerfile.demo` 建映像時（`--generate-demo-data`）就失敗，兩道都在建置期擋下設計 §8.1 擔心的「悄悄全部變繁中」；完整映像建置要十分鐘以上，放進每次 gate 不划算。設計 §19 的「Docker 映像冒煙」在 Task 11 本機做一次，結果寫進交付說明。`gate.yml` 不改（`shared/` 與 `Dockerfile*` 的變更本來就觸發前後端全跑）。

---

## 待決

i4b 開工不需要等這兩題；計畫照推薦實作，兩題都做成單點可改。

- **Q1（原 i4a 待決 Q1）伺服器連不上時，語言選單列哪些語言？** 主控裁決（2026-11-20，待使用者確認）＝**維持列內建四語**。
  - 背景：i4b 起前端只打包繁中，其他語言的文字要從伺服器取；斷線時若這個瀏覽器沒存過那個語言，就沒有文字可顯示。
  - 推薦：維持列內建四語（判斷 18）。選到沒存過的語言時提示「無法載入〇〇，請稍後再試」並停在原語言。好處：和現在（i4a）的選單一樣，存過泰文的泰籍使用者斷線時仍能切回泰文。
  - 選錯代價：改成「只列繁中」，斷線時存過泰文的人也只能看繁中選項（雖然他目前畫面仍是泰文），選單和畫面語言不一致；維持四語的代價是斷線時選英文會跳一次「無法載入」的提示。改動點只有 `src/i18n/locale.ts` 的 `OFFLINE_LOCALES` 一行與測試。
- **Q2 管理員「試看」未啟用語系，重新整理分頁後要不要留在試看中？**
  - 背景：設計 M2（使用者 2026-11-18 確認）寫「試看存 `sessionStorage`、只在該分頁生效」；設計 §19 的測試重點卻寫「重新整理分頁後回到原語系」。`sessionStorage` 的特性是同一分頁重新整理後**還在**、關掉分頁才消失，兩句互相矛盾。
  - 推薦：**照 M2，重新整理後仍在試看**（關掉分頁或在選單選回啟用語系才結束），並把 §19 那句改成「開新分頁時是原語系」。理由：試看時常要重新整理看新匯入的翻譯，每次都跳回原語系很煩；一般使用者完全不受影響（只有管理員看得到試看項目，判斷 17 另有 guard 清除）。
  - 選錯代價：改成「重新整理就結束試看」要把試看值改存記憶體（`sessionStorage` 換成模組變數），管理員每次重新整理都要重選；照推薦的代價是管理員可能忘了自己在試看（選單上該項目標「未啟用」可看出來）。改動點只有 `locale-preview.ts` 的儲存方式。

**留到之後（不在 i4b 問）**：警示信選了沒有信件翻譯的語系 → i4c 前問（i4a 待決第二條，不變）。

---

## Global Constraints

- **使用者看得到的行為不變**：內建四語的語言選單、文字、切換結果與 main 相同；新增的只有（a）切換語言時的短暫載入狀態、（b）載入失敗的提示、（c）管理員的試看項目（i4b 沒有安裝語系的入口，Demo 上看不到）。既有測試的預期值不改，例外只有：
  - `BuiltinLocalesFixtureTests` 改讀 `shared/i18n/*/locale.json`（判斷 2）。
  - `locales.vitest.spec.ts`、`docflow-i18n.vitest.spec.ts`、`manual-permission-axes.vitest.spec.ts` 的 import 路徑（判斷 1）。
  - `AuthApiTests`、`PolicyApiTests` 對 `locales` 每筆欄位的逐欄斷言多了 `uiHash`（若有用整筆物件比對）。
  - `LedgerTab`、`designer-graph` 的 i4a 案例「沒有翻譯包的 `ja` 不產生 `ja` 鍵」改成「API 取得到時產生、取不到時不產生」（判斷 16）。
  - `locale.vitest.spec.ts` 的 `BUILTIN_LOCALES` 來源（內容不變）。
- **鐵則**：介面文字只有一個來源（`shared/i18n/<code>/ui.json`，前端打包的 zh-TW 是同一份檔，不是副本）；啟用、預設、必填仍只在 p3 政策；權限 fail closed（匿名只拿啟用語系、試看只給系統管理員，前後端都擋）；驗證不只在前端（合併、基準鍵集在後端；前端預編譯是縱深防禦）。
- **可寫死、要列清單的**（交付說明列出，Task 11 寫進設計 §14）：啟動等待 3 秒（§14 已列）；`immutable` 的 `max-age=31536000`；`uiHash` 取 SHA-256 前 16 個十六進位字元；瀏覽器快取只存一份；文字請求逾時 15 秒；試看存 `sessionStorage`；介面文字的退回鏈截到繁中；建置檢查「`ui.json` 至少 4 份」；`quasar-langs.json` 由腳本產生。
- **PostgreSQL**：本段沒有 migration、沒有 raw SQL、沒有新 repository；用到 `LocaleTestKit`（直接寫 `Dynamic_Locale`）的 API 測試照 i4a 慣例標 `Category=Postgres` 並在 PostgreSQL 跑過一次（`I18nUiApiTests`、`AuthApiTests`／`PolicyApiTests` 的新案例）。
- **後端新的使用者訊息**：無（404 不帶句子，沿用 `ServiceResult.NotFound(null)`）。
- **前端新字串**：判斷 19 的三句，四語、單行 JSON，寫在 `shared/i18n/<code>/ui.json`。
- **有畫面的改動**（語言選單的載入狀態、試看項目、失敗提示）在 390、820、1366、1920 四種寬度不橫捲、不露出鍵名。
- **測試只跑窄範圍**；後端、前端、gate 不並行；vitest 加 `--maxWorkers=2`。本機只跑新增／修改的測試類別、`bun run typecheck`、`bun run build`、全域守衛 spec（`src/css/themes/` 的兩個寫死顏色 spec、`locales.vitest.spec.ts`）；完整套件交 CI。
- **git**：逐檔 `git add`（`git mv` 已暫存的不重加）；commit 前 `git status`；不用 `--no-verify`、不 force push、不 merge commit；公開 repo 不寫公司字串、個人絕對路徑、內網 IP。
- 段收尾用 `formwright-purpose-check`；上線用 `formwright-ship`。

## 檔案地圖

**共用檔（並行工作要協調；★＝本段會動）**

| 共用檔 | 本段 | 說明 |
|---|---|---|
| ★ i18n 語系檔 | Task 1、8 | `frontend/src/i18n/*.json` 整份 `git mv` 到 `shared/i18n/<code>/ui.json`；Task 8 加三句 |
| ★ `shared/` | Task 1、2 | 新增 `shared/i18n/<code>/{ui,locale}.json`、`shared/quasar-langs.json`；刪除 `shared/builtin-locales.json` |
| ★ 前端路由與選單 | Task 7、8 | `router/auth-guard.ts`（消費啟動結果、試看驗證、斷線清單常數）；`MainLayout.vue` 只多傳一個 prop 給語言選單；`routes.ts`、選單結構不動 |
| ★ 前端建置設定 | Task 1、6、7 | `quasar.config.ts`、`vitest.config.ts`、`tsconfig.json`（alias）、`package.json` 與 `bun.lock`（判斷 11）、`vitest.setup.ts` |
| ★ 後端啟動與 middleware | Task 3、5 | `WebAPI/Program.cs`（啟動自檢）、`WebAPI/Security/SessionMiddleware.cs`（optional 路徑） |
| ★ 部署檔 | Task 3 | `Dockerfile`、`Dockerfile.demo` 後端 stage |
| 後端 DI 註冊 | 不動 | handler 由 MediatR 掃描；`LocaleRegistry` 已是 singleton |
| DbContext／migration | 不動 | 本段沒有 schema 變更 |
| `backend/apps/*.app.json` | 不動 | 套件語系鍵放寬是 i4f |

**後端（i4b-1）**

- Create: `backend/src/DynamicModules.Domain/Localization/UiTextMerge.cs`（Task 3 建 `Flatten`，Task 4 加合併與雜湊）、`backend/src/DynamicModules.Application/Common/Localization/BuiltinLocaleResources.cs`、`backend/src/DynamicModules.Application/Dynamic/LocaleService/Queries/GetUiMessages/{GetUiMessagesRequest,GetUiMessagesHandler}.cs`、`backend/src/DynamicModules.WebAPI/Controllers/v3/Dynamic/I18nController.cs`
- Modify: `DynamicModules.Application.csproj`、`Application/Common/LocaleRegistry.cs`、`Application/Common/LocaleInfoResponse.cs`、`Application/Dynamic/AccountService/Queries/GetAuthOptions/GetAuthOptionsHandler.cs`、`Application/Dynamic/PolicyService/PolicyHandlers.cs:174`、`WebAPI/Program.cs`、`WebAPI/Security/SessionMiddleware.cs`、`Dockerfile`、`Dockerfile.demo`
- Test: `DomainTests/Localization/{UiTextMergeTests,BuiltinLocalesFixtureTests}.cs`、`ApplicationTests/Common/{BuiltinLocaleResourcesTests,LocaleRegistryUiTests}.cs`、`WebAPITests/Dynamic/I18nUiApiTests.cs`、`WebAPITests/AuthApiTests.cs`、`WebAPITests/Dynamic/PolicyApiTests.cs`

**前端（i4b-1 Task 1–2；i4b-2 Task 6–10）**

- Create: `frontend/src/i18n/{ui-messages,startup,locale-preview}.ts` 與同名 `.vitest.spec.ts`、`frontend/src/i18n/quasar-langs.vitest.spec.ts`、`scripts/i18n/gen-quasar-langs.mjs`
- Modify: `frontend/src/boot/i18n.ts`、`frontend/src/i18n/{locale.ts,locale-settings.ts}`、`frontend/src/router/auth-guard.ts`、`frontend/src/application/dynamic/queries/auth-queries.ts`、`frontend/src/layouts/MainLayout.vue`、`frontend/package.json`、`frontend/bun.lock`、`frontend/src/presentation/locale/LocaleSwitcher.vue`、`frontend/src/application/dynamic/queries/use-business-time-watch.ts`、`frontend/src/presentation/dynamic/ledger/LedgerTab.vue`、`frontend/src/presentation/dynamic/approval-designer/graph/designer-graph.ts`、`frontend/src/pages/system/dynamic-modules/FlowDesignerPage.vue`、`frontend/src/domain/dynamic/types.ts`（`LocaleInfo` 隨 `locale-settings.ts`）、148 個 import 繁中語系檔的檔案（機械改寫）

**文件（Task 11）**：`docs/roadmap.md`、設計 §8、§14、§15、§18、§19、§20 補註、`docs/feature.md`、`docs/manual.md` §20、`.claude/rules/frontend.md`、`AGENTS.md` 架構地圖、`.agents/` 鏡像（腳本重產）。

---

# i4b-1 後端＋shared 搬家

### Task 1: 內建介面文字搬到 `shared/i18n/`，前端改 import 路徑

**Files:**
- Move（`git mv`）：`frontend/src/i18n/{zh-TW,en-US,th,vi}.json` → `shared/i18n/{zh-TW,en-US,th,vi}/ui.json`
- Create: `shared/i18n/<code>/locale.json` 四份（判斷 2）；Delete: `shared/builtin-locales.json`
- Modify: `frontend/quasar.config.ts:11`、`frontend/vitest.config.ts:7`、`frontend/tsconfig.json`（alias `shared`，判斷 1）
- Modify: `frontend/src/i18n/locale.ts:2,7-12`（`BUILTIN_LOCALES` 改 glob `locale.json` 依 `order` 排序；註解改「單一來源是 `shared/i18n/<code>/locale.json`」）
- Modify（機械改寫，單獨 commit）：148 個 import `src/i18n/<code>.json` 的檔、`src/i18n/` 下三個用 `./<code>.json` 的 spec、`task-board-types.vitest.spec.ts:78`、`progress-graph.vitest.spec.ts:60-61` 的動態 import
- Modify: `backend/tests/DynamicModules.DomainTests/Localization/BuiltinLocalesFixtureTests.cs`（讀 `shared/i18n/*/locale.json`；`SharedFile` helper 若只支援根目錄檔名就加子路徑參數）
- Test: `frontend/src/i18n/locales.vitest.spec.ts`（改讀新位置，斷言不變）、`frontend/src/i18n/locale.vitest.spec.ts`（`BUILTIN_LOCALES` 順序與內容不變）

**Interfaces:**
- `shared/i18n/<code>/locale.json`：`{ "code": "th", "nativeName": "ไทย", "quasarLang": "th", "order": 3 }`。
- 前端 `BUILTIN_LOCALES: LocaleInfo[]` 形狀不變（`fallbackCode: null`；Task 6 加 `uiHash: ''`）。

- [ ] **Step 1: 寫失敗的測試**：`BuiltinLocalesFixtureTests` 改讀四份 `locale.json`（逐筆逐欄等於 `BuiltinLocales.All`、`order` 等於索引＋1）；`locale.vitest.spec.ts` 加 `BUILTIN_LOCALES.map(l => l.code)` 等於 `['zh-TW','en-US','th','vi']`。
- [ ] **Step 2: 跑測試確認失敗**：`cd backend && dotnet test tests/DynamicModules.DomainTests --filter "FullyQualifiedName~BuiltinLocalesFixture"`（檔案不存在而失敗）
- [ ] **Step 3: 實作**
  1. `git mv` 四份；建 `locale.json` 四份；`git rm shared/builtin-locales.json`。
  2. 三處 alias；`locale.ts` 改 glob。
  3. 機械改寫（commit A，只含這一步）：
     ```bash
     cd frontend
     grep -rlE "src/i18n/(zh-TW|en-US|th|vi)\.json" src | xargs sed -i '' -E "s#src/i18n/(zh-TW|en-US|th|vi)\.json#shared/i18n/\1/ui.json#g"
     grep -rlE "'\./(zh-TW|en-US|th|vi)\.json'" src/i18n | xargs sed -i '' -E "s#'\./(zh-TW|en-US|th|vi)\.json'#'shared/i18n/\1/ui.json'#g"
     ```
     （`sed -i ''` 是 macOS 寫法；Linux 用 `sed -i`。改完 `git diff --stat` 約 150 個檔、每檔 1–4 行，實際檔數記進交付說明。）
- [ ] **Step 4: 跑測試確認通過**：Step 2；`cd frontend && bunx vitest run src/i18n src/domain/dynamic/task-board-types.vitest.spec.ts src/presentation/dynamic/approval/progress-graph.vitest.spec.ts --maxWorkers=2`；`bun run typecheck`；`bun run build`；`grep -rnE "src/i18n/(zh-TW|en-US|th|vi)\.json|builtin-locales" src ../backend/src ../backend/tests ../shared` 無結果；`git log --follow --oneline shared/i18n/th/ui.json | head -3` 看得到搬家前的歷史
- [ ] **Step 5: Commit**：commit A `refactor(i18n): 前端語系檔 import 改指向 shared/i18n（機械改寫）`（含 `git mv` 與改寫）；commit B `refactor(i18n): 內建語系資訊併入 shared/i18n/<code>/locale.json`

**驗收**：Step 4 全過；`frontend/src/i18n/*.json` 不存在；前端仍打包四語（boot 只改路徑），行為不變。

### Task 2: `shared/quasar-langs.json` 與一致性測試

**Files:**
- Create: `scripts/i18n/gen-quasar-langs.mjs`、`shared/quasar-langs.json`（腳本產生後 commit）、`frontend/src/i18n/quasar-langs.vitest.spec.ts`

**Interfaces:**
- 腳本：`node scripts/i18n/gen-quasar-langs.mjs` → 寫 `shared/quasar-langs.json`（排序後的字串陣列，74 筆，一行一筆、結尾換行）。只用 `node:fs`、`node:path`，從 repo 根目錄或 `frontend/` 執行都可以（以腳本自身位置找路徑，跨平台）。
- spec：`Object.keys(import.meta.glob('../../node_modules/quasar/lang/*.js'))` 取檔名去 `.js` 排序，等於 JSON 內容；失敗訊息寫「Quasar 語言包有變動，執行 node scripts/i18n/gen-quasar-langs.mjs」；另驗 `BUILTIN_LOCALES` 的每個 `quasarLang` 都在清單內。

- [ ] **Step 1: 寫失敗的測試**（先寫 spec，JSON 還沒有）
- [ ] **Step 2: 跑測試確認失敗**：`cd frontend && bunx vitest run src/i18n/quasar-langs.vitest.spec.ts --maxWorkers=2`
- [ ] **Step 3: 實作**：寫腳本、執行、commit 產物
- [ ] **Step 4: 跑測試確認通過**：同 Step 2
- [ ] **Step 5: Commit**（`feat(i18n): Quasar 語言包清單 shared/quasar-langs.json 與一致性測試`）

**驗收**：Step 4 過；JSON 74 筆、含 `zh-TW`、`en-US`、`th`、`vi`。

### Task 3: 後端內嵌內建層、建置檢查、啟動自檢、兩份 Dockerfile

**Files:**
- Modify: `backend/src/DynamicModules.Application/DynamicModules.Application.csproj`（判斷 4 的屬性、`EmbeddedResource`、Target）
- Create: `backend/src/DynamicModules.Application/Common/Localization/BuiltinLocaleResources.cs`
- Create: `backend/src/DynamicModules.Domain/Localization/UiTextMerge.cs`（本 Task 只有 `Flatten`；第二輪必修 2）
- Modify: `backend/src/DynamicModules.WebAPI/Program.cs`（`InitializeDatabaseAsync` 之前呼叫自檢）
- Modify: `Dockerfile`、`Dockerfile.demo`（後端 stage `COPY backend/ ./` 之後加 `COPY shared/ /shared/`，註解：「後端內嵌 shared/i18n 與 quasar-langs.json（csproj 的 ..\..\..\shared 在這裡解析成 /shared）；少了這行 dotnet publish 會因建置檢查失敗」）
- Modify: `backend/src/DynamicModules.Domain/Localization/BuiltinLocales.cs:8`（註解改：「與 `shared/i18n/<code>/locale.json` 一致，由 `BuiltinLocalesFixtureTests` 與啟動自檢把關；不讀資源（i4b 計畫判斷 3）」）
- Test: `backend/tests/DynamicModules.ApplicationTests/Common/BuiltinLocaleResourcesTests.cs`（新）、`backend/tests/DynamicModules.DomainTests/Localization/UiTextMergeTests.cs`（新，本 Task 只寫 `Flatten` 的案例）

**Interfaces:**
- `public static class UiTextMerge { public static IReadOnlyDictionary<string, string> Flatten(JsonElement nested); }`：巢狀物件 → `a.b.c` 鍵；值不是字串或物件（陣列、數字）就丟 `FormatException`（`Verify` 接住後列成一條問題）。
- `public sealed record BuiltinResourceSet(IReadOnlyDictionary<string, string> UiJson, IReadOnlyDictionary<string, string> LocaleJson, string? QuasarLangsJson);`（代碼 → 原始 JSON 字串；給自檢與測試注入）
- `public static class BuiltinLocaleResources`
  - `public static BuiltinResourceSet Load()`：讀 `typeof(BuiltinLocaleResources).Assembly` 的 `i18n/<code>/ui.json`、`i18n/<code>/locale.json`、`quasar-langs.json`（資源名 `\` → `/`）。
  - `public static IReadOnlyList<string> Verify(BuiltinResourceSet set)`：判斷 4 的檢查，回傳問題清單（每項一句繁中，含代碼與鍵名，最多列前 20 個缺鍵）。
  - `public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Ui { get; }`（`Lazy`，代碼 → 壓平的內建層；`Load` 之後經 `UiTextMerge.Flatten`）、`public static IReadOnlyList<string> BaseKeys { get; }`（zh-TW 的鍵，排序）、`public static IReadOnlySet<string> QuasarLangs { get; }`。用 `Lazy<T>`，不在 static 建構子讀，避免型別初始化例外（判斷 3）。
- `Program.cs`：`var problems = BuiltinLocaleResources.Verify(BuiltinLocaleResources.Load()); if (problems.Count > 0) { log Critical（逐條）; throw new InvalidOperationException("內建語系資源不齊：" + string.Join("；", problems)); }`

- [ ] **Step 1: 寫失敗的測試**
  - `UiTextMergeTests.Flatten_Nested`：`{"a":{"b":"x","c":{"d":"y"}}}` → `a.b=x`、`a.c.d=y`；`Flatten_Array_Throws`。
  - `BuiltinLocaleResourcesTests.ResourceNames_EightUnique`（第二輪必修 1）：組件的 `GetManifestResourceNames()` 中，`\` 換成 `/` 後以 `i18n/` 開頭的恰好 8 筆、互不相同、全部符合 `^i18n/[A-Za-z0-9-]+/(ui|locale)\.json$`，代碼集合等於 `BuiltinLocales.Codes`；另有一筆 `quasar-langs.json`。
  - `Verify_UnexpectedResourceName_Reported`：`BuiltinResourceSet` 混入 `i18n/zh-TW/x/ui.json` 這種名稱 → 問題清單有一條。
  - `Embedded_HasFourBuiltinLocales`：`Load()` 的 `UiJson.Keys`、`LocaleJson.Keys` 都等於 `BuiltinLocales.Codes`（集合相等）。
  - `Embedded_VerifyPasses`：`Verify(Load())` 是空清單。
  - `Embedded_KeyCountMatchesRepo`：`Ui["th"].Count == BaseKeys.Count`，且等於直接讀 repo `shared/i18n/zh-TW/ui.json` 壓平後的鍵數（證明內嵌的就是 repo 那份）。
  - `Verify_MissingLocale_Reported`：拿掉 `vi` 的 `UiJson` → 問題清單含 `vi`。
  - `Verify_KeyMismatch_Reported`：`th` 少一個鍵、多一個鍵 → 各一條，含鍵名。
  - `Verify_EmptyString_Reported`、`Verify_BadJson_Reported`、`Verify_LocaleJsonDiffersFromBuiltinLocales_Reported`（`nativeName` 不同）、`Verify_QuasarLangsMissing_Reported`、`Verify_BuiltinQuasarLangNotInList_Reported`。
  - `QuasarLangs_Has74AndBuiltins`。
- [ ] **Step 2: 跑測試確認失敗**：`cd backend && dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~BuiltinLocaleResourcesTests"`；`dotnet test tests/DynamicModules.DomainTests --filter "FullyQualifiedName~UiTextMerge"`
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；`dotnet test tests/DynamicModules.DomainTests --filter "FullyQualifiedName~Localization"`；
  - **建置檢查有效**：`mkdir -p <scratchpad>/empty-i18n && dotnet build src/DynamicModules.Application -p:BuiltinI18nDir=<scratchpad>/empty-i18n` 失敗且訊息含「內建語系檔不齊」，不帶參數時成功。
  - **啟動自檢真的擋住啟動**（建議收）：把 `shared/i18n` 複製到 `<scratchpad>/broken-i18n`、刪掉 `th/ui.json` 的任一個鍵；`dotnet build src/DynamicModules.WebAPI -p:BuiltinI18nDir=<scratchpad>/broken-i18n` **成功**（檔數夠，建置檢查不管內容）；接著 `dotnet run --no-build --project src/DynamicModules.WebAPI` 以非零碼結束、log 有 Critical「內建語系資源不齊」與該鍵名。驗完不帶參數重建一次。
  - **正常啟動**：`dotnet run --project src/DynamicModules.WebAPI` 啟動正常（log 沒有 Critical）。
- [ ] **Step 5: Commit**（`feat(i18n): 後端內嵌內建介面文字，建置檢查與啟動自檢，Dockerfile 複製 shared`）

**驗收**：Step 4 全過；兩份 Dockerfile 的 diff 各一行加註解；Docker 實際建置在 Task 11 驗。

### Task 4: 介面文字合併、`uiHash`，`auth/options` 與生效政策帶 `uiHash`

**Files:**
- Modify: `backend/src/DynamicModules.Domain/Localization/UiTextMerge.cs`（加 `Merge`、`Serialize`）
- Modify: `backend/src/DynamicModules.Application/Common/LocaleRegistry.cs`（`Published` 加 `Ui` 快取字典；新方法 `Ui`、`UiHash`；`Build` 對不在 `QuasarLangs` 的 `QuasarLang` 當 null 並 Warning；類別註解補判斷 5–7）
- Modify: `backend/src/DynamicModules.Application/Common/LocaleInfoResponse.cs`（加 `UiHash`；`Of(l, uiHash)`；註解拿掉「`uiHash` 在 i4b 再加」）
- Modify: `Application/Dynamic/AccountService/Queries/GetAuthOptions/GetAuthOptionsHandler.cs:17`、`Application/Dynamic/PolicyService/PolicyHandlers.cs:174`（`GetEffectivePoliciesHandler`；預設語系取自 `values["locale.default"]`，判斷 10）
- Test: `backend/tests/DynamicModules.DomainTests/Localization/UiTextMergeTests.cs`（Task 3 已建，加案例）、`backend/tests/DynamicModules.ApplicationTests/Common/LocaleRegistryUiTests.cs`（新，純記憶體，沿用 `LocaleRegistryTests` 的假 repository 與 `ManualTimeProvider`）、`backend/tests/DynamicModules.WebAPITests/AuthApiTests.cs`、`backend/tests/DynamicModules.WebAPITests/Dynamic/PolicyApiTests.cs`

**Interfaces:**
- `UiTextMerge`（`Flatten` 在 Task 3）
  - `public static SortedDictionary<string, string> Merge(IReadOnlyList<string> baseKeys, IEnumerable<IReadOnlyDictionary<string, string>> layers)`：判斷 6；每鍵取第一個非空字串（`string.IsNullOrEmpty` 視為缺）；基準外的鍵不輸出；`SortedDictionary` 用 `StringComparer.Ordinal`。
  - `public static (string Hash, byte[] Json) Serialize(SortedDictionary<string, string> merged)`：`JsonSerializer.SerializeToUtf8Bytes`（`JavaScriptEncoder.UnsafeRelaxedJsonEscaping` 不用；用預設 encoder，泰文會被跳脫成 `\uXXXX`，前端解析結果相同、雜湊穩定），雜湊判斷 5。
- `public sealed record UiBundle(string Hash, byte[] Json, int KeyCount);`（Application/Common）
- `LocaleRegistry`：
  - `public UiBundle? Ui(string code, string defaultLocale)`：開頭讀一次快照（判斷 7）；沒安裝回 null；鏈＝`[code, ..FallbacksFor(code, defaultLocale)]` 截到 `zh-TW`（含）；每個語系的層＝`BuiltinLocaleResources.Ui.GetValueOrDefault(code)`（i4d1 在前面插覆寫層）；結果存 `Published.Ui[(code, defaultLocale)]`。
  - `public string UiHash(string code, string defaultLocale) => Ui(code, defaultLocale)?.Hash ?? ""`。
- `LocaleInfoResponse(string Code, string NativeName, string? QuasarLang, string? FallbackCode, string UiHash)`；`Of(InstalledLocaleInfo l, string uiHash)`。

- [ ] **Step 1: 寫失敗的測試**
  - `UiTextMergeTests`：
    - `Merge_FirstNonEmptyWins`：三層，第一層缺、第二層空字串、第三層有 → 取第三層。
    - `Merge_DropsKeysOutsideBase`、`Merge_OutputSortedByKey`。
    - `Merge_U3_ParentBeforeDefault`：`ja`（無內建層）→ 上層 `en-US` → 預設 `th` → zh-TW：`ja` 層有 `k1`、`en-US` 有 `k2`、`th` 有 `k2,k3`、zh-TW 全有 → `k1=ja`、`k2=en`、`k3=th`、`k4=zh`。
    - `Merge_U3_NoParent_UsesDefault`（設計 U3 例）：`ja` 無上層、預設 `th` → 缺的句子是泰文，不是繁中。
    - `Serialize_SameContentSameHash`、`Serialize_DifferentContentDifferentHash`、`Serialize_Hash16Hex`、`Serialize_OrdinalOrder`（`a.B` 排在 `a.a` 前，與文化設定無關）。
  - `LocaleRegistryUiTests`：
    - `Ui_Builtin_EqualsEmbedded`：出廠、預設 zh-TW → `Ui("th","zh-TW").KeyCount == BaseKeys.Count`，取一個鍵是泰文。
    - `Ui_InstalledJa_FallsToParent`：假 repository 加 `ja`（上層 `en-US`）→ `Ui("ja","th")` 的每個鍵等於 `en-US` 內建層；`UiHash("ja","th") == UiHash("en-US","th")`（內容相同就同雜湊，判斷 5）。
    - `Ui_InstalledJaNoParent_FallsToDefault`：`ja` 無上層、預設 `th` → 內容等於 `th`。
    - `UiHash_ChangesWithDefault`：`ja` 無上層，`UiHash("ja","th") != UiHash("ja","vi")`。
    - `UiHash_ChangesWhenParentChanges`：`ja` 上層從 `en-US` 改成 `vi`（戳記變、generation＋1）→ 雜湊變。
    - `Ui_Cached_SameInstance`：同參數兩次 `ReferenceEquals`；registry 換新後是新物件。
    - `Ui_NotInstalled_Null`。
    - `Build_UnknownQuasarLang_TreatedAsNull`：資料表 `ja` 的 `QuasarLang = "xx"` → `Find("ja").QuasarLang == null`，log 有 Warning。
  - `AuthApiTests.Options_LocaleInfos_HaveUiHash`（`Category=Postgres`）：四筆都有 16 字元 `uiHash`（與端點標頭的比對在 Task 5，第二輪必修 2）。
  - `AuthApiTests.Options_UiHash_ChangesWithDefaultLocale`（`Category=Postgres`）：裝 `ja`（無上層）並啟用 → 預設 `zh-TW` 與預設 `th` 時 `ja` 的 `uiHash` 不同；`Options_IncludesLocaleSettings` 不改並照過。
  - `PolicyApiTests.Effective_LocalesHaveUiHash`（`Category=Postgres`）：含未啟用 `ja` 也有 `uiHash`；預設語系改成 `th` 後，回應的 `values["locale.default"]` 是 `th`，且無上層的 `ja` 的 `uiHash` 跟著變、等於 `auth/options` 同一時刻給的值。
- [ ] **Step 2: 跑測試確認失敗**：`cd backend && dotnet test tests/DynamicModules.DomainTests --filter "FullyQualifiedName~UiTextMerge"`；`dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~LocaleRegistry"`；`dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~AuthApiTests|FullyQualifiedName~PolicyApiTests"`
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；另跑 WebAPITests `--filter "FullyQualifiedName~DemoLoginApiTests|FullyQualifiedName~InstalledLocalePolicyTests"`；PostgreSQL：WebAPITests `(FullyQualifiedName~AuthApiTests|FullyQualifiedName~PolicyApiTests)&Category!=SqliteOnly&Category!=Benchmark`
- [ ] **Step 5: Commit**（`feat(i18n): 伺服器沿退回鏈合併介面文字，語系資訊帶 uiHash`）

**驗收**：Step 4 全過（含 PostgreSQL）；`enabledLocales`、`defaultLocale` 形狀不變；`grep -rn "LocaleInfoResponse.Of(" backend/src` 兩處都帶雜湊。

### Task 5: `GET i18n/ui/{code}` 與快取標頭

**Files:**
- Create: `backend/src/DynamicModules.Application/Dynamic/LocaleService/Queries/GetUiMessages/GetUiMessagesRequest.cs`、`GetUiMessagesHandler.cs`（資料夾慣例照 `AccountService/Queries/GetAuthOptions/`）
- Create: `backend/src/DynamicModules.WebAPI/Controllers/v3/Dynamic/I18nController.cs`
- Modify: `backend/src/DynamicModules.WebAPI/Security/SessionMiddleware.cs:73-75`（`IsAnonymousOptional` 加 GET `Prefix + "/i18n/ui/"` 開頭，判斷 9）
- Test: `backend/tests/DynamicModules.WebAPITests/Dynamic/I18nUiApiTests.cs`（新，`Category=Postgres`）

**Interfaces:**
- `public sealed record GetUiMessagesRequest(string Code, bool SignedIn) : IRequest<ServiceResult>;`
- `public sealed record UiMessagesResult(string Hash, byte[] Json, bool Enabled);`
- Handler（注入 `LocaleRegistry`、`CachedSystemPolicy`）：`LocaleCode.IsValid(code)` 不過 → `NotFound(null)`；`registry.Find(code)` 為 null → `NotFound`；`enabled = policies.EnabledLocales.Contains(code)`；`!enabled && !SignedIn` → `NotFound`；`registry.Ui(code, policies.DefaultLocale)` → `Success(new UiMessagesResult(b.Hash, b.Json, enabled))`。
- Controller：`[Route("api/v3/dynamic/i18n")]`；`[HttpGet("ui/{code}")] public async Task<IActionResult> Ui(string code, [FromQuery] string? v)`：送 request（`SignedIn: User.Identity?.IsAuthenticated == true`）；不是成功就 `ToActionResult(this)`；成功依判斷 8 設 `Response.Headers.CacheControl` 與 `X-UI-Hash`，回 `File(r.Json, "application/json; charset=utf-8")`。

- [ ] **Step 1: 寫失敗的測試**（`I18nUiApiTests`，用 `TestApp` 與 i4a 的 `LocaleTestKit`）
  - `Options_UiHash_EqualsHeader`（從 Task 4 移來）：`auth/options` 每個語系的 `uiHash` 等於 `GET i18n/ui/<code>` 的 `X-UI-Hash`；`policies/effective` 同。
  - `Anonymous_Enabled_ReturnsMergedFlat`：未登入 GET `i18n/ui/th` → 200；本體是扁平物件、鍵數等於 `BuiltinLocaleResources.BaseKeys.Count`；`menu.home` 是泰文；`X-UI-Hash` 等於 `auth/options` 裡 `th` 的 `uiHash`。
  - `MatchingV_Immutable`：帶 `?v=<該雜湊>` → `Cache-Control` 含 `public`、`max-age=31536000`、`immutable`。
  - `MissingOrStaleV_NoStore`：不帶 `v`、帶 `v=0000000000000000` → `no-store`；本體仍是這台目前內容；`X-UI-Hash` 是目前雜湊（設計 §19 多台競態：模擬「前端拿到 A 台的新雜湊、打到 B 台」）。
  - `Anonymous_NotEnabled_404`：裝 `ja` 未啟用 → 未登入 404；`Anonymous_NotInstalled_404`（`ko`）；`InvalidCode_404`（`JA`、`zh_TW`、`../x`）；三種 404 的本體相同。
  - `SignedIn_NotEnabled_PrivateNoCache`：一般使用者登入取未啟用 `ja` → 200、`private, no-cache`（帶相符的 `v` 也一樣，不給 `immutable`）。
  - `SignedIn_Ja_ParentThenDefault`（設計 §19「前端拿到的 `i18n/ui/ja` 就是補齊結果」）：裝 `ja`（上層 `en-US`）→ 內容等於 `en-US`；改成無上層、預設 `th` → 內容等於 `th`、`X-UI-Hash` 變了。
  - `ExpiredCookie_TreatedAsAnonymous`：帶無效 cookie 取 `th` → 200（不是 401）；取未啟用 `ja` → 404。
  - `MustChangePassword_CanFetch`：強制改密碼的使用者取 `zh-TW` → 200。
  - `ZhTw_AlsoServed`：`zh-TW` 也回 200，內容等於內建繁中（設計 §8.2：繁中也走這支）。
- [ ] **Step 2: 跑測試確認失敗**：`cd backend && dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~I18nUiApiTests"`
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；另跑 WebAPITests `--filter "FullyQualifiedName~SessionMiddleware|FullyQualifiedName~AuthApiTests|FullyQualifiedName~ClientError"`；PostgreSQL：`(FullyQualifiedName~I18nUiApiTests)&Category!=SqliteOnly&Category!=Benchmark`
- [ ] **Step 5: Commit**（`feat(i18n): GET i18n/ui/{code} 回補齊後的介面文字，依雜湊設快取標頭`）

**驗收**：Step 4 全過（含 PostgreSQL）。

**i4b-1 收尾**：PostgreSQL 窄範圍雙跑：`FORMWRIGHT_TEST_DB=postgres dotnet test tests/DynamicModules.WebAPITests --filter "Category=Postgres&(FullyQualifiedName~I18nUi|FullyQualifiedName~AuthApi|FullyQualifiedName~PolicyApi|FullyQualifiedName~InstalledLocale)&Category!=SqliteOnly&Category!=Benchmark"`；`formwright-purpose-check` 對 Task 1–5 跑一次；寫交棒說明（上面「交棒條件」），不推分支、不跑 gate。

---

# i4b-2 前端載入

### Task 6: 介面文字載入模組（取文字、預編譯、轉巢狀、瀏覽器快取、`ensureLocaleMessages`）

**Files:**
- Create: `frontend/src/i18n/ui-messages.ts`、`frontend/src/i18n/ui-messages.vitest.spec.ts`
- Modify: `frontend/src/i18n/locale-settings.ts`（`LocaleInfo` 加 `uiHash: string`）、`frontend/src/i18n/locale.ts`（`BUILTIN_LOCALES` 補 `uiHash: ''`；新增 `STARTUP_BUDGET_MS`，第三輪 1）、`frontend/src/services/api/dynamic-api.ts`（`getUiMessages(code, v)` 回 `{ hash: string; messages: Record<string, string> }`，`hash` 取回應標頭 `x-ui-hash`）、`frontend/package.json` 與 `frontend/bun.lock`（判斷 11：`bun add @intlify/message-compiler@11.4.19`，`dependencies` 寫精確版本）

**Interfaces:**（i18n 實例當參數，判斷 20）
```ts
type I18nLike = { global: { setLocaleMessage(code: string, m: object): void; availableLocales: string[] } };
export function prepareMessages(flat: Record<string, unknown>, base: Record<string, string>): { nested: object; dropped: string[]; missing: string[] };
export function installUiMessages(i18n: I18nLike, code: LocaleCode, flat: Record<string, unknown>, hash: string): void;
export const hasUiMessages = (i18n: I18nLike, code: LocaleCode): boolean;
export function loadedHash(code: LocaleCode): string | undefined;
export const STARTUP_BUDGET_MS = 3000;                        // locale.ts；設計 §14，ensureLocaleMessages 的預設預算、startup 匯入
export const UI_FETCH_TIMEOUT_MS = 15000;                     // ui-messages.ts；判斷 15
export function loadUiMessages(code: LocaleCode, hash?: string): Promise<{ hash: string; messages: Record<string, string> }>;
export function ensureLocaleMessages(i18n: I18nLike, codes: LocaleCode[], opts?: { budgetMs?: number }): Promise<LocaleCode[]>;
export function ensureCurrentMessages(i18n: I18nLike, code: LocaleCode): Promise<void>;   // 失敗丟錯（切換語系用）
export function refreshLoadedMessages(i18n: I18nLike, infos: LocaleInfo[]): Promise<void>;
export function readUiCache(): { code: LocaleCode; hash: string; messages: Record<string, string> } | null;
export function writeUiCache(code: LocaleCode, hash: string, messages: Record<string, string>): void;
```
- `base` 是打包的 zh-TW 壓平結果（模組內算一次）。`prepareMessages`：非字串值、基準外的鍵丟掉；`baseCompile` 有錯的句子丟掉並改用 `base` 同鍵；回傳巢狀物件。
- 元件與頁面用的便捷版：`ensureLocaleMessages` 等另有不帶 `i18n` 參數的匯出（綁 `src/boot/i18n` 單例），放同檔底部、名稱相同但從 `src/i18n/ui-messages-app.ts` 匯出也可以；實作者擇一，型別檢查要能防止 spec 誤用單例。

- [ ] **Step 1: 寫失敗的測試**（`ui-messages.vitest.spec.ts`，每案自建 `createI18n({ legacy: false, locale: 'zh-TW', fallbackLocale: 'zh-TW', messages: { 'zh-TW': zhTW } })`，API 用 `vi.spyOn(dynamicApi, 'getUiMessages')`）
  - `prepareMessages`：合法句子原樣；未配對大括號 `"{name"`、裸 `@`（`"a@b"` 在 vue-i18n 是 linked 語法開頭，確認會 `onError` 的寫法以實測為準）→ 丟掉、改用繁中、`dropped` 含該鍵；基準外的鍵丟掉；缺的鍵列在 `missing`；`{'@'}`、`{'{{欄位}}'}` 合法；結果轉成巢狀、`t()` 取得到。
  - `installUiMessages` 之後 `hasUiMessages` 為 true、`loadedHash` 是傳入值。
  - 開發模式（`import.meta.env.DEV` 用 `vi.stubEnv`）缺鍵與壞句各印一次 `console.warn`；正式模式不印。
  - `ensureLocaleMessages`：`['zh-TW','ja','ko']`，API 對 `ja` 回文字、`ko` reject → 回傳 `['zh-TW','ja']`（zh-TW 已打包算有，但 `uiHash` 與 `loadedHash` 不同時也會取，見下一案）、不丟例外；同時呼叫兩次只打一次 `ja`；`loadedHash('ja') === localeInfo('ja').uiHash` 時不再打。
  - `ensureLocaleMessages` 對 zh-TW：`localeInfo('zh-TW').uiHash` 有值且與 `loadedHash` 不同 → 取一次（覆寫層，U1）。
  - 純文字輸出（§13.3）：裝上一句 `<b>粗體</b>` 後掛載一個 `{{ t(key) }}` 的元件 → `text()` 含 `<b>`、`find('b').exists()` 為 false。
  - `ensureLocaleMessages` 預算：`ja` 永不回、`budgetMs: 3000`（fake timers）→ 3000 ms 時 resolve、回傳不含 `ja`；之後 `ja` 回來 → `hasUiMessages('ja')` 變 true。
  - `refreshLoadedMessages`：已載入 `th`（雜湊 A），新 `infos` 的 `th` 是 B → 重取並換上；沒載入過的 `vi` 不取；`th` 是目前語系時另寫快取。
  - **版本協調**（第二輪必修 6）：`th` 已載入 A，`wanted` 變 B：(a) 第一次重取回 `X-UI-Hash: A`（舊台）→ 不裝、仍是 A；第二次呼叫 `refreshLoadedMessages`（同樣是 B）→ 再取、回 B → 裝上 B；(b) 第一次重取 reject → 第二次成功回 B → 裝上；(c) 舊請求晚到：對 `th` 先後發出兩次請求（第二次因 `wanted` 又變 C），C 先回、B 後回 → 最後是 C；(d) 請求超過 `UI_FETCH_TIMEOUT_MS` → 視為失敗，下一輪會重發。
  - 還沒有任何文字的語系：回應雜湊與 `wanted` 不同也先裝上。
  - `writeUiCache`／`readUiCache`：寫 `th` 再寫 `vi` → `readUiCache()` 回 `{ code: 'vi', … }`（同一個鍵，只剩一份）；`setItem` 丟 `QuotaExceededError` 不丟例外；形狀不符（`messages` 是字串）讀回 null；localStorage 不可用回 null。
- [ ] **Step 2: 跑測試確認失敗**：`cd frontend && bunx vitest run src/i18n/ui-messages.vitest.spec.ts --maxWorkers=2`
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；`bunx vitest run src/i18n src/services/api --maxWorkers=2`；`bun run typecheck`
- [ ] **Step 5: Commit**（`feat(i18n): 前端介面文字載入、預編譯與瀏覽器快取`）

**驗收**：Step 4 全過；`git diff frontend/bun.lock` 只有根套件的相依宣告多一行 `@intlify/message-compiler`，套件節點數不變；`cd frontend && bun install --frozen-lockfile` 成功。

### Task 7: 啟動順序、只打包繁中、與 auth guard 共用 `auth/options`

**Files:**
- Create: `frontend/src/i18n/startup.ts`、`frontend/src/i18n/startup.vitest.spec.ts`
- Create: `frontend/src/i18n/locale-preview.ts`、`frontend/src/i18n/locale-preview.vitest.spec.ts`（只有儲存；選單與 guard 的接法在 Task 8；第二輪必修 2）
- Modify: `frontend/src/application/dynamic/queries/auth-queries.ts:9-15`（`useAuthOptions` 的 `queryFn`，判斷 12）
- Modify: `frontend/src/i18n/locale.ts`（加 `OFFLINE_LOCALES`，判斷 18）
- Modify: `frontend/src/boot/i18n.ts`（只 import `shared/i18n/zh-TW/ui.json`；async boot 呼叫 `startLocale(i18n)`；`createI18n` 的 `messages` 只有 zh-TW，鍵型別維持字串）
- Modify: `frontend/src/router/auth-guard.ts:28-48`（`loadLocaleSettings` 改成 await `startupOutcome()` 設定語系資訊與政策，不呼叫 API、不套語系、不等文字（判斷 12、13）；斷線分支用 `OFFLINE_LOCALES`（判斷 18））
- Modify: `frontend/vitest.setup.ts`（判斷 20：en-US、th、vi 用 `setLocaleMessage` 預載，註明「只給測試」）
- Test: `frontend/src/i18n/startup.vitest.spec.ts`、`frontend/src/i18n/locale-preview.vitest.spec.ts`、`frontend/src/router/auth-guard.vitest.spec.ts`、`frontend/src/pages/auth/LoginPage.vitest.spec.ts`

**Interfaces:**
```ts
// STARTUP_BUDGET_MS 從 src/i18n/locale.ts 匯入（Task 6 建）
export interface StartupOutcome { options: AuthOptions | null }
export function startupOutcome(): Promise<StartupOutcome>;           // 最晚預算到時 resolve，永不 reject
export function takeStartupAuthOptions(): AuthOptions | undefined;   // 啟動那次成功才給、只給一次
export function resetStartupForTests(): void;
export const PREVIEW_KEY = 'formwright.locale.preview';            // locale-preview.ts
export function previewLocale(): LocaleCode | null;                 // sessionStorage 不可用回 null
export function setPreviewLocale(code: LocaleCode | null): void;
export function startLocale(i18n: I18nLike & { global: { locale: { value: string } } }): Promise<void>;
```
- `startLocale` 照判斷 13；背景工作不阻塞 boot，錯誤吞掉（不讓未處理的 rejection 進 error-reporter）。

- [ ] **Step 1: 寫失敗的測試**（`startup.vitest.spec.ts`，fake timers，乾淨 i18n 實例，mock `getAuthOptions`、`getUiMessages`）
  - `Cache_MountsWithoutWaiting`：localStorage 記 `th`、快取有 `th`（雜湊 A）、`getAuthOptions` 永不 resolve → `startLocale` 在 0 ms 內 resolve，`t('menu.home')` 是快取的泰文。
  - `Cache_RevalidatesInBackground`：同上但 `auth/options` 給 `th` 雜湊 B → resolve 後背景取 `th?v=B`、換上新文字、快取變 B；雜湊相同（A）則不取文字。
  - `NoCache_WaitsForBoth`：沒有快取、記 `en-US` → 依序 `auth/options` → `i18n/ui/en-US?v=<雜湊>` → resolve 時已是英文；`getAuthOptions` 只被呼叫一次。
  - `NoStoredLocale_UsesDefault`：沒記語系、`auth/options` 預設 `th` → 取 `th`、寫快取。
  - `NoStoredLocale_CacheUsed`（建議收）：沒有 `formwright.locale`、快取是預設語系 `th` → 不等網路直接泰文；`auth/options` 回預設已改成 `vi` → 背景載入 `vi` 後切過去；預設仍是 `th` 就留在泰文。
  - `StoredLocaleDisabled_UsesDefault`：記 `th` 但只啟用 zh-TW、en-US，預設 en-US → 套 en-US（從 guard 搬來的規則）。
  - `OptionsNeverResolve_BudgetEnds`（第二輪必修 5）：沒有快取、`getAuthOptions` 永不 resolve → 3000 ms 時 `startLocale` 與 `startupOutcome()` 都 resolve（`options: null`）、畫面繁中。
  - `TextNeverResolve_BudgetEnds`：`auth/options` 立即回、`i18n/ui/th` 永不回 → 3000 ms 時 resolve、畫面繁中；`startupOutcome()` 已帶 `options`。
  - `Timeout_MountsZhTwThenSwaps`：沒有快取、`i18n/ui/th` 3 秒後才回 → 3000 ms 時 resolve、畫面是繁中（`fallbackLocale`）；之後回來 → 變泰文、寫快取。
  - `OptionsFail_UsesCache`／`OptionsFail_NoCache_ZhTw`（U4：先用快取，沒有就繁中）。
  - `ZhTw_FetchedForOverrides`：記 `zh-TW`、沒有快取 → 仍取 `i18n/ui/zh-TW` 一次，期間顯示打包的繁中。
  - `Preview_IgnoredAtStartup`：`sessionStorage` 有試看 `ja`、`formwright.locale` 是 `th` → 啟動套 `th`、不取 `ja`、不寫 `ja` 的快取（判斷 13、17）。
  - `locale-preview`：寫入 `sessionStorage`、不碰 `localStorage` 的 `formwright.locale`；`null` 移除；不可用時不丟例外。
  - **打包檢查**（放 `startup.vitest.spec.ts` 或另一個 spec）：`boot/i18n.ts` 原始碼只 import `zh-TW/ui.json`（讀檔字串比對，防止有人加回其他語系）。
  - `auth-guard`：既有案例照過（「記的語系已停用就套預設」的斷言移到 startup）；新案 `ConsumesStartupOutcome`：先 `startLocale` 再跑 guard → `getAuthOptions` 共 1 次；`Offline_ListsOfflineLocales`：`getAuthOptions` reject → `enabledLocales()` 等於 `OFFLINE_LOCALES` 的代碼（現值內建四語，Q1）；`OptionsNeverResolve_NavigationCompletes`：`getAuthOptions` 永不 resolve → 導覽到 `/login` 在 3000 ms 後完成（不是一直卡住）、`enabledLocales()` 是 `OFFLINE_LOCALES`。
  - `LoginPage`（真正掛載，第二輪必修 4）：boot（`startLocale`）＋ guard ＋ 掛載 `LoginPage` → `getAuthOptions` 共 **1** 次、`getMe` 1 次（guard 的）、沒有其他 `/me`；第一次回 `demoGenerating: true` → 2 秒後第 2 次請求回 `demoGenerating: false` 與一個 Demo 帳號 → 畫面出現該帳號。啟動那次 reject → 登入頁自己發 1 次（不被鎖住）。
- [ ] **Step 2: 跑測試確認失敗**：`cd frontend && bunx vitest run src/i18n/startup.vitest.spec.ts src/router --maxWorkers=2`
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；`bunx vitest run src/i18n src/router src/layouts src/pages/auth --maxWorkers=2`；`bun run typecheck`；`bun run build` 後確認包體只有繁中：`grep -rl "หน้าแรก" dist/spa` 無結果（`menu.home` 的泰文；用當下 `shared/i18n/th/ui.json` 的值），`grep -rl "首頁" dist/spa` 有結果；記錄 `dist/spa/assets` 總大小與 main 相比的差（預期少約 370 KB 未壓縮，設計 §8.1）
- [ ] **Step 5: Commit**（`feat(i18n): 前端只打包繁中，啟動時載入介面文字（stale-while-revalidate、3 秒上限）`）

**驗收**：Step 4 全過；打包檢查兩條 grep 結果如上。

### Task 8: 切換語系、管理員試看（M2）、斷線清單常數

**Files:**
- Modify: `frontend/src/i18n/ui-messages.ts`（`switchLocale(code, { preview })`，判斷 19）
- Modify: `frontend/src/presentation/locale/LocaleSwitcher.vue`（載入狀態、失敗提示、prop `allowPreview` 與試看項目，判斷 17、19）
- Modify: `frontend/src/layouts/MainLayout.vue:17`（傳 `:allow-preview`）
- Modify: `frontend/src/router/auth-guard.ts`（判斷 17 的試看驗證順序與分支表）、`frontend/src/application/dynamic/queries/auth-queries.ts`（`useLogout` 成功時清除試看）、`auth-guard.ts` 的 `handleUnauthorized`（清除試看）
- Test: `frontend/src/i18n/locale.vitest.spec.ts:63-80`（`LocaleSwitcher` 案例）、`frontend/src/router/auth-guard.vitest.spec.ts`、`frontend/src/layouts/MainLayout.vitest.spec.ts`（若有語言選單案例）

**Interfaces:**
```ts
export function switchLocale(code: LocaleCode, opts?: { preview?: boolean }): Promise<boolean>;  // 成功 true；失敗已提示、回 false
```

- [ ] **Step 1: 寫失敗的測試**
  - `LocaleSwitcher`：
    - 沒傳 `allowPreview`（登入頁、一般使用者）：選項仍是 `['繁體中文','English','ไทย','Tiếng Việt']`，沒有 `locale-preview-*`。
    - 選 `th` 且 `getUiMessages` 延遲 → 載入中 `th` 項目有 spinner（`aria-label` 是 `locale.loading` 的文字）；完成後 `i18n.locale === 'th'`、`formwright.locale === 'th'`。
    - `getUiMessages` reject → 提示「無法載入ไทย，請稍後再試」（繁中介面時的文字）、`i18n.locale` 不變、`formwright.locale` 不變。
    - `allowPreview` 為 true、已安裝含未啟用 `ja`（`setLocaleInfos` 五筆、啟用四筆）→ 分隔線後有 `日本語（未啟用）`；選它 → `sessionStorage` 有 `ja`、`formwright.locale` 不變、`formwright.ui` 沒寫 `ja`；再選 `English` → 試看清掉、`formwright.locale === 'en-US'`。
    - `allowPreview` 為 true 但沒有未啟用語系 → 沒有分隔線。
    - 元件本身不呼叫 `getMe`（spy 為 0 次，第二輪必修 9）。
  - `MainLayout`：`/me` 回 `isSystemAdmin: true` → 語言選單有試看項目；`false` → 沒有。
  - `auth-guard`（判斷 17 的分支表逐列一案；`sessionStorage` 都先放 `ja`，`ja` 已安裝但**未啟用**，`auth/options` 的 `locales` 不含它）：匿名 → 清除；強制改密碼 → 清除；非管理員 → 清除、畫面是 `formwright.locale` 或預設；管理員 → 在 `loadSessionSettings` 之後才判斷（`getEffectivePolicies` 的 `locales` 含 `ja`）→ 背景載入並套用 `ja`、`formwright.locale` 不變；管理員但 `ja` 不在生效政策的 `locales` → 清除；`loadSessionSettings` 失敗 → 保留；`useLogout` 成功、`handleUnauthorized` → 清除。
    - **晚到的回應**（第三輪 4）：管理員背景載入 `ja` 期間 (a) 登出、(b) session 失效（`handleUnauthorized`）、(c) 在選單選回 `English` → `ja` 的回應之後才到也**不** `applyLocale('ja')`，`i18n.locale` 分別是登出後的語系、預設語系、`en-US`；(d) 一般切換：選 `th`（慢）後立刻選 `vi`（快）→ 最後是 `vi`。
- [ ] **Step 2: 跑測試確認失敗**：`cd frontend && bunx vitest run src/i18n src/router src/layouts --maxWorkers=2`
- [ ] **Step 3: 實作**（判斷 19 的三個新鍵在本 Task 加進四份 `ui.json`，Step 4 的 `locales.vitest.spec.ts` 才過：`locale.notEnabled` 未啟用／Not enabled／ยังไม่เปิดใช้／Chưa bật；`locale.loadFailed` 無法載入{name}，請稍後再試／Couldn't load {name}. Please try again later.／โหลด{name}ไม่ได้ โปรดลองอีกครั้งภายหลัง／Không thể tải {name}, vui lòng thử lại sau；`locale.loading` 載入中／Loading／กำลังโหลด／Đang tải。泰文、越南文是機器翻譯，同 #30 的既有狀態）
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；`bunx vitest run src/i18n/locales.vitest.spec.ts --maxWorkers=2`；`bun run typecheck`
- [ ] **Step 5: Commit**（`feat(i18n): 切換語系先載入文字，系統管理員可試看未啟用語系（M2）`）

**驗收**：Step 4 全過；`grep -rn "OFFLINE_LOCALES =" frontend/src` 只有 `i18n/locale.ts` 一處定義（Q1 單點可改）；`grep -rn "formwright.locale.preview" frontend/src | grep -v spec` 只在 `locale-preview.ts`（Q2 單點可改）。

### Task 9: 已開頁面 60 秒換新

**Files:**
- Modify: `frontend/src/application/dynamic/queries/use-business-time-watch.ts:26`（改看 `query.dataUpdatedAt`；每次都 `applyLocalePolicies` 後 `void refreshLoadedMessages(eff.locales)`，判斷 15）
- Test: `frontend/src/application/dynamic/queries/use-business-time-watch.vitest.spec.ts`

- [ ] **Step 1: 寫失敗的測試**：目前語系 `th` 已載入（雜湊 A）；生效政策回 `th` 雜湊 B → 取 `th?v=B`、`t()` 換新句子、快取變 B、**不**開 Dialog；雜湊不變不取；政策值完全不變、只有 `uiHash` 變（`values` 同一個參考）也會換（沿用 i4a 第二輪必修 8 的「看整份回應」）。**連回兩次 B**（第二輪必修 6）：第一輪文字請求失敗（或回 `X-UI-Hash: A`）、第二輪 60 秒後政策回應與上一輪完全相同（結構共享、同一個參考）→ 第二輪仍會重取、回 B 後換上。
- [ ] **Step 2: 跑測試確認失敗**：`cd frontend && bunx vitest run src/application/dynamic/queries/use-business-time-watch.vitest.spec.ts --maxWorkers=2`
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；`bun run typecheck`
- [ ] **Step 5: Commit**（`feat(i18n): 介面文字雜湊改變時背景換上新翻譯`）

**驗收**：Step 4 全過。

### Task 10: 兩個多語產生點涵蓋每個已安裝語系（`ensureLocaleMessages`，M3）

**Files:**
- Modify: `frontend/src/presentation/dynamic/ledger/LedgerTab.vue:17,23,88-94`（判斷 16：mutation 先 `await ensureLocaleMessages(installedLocales())`；移除 `availableLocales` 解構）
- Modify: `frontend/src/presentation/dynamic/approval-designer/graph/designer-graph.ts:70`（`nameLocales` 用 `hasUiMessages`）
- Modify: `frontend/src/pages/system/dynamic-modules/FlowDesignerPage.vue`（setup 時呼叫一次 `ensureLocaleMessages`、`namesReady` 進載入 watcher 的條件，判斷 16）
- Test: `frontend/src/presentation/dynamic/ledger/LedgerTab.vitest.spec.ts`、`frontend/src/presentation/dynamic/approval-designer/graph/designer-graph.vitest.spec.ts`、`frontend/src/pages/system/dynamic-modules/FlowDesignerPage.vitest.spec.ts`（若沒有就加在 `use-graph-designer` 相關 spec）

- [ ] **Step 1: 寫失敗的測試**
  - `LedgerTab`：已安裝四語 → 標籤四語（既有斷言照過）；已安裝多一個 `ja`（**未啟用**也要產生，M3）、`getUiMessages('ja')` 回日文 → 送出的 `addField` 標籤有 `ja` 鍵、值是日文那句；`getUiMessages('ja')` reject → 沒有 `ja` 鍵、其他四語照常、不報錯。
  - `designer-graph`：`ja` 已 `installUiMessages` → `approvalNames(2)` 有 `ja`；沒有 → 沒有 `ja`；`hasUiMessages` 在 `installUiMessages` 之後立即為 true（證明沒有快照）。
  - `FlowDesignerPage`（第二輪必修 7；用單例 i18n、清掉 en-US／th／vi 的預載或改用沒有預載的 `ja`）：
    - 新流程（`_new`）：`getUiMessages` 對四語延遲 500 ms 才回 → 500 ms 前畫布還沒有初始節點（不能新增）；回來後的初始簽核節點名稱含全部已安裝語系。
    - 慢網路：某語系永不回 → 3000 ms 後設計器照常載入，初始節點沒有那個語系、其他語系都有；載入完成後立即新增的節點同樣。
    - `ensureLocaleMessages` 只被呼叫一次。
- [ ] **Step 2: 跑測試確認失敗**：`cd frontend && bunx vitest run src/presentation/dynamic/ledger src/presentation/dynamic/approval-designer/graph src/pages/system/dynamic-modules --maxWorkers=2`
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；`bun run typecheck`；`grep -n "availableLocales" frontend/src/presentation/dynamic/ledger/LedgerTab.vue` 無結果
- [ ] **Step 5: Commit**（`feat(i18n): 沖銷欄位標籤與簽核新節點名稱涵蓋每個已安裝語系`）

**驗收**：Step 4 全過。

### Task 11: 本機走查、Docker 映像冒煙、文件回填、gate

**Files:**
- Modify: `docs/roadmap.md`（i4 列進度「i4a 已上線 3d0dd59b；i4b 已上線 <sha>」）
- Modify: `docs/superpowers/specs/2026-11-17-language-packs-design.md`：§8.1 搬檔數字改「148 檔（2026-11-20）」；§8.2「共用 TanStack Query 快取」補「實作改成啟動那次結果由 boot、guard、登入頁第一次共用，之後照常重取（i4b 計畫判斷 12）」、`uiHash` 補「實作為合併結果的內容雜湊（判斷 5）」；§14「技術限制」補判斷 8 的 `max-age`、判斷 5 的雜湊長度、判斷 14 的「瀏覽器快取一份」、判斷 15 的文字請求逾時 15 秒、判斷 6 的「介面文字退回鏈截到繁中」；§15 刪掉 i4a 已知限制中「`auth/options` 還沒有 `uiHash`」「沖銷欄位標籤只對前端有翻譯包的語系」兩條，加判斷 16 的已知限制；§18 第 4 點補 Q1 的結果、第 8 點標「i4b 完成」；§19 試看那句依 Q2 的裁決改；§20 i4b 列後補「i4b 完成（<sha>）」
- Modify: `docs/feature.md`（i4 列補「i4b 已實作（介面文字執行期載入）」；跑 `feature_checklist.py docs/feature.md --write`）
- Modify: `docs/manual.md` §20（切換語言時若無法載入會顯示「無法載入〇〇，請稍後再試」並維持原語言；試看項目因為 i4b 沒有安裝入口，延到 i4d2 的語系頁一起寫）
- Modify: `.claude/rules/frontend.md` 的 i18n 條目（位置改 `shared/i18n/<code>/ui.json`；「前端只打包繁中，其他語系執行期載入」；新增句子仍要給四語）、`AGENTS.md` 架構地圖（`i18n`（四語 JSON）改成「`i18n`（語系載入與設定；內建介面文字在 `shared/i18n/`）」、`shared/` 一行補 `i18n/`）；跑 `node .claude/scripts/sync-codex-mirror.mjs`，`.agents/` 的變更一起 commit（設計 §18 第 11 點的 i4b 部分；`backend.md` 的 `MessageText` 條目是 i4c）

- [ ] **Step 1: 整段驗收掃描**（下一節），逐條勾在交付說明。
- [ ] **Step 2: 本機走查**（`bash scripts/unattended/local-demo.sh`，背景執行等 READY；用 `formwright-demo-verify` skill；390、820、1366、1920 四種寬度）：
  1. 清空站台資料（localStorage、sessionStorage）後開登入頁：Network 只多一次 `i18n/ui/zh-TW`，`auth/options` 只一次，沒有額外的 `/me`（guard 那一次之外；第二輪必修 9）；畫面繁中。
  2. 登入頁切 **English**、**ไทย**（冒煙）：先看到載入狀態再換語言，日期選擇器（Quasar）跟著換；重新整理 → 不等網路直接是泰文（快取），背景有一次 `auth/options`。
  3. 用開發者工具把 `i18n/ui/*` 設成離線 → 切 English 提示「無法載入English，請稍後再試」並停在泰文；整個 API 離線重新整理 → 用快取的泰文、選單列內建四語（Q1）。
  4. 登入 Demo 管理員：開新簽核流程 → **初始節點**與新增的簽核、條件節點都是四語名稱；開發者工具把網路設成慢速 3G 再開一次新流程，初始節點仍是四語（第二輪必修 7）；分錄沖銷欄位 → 四語標籤（Network 有其他三語的 `i18n/ui/*`）。
  5. 用資料庫插一列 `ja`（`installed`，上層 `en-US`，`QuasarLang` `ja`）並等 60 秒：管理員語言選單出現「日本語（未啟用）」、一般使用者沒有；切過去畫面是英文（上層補齊）、日期選擇器是日文包；重新整理後依 Q2 裁決的行為；開新分頁是原語系；簽核設計器新節點名稱有 `ja`（英文那句）。刪掉該列前先切回啟用語系。
  6. 全程不露出鍵名（`menu.home` 這類）、四種寬度不橫捲。
- [ ] **Step 3: Docker 映像冒煙**（設計 §19、判斷 22）：`docker build -f Dockerfile.demo -t formwright-i4b-smoke .` 成功；`docker run --rm -d -p 18080:8080 formwright-i4b-smoke`；`curl -s localhost:18080/api/v3/dynamic/i18n/ui/th | python3 -c "import json,sys;print(len(json.load(sys.stdin)))"` 等於 repo 內 zh-TW 壓平後的鍵數，`en-US` 同；回應的 `menu.home` 是泰文；停容器、刪映像。另做一次反證：暫時在 `Dockerfile.demo` 拿掉 `COPY shared/` 那行建置 → 失敗於建置檢查（驗完還原，不 commit）。
- [ ] **Step 4: 全域守衛與建置**：`cd frontend && bunx vitest run src/css/themes src/i18n/locales.vitest.spec.ts src/i18n/quasar-langs.vitest.spec.ts --maxWorkers=2`；`bun run typecheck`；`bun run build`。
- [ ] **Step 5: gate**：推分支前外洩掃描；`bash scripts/unattended/ci-wait.sh <sha>`（含 PostgreSQL 兩步），`exit=0`。
- [ ] **Step 6: `formwright-purpose-check`**（八項，重點：單一來源、空系統（沒開應用程式時登入頁照常載入文字）、權限 fail closed（匿名 404、試看只給管理員）、驗證不只在前端）。
- [ ] **Step 7: Commit**（`docs(i18n): i4b 完成，路線、設計、手冊與規則補註`），之後照 `formwright-ship` fast-forward 推 main。

**驗收**：掃描全符合；走查逐項記錄（含截圖路徑放專案 `.tmp/`）；Docker 冒煙數字與 repo 一致；gate `exit=0`；宗旨檢查無違反。Demo 資料：**無可示範**的新功能（i4b 沒有安裝語系的入口，試看要等 i4d1；冒煙只驗內建四語切換），交付說明寫明。

---

## 整段驗收

設計 §1 寫死清單中屬於 i4b 的只有「前端介面文字 `frontend/src/i18n/*.json` 隨前端打包、`locales.vitest.spec.ts` 檢查四份」；其餘 i4a 已改完、i4c／i4f 不在本段。

| # | 項目 | 處置 | 驗證 |
|---|---|---|---|
| 1 | 四語 JSON 位置 | `shared/i18n/<code>/ui.json`（Task 1） | S1 |
| 2 | 前端打包四語 | 只打包 zh-TW（Task 7） | Task 7 的 dist grep |
| 3 | `locales.vitest.spec.ts` | 讀新位置、四份鍵一致（Task 1） | 該 spec |
| 4 | `shared/builtin-locales.json` | 併入 `locale.json`（Task 1） | S2 |
| 5 | Quasar 語言包清單 | `shared/quasar-langs.json`＋一致性測試＋後端內嵌（Task 2、3） | S3 |
| 6 | 後端內嵌、建置檢查、啟動自檢、兩份 Dockerfile（M5） | Task 3 | Task 3 Step 4、Task 11 Step 3 |
| 7 | `GET i18n/ui/{code}`、快取標頭 | Task 5 | `I18nUiApiTests` |
| 8 | `auth/options` 加 `uiHash`（i4a 判斷 12） | Task 4 | `AuthApiTests` |
| 9 | `LedgerTab`／`designer-graph` 每個已安裝語系（i4a 判斷 16） | Task 10 | 行為測試、S4 |
| 10 | 試看（M2） | Task 8 | `LocaleSwitcher`、`auth-guard` 案例 |

```bash
# S1 舊位置與舊路徑已不存在（repo 根目錄）
ls frontend/src/i18n/*.json 2>/dev/null                                                         # 無結果
grep -rnE "src/i18n/(zh-TW|en-US|th|vi)\.json|\./(zh-TW|en-US|th|vi)\.json" frontend/src      # 無結果
# S2 內建語系清單只有 shared/i18n/*/locale.json（加 C# BuiltinLocales，由 fixture 與自檢對齊）
grep -rn "builtin-locales" frontend backend/src backend/tests shared .github                   # 無結果
# S3 Quasar 清單
python3 -c "import json;print(len(json.load(open('shared/quasar-langs.json'))))"                 # 74
# S4 多語產生點不再用快照
grep -rn "availableLocales" frontend/src --include='*.vue' | grep -v spec                      # 無結果
grep -rn "availableLocales" frontend/src --include='*.ts' | grep -v spec                       # 只在 i18n/ui-messages.ts（hasUiMessages）
# S5 前端只 import 繁中
grep -rnE "shared/i18n/(en-US|th|vi)/ui.json" frontend/src | grep -v "spec.ts\|test-kit"       # 無結果（vitest.setup.ts 在 src 外）
# S6 部署檔都複製了 shared
grep -c "COPY shared/ /shared/" Dockerfile Dockerfile.demo                                      # 各 1
```

## 預估規模

| 批 | Task | 新檔 | 改檔 | 手寫行數（不含機械改寫與產生的 JSON） |
|---|---|---|---|---|
| i4b-1 | 1 | 4（locale.json） | 6＋約 150 機械 | ≈ 60（機械改寫約 190 行） |
| | 2 | 3 | 0 | ≈ 70 |
| | 3 | 4 | 5 | ≈ 380（測試約 200） |
| | 4 | 2 | 6 | ≈ 340（測試約 190） |
| | 5 | 4 | 1 | ≈ 270（測試約 170） |
| | 小計 | 17 | 18 | ≈ 1120 |
| i4b-2 | 6 | 2 | 5 | ≈ 480（測試約 260） |
| | 7 | 4 | 6 | ≈ 470（測試約 270） |
| | 8 | 0 | 8（含四份 ui.json） | ≈ 300 |
| | 9 | 0 | 2 | ≈ 70 |
| | 10 | 0 | 5 | ≈ 160 |
| | 11 | 0 | 9 | ≈ 60 |
| | 小計 | 10 | 35 | ≈ 1540 |
| 合計 | | 27 | 約 53（另約 150 檔機械改寫） | ≈ 2660，其中測試約 57% |

第二輪比第一輪多約 360 行，幾乎都是測試（版本協調、預算、登入頁、試看分支）。兩批都超過單批約 800 行的指引，主因是測試（合併規則、快取標頭的每個分支、啟動順序的六種情境）。Task 6、7 若實作超過 500 行就各拆兩個 commit（Task 6：載入與預編譯／版本協調、快取與 ensure；Task 7：startup 與登入頁／guard 與打包）。

## 已知限制（i4b，交付時寫回設計 §15）

- 一次產生多語的地方（沖銷欄位標籤、簽核新節點）拿到的是補齊後的文字：`ja` 缺的句子會以上層或預設語系的譯文寫進 `ja`；日後補了日文翻譯，已建立的標籤不會跟著改（判斷 16）。
- 簽核設計器與沖銷欄位等語系文字最多 3 秒；超過還沒取到的語系，那次產生的名稱沒有它（判斷 16）。
- 試看中的管理員重新整理後，先短暫顯示原語系、確認身分與已安裝清單後才換成試看語系（判斷 17）。
- 試看未啟用語系時，後端訊息仍是預設語系（設計 §15 已列，M2）。
- 斷線時選了這個瀏覽器沒存過的語言，會提示「無法載入」並停在原語言（Q1 推薦的結果）。
- 多台部署時，前端拿到新雜湊卻打到還沒換新的另一台，那次回應是舊內容且不快取（`no-store`），最多 60 秒後一致（設計 §15 已列的同類限制）。

## 自我檢查

1. **設計覆蓋（§20 i4b 列）**：搬 `shared/i18n/`（alias＋機械改寫、`git mv`）→ Task 1；後端內嵌、兩份 Dockerfile、建置檢查、啟動自檢 → Task 3；`quasar-langs.json` → Task 2、3；`GET i18n/ui/{code}`（伺服器合併、快取標頭）→ Task 4、5；前端只打包 zh-TW、啟動載入與 stale-while-revalidate、瀏覽器快取 → Task 6、7；前端預編譯 → Task 6；`ensureLocaleMessages` 與兩個產生點 → Task 6、10；試看（M2）→ Task 8；`auth/options` 加 `uiHash` → Task 4；已開頁面換新（§8.2）→ Task 9；切換失敗停在原語系（§8.2）→ Task 8；開發模式印缺鍵（§8.3）→ Task 6。
2. **§19 執行期載入各條** → Task 6、7、9 的案例；多台競態 → Task 5 `MissingOrStaleV_NoStore`；內建層完整性 → Task 3、11；試看 → Task 8。
3. **型別一致**：`UiTextMerge.{Flatten,Merge,Serialize}`、`UiBundle`、`BuiltinResourceSet`、`BuiltinLocaleResources.{Load,Verify,Ui,BaseKeys,QuasarLangs}`、`LocaleRegistry.{Ui,UiHash}`、`LocaleInfoResponse.UiHash`、`GetUiMessagesRequest`、`UiMessagesResult`、`I18nController.Ui`；前端 `LocaleInfo.uiHash`、`prepareMessages`、`installUiMessages`、`hasUiMessages`、`loadedHash`、`loadUiMessages`、`ensureLocaleMessages`、`ensureCurrentMessages`、`refreshLoadedMessages`、`readUiCache`、`writeUiCache`、`switchLocale`、`STARTUP_BUDGET_MS`、`UI_FETCH_TIMEOUT_MS`、`StartupOutcome`、`startupOutcome`、`takeStartupAuthOptions`、`startLocale`、`previewLocale`、`setPreviewLocale`、`OFFLINE_LOCALES`、`dynamicApi.getUiMessages`。
4. **待決**：Q1（斷線清單，主控裁決待確認）、Q2（試看重新整理後是否保留），都已照推薦實作且單點可改。

## 使用者裁決（沿用，不再問）

- 2026-11-17／11-18：U1（內建四語可覆寫，所以 zh-TW 也走端點）、U3（退回鏈）、U4（載入失敗先用瀏覽器快取、再繁中）、M2（管理員試看，存 `sessionStorage`）；M3（沖銷欄位標籤每個已安裝語系）依 p3 Q12。
- 2026-11-20：業務政策不寫死、權限 fail closed、驗證不只做在前端；窄範圍 vitest 可加 `--maxWorkers=2` 並行；Demo 種子要示範成功與失敗分支（本段無可示範，見 Task 11）。
