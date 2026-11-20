import { baseCompile } from '@intlify/message-compiler';
import { describe, expect, it } from 'vitest';
import cases from '../../../shared/i18n-message-cases.json';
import { checkUiMessage } from './message-check';

interface UiCase { name: string; text: string; repeat?: number; allowed: string[]; expected: string | null }
const ui = cases.ui as UiCase[];
const textOf = (c: UiCase) => (c.repeat ? c.text.repeat(c.repeat) : c.text);

// 與後端 LocaleMessageCasesTests 同一份案例檔（shared/i18n-message-cases.json 的 ui 段）
describe('checkUiMessage（共用案例）', () => {
  it.each(ui.map((c) => [c.name, c] as const))('%s', (_name, c) => {
    expect(checkUiMessage(textOf(c), new Set(c.allowed))).toBe(c.expected);
  });

  it('通過的案例 vue-i18n 都編譯得過', () => {
    for (const c of ui.filter((x) => x.expected === null)) {
      const errors: string[] = [];
      baseCompile(textOf(c), { onError: (e) => errors.push(e.message) });
      expect(errors, c.name).toEqual([]);
    }
  });
});
