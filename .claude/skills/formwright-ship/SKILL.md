---
name: formwright-ship
description: Use when a Formwright feature branch or worktree is implemented and reviewed and is ready to go to main — running the gate, leak scan, fast-forward push, waiting for the Render deploy, then verifying on the Demo.
---

# Formwright 上線流程

前提：實作完成、審查過（變更超過 300 行或碰權限、資料、公開介面要跨家族審查）。每一步都看結果再往下，失敗就停在那一步。

## 1. Gate

```bash
bash scripts/unattended/gate.sh <worktree 絕對路徑>
```

- 從要上線的 worktree 裡執行（別的 checkout 可能是舊版、沒有這支腳本）；背景跑。
- 每步印 `exit=`，全部 0 才算過；背景指令結尾不要加 `echo "exit=$?"`（會讓背景工作永遠回 0）。
- gate 期間不改這個 worktree、不跑其他吃 CPU 的測試；審查、規劃可以並行。
- 失敗或逾時的判讀見 `docs/ai/testing.md`「異常判讀」；真的壞就修、再跑完整 gate。

## 1.5 本機走查（有畫面的改動）

`poc/frontend` 有變更、或後端改到畫面讀的 API 時要做；純後端、純文件跳過。

```bash
bash scripts/unattended/local-demo.sh <worktree 絕對路徑>
```

- 背景跑，等到印出 `READY`。它會用全新 Demo 資料啟動，不碰 `poc.db`；port 5180、9180 被占用時會拒絕啟動，這時不要去停別人的程序，先問。
- 用 `formwright-demo-verify` skill 在 `http://127.0.0.1:9180` 驗這次的改動與四種寬度。
- 走查發現問題就修、重跑相關測試；改到程式就要重跑 gate。
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

- 不成立就 rebase 到 origin/main，**重跑 gate**。
- 不用 merge commit、不 force push。推完 `git rev-parse origin/main` 確認等於 HEAD。
- 沒動到 `poc/` 的變更（`git diff --name-only origin/main..HEAD | grep -q '^poc/'` 不成立）可以不跑 gate，但外洩掃描照跑。

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
