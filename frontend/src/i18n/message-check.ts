import { baseCompile } from '@intlify/message-compiler';

/** 與後端 LocalePackLimits.MaxTextLength 同值；共用案例檔（shared/i18n-message-cases.json）兩邊都跑，漂移會失敗。 */
const MAX_TEXT_LENGTH = 2000;

// 雙向控制字元（U+202A–202E、U+2066–2069）與其他控制字元（換行、tab 除外）
const CONTROL = /[\u0000-\u0008\u000b-\u001f\u007f-\u009f‪-‮⁦-⁩]/;

const nameStart = (c: string) => /[A-Za-z_]/.test(c);
const namePart = (c: string) => /[A-Za-z0-9_]/.test(c);

const INVALID = -1;
const CONTROL_CHAR = -2;
const isForbiddenCodePoint = (cp: number) => CONTROL.test(String.fromCodePoint(cp));

/** 從字面量開頭單引號之後讀到 `'}`（照 message compiler：跳脫只認 \\、\'、\uXXXX、\UXXXXXX；內有換行不合法），回下一個位置。 */
function readLiteral(text: string, start: number): number {
  let i = start;
  let control = false;
  while (i < text.length && text[i] !== "'") {
    const c = text[i];
    if (c === '\n' || c === '\r' || c === '\u2028' || c === '\u2029') return INVALID;
    if (c === '\\') {
      const next = text[i + 1];
      if (next === '\\' || next === "'") { i += 2; continue; }
      const digits = next === 'u' ? 4 : next === 'U' ? 6 : 0;
      if (!digits || i + 2 + digits > text.length) return INVALID;
      const hex = text.slice(i + 2, i + 2 + digits);
      if (!/^[0-9A-Fa-f]+$/.test(hex)) return INVALID;
      const cp = parseInt(hex, 16);
      if (cp > 0x10ffff) return INVALID;
      control ||= isForbiddenCodePoint(cp);
      i += 2 + digits;
      continue;
    }
    i++;
  }
  if (text[i + 1] !== '}' || i >= text.length) return INVALID;
  return control ? CONTROL_CHAR : i + 2;
}

/** 從 start 的 `{` 讀一段插值或字面，回下一個位置；不合法回 INVALID，字面量解出控制字元回 CONTROL_CHAR。 */
function readBrace(text: string, start: number, names: string[]): number {
  let i = start + 1;
  if (text[i] === "'") return readLiteral(text, i + 1);
  if (i >= text.length || !nameStart(text[i])) return INVALID;
  const from = i;
  while (i < text.length && namePart(text[i])) i++;
  if (text[i] !== '}') return INVALID;
  names.push(text.slice(from, i));
  return i + 1;
}

function checkSyntax(text: string, allowed: ReadonlySet<string>): 'syntax' | 'control' | 'placeholder' | null {
  const names: string[] = [];
  let i = 0;
  while (i < text.length) {
    const c = text[i];
    // 普通文字的跳脫照 message compiler 的 readText：\ 後接 { } @ | \ 兩字一起當文字，其他反斜線就是反斜線（第二輪審查必修 2）
    if (c === '\\' && i + 1 < text.length && '{}@|\\'.includes(text[i + 1]!)) { i += 2; continue; }
    if (c === '@' || c === '|' || c === '}' || (c === '%' && text[i + 1] === '{')) return 'syntax';
    if (c === '{') {
      const end = readBrace(text, i, names);
      if (end === CONTROL_CHAR) return 'control';
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
