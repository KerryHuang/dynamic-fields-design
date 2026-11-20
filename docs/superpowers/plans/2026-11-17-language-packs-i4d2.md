# 語系包 i4d2 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把 i4d1 的最小語系頁做成完整版：每個語系顯示介面文字與系統訊息的完整度（含缺最多的分類）、「N 句覆寫的原文已更新」（其中參數已變、執行期退回內建翻譯的後端訊息另外標出）、「N 句覆寫已失效（鍵已不存在）」；任一語系（含尚未安裝的新代碼）可以匯出 JSON 翻譯樣板（`.langpack.json`，可直接再匯入）；尚未啟用的語系可以「以此語系預覽」（沿用 i4b 的管理員試看）；語系包的 `builtFor` 開始和平台版本比對並提示；390 寬改卡片。後端加一支唯讀的樣板匯出 API、清單回應多帶完整度與覆寫檢視，分析器補「原文已更新的句子重新匯入即確認」與「已失效的覆寫在下一次套用時清掉」的報告。使用者看得到的行為：**沒有匯入任何語系包時，除語系頁本身外與 main 完全相同**；Demo 站可以預覽匯入報告、可以匯出樣板，不能套用。

**Architecture:** 本段**沒有 migration**：「原文已更新」用 i4d1 已存的 `Dynamic_LocalePack.BaseHashesJson`，「覆寫已失效」讀同一列的原始 `UiJson`／`ServerJson`（快照與 `LocaleWorld` 的覆寫層已濾掉基準鍵以外的鍵，看不到失效的句子）。新增三個純函式類別（`Application/Dynamic/LocaleService/Packs/`）：`LocaleOverlayReview`（一列覆寫層 → 原文已更新、參數已變、已失效的鍵）、`LocaleCompleteness`（從 `LocalePackAnalyzer` 抽出，報告與清單共用同一個定義，另算缺最多的分類）、`LocaleTemplateBuilder`（`LocaleWorld`＋選項 → 樣板 JSON 位元組）。`GET i18n/locales` 每列多 `completeness`、`review`；新端點 `GET i18n/locales/{code}/template` 回附件。平台版本取既有的 `IOpsEnvironment.Version`（建置注入，沒注入是 `dev`），預覽 handler 傳給分析器。前端 `LocalesPage.vue` 改卡片版型，新 `LocaleTemplateExportDialog.vue`，「以此語系預覽」呼叫既有的 `switchLocale(…, { preview: true })`。

**Tech Stack:** .NET 8、MediatR、EF Core（SQLite 研發、PostgreSQL 正式）、xUnit＋FluentAssertions；Vue 3＋Quasar、TanStack Query、vitest。不新增套件（JSON 用 `System.Text.Json`）。

**Spec:** `docs/superpowers/specs/2026-11-17-language-packs-design.md`：§5.1（格式、三段都可缺、空字串＝沒翻）、§5.2（缺鍵報告「哪幾個分類缺最多」、`builtFor` 提示，延期自 i4d1）、§6.3 第 4 步（完成後「以此語系預覽」）、§6.4（基準原文逐句雜湊）、§6.5（語系頁完整版、原文已更新、覆寫已失效、四種寬度）、§7.1（JSON 樣板；Excel 是 i4e）、§10.4（`designData` 的原文已改，本段不做）、§11（匯出只限系統管理員；稽核清單不含匯出）、§12（M2 試看、Demo 列「可預覽、可匯出，不能套用」）、§13.3、§13.4、§15（i4d1 已知限制裡延到 i4d2 的兩條）、§17.4 M2、§19（「合併與回滾」的升級後原文已更新計數、「畫面」四種寬度）、§20 i4d2 列。前置計畫：[i4d1 計畫](2026-11-17-language-packs-i4d1.md)（判斷 7、10、13、21、23、24、25；Q3、Q5、Q6、Q8、Q10）。票：#71（併入）、#70（不併入，見「票」節）。repo 規範：`AGENTS.md`、`.claude/rules/{backend,frontend,docs}.md`、`CONTRIBUTING.md`、`docs/ai/testing.md`。

**路徑慣例：** 後端相對 `backend/`，前端相對 `frontend/`，文件與 `shared/` 相對 repo 根目錄。`Application/…` 是 `backend/src/DynamicModules.Application/…` 的縮寫，`Domain/…`、`WebAPI/…`、`ApplicationTests/…`、`WebAPITests/…`、`DomainTests/…` 同理。符號以 main `821c956d` 為準（不寫行號，用符號定位）。

---

## 前置條件（開工前逐項確認，不符就停下回報）

1. **i4d1 已在 main**：`git log --oneline main | grep 0e42b98e` 有結果；`Application/Dynamic/LocaleService/Packs/{LocalePackAnalyzer,LocalePackVersions,LocaleWorldReader,LocaleVersionSwitch}.cs`、`Application/Dynamic/LocaleService/Queries/GetLocales/`、`frontend/src/pages/system/LocalesPage.vue`、`frontend/src/presentation/locale/{LocalePackImportDialog,LocalePackReport}.vue`、`frontend/src/i18n/locale-preview.ts` 都在。
2. **i4d2 沒開工**：`grep -rn "LocaleOverlayReview\|LocaleTemplateBuilder" backend/src frontend/src` 無結果。
3. **沒有段在改**：`LocalePackAnalyzer.cs`、`LocalePackModels.cs`、`GetLocales*`、`I18nController.cs`、`LocalesPage.vue`、`LocalePackImportDialog.vue`、`shared/i18n/*/ui.json` 的 `localePacks` 區塊（i3 若同時開工會動 `ui.json`，先協調誰先合）。本段沒有 migration，可以和會加 migration 的段平行。
4. **基準數字**（記進交棒說明）：`BuiltinLocaleResources.BaseKeys.Count`、`BuiltinServerText.BaseKeys.Count`；`IOpsEnvironment.Version` 在測試主機是 `dev`。
5. **PostgreSQL 測試環境可用**（`docker info` 成功或設了 `FORMWRIGHT_TEST_PG`）。
6. **plugin 更新**（AGENTS.md「票」）：`claude plugin marketplace update plugharbor && claude plugin update tickets@plugharbor --scope project`，再 `/reload-plugins`；失敗就照舊版跑並記進回報。票 #71 改 in-progress。

## 子段切法（兩個實作者交棒點）

| 批 | 內容 | Task | 手寫行數（估） | migration |
|---|---|---|---|---|
| i4d2-1 | 後端：`builtFor` 比對、原文已更新的確認與已失效的清除報告、覆寫檢視與完整度進清單 API、JSON 樣板匯出 API | 1–3 | ≈ 1500（測試約 55%） | 無 |
| i4d2-2 | 前端：型別與查詢、樣板匯出對話框、語系頁完整版（卡片、完整度、覆寫檢視、以此語系預覽）、匯入對話框補強與 #71；文件、手冊、gate、四種寬度走查 | 4–6 | ≈ 1450（測試約 40%；另有約 250 行四語文案） | 無 |

同一分支 `i4d2`（worktree `../formwright-i4d2`），每個 Task 各自 commit。i4d2-1 收尾跑一次窄範圍 PostgreSQL 雙跑（不跑 gate）；i4d2-2 收尾才跑 gate、合併。

**交棒條件**：
- **i4d2-1 → i4d2-2**：Task 1–3 驗收全過（含 PostgreSQL）；把下列寫進交棒說明，前端照它做：`GET i18n/locales` 每列新增的 `completeness`、`review` 形狀（判斷 6，含明示 null 的欄位）；`GET i18n/locales/{code}/template` 的查詢參數、回應標頭（`Content-Disposition` 檔名）與 400 的錯誤碼（判斷 9）；報告新增的 `ui.confirmed`／`server.confirmed`、`orphanedDropped`、警告 `builtForDiffers` 的參數順序（判斷 2、7、8）；`IOpsEnvironment.Version` 在本機與 Demo 的值。i4d2-1 單獨合併時使用者看得到的差異只有：匯入報告多一行確認句數與已失效的清除提示（前端還沒顯示新欄位；舊前端不認得的欄位直接忽略），清單 API 多欄位。**i4d2-1 可以單獨合併**（後端加欄位不破壞 i4d1 前端），但建議兩批一起上線，手冊一次改。
- 分界理由：i4d2-1 全是後端計算與一支唯讀 API（審查重點：檢視的三種計數對、樣板可原樣再匯入、權限 fail closed）；i4d2-2 是畫面與收尾（審查重點：四種寬度、試看流程、文案）。

---

## 計畫期間的判斷

〔行為〕＝影響使用者看到或能做的事（列在「代為決定的事」，附推薦）；〔技術〕＝實作選擇。設計 §17 與 i4d1 已拍板的不再問。

1. 〔技術〕**沒有 migration、沒有新的 repository 寫入方法**。用到的資料都已存在：`Dynamic_LocalePack` 目前版的 `UiJson`、`ServerJson`、`BaseHashesJson`、`BuiltFor`（i4d1 判斷 7）。讀取用既有的 `ILocalePackRepository.ListCurrentAsync`（不追蹤）。因此 `AuthorizationWriteInventoryTests` 的 PAX23 批次方法清單**不變**；實作中若為了效能新增任何 `ExecuteUpdate`／`ExecuteDelete` 或批次寫入方法，必須同一個 commit 登記進該測試的清單並寫理由（i4d1 漏過一次，見 `694bcb60`）。
2. 〔技術〕**平台版本來源是 `IOpsEnvironment.Version`**（o1a 判斷 J10：建置時 `-p:AppVersion=…` 注入的 `AssemblyMetadata`，沒注入是 `dev`；`WebAPI/Ops/OpsEnvironment.cs` 實作、Application 已在用）。i4d1 判斷 24 寫「main 上沒有任何版本字串可取」不精確：來源存在，只是沒有發版流程，Demo 與本機都是 `dev`（見「與設計的矛盾」第 1 點）。比對規則（Q1）：
   - 樣板匯出寫 `pack.builtFor`＝`Version`，超過 `LocalePackLimits.MaxPackBuiltFor`（20）就截斷（匯出端截，不讓自己產的檔被自己的 Parser 拒收）。
   - 預覽時，`pack.builtFor` 與 `Version` 都有值、都不是 `dev`、而且不相等（Ordinal）→ 報告加警告 `builtForDiffers`，參數 `[builtFor, Version, missing]`，`missing`＝這份包沒帶非空值的基準鍵數（`ui`＋`server`，與完整度無關：完整度含內建層與目前覆寫，這裡要回答「這份檔本身缺幾句」）。只提示、不擋（設計 §5.2）。
   - 實作：`LocalePackAnalyzer.Analyze` 加參數 `string? platformVersion`（靜態純函式不注入服務）；`PreviewLocalePackHandler` 注入 `IOpsEnvironment` 傳入。`"dev"` 這個字面值只寫在一處：`IOpsEnvironment` 旁加 `public const string DevVersion = "dev"`（`BuildInfo` 改引用它），分析器比對它。
