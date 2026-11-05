# 第 4 階段實作裁決（R1–R16）

執行 [`2026-11-03-phase4-rules.md`](2026-11-03-phase4-rules.md) 時，對計畫原文的補充與修正。格式：裁決 — 理由 — 錯了的代價。與計畫衝突時以這裡為準；第 1、2、3 階段裁決（[`2026-11-03-phase1-rulings.md`](2026-11-03-phase1-rulings.md)、[`2026-11-03-phase2-rulings.md`](2026-11-03-phase2-rulings.md)、[`2026-11-03-phase3-rulings.md`](2026-11-03-phase3-rulings.md)）仍然有效。

M2、M3 兩條在最後一波修正才實作於後端（前端 Task 10 已照這兩條的語意對齊），現已實作於後端（commit `bb44db3`）；其餘裁決在驗收時已落實。

- Ruling R1: Task 1 另外修改 `Domain/Conditions/ConditionParser.cs`，改呼叫 `handler.OperatorsFor(field)`，不再用 `handler.Operators` — 列表篩選公式日期欄位需要日期運算子；計畫的 Interfaces 文字要求這樣改，只是 Files 清單漏列 — 錯了的代價：無。
- Ruling R2: `poc/shared/rule-cases.json` 裡欄位的設定放在 `"config"` 底下（如 `"config": {"required": true}`），與 `SchemaFieldLike.config` 一致 — 前後端讀同一種形狀 — 錯了的代價：無。
- Ruling R3: commit 的 co-author 結尾依各 agent 自己的 harness 歸屬行（subagent 用 Sonnet 5.5） — 這才是準確的作者；約束檔已更新 — 錯了的代價：無。
- Ruling R4: 結果型別與引用欄位不符的錯誤，鍵用 `Config.ResultType`（計畫沒指定鍵） — 錯誤顯示在出錯的下拉旁邊；Task 8 要在結果型別下拉底下對應 `Config.ResultType` — 錯了的代價：多一個錯誤鍵對應。
- Ruling R5: 自動編號併發（Task 4 審查 I2）POC 不修，維持計畫指定的「讀取後在呼叫端交易內更新」 — SQLite 以 `BEGIN IMMEDIATE` 序列化寫入，這裡不會重號；改在 spec §15 第 23 條記風險：SQL Server 必須用 `UPDLOCK` 或原子 `UPDATE … SET NextValue = NextValue + 1` 再讀回，並處理第一次插入的主鍵衝突 — 錯了的代價：忽略該風險就併入的話，會產生重複的業務編號。
- Ruling R6: 自動編號格式已存但無法解析時（Task 4 審查 I1），建立記錄丟 `InvalidOperationException` — 設計器驗證保證不會發生；資料損毀應該大聲失敗，不是靜默存成沒有編號 — 錯了的代價：損毀的欄位會讓建立記錄回 500，直到修好為止。
- Ruling R7: `OptionFilter` 的 `optionIds` 為空仍然允許（條件成立 ⇒ 沒有任何選項可選） — 與「取交集」語意一致；設計器 UI（Task 11）仍要求至少勾一個 — 錯了的代價：API 呼叫者可建立擋掉所有選項的規則。
- Ruling R8: 計畫衝突（Task 6）：Brief 步驟 3 寫「只檢查與已存值不同的選項」，Review Focus 3 與測試 `Update_OptionFilterParentChanged_StaleChildRejected` 要求父欄位變動後子欄位要重驗。採實作者的規則：目標欄位在目前值下的可選集合，若與在已存值下的集合不同，就整個重驗目前值；相同則只檢查有變動或新增的選項 — 下拉連動保持一致才是 spec 的意圖，優先於步驟字面 — 錯了的代價：父欄位變動後，沒動的過期子值會擋住存檔（這是預期行為）。
- Ruling R9: 共用語意（凍結，供 Task 9、10 對齊）：`RuleOutcome.required` = `config.required` ∪ 成立的 Required 規則 − 被規則隱藏的欄位（不因唯讀而減少，可寫性由呼叫端套用）；ReadOnly 只用已存值求值（無已存值一律不成立，空條件也一樣）；不相交的 OptionFilter 交集 ⇒ 該鍵對應 `[]`；停用或無法解析的規則略過；文字比較用 ordinal；datetime 的相對日期取營運時區（+08:00）的日期部分；案例檔有額外的頂層 `"users"`，以及每個結果可覆寫的 fields／blocks — 錯了的代價：前端必須完全照做，否則對照測試會失敗（這正是對照測試的目的）。
- Ruling R10: （M2）選項範圍檢查略過使用者無法寫入的欄位（權限、`Config`、規則造成的唯讀） — 與決策 8 的必填略過一致；使用者改不了的值，不該因此被擋；最後一波修正已實作於後端（commit `bb44db3`），Task 10 前端同步對齊 — 錯了的代價：唯讀的子欄位可能保留超出範圍的值。
- Ruling R11: （M3）驗證錯誤的鍵取「使用者看得到、且沒被規則隱藏的第一個目標欄位」，否則 `_record` — 錯誤必須能顯示出來；驗證本身照常執行；最後一波修正已實作於後端（commit `bb44db3`），Task 10 前端同步對齊 — 錯了的代價：無。
- Ruling R12: （M4 推論管道）規則引用權限隱藏欄位時，POC 接受這個風險，只在 spec §15 第 24 條記錄：設計者不可在某些群組看不到的欄位上寫給使用者看的規則；之後可考慮儲存時依群組拒絕 — POC 沒有真正的不信任使用者 — 錯了的代價：有心人可反覆存檔，推測隱藏欄位的值。
- Ruling R13: 規則引擎引用 `poc/shared/filter-operators.json`，另加單一 `textarea` 覆寫，不把 `operatorsFor` 搬出 presentation — 消除運算子表漂移，且對 Task 8 的程式碼改動最小 — 錯了的代價：無。
- Ruling R14: （I1）`DynamicForm` 對條件引用公式或自動編號欄位的規則略過不求值 — 後端是權威，其 400 已會顯示；`DynamicDetail` 照常求值全部規則（已存的值帶有伺服器算好的公式結果）；Task 11 設計器提示與驗收文件須註明「公式／自動編號條件只在儲存時由伺服器判斷」 — 錯了的代價：這類規則在表單沒有即時預覽。
- Ruling R15: （I2）任何綁在目前未渲染欄位上的錯誤（前端或伺服器）都顯示在表單頂端橫幅，格式「欄位名稱：訊息」 — 涵蓋過期的隱藏值與伺服器對隱藏欄位的錯誤；驗證維持完整 — 錯了的代價：多一小段橫幅邏輯。
- Ruling R16: Task 11 仍補一輪小修：(a) `ConditionEditor` 的 `emitted` 在模型清空時沒有重設（真實的畫面殘留 bug）；(b) Task 10 的不穩定測試（vee-validate 5ms debounce 競態） — 測試不穩定會污染 Task 12 與最終驗證 — 錯了的代價：多一輪短修。
