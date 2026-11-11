# 無人值守：本專案設定

有人在場時的上線流程見 `formwright-ship` skill；兩邊步驟改了要一起改。

## 閘門
`bash scripts/unattended/gate.sh <worktree>`，用 run_in_background 跑，等通知。

- 依序跑後端三個測試專案（不平行、不跑 sln），再跑前端 test／typecheck／build。每一步印 exit code，失敗時列出測試名稱。
- 全部 exit 0 才算通過，看輸出最後幾行判斷。
- 背景指令不要在結尾加 `echo "exit=$?"`，那會讓背景工作永遠回 0。

## 外洩掃描
`LEAK_DENYLIST=<repo 外的清單檔> bash scripts/unattended/leak-scan.sh`

- 三項筆數（diff、log、names）都要是 0。
- 清單檔不進 repo；掃描只印筆數，不印字串。

## 合併
可以自己合併：

1. `git merge-base --is-ancestor origin/main HEAD` 成立就 `git push origin HEAD:main`，用 fast-forward。
2. 不成立就先 rebase 到 origin/main，再重跑閘門。

不用 merge commit、不 force push。只改 `docs/`、`.claude/`、`.plugharbor/`、`scripts/` 的 push 不會部署。

## 部署
main push 會自動部署 Demo（Render），但只在 `poc/` 有變更時才觸發；只改 docs、`.claude/` 等的 push 不會部署，不必等。

等部署完成：`bash scripts/unattended/wait-deploy.sh <sha>`，背景跑，等通知。腳本會把短 sha 展開成完整 sha。

## 實機驗證
- 時機：有畫面的改動在 gate 通過、推上 main 之前，先用 `bash scripts/unattended/local-demo.sh <worktree>` 在本機走查（使用者 2026-11-11 裁決）。部署後的 Demo 只做冒煙檢查。
- 登入：**只用登入頁的「Demo 帳號（點一下直接登入）」按鈕**，絕不在任何欄位輸入帳號密碼。
- 登入後整頁重新載入再看畫面，不在同一段腳本裡 pushState 換頁，否則會讀到舊 schema。
- 版型：用 `scripts/unattended/rwd-check.js` 檢查這次動到的頁面，寬度 390、820、1366、1920 四種。
  - `overflow` 必須是 false。
  - `innerOverflow` 列出表格等容器自己的橫捲，逐項判斷是不是刻意的：寬報表可以，一般列表在手機不行。
- 做完清掉 emulate，導回 about:blank。

## 不並行的工作
- 兩件會加 EF migration 的實作：model snapshot 會衝突。
- 兩件都動 `poc/backend/apps/*.app.json` 的工作。
- 全套 WebAPI 測試，跟另一個吃 CPU 的測試同時跑：DemoData 測試會逾時。必須同時跑前端測試時，vitest 加 `--maxWorkers=2`。

## 電源與睡眠
- 開場：用 `pmset -g batt` 看有沒有接電源。使用者離開前提醒接電源、別蓋螢幕。`caffeinate` 擋不住蓋螢幕。
- 測試大量逾時：用 `pmset -g log | grep -E "Entering Sleep|DarkWake to FullWake"` 查那段時間的睡眠原因（Clamshell、Idle、電池）。是睡眠就照原樣重跑。
