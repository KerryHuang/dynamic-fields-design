import type { LocaleCode } from './locale';

// 系統管理員試看未啟用語系（語系包設計 M2、i4b 計畫判斷 17）：只存在這個分頁的 sessionStorage，重新整理仍在試看、
// 開新分頁回原語系；不碰 localStorage 的 formwright.locale，也不寫介面文字的瀏覽器快取。

export const PREVIEW_KEY = 'formwright.locale.preview';

/** 試看中的語系；sessionStorage 不可用回 null。 */
export function previewLocale(): LocaleCode | null {
  try {
    return sessionStorage.getItem(PREVIEW_KEY) || null;
  } catch {
    return null;
  }
}

export function setPreviewLocale(code: LocaleCode | null): void {
  try {
    if (code) sessionStorage.setItem(PREVIEW_KEY, code);
    else sessionStorage.removeItem(PREVIEW_KEY);
  } catch {
    /* 不可用：不試看 */
  }
}
