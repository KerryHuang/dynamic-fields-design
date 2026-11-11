---
paths:
  - "docs/**"
  - "README*.md"
trigger: always_on
---

# 文件

- **功能清單** `docs/feature-checklist.md`：每項一行，`[x]` 完成、`[ ]` 加 🚧 進行中／📋 已規劃／💡 構想、`[-]` 不做附理由。改完用 lifecycle plugin `scripts/` 下的 `feature_checklist.py docs/feature-checklist.md --write` 重算統計，不手改統計行。
- **裁決**寫回對應的設計文件（`docs/superpowers/specs/`），標明是使用者裁決還是主控暫定；進度寫在路線文件的工作段表格。
- **設計文件**：應用程式或功能設計要有「ERP 對照」一節；寫明各開關組合下誰負責；新的寫死項目要列在已知限制或「可寫死清單」。
- **手冊** `docs/manual.md`：使用者看得到的行為改變要同步；文案以畫面實際文字為準。
- 站內連結用相對路徑；搬檔時同步修所有引用。
