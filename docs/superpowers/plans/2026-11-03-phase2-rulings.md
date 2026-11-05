# 第 2 階段實作裁決（R1–R12）

執行 [`2026-11-03-phase2-list.md`](2026-11-03-phase2-list.md) 時，對計畫原文的補充與修正。格式：裁決 — 理由 — 錯了的代價。與計畫衝突時以這裡為準；第 1 階段裁決（[`2026-11-03-phase1-rulings.md`](2026-11-03-phase1-rulings.md)）仍然有效。

- Ruling R1: enum／multienum 篩選比對 `TextValue`（`opt_<id>`）；`NumberValue` 存選項 id，只供排序 join `Dynamic_FieldOption.Sequence` — 與記錄值格式一致、停用選項仍能比對 — 錯了是改一個比較欄。
- Ruling R2: `ConditionUsage` 只有 `ListFilter` — 其他用途屬後續階段，YAGNI — 錯了是加 enum 值。
- Ruling R3: `dotnet ef migrations add` 一律帶 `--startup-project poc/backend/src/DynamicModules.Persistence` — WebAPI 當 startup 會失敗 — 錯了是改指令。
- Ruling R4: SetFieldStatus 由停用改為啟用且 ShouldIndex 為真時，用共用的 `RecordIndexBackfill.RebuildAsync` 回填該欄 — 停用期間改過的記錄會缺索引列 — 錯了是多一次回填。
- Ruling R5: `ConditionParser.Parse(JsonElement, Module, FieldTypeRegistry, ConditionUsage, IReadOnlySet<string> userIds)`，`userIds` 必填，呼叫端傳 `await modules.ListUserIdsAsync(ct)` — 可選參數會讓 user 條件默默不驗 — 錯了是改簽章。
- Ruling R6: `ViewResponse.IsOwner` 對個人檢視是 `OwnerUserId == 我`，共用檢視是 `CreatedUserId == 我`；`Filter` 為 null 時 JSON 省略 — 計畫未定義共用檢視的擁有者 — 錯了是改一個判斷。
- Ruling R7／R7a: 文字型別 in／notIn 用單一 textarea，逗號或換行分隔，trim、去空、去重，最多 50 個，以陣列送出 — 計畫未定義多值輸入 — 錯了是換輸入元件。
- Ruling R8: 每個欄位條件產生非相關子查詢 `r.Id IN (SELECT i.RecordId FROM Dynamic_RecordIndex i WHERE i.FieldId = @f …)`，否定形式用 `NOT IN`，取代計畫的「每個條件一個 EXISTS」 — 相關 EXISTS 在 1 萬筆上 15–21 秒（SQLite 無統計資訊時選錯索引）；不用 SQLite 專屬的 planner 提示，SQL Server 也通用；`RecordId` 不可為 null，`NOT IN` 沒有 NULL 陷阱 — 錯了是改 builder 一處。
- Ruling R9: 列表篩選的值下拉列出已停用的選項；新增／編輯表單照舊隱藏 — 既有記錄可能仍存著停用的值，篩選要找得到 — 錯了是 FilterPanel 加一道過濾。
- Ruling R10: 索引 `TextValue` 上限由 450 改 440（含 FieldId 共 888 bytes） — spec §6 以 SQL Server 2008 為目標，900-byte 索引鍵上限；趁第 2 階段 migration 還新時改 — 錯了是 440 字以後的部分不參與比對與排序。
- Ruling R11: 最終修正範圍為 I1（設計器改欄位後套用快取舊檢視）、身分切換重設、多選運算子文字、標題欄表頭排序、檢視名稱大小寫、R10、文字清單截斷提示、builder helper 改名與註解、spec §15 上線注意事項；其餘審查小項延後 — 一輪修正、範圍可審 — 錯了是後續階段再修。
- Ruling R12: 身分切換時 `router-view` 以 userId 為 key 整頁重新掛載 — 身分切換器是 POC 開發工具，最小改動 — 錯了是切換時遺失設計器未存的對話框內容。

## 延後項目

最終審查與再審認為不影響合併、留給後續階段：

- views 重抓失敗且快取仍在時，ListPage 會用快取的舊檢視初始化，列表回 400 一次（`ListPage.vue:75-76`）；需要 views API 本身失敗才會發生。
- 欄位設定改變後（如長度上限、小數位數調小），已存檢視裡的條件值失效，列表回 400。
- 檢視的欄位全部失效時「設為預設」送出 `columns: []` 被擋。
- 前端自組 `f_${id}` 當 `sortBy`，依賴後端的 key 命名不變式。
- `VSTestTestCaseFilter` 只在 `dotnet test` 不帶其他 `--filter` 時排除 Benchmark；IDE 會照跑。
- 泰文、越南文的新文字為英文佔位，設計器區塊在第 1 階段就缺（由 fallback zh-TW 補）。
- 各任務審查的其他小項，詳見各任務審查紀錄（未入庫）。