3. 〔技術〕**`LocaleOverlayReview`**（`Packs/LocaleOverlayReview.cs`，`public static`，純函式）：
   ```csharp
   public sealed record OverlayReviewSection(int BaseChanged, int ParamsChanged, int Orphaned,
       IReadOnlyList<string> BaseChangedKeys, IReadOnlyList<string> ParamsChangedKeys, IReadOnlyList<string> OrphanedKeys);
   public sealed record OverlayReview(OverlayReviewSection Ui, OverlayReviewSection Server);
   public static class LocaleOverlayReview
   {
       public static OverlayReview Of(LocalePack? current);                       // 沒有版本 → 全 0
       public static IReadOnlyDictionary<string, string> OrphanedTexts(LocalePack current, string segment); // 給樣板的 review 段
   }
   ```
   - **原文已更新**：覆寫層裡、仍是基準鍵、`BaseHashesJson` 有記雜湊、而且和**現在**內建 zh-TW 原文的雜湊（同 `LocalePackVersions` 的算法：SHA-256 前 16 個十六進位字元）不同的鍵。沒記雜湊的鍵（資料修補）不算，不猜。雜湊函式從 `LocalePackVersions` 的私有 `Hash` 改成 `internal static BaseHash(string)` 共用，不另寫一份。
   - **參數已變**（原文已更新的子集，第二道檢查）：用**現在**的規則重驗覆寫句。後端訊息：`ServerTemplateSyntax.Check(text, 現在的 zh 範本)` 不是 null——這些句子執行期 `string.Format` 會丟例外、`MessageCatalog` 退回內建層並記 Warning（i4d1 判斷 11），使用者其實看不到覆寫，設計 §15 的「語系頁看不到是哪幾句」就是這些。介面文字：`UiMessageSyntax.Check(text, 現在的 UiPlaceholderNames[key])` 不是 null（佔位符名稱被平台改掉，畫面會顯示空白或原樣的 `{x}`）。
   - **已失效**：原始 `UiJson`／`ServerJson`（不經 `OverlaysFrom` 的濾鍵）裡、不在 `BuiltinLocaleResources.BaseKeys`／`BuiltinServerText.BaseKeys` 的鍵。
   - 鍵清單 Ordinal 排序、各取前 `LocalePackLimits.MaxListedItems`（50）筆；計數是全部。JSON 解析失敗（只可能是資料修補）回全 0，不丟例外（同快照的處理）。
4. 〔技術〕**`LocaleCompleteness`**（`Packs/LocaleCompleteness.cs`）：把 `LocalePackAnalyzer.Analyze` 裡算 `CompletenessReport` 的兩行抽成 `public static CompletenessReport Of(string code, LocaleOverlay? overlay)`，定義不變（「該語系自己有句子」＝覆寫層或該語系內建層有非空值，不含退回鏈補的；覆寫層用濾過基準鍵的那份）。另加 `public static IReadOnlyList<MissingCategory> TopMissing(string code, LocaleOverlay? overlay, int take)`：缺的基準鍵依分類分組、取缺最多的前 `take` 組（同數依分類名 Ordinal）；分類＝介面文字鍵的第一段（`approval.panel.x` → `approval`）、後端訊息 `report.`→`report`、`alert.`→`alert`、其餘→`message`。`take` 是 `LocalePackLimits.MaxMissingCategories = 5`（技術限制，列寫死清單）。`public sealed record MissingCategory(string Segment, string Category, int Missing)`。
5. 〔技術〕**分析器重構只搬不改**：Task 1 Step 0 先跑一次 `LocalePackAnalyzerTests`、`LocalePackPreviewApiTests`、`LocalePackApplyApiTests` 全綠，抽出 `LocaleCompleteness` 後同一組照過（預期值不改）。
6. 〔技術〕**清單回應**（`GET i18n/locales`，`LocaleAdminItem` 加兩欄，形狀給前端）：
   ```jsonc
   "completeness": { "ui": { "have": 1790, "total": 2701 }, "server": { "have": 160, "total": 300 },
                     "topMissing": [{ "segment": "ui", "category": "approval", "missing": 120 }] },
   "review": { "ui":     { "baseChanged": 3, "paramsChanged": 0, "orphaned": 1, "baseChangedKeys": ["…"], "paramsChangedKeys": [], "orphanedKeys": ["…"] },
               "server": { "baseChanged": 2, "paramsChanged": 1, "orphaned": 0, "…": [] } },
   "builtFor": "2026.11 或 null"
   ```
   `builtFor` 是目前版的 `BuiltFor`（標 `JsonIgnoreCondition.Never`，前端型別 `string | null`）。完整度用 `LocaleWorld.Overlays`（濾過的），檢視用 `ListCurrentAsync` 的原始列；兩者都在既有 handler 的同一次讀取內，不在迴圈裡另發查詢（`backend.md`：Npgsql 同一連線不能同時兩個讀取器；既有的 `PreviousAsync` 迴圈照舊，是對記憶體裡的清單逐一 await，不是列舉查詢時再查）。清單仍讀資料庫、不讀快照。
7. 〔技術〕**「原文已更新」的確認**（Q3）：分析時，這份包裡**與目前覆寫相同**（`Kind.Unchanged`）、而且該鍵是「原文已更新」的句子，列進計畫的新欄位 `UiConfirm`／`ServerConfirm`（`IReadOnlyList<string>`）；套用時 `LocalePackVersions.BaseHashesJson` 的「換新雜湊」鍵集合改成 `UiSet.Keys ∪ UiConfirm`（後端同理）。`LocalePackPlan.NoChanges` 加上兩個清單都空的條件（只有確認也會寫一個新版本：雜湊不寫進新列就沒有地方存）。報告每段加 `confirmed`（int）。**相容**：部署前建立、還沒過期的預覽列，`PlanJson` 沒有這兩欄，反序列化成 null → 一律當空清單（`LocalePackPlan` 的屬性以 `?? []` 正規化；加一個測試用舊形狀的 JSON 反序列化）。參數已變的後端訊息原樣再匯入會被 `ServerTemplateSyntax` 以 `placeholder` 拒收（i4d1 規則不變），不會被「確認」。
8. 〔技術〕**已失效的覆寫在下一次寫入新版本時清掉**（Q4）：現況就是如此——套用從 `LocaleWorld` 的覆寫層（已濾掉基準鍵以外的鍵）合併出新版本，失效的鍵不會進新列（`ApplyLocalePackHandler` 的 `LocalePackAnalyzer.After(world, plan).Overlays[plan.Code]`）；回到上一版是複製原始 JSON，失效的鍵會跟著回來。本段不改這個行為，只讓它看得到：預覽 handler 在**計畫不是 `NoChanges`** 時，以 `LocaleOverlayReview.Of(目前版).{Ui,Server}.Orphaned` 填報告的 `orphanedDropped: { ui, server }`（分析器不碰原始列，由 handler 補，同 `fileName`、`fileHash` 的補法）；`NoChanges` 時兩個都是 0。
9. 〔技術〕**樣板匯出 API**（Task 3）：`GET i18n/locales/{code}/template`，查詢參數 `scopes`（`ui`、`server`，可重複；至少一個）、`onlyMissing`、`onlyBaseChanged`（bool）、`source`（預設 zh-TW）、`reference`（預設 en-US；空字串＝不附）；目標語系**尚未安裝**時另收 `nativeName`（必填）、`fallback`、`quasarLang`。
   - `GetLocaleTemplateRequest : IRequest<ServiceResult>, ISystemAdminRequest`（**不是** `ITransactionalRequest`、不稽核：設計 §11 的稽核動作清單不含匯出，匯出不改任何資料；`AuditCoverageTests` 只掃交易型請求，不必列白名單）。權限由既有 `SystemAdminBehavior` 擋，fail closed。**不看 Demo 開關**（設計 §12、§13.4：Demo 可匯出）。
   - handler：`LocaleWorldReader.ReadAsync`（讀資料庫，和清單同一個來源）＋`ListCurrentAsync` 取目前版原始列（給 `review` 段）→ `LocaleTemplateBuilder.Build(world, current, options, platformVersion)` → `ServiceResult.Success(new LocaleTemplateFile(fileName, bytes))`；controller 照既有匯出端點的寫法回 `File(bytes, "application/json; charset=utf-8", fileName)`（`Content-Disposition: attachment`），失敗走 `ToActionResult`。檔名 `<正規化代碼>.langpack.json`（代碼過 `LocaleCode.TryNormalize` 才組檔名，設計 §13.1）。
   - 驗證（都回 400 `localePackInvalid`，訊息在 `errors.file`，沿用 i4d1 的錯誤碼表）：代碼格式（`LocalePackMessages.CodeInvalidFormat`）；未安裝時 `nativeName` 必填且過 `LocaleTextSafety`／長度 50（`NativeNameRequired`、`FieldInvalid`）、`fallback` 必須已安裝（`FallbackNotInstalled`）、`quasarLang` 在清單內（`QuasarUnknown`）；`source`／`reference` 必須已安裝（新訊息 `LocaleTemplateLocaleNotInstalled`「語系 {0} 沒有安裝」）；`scopes` 空或有不認得的值（新訊息 `LocaleTemplateScopeInvalid`）。兩個新鍵進 `MessageText`（四語），集中在 `LocalePackMessages`；新建凍結基準檔 `ApplicationTests/Common/ServerTextBaseline/locale-template.json`（只放這兩個鍵），`BuiltinServerTextTests.BaselineFiles` 加檔名；既有四個基準檔（`message-report`、`task-report`、`alert`、`locale-pack`）不改。新鍵也自動成為語系包 `server` 段的基準鍵。
