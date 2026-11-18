# 無人值守：本專案設定

有人在場時的上線流程見 `formwright-ship` skill；兩邊步驟改了要一起改。

## 閘門
GitHub Actions（`.github/workflows/gate.yml`）：先跑外洩掃描（三項 0），再 `git push -u origin <功能分支>`，然後 `bash scripts/unattended/ci-wait.sh <sha>` 用 run_in_background 跑、等通知。

- 範圍依改動決定：只改文件不跑；只改前端只跑前端 test／typecheck／build；動到後端、`shared/`、Dockerfile 或 workflow 全跑（使用者 2026-11-13 裁決）。
- 最後一行 `success` 才算通過；失敗的 job 會列 `FAILED`，用 `gh run view <id> --log-failed` 看原因。
- 完整套件只在這一步跑，交給 CI；實作與修正輪的 subagent 本機只跑相關測試類別（`dotnet test --filter`、指定 vitest 檔）加 typecheck、build（使用者 2026-11-14 裁決）。
- 等 CI 時可以同時做本機走查；本機 `bash scripts/unattended/gate.sh <worktree>` 只在 GitHub 不能用時當備援。
- 背景指令不要在結尾加 `echo "exit=$?"`，那會讓背景工作永遠回 0。

## 單元流程（使用者 2026-11-17 裁決，加速）
- 設計夠細（寫到欄位、算法、測試重點）的段：不另寫實作計畫、不審計畫；整段派一個實作 subagent 讀設計、在分支上列簡短待辦後直接做到底。設計不夠細才照舊寫計畫。
- 每段只做一次程式碼審查（整段完成後讀整份 diff）、修一輪、跑一次 CI 就合併；權限或安全類的段審出 🔴 時，修完再審一次。
- 審查者從 diff 出發，可讀必要的上下文（呼叫端、權限守衛、既有測試），不跑測試；測試交給 CI。
- 審查結論的必修門檻：安全、權限、資料錯誤與公開契約（API、DTO、設定鍵、migration）的問題一律列必修；其餘列建議。
- 審查 prompt 要附上該段已拍板的例外（使用者裁決過的行為），避免審查者用總原則推翻（2026-11-17 st1a 審查誤判 U13）。
- 實作者本機只跑自己新增或修改的測試類別加 build；「既有功能沒被改壞」的安全網測試交給 CI。
- 前端改動另加跑全域守衛 spec（`src/css/themes/` 的寫死顏色、`locales` 鍵一致）——2026-11-18 kr2 漏跑，CI 才擋下。
- 效能類工作本機只驗正確性，快慢一律推分支交 CI 量；機器同時有別條線跑重測試時本機數字不可信（2026-11-18 測試加速在本機量測耗掉數小時）。
- 大量 Demo 產生的測試（Demo*、DesignCase*）不和另一條線的重測試同時跑。
- 自動駕駛期間電腦不可睡眠（接電源也會進維護睡眠，本機測試與 subagent 會整段暫停）。
- 固定兩條線：主線加一條和主線沒有共用檔（見下方「不並行的工作」）的平行線，各自一個 worktree。
- 模型照派工等級表：權限、安全、DB 結構的段實作用 opus，其餘實作用 sonnet。

## 外洩掃描
`LEAK_DENYLIST="$HOME/Work/projects/plugharbor/.leak-denylist" bash scripts/unattended/leak-scan.sh`

- 清單檔在 plugharbor repo 根目錄（已 gitignore），路徑用 `$HOME` 起頭，不寫展開後的絕對路徑。

- 三項筆數（diff、log、names）都要是 0。
- 清單檔不進 repo；掃描只印筆數，不印字串。

## 合併
可以自己合併：

1. `git merge-base --is-ancestor origin/main HEAD` 成立就 `git push origin HEAD:main`，用 fast-forward。
2. 不成立就先 rebase 到 origin/main；rebase 帶進的 `backend/`、`frontend/`、`shared/`、Dockerfile 或 workflow 有變才重跑閘門，只差文件就沿用原結果（與 `formwright-ship` 一致）。

不用 merge commit、不 force push。只改 `docs/`、`.claude/`、`.plugharbor/`、`scripts/` 的 push 不會部署。

## 部署
main push 會自動部署 Demo（Render），但只在 `backend/`、`frontend/`、`shared/` 或 `Dockerfile.demo` 有變更時才觸發（`render.yaml` 的 `buildFilter`）；只改 docs、`.claude/` 等的 push 不會部署，不必等。

等部署完成：`bash scripts/unattended/wait-deploy.sh <sha>`，背景跑，等通知。腳本會把短 sha 展開成完整 sha。

## 實機驗證
- 時機：有畫面的改動在 gate 通過、推上 main 之前，先用 `bash scripts/unattended/local-demo.sh <worktree>` 在本機走查（使用者 2026-11-11 裁決）。部署後的 Demo 只做冒煙檢查。
- 登入：**只用登入頁的「Demo 帳號（點一下直接登入）」按鈕**，絕不在任何欄位輸入帳號密碼。
- 登入後整頁重新載入再看畫面，不在同一段腳本裡 pushState 換頁，否則會讀到舊 schema。
- 版型：用 `scripts/unattended/rwd-check.js` 檢查這次動到的頁面，寬度 390、820、1366、1920 四種。
  - `overflow` 必須是 false。
  - `innerOverflow` 列出表格等容器自己的橫捲，逐項判斷是不是刻意的：寬報表可以，一般列表在手機不行。
- 做完清掉 emulate，導回 about:blank。

## 空檔來源
`docs/todo.md` 的「近期可開工」區（欄位照 workspace unattended 的 speed-rules）；做完一項就從同檔「其他」補一項。

## 不並行的工作
- 獨占資源：本機 Demo 埠（9180）、瀏覽器（chrome-devtools）、本機 Demo DB；同時只給一條線用，另一條線要走查就等前一條收掉。
- 共用檔：i18n 語系檔（`frontend/src/i18n/*.json`）、前端路由與選單、後端 DI 註冊、DbContext 與 EF migration、`backend/apps/*.app.json`。單元計畫要列出會碰到哪幾個；兩條平行線碰到同一個就照順序做，不並行實作。
- 平行線（另一個 worktree）只能做和進行中單元沒有共用檔的工作，例如簽核 a3 進行中時做任務看板 kb；兩條線的重測試錯開，不同時跑（使用者 2026-11-14 裁決）。
- 兩件會加 EF migration 的實作：model snapshot 會衝突。
- 兩件都動 `backend/apps/*.app.json` 的工作。
- 全套 WebAPI 測試，跟另一個吃 CPU 的測試同時跑：DemoData 測試會逾時。
- 有牆鐘上限的測試（如 `CachedSystemPolicyTests`）不和其他重測試同時跑：xUnit 工作執行緒被佔滿會讓逾時後的接續等太久（2026-11-14 CI 兩次失敗）。必須同時跑前端測試時，vitest 加 `--maxWorkers=2`。

## 電源與睡眠
- 開場：用 `pmset -g batt` 看有沒有接電源。使用者離開前提醒接電源、別蓋螢幕。`caffeinate` 擋不住蓋螢幕。
- 測試大量逾時：用 `pmset -g log | grep -E "Entering Sleep|DarkWake to FullWake"` 查那段時間的睡眠原因（Clamshell、Idle、電池）。是睡眠就照原樣重跑。
