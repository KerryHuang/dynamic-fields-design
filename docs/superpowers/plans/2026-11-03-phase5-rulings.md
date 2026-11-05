# 第 5 階段實作裁決（R1–R10）

執行 [`2026-11-03-phase5-relations.md`](2026-11-03-phase5-relations.md) 時，對計畫原文的補充與修正。格式：裁決 — 理由 — 錯了的代價。與計畫衝突時以這裡為準；第 1、2、3、4 階段裁決（[`2026-11-03-phase1-rulings.md`](2026-11-03-phase1-rulings.md)、[`2026-11-03-phase2-rulings.md`](2026-11-03-phase2-rulings.md)、[`2026-11-03-phase3-rulings.md`](2026-11-03-phase3-rulings.md)、[`2026-11-03-phase4-rulings.md`](2026-11-03-phase4-rulings.md)）仍然有效。

- Ruling R1: 刪除被擋時回傳的 `BlockingRef.Title`，只有在使用者對來源模組有 `Read`、來源記錄在資料範圍內、標題欄位對使用者可見時才填，否則為 null — `ReferenceResolver` 是依單一目標查詢，不適用這裡 — 錯了的代價：小幅重構。
- Ruling R2: `SchemaReference.LightUrl` 是相對於 API 基底 `/api/v3/dynamic` 的路徑（如 `/modules/x/records/light`），前端自己加上 API 基底 — 後端不必知道部署的完整網址 — 錯了的代價：改一個字串。
- Ruling R3: `Module.Kind` 在資料庫存 PascalCase（`Standalone`／`Extension`），與既有的 `ModuleStatus` 一致，JSON 仍是 camelCase — 與現有列舉的儲存方式一致，比計畫寫的小寫更重要 — 錯了的代價：一個 converter 加一個 migration。
- Ruling R4: 前端的 `field-type-registry` 測試（`filter-operators.json` 的鍵必須對上前端型別）從 Task 2 起是紅的，到 Task 8 把 `reference` 加進前端註冊表才轉綠 — 任務順序是後端先行；Task 8 的派工文字要特別點名這支測試 — 錯了的代價：中間幾個 commit 有一支前端測試是紅的。
- Ruling R5: `ListVisibleIdsAsync` 的資料範圍參數型別是 `IReadOnlyCollection<string>? ownerUserIds`，不是計畫寫的 `long` — 這個程式碼庫的使用者代號是字串，計畫筆誤 — 錯了的代價：無。
- Ruling R6: commit `100eb67` 的 trailer 寫的是 Claude Sonnet 5.5（實際作者模型），維持原樣、不改寫歷史 — 改寫歷史的風險大於這個差異 — 錯了的代價：只是外觀。
- Ruling R7: 伺服器的 JSON 設定是 `WhenWritingNull`，null 屬性不會輸出（如 `BlockingRef.title`、`ReferenceInfo.title`）；前端型別把它們當選填（`title?: string | null`），undefined 視同 null；Task 8、Task 11 的派工文字要帶這條 — 否則前端會把「沒有這個屬性」當成錯誤 — 錯了的代價：前端顯示微調。
- Ruling R8: 刪除規劃器要把 Blocking 裡「同一次刪除也會連帶刪除」的來源濾掉，因為它們的參照會跟著消失 — 這符合「剩下的參照全有或全無」的原意 — 錯了的代價：多一行篩選。
- Ruling R9: Task 11 的派工文字寫「409 行為維持不變」是錯的；正確行為是：連帶刪除超過 5 層的 409 要顯示伺服器訊息，只有 RowVersion 過期的 409 才通知並重新載入 — 這是審查者提出的重要問題，錯在控制者的措辭 — 錯了的代價：無（已更正）。
- Ruling R10: Task 12 拆成兩半：實作者做 Step 1–2（`Phase5AcceptanceTests` 與完整測試）與 Step 4 的文件（`phase5.md` 的實機走查除外）；控制者在最終審查後自己做瀏覽器走查（Step 3），再補上該段 — 瀏覽器工作階段是共用的，走查需要控制者判斷，和第 4 階段一樣 — 錯了的代價：無。