10. 〔技術〕**樣板內容**（`LocaleTemplateBuilder`，純函式；給前端與翻譯者看的形狀）：
    ```jsonc
    {
      "format": "formwright-langpack", "formatVersion": 1,
      "locale": { "code": "th", "nativeName": "ไทย", "quasarLang": "th", "fallback": null },
      "pack": { "builtFor": "dev", "baseLocale": "zh-TW" },
      "ui":     { "approval.x": "目前譯文或空字串" },
      "server": { "Forbidden": "…" },
      "source":    { "locale": "zh-TW", "ui": { "approval.x": "原文" }, "server": { … } },
      "reference": { "locale": "en-US", "ui": { … }, "server": { … } },
      "review": { "baseChanged": { "ui": ["…"], "server": ["…"] }, "paramsChanged": { "ui": [], "server": ["…"] },
                  "orphaned": { "ui": { "舊鍵": "舊譯文" }, "server": {} } }
    }
    ```
    - `ui`／`server` 的值是目標語系**自己的**句子（覆寫層 → 該語系內建層；不含退回鏈補的），沒有就是空字串（設計 §7.1）。扁平鍵、Ordinal 排序、`WriteIndented`、`UnsafeRelaxedJsonEscaping`（泰文、越南文可讀；檔案以附件下載、`application/json`，不在頁面渲染）。
    - 篩選：兩個勾選都沒勾＝全部基準鍵；`onlyMissing`＝自己沒有句子的鍵；`onlyBaseChanged`＝`review` 的原文已更新鍵（含參數已變）；兩個都勾取**聯集**。`ui` 或 `server` 不在 `scopes` 就整段不寫（三段都可缺，設計 §5.1）。
    - `source`、`reference`（Q2）：同一組鍵的原文與參考譯文，沿 `LocaleChain.Fallbacks(code, world.DefaultLocale, …)` 取第一個有值的層（每個語系先覆寫層、再內建層）。把 `LocalePackAnalyzer` 私有的 `Resolve` 搬成 `internal static LocaleWorldText.Resolve(LocaleWorld, IReadOnlyList<string> chain, string segment, string key)`，介面文字與後端訊息共用（分析器改呼叫它，行為不變）。`reference` 空字串＝不寫這段。
    - `review`：只在目標語系有目前版時寫；`orphaned` 帶舊譯文（供改鍵後重新對應，設計 §6.5）。
    - 匯入相容：Parser 只讀 `format`、`formatVersion`、`locale`、`pack`、`ui`、`server`、`designData`，其他頂層欄位略過（i4d1 判斷 1），所以 `source`、`reference`、`review` 匯入時不影響。`locale.fallback` 是 null 時 Parser 視為「沿用」，與內建語系 Q3 略過的規則一致；Task 3 的往返測試鎖住。
    - 未安裝的新代碼：`locale` 段取查詢參數，`ui`／`server` 全是空字串，沒有 `review`。
11. 〔技術〕**大小**：樣板不設伺服器端上限（鍵數有界：基準鍵 × 三段＋失效鍵；泰文全套含原文與參考約 0.6 MB）。前端下載後比對清單的 `packMaxBytes`，超過就照樣存檔並提示（Q8）。
12. 〔技術〕**前端版型**（Task 5）：清單改成卡片格（`row q-col-gutter-md`，卡片 `col-12 col-sm-6 col-lg-4`；390 一欄、820 兩欄、1366／1920 三欄），所有寬度同一套模板（`frontend.md`「清單在手機寬度改用卡片…操作鈕放卡片下方」）。卡片內：標題列（自稱、代碼、來源、啟用／預設標籤）、完整度兩行（`q-linear-progress` 加文字「介面 1790／2701（66%）」）、覆寫句數與目前版本（沿用 i4d1 文案）、檢視提示（有才顯示）、`q-expansion-item`「查看明細」（缺最多的分類、三種鍵清單各前 50 筆並註明總數）；`q-card-actions` 放「匯出樣板」「以此語系預覽」（只限未啟用）與「⋯」（回到上一版、移除覆寫、移除語系，沿用 i4d1）。既有 `data-testid`（`locale-row-<code>`、`locale-actions-<code>` 等）保留，既有 spec 不改預期值。長代碼與長自稱 `overflow-wrap: anywhere`。顏色只用 Quasar 色名與既有 CSS 變數（`src/css/themes/` 守衛）。
13. 〔技術〕**以此語系預覽**（設計 §12、M2）：按鈕呼叫 `switchLocale({ global: composer }, code, { preview: true })`（`src/i18n/ui-messages.ts`，i4b 判斷 17 的完整試看流程：只在這個分頁、`sessionStorage`、登出結束），不另寫一份。顯示條件：該語系未啟用、不是目前試看中的語系。匯入對話框在套用成功、新語系未啟用時，「現在啟用」旁加同一個按鈕；按之前先 `await queryClient.refetchQueries({ queryKey: ['effective-policies'] })`，等 `installedLocales()` 含這個代碼再切（`LocaleSwitcher` 的試看清單也從這裡來），否則顯示 `locale.loadFailed`。後端訊息在試看時仍是預設語系（設計 §15 已列），按鈕旁的說明寫明。
14. 〔技術〕**匯出下載**：`dynamicApi.exportLocaleTemplate(code, options)` 照 `exportReport` 的寫法（`responseType: 'blob'`、`validateStatus: () => true`、錯誤本文從 Blob 解析成 `ApiError`、檔名取 `Content-Disposition`），對話框用 `URL.createObjectURL` 下載（同 `ReportViewPage.vue`）。
15. 〔技術〕**安全網（各批必跑）**：`AuditCoverageTests`、`AuthorizationWriteInventoryTests`（ApplicationTests）、`EndpointRoutesTests`、`SecurityTests`（WebAPITests）；新端點落在 `/api/v3/dynamic/` 底下（`EndpointRoutesTests` 自動涵蓋）。前端全域守衛：`src/css/themes/no-hardcoded-colors.vitest.spec.ts`、`src/i18n/locales.vitest.spec.ts`（四語鍵一致、可編譯）、`src/i18n/table-no-data-label.vitest.spec.ts`、`src/domain/dynamic/policy-i18n.vitest.spec.ts`。

---

## 代為決定的事

### 使用者行為類（答案反過來，使用者的操作、畫面或結果會變；主控暫定，待使用者確認）

- **Q1 `builtFor` 寫什麼、什麼時候提示「這份語系包是舊版產生的」？**
  - 例：廠商用 2026.10 版匯出的日文樣板翻好，客戶 2026.11 版才匯入。設計 §5.2 要提示「這份語系包是 2026.10 版產生的，目前是 2026.11，缺 42 句」。平台有建置注入的版本號（`/api/version` 的 `version`），但目前沒有發版流程，Demo 與自行建置都是 `dev`。
  - 推薦：**樣板寫建置版本號；兩邊都有正式版本號（不是 `dev`）而且不同時才提示，提示附「這份檔缺 N 句」，不擋**。理由：`dev` 和 `dev` 比不出新舊，硬比會每次誤報或永遠不報；缺幾句本來就由完整度與鍵比對回答（設計 T5「版本字串只提示」）。
  - 另一個選項：改寫「基準鍵指紋」（基準鍵集合的雜湊）進 `builtFor`，鍵集合不同就提示。代價是使用者看到一串十六進位而不是版本號，設計 §5.1 的範例也得改。
  - 選錯代價：選「版本號」，在還沒有發版流程前（Demo、自行建置）永遠不會提示；選「指紋」如上。
- **Q2 JSON 樣板要不要附原文與參考譯文？**
  - 例：管理員匯出日文樣板交給翻譯社。設計 §7.1 讓匯出時選「原文語系（預設繁中）」與「參考語系（預設英文）」，但語系包格式（§5.1）沒有放原文的地方；只有空字串的 `"menu.home": ""`，翻譯者不知道要翻什麼。
  - 推薦：**附**：樣板多兩段 `source`、`reference`（同一組鍵的原文與參考譯文），匯入時略過（格式本來就忽略不認得的頂層欄位）。理由：「只匯出缺的句子」的樣板全是空字串，不附原文就沒法翻；檔案約大 2 倍，仍遠低於 5 MB。
  - 另一個選項：不附，原文與參考語系兩個選項等 i4e 的 Excel 樣板才出現；要原文就另外匯出繁中的樣板對照。代價是兩個檔要靠鍵名對照。
  - 選錯代價：選「附」，翻譯工具若把整個檔當成要翻的內容，可能連 `source` 段也翻掉（匯入時略過，不影響結果，只是浪費翻譯費）；選「不附」如上。
- **Q3 「原文已更新」的句子，管理員看過後覺得譯文不必改，怎麼讓提示消失？**
  - 例：升級後繁中「關卡」改成「簽核關卡」，泰文覆寫那句被標「原文已更新」；泰籍主管看過覺得泰文不用改。現況（i4d1）只有「改了譯文」才會換掉記下的原文，原樣再匯入不算數，提示永遠消不掉，只能「移除覆寫」。
  - 推薦：**把那句原樣放進語系包再匯入，就算確認**：報告列「確認原文已更新 N 句」，套用後提示消失（技術上要寫一個新版本，所以「回到上一版」會回到確認之前）。最順的做法是匯出時勾「只匯出原文已變的句子」，看過後整份匯回。
  - 另一個選項：語系頁每個語系加一個「標為已確認」按鈕，一次確認全部。代價是多一個寫入 API、稽核動作與確認框；而且一鍵全確認容易沒看就按。
  - 選錯代價：選「再匯入即確認」，管理員拿一份很舊的完整檔再匯入時，也會順便把所有原文已更新的句子確認掉（報告會列出確認幾句）；選「按鈕」如上。
- **Q4 「覆寫已失效」（平台改了鍵名或刪掉的句子）要保留到什麼時候？**
  - 例：平台把 `approval.stage` 改名 `approval.step`，日文覆寫的那句失效（畫面上不會用到它）。現況是下一次匯入日文時它就被默默丟掉；回到上一版又會帶回來。
  - 推薦：**照現況在下一次匯入同語系時清掉，但報告事先列出「這次套用會清掉 N 句已失效的覆寫」**；在那之前語系頁顯示句數與鍵名，匯出樣板的 `review` 段附舊譯文供改新鍵。理由：失效的句子不會再被用到，留著只會讓句數越積越多；改鍵後的正確做法本來就是把譯文放到新鍵重新匯入。
  - 另一個選項：每次匯入都保留失效的句子，直到「移除覆寫」。代價是它們永遠佔著句數，客服看到的覆寫句數不準。
  - 選錯代價：選「下次匯入清掉」，沒看報告的管理員會失去那幾句舊譯文（上一版仍在，可回到上一版取回或從匯出樣板找回）；選「保留」如上。
- **Q5 語系頁要列出「原文已更新」「已失效」是哪幾句，還是只顯示句數？**
  - 例：泰文顯示「12 句覆寫的原文已更新（其中 2 句參數已變，目前顯示內建翻譯）」。設計 §6.5 只寫「完整度旁顯示 N 句」「匯出樣板時標記出來」；設計 §15 另說 i4d2 要讓管理員看得到「參數改變、執行期退回內建翻譯」的後端訊息是哪幾句。
  - 推薦：**卡片顯示句數，展開「查看明細」列鍵名（各前 50 筆並註明總數）與缺最多的 5 個分類**；完整清單與原文對照靠匯出樣板。理由：參數已變的那幾句使用者現在看不到覆寫，管理員要能直接知道是哪幾句才會去修；鍵名清單不需要新 API（清單回應多帶前 50 筆）。
  - 另一個選項：只顯示句數，鍵名全靠匯出。代價是要查一句就得下載、開檔、搜尋。
  - 選錯代價：選「展開列鍵名」，看到的是鍵（`approval.panel.x`）不是句子內容，對非工程背景的管理員不直觀；選「只顯示句數」如上。
