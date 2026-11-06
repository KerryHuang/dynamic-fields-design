import { describe, expect, it } from 'vitest';
import enUS from 'src/i18n/en-US.json';
import th from 'src/i18n/th.json';
import vi from 'src/i18n/vi.json';
import zhTW from 'src/i18n/zh-TW.json';

function leaves(o: unknown, path = ''): Record<string, string> {
  if (typeof o === 'string') return { [path]: o };
  return Object.entries(o as object).reduce((acc, [k, v]) => ({ ...acc, ...leaves(v, path ? `${path}.${k}` : k) }), {});
}

const zh = leaves(zhTW.theme);
const en = leaves(enUS.theme);

describe('theme 文案', () => {
  it('zh-TW 涵蓋計畫列出的所有鍵', () => {
    expect(Object.keys(zh).sort()).toEqual([
      'adminDemoReadOnly', 'adminHint', 'adminMenu', 'adminSave', 'adminSaved', 'adminTitle', 'demoLocalHint', 'follow',
      'mode', 'modes.dark', 'modes.light', 'modes.system', 'sampleText', 'saveFailed',
      'style', 'styles.airy.desc', 'styles.airy.name', 'styles.enterprise.desc', 'styles.enterprise.name',
      'styles.industrial.desc', 'styles.industrial.name', 'styles.workbench.desc', 'styles.workbench.name', 'title',
    ].sort());
  });

  it.each([['en-US', enUS], ['th', th], ['vi', vi]])('%s 的 theme 鍵與 zh-TW 完全相同', (_n, msgs) => {
    expect(Object.keys(leaves(msgs.theme)).sort()).toEqual(Object.keys(zh).sort());
  });

  it.each([['th', th], ['vi', vi]])('%s 的文案不是直接抄 zh-TW 或 en-US', (_n, msgs) => {
    for (const [k, v] of Object.entries(leaves(msgs.theme))) {
      expect(v, k).not.toBe(zh[k]);
      expect(v, k).not.toBe(en[k]);
      expect(v.trim(), k).not.toBe('');
    }
  });

  it.each([['zh-TW', zhTW], ['en-US', enUS], ['th', th], ['vi', vi]])('%s 的 follow 帶 {value} 佔位', (_n, msgs) => {
    expect(msgs.theme.follow).toContain('{value}');
  });
});
