---
name: night-run
description: Use when 使用者要離開（睡覺、外出）並交代「繼續做、中間不用問我、依你的建議」，在本 repo 無人值守地連續推進多個工作單元（實作、審查、合併、部署 Demo），或使用者說「夜間模式」「無人值守」「我回來再看」。
---

# 無人值守夜間模式（本專案版）

使用者不在時，主控照路線一個工作單元接一個往下做：可逆的決定自己拍板並記帳，只有下面「停下條件」才停。醒來時交一份能直接照著測的晨間報告。

## 1. 開場：把授權範圍寫下來

在帳本（見第 2 節）開頭寫明，派出去的每個 worker／subagent prompt 也照抄：

- 可改：本 repo 的哪些 worktree／分支；可 ff 合併到 main 並 push（main 會自動部署 Demo）。
- **其他 repo 一律不動**：看到別的 repo（例如 plugin marketplace）該修，只寫進晨間報告的「刻意沒做」，不 commit、不 push、不發版。
- 範圍：路線上的哪幾個工作單元、做到哪算哪。
- 電源：`pmset -g batt` 看是否接電源。沒接就在使用者離開前提醒「接電源、別蓋螢幕」；人已經走了就記進晨間報告，並預期長測試可能因睡眠逾時（見第 6 節）。

## 2. 裁決帳本

帳本放 repo 外的 `.tmp/<YYYYMMDD>-<slug>/plan.md`，每件事一行，附時間：

- `使用者裁決：…`（原話或明確選項）和 `主控裁決（待使用者確認）：… — 理由 — 選錯的代價` 分開寫，絕不把自己的決定寫成使用者說的。
- 審查結果、閘門數字、commit 範圍、部署結果都記進來。context 被壓縮後以帳本與 `git log` 為準。

## 3. 一個工作單元的流程

1. 派實作（subagent 或 pane worker），prompt 自足並含第 1 節的授權範圍。
2. 審查：先試跨家族；湊不到就同家、用新 context，回報寫「湊不到別家，退回同家審查」。
3. 依審查裁決修正。會改變使用者看得到的行為的建議，採用也可以，但要記成主控裁決待確認。
4. 閘門：`bash .claude/skills/night-run/scripts/gate.sh <worktree>`，用 run_in_background 跑，等通知。全部 exit 0 才往下。
5. 外洩掃描：`LEAK_DENYLIST=<repo 外的清單檔> bash .claude/skills/night-run/scripts/leak-scan.sh`，只看筆數，必須三項都是 0。
6. 合併：`git merge-base --is-ancestor origin/main HEAD` 成立就 `git push origin HEAD:main`（fast-forward）；不成立先 rebase 到 origin/main 再重跑閘門。不用 merge commit、不 force push。
7. 部署：`bash .claude/skills/night-run/scripts/wait-deploy.sh <sha>`，背景跑，等通知。
8. Demo 實機：**只用登入頁的「Demo 帳號（點一下直接登入）」按鈕**，絕不在任何欄位輸入帳號密碼。用 `scripts/rwd-check.js` 在四種版型檢查這次動到的頁面沒有橫向捲動；做完清掉 emulate、導回 about:blank。
9. 更新帳本與晨間報告，**接著做下一個工作單元**。做完一個就停是錯的：授權是整晚往下推。

## 4. 資源規則

- 同一個 worktree 裡，有人在改檔時不跑閘門；要並行就讓另一方用另一個 worktree。
- 會加 EF migration 的兩件實作不同時進行（model snapshot 會衝突）；動 `poc/backend/apps/*.app.json` 的工作也不並行。
- worker 交回、讀完結果檔就關它的 pane，不留著待命。
- 不寫自動回答 worker 提問的程式；worker 卡在提問就記進報告。

## 5. 停下條件（只有這些）

- 不可逆或破壞性操作：force push、刪分支／資料、`reset --hard`。
- 要動授權範圍外的東西：其他 repo、使用者層設定、帳號、付費服務。
- 閘門或部署失敗，查不出原因，或修了兩次還是失敗。
- 設計沒涵蓋、而且會改格式或使用者行為的分岔；兩邊都可逆的話，照建議做並記帳，不停。

停下時把現況、證據、建議寫進晨間報告的「待決」，然後去做路線上不依賴它的下一件事；沒有可做的才結束。

## 6. 異常判讀

- 測試大量逾時、執行時間是平常的好幾倍：先查是不是電腦睡了，`pmset -g log | grep -E "Entering Sleep|DarkWake to FullWake"` 看那段時間的原因（Clamshell、Idle、電池）。是睡眠就照原樣重跑，不改測試。`caffeinate` 擋不住蓋螢幕，報告裡提醒使用者別蓋螢幕，或接電源加外接螢幕。
- 部署失敗：看託管平台 log；是這次合併造成的就 `git revert` 該 commit 並 push，不 reset。

## 7. 晨間報告

寫在帳本同一個資料夾的 `morning-report.md`，依序：

1. 一句話結論：哪些可以測、哪些還不行。
2. 可測項目：每項附 Demo 路徑、用哪個示範帳號、具體操作步驟與預期結果。
3. 驗證結果分開宣稱：本機閘門數字、外洩掃描、push 的 commit 範圍、部署、Demo 實機；沒跑到的標「未驗證」。
4. 主控裁決待確認：每條附理由與選錯代價。
5. 刻意沒做的事（含其他 repo 的發現）與已知問題。
6. 待決：早上用 AskUserQuestion 一次一題問。