- **Q6 尚未安裝的語系（例如還沒有的印尼文）能不能匯出樣板？**
  - 例：客戶要給印尼移工用，想先拿一份空白樣板給翻譯社。設計 §7.1 寫「目標語系可以是尚未安裝的新代碼，此時要填 `locale` 資訊」。
  - 推薦：**能**：頁首「匯入語系包」旁加「新語系樣板」，填代碼、自稱、上層語系（選填）、日期元件語言包（選填）後下載；送出前後端驗代碼格式與上層語系已安裝。理由：沒有這個入口，第一份新語系包只能手寫 JSON。
  - 另一個選項：只能匯出已安裝的語系，新語系先匯出繁中或英文樣板、自己改 `locale` 段。代價是管理員要手改 JSON。
  - 選錯代價：選「能」多約 80 行前端；選「不能」如上。
- **Q7 Demo 種子要不要放一份「匯入過」的語系，讓訪客看得到「原文已更新」「已失效」？**（AGENTS.md「Demo 資料」：有使用者看得到的功能，合併前 Demo 要能示範成功路徑與主要失敗分支）
  - 例：Demo 站不能套用語系包（§13.4），所以永遠不會有覆寫層，卡片上不會出現「原文已更新」「已失效」與「回到上一版」。
  - 推薦：**種子不改**。Demo 上能示範的路徑：語系頁四張卡片的完整度 → 匯出泰文樣板（或新語系樣板）→ 用下載的檔匯入 → 預覽報告 → 套用被擋（失敗分支）；到政策把 ไทย 停用 → 卡片出現「以此語系預覽」→ 試看。「原文已更新」「已失效」只在本機走查示範（測試夾具直接寫舊雜湊與失效鍵），交付說明寫明。理由：要讓 Demo 有這兩種狀態，得在種子裡偽造一筆匯入紀錄與過期的原文雜湊，種子會多一條只為 Demo 存在、和真實匯入路徑不同的寫法，而且每天重部署都要能重現。
  - 另一個選項：Demo 種子（只在 `Demo:LoginEnabled` 時）寫一筆泰文覆寫層，含 3 句舊雜湊、1 句失效鍵。代價是約 60 行種子與它的測試，並要 grep 既有對語系清單、覆寫句數的斷言。
  - 選錯代價：選「不改」，Demo 訪客看不到這兩個提示長什麼樣（手冊有截圖文字說明）；選「改」如上。
- **Q8 匯出的樣板比目前的「語系包檔案大小上限」還大時怎麼辦？**
  - 例：系統管理員把上限（A38）調成 100 KB，匯出的泰文完整樣板（含原文與參考）約 600 KB，翻好後匯不回去。
  - 推薦：**照樣下載，下載後提示「這份樣板 0.6 MB，超過目前的語系包大小上限 0.1 MB；匯入前要先調高上限，或只匯出部分範圍」**。理由：樣板是唯讀的，擋下載沒有保護到任何東西；提示讓管理員在交給翻譯社之前就知道。
  - 另一個選項：超過就不給下載。代價是管理員得先去政策頁調高才拿得到檔。
  - 選錯代價：選「照樣下載」，沒注意提示的人會到匯入時才被擋（訊息帶上限數字）；選「不給下載」如上。

### 與設計的差異（設計文件本計畫不改；Task 6 交付時回填）

- **`builtFor` 的版本來源與提示條件**（設計 §5.2 只寫「只提示」）：見判斷 2、Q1。
- **原文已更新的確認方式**（設計 §6.5 沒寫怎麼消掉）：見判斷 7、Q3。
- **已失效的覆寫在下一次匯入時清掉**（設計 §6.5 只寫要看得到）：見判斷 8、Q4。
- **JSON 樣板多 `source`、`reference`、`review` 三段**（設計 §5.1 的格式沒有這些；§7.1 有原文、參考語系的選項）：見判斷 10、Q2。格式版本不變（Parser 本來就忽略不認得的頂層欄位）。
- **「參數已變」是「原文已更新」的子集，另外標出**（設計 §6.5 只有原文已更新；§15 要求列出執行期退回內建的後端訊息）：見判斷 3。

### 技術類（主控決定，可推翻）

判斷 1–15 全部是技術類。重點摘要：沒有 migration、沒有新的批次寫入（判斷 1）；平台版本取 `IOpsEnvironment.Version`、`dev` 不比（判斷 2）；覆寫檢視三種計數由純函式從原始列算、參數已變用現在的驗證規則重驗（判斷 3）；完整度定義從分析器抽出共用（判斷 4、5）；樣板匯出是不稽核、不看 Demo 開關的唯讀系統管理員 API，值取語系自己的句子、原文與參考沿退回鏈取（判斷 9、10）；卡片版型所有寬度同一套（判斷 12）；試看沿用 i4b 的 `switchLocale`（判斷 13）。

---

## 票

- **#71（併入 i4d2-2 Task 5）**：`LocalePackImportDialog` 的兩個回歸案例（關閉重開後舊的套用／「現在啟用」才失敗不顯示在新對話框；`extraKeys` 含 HTML／腳本字串以純文字呈現）。理由：本段 Task 5 本來就要改這個對話框（確認句數、已失效提示、以此語系預覽），同一個 spec 檔、約 60 行測試，分開做要兩次熟悉同一段世代核對邏輯。合併後關票留 commit SHA。
- **#70（不併入）**：CI PostgreSQL 上 `LocaleManageApiTests` 等語系快照等待逾時的根因。理由：票上的下一步是「等下一次逾時拿診斷訊息」，本機重現不出，沒有可寫的失敗測試；根因調查與功能實作不同驗證迴圈（`.claude/rules` 派工判準：失敗後的診斷另派）。本段新增的 WebAPI 測試照樣經 `LocaleTestKit.WaitForSnapshotAsync`，**若 i4d2 的 CI 出現同樣逾時**：把診斷訊息（預期與實際 packId、generation、嘗試次數、耗時）貼到 #70，照原樣重跑一次（`docs/ai/testing.md`「異常判讀」），不放寬逾時、不在本段追根因。
- 其他開著的票（#66 名稱解析、#64 串流匯入上限訊息、#13 MessageText 四語、#33／#29／#23 母語審稿等）與本段的檔案不重疊，不併入。本段審查與實測的新待辦用 `tickets:ticket-create` 開 backlog 票。

## 與設計的矛盾（規劃時發現，Task 6 回填時一併處理）

1. **i4d1 判斷 24 與設計 §5.2、§15「平台目前沒有可比的版本號」不精確**：o1a 起就有 `BuildInfo.Version`／`IOpsEnvironment.Version`（`/api/version`），缺的是發版流程（`APP_VERSION` 沒人注入，o1a 判斷 J11）。回填時把 §15 那句改成「沒有發版流程時版本是 dev，不比對」。
2. **設計 §6.5「覆寫已失效…管理員要看得到」與 i4d1 的實作衝突**：套用時從濾過基準鍵的覆寫層合併，失效的句子在下一次匯入就被丟掉，沒有任何提示（Q4）。
3. **設計 §6.5 的「原文已更新」沒有消掉的路徑**：i4d1 只在譯文改變時換雜湊（Q3）。
4. **設計 §7.1 的原文、參考語系選項只對 Excel 有意義**：JSON 格式（§5.1）沒地方放（Q2）。
5. **設計 §6.5「390 寬以卡片列出」與 i4d1 Q8 的「清單在 390 寬仍是同一個列表」**：i4d1 是過渡（設計 §6.5 最後一點已註明），本段改卡片，同時符合 `frontend.md` 的使用者裁決。
6. **AGENTS.md「Demo 資料」與設計 §13.4（Demo 不能套用）**：Demo 永遠沒有覆寫層，「原文已更新」「已失效」「回到上一版」在 Demo 上示範不了（Q7）。

---

## Global Constraints

- **沒有匯入語系包時，語系頁以外的行為不變**：`I18nUiApiTests`、`LocaleRegistryUiTests`、`MessageCatalogTests.Builtin_SameAsBaseline`、`BuiltinServerTextTests.Table_EqualsBaselines`、`InstalledLocalePolicyTests`、`AuthApiTests.Options_IncludesLocaleSettings` 照過；既有四個範本基準檔不改（S4）。
- **既有測試的預期值不改**，例外只有：`BuiltinServerTextTests.BaselineFiles` 加 `locale-template.json`；`LocalesPage.vitest.spec.ts` 因版型由列表改卡片而改的選擇器（`data-testid` 保留，只改結構相關的查詢）；`LocalePackAnalyzerTests` 呼叫 `Analyze` 多傳 `platformVersion: null`。
- **鐵則**：本段沒有新的業務政策（A38 仍是上限的唯一來源，前端從清單 API 取）；新的寫死項目列在下一節；權限只限系統管理員、fail closed；驗證全在後端（前端只在「送出一定會被擋」時先擋：匯出對話框的代碼格式不先擋，照常送出顯示後端訊息）；沒有執行期 DDL；空系統照樣能匯出樣板、看完整度（不依賴任何應用程式）。
- **PostgreSQL**：清單 API 與樣板 API 的 WebAPI 測試標 `Category=Postgres`（讀 `Dynamic_LocalePack` 原始列與 JSON 欄），i4d2-1 收尾在 PostgreSQL 跑過一次。不寫 raw SQL。
- **前端**：新句子加進 `shared/i18n/zh-TW/ui.json`，en-US、th、vi 同步（不留中文當英文），維持單行 JSON；四個全域守衛 spec 每次改前端都跑（判斷 15）。
- **測試只跑窄範圍**；後端與其他吃 CPU 的測試不並行；窄範圍 vitest 加 `--maxWorkers=2`。完整套件交 CI（i4d2-2 收尾的 gate）。
- **git**：逐檔 `git add`；commit 前 `git status`；不用 `--no-verify`、不 force push、不 merge commit；公開 repo 不寫公司字串、個人絕對路徑、內網 IP；測試資料全部虛構。
- **本機走查**：`bash scripts/unattended/local-demo.sh <worktree 絕對路徑>`（一律絕對路徑，背景執行等 READY）。
- 段收尾用 `formwright-purpose-check`；上線用 `formwright-ship`。

## 寫死清單（本段新增，逐條標性質；Task 6 寫進設計 §14）

