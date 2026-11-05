# 第 3 階段實作裁決（R1–R19）

執行 [`2026-11-03-phase3-permissions.md`](2026-11-03-phase3-permissions.md) 時，對計畫原文的補充與修正。格式：裁決 — 理由 — 錯了的代價。與計畫衝突時以這裡為準；第 1、2 階段裁決（[`2026-11-03-phase1-rulings.md`](2026-11-03-phase1-rulings.md)、[`2026-11-03-phase2-rulings.md`](2026-11-03-phase2-rulings.md)）仍然有效。

- Ruling R1: Task 2 另外產出 `IPermissionRepository.LoadProgramInputsAsync(userId, programCode, ct) → Task<PermissionInputs?>`，只填該程式的 GroupPrivileges／UserPrivilege，其餘清單為空，供 `ResolveProgram`／`DesignerActionsAsync` 使用 — `LoadInputsAsync` 需要 Module，而 `DYN00000` 沒有 Module — 錯了的代價：之後多收斂一個 repo 方法。
- Ruling R2: Task 3 的測試輔助保留建立「不帶 X-User」client 的方式（如 `CreateClient(user: null)`），Task 7 沿用 — 否則匿名測試無法表達「沒有 header」 — 錯了的代價：小到可忽略。
- Ruling R3: `permissions-queries.ts` 由 Task 8 建立，Task 10 追加 — 循序任務不能都建立同一個檔 — 錯了的代價：無。
- Ruling R4: Task 8 只加 `Me`、`User` 型別；`ModulePermissions`／`Privileges`／`DataScope`／`Group` 由 Task 10 加 — 與各任務的 Interfaces 區塊一致 — 錯了的代價：無。
- Ruling R5: `Forbidden_MapsTo403` 放進既有的 `WebAPITests/ServiceResultMappingTests.cs`；`ServiceResult.Forbidden()` 旗標的測試若已有 `ServiceResultTests` 就放 ApplicationTests；Task 3 更新 `HeaderCurrentUserTests`，移除 Anonymous 的預期 — 狀態碼映射屬於 WebAPI 層的職責 — 錯了的代價：無。
- Ruling R6: Task 12 驗收情境中，`floor` 給 `Read`／`Add`／`Edit`（範圍 `Own`，「說明」唯讀）；第 8 步改為：floor01 建一筆記錄，admin 把「說明」設成原值，floor01 修改時送不同的「說明」，回 200 且存的仍是原值；步驟 6 的 manager01 要送預估費用；步驟 7 的 4 筆發生在 floor01 建記錄之前 — 要驗證「唯讀欄位的輸入直接忽略」風險，必須有 `Edit` — 錯了的代價：驗收情境與計畫原文略有不同。
- Ruling R7: 首次升級授權只在「建立權限群組的那一次 seed」執行（執行前沒有任何 `PermissionGroup` 列），取代「授權表為空」的判斷 — 「授權表為空」會在兩種情況 fail-open；新判斷不需要新表，也符合決策 13 的意圖：升級一次既有的第 2 階段資料，新模組 fail-closed — 錯了的代價：群組已 seed 但授權在升級中途被清空的資料庫不會重新授權，需 admin 從 UI 補。
- Ruling R8: 切換身分時必須重設快取的伺服器狀態（`queryClient.clear()` 或 `resetQueries`，在呼叫 `setUser` 的地方），選單、schema、記錄、`me` 才不會顯示上一位使用者的資料 — 計畫只讓 `me` 以 userId 為 key，但現在每個查詢結果都依使用者而異 — 錯了的代價：每次切身分多一波重抓。
- Ruling R9: 篩選裡引用隱藏欄位時，在型別、運算子、可篩選的驗證之前就回 `欄位 {id} 不存在或未啟用` — 決策 7 的「不洩漏欄位存在」優先於計畫的步驟順序；否則錯誤訊息會透露隱藏欄位存在 — 錯了的代價：parser 多改一處。
- Ruling R10: 標題與關鍵字搜尋（`q`）不另外擋隱藏欄位（`Pricing` 不會隱藏標題，因為標題是 `text`）；另在設計器兩處擋下隱藏的標題欄位：`SaveModulePermissions` 拒絕把目前的標題欄位設為隱藏（`標題欄位不能設為隱藏`），`UpdateModule` 拒絕把標題換成已有隱藏例外的欄位（見 R17） — 原先的前提「唯一的繞法是直接改資料庫」不成立：先把欄位 X 對 sales 設為隱藏、再把標題改成 X，就能在正常操作下把 X 的值寫進 `Record.Title`，最終審查發現後改為驗證加防禦性遮蔽雙層處理 — 錯了的代價：隱藏欄位的值經 `title` 與 `q` 外露。
- Ruling R11: `ToResponse` 改為允許清單（只輸出模組中 `CanSee` 的欄位鍵） — 原本載入時沒帶子項的 Module 會變成輸出全部值（fail-open） — 錯了的代價：已不在模組內的欄位鍵不會再出現在回應，但那些欄位本來就無法顯示。
- Ruling R12: 刪除 `EffectivePermissionsFactory.AdminFor` — 計畫假設測試或 benchmark 會直接呼叫 `Process`，實際沒有任何呼叫端，是 dead code — 錯了的代價：日後補回 5 行 helper。
- Ruling R13: 標題欄位對使用者不可見時，schema 的 `TitleFieldKey` 回 null，並補測試 — Task 7 雖禁止隱藏標題，但 `Pricing` 或直接改 DB 仍可能讓它不可見，防禦性處理 — 錯了的代價：這類使用者的畫面不顯示標題。
- Ruling R14: 修改**共用**檢視時，若已存的 `columns`／`filter`／`sort` 引用了編輯者看不到的欄位，回 403 且不寫入；個人檢視維持存檔時剔除（只損失擁有者自己的失效引用） — 避免在不合併篩選樹的前提下，默默弄丟其他人看得到的設定 — 錯了的代價：看不到該欄位的設計者改不了那個共用檢視（admin 可以）。
- Ruling R15: 前端 store 的使用者清單改存 `{userId, displayName}[]`，身分下拉與 `user` 欄位、篩選的選項都顯示 `displayName` — `/users` 已提供，原本是 spec 部分遺漏，也順手修正 `field-type-registry.ts:216` 的過時註解 — 錯了的代價：3 個呼叫點的型別小改。
- Ruling R16: 標題欄位若已有隱藏例外，設計器仍顯示「隱藏」選項，讓設計者看得到並改掉（後端拒絕儲存隱藏標題） — 否則 `q-select` 會顯示原始的 `hidden` — 錯了的代價：無。
- Ruling R17: I1 兩層都修：`UpdateModule` 把標題換成有任何 `Hidden` 例外的欄位時回 `TitleFieldId` 400（`記錄標題欄位不能是有隱藏例外的欄位`）；防禦層在標題欄位對使用者不可見時，記錄回應的 `title` 輸出空字串、分頁列表忽略 `q`；R10 的理由同步改寫 — 只擋寫入端擋不住已存在的資料或直接改 DB，只做防禦層則設計者會誤以為設定生效 — 錯了的代價：隱藏標題的值經 `title` 或 `q` 逐字猜出。
- Ruling R18: 最終審查 M1–M5 在最後一波修完：`DesignerAccessBehavior` 遇到非 `ServiceResult` 的設計器請求丟 `InvalidOperationException`（M1）；`ViewMenu` 在他人共用檢視上，更新、設為預設、刪除只給有 `designer.edit` 的人（M2）；`MainLayout` 沒有 `designer.read` 時連「系統管理」標題與分隔線一起隱藏（M3）；驗收第 8 步補斷言說明欄位在新增時被忽略（M4）；`SeedData` 把 `OwnerUserId == "anonymous"` 的個人檢視改歸 `admin`（M5） — 都是幾行的小修，留著會讓文件宣稱與實際不符或留下孤兒列 — 錯了的代價：小。
- Ruling R19: 上線前的四項待辦只記進 spec §15「上線前必做（POC 未做）」，POC 不實作：擁有者 SQL 參數改子查詢、schema 加 `Cache-Control: private` 與 `Vary`、決定是否拆開設計與授權兩條軸（如 `DYN00001`）、有效權限改用 `ICache` 並在權限異動時清除 — POC 沒有共用 proxy、資料量小、身分是刻意可切換的，做了也驗不到 — 錯了的代價：上線時才發現，改動範圍在 Persistence 與 WebAPI。
