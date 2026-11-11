---
paths:
  - "poc/frontend/**"
trigger: always_on
---

# 前端（Vue 3＋Quasar＋bun）

- **畫面由模組定義動態產生**（AGENTS.md 鐵則 2、5）：前端的寫法是讀 schema 與 API 回傳的上限、預設值；不寫 `moduleKey === '...'` 分支，也不另存一份常數。
- **i18n**：`src/i18n/{zh-TW,en-US,th,vi}.json` 四語鍵集合必須一致，維持既有單行 JSON 格式；訊息含 `@`、`{`、`}`、`|` 要用 vue-i18n 字面量寫法（`locales.vitest.spec.ts` 會編譯檢查）。新增句子要給四語，不留中文當英文。
- **RWD**（四種寬度見 AGENTS.md）：可捲動容器（`scroll` 加 `max-height`）裡用 Quasar `column` 時一定加 `no-wrap`，否則內容超高會折成第二欄往右溢出。
- **無障礙**：輸入框要有可讀名稱（`label` 或 `aria-label`），只靠 placeholder 不算。
- **測試**：vitest；跟後端測試或 gate 同時跑時加 `--maxWorkers=2`。改完跑相關 spec 與 `bun run typecheck`。