| 項目 | 性質 |
|---|---|
| 缺最多的分類只列前 5 組（`LocalePackLimits.MaxMissingCategories`） | 技術限制 |
| 覆寫檢視的鍵清單各列前 50 筆（沿用 `MaxListedItems`） | 技術限制 |
| 沒有發版流程時的版本字串 `dev` 不比對（`IOpsEnvironment.DevVersion`） | 技術限制 |
| 樣板的 `builtFor` 超過 20 字元截斷 | 技術限制（沿用 `MaxPackBuiltFor`） |
| 「原文已更新」以 zh-TW 內建原文的雜湊判斷；沒記雜湊的句子不算 | 技術限制（沿用 i4d1 判斷 7） |
| 分類規則：介面文字取鍵的第一段，後端訊息分 `report`、`alert`、`message` | 技術限制 |
| 樣板檔名 `<代碼>.langpack.json`，代碼過格式守衛才組 | 安全 |

**業務政策**：本段沒有新增；樣板大小提示讀 A38 `locale.packMaxBytes`。

## 檔案地圖

**共用檔（並行工作要協調；★＝本段會動）**

| 共用檔 | 本段 | 說明 |
|---|---|---|
| ★ 後端訊息表 | Task 3 | `Application/Common/MessageText.cs`（兩個新鍵），新基準檔 `locale-template.json` |
| ★ 介面文字 | Task 4、5 | `shared/i18n/{zh-TW,en-US,th,vi}/ui.json` 的 `localePacks.*` |
| ★ 語系包分析 | Task 1 | `Packs/{LocalePackAnalyzer,LocalePackModels,LocalePackVersions}.cs` |
| DbContext 模型與 migration | 不動 | 本段沒有 migration |
| `LocaleRegistry`、`MessageCatalog` | 不動 | 只讀它們既有的公開成員 |

**新檔（後端）**：`Packs/{LocaleOverlayReview,LocaleCompleteness,LocaleWorldText,LocaleTemplateBuilder}.cs`；`Queries/GetLocaleTemplate/{GetLocaleTemplateRequest,GetLocaleTemplateHandler}.cs`；`ApplicationTests/Common/ServerTextBaseline/locale-template.json`。

**新檔（測試）**：`ApplicationTests/Dynamic/LocalePacks/{LocaleOverlayReviewTests,LocaleCompletenessTests,LocaleTemplateBuilderTests}.cs`；`WebAPITests/Dynamic/LocaleTemplateApiTests.cs`。

**新檔（前端）**：`src/presentation/locale/LocaleTemplateExportDialog.vue`＋`.vitest.spec.ts`；`src/presentation/locale/LocaleCard.vue`（一張卡片，`LocalesPage.vue` 過 200 行時拆出；拆不拆由實作者依行數決定）。

---

# i4d2-1 後端

### Task 1: `builtFor` 比對、原文已更新的確認、已失效的清除報告

**Files:**
- Modify: `Application/Dynamic/OpsService/IOpsEnvironment.cs`（`DevVersion`）、`WebAPI/Ops/BuildInfo.cs`（改引用 `DevVersion`）
- Create: `Packs/LocaleCompleteness.cs`、`Packs/LocaleWorldText.cs`（判斷 4、10：從分析器搬出，行為不變）、`Packs/LocaleOverlayReview.cs`（判斷 3；本 Task 只用到 `Of`，Task 2 再接進清單）
- Modify: `Packs/LocalePackAnalyzer.cs`（`Analyze(…, string? platformVersion)`、確認鍵、改用抽出的類別）、`Packs/LocalePackModels.cs`（`LocalePackPlan` 加 `UiConfirm`／`ServerConfirm` 並正規化 null、`SectionReport` 加 `Confirmed`、`LocalePackReport` 加 `OrphanedDropped`、`NoChanges` 條件）、`Packs/LocalePackVersions.cs`（`BaseHash` 改 `internal static`；換新雜湊的鍵集合含確認鍵）、`Commands/PreviewLocalePack/PreviewLocalePackHandler.cs`（注入 `IOpsEnvironment`、補 `orphanedDropped`）、`Commands/ApplyLocalePack/ApplyLocalePackHandler.cs`（雜湊換新集合）、`Domain/Localization/LocalePackLimits.cs`（`MaxMissingCategories`）
- Test: `ApplicationTests/Dynamic/LocalePacks/{LocalePackAnalyzerTests（加案例）,LocaleOverlayReviewTests,LocaleCompletenessTests}.cs`；`WebAPITests/Dynamic/{LocalePackPreviewApiTests,LocalePackApplyApiTests}.cs`（加案例）

- [ ] **Step 0: 基準**：跑 Step 2 的三個指令一次，記下全綠（重構前基準，判斷 5）。
- [ ] **Step 1: 寫失敗的測試**
  - `LocalePackAnalyzerTests.BuiltFor_DiffersFromPlatform_Warns`：`builtFor` "2026.10"、`platformVersion` "2026.11"、包只帶 30 句 → 警告 `builtForDiffers`，參數 `["2026.10","2026.11", 基準總數−30]`；`BuiltFor_Dev_NoWarning`（任一邊是 `dev`、null、空字串，或兩邊相等）→ 沒有這個警告。
  - `LocalePackAnalyzerTests.Unchanged_BaseChanged_IsConfirmed`：世界裡 `th` 的覆寫層有 `common.save`＝"X"，目前版記的雜湊是假的舊雜湊；包帶同一句 "X" → `plan.UiConfirm` 含 `common.save`、`report.ui.confirmed` 1、`plan.NoChanges` false；雜湊相同（原文沒變）時 `UiConfirm` 空、`NoChanges` true。分析器拿目前版雜湊的方式：`LocaleWorld` 不帶原始列，所以 `Analyze` 另收 `OverlayReview? currentReview`（預覽 handler 以 `LocaleOverlayReview.Of(目前版)` 算好傳入；`BaseChangedKeys` 只有前 50 筆，所以 `OverlayReview` 內部另保留完整的 `IReadOnlySet<string>`，`internal` 不序列化）。
  - `LocalePackAnalyzerTests.ParamsChanged_Server_ReimportRejected`：目前覆寫是舊參數的範本 → 原樣再匯入被拒（`placeholder`），不進 `ServerConfirm`。
  - `LocalePackModelsTests.Plan_OldJsonWithoutConfirm_DeserializesEmpty`：i4d1 形狀的 `PlanJson`（沒有兩個新欄位）用 `LocalePackStorage.Json` 反序列化 → 兩個清單是空的，`NoChanges` 照舊算。
  - `LocaleOverlayReviewTests`：沒有版本 → 全 0；雜湊不同 → 原文已更新；雜湊缺 → 不算；後端覆寫參數個數和現在 zh 不同 → 原文已更新且參數已變；介面文字用了現在不允許的名稱 → 參數已變；原始 `UiJson` 有非基準鍵 → 已失效 1 且鍵名在清單；60 個已失效鍵 → 計數 60、清單 50 筆且 Ordinal 排序；`UiJson` 不是合法 JSON → 全 0 不丟例外。
  - `LocaleCompletenessTests`：同一組輸入和 `Analyze` 報告的 `completeness` 相等；`TopMissing`：介面文字缺 `approval.*` 3 句、`common.*` 1 句 → 第一組 `("ui","approval",3)`；同數依分類名排序；最多 5 組；後端訊息的 `report.`、`alert.`、其他分到三組。
  - `LocalePackPreviewApiTests.Preview_OrphanedDropped`：套用 `ja-partial` 後，用 `DbContext` 直接把目前版 `UiJson` 加一個非基準鍵（模擬平台改鍵名），再預覽一份有新句子的包 → `orphanedDropped.ui` 1；預覽一份和目前完全相同的包（`NoChanges`）→ 0。`Preview_BuiltFor_PlatformVersion`：`TestApp.ExtraServices` 換成回傳 "2026.11" 的假 `IOpsEnvironment`，上傳 `builtFor` "2026.10" → 警告存在；預設主機（`dev`）→ 沒有。
  - `LocalePackApplyApiTests.Apply_Confirm_RefreshesHash`：造原文已更新的覆寫（同上，直接改 `BaseHashesJson` 成舊雜湊），原樣再匯入並套用 → 新目前版的雜湊等於現在的原文雜湊、`Id` 換新、上一版是確認前的那一列；`Apply_Orphaned_Dropped`：已失效鍵在套用後的目前版 `UiJson` 不存在、上一版仍在（回到上一版會帶回來，判斷 8）。兩個都標 `Category=Postgres`。
- [ ] **Step 2: 跑測試確認失敗**：`cd backend && dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~LocalePacks"`；`dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~LocalePackPreviewApiTests|FullyQualifiedName~LocalePackApplyApiTests"`
- [ ] **Step 3: 實作**（判斷 2–5、7、8）。
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；另跑 ApplicationTests `--filter "FullyQualifiedName~AuditCoverageTests|FullyQualifiedName~AuthorizationWriteInventoryTests|FullyQualifiedName~Ops"`（`IOpsEnvironment` 有改）；PostgreSQL：`FORMWRIGHT_TEST_DB=postgres dotnet test tests/DynamicModules.WebAPITests --filter "Category=Postgres&(FullyQualifiedName~LocalePackPreviewApiTests|FullyQualifiedName~LocalePackApplyApiTests)"`。
- [ ] **Step 5: Commit**（`feat(i18n): 語系包比對平台版本、原文已更新可重新匯入確認、預告清掉已失效的覆寫`）。超過 700 行就拆兩個 commit：1a＝抽出 `LocaleCompleteness`／`LocaleWorldText`（純重構，Step 0 的測試照過）；1b＝其餘。

**驗收**：Step 4 全過（含 PostgreSQL）；Step 0 記下的既有測試預期值沒改（`git diff` 只有新增案例與 `platformVersion: null`）。

### Task 2: 清單 API 帶完整度與覆寫檢視

**Files:**
- Modify: `Queries/GetLocales/GetLocalesRequest.cs`（`LocaleAdminItem` 加 `Completeness`、`Review`、`BuiltFor`；新 record `LocaleAdminCompleteness(CompletenessCount Ui, CompletenessCount Server, IReadOnlyList<MissingCategory> TopMissing)`）、`GetLocalesHandler.cs`
- Test: `WebAPITests/Dynamic/LocaleManageApiTests.cs`（加案例）

- [ ] **Step 1: 寫失敗的測試**（`LocaleManageApiTests`，新案例標 `Category=Postgres`）
  - `List_Factory_Completeness`：出廠四列的 `completeness.ui.have`＝`total`＝`BaseKeys.Count`（內建層完整），`topMissing` 是空陣列，`review` 三種計數全 0，JSON 裡**有** `builtFor` 鍵且是 null。
  - `List_NewLocale_Completeness`：套用 `ja-partial`（30 句介面）→ `ja` 的 `completeness.ui.have` 30、`topMissing` 至多 5 組且第一組缺最多。
  - `List_Review_BaseChangedAndOrphaned`：照 Task 1 的寫法直接改 `th` 目前版的 `BaseHashesJson`（2 句舊雜湊）與 `UiJson`（加 1 個非基準鍵）→ `review.ui.baseChanged` 2、`orphaned` 1、鍵名都在清單；改一句 `ServerJson` 成參數錯的範本 → `review.server.paramsChanged` 1。
  - `List_ReadsDb_NotSnapshot`：直接寫資料庫、不 `Invalidate`，清單立刻反映（沿用 i4d1 `WorldReader_DbAheadOfRegistry` 的寫法）。
  - 一般使用者 403（既有案例涵蓋就不重寫，確認仍在）。
