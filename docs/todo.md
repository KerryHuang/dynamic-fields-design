# 待辦

跨 session、又不屬於任何功能的雜事。一行一項、動詞開頭，冒號後寫脈絡；做完就刪（紀錄留在 git 歷史）。發現其實是功能，就移到 [功能清單](feature-checklist.md)。

- 建 Playwright 主線 e2e 測試：簽核前端 a4b 完成後做，5–8 條（登入、建記錄、送簽、同意、任務留言），放進 gate 防回歸（2026-11-11 使用者裁決）
- 加 favicon（public/ 放圖示＋index.html 一行 `<link rel="icon">`）消除登入頁 404；登入頁的 401 是 auth guard 探 `/me`，屬設計行為不改（2026-11-11 調查結論）
