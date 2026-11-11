---
name: formwright-demo-verify
description: Use when checking a Formwright change on the live Demo site (https://dynamic-modules-demo.onrender.com) with a browser — after a deploy, or to reproduce a reported UI problem.
---

# Formwright Demo 實機驗證

## 規則

- **登入只用登入頁「Demo 帳號（點一下直接登入）」按鈕**，絕不在帳號密碼欄位輸入任何東西，不建立帳號。
- 只驗證、不存資料；需要改資料才能驗的項目，先確認 Demo 每次部署會重建再做，並在回報說明。
- 一個瀏覽器分頁做到底，不另開新分頁。

## 步驟

1. 開要驗的頁面；被導到登入頁就按對應角色的 Demo 按鈕。換頁用整頁導航，不在同一段腳本裡 pushState 換頁（會讀到舊 schema）。
2. **等畫面長出來再查**：查 DOM 前等 2–3 秒或等到指定文字出現；查不到元素先重試一次，別急著判定失敗。
3. 驗這次改動的行為：讀畫面文字、`aria-checked`、`data-testid`，必要時截圖。
4. **四種寬度**：390×844（手機）、820×1180（平板）、1366×768（筆電，矮螢幕最容易露出問題）、1920×1080（桌機）。
   - 每換一次寬度頁面會重載，要重新打開對話框或分頁再量。
   - 頁面：`document.documentElement.scrollWidth` 不得大於視窗寬。
   - 對話框與可捲動容器也要量：`scrollWidth` 不得大於 `clientWidth`（只量整頁會漏掉對話框內部溢出）。
   - 也可用 `scripts/unattended/rwd-check.js`（它用 pushState 換頁，只拿來量版面，不拿來驗資料或 schema 行為）：`overflow` 必須是 false；`innerOverflow` 逐項判斷，寬報表可以橫捲，一般列表在手機不行。
5. **收尾**：清掉 emulate、導回 `about:blank`；若切過語系，把 `formwright.locale` 改回 zh-TW。

## 回報

逐項寫：帳號、頁面、看到什麼、四種寬度結果；沒驗到的寫「未驗證」與原因（例：Demo 沒有對應資料）。