- [ ] **Step 2: 跑測試確認失敗**：`cd backend && dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~LocaleManageApiTests"`
- [ ] **Step 3: 實作**（判斷 6）：handler 一次取 `ListCurrentAsync`，逐語系 `LocaleCompleteness.Of`／`TopMissing`、`LocaleOverlayReview.Of`。
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；另跑 `--filter "FullyQualifiedName~EndpointRoutesTests|FullyQualifiedName~SecurityTests"`；PostgreSQL：`FORMWRIGHT_TEST_DB=postgres dotnet test tests/DynamicModules.WebAPITests --filter "Category=Postgres&FullyQualifiedName~LocaleManageApiTests"`。
- [ ] **Step 5: Commit**（`feat(i18n): 語系清單帶完整度、缺最多的分類與覆寫檢視`）

**驗收**：Step 4 全過；清單回應形狀與判斷 6 相同（寫進交棒說明）。

### Task 3: JSON 樣板匯出 API

**Files:**
- Create: `Packs/LocaleTemplateBuilder.cs`（判斷 10）、`Queries/GetLocaleTemplate/{GetLocaleTemplateRequest,GetLocaleTemplateHandler}.cs`（判斷 9；`LocaleTemplateOptions`、`LocaleTemplateFile` 放在 Request 檔）
- Modify: `WebAPI/Controllers/v3/Dynamic/I18nController.cs`（`[HttpGet("locales/{code}/template")]`）、`Application/Common/MessageText.cs`（兩個新鍵四語）、`LocaleService/LocalePackMessages.cs`
- Create: `ApplicationTests/Common/ServerTextBaseline/locale-template.json`；Modify: `ApplicationTests/Common/BuiltinServerTextTests.cs`（`BaselineFiles`）
- Test: `ApplicationTests/Dynamic/LocalePacks/LocaleTemplateBuilderTests.cs`、`WebAPITests/Dynamic/LocaleTemplateApiTests.cs`（新）

- [ ] **Step 1: 寫失敗的測試**
  - `LocaleTemplateBuilderTests`（純函式，造 `LocaleWorld`）：
    - `Builtin_Full_AllKeysOwnText`：`th`、兩段、不篩 → `ui` 鍵集＝`BaseKeys`，值＝內建泰文；有覆寫的句子是覆寫值；鍵 Ordinal 排序（比對序列化後的屬性順序）。
    - `OnlyMissing`：`ja` 有 30 句 → `ui` 只有其餘的鍵、值全是空字串；`OnlyBaseChanged` → 只有原文已更新的鍵、值是目前覆寫；兩個都勾 → 聯集。
    - `SourceAndReference`：`source`＝zh-TW、`reference`＝en-US 的同一組鍵；`reference` 空字串 → 沒有 `reference` 段；`source` 是 `ja`（缺句）→ 缺的句子沿 `ja` 的鏈取（上層、預設語系、繁中）。
    - `Review`：有目前版時 `review.orphaned.ui` 帶舊譯文；沒有版本時沒有 `review` 段。
    - `NewCode`：未安裝的 `id` → `locale` 段取選項、`ui`／`server` 全空、沒有 `review`。
    - `BuiltFor`：`platformVersion` 25 字元 → 截成 20；null → `dev`。
    - `ScopeUiOnly` → 沒有 `server` 段（`source`、`reference` 也只有 `ui`）。
    - `RoundTrip_Parses`：每個案例的輸出過 `LocalePackParser.Parse` 沒有錯誤、沒有逐鍵問題。
  - `LocaleTemplateApiTests`（新類別標 `Category=Postgres`）：
    - `Export_Builtin_Th`：200、`Content-Type` 是 `application/json`、`Content-Disposition` 是 attachment 且檔名 `th.langpack.json`；本文可解析、`format`／`formatVersion` 正確。
    - `Export_ThenImport_RoundTrip`（主要驗收）：匯出 `th` 全部 → 原檔上傳預覽 → `ui.rejected`、`server.rejected`、`ui.extra`、`server.extra` 都是 0，`ui.unchanged + ui.sameAsBuiltin + ui.skipped`＝基準鍵數；匯出新代碼 `id`（自稱「Bahasa Indonesia」、上層 `en-US`）→ 預覽 `locale.isNew` true、`fallbackCode` `en-US`、`ui.skipped`＝基準鍵數。
    - `Export_Invalid_400`：代碼格式不對、未安裝且缺 `nativeName`、`fallback` 未安裝、`quasarLang` 不在清單、`source` 未安裝、`scopes` 空或有 `designData` → 各自 400 `localePackInvalid`、`errors.file` 是對應訊息（`X-Locale: en-US` 時是英文）。
    - `Export_NonAdmin_403`、`Export_DemoOff_Allowed`（`Localization:PackImportEnabled=false` 仍 200，設計 §12）。
    - `Export_OverlayWithScript_IsData`：`th` 覆寫一句含 `<script>` 的介面文字 → 匯出本文裡是原字串（JSON 跳脫規則下可解析回相同字串），回應是附件不是 `text/html`。
  - `BuiltinServerTextTests` 照既有規則自動比對新基準檔（四語、參數與 zh 一致）。
- [ ] **Step 2: 跑測試確認失敗**：`cd backend && dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~LocaleTemplateBuilderTests|FullyQualifiedName~BuiltinServerTextTests"`；`dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~LocaleTemplateApiTests"`
- [ ] **Step 3: 實作**（判斷 9–11）；`locale-template.json` 在 Step 3 由新表的兩個鍵產生後凍結（同 i4d1 判斷 18 的做法）。
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；安全網 ApplicationTests `--filter "FullyQualifiedName~AuditCoverageTests|FullyQualifiedName~AuthorizationWriteInventoryTests|FullyQualifiedName~MessageCatalogTests"`、WebAPITests `--filter "FullyQualifiedName~EndpointRoutesTests|FullyQualifiedName~SecurityTests|FullyQualifiedName~I18nUiApiTests"`；PostgreSQL：`FORMWRIGHT_TEST_DB=postgres dotnet test tests/DynamicModules.WebAPITests --filter "Category=Postgres&(FullyQualifiedName~LocaleTemplateApiTests|FullyQualifiedName~LocaleManageApiTests|FullyQualifiedName~LocalePack)"`。
- [ ] **Step 5: Commit**（`feat(i18n): 語系包 JSON 翻譯樣板匯出（含原文、參考與覆寫檢視）`）

**驗收**：Step 4 全過（含 PostgreSQL）；`git diff --stat main...HEAD -- backend/tests/DynamicModules.ApplicationTests/Common/ServerTextBaseline/` 只有新增 `locale-template.json`。**i4d2-1 交棒**：照「子段切法」的交棒條件寫說明。

---

# i4d2-2 畫面與收尾

### Task 4: 前端型別、API、查詢與樣板匯出對話框

**Files:**
- Modify: `frontend/src/domain/dynamic/locale-pack-types.ts`（`LocaleAdminEntry` 加 `completeness`、`review`、`builtFor: string | null`；報告加 `confirmed`、`orphanedDropped`；`LocaleTemplateOptions`）、`frontend/src/services/api/dynamic-api.ts`（`exportLocaleTemplate`，判斷 14）
- Create: `frontend/src/presentation/locale/LocaleTemplateExportDialog.vue`＋`.vitest.spec.ts`
- Modify: `shared/i18n/{zh-TW,en-US,th,vi}/ui.json`（`localePacks.export.*`）

對話框（繁中文案；四語都要給）：標題「匯出翻譯樣板」；目標語系（從語系卡片開啟時固定顯示自稱與代碼；從頁首「新語系樣板」開啟時是代碼、自稱、上層語系（選填，下拉列已安裝語系）、日期元件語言包（選填）四個輸入，Q6）；原文語系（下拉，已安裝，預設繁體中文）、參考語系（下拉，已安裝＋「不附」，預設 English）；範圍「介面文字」「系統訊息」（核取，預設都勾；都不勾時「下載」停用：送出一定會被擋）；「只匯出缺的句子」「只匯出原文已更新的句子」（後者在目標語系沒有原文已更新的句子時停用）；說明一行「樣板是 .langpack.json，翻好後從『匯入語系包』匯回；空白的句子代表沒翻，匯入時略過。」；按鈕「下載」「取消」。下載後若檔案大於清單的 `packMaxBytes`，對話框不關，顯示 Q8 的提示；否則關閉並提示「已下載」。400 顯示後端訊息（`errors.file`）；403 顯示既有的權限不足文字。版型 `max-width: 560px`、手機全寬、輸入框都有 `label`。

- [ ] **Step 1: 寫失敗的測試**（mock `dynamicApi`，照 `LocalePackImportDialog.vitest.spec.ts` 的寫法）
  - 從卡片開啟：送出的參數是 `code`＋`scopes=['ui','server']`、`source='zh-TW'`、`reference='en-US'`、兩個篩選 false；勾選後參數跟著變；參考選「不附」送空字串。
  - 「新語系樣板」：多送 `nativeName`、`fallback`、`quasarLang`；代碼格式不對時**照常送出**並顯示後端 400 訊息（判斷：前端不重算格式規則）。
  - 範圍都不勾 → 「下載」停用；目標沒有原文已更新句子 → 該核取停用。
  - 回傳 Blob 大於 `packMaxBytes` → 顯示提示（含兩個 MB 數字）、對話框不關；小於 → 關閉。
  - 下載用 `URL.createObjectURL`、`a.download` 是回應的檔名。
- [ ] **Step 2: 跑測試確認失敗**：`cd frontend && bunx vitest run src/presentation/locale/LocaleTemplateExportDialog.vitest.spec.ts --maxWorkers=2`
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；加跑 `src/i18n/locales.vitest.spec.ts src/css/themes/no-hardcoded-colors.vitest.spec.ts src/i18n/table-no-data-label.vitest.spec.ts src/domain/dynamic/policy-i18n.vitest.spec.ts`；`bun run typecheck`。
- [ ] **Step 5: Commit**（`feat(i18n): 語系翻譯樣板匯出對話框`）

**驗收**：Step 4 全過；`grep -n "#[0-9a-fA-F]\{3,6\}" frontend/src/presentation/locale/LocaleTemplateExportDialog.vue` 無結果。

