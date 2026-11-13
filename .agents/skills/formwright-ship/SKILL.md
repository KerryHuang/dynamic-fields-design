---
name: formwright-ship
description: Use when a Formwright feature branch or worktree is implemented and reviewed and is ready to go to main — running the gate, leak scan, fast-forward push, waiting for the Render deploy, then verifying on the Demo.
---

# Formwright 上線流程

前提：實作完成、審查過（變更超過 300 行或碰權限、資料、公開介面要跨家族審查）。每一步都看結果再往下，失敗就停在那一步。

## 1. Gate（GitHub Actions）

功能分支推上 GitHub 就會跑 `.github/workflows/gate.yml`。repo 是公開的，**推分支前先跑第 2 節的外洩掃描，三項都是 0 才推**（使用者 2026-11-13 裁決）。

```bash
git push -q -u origin <功能分支>
bash scripts/unattended/ci-wait.sh <sha>
```

- `ci-wait.sh` 背景跑，最後一行 `success` 才算過；之前幾行是各 job 的結論與分鐘數，失敗的 job 另列 `FAILED`。
- 範圍依改動自動決定：只改文件不跑；只改 `poc/frontend/` 只跑前端 test／typecheck／build；動到 `poc/backend/`、`poc/shared/`、Dockerfile 或 workflow 就全跑。
- 失敗先看 log（`gh run view <id> --log-failed`）：真的壞就修、推上去重跑；逾時型、單獨重跑會過的照 `docs/ai/testing.md`「異常判讀」處理，重跑用 `gh run rerun <id> --failed`。
- 本機的 `bash scripts/unattended/gate.sh <worktree>` 留作備援（GitHub 不能用時），不再每段跑。

## 1.5 本機走查（有畫面的改動）

`poc/frontend` 有變更、或後端改到畫面讀的 API 時要做；純後端、純文件跳過。

```bash
bash scripts/unattended/local-demo.sh <worktree 絕對路徑>
```

- 背景跑，等到印出 `READY`。它會用全新 Demo 資料啟動，不碰 `poc.db`；port 5180、9180 被占用時會拒絕啟動，這時不要去停別人的程序，先問。
- 用 `formwright-demo-verify` skill 在 `http://127.0.0.1:9180` 驗這次的改動與四種寬度。
- 走查可以跟 CI 同時進行。發現問題就修、重跑相關測試，再推上分支讓 CI 重跑。
- 驗完停掉這個背景工作。

## 2. 外洩掃描

```bash
LEAK_DENYLIST=<repo 外的清單檔> bash scripts/unattended/leak-scan.sh origin/main
```

三項筆數 `diff`、`log`、`names` 都要是 0。`exit 2` 代表清單檔沒設定、**沒掃描**，不算通過，停下來問。清單檔不進 repo；掃描只印筆數，不印字串。

## 3. 推上 main（只用 fast-forward）

```bash
git fetch -q && git merge-base --is-ancestor origin/main HEAD && git push -q origin HEAD:main
```

- 不成立就 rebase 到 origin/main；rebase 後 `poc/` 有變，推上分支讓 CI 重跑，只有文件衝突就不重跑。
- 不用 merge commit、不 force push。推完 `git rev-parse origin/main` 確認等於 HEAD。
- 推進 main 的 commit 必須就是 CI 通過的那個 sha（fast-forward 保證這點）。沒動到 `poc/` 的變更 CI 會略過測試，外洩掃描照跑。

## 4. 等部署

`poc/` 有變更才會觸發 Render 部署；只改 docs、`.claude/` 等不會部署，跳過這步。

```bash
bash scripts/unattended/wait-deploy.sh <sha>
```

背景跑，最後一行 `success` 才算成功；`failure`／`error`／`timeout` 都停下。`timeout` 先到 Render 看狀態，不 revert；部署失敗而且查明是這次合併造成的，才 `git revert` 該 commit 再推，不 reset。

## 5. Demo 冒煙檢查

本機已走查過，Demo 上只做冒煙檢查：用 `formwright-demo-verify` skill 開這次動到的頁面一次，確認載得出來、沒有 console 錯誤。版面要量的話，只量最容易出問題的那個寬度。沒有做本機走查的變更（例如純後端），照原本的完整驗證做。

## 6. 收尾

- 進度寫進 `docs/roadmap.md` 的工作段表格；功能清單打勾並重算。
- 確認分支已是 origin/main 的祖先後，移除 worktree 與分支。
