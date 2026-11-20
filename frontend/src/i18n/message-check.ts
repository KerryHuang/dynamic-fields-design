import { baseCompile } from '@intlify/message-compiler';

/** 與後端 LocalePackLimits.MaxTextLength 同值；共用案例檔（shared/i18n-message-cases.json）兩邊都跑，漂移會失敗。 */
const MAX_TEXT_LENGTH = 2000;

// 雙向控制字元（U+202A–202E、U+2066–2069）與其他控制字元（換行、tab 除外）
// eslint-disable-next-line no-control-regex
const CONTROL = /[\u0000-\u0008\u000b-\u001f\u007f-\u009f‪-‮⁦-⁩]/;

const nameStart = (c: string) => /[A-Za-z_]/.test(c);
const namePart = (c: string) => /[A-Za-z0-9_]/.test(c);

/** 從 start 的 `{` 讀一段插值或字面，回下一個位置；不合法回 -1。 */
function readBrace(text: string, start: number, names: string[]): number {
  let i = start + 1;
  if (text[i] === "'") {
    const close = text.indexOf("'", i + 1);
    return close >= 0 && text[close + 1] === '}' ? close + 2 : -1;
  }
  if (i >= text.length || !nameStart(text[i])) return -1;
  const from = i;
  while (i < text.length && namePart(text[i])) i++;
  if (text[i] !== '}') return -1;
  names.push(text.slice(from, i));
  return i + 1;
}

function checkSyntax(text: string, allowed: ReadonlySet<string>): 'syntax' | 'placeholder' | null {
  const names: string[] = [];
  let i = 0;
  while (i < text.length) {
    const c = text[i];
    if (c === '@' || c === '|' || c === '}' || (c === '%' && text[i + 1] === '{')) return 'syntax';
    if (c === '{') {
      const end = readBrace(text, i, names);
      if (end < 0) return 'syntax';
      i = end;
      continue;
    }
    i++;
  }
  return names.every((n) => allowed.has(n)) ? null : 'placeholder';
}

/**
 * 介面文字語法檢查（語系包設計 §13.2）：與後端 LocaleTextSafety＋UiMessageSyntax 同一套規則，
 * 回拒收原因代碼（control、tooLong、syntax、placeholder），通過回 null。只用在測試與共用案例，
 * 驗證本身在後端；執行期的 prepareMessages 不加白名單。
 */
export function checkUiMessage(text: string, allowedNames: ReadonlySet<string>): string | null {
  if (CONTROL.test(text.replace(/[\n\t]/g, ''))) return 'control';
  if (text.length > MAX_TEXT_LENGTH) return 'tooLong';
  const reason = checkSyntax(text, allowedNames);
  if (reason) return reason;
  let compileError = false;
  baseCompile(text, { onError: () => { compileError = true; } });
  return compileError ? 'syntax' : null;
}