### Task 5: 語系頁完整版、匯入對話框補強與 #71

**Files:**
- Modify: `frontend/src/pages/system/LocalesPage.vue`（卡片版型、完整度、覆寫檢視、「查看明細」、「匯出樣板」、「新語系樣板」、「以此語系預覽」；判斷 12、13；需要時拆出 `presentation/locale/LocaleCard.vue`）
- Modify: `frontend/src/presentation/locale/LocalePackReport.vue`（`builtForDiffers` 警告文字、確認句數、`orphanedDropped` 提示）、`LocalePackImportDialog.vue`（套用成功後「以此語系預覽」）
- Modify: `shared/i18n/{zh-TW,en-US,th,vi}/ui.json`（`localePacks.card.*`、`localePacks.review.*`、`localePacks.preview.*`、`localePacks.warnings.builtForDiffers`、`localePacks.report.confirmed`、`localePacks.report.orphanedDropped`）
- Test: `frontend/src/pages/system/LocalesPage.vitest.spec.ts`、`frontend/src/presentation/locale/LocalePackImportDialog.vitest.spec.ts`（加案例）

畫面重點（繁中文案；四語都要給）：
- 頁首：「語系」；右側「新語系樣板」「匯入語系包」。說明與 Demo 提示沿用 i4d1。
- 卡片：完整度「介面 1790／2701（66%）」「系統訊息 160／300（53%）」各一條進度條；覆寫句數與目前版本沿用 i4d1；有才顯示：「12 句覆寫的原文已更新（其中 2 句參數已變，目前顯示內建翻譯）」「1 句覆寫已失效（鍵已不存在），下次匯入這個語系時會清掉」；「查看明細」展開：缺最多的分類（「approval 缺 120 句」）、三種鍵清單（前 50 筆，超過註明「共 N 筆，完整清單請匯出樣板」）；目前版的 `builtFor` 有值時顯示「語系包產生時的平台版本 2026.10」。
- 卡片動作：「匯出樣板」（開 Task 4 的對話框）、「以此語系預覽」（未啟用才有；說明「只在這個分頁換介面文字，系統訊息仍是預設語系」以 tooltip 或按鈕下方小字呈現）、「⋯」（沿用 i4d1 三個動作與確認框）。
- 匯入報告：`builtForDiffers`「這份語系包是 {0} 版產生的，目前是 {1}，檔案裡缺 {2} 句」；「確認原文已更新 N 句」；`orphanedDropped` 不是 0 時「這次套用會清掉 N 句已失效的覆寫（上一版仍保留）」。
- 匯入完成：新語系未啟用時「現在啟用」旁加「以此語系預覽」。

- [ ] **Step 1: 寫失敗的測試**
  - `LocalesPage.vitest.spec.ts`：四張內建卡片＋一張匯入的；完整度文字與百分比（四捨五入到整數）；`review` 全 0 時不顯示檢視行、有值時顯示且參數已變的句數在括號內；「查看明細」展開後列出鍵名與「共 N 筆」；未啟用的卡片有「以此語系預覽」、按下呼叫 `switchLocale(…, code, { preview: true })`（mock `src/i18n/ui-messages`）；啟用中的沒有；「匯出樣板」開對話框且目標是該語系；「新語系樣板」開對話框且目標可輸入；既有 i4d1 案例（三個動作、確認框文字、invalidate 三個 key、`importEnabled: false` 停用）照過。
  - `LocalePackImportDialog.vitest.spec.ts`：
    - 報告有 `builtForDiffers` 警告時顯示三個參數；`confirmed`、`orphanedDropped` 的兩行在 0 時不顯示。
    - 套用成功、`enabled: false` → 有「以此語系預覽」；按下先 `refetchQueries(['effective-policies'])` 再 `switchLocale(…, { preview: true })`。
    - **#71-1**：套用送出後關閉對話框、重開並選了另一個檔，舊的套用才回 409（或「現在啟用」才回 400）→ 新對話框不顯示舊錯誤、不出現「重新預覽」。
    - **#71-2**：報告的 `extraKeys` 含 `<img src=x onerror=alert(1)>` 與 `<script>…</script>` → 以文字節點呈現（`wrapper.find('img').exists()` false、`textContent` 含原字串）。
- [ ] **Step 2: 跑測試確認失敗**：`cd frontend && bunx vitest run src/pages/system/LocalesPage.vitest.spec.ts src/presentation/locale --maxWorkers=2`
- [ ] **Step 3: 實作**
- [ ] **Step 4: 跑測試確認通過**：同 Step 2；加跑 Task 4 Step 4 的四個守衛與 `src/i18n/ui-messages.vitest.spec.ts`（試看流程沒被改壞）；`bun run typecheck`。
- [ ] **Step 5: 本機走查**（`Localization__PackImportEnabled=true bash scripts/unattended/local-demo.sh <worktree 絕對路徑>`，背景執行等 READY；`formwright-demo-verify` skill）：admin 登入 → 系統管理 → 語系 → 四張卡片完整度 100% → 匯出 `th` 樣板（只勾缺的：應是空的 `ui`／`server`）→ 「新語系樣板」匯出 `id` → 用該檔匯入 → 報告新安裝、全部略過 → 套用 → 「以此語系預覽」（介面沿退回鏈顯示 English）→ 結束試看；匯入 `ja-partial` → 卡片完整度與缺最多的分類 → 用測試夾具（或 `sqlite3` 直接改本機 Demo 資料庫的 `BaseHashesJson` 與 `UiJson`，只在本機）造原文已更新 2 句、已失效 1 句 → 卡片顯示兩行提示與明細 → 匯出「只匯出原文已更新」→ 原樣匯回 → 報告「確認原文已更新 2 句」「會清掉 1 句已失效」→ 套用後提示消失 → 回到上一版提示回來。390、820、1366、1920 四種寬度截圖（放專案 `.tmp/`），沒有橫向捲動（`document.documentElement.scrollWidth <= innerWidth`）。再用預設的 `local-demo.sh`（Demo 模式）重跑：匯出可用、匯入報告可看、「套用」停用；到政策停用 ไทย → 卡片出現「以此語系預覽」。
- [ ] **Step 6: Commit**（`feat(i18n): 語系頁完整版（卡片、完整度、覆寫檢視、以此語系預覽）`）；`#71` 的兩個測試另一個 commit（`test(i18n): 匯入對話框補關閉重開與多餘鍵純文字的回歸測試（#71）`）。超過 700 行就把頁面與對話框分兩個 commit。

**驗收**：Step 4 全過；Step 5 逐項記錄（含截圖路徑）；`grep -n "#[0-9a-fA-F]\{3,6\}" frontend/src/pages/system/LocalesPage.vue frontend/src/presentation/locale/*.vue` 無結果。

### Task 6: 文件、手冊、票、gate

**Files:**
- Modify: `docs/superpowers/specs/2026-11-17-language-packs-design.md`：
  - §5.2：`builtFor` 那條補「i4d2 起：樣板寫建置版本號；兩邊都不是 dev 且不同時提示，附檔案缺的句數（Q1）」，刪「延到 i4d2」。
  - §5.1：補「樣板另有 `source`、`reference`、`review` 三段，匯入時略過（Q2）」。
  - §6.5：補「原文已更新的句子原樣再匯入即確認（Q3）」「已失效的覆寫在下一次匯入同語系時清掉，報告預告（Q4）」「參數已變另外標出」「卡片列句數，展開列鍵名前 50 筆與缺最多的 5 個分類（Q5）」；刪「i4d1 的最小語系頁」那段裡「留給 i4d2」的字樣。
  - §7.1：補「新代碼的樣板入口在頁首（Q6）」「樣板大於 A38 照樣下載並提示（Q8）」。
  - §14：本計畫「寫死清單」。§15：「i4d1 交付時的已知限制」裡延到 i4d2 的兩條改成「i4d2 已補」；加「i4d2 交付時的已知限制」（下面「已知限制」節）；版本號那句照「與設計的矛盾」第 1 點改。
  - §20：列表加「**i4d2 計畫**：[i4d2 計畫](../plans/2026-11-17-language-packs-i4d2.md)」（規劃者已在同一 commit 加過就只補「已上線」）。
  - Q 若被使用者推翻，照裁決改；標明使用者裁決或主控暫定。
- Modify: `docs/manual.md`「匯入語系包（系統管理 → 語系）」一節：卡片內容（完整度、原文已更新、參數已變、已失效、明細）、「匯出樣板」與「新語系樣板」（選項、`source`／`reference` 段的用途、只匯出缺的與原文已更新的、超過大小上限的提示）、「以此語系預覽」（只在這個分頁、系統訊息仍是預設語系）、報告新增的三行（產生時的平台版本與缺幾句、確認原文已更新、會清掉的已失效覆寫）；刪掉「只顯示、不比對」的說法；Demo 站可以匯出、預覽報告、不能套用。文案照畫面實際文字。
- Modify: `docs/roadmap.md` i4 列（「i4d2 已上線 <sha>」在合併後由主控補）；`docs/feature.md` 的 i4 項目補「i4d2 已實作（語系頁完整版、JSON 樣板匯出、以此語系預覽）」，**不改核取狀態**；跑 lifecycle 的 `feature_checklist.py docs/feature.md --write`（找不到腳本就照 `.claude/rules/docs.md` 回報「統計行待重算」）。
- 票：#71 合併後關票留 commit SHA；#70 若本段 CI 出現逾時，把診斷訊息貼上去（不關）；新待辦用 `tickets:ticket-create` 開 backlog 票。

- [ ] **Step 1: 整段驗收 grep**（下面 S1–S5）全符合。
- [ ] **Step 2: 文件回填**（上列）。
- [ ] **Step 3: gate**：推分支前外洩掃描；`bash scripts/unattended/ci-wait.sh <sha>`（含 PostgreSQL 兩步），`exit=0`。
- [ ] **Step 4: `formwright-purpose-check`**（重點：樣板大小提示讀 A38、不另存常數；完整度與檢視的單一來源是後端；空系統可匯出；權限 fail closed；沒有 migration）。
- [ ] **Step 5: Commit**（`docs(i18n): i4d2 完成，設計、手冊與功能清單回填`），之後照 `formwright-ship` fast-forward 推 main。

**驗收**：S1–S5 全符合；gate `exit=0`；宗旨檢查無違反。Demo 資料：**種子不改**（Q7）；交付說明寫 Demo 可示範的路徑（匯出 → 匯入預覽 → 套用被擋；停用 ไทย 後以此語系預覽），以及只在本機示範的「原文已更新」「已失效」「確認」。

---

## 整段驗收

設計 §20 i4d2 列逐項：

| # | 項目 | Task | 驗證 |
|---|---|---|---|
| 1 | 語系頁完整版（四種寬度） | 5 | `LocalesPage.vitest.spec.ts`、走查四種寬度 |
| 2 | JSON 匯出樣板 | 3、4 | `LocaleTemplateBuilderTests`、`Export_ThenImport_RoundTrip`、`LocaleTemplateExportDialog.vitest.spec.ts` |
| 3 | 完整度 | 1、2 | `LocaleCompletenessTests`、`List_*_Completeness` |
| 4 | 原文已更新（含參數已變的後端訊息） | 1、2 | `LocaleOverlayReviewTests`、`List_Review_BaseChangedAndOrphaned`、`Apply_Confirm_RefreshesHash` |
| 5 | 覆寫已失效 | 1、2 | `LocaleOverlayReviewTests`、`Preview_OrphanedDropped`、`Apply_Orphaned_Dropped` |
| 6 | 以此語系預覽 | 5 | `LocalesPage`、`LocalePackImportDialog` 的試看案例、走查 |
| 7 | `builtFor` 比對（i4d1 延期） | 1 | `BuiltFor_*`、`Preview_BuiltFor_PlatformVersion` |
| 8 | 手冊 | 6 | Step 2 |
| 9 | Demo 可預覽、可匯出、不能套用 | 3、5 | `Export_DemoOff_Allowed`、Demo 模式走查 |

```bash
# S1 沒有 migration
git diff --name-only main...HEAD -- 'backend/src/*/Migrations/*'   # 無結果
# S2 新的請求都要求系統管理員；匯出不是交易型
grep -rLn "ISystemAdminRequest" backend/src/DynamicModules.Application/Dynamic/LocaleService/Queries/GetLocaleTemplate/*Request.cs   # 無結果
grep -n "ITransactionalRequest" backend/src/DynamicModules.Application/Dynamic/LocaleService/Queries/GetLocaleTemplate/*.cs        # 無結果
# S3 上限不寫死（大小讀 A38；暫存與清單筆數只在 LocalePackLimits）
grep -rn "5242880\|20971520\|Take(50)\|Take(5)" backend/src/DynamicModules.Application/Dynamic/LocaleService frontend/src/pages/system/LocalesPage.vue frontend/src/presentation/locale   # 無結果
# S4 既有範本基準沒被改
git diff --stat main...HEAD -- backend/tests/DynamicModules.ApplicationTests/Common/ServerTextBaseline/message-report.json backend/tests/DynamicModules.ApplicationTests/Common/ServerTextBaseline/task-report.json backend/tests/DynamicModules.ApplicationTests/Common/ServerTextBaseline/alert.json backend/tests/DynamicModules.ApplicationTests/Common/ServerTextBaseline/locale-pack.json   # 無輸出
# S5 前端沒有 v-html、html: true 的 Notify（設計 §13.3）；"dev" 字面值只在一處
grep -rn "v-html\|html: true" frontend/src   # 無結果
grep -rn '"dev"' backend/src --include=*.cs | grep -v Migrations   # 只有 IOpsEnvironment.DevVersion 一筆
```

## 預估規模

| 批 | Task | 新檔 | 改檔 | 手寫行數（估，含測試） |
|---|---|---|---|---|
| i4d2-1 | 1 `builtFor`、確認、已失效報告（含抽出共用類別） | 5（含 2 個測試檔） | 9 | ≈ 680（測試約 380） |
| | 2 清單帶完整度與檢視 | 0 | 3 | ≈ 230（測試約 140） |
| | 3 樣板匯出 API | 5（含 2 個測試檔、1 個基準檔） | 5 | ≈ 590（測試約 330；另有約 15 行四語訊息） |
| | 小計 | 10 | 17 | ≈ 1500 |
| i4d2-2 | 4 型別、API、匯出對話框 | 2 | 6 | ≈ 420（測試約 160；另有約 60 行四語文案） |
| | 5 語系頁完整版、匯入對話框、#71 | 0–1 | 8 | ≈ 830（測試約 330；另有約 190 行四語文案） |
| | 6 文件與收尾 | 0 | 5 | ≈ 200 |
| | 小計 | 2–3 | 19 | ≈ 1450 |
| 合計 | | 約 13 | 約 36 | ≈ 2950，其中測試約 47% |

兩批都在 1300–1700 行之間。測試用的語系包檔與基準檔不計入。Task 1、Task 5 超過 700 行就照各自 Step 5／Step 6 的說明拆 commit。

## 已知限制（i4d2，交付時寫回設計 §15）

- 沒有發版流程時平台版本是 `dev`，`builtFor` 不比對（Q1）；Demo 與自行建置都是這樣。
- 原文已更新的句子原樣再匯入即確認；拿一份很舊的完整檔匯入也會順便確認（報告列確認幾句，Q3）。
- 已失效的覆寫在下一次匯入同語系時清掉；回到上一版會把它們帶回來，直到再一次匯入（Q4）。
- 「原文已更新」只看 zh-TW 原文；只改了英文內建翻譯、繁中沒變時不提示（基準語系是繁中，設計 §6.4）。
- 語系頁的明細列鍵名不列句子內容；看原文與譯文要匯出樣板（Q5）。
- 試看時系統訊息仍是預設語系（設計 §15 已列）。
- JSON 樣板的 `source`、`reference`、`review` 段可能被翻譯工具當成要翻的內容（匯入時略過，不影響結果，Q2）。
- Demo 站看不到「原文已更新」「已失效」「回到上一版」（不能套用，種子不改，Q7）。
- 樣板不受 A38 限制，超過時只提示，匯回要先調高上限（Q8）。
- `designData` 的匯出與「原文已改」（設計 §10.4）仍在 i4e。

## 宗旨檢查（formwright-purpose-check）

1. **機制還是政策**：通過。完整度、原文已更新、已失效、樣板產生是平台機制；本段沒有新的業務政策，樣板大小提示讀 A38；分類取前 5、清單前 50 是技術限制，列在寫死清單。
2. **寫死掃描**：計畫階段不適用；Task 6 Step 4 對 diff 跑 skill 的 grep，預期命中 `MaxMissingCategories`（技術限制，已列）。
3. **單一來源**：通過。完整度的定義從分析器抽成 `LocaleCompleteness`，報告與清單共用；原文雜湊只有 `LocalePackVersions.BaseHash` 一份；`dev` 字面值只有 `IOpsEnvironment.DevVersion`；大小上限只有 A38，前端從清單 API 取；完整度與檢視由後端算好，前端不重算。
4. **兩個應用程式**：通過。語系與翻譯樣板是全平台能力，CRM、進銷存、MES 的使用者都用同一份介面文字。
5. **空系統**：通過。沒開任何應用程式時照樣能看完整度、匯出樣板、試看（內容只有平台的介面文字與系統訊息）。
6. **套件只用定義組成**：不適用（本段不碰應用程式套件）。
7. **ERP 對照**：不適用於本段（語系包是平台能力，設計 §16 已對照）；沒有新增主檔或單據。
8. **權限 fail closed、驗證不只在前端、沒有執行期 DDL**：通過。新端點是 `ISystemAdminRequest`，由 `SystemAdminBehavior` 擋；匯出選項的驗證在後端，前端只擋「範圍都不勾」這種一定會被擋的情況；沒有 migration、沒有 DDL。
9. **不限產業**：通過。樣板與完整度對服務業、貿易商、批量工廠的語系需求相同；例子用的是移工語言與外包翻譯，不假設產業。

## 自我檢查

1. **設計覆蓋（§20 i4d2 列）**：見「整段驗收」表 9 項；§5.2 缺鍵「哪幾個分類缺最多」→ 判斷 4；§6.3 第 4 步 → Task 5；§6.5 → Task 1、2、5；§7.1 JSON → Task 3、4（Excel 是 i4e）；§12 Demo 列 → Task 3、5；§13.3 → `Export_OverlayWithScript_IsData`、S5；§15 延到 i4d2 的兩條 → 判斷 2、3；§19「升級後原文已更新計數正確」→ `LocaleOverlayReviewTests`、`List_Review_*`。
2. **i4d1 留給本段的**：`builtFor` 比對與版本來源 → Task 1；「原文已更新」列出參數改變的後端訊息 → 判斷 3、Task 2、5；Demo 訪客能拿到可上傳預覽的檔（i4d1 Q10）→ Task 3、走查；390 寬卡片（i4d1 Q8）→ Task 5。
3. **型別一致**：`IOpsEnvironment.DevVersion`、`LocalePackAnalyzer.Analyze(ParsedLocalePack, LocaleWorld, int, string?, OverlayReview?)`、`LocalePackPlan.{UiConfirm,ServerConfirm}`、`SectionReport.Confirmed`、`LocalePackReport.OrphanedDropped`、`LocalePackVersions.BaseHash`、`LocaleOverlayReview.{Of,OrphanedTexts}`、`OverlayReview`、`OverlayReviewSection`、`LocaleCompleteness.{Of,TopMissing}`、`MissingCategory`、`LocaleWorldText.Resolve`、`LocaleTemplateBuilder.Build`、`LocaleTemplateOptions`、`LocaleTemplateFile`、`GetLocaleTemplateRequest`、`LocaleAdminItem.{Completeness,Review,BuiltFor}`、`LocaleAdminCompleteness`、`LocalePackLimits.MaxMissingCategories`、`LocalePackMessages.{LocaleTemplateLocaleNotInstalled,LocaleTemplateScopeInvalid}`、前端 `dynamicApi.exportLocaleTemplate`、`LocaleTemplateExportDialog`。
4. **前段審查踩過的坑**：PAX23 盤點（判斷 1）；明示 null（`builtFor`）；新案例進 PostgreSQL 雙跑集；寫入後等快照（本段新增的寫入測試沿用 `LocaleTestKit`，#70 的處置見「票」節）；前端 i18n 鍵四語同步並跑守衛；local-demo.sh 給絕對路徑；既有基準檔凍結、新鍵另開基準檔。

## 使用者裁決（沿用，不再問）

- 2026-11-17：U1、U2、U5、U7、U9（JSON 與 Excel 都提供；本段只做 JSON）、U10；T4–T7、T15、T16、T18 照設計。
- 2026-11-18：M2（管理員試看，本段只加捷徑）。
- 2026-11-20：業務政策不寫死、權限 fail closed、驗證不只做在前端；清單在手機寬度改卡片、操作鈕放卡片下方；前端只在送出一定會被擋時先擋；系統自記的時間照營運時區並標示時區。
- i4d1 的 Q1–Q12 為主控暫定、待使用者確認；本段沿用其結論（特別是 Q3 內建語系的 `locale` 段不改、Q5 移除覆寫可復原、Q6 與內建相同不存），若被推翻再回頭調整樣板的 `locale` 段與「與內建相同」的計數。
